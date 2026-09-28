import { useRef, useState } from 'react'
import type { InspectionWindow, Roi, WindowResult } from '../api/client'
import './RoiEditor.css'

/** Below this, a drag is a click that selects rather than an attempt to draw a window. */
const MIN_DRAG_PIXELS = 8

/**
 * What a drag on the image draws.
 *
 * One canvas, three things to place: the windows that measure, the areas inside a window that must
 * be left out of the measurement, and the pattern that says where the part is. Keeping them on one
 * canvas with a mode - rather than opening a separate editor for each - means every one of them is
 * drawn against the same picture, at the same zoom, next to the others it has to line up with.
 */
export type DrawMode = 'window' | 'mask' | 'pattern'

interface RoiEditorProps {
  imageUrl: string | null
  frameWidth: number
  frameHeight: number
  windows: InspectionWindow[]
  results: WindowResult[] | null
  selectedId: string | null
  /** Windows can only be drawn and dragged while teaching; in Run mode they are annotations. */
  editable: boolean
  /** Magnification, or "fit" to letterbox the whole frame into the stage. */
  zoom: number | 'fit'
  /** The reference console's "tool display" switch: hide the overlay to see the bare image. */
  showWindows: boolean
  drawMode: DrawMode
  /** The taught position pattern, drawn so it can be seen against the part it was taught on. */
  pattern: Roi | null
  /** How far around the pattern the search runs, drawn as the fainter outer box. */
  searchMargin: number
  positionEnabled: boolean
  onSelect: (id: string | null) => void
  onCreate: (region: Roi) => void
  onChange: (id: string, region: Roi) => void
  /** The position pattern moved or resized on the image. */
  onPatternChange: (region: Roi) => void
}

type Drag =
  | { kind: 'create'; originX: number; originY: number; region: Roi }
  | { kind: 'move'; id: string; grabX: number; grabY: number; start: Roi }
  | { kind: 'resize'; id: string; start: Roi }
  | { kind: 'pattern-move'; grabX: number; grabY: number; start: Roi }
  | { kind: 'pattern-resize'; start: Roi }

