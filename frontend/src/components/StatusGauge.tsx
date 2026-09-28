import type { InspectionWindow, WindowResult } from '../api/client'
import './StatusGauge.css'

/**
 * One window's reading as a bar with its limit band marked — the reference's status gauge.
 *
 * The scale is derived from the limits rather than spanning the tool's whole 0-255 range: a
 * contrast band of 14-21 drawn on a 0-255 bar collapses into a sliver at the far left, which tells
 * an operator nothing about how close a reading is to failing. Padding around the band keeps both
 * the band and the needle readable while the printed numbers stay absolute.
 */
const TOOL_MAX = 255

interface StatusGaugeProps {
  window: InspectionWindow
  result: WindowResult | null
  isSelected: boolean
  onSelect: () => void
}

export function StatusGauge({ window, result, isSelected, onSelect }: StatusGaugeProps) {
  const { min: scaleMin, max: scaleMax } = scaleFor(window.lowerLimit, window.upperLimit, result?.value)
  const percent = (value: number) =>
    `${Math.max(0, Math.min(100, ((value - scaleMin) / (scaleMax - scaleMin)) * 100))}%`
  const width = (from: number, to: number) =>
    `${Math.max(0, Math.min(100, ((to - from) / (scaleMax - scaleMin)) * 100))}%`
  const state = result ? (result.passed ? 'ok' : 'ng') : 'idle'

  return (
    <button
      type="button"
      className={`gauge gauge--${state} ${isSelected ? 'is-selected' : ''}`}
      onClick={onSelect}
    >
      <span className="gauge__name">{window.name}</span>
      <span className="gauge__tool">
        OCR
        {result?.text ? ` · "${result.text}"` : ''}
      </span>

      <span className="gauge__track">
        <span
          className="gauge__band"
          style={{
            left: percent(window.lowerLimit),
            width: width(window.lowerLimit, window.upperLimit),
          }}
        />
        {result && <span className="gauge__needle" style={{ left: percent(result.value) }} />}
      </span>

      <span className="gauge__limits">
        <span>{round(scaleMin)}</span>
        <span className="gauge__limit-values">
          {window.lowerLimit} – {window.upperLimit}
        </span>
        <span>{round(scaleMax)}</span>
      </span>

      <span className="gauge__value">
        {result ? result.value.toFixed(1) : '—'}
        <span className="gauge__chip">{result ? (result.passed ? 'OK' : 'NG') : '·'}</span>
      </span>
    </button>
  )
}

/** Band plus half a band of air on each side, widened if the reading falls outside it. */
function scaleFor(lower: number, upper: number, value: number | undefined): { min: number; max: number } {
  const span = Math.max(upper - lower, 1)
  let min = lower - span / 2
  let max = upper + span / 2

  if (value !== undefined) {
    min = Math.min(min, value - span / 4)
    max = Math.max(max, value + span / 4)
  }

  return { min: Math.max(0, min), max: Math.min(TOOL_MAX, max) }
}

function round(value: number): string {
  return Number.isInteger(value) ? value.toString() : value.toFixed(1)
}
