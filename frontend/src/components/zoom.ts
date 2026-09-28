/** The magnifications the reference console offers; "fit" letterboxes the whole frame instead. */
export const ZOOM_STEPS = [1, 1.25, 1.5, 2, 3, 5] as const

export type Zoom = number | 'fit'

/** Moves one step along ZOOM_STEPS, treating "fit" as 100 %. */
export function stepZoom(current: Zoom, direction: 1 | -1): Zoom {
  const magnification = current === 'fit' ? 1 : current
  const index = ZOOM_STEPS.indexOf(magnification as (typeof ZOOM_STEPS)[number])
  const next = Math.max(0, Math.min(ZOOM_STEPS.length - 1, (index < 0 ? 0 : index) + direction))
  return ZOOM_STEPS[next]
}