export function RoiEditor({
  imageUrl,
  frameWidth,
  frameHeight,
  windows,
  results,
  selectedId,
  editable,
  zoom,
  showWindows,
  drawMode,
  pattern,
  searchMargin,
  positionEnabled,
  onSelect,
  onCreate,
  onChange,
  onPatternChange,
}: RoiEditorProps) {
  const svgRef = useRef<SVGSVGElement>(null)
  const [drag, setDrag] = useState<Drag | null>(null)

  // Whether the pattern is the thing that was last clicked, which is when it shows its handle.
  // Kept here rather than in the app: a window's selection drives a whole panel, the pattern's
  // only decides whether a corner handle is drawn.
  const [patternActive, setPatternActive] = useState(false)

  /**
   * Screen point to sensor pixel. The SVG shares the image's viewBox and object-fit, so the
   * browser's own transform is the source of truth for where a click landed — no letterbox maths.
   */
  const toSensor = (event: React.PointerEvent): { x: number; y: number } | null => {
    const svg = svgRef.current
    const screenToLocal = svg?.getScreenCTM()?.inverse()
    if (!svg || !screenToLocal) {
      return null
    }

    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(screenToLocal)
    return { x: Math.round(point.x), y: Math.round(point.y) }
  }

  const clampRegion = (region: Roi): Roi => {
    const x = Math.max(0, Math.min(region.x, frameWidth - 1))
    const y = Math.max(0, Math.min(region.y, frameHeight - 1))
    return {
      x,
      y,
      width: Math.max(1, Math.min(region.width, frameWidth - x)),
      height: Math.max(1, Math.min(region.height, frameHeight - y)),
    }
  }

  const handlePointerDown = (event: React.PointerEvent) => {
    if (!editable) {
      return
    }

    const point = toSensor(event)
    if (!point) {
      return
    }

    event.currentTarget.setPointerCapture(event.pointerId)

    // While drawing a mask or the position pattern, a drag that starts over a window is drawing on
    // it, not grabbing it. Moving windows by accident is exactly what you do not want when the
    // thing you are drawing has to line up with what is underneath.
    if (drawMode !== 'window') {
      setDrag({
        kind: 'create',
        originX: point.x,
        originY: point.y,
        region: { x: point.x, y: point.y, width: 0, height: 0 },
      })
      return
    }

    const target = event.target as Element

    if (pattern && target.getAttribute('data-pattern-handle')) {
      setDrag({ kind: 'pattern-resize', start: pattern })
      return
    }

    const handleId = target.getAttribute('data-resize-handle')
    if (handleId) {
      const window = windows.find((w) => w.id === handleId)
      if (window) {
        setPatternActive(false)
        setDrag({ kind: 'resize', id: handleId, start: window.region })
        onSelect(handleId)
        return
      }
    }

    const hitId = target.getAttribute('data-window-id')
    if (hitId) {
      const window = windows.find((w) => w.id === hitId)
      if (window) {
        setPatternActive(false)
        setDrag({
          kind: 'move',
          id: hitId,
          grabX: point.x - window.region.x,
          grabY: point.y - window.region.y,
          start: window.region,
        })
        onSelect(hitId)
        return
      }
    }

    // The pattern is grabbed like a window: drag to move, corner handle to resize.
    if (pattern && target.getAttribute('data-pattern')) {
      setPatternActive(true)
      onSelect(null)
      setDrag({
        kind: 'pattern-move',
        grabX: point.x - pattern.x,
        grabY: point.y - pattern.y,
        start: pattern,
      })
      return
    }

    setPatternActive(false)
    onSelect(null)
    setDrag({
      kind: 'create',
      originX: point.x,
      originY: point.y,
      region: { x: point.x, y: point.y, width: 0, height: 0 },
    })
  }

  const handlePointerMove = (event: React.PointerEvent) => {
    if (!drag) {
      return
    }

    const point = toSensor(event)
    if (!point) {
      return
    }

    if (drag.kind === 'create') {
      setDrag({
        ...drag,
        region: {
          x: Math.min(drag.originX, point.x),
          y: Math.min(drag.originY, point.y),
          width: Math.abs(point.x - drag.originX),
          height: Math.abs(point.y - drag.originY),
        },
      })
      return
    }

    if (drag.kind === 'move') {
      onChange(drag.id, clampRegion({ ...drag.start, x: point.x - drag.grabX, y: point.y - drag.grabY }))
      return
    }

    if (drag.kind === 'pattern-move') {
      onPatternChange(clampRegion({ ...drag.start, x: point.x - drag.grabX, y: point.y - drag.grabY }))
      return
    }

    if (drag.kind === 'pattern-resize') {
      onPatternChange(
        clampRegion({ ...drag.start, width: point.x - drag.start.x, height: point.y - drag.start.y }),
      )
      return
    }

    onChange(
      drag.id,
      clampRegion({
        ...drag.start,
        width: point.x - drag.start.x,
        height: point.y - drag.start.y,
      }),
    )
  }

  const handlePointerUp = (event: React.PointerEvent) => {
    event.currentTarget.releasePointerCapture(event.pointerId)

    if (drag?.kind === 'create') {
      const { region } = drag
      if (region.width >= MIN_DRAG_PIXELS && region.height >= MIN_DRAG_PIXELS) {
        onCreate(clampRegion(region))
      }
    }

    setDrag(null)
  }

  const verdictOf = (id: string): 'pass' | 'fail' | null => {
    const result = results?.find((r) => r.windowId === id)
    return result ? (result.passed ? 'pass' : 'fail') : null
  }

  // Keep strokes and labels a constant size on screen no matter how large the sensor is.
  const scale = frameWidth > 0 ? frameWidth / 1000 : 1

  // At "fit" the frame fills the stage and letterboxes; magnified, it takes its natural pixel size
  // times the zoom and the stage scrolls. Either way the image and the overlay share one box, so
  // sensor coordinates keep landing on the pixels they describe.
  const frameStyle =
    zoom === 'fit' || frameWidth === 0
      ? undefined
      : { width: frameWidth * zoom, height: frameHeight * zoom, flex: 'none' as const }

  return (
    <div className={`roi-editor ${zoom === 'fit' ? '' : 'is-zoomed'}`}>
      <div className="roi-editor__frame" style={frameStyle}>
      {imageUrl ? (
        <img src={imageUrl} alt="Canlı kamera görüntüsü" />
      ) : (
        <div className="roi-editor__placeholder">Canlı görüntü yok</div>
      )}

      {showWindows && frameWidth > 0 && frameHeight > 0 && (
        <svg
          ref={svgRef}
          className={`roi-editor__overlay ${editable ? 'is-editable' : ''}`}
          viewBox={`0 0 ${frameWidth} ${frameHeight}`}
          preserveAspectRatio="xMidYMid meet"
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
        >
          {/* Shown even while position correction is off (dimmed): a pattern that vanishes with the
              switch cannot be found again to move or delete. */}
          {pattern && (positionEnabled || editable) && (
            <g className={`roi-position ${positionEnabled ? '' : 'is-disabled'}`}>
              {positionEnabled && (
                <rect
                  className="roi-position__search"
                  x={pattern.x - searchMargin}
                  y={pattern.y - searchMargin}
                  width={pattern.width + 2 * searchMargin}
                  height={pattern.height + 2 * searchMargin}
                  strokeWidth={1.5 * scale}
                />
              )}
              <rect
                data-pattern="true"
                className={`roi-position__pattern ${patternActive && selectedId === null ? 'is-selected' : ''}`}
                x={pattern.x}
                y={pattern.y}
                width={pattern.width}
                height={pattern.height}
                strokeWidth={2 * scale}
              />
              <text
                className="roi-position__label"
                x={pattern.x + 6 * scale}
                y={pattern.y - 8 * scale}
                fontSize={16 * scale}
              >
                Konum deseni
              </text>
              {editable && patternActive && selectedId === null && drawMode === 'window' && (
                <rect
                  data-pattern-handle="true"
                  className="roi__handle"
                  x={pattern.x + pattern.width - 7 * scale}
                  y={pattern.y + pattern.height - 7 * scale}
                  width={14 * scale}
                  height={14 * scale}
                />
              )}
            </g>
          )}

          {windows.map((window) => {
            const verdict = verdictOf(window.id)
            return (
              <g key={window.id} className={`roi roi--${verdict ?? 'neutral'}`}>
                <rect
                  data-window-id={window.id}
                  className={`roi__rect ${window.id === selectedId ? 'is-selected' : ''}`}
                  x={window.region.x}
                  y={window.region.y}
                  width={window.region.width}
                  height={window.region.height}
                  strokeWidth={2 * scale}
                />
                <text
                  className="roi__label"
                  x={window.region.x + 6 * scale}
                  y={window.region.y - 8 * scale}
                  fontSize={16 * scale}
                >
                  {window.name}
                </text>
                {window.mask.map((area, index) => (
                  <rect
                    key={`${window.id}-mask-${index}`}
                    className="roi__mask"
                    x={window.region.x + area.x}
                    y={window.region.y + area.y}
                    width={area.width}
                    height={area.height}
                    strokeWidth={1.5 * scale}
                  />
                ))}
                {window.id === selectedId && drawMode === 'window' && (
                  <rect
                    data-resize-handle={window.id}
                    className="roi__handle"
                    x={window.region.x + window.region.width - 7 * scale}
                    y={window.region.y + window.region.height - 7 * scale}
                    width={14 * scale}
                    height={14 * scale}
                  />
                )}
              </g>
            )
          })}

          {drag?.kind === 'create' && (
            <rect
              className={`roi__rect roi__rect--drafting roi__rect--${drawMode}`}
              x={drag.region.x}
              y={drag.region.y}
              width={drag.region.width}
              height={drag.region.height}
              strokeWidth={2 * scale}
            />
          )}
        </svg>
      )}
      </div>
    </div>
  )
}
