import { useCallback, useEffect, useState } from 'react'
import {
  getExternalIo,
  saveExternalIo,
  testExternalSignal,
  type ExternalIoInfo,
  type ExternalIoSettings,
  type ExternalIoStatus,
  type ExternalSignalId,
  type ExternalTerminator,
} from '../api/client'
import { ExternalIoConsole } from './ExternalIoConsole'
import './ExternalIoDialog.css'

/** How often the connection status and signal board are refreshed while the dialog is open. */
const STATUS_POLL_MS = 1000

/** A signal counts as "just sent" this long, which is a little over two polls so a flash is never missed. */
const FLASH_MS = 2000

type Tab = 'monitor' | 'settings'

const TABS: { id: Tab; label: string }[] = [
  { id: 'monitor', label: 'İzleme' },
  { id: 'settings', label: 'Ayarlar' },
]

function formatAge(ms: number | null): string {
  if (ms === null) {
    return 'hiç gönderilmedi'
  }

  const seconds = Math.round(ms / 1000)
  if (seconds < 60) {
    return `${seconds} sn önce`
  }

  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? `${minutes} dk önce` : `${Math.floor(minutes / 60)} sa önce`
}

type MessageKey =
  | 'readyMessage'
  | 'busyMessage'
  | 'runMessage'
  | 'errorMessage'
  | 'allOkMessage'
  | 'allNokMessage'
  | 'resultAvailableMessage'

type Tone = 'ok' | 'ng' | 'busy' | 'info'

/**
 * `level` marks the signals that also describe a state that is true right now (Run, Busy, Ready),
 * which the board shows as a steady light; the others are events and only flash when sent.
 */
const SIGNALS: { id: ExternalSignalId; key: MessageKey; hint: string; tone: Tone; level?: 'run' | 'busy' | 'ready' }[] = [
  { id: 'Ready', key: 'readyMessage', hint: 'Hazır: şimdi gelen bir tetik kabul edilir', tone: 'ok', level: 'ready' },
  { id: 'Busy', key: 'busyMessage', hint: 'Denetim sürüyor', tone: 'busy', level: 'busy' },
  { id: 'Run', key: 'runMessage', hint: 'Konsol Çalıştır modunda', tone: 'info', level: 'run' },
  { id: 'Error', key: 'errorMessage', hint: 'Tetik yapılamadı ya da denetim hata verdi', tone: 'ng' },
  { id: 'AllOK', key: 'allOkMessage', hint: 'Sayılan tüm pencereler geçti', tone: 'ok' },
  { id: 'AllNok', key: 'allNokMessage', hint: 'En az bir pencere kaldı', tone: 'ng' },
  { id: 'ResultAvailable', key: 'resultAvailableMessage', hint: 'Yeni sonuç var (kararın hemen öncesinde)', tone: 'info' },
]

const TERMINATORS: { id: ExternalTerminator; label: string }[] = [
  { id: 'CRLF', label: 'CR+LF (\\r\\n)' },
  { id: 'LF', label: 'LF (\\n)' },
  { id: 'CR', label: 'CR (\\r)' },
  { id: 'None', label: 'Yok' },
]

interface ExternalIoDialogProps {
  onClose: () => void
}

/**
 * The TCP/IP interface a PLC or robot uses. The Monitor tab shows the status signals as they are
 * sent and opens a console of everything crossing the interface; the Settings tab sets up the port
 * this console listens on, the text that means "inspect now", and the text sent back for each
 * signal. Every message is editable because controllers disagree about what one looks like.
 */
