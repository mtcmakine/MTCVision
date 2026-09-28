import { useEffect, useRef, useState } from 'react'
import {
  autoAdjustBrightness,
  disconnectCamera,
  getCameraIdentity,
  getCameraSettings,
  getExternalIo,
  measureFocus,
  saveExternalIo,
  saveSettingsToCamera,
  updateCameraIdentity,
  updateCameraSettings,
  type AutoBrightnessResult,
  type CameraIdentity,
  type CameraSettings,
  type ExternalIoInfo,
  type FeatureRange,
} from '../api/client'
import './CameraDialog.css'

type Tab = 'trigger' | 'brightness' | 'focus' | 'image' | 'identity'

const TABS: { id: Tab; label: string }[] = [
  { id: 'trigger', label: 'Tetikleme' },
  { id: 'brightness', label: 'Parlaklık' },
  { id: 'focus', label: 'Netlik' },
  { id: 'image', label: 'Görüntü' },
  { id: 'identity', label: 'Kimlik ve Ağ' },
]

/** How often the focus meter re-measures while the tab is open. */
const FOCUS_POLL_MS = 500

interface CameraDialogProps {
  settings: CameraSettings | null
  onSettingsChange: (settings: CameraSettings) => void
  onDisconnected: () => void
  onClose: () => void
}

/**
 * Camera setup, arranged like the reference console's settings screens: the form holds a draft and
 * nothing reaches the camera until Apply or Save. Buttons that are actions rather than settings —
 * automatic brightness, writing to the camera's own memory, disconnecting — still act at once,
 * because there is nothing meaningful to defer about them.
 */
