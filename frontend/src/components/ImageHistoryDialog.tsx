import { useEffect, useState } from 'react'
import {
  clearSavedImages,
  deleteSavedImage,
  inspectSavedImage,
  listSavedImages,
  saveCurrentImage,
  savedImageUrl,
  updateImageHistorySettings,
  type ImageHistorySettings,
  type ImageSaveMode,
  type InspectionResult,
  type SavedImage,
} from '../api/client'
import './ImageHistoryDialog.css'

const SAVE_MODES: { id: ImageSaveMode; label: string }[] = [
  { id: 'Manual', label: 'Elle — yalnızca "Görüntüyü Sakla" ile' },
  { id: 'All', label: 'Her denetim — reçeteyi denerken kullanışlı' },
  { id: 'FailuresOnly', label: 'Yalnızca NG — çalışan hatta genelde bu istenir' },
]

interface ImageHistoryDialogProps {
  /** Recipe to re-judge a stored image against; null when none is loaded. */
  recipeId: string | null
  isCameraConnected: boolean
  onClose: () => void
}

/**
 * The saved-image history, in place of the reference console's [Image Hist.] screen: what was
 * photographed, what it was judged, and the ability to re-judge it against the recipe as it stands
 * now — which is how an operator finds out whether a limit change would have passed the part that
 * was rejected an hour ago.
 */
