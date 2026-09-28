import { SNAPSHOT_URL, type InspectionWindow, type Roi, type WindowResult } from '../api/client'
import { RoiEditor, type DrawMode } from './RoiEditor'
import { stepZoom, type Zoom } from './zoom'
import './ImageStage.css'

interface ImageStageProps {
  imageUrl: string | null
  frameWidth: number
  frameHeight: number
  windows: InspectionWindow[]
  results: WindowResult[] | null
  selectedId: string | null
  editable: boolean
  onSelect: (id: string | null) => void
  onCreate: (region: Roi) => void
  onChange: (id: string, region: Roi) => void
  onPatternChange: (region: Roi) => void
  /** Left side of the black bar: the verdict in Run mode, the mode name in Program. */
  verdict: 'OK' | 'NG' | null
  headline: string
  /** Right side of the black bar — trigger mode, cycle time. */
  meta: string
  zoom: Zoom
  onZoomChange: (zoom: Zoom) => void
  showWindows: boolean
  onShowWindowsChange: (show: boolean) => void
  /** What a drag on the image draws: a window, a mask inside one, or the position pattern. */
  drawMode: DrawMode
  pattern: Roi | null
  searchMargin: number
  positionEnabled: boolean
  /** Manual trigger, shown only when the camera is waiting for one. */
  onManualTrigger: (() => void) | null
  isTriggering: boolean
  hint?: string
}

export function ImageStage({
  imageUrl,
  frameWidth,
  frameHeight,
  windows,
  results,
  selectedId,
  editable,
  onSelect,
  onCreate,
  onChange,
  onPatternChange,
  verdict,
  headline,
  meta,
  zoom,
  onZoomChange,
  showWindows,
  onShowWindowsChange,
  drawMode,
  pattern,
  searchMargin,
  positionEnabled,
  onManualTrigger,
  isTriggering,
  hint,
}: ImageStageProps) {
  return (
    <div className="stage">
      <div className={`stage__verdict ${verdict ? `is-${verdict.toLowerCase()}` : ''}`}>
        <span className="stage__headline">{verdict ?? headline}</span>
        <span className="stage__meta">{meta}</span>
      </div>

      <div className="stage__toolbar">
        <button type="button" className="btn btn--icon" onClick={() => onZoomChange(stepZoom(zoom, -1))} title="Uzaklaştır">
          −
        </button>
        <button type="button" className="btn btn--icon" onClick={() => onZoomChange(stepZoom(zoom, 1))} title="Yakınlaştır">
          +
        </button>
        <button
          type="button"
          className={`btn ${zoom === 'fit' ? 'is-toggled' : ''}`}
          onClick={() => onZoomChange('fit')}
          title="Pencereye sığdır"
        >
          Sığdır
        </button>
        <span className="stage__zoom">{zoom === 'fit' ? 'sığdır' : `${Math.round(zoom * 100)} %`}</span>

        <span className="stage__sep" />

        <button
          type="button"
          className={`btn ${showWindows ? 'is-toggled' : ''}`}
          onClick={() => onShowWindowsChange(!showWindows)}
          title="Araçları göster/gizle"
        >
          Araçlar
        </button>

        {onManualTrigger && (
          <button type="button" className="btn is-primary" onClick={onManualTrigger} disabled={isTriggering}>
            {isTriggering ? 'Tetikleniyor…' : 'Manuel Tetik'}
          </button>
        )}

        <a
          className="btn"
          href={SNAPSHOT_URL}
          download={`mtcvision-${new Date().toISOString().replace(/[:.]/g, '-')}.jpg`}
          title="Görüntüyü kaydet"
        >
          Görüntüyü Kaydet
        </a>

        <span className="stage__sep" />
        <span>{frameWidth > 0 ? `${frameWidth}×${frameHeight}` : 'Görüntü yok'}</span>
        <span style={{ flex: 1 }} />
        {hint && <span>{hint}</span>}
      </div>

      <div className="stage__canvas">
        <RoiEditor
          drawMode={drawMode}
          pattern={pattern}
          searchMargin={searchMargin}
          positionEnabled={positionEnabled}
          imageUrl={imageUrl}
          frameWidth={frameWidth}
          frameHeight={frameHeight}
          windows={windows}
          results={results}
          selectedId={selectedId}
          editable={editable}
          zoom={zoom}
          showWindows={showWindows}
          onSelect={onSelect}
          onCreate={onCreate}
          onChange={onChange}
          onPatternChange={onPatternChange}
        />
      </div>
    </div>
  )
}