export function CameraDialog({ settings, onSettingsChange, onDisconnected, onClose }: CameraDialogProps) {
  const [tab, setTab] = useState<Tab>('brightness')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [isDirty, setIsDirty] = useState(false)

  // Kept as text so a half-typed number stays editable.
  const [exposure, setExposure] = useState('')
  const [gain, setGain] = useState('')
  const [blackLevel, setBlackLevel] = useState('')
  const [frameRate, setFrameRate] = useState('')
  const [reverseX, setReverseX] = useState(false)
  const [reverseY, setReverseY] = useState(false)
  const [triggerKind, setTriggerKind] = useState<'free' | 'internal' | 'external' | 'tcp'>('free')
  const [triggerDelay, setTriggerDelay] = useState('0')
  const [triggerActivation, setTriggerActivation] = useState('RisingEdge')

  // The TCP/IP interface a trigger message arrives on. Its port and message are edited here, next
  // to the choice that makes them matter, and saved with the rest of the draft.
  const [externalIo, setExternalIo] = useState<ExternalIoInfo | null>(null)
  const [tcpPort, setTcpPort] = useState('')
  const [tcpMessage, setTcpMessage] = useState('')

  const [identity, setIdentity] = useState<CameraIdentity | null>(null)
  const [userId, setUserId] = useState('')
  const [persistentIp, setPersistentIp] = useState('')
  const [subnetMask, setSubnetMask] = useState('')
  const [gateway, setGateway] = useState('')
  const [ipConfiguration, setIpConfiguration] = useState('PersistentIP')

  const [brightnessTarget, setBrightnessTarget] = useState('110')
  const [brightnessFloorHz, setBrightnessFloorHz] = useState('2')
  const [autoResult, setAutoResult] = useState<AutoBrightnessResult | null>(null)

  const [focusScore, setFocusScore] = useState<number | null>(null)
  const [focusPeak, setFocusPeak] = useState(0)

  // Refill the form whenever a new settings object arrives — on open, and after anything that
  // changes the camera behind the form's back. Adjusting state during render rather than in an
  // effect keeps the fields from flashing stale values for a frame.
  const [syncedFrom, setSyncedFrom] = useState<CameraSettings | null>(null)
  if (settings !== syncedFrom) {
    setSyncedFrom(settings)
    setExposure(settings?.exposureTimeUs != null ? Math.round(settings.exposureTimeUs).toString() : '')
    setGain(settings?.gain != null ? settings.gain.toFixed(2) : '')
    setBlackLevel(settings?.blackLevel != null ? settings.blackLevel.toString() : '')
    setFrameRate(settings?.frameRateHz != null ? settings.frameRateHz.toString() : '')
    setReverseX(settings?.reverseX ?? false)
    setReverseY(settings?.reverseY ?? false)
    setTriggerDelay(settings?.triggerDelayUs != null ? Math.round(settings.triggerDelayUs).toString() : '0')
    setTriggerActivation(settings?.triggerActivation ?? 'RisingEdge')
    setTriggerKind(
      settings?.triggerMode !== 'On'
        ? 'free'
        : settings.triggerSource === 'Software'
          ? 'internal'
          : settings.triggerSource === 'TCP/IP'
            ? 'tcp'
            : 'external',
    )
    setIsDirty(false)
  }

  useEffect(() => {
    getExternalIo()
      .then((info) => {
        setExternalIo(info)
        setTcpPort(info.settings.port.toString())
        setTcpMessage(info.settings.triggerMessage)
      })
      .catch(() => undefined)

    getCameraIdentity()
      .then((current) => {
        setIdentity(current)
        setUserId(current.userId ?? '')
        setPersistentIp(current.persistentIp ?? '')
        setSubnetMask(current.persistentSubnetMask ?? '')
        setGateway(current.persistentGateway ?? '')
        setIpConfiguration(current.ipConfiguration ?? 'PersistentIP')
      })
      .catch(() => undefined)
  }, [])

  // The focus meter only runs while its tab is open: each reading is a full-frame Laplacian on the
  // server, and there is no reason to pay for that while the operator is on another tab.
  const peakRef = useRef(0)
  useEffect(() => {
    if (tab !== 'focus') {
      return
    }

    let cancelled = false
    const timer = setInterval(() => {
      measureFocus(null)
        .then((score) => {
          if (cancelled) {
            return
          }

          setFocusScore(score.sharpness)
          peakRef.current = Math.max(peakRef.current, score.sharpness)
          setFocusPeak(peakRef.current)
        })
        .catch(() => undefined)
    }, FOCUS_POLL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [tab])

  const run = async (label: string, action: () => Promise<void>) => {
    setError(null)
    setNotice(null)
    setBusy(label)
    try {
      await action()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İşlem başarısız')
    } finally {
      setBusy(null)
    }
  }

  const edit = <T,>(setter: (value: T) => void) => (value: T) => {
    setter(value)
    setIsDirty(true)
  }

  /** Writes every pending change in one go, so the camera cycles acquisition once rather than per field. */
  const applyDraft = async () => {
    // First, and on its own: a port that cannot be used should stop the whole apply before the
    // camera has been switched into a mode that waits for a message that can never arrive.
    if (triggerKind === 'tcp' && externalIo) {
      const port = Math.round(toNumber(tcpPort) ?? 0)
      const saved = await saveExternalIo({
        ...externalIo.settings,
        enabled: true,
        port,
        triggerMessage: tcpMessage.trim(),
      })
      setExternalIo(saved)
      setTcpPort(saved.settings.port.toString())
      setTcpMessage(saved.settings.triggerMessage)
    }

    const applied = await updateCameraSettings({
      exposureTimeUs: toNumber(exposure),
      gain: toNumber(gain),
      blackLevel: toNumber(blackLevel),
      frameRateHz: toNumber(frameRate),
      reverseX,
      reverseY,
      triggerMode: triggerKind === 'free' ? 'Off' : 'On',
      triggerSource:
        triggerKind === 'internal'
          ? 'Software'
          : triggerKind === 'external'
            ? 'Line1'
            : triggerKind === 'tcp'
              ? 'TCP/IP'
              : undefined,
      triggerActivation: triggerKind === 'external' ? triggerActivation : undefined,
      triggerDelayUs: triggerKind === 'external' ? toNumber(triggerDelay) : undefined,
    })
    onSettingsChange(applied)

    const identityChanged =
      userId !== (identity?.userId ?? '') ||
      persistentIp !== (identity?.persistentIp ?? '') ||
      subnetMask !== (identity?.persistentSubnetMask ?? '') ||
      gateway !== (identity?.persistentGateway ?? '') ||
      ipConfiguration !== (identity?.ipConfiguration ?? '')

    if (identityChanged) {
      const result = await updateCameraIdentity({
        userId: userId !== (identity?.userId ?? '') ? userId : undefined,
        persistentIp: persistentIp !== (identity?.persistentIp ?? '') ? persistentIp : undefined,
        persistentSubnetMask:
          subnetMask !== (identity?.persistentSubnetMask ?? '') ? subnetMask : undefined,
        persistentGateway: gateway !== (identity?.persistentGateway ?? '') ? gateway : undefined,
        ipConfiguration: ipConfiguration !== (identity?.ipConfiguration ?? '') ? ipConfiguration : undefined,
      })

      setIdentity(result.identity)
      if (result.message) {
        setNotice(result.message)
      }
    }

    setIsDirty(false)
  }

  return (
    <div className="modal__scrim">
      <div className="modal modal--wide">
        <div className="modal__title">
          <span>Kamera Ayarları{settings ? '' : ' — kamera bağlı değil'}</span>
        </div>

        <div className="tabs">
          {TABS.map((t) => (
            <button
              key={t.id}
              type="button"
              className={tab === t.id ? 'is-active' : ''}
              onClick={() => setTab(t.id)}
            >
              {t.label}
            </button>
          ))}
        </div>

        <div className="modal__body">
          {error && <p className="error-text">{error}</p>}
          {notice && <p className="notice-text">{notice}</p>}

          {tab === 'trigger' && settings && (
            <>
              <div className="row">
                <label className="labeled" style={{ flex: 1 }}>
                  Tetikleme tipi
                  <select
                    className="field"
                    value={triggerKind}
                    onChange={(e) => edit(setTriggerKind)(e.target.value as typeof triggerKind)}
                  >
                    <option value="free">Sürekli — kamera kendi kare hızında çalışır</option>
                    <option value="internal">Dahili tetik — uygulama belirli aralıkla tetikler</option>
                    <option value="external">Harici tetik — sensör/PLC sinyali (Line1)</option>
                    <option value="tcp">TCP/IP tetik — kontrolcüden gelen mesajla tetiklenir</option>
                  </select>
                </label>
              </div>

              {triggerKind === 'tcp' && (
                <>
                  <div className="row">
                    <label className="labeled">
                      Dinlenen port
                      <input
                        className="field"
                        type="number"
                        min={1024}
                        max={65535}
                        value={tcpPort}
                        onChange={(e) => edit(setTcpPort)(e.target.value)}
                      />
                    </label>
                    <label className="labeled" style={{ flex: 1 }}>
                      Tetik mesajı
                      <input
                        className="field"
                        value={tcpMessage}
                        onChange={(e) => edit(setTcpMessage)(e.target.value)}
                      />
                    </label>
                  </div>
                  <p className="hint-text">
                    {externalIo?.status.listening
                      ? `Şu an dinliyor: port ${externalIo.status.port} · ${externalIo.status.clients.length} istemci bağlı. `
                      : 'TCP/IP arayüzü şu an kapalı; Uygula ile açılır. '}
                    Kamera yazılım tetiği modunda bekler; bu port üzerinden gelen tetik mesajı bir kare
                    üretir. Çalıştır modunda o kare denetlenir, Program modunda yalnızca görüntü alınır.
                    Durum sinyalleri ve mesaj metinleri menüdeki Haberleşme penceresinden ayarlanır.
                  </p>
                </>
              )}

              {triggerKind === 'external' && (
                <div className="row">
                  <label className="labeled">
                    Tetik kenarı
                    <select
                      className="field"
                      value={triggerActivation}
                      onChange={(e) => edit(setTriggerActivation)(e.target.value)}
                    >
                      <option value="RisingEdge">Yükselen kenar</option>
                      <option value="FallingEdge">Düşen kenar</option>
                      <option value="AnyEdge">Her kenar</option>
                      <option value="LevelHigh">Seviye yüksek</option>
                      <option value="LevelLow">Seviye düşük</option>
                    </select>
                  </label>
                  <label className="labeled">
                    Tetik gecikmesi (µs)
                    <input
                      className="field"
                      type="number"
                      value={triggerDelay}
                      min={settings.triggerDelayRange?.min ?? 0}
                      max={settings.triggerDelayRange?.max ?? undefined}
                      onChange={(e) => edit(setTriggerDelay)(e.target.value)}
                    />
                    <small>{describeRange(settings.triggerDelayRange)}</small>
                  </label>
                </div>
              )}

              <p className="hint-text">
                Dahili, harici ve TCP/IP tetiklemede kamera boşta bekler, yalnızca tetik geldiğinde tek kare üretir.
                Dahili tetiğin aralığı ana ekrandaki mod çubuğundan ayarlanır. Kameradaki güncel durum:{' '}
                {settings.triggerMode === 'On' ? `tetiklemeli (${settings.triggerSource})` : 'sürekli'}.
              </p>
            </>
          )}

          {tab === 'brightness' && settings && (
            <>
              <div className="pane__title">Otomatik Parlaklık Ayarı</div>
              <div className="row" style={{ marginTop: 10 }}>
                <label className="labeled">
                  Hedef gri seviye
                  <input
                    className="field"
                    type="number"
                    min={0}
                    max={255}
                    value={brightnessTarget}
                    onChange={(e) => setBrightnessTarget(e.target.value)}
                  />
                  <small>0 – 255</small>
                </label>
                <label className="labeled">
                  En düşük kare hızı (Hz)
                  <input
                    className="field"
                    type="number"
                    min={0.06}
                    step={0.5}
                    value={brightnessFloorHz}
                    onChange={(e) => setBrightnessFloorHz(e.target.value)}
                  />
                  <small>pozlama bunun altına inmez</small>
                </label>
                <div className="labeled labeled--action">
                  <span aria-hidden>&nbsp;</span>
                  <button
                    type="button"
                    className="btn"
                    disabled={busy !== null}
                    onClick={() =>
                      run('auto', async () => {
                        const result = await autoAdjustBrightness(
                          toNumber(brightnessTarget) ?? 110,
                          toNumber(brightnessFloorHz) ?? 2,
                        )
                        setAutoResult(result)
                        onSettingsChange(await getCameraSettings())
                      })
                    }
                  >
                    {busy === 'auto' ? 'Ayarlanıyor…' : 'Otomatik Ayarla'}
                  </button>
                  <small aria-hidden>&nbsp;</small>
                </div>
              </div>

              {autoResult && (
                <p className={autoResult.reached ? 'notice-text' : 'error-text'}>
                  {autoResult.reached
                    ? `Hedefe ulaşıldı: parlaklık ${autoResult.measuredBrightness}, pozlama ${Math.round(
                        autoResult.exposureTimeUs ?? 0,
                      )} µs, kazanç ${autoResult.gain?.toFixed(1)}, ${autoResult.frameRateHz} Hz.`
                    : (autoResult.message ?? 'Hedefe ulaşılamadı.')}
                </p>
              )}

              <div className="pane__title" style={{ marginTop: 6 }}>
                Elle Ayar
              </div>
              <div className="row" style={{ marginTop: 10 }}>
                <label className="labeled">
                  Pozlama (µs)
                  <input
                    className="field"
                    type="number"
                    value={exposure}
                    min={settings.exposureRange?.min ?? 1}
                    max={settings.exposureRange?.max ?? undefined}
                    step={100}
                    onChange={(e) => edit(setExposure)(e.target.value)}
                  />
                  <small>{describeRange(settings.exposureRange)}</small>
                </label>
                <label className="labeled">
                  Kazanç (x)
                  <input
                    className="field"
                    type="number"
                    value={gain}
                    min={settings.gainRange?.min ?? 1}
                    max={settings.gainRange?.max ?? undefined}
                    step={settings.gainRange?.increment ?? 0.1}
                    onChange={(e) => edit(setGain)(e.target.value)}
                  />
                  <small>{describeRange(settings.gainRange)}</small>
                </label>
                <label className="labeled">
                  Siyah seviye
                  <input
                    className="field"
                    type="number"
                    value={blackLevel}
                    min={settings.blackLevelRange?.min ?? 0}
                    max={settings.blackLevelRange?.max ?? undefined}
                    onChange={(e) => edit(setBlackLevel)(e.target.value)}
                  />
                  <small>{describeRange(settings.blackLevelRange)}</small>
                </label>
                <label className="labeled">
                  Kare hızı (Hz)
                  <input
                    className="field"
                    type="number"
                    value={frameRate}
                    min={settings.frameRateRange?.min ?? 0.1}
                    max={settings.frameRateRange?.max ?? undefined}
                    step={0.5}
                    onChange={(e) => edit(setFrameRate)(e.target.value)}
                  />
                  <small>{describeRange(settings.frameRateRange)}</small>
                </label>
              </div>

              <p className="hint-text">
                Aralık dışı bir değer kameranın kabul ettiği en yakın değere çekilir. Uzun pozlama kare hızını
                düşürür — ikisi birbirini sınırlar.
              </p>
            </>
          )}

          {tab === 'focus' && (
            <>
              <p className="hint-text">
                Bu kamerada motorlu lens yok, dolayısıyla odak yazılımdan sürülemez. Lens halkasını
                çevirirken aşağıdaki netlik değerini en yükseğe çıkarın; tepe değer işaretli kalır.
              </p>

              <div className="focus">
                <div className="focus__value">{focusScore !== null ? Math.round(focusScore) : '—'}</div>
                <div className="focus__bar">
                  <div
                    className="focus__fill"
                    style={{ width: `${focusPeak > 0 ? Math.min(100, ((focusScore ?? 0) / focusPeak) * 100) : 0}%` }}
                  />
                </div>
                <div className="focus__legend">
                  <span>netlik (Laplacian varyansı)</span>
                  <span>tepe: {Math.round(focusPeak)}</span>
                </div>
              </div>

              <div className="row">
                <button
                  type="button"
                  className="btn"
                  onClick={() => {
                    peakRef.current = 0
                    setFocusPeak(0)
                  }}
                >
                  Tepe Değeri Sıfırla
                </button>
              </div>
            </>
          )}

          {tab === 'image' && settings && (
            <>
              <div className="row">
                <label className="labeled">
                  <span>
                    <input type="checkbox" checked={reverseX} onChange={(e) => edit(setReverseX)(e.target.checked)} />{' '}
                    Yatay çevir
                  </span>
                </label>
                <label className="labeled">
                  <span>
                    <input type="checkbox" checked={reverseY} onChange={(e) => edit(setReverseY)(e.target.checked)} />{' '}
                    Dikey çevir
                  </span>
                </label>
              </div>

              <p className="hint-text">
                Sensör alanı: {settings.width ?? '?'}×{settings.height ?? '?'} (offset {settings.offsetX ?? 0},{' '}
                {settings.offsetY ?? 0})
                {settings.deviceTemperatureC != null && ` · sensör sıcaklığı ${settings.deviceTemperatureC.toFixed(1)} °C`}
              </p>

              <div className="pane__title" style={{ marginTop: 12 }}>
                Kameraya Kalıcı Yazma
              </div>
              <div className="row" style={{ marginTop: 10 }}>
                <button
                  type="button"
                  className="btn"
                  disabled={busy !== null}
                  onClick={() =>
                    run('persist', async () => {
                      await saveSettingsToCamera()
                      setNotice('Ayarlar kameranın kalıcı belleğine yazıldı; elektrik kesilse de korunur.')
                    })
                  }
                >
                  {busy === 'persist' ? 'Kaydediliyor…' : 'Ayarları Kameraya Kaydet'}
                </button>
                <button
                  type="button"
                  className="btn is-danger"
                  disabled={busy !== null}
                  onClick={() =>
                    run('disconnect', async () => {
                      await disconnectCamera()
                      onDisconnected()
                    })
                  }
                >
                  Bağlantıyı Kes
                </button>
              </div>
              <p className="hint-text">
                Kamera ayarları normalde yalnızca oturum boyunca kalır. Bu düğme onları kameranın kendi
                kalıcı belleğine yazar.
              </p>
            </>
          )}

          {tab === 'identity' && (
            <>
              <div className="row">
                <label className="labeled" style={{ flex: 1 }}>
                  Kamera adı
                  <input
                    className="field"
                    value={userId}
                    maxLength={15}
                    onChange={(e) => edit(setUserId)(e.target.value)}
                  />
                  <small>en fazla 15 karakter · ağ taramasında bu ad görünür</small>
                </label>
              </div>

              <p className="hint-text">
                {identity
                  ? `${identity.modelName} · seri no ${identity.serialNumber} · sürüm ${identity.deviceVersion} · şu anki adres ${identity.currentIp}`
                  : 'Kimlik okunuyor…'}
              </p>

              <div className="pane__title" style={{ marginTop: 8 }}>
                Ağ Ayarları
              </div>

              <div className="row" style={{ marginTop: 10 }}>
                <label className="labeled" style={{ flex: 1 }}>
                  Adres alma yöntemi
                  <select
                    className="field"
                    value={ipConfiguration}
                    onChange={(e) => edit(setIpConfiguration)(e.target.value)}
                  >
                    <option value="PersistentIP">Sabit IP — aşağıdaki adres kullanılır</option>
                    <option value="DHCP">DHCP — adresi ağdaki sunucu verir</option>
                    <option value="LLA">Otomatik (LLA) — 169.254.x.x</option>
                  </select>
                </label>
              </div>

              <div className="row">
                <label className="labeled">
                  IP adresi
                  <input
                    className="field"
                    style={{ width: 140 }}
                    value={persistentIp}
                    placeholder="192.168.10.55"
                    disabled={ipConfiguration !== 'PersistentIP'}
                    onChange={(e) => edit(setPersistentIp)(e.target.value)}
                  />
                </label>
                <label className="labeled">
                  Alt ağ maskesi
                  <input
                    className="field"
                    style={{ width: 140 }}
                    value={subnetMask}
                    placeholder="255.255.255.0"
                    disabled={ipConfiguration !== 'PersistentIP'}
                    onChange={(e) => edit(setSubnetMask)(e.target.value)}
                  />
                </label>
                <label className="labeled">
                  Ağ geçidi
                  <input
                    className="field"
                    style={{ width: 140 }}
                    value={gateway}
                    placeholder="0.0.0.0"
                    disabled={ipConfiguration !== 'PersistentIP'}
                    onChange={(e) => edit(setGateway)(e.target.value)}
                  />
                </label>
              </div>

              <p className="hint-text">
                Ad hemen geçerli olur. <strong>Ağ ayarları ancak kamera yeniden başlatıldığında</strong>{' '}
                geçerli olur — GigE Vision, çalışan bir kamerayı yeni adrese taşımaya izin vermez. Yeni
                adresi bu bilgisayarın da göreceği alt ağda seçin, yoksa kamera yeniden başladıktan sonra
                erişilemez.
              </p>
            </>
          )}

          {!settings && tab !== 'identity' && tab !== 'focus' && (
            <p className="empty">Bu ayarlar için kameraya bağlı olmanız gerekir.</p>
          )}
        </div>

        <div className="modal__footer">
          {isDirty && <span className="modal__dirty">kaydedilmemiş değişiklik var</span>}
          <button type="button" className="btn" onClick={onClose} disabled={busy !== null}>
            Çık
          </button>
          <button
            type="button"
            className="btn"
            disabled={!isDirty || busy !== null}
            onClick={() => run('apply', applyDraft)}
          >
            {busy === 'apply' ? 'Uygulanıyor…' : 'Uygula'}
          </button>
          <button
            type="button"
            className="btn is-primary"
            disabled={busy !== null}
            onClick={() =>
              run('save', async () => {
                if (isDirty) {
                  await applyDraft()
                }
                onClose()
              })
            }
          >
            {busy === 'save' ? 'Kaydediliyor…' : 'Kaydet ve Çık'}
          </button>
        </div>
      </div>
    </div>
  )
}

/** Blank means "leave this setting alone", so it must not become 0. */
function toNumber(draft: string): number | undefined {
  // A Turkish-locale number input can hand back a decimal comma.
  const trimmed = draft.trim().replace(',', '.')
  if (trimmed === '') {
    return undefined
  }

  const value = Number(trimmed)
  return Number.isFinite(value) ? value : undefined
}

function describeRange(range: FeatureRange | null): string {
  if (!range || (range.min === null && range.max === null)) {
    return ''
  }

  const format = (value: number) => (Number.isInteger(value) ? value.toLocaleString('tr-TR') : value.toFixed(2))
  return `${range.min !== null ? format(range.min) : '?'} – ${range.max !== null ? format(range.max) : '?'}`
}