export function ExternalIoDialog({ onClose }: ExternalIoDialogProps) {
  const [tab, setTab] = useState<Tab>('monitor')
  const [showConsole, setShowConsole] = useState(false)
  const [draft, setDraft] = useState<ExternalIoSettings | null>(null)
  const [status, setStatus] = useState<ExternalIoStatus | null>(null)
  const [isDirty, setIsDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)

  const applyInfo = useCallback((info: ExternalIoInfo, replaceDraft: boolean) => {
    setStatus(info.status)
    if (replaceDraft) {
      setDraft(info.settings)
      setIsDirty(false)
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    getExternalIo()
      .then((info) => !cancelled && applyInfo(info, true))
      .catch((err: unknown) => !cancelled && setError(err instanceof Error ? err.message : 'Ayarlar alınamadı'))

    // Only the status is refreshed: replacing the draft would wipe what is being typed.
    const timer = setInterval(() => {
      getExternalIo()
        .then((info) => !cancelled && applyInfo(info, false))
        .catch(() => undefined)
    }, STATUS_POLL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [applyInfo])

  const patch = (change: Partial<ExternalIoSettings>) => {
    setDraft((current) => (current ? { ...current, ...change } : current))
    setIsDirty(true)
    setNote(null)
  }

  const save = async () => {
    if (!draft) {
      return
    }

    setBusy(true)
    setError(null)
    try {
      applyInfo(await saveExternalIo(draft), true)
      setNote('Kaydedildi ve uygulandı.')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Kaydedilemedi')
    } finally {
      setBusy(false)
    }
  }

  const sendTest = async (signal: ExternalSignalId) => {
    setError(null)
    try {
      const count = await testExternalSignal(signal)
      setNote(count > 0 ? `${signal} mesajı ${count} istemciye gönderildi.` : 'Bağlı istemci yok, gönderilecek kimse yok.')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Gönderilemedi')
    }
  }

  const statusText = !status
    ? 'Durum okunuyor…'
    : status.error
      ? status.error
      : status.listening
        ? `Dinliyor — port ${status.port} · ${status.clients.length} istemci bağlı`
        : 'Kapalı'

  return (
    <div className="modal__scrim" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal modal--external">
        <div className="modal__title">
          <span>Dış Haberleşme (TCP/IP)</span>
          <button type="button" className="btn" onClick={onClose}>
            Kapat
          </button>
        </div>

        <div className="tabs">
          {TABS.map((t) => (
            <button key={t.id} type="button" className={tab === t.id ? 'is-active' : ''} onClick={() => setTab(t.id)}>
              {t.label}
            </button>
          ))}
        </div>

        <div className="modal__body">
          <div className="external__status">
            <span className={`dot ${status?.listening ? 'is-on' : ''}`} />
            <span className={status?.error ? 'is-ng' : undefined}>{statusText}</span>
          </div>
          {status && status.clients.length > 0 && <p className="hint-text">Bağlı: {status.clients.join(', ')}</p>}
          {status?.lastTriggerUtc && (
            <p className="hint-text">Son tetik: {new Date(status.lastTriggerUtc).toLocaleString('tr-TR')}</p>
          )}

          {tab === 'monitor' && (
            <>
              <div className="signals">
                {SIGNALS.map((signal) => {
                  const info = status?.signals.find((s) => s.signal === signal.id)
                  const isLevel = signal.level ? status?.state[signal.level] === true : false
                  const isFlash = info?.ageMs != null && info.ageMs < FLASH_MS
                  return (
                    <div
                      key={signal.id}
                      className={`signal is-${signal.tone}${isLevel ? ' is-level' : ''}${isFlash ? ' is-flash' : ''}`}
                    >
                      <span className="signal__led" />
                      <div className="signal__text">
                        <b>{signal.id}</b>
                        <small>{signal.hint}</small>
                        <small className="signal__meta">
                          {info && info.count > 0 ? `${formatAge(info.ageMs)} · ${info.count} kez` : 'henüz gönderilmedi'}
                        </small>
                      </div>
                      <button
                        type="button"
                        className="btn"
                        disabled={!status?.listening || status.clients.length === 0}
                        onClick={() => void sendTest(signal.id)}
                      >
                        Gönder
                      </button>
                    </div>
                  )
                })}
              </div>
              <p className="hint-text">
                Sabit yanan ışık o an doğru olan durumu (Run, Busy, Ready), yanıp sönen ışık az önce gönderilen
                sinyali gösterir. Kontrolcü bağlı olmasa da sinyaller burada sayılır.
              </p>

              <div className="external__consolebar">
                <button type="button" className="btn" onClick={() => setShowConsole((open) => !open)}>
                  {showConsole ? 'Konsolu Kapat' : 'Konsolu Aç'}
                </button>
              </div>
              {showConsole && <ExternalIoConsole canSend={status?.listening === true} />}
            </>
          )}

          {tab === 'settings' && !draft && !error && <p className="empty">Ayarlar okunuyor…</p>}

          {tab === 'settings' && draft && (
            <>
              <label className="external__check">
                <input
                  id="external-enabled"
                  type="checkbox"
                  checked={draft.enabled}
                  onChange={(e) => patch({ enabled: e.target.checked })}
                />
                TCP/IP arayüzünü etkinleştir
              </label>

              <div className="row">
                <label className="labeled" htmlFor="external-port">
                  Port (bu bilgisayarda dinlenir)
                  <input
                    id="external-port"
                    className="field"
                    style={{ width: 120 }}
                    type="number"
                    min={1024}
                    max={65535}
                    value={draft.port}
                    onChange={(e) => patch({ port: Number(e.target.value) || 0 })}
                  />
                </label>
                <label className="labeled" htmlFor="external-terminator">
                  Mesaj sonlandırıcı
                  <select
                    id="external-terminator"
                    className="field"
                    value={draft.terminator}
                    onChange={(e) => patch({ terminator: e.target.value as ExternalTerminator })}
                  >
                    {TERMINATORS.map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.label}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              <div className="pane__title">Kontrolcüden gelen: tetik</div>
              <div className="row">
                <label className="labeled" htmlFor="external-trigger" style={{ flex: 1 }}>
                  Tetik mesajı
                  <input
                    id="external-trigger"
                    className="field"
                    value={draft.triggerMessage}
                    onChange={(e) => patch({ triggerMessage: e.target.value })}
                  />
                  <small>Bu mesaj gelince bir denetim yapılır (yalnızca Çalıştır modunda).</small>
                </label>
              </div>

              <div className="pane__title">Kontrolcüye giden: durum sinyalleri</div>
              <table className="external__signals">
                <tbody>
                  {SIGNALS.map((signal) => (
                    <tr key={signal.id}>
                      <td>
                        <b>{signal.id}</b>
                        <small>{signal.hint}</small>
                      </td>
                      <td>
                        <input
                          id={`external-${signal.id}`}
                          className="field"
                          aria-label={`${signal.id} mesajı`}
                          value={draft[signal.key]}
                          onChange={(e) => patch({ [signal.key]: e.target.value })}
                        />
                      </td>
                      <td>
                        <button
                          type="button"
                          className="btn"
                          disabled={!status?.listening || status.clients.length === 0}
                          onClick={() => void sendTest(signal.id)}
                        >
                          Gönder
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p className="hint-text">
                Boş bırakılan bir sinyal gönderilmez. Kontrolcü bağlandığında güncel durum (Run / Ready / Busy)
                hemen gönderilir. Kontrolcünün bağlanabilmesi için Windows Güvenlik Duvarı'nda bu portun gelen
                TCP bağlantılarına açık olması gerekir.
              </p>
            </>
          )}

          {error && <p className="error-text">{error}</p>}
          {note && <p className="hint-text">{note}</p>}
        </div>

        <div className="modal__footer">
          {tab === 'settings' && (
            <button type="button" className="btn is-primary" disabled={!draft || !isDirty || busy} onClick={() => void save()}>
              {busy ? 'Uygulanıyor…' : 'Kaydet ve Uygula'}
            </button>
          )}
          <button type="button" className="btn" onClick={onClose}>
            Kapat
          </button>
        </div>
      </div>
    </div>
  )
}
