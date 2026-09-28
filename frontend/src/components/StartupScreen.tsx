import { useCallback, useEffect, useState } from 'react'
import {
  connectCamera,
  discoverCameras,
  getCameraBackend,
  getCameraSettings,
  setCameraBackend,
  type CameraBackendId,
  type CameraBackendInfo,
  type CameraInformation,
  type CameraSettings,
  type HealthResponse,
} from '../api/client'
import './StartupScreen.css'

interface StartupScreenProps {
  health: HealthResponse | null
  healthError: string | null
  onConnected: (device: CameraInformation, settings: CameraSettings) => void
  onSkip: () => void
}

/**
 * The screen the console opens on, in place of the reference tool's activation menu: pick the
 * camera and connect before the main window appears.
 *
 * Discovery starts on its own — on a line there is normally exactly one camera, and making the
 * operator press "search" before "connect" every shift is ceremony. Working offline stays possible
 * because recipes can be reviewed and edited without a camera attached.
 */
export function StartupScreen({ health, healthError, onConnected, onSkip }: StartupScreenProps) {
  const [devices, setDevices] = useState<CameraInformation[]>([])
  const [selectedIp, setSelectedIp] = useState('')
  const [busy, setBusy] = useState<'discover' | 'connect' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [backendInfo, setBackendInfo] = useState<CameraBackendInfo | null>(null)

  const selected = devices.find((d) => d.ip === selectedIp) ?? null
  const currentBackend = backendInfo?.backends.find((b) => b.id === backendInfo.current) ?? null

  const discover = useCallback(async () => {
    setError(null)
    setBusy('discover')
    try {
      const found = await discoverCameras()
      setDevices(found)
      setSelectedIp(found[0]?.ip ?? '')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Kamera taraması başarısız')
    } finally {
      setBusy(null)
    }
  }, [])

  // Only once the backend has answered: a scan against a server that is not up just fails twice.
  // Started after the effect commits so the first paint is not held up by a network round trip.
  useEffect(() => {
    if (!health) {
      return
    }

    let cancelled = false
    const timer = setTimeout(async () => {
      try {
        const info = await getCameraBackend()
        if (!cancelled) {
          setBackendInfo(info)
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Kamera tipi okunamadı')
        }
      }

      if (!cancelled) {
        void discover()
      }
    }, 0)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [health, discover])

  // The list of cameras belongs to the kind that was scanned for, so a change starts over.
  const changeBackend = async (id: CameraBackendId) => {
    if (!backendInfo || id === backendInfo.current) {
      return
    }

    setError(null)
    setBusy('discover')
    try {
      await setCameraBackend(id)
      setBackendInfo({ ...backendInfo, current: id })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Kamera tipi değiştirilemedi')
      setBusy(null)
      return
    }

    setDevices([])
    setSelectedIp('')
    setBusy(null)
    await discover()
  }

  const connect = async () => {
    if (!selected) {
      return
    }

    setError(null)
    setBusy('connect')
    try {
      await connectCamera(selected.ip, selected.localIp)
      onConnected(selected, await getCameraSettings())
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Kameraya bağlanılamadı')
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="startup">
      <header className="startup__header">
        <span className="startup__logo" />
        <h1>Vision</h1>
      </header>

      <main className="startup__body">
        <div className="startup__cards">
        <section className="startup__card">
          <h2>Kameraya Bağlan</h2>

          {healthError ? (
            <p className="error-text">
              Sunucuya bağlanılamadı: {healthError}
              <br />
              Arka uç çalışıyor mu? (backend klasöründe: dotnet run)
            </p>
          ) : !health ? (
            <p className="startup__status">Sunucu kontrol ediliyor…</p>
          ) : (
            <>
              {backendInfo && (
                <>
                  <label className="labeled">
                    Kamera tipi
                    <select
                      className="field"
                      value={backendInfo.current}
                      onChange={(e) => void changeBackend(e.target.value as CameraBackendId)}
                      disabled={busy !== null}
                    >
                      {backendInfo.backends.map((b) => (
                        <option key={b.id} value={b.id}>
                          {b.name}
                        </option>
                      ))}
                    </select>
                  </label>
                  {currentBackend && (
                    <p className="startup__detail startup__detail--spaced">{currentBackend.description}</p>
                  )}
                </>
              )}

              <label className="labeled">
                Ağdaki kameralar
                <select
                  className="field"
                  value={selectedIp}
                  onChange={(e) => setSelectedIp(e.target.value)}
                  disabled={devices.length === 0}
                  size={devices.length > 1 ? Math.min(4, devices.length) : 1}
                >
                  {devices.length === 0 && (
                    <option value="">{busy === 'discover' ? 'Taranıyor…' : '— kamera bulunamadı —'}</option>
                  )}
                  {devices.map((d) => (
                    <option key={d.ip} value={d.ip}>
                      {d.model} — {d.ip}
                      {d.serialNumber ? ` (${d.serialNumber})` : ''}
                    </option>
                  ))}
                </select>
              </label>

              {selected && (
                <p className="startup__detail">
                  {[
                    selected.manufacturerName,
                    selected.deviceVersion && `sürüm ${selected.deviceVersion}`,
                    selected.mac && `MAC ${selected.mac}`,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                  {selected.localIp && (
                    <>
                      <br />
                      Yerel arayüz: {selected.localIp}
                    </>
                  )}
                </p>
              )}

              {error && <p className="error-text">{error}</p>}

              <div className="startup__actions">
                <button type="button" className="btn" onClick={() => void discover()} disabled={busy !== null}>
                  {busy === 'discover' ? 'Taranıyor…' : 'Yeniden Tara'}
                </button>
                <button
                  type="button"
                  className="btn is-primary startup__connect"
                  onClick={() => void connect()}
                  disabled={!selected || busy !== null}
                >
                  {busy === 'connect' ? 'Bağlanıyor…' : 'Bağlan'}
                </button>
              </div>
            </>
          )}
        </section>

        <section className="startup__card startup__card--muted">
          <h2>Kamerasız Devam Et</h2>
          <p className="startup__detail">
            Kayıtlı reçeteleri açıp pencereleri ve limitleri inceleyebilirsiniz. Canlı görüntü, denetim ve
            kamera ayarları için bağlantı gerekir; ana ekrandan sonradan bağlanabilirsiniz.
          </p>
          <div className="startup__actions">
            <button type="button" className="btn" onClick={onSkip} disabled={busy !== null}>
              Kamerasız Aç
            </button>
          </div>
        </section>
        </div>
      </main>

      <footer className="startup__footer">
        <span>{health ? 'Sunucu bağlı' : 'Sunucu bekleniyor'}</span>
      </footer>
    </div>
  )
}