export function ImageHistoryDialog({ recipeId, isCameraConnected, onClose }: ImageHistoryDialogProps) {
  const [images, setImages] = useState<SavedImage[]>([])
  const [settings, setSettings] = useState<ImageHistorySettings | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [reJudged, setReJudged] = useState<InspectionResult | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Draft settings, applied on their own button so a half-typed folder is never created.
  const [directory, setDirectory] = useState('')
  const [capacity, setCapacity] = useState('1000')
  const [mode, setMode] = useState<ImageSaveMode>('Manual')

  const selected = images.find((image) => image.id === selectedId) ?? null

  const refresh = async () => {
    const history = await listSavedImages()
    setImages(history.images)
    setSettings(history.settings)
    setDirectory(history.settings.directory)
    setCapacity(history.settings.capacity.toString())
    setMode(history.settings.mode)
    setSelectedId((current) => (history.images.some((i) => i.id === current) ? current : (history.images[0]?.id ?? null)))
  }

  // Loaded once when the dialog opens; the operator refreshes with the button. Started after the
  // effect commits so the dialog paints before the list arrives.
  useEffect(() => {
    const timer = setTimeout(
      () =>
        void refresh().catch((err: unknown) =>
          setError(err instanceof Error ? err.message : 'Görüntü geçmişi okunamadı'),
        ),
      0,
    )
    return () => clearTimeout(timer)
  }, [])

  const run = async (label: string, action: () => Promise<void>) => {
    setError(null)
    setBusy(label)
    try {
      await action()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İşlem başarısız')
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="modal__scrim">
      <div className="modal modal--history">
        <div className="modal__title">
          <span>Görüntü Geçmişi</span>
          <span className="history__count">
            {images.length}
            {settings ? ` / ${settings.capacity}` : ''} görüntü
          </span>
        </div>

        <div className="modal__body history">
          {error && <p className="error-text">{error}</p>}

          <div className="row">
            <label className="labeled" style={{ flex: 1 }}>
              Kayıt klasörü
              <input className="field" value={directory} onChange={(e) => setDirectory(e.target.value)} />
            </label>
            <label className="labeled">
              En fazla görüntü
              <input
                className="field"
                style={{ width: 100 }}
                type="number"
                min={1}
                max={10000}
                value={capacity}
                onChange={(e) => setCapacity(e.target.value)}
              />
              <small>dolunca en eskisi silinir</small>
            </label>
          </div>

          <div className="row">
            <label className="labeled" style={{ flex: 1 }}>
              Kayıt kuralı
              <select className="field" value={mode} onChange={(e) => setMode(e.target.value as ImageSaveMode)}>
                {SAVE_MODES.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
            <button
              type="button"
              className="btn"
              disabled={busy !== null}
              onClick={() =>
                run('settings', async () => {
                  await updateImageHistorySettings({
                    directory,
                    capacity: Math.max(1, Math.min(10000, Number(capacity) || 1000)),
                    mode,
                  })
                  await refresh()
                })
              }
            >
              {busy === 'settings' ? 'Uygulanıyor…' : 'Ayarları Uygula'}
            </button>
          </div>

          <div className="history__split">
            <div className="history__list">
              {images.length === 0 ? (
                <p className="empty">Kayıtlı görüntü yok.</p>
              ) : (
                images.map((image) => (
                  <button
                    key={image.id}
                    type="button"
                    className={`history__item ${image.id === selectedId ? 'is-selected' : ''}`}
                    onClick={() => {
                      setSelectedId(image.id)
                      setReJudged(null)
                    }}
                  >
                    <img src={savedImageUrl(image.id)} alt="" loading="lazy" />
                    <span className="history__meta">
                      <b>{new Date(image.savedAtUtc).toLocaleString('tr-TR')}</b>
                      <small>
                        {image.recipeName ?? 'reçetesiz'} · {Math.round(image.sizeBytes / 1024)} KB
                      </small>
                    </span>
                    {image.verdict && (
                      <span className={`history__verdict is-${image.verdict.toLowerCase()}`}>{image.verdict}</span>
                    )}
                  </button>
                ))
              )}
            </div>

            <div className="history__preview">
              {selected ? (
                <>
                  <img src={savedImageUrl(selected.id)} alt="Kayıtlı görüntü" />
                  <p className="hint-text">
                    {selected.width}×{selected.height} · {new Date(selected.savedAtUtc).toLocaleString('tr-TR')}
                    {selected.note ? ` · ${selected.note}` : ''}
                  </p>

                  {reJudged && (
                    <div className={`history__rejudge ${reJudged.passed ? 'is-ok' : 'is-ng'}`}>
                      <strong>{reJudged.passed ? 'OK' : 'NG'}</strong>
                      <span>güncel reçeteyle yeniden değerlendirildi</span>
                      <table>
                        <tbody>
                          {reJudged.windows.map((w) => (
                            <tr key={w.windowId} className={w.passed ? 'is-ok' : 'is-ng'}>
                              <td>{w.name}</td>
                              <td>{w.value}</td>
                              <td>
                                {w.lowerLimit} – {w.upperLimit}
                              </td>
                              <td>{w.passed ? 'OK' : 'NG'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              ) : (
                <p className="empty">Önizleme için bir görüntü seçin.</p>
              )}
            </div>
          </div>
        </div>

        <div className="modal__footer">
          <button
            type="button"
            className="btn"
            disabled={!isCameraConnected || busy !== null}
            onClick={() =>
              run('save', async () => {
                await saveCurrentImage()
                await refresh()
              })
            }
          >
            {busy === 'save' ? 'Saklanıyor…' : 'Görüntüyü Sakla'}
          </button>
          <button
            type="button"
            className="btn"
            disabled={!selected || !recipeId || busy !== null}
            onClick={() =>
              run('inspect', async () => {
                if (selected && recipeId) {
                  setReJudged(await inspectSavedImage(selected.id, recipeId))
                }
              })
            }
            title={recipeId ? undefined : 'Önce bir reçete seçin'}
          >
            {busy === 'inspect' ? 'Denetleniyor…' : 'Bu Görüntüyü Denetle'}
          </button>
          <button
            type="button"
            className="btn is-danger"
            disabled={!selected || busy !== null}
            onClick={() =>
              run('delete', async () => {
                if (selected) {
                  await deleteSavedImage(selected.id)
                  setReJudged(null)
                  await refresh()
                }
              })
            }
          >
            Seçiliyi Sil
          </button>
          <button
            type="button"
            className="btn is-danger"
            disabled={images.length === 0 || busy !== null}
            onClick={() =>
              run('clear', async () => {
                await clearSavedImages()
                setReJudged(null)
                await refresh()
              })
            }
          >
            Tümünü Sil
          </button>
          <span style={{ flex: 1 }} />
          <button type="button" className="btn" onClick={() => void run('refresh', refresh)}>
            Yenile
          </button>
          <button type="button" className="btn is-primary" onClick={onClose}>
            Kapat
          </button>
        </div>
      </div>
    </div>
  )
}
