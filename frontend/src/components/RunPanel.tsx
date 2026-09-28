import type { InspectionResult, InspectionWindow } from '../api/client'
import { StatusGauge } from './StatusGauge'
import type { RunStatistics } from './runStatistics'
import './RunPanel.css'

interface RunPanelProps {
  windows: InspectionWindow[]
  result: InspectionResult | null
  statistics: RunStatistics
  selectedWindowId: string | null
  onSelectWindow: (id: string | null) => void
  onResetStatistics: () => void
}

export function RunPanel({
  windows,
  result,
  statistics,
  selectedWindowId,
  onSelectWindow,
  onResetStatistics,
}: RunPanelProps) {
  const selected = result?.windows.find((w) => w.windowId === selectedWindowId) ?? null

  return (
    <>
      <div className="pane pane--fill">
        <div className="pane__title">
          <span>Araç Durumu</span>
          <span>{windows.length} pencere</span>
        </div>
        <div className="pane__body">
          {result && (result.positionApplied || result.message) && (
            <p className={`position-note ${result.message ? 'is-bad' : ''}`}>
              {result.message ??
                `Konum bulundu · kayma ${result.positionOffsetX >= 0 ? '+' : ''}${result.positionOffsetX}, ` +
                  `${result.positionOffsetY >= 0 ? '+' : ''}${result.positionOffsetY} px · eşleşme %${result.positionScore.toFixed(1)}`}
            </p>
          )}
          {windows.length === 0 ? (
            <p className="empty">Bu reçetede pencere yok. Program moduna geçip pencere çizin.</p>
          ) : (
            windows.map((window) => (
              <StatusGauge
                key={window.id}
                window={window}
                result={result?.windows.find((w) => w.windowId === window.id) ?? null}
                isSelected={window.id === selectedWindowId}
                onSelect={() => onSelectWindow(window.id)}
              />
            ))
          )}
        </div>
      </div>

      <div className="pane">
        <div className="pane__title">
          <span>{selected ? `Araç Bilgisi — ${selected.name}` : 'Araç Bilgisi'}</span>
        </div>
        <div className="pane__body">
          {selected ? (
            <dl className="kv">
              <dt>Ölçüm</dt>
              <dd>{selected.value}</dd>
              {selected.text !== null && (
                <>
                  <dt>Okunan metin</dt>
                  <dd>{selected.text || '(okunamadı)'}</dd>
                </>
              )}
              <dt>Limitler</dt>
              <dd>
                {selected.lowerLimit} – {selected.upperLimit}
              </dd>
              <dt>Alan</dt>
              <dd>
                {selected.region.x}, {selected.region.y} · {selected.region.width}×
                {selected.region.height}
              </dd>
              <dt>Sonuç</dt>
              <dd className={selected.passed ? 'is-ok' : 'is-ng'}>
                {selected.passed ? 'OK' : 'NG'}
              </dd>
              {selected.message && (
                <>
                  <dt>Not</dt>
                  <dd style={{ fontFamily: 'var(--sans)', fontSize: 11 }}>{selected.message}</dd>
                </>
              )}
            </dl>
          ) : (
            <p className="empty">Ayrıntı için bir araç seçin.</p>
          )}
        </div>
      </div>

      <div className="pane">
        <div className="pane__title">
          <span>İstatistik</span>
          <button type="button" className="btn" onClick={onResetStatistics}>
            Sıfırla
          </button>
        </div>
        <div className="pane__body">
          <div className="stats">
            <div>
              <span>Toplam</span>
              <b>{statistics.total.toLocaleString('tr-TR')}</b>
            </div>
            <div>
              <span>OK</span>
              <b className="is-ok">{statistics.ok.toLocaleString('tr-TR')}</b>
            </div>
            <div>
              <span>NG</span>
              <b className="is-ng">{statistics.ng.toLocaleString('tr-TR')}</b>
            </div>
            <div>
              <span>Süre</span>
              <b>{Math.round(statistics.lastMs)} ms</b>
            </div>
            <div>
              <span>En kısa</span>
              <b>{Math.round(statistics.minMs)} ms</b>
            </div>
            <div>
              <span>En uzun</span>
              <b>{Math.round(statistics.maxMs)} ms</b>
            </div>
            <div>
              <span>Ortalama</span>
              <b>{statistics.total > 0 ? Math.round(statistics.sumMs / statistics.total) : 0} ms</b>
            </div>
            <div>
              <span>NG oranı</span>
              <b>
                {statistics.total > 0
                  ? `${((statistics.ng / statistics.total) * 100).toFixed(1)} %`
                  : '—'}
              </b>
            </div>
          </div>
        </div>
      </div>
    </>
  )
}
