import { API_BASE_URL, type HealthResponse } from '../api/client'

/** The subset of a camera's identity the About screen shows, however the session obtained it. */
export interface AboutDevice {
  model: string
  manufacturerName: string
  serialNumber: string
  deviceVersion: string
  ip: string
  mac: string
  localIp: string | null
}

interface AboutDialogProps {
  health: HealthResponse | null
  healthError: string | null
  device: AboutDevice | null
  onClose: () => void
}

/**
 * Identity and connection status. The application name lives here rather than in the chrome so the
 * top of the window stays given over to the controls an operator actually uses.
 */
export function AboutDialog({ health, healthError, device, onClose }: AboutDialogProps) {
  return (
    <div className="modal__scrim" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal">
        <div className="modal__title">
          <span>Uygulama Hakkında</span>
          <button type="button" className="btn" onClick={onClose}>
            Kapat
          </button>
        </div>

        <div className="modal__body">
          <div className="about__brand">
            <span className="app__logo" />
            <strong>Vision</strong>
          </div>

          <div className="pane__title">API Bağlantısı</div>
          <dl className="kv" style={{ margin: '10px 0 14px' }}>
            <dt>Adres</dt>
            <dd>{API_BASE_URL}</dd>
            <dt>Durum</dt>
            <dd className={health ? 'is-ok' : 'is-ng'}>{health ? 'Bağlı' : (healthError ?? 'Bağlanamadı')}</dd>
            {health && (
              <>
                <dt>Sunucu saati (UTC)</dt>
                <dd>{health.timeUtc}</dd>
              </>
            )}
          </dl>

          <div className="pane__title">Kamera</div>
          {device ? (
            <dl className="kv" style={{ margin: '10px 0 0' }}>
              <dt>Model</dt>
              <dd>{device.model}</dd>
              <dt>Üretici</dt>
              <dd>{device.manufacturerName}</dd>
              <dt>Seri no</dt>
              <dd>{device.serialNumber}</dd>
              <dt>Sürüm</dt>
              <dd>{device.deviceVersion}</dd>
              <dt>IP / MAC</dt>
              <dd>
                {device.ip} · {device.mac}
              </dd>
              {device.localIp && (
                <>
                  <dt>Yerel arayüz</dt>
                  <dd>{device.localIp}</dd>
                </>
              )}
            </dl>
          ) : (
            <p className="empty">Kamera bağlı değil.</p>
          )}
        </div>
      </div>
    </div>
  )
}
