/**
 * Empty means same-origin, which is the case for the built console the backend serves. The Vite
 * dev server sets VITE_API_BASE_URL instead, because there the page and the API are on different
 * ports.
 */
export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''
export const WS_BASE_URL = API_BASE_URL
  ? API_BASE_URL.replace(/^http/, 'ws')
  : `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}`

export interface HealthResponse {
  status: string
  service: string
  timeUtc: string
}

export async function getHealth(): Promise<HealthResponse> {
  const response = await fetch(`${API_BASE_URL}/health`)
  if (!response.ok) {
    throw new Error(`Backend returned ${response.status}`)
  }
  return response.json()
}

export interface NetworkInterfaceInfo {
  name: string
  description: string
  ip: string
  prefixLength: number
  isLoopback: boolean
}

export async function getNetworkInterfaces(): Promise<NetworkInterfaceInfo[]> {
  const response = await fetch(`${API_BASE_URL}/api/camera/network-interfaces`)
  if (!response.ok) {
    throw new Error(`Backend returned ${response.status}`)
  }
  return response.json()
}

export interface CameraInformation {
  ip: string
  mac: string
  manufacturerName: string
  model: string
  deviceVersion: string
  serialNumber: string
  userDefinedName: string
  /** Local interface that answered for this camera — the one that can stream from it. */
  localIp: string
}

async function parseErrorDetail(response: Response): Promise<string> {
  try {
    const body = await response.json()
    return body.detail ?? body.title ?? `HTTP ${response.status}`
  } catch {
    return `HTTP ${response.status}`
  }
}

/** Omit localIp to scan every interface; each camera comes back tagged with the one that reaches it. */
export async function discoverCameras(localIp?: string): Promise<CameraInformation[]> {
  const query = localIp ? `?localIp=${encodeURIComponent(localIp)}` : ''
  const response = await fetch(`${API_BASE_URL}/api/camera/discover${query}`)
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

export type CameraBackendId = 'Gige' | 'Zebra'

export interface CameraBackendOption {
  id: CameraBackendId
  name: string
  description: string
}

export interface CameraBackendInfo {
  current: CameraBackendId
  backends: CameraBackendOption[]
}

/** Which kind of camera the server talks to, and the kinds it can talk to. */
export async function getCameraBackend(): Promise<CameraBackendInfo> {
  const response = await fetch(`${API_BASE_URL}/api/camera/backend`)
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

/** Switching disconnects the current camera; the server refuses while an inspection is running. */
export async function setCameraBackend(backend: CameraBackendId): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/api/camera/backend`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ backend }),
  })
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
}

export async function connectCamera(cameraIp: string, localIp: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/api/camera/connect`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cameraIp, localIp }),
  })
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
}

export async function disconnectCamera(): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/api/camera/disconnect`, { method: 'POST' })
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
}

/** Reception counters from the GVSP receiver, present only while connected. */
export interface CameraStatistics {
  width: number
  height: number
  pixelFormat: string
  framesCompleted: number
  framesIncomplete: number
  /** Blocks the camera opened and then discarded — it does this when the link cannot carry the configured frame rate. */
  blocksAbortedByCamera: number
  fps: number
}

export interface CameraStatus {
  connected: boolean
  cameraIp: string | null
  statistics: CameraStatistics | null
  /** Present while connected, so a reloaded client can restore its display without reconnecting. */
  device: CameraIdentity | null
}

/** Limits the camera itself declares for a feature; a value outside them is clamped on apply. */
export interface FeatureRange {
  min: number | null
  max: number | null
  increment: number | null
}

export interface CameraSettings {
  exposureTimeUs: number | null
  gain: number | null
  frameRateHz: number | null
  width: number | null
  height: number | null
  offsetX: number | null
  offsetY: number | null
  exposureRange: FeatureRange | null
  gainRange: FeatureRange | null
  frameRateRange: FeatureRange | null
  blackLevel: number | null
  blackLevelRange: FeatureRange | null
  /** "On" while the camera waits for a trigger, "Off" while it free-runs. */
  triggerMode: string | null
  /** GenICam trigger source name, e.g. "Software" or "Line1". */
  triggerSource: string | null
  /** Which edge or level of a hardware trigger line starts a frame. */
  triggerActivation: string | null
  triggerDelayUs: number | null
  triggerDelayRange: FeatureRange | null
  /** The camera's own brightness regulation: "Active" or "Off". */
  autoBrightness: string | null
  autoBrightnessTarget: number | null
  autoBrightnessTargetRange: FeatureRange | null
  reverseX: boolean
  reverseY: boolean
  deviceTemperatureC: number | null
}

export interface CameraSettingsUpdate {
  exposureTimeUs?: number
  gain?: number
  blackLevel?: number
  frameRateHz?: number
  triggerMode?: 'On' | 'Off'
  triggerSource?: string
  triggerActivation?: string
  triggerDelayUs?: number
  autoBrightness?: 'Active' | 'Off'
  autoBrightnessTarget?: number
  reverseX?: boolean
  reverseY?: boolean
}

export async function getCameraSettings(): Promise<CameraSettings> {
  const response = await fetch(`${API_BASE_URL}/api/camera/settings`)
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

export async function updateCameraSettings(update: CameraSettingsUpdate): Promise<CameraSettings> {
  const response = await fetch(`${API_BASE_URL}/api/camera/settings`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(update),
  })
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

export async function getCameraStatus(): Promise<CameraStatus> {
  const response = await fetch(`${API_BASE_URL}/api/camera/status`)
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

// ---------------------------------------------------------------------------
// Recipes and inspection
// ---------------------------------------------------------------------------

/** Region of interest in sensor pixels, origin at the frame's top-left. */
export interface Roi {
  x: number
  y: number
  width: number
  height: number
}

export type InspectionToolKind = 'Ocr'

/** One rectangle plus the measurement taken in it and the limits it must fall between. */
/** One measured reading from a part the operator labelled good or bad. */
export interface ToolSample {
  id: string
  capturedUtc: string
  value: number
  isGood: boolean
}

export interface InspectionWindow {
  id: string
  name: string
  region: Roi
  tool: InspectionToolKind
  /** Rectangles inside the window that take no part in the measurement, in window coordinates. */
  mask: Roi[]
  /** Whether this window's verdict counts towards the overall OK/NG. */
  affectsTotal: boolean
  /** Whether this window moves with the position adjustment. */
  followsPosition: boolean
  /** What the OCR tool should find in this window; ignored by the other tools. */
  expectedText: string | null
  /** Treats Latin diacritics as their plain letters when comparing OCR output. */
  ignoreDiacritics: boolean
  /** Which OCR language pack reads this window; null means the default (Latin scripts). */
  ocrLanguage: string | null
  lowerLimit: number
  upperLimit: number
  /** Readings taken from judged parts; the evidence a limit was derived from. */
  samples: ToolSample[]
}

/** What is known about the registered master image; the pixels are fetched by URL. */
export interface MasterImage {
  capturedUtc: string
  width: number
  height: number
  source: string
}

/** Finds a taught pattern each frame and moves the windows by however far the part has shifted. */
export interface PositionAdjustment {
  enabled: boolean
  /** The patch of the master image to look for. */
  pattern: Roi | null
  /** How far either side of the taught position to search, in pixels. */
  searchMargin: number
  /** Match strength below which the part counts as not found, 0-100. */
  minScore: number
}

export type TotalJudgment = 'All' | 'Any'

export interface OutputAssignment {
  logic: TotalJudgment
}

export interface RecipeCameraSettings {
  exposureTimeUs: number | null
  gain: number | null
  frameRateHz: number | null
}

export interface Recipe {
  id: string
  name: string
  createdUtc: string
  updatedUtc: string
  camera: RecipeCameraSettings
  windows: InspectionWindow[]
  /** The registered picture of a good part, or null if none has been taken. */
  master: MasterImage | null
  position: PositionAdjustment
  output: OutputAssignment
}

/** What a caller may set; id, timestamps and the master belong to the store. */
export interface RecipeDraft {
  name: string
  camera: RecipeCameraSettings
  windows: InspectionWindow[]
  position: PositionAdjustment
  output: OutputAssignment
}

export interface WindowResult {
  windowId: string
  name: string
  tool: InspectionToolKind
  region: Roi
  value: number
  lowerLimit: number
  upperLimit: number
  passed: boolean
  /** Whether this window's verdict counted towards the overall result. */
  affectsTotal: boolean
  /** What the OCR tool read; null for the other tools. */
  text: string | null
  message: string | null
}

export interface InspectionResult {
  recipeId: string
  recipeName: string
  timestampUtc: string
  frameTimestampUtc: string
  frameWidth: number
  frameHeight: number
  passed: boolean
  windows: WindowResult[]
  /** Whether the windows were moved to follow the part on this frame. */
  positionApplied: boolean
  positionOffsetX: number
  positionOffsetY: number
  /** How strongly the taught pattern matched, 0-100. */
  positionScore: number
  /** Why the result is what it is, when something outside the windows decided it. */
  message: string | null
}

export interface WindowMeasurement {
  region: Roi
  tool: InspectionToolKind
  value: number
  brightness: number
  contrast: number
  /** What the OCR tool read, so the operator can see it before setting a limit on it. */
  text: string | null
  note: string | null
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(await parseErrorDetail(response))
  }
  return response.json()
}

export async function listRecipes(): Promise<Recipe[]> {
  return json(await fetch(`${API_BASE_URL}/api/recipes`))
}

export async function createRecipe(draft: RecipeDraft): Promise<Recipe> {
  return json(
    await fetch(`${API_BASE_URL}/api/recipes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(draft),
    }),
  )
}

export async function updateRecipe(id: string, draft: RecipeDraft): Promise<Recipe> {
  return json(
    await fetch(`${API_BASE_URL}/api/recipes/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(draft),
    }),
  )
}

export async function deleteRecipe(id: string): Promise<void> {
  await json(await fetch(`${API_BASE_URL}/api/recipes/${encodeURIComponent(id)}`, { method: 'DELETE' }))
}

/** Pushes the recipe's taught exposure/gain/frame rate back onto the camera. */
export async function applyRecipeToCamera(id: string): Promise<CameraSettings> {
  return json(
    await fetch(`${API_BASE_URL}/api/recipes/${encodeURIComponent(id)}/apply`, { method: 'POST' }),
  )
}

export interface AutoBrightnessStep {
  iteration: number
  brightness: number
  exposureTimeUs: number
  gain: number
}

export interface AutoBrightnessResult {
  target: number
  exposureTimeUsBefore: number | null
  gainBefore: number | null
  exposureTimeUs: number | null
  gain: number | null
  frameRateHz: number | null
  measuredBrightness: number | null
  reached: boolean
  message: string | null
  steps: AutoBrightnessStep[]
}

/** Adjusts exposure and gain until the image reaches a target grey level, keeping a frame-rate floor. */
export async function autoAdjustBrightness(target: number, minFrameRateHz: number): Promise<AutoBrightnessResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/camera/auto-brightness`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ target, minFrameRateHz }),
    }),
  )
}

export interface FocusScore {
  region: Roi
  /** Rises as edges sharpen. Comparable only against itself, on one scene. */
  sharpness: number
  brightness: number
  contrast: number
}

export async function measureFocus(region: Roi | null): Promise<FocusScore> {
  return json(
    await fetch(`${API_BASE_URL}/api/camera/focus`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ region }),
    }),
  )
}

export interface CameraIdentity {
  /** Free-text name the operator can set, up to 15 characters. */
  userId: string | null
  manufacturerName: string | null
  modelName: string | null
  serialNumber: string | null
  deviceVersion: string | null
  /** "PersistentIP", "DHCP" or "LLA" — how the camera obtains its address at boot. */
  ipConfiguration: string | null
  persistentIp: string | null
  persistentSubnetMask: string | null
  persistentGateway: string | null
  currentIp: string | null
  mac: string | null
}

export interface CameraIdentityUpdate {
  userId?: string
  ipConfiguration?: string
  persistentIp?: string
  persistentSubnetMask?: string
  persistentGateway?: string
}

export interface CameraIdentityResult {
  identity: CameraIdentity
  /** Address changes only take hold at the camera's next power cycle. */
  rebootRequired: boolean
  message: string | null
}

export async function getCameraIdentity(): Promise<CameraIdentity> {
  return json(await fetch(`${API_BASE_URL}/api/camera/identity`))
}

export async function updateCameraIdentity(update: CameraIdentityUpdate): Promise<CameraIdentityResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/camera/identity`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(update),
    }),
  )
}

/** Writes the current settings into the camera's own non-volatile user set. */
export async function saveSettingsToCamera(): Promise<void> {
  await json(await fetch(`${API_BASE_URL}/api/camera/save-to-camera`, { method: 'POST' }))
}

export const SNAPSHOT_URL = `${API_BASE_URL}/api/camera/snapshot`

export interface TriggeredFrame {
  width: number
  height: number
  pixelFormat: string
  timestampUtc: string
}

/** Fires a software trigger and reports the frame it produced, without judging it. */
export async function triggerFrame(): Promise<TriggeredFrame> {
  return json(await fetch(`${API_BASE_URL}/api/camera/trigger`, { method: 'POST' }))
}

/** Fires a software trigger and judges that exact frame — the production flow. */
export async function triggerRunInspection(recipeId: string): Promise<InspectionResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/trigger-run/${encodeURIComponent(recipeId)}`, {
      method: 'POST',
    }),
  )
}

export async function runInspection(recipeId: string): Promise<InspectionResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/run/${encodeURIComponent(recipeId)}`, { method: 'POST' }),
  )
}

/** Measures a region on the newest frame without judging it — used to set limits from a good part. */
export async function measureRegion(
  region: Roi,
  tool: InspectionToolKind,
  expectedText?: string | null,
  ignoreDiacritics = false,
  ocrLanguage?: string | null,
  mask: Roi[] = [],
): Promise<WindowMeasurement> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/measure`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ region, tool, expectedText, ignoreDiacritics, ocrLanguage, mask }),
    }),
  )
}

/**
 * Registers the newest live frame as the recipe's master image — the picture of a good part that
 * position adjustment measures against.
 */
export async function registerMasterFromLive(recipeId: string): Promise<Recipe> {
  return json(
    await fetch(`${API_BASE_URL}/api/recipes/${encodeURIComponent(recipeId)}/master`, { method: 'POST' }),
  )
}

/** Registers a stored frame as the master, for a part that has already gone down the line. */
export async function registerMasterFromImage(recipeId: string, imageId: string): Promise<Recipe> {
  return json(
    await fetch(
      `${API_BASE_URL}/api/recipes/${encodeURIComponent(recipeId)}/master/from-image/${encodeURIComponent(imageId)}`,
      { method: 'POST' },
    ),
  )
}

export async function deleteMaster(recipeId: string): Promise<Recipe> {
  return json(
    await fetch(`${API_BASE_URL}/api/recipes/${encodeURIComponent(recipeId)}/master`, { method: 'DELETE' }),
  )
}

/**
 * Where to fetch the master's pixels. Carries the capture time so the browser refetches when a new
 * master is registered instead of showing the one it already has.
 */
export function masterImageUrl(recipeId: string, capturedUtc: string): string {
  return `${API_BASE_URL}/api/recipes/${encodeURIComponent(recipeId)}/master?v=${encodeURIComponent(capturedUtc)}`
}

/** One installed recognition model and the scripts it can read. */
export interface OcrLanguage {
  id: string
  name: string
  description: string
  isDefault: boolean
}

export async function listOcrLanguages(): Promise<{ languages: OcrLanguage[]; error: string | null }> {
  return json(await fetch(`${API_BASE_URL}/api/inspection/ocr-languages`))
}

// ---------------------------------------------------------------------------
// Saved image history
// ---------------------------------------------------------------------------

/** Manual: only an explicit save writes. All: every inspection. FailuresOnly: rejects only. */
export type ImageSaveMode = 'Manual' | 'All' | 'FailuresOnly'

export interface ImageHistorySettings {
  directory: string
  /** Oldest images are discarded once this many are stored. */
  capacity: number
  mode: ImageSaveMode
}

export interface SavedImage {
  id: string
  savedAtUtc: string
  width: number
  height: number
  sizeBytes: number
  /** "OK", "NG", or null when saved outside an inspection. */
  verdict: string | null
  recipeId: string | null
  recipeName: string | null
  note: string | null
}

export interface ImageHistory {
  settings: ImageHistorySettings
  images: SavedImage[]
}

export function savedImageUrl(id: string): string {
  return `${API_BASE_URL}/api/images/${encodeURIComponent(id)}`
}

export async function listSavedImages(): Promise<ImageHistory> {
  return json(await fetch(`${API_BASE_URL}/api/images`))
}

export async function updateImageHistorySettings(
  update: Partial<ImageHistorySettings>,
): Promise<ImageHistorySettings> {
  return json(
    await fetch(`${API_BASE_URL}/api/images/settings`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(update),
    }),
  )
}

/** Saves whatever the camera has right now, outside any inspection. */
export async function saveCurrentImage(note?: string): Promise<SavedImage> {
  return json(
    await fetch(`${API_BASE_URL}/api/images`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ note }),
    }),
  )
}

export async function deleteSavedImage(id: string): Promise<void> {
  await json(await fetch(`${API_BASE_URL}/api/images/${encodeURIComponent(id)}`, { method: 'DELETE' }))
}

export async function clearSavedImages(): Promise<void> {
  await json(await fetch(`${API_BASE_URL}/api/images`, { method: 'DELETE' }))
}

/** Re-judges a stored image against a recipe, without the camera being involved. */
export async function inspectSavedImage(id: string, recipeId: string): Promise<InspectionResult> {
  return json(
    await fetch(
      `${API_BASE_URL}/api/images/${encodeURIComponent(id)}/inspect/${encodeURIComponent(recipeId)}`,
      { method: 'POST' },
    ),
  )
}

// ---------------------------------------------------------------------------
// Continuous run — owned by the server, so it survives the browser closing
// ---------------------------------------------------------------------------

export interface RunStatistics {
  total: number
  ok: number
  ng: number
  lastMs: number
  minMs: number
  maxMs: number
  sumMs: number
}

export interface RunState {
  isRunning: boolean
  recipeId: string | null
  recipeName: string | null
  lastResult: InspectionResult | null
  /** The most recent cycle's failure, if the loop is limping rather than stopped. */
  lastError: string | null
  statistics: RunStatistics
}

export async function getRunState(): Promise<RunState> {
  return json(await fetch(`${API_BASE_URL}/api/inspection/run`))
}

export async function startRun(recipeId: string): Promise<RunState> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/run/start/${encodeURIComponent(recipeId)}`, {
      method: 'POST',
    }),
  )
}

export async function stopRun(): Promise<RunState> {
  return json(await fetch(`${API_BASE_URL}/api/inspection/run/stop`, { method: 'POST' }))
}

export async function resetRunStatistics(): Promise<RunState> {
  return json(await fetch(`${API_BASE_URL}/api/inspection/run/reset-statistics`, { method: 'POST' }))
}

/**
 * One inspection through the server's runner, so its result and tally are the same ones a trigger
 * arriving over TCP/IP produces.
 */
export async function runInspectionOnce(recipeId: string): Promise<InspectionResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/run-once/${encodeURIComponent(recipeId)}`, { method: 'POST' }),
  )
}

// ---------------------------------------------------------------------------
// Operating mode (Run / Program), kept on the server so the console reopens as it was left
// ---------------------------------------------------------------------------

export type OperatingMode = 'run' | 'program'

export interface SessionInfo {
  mode: OperatingMode
  activeRecipeId: string | null
}

export async function getSession(): Promise<SessionInfo> {
  return json(await fetch(`${API_BASE_URL}/api/session`))
}

export async function setSessionMode(mode: OperatingMode, recipeId: string | null): Promise<SessionInfo> {
  return json(
    await fetch(`${API_BASE_URL}/api/session/mode`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode, recipeId }),
    }),
  )
}

// ---------------------------------------------------------------------------
// TCP/IP interface for a PLC or robot
// ---------------------------------------------------------------------------

export type ExternalSignalId = 'Ready' | 'Busy' | 'Run' | 'Error' | 'AllOK' | 'AllNok' | 'ResultAvailable'

export type ExternalTerminator = 'CRLF' | 'LF' | 'CR' | 'None'

export interface ExternalIoSettings {
  enabled: boolean
  port: number
  terminator: ExternalTerminator
  triggerMessage: string
  readyMessage: string
  busyMessage: string
  runMessage: string
  errorMessage: string
  allOkMessage: string
  allNokMessage: string
  resultAvailableMessage: string
}

export interface ExternalSignalStatus {
  signal: ExternalSignalId
  /** How many times it has been sent since the server started. */
  count: number
  /** Milliseconds since it was last sent, or null if it never has been. */
  ageMs: number | null
}

export interface ExternalIoStatus {
  listening: boolean
  port: number
  clients: string[]
  error: string | null
  lastTriggerUtc: string | null
  signals: ExternalSignalStatus[]
  /** What is true right now, as opposed to the events in `signals`. */
  state: { run: boolean; busy: boolean; ready: boolean }
}

export interface ExternalIoInfo {
  settings: ExternalIoSettings
  status: ExternalIoStatus
}

export async function getExternalIo(): Promise<ExternalIoInfo> {
  return json(await fetch(`${API_BASE_URL}/api/external-io`))
}

export async function saveExternalIo(settings: ExternalIoSettings): Promise<ExternalIoInfo> {
  return json(
    await fetch(`${API_BASE_URL}/api/external-io`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settings),
    }),
  )
}

/** Sends one signal's message to the connected controllers; resolves to how many received it. */
export async function testExternalSignal(signal: ExternalSignalId): Promise<number> {
  const result = await json<{ sentTo: number }>(
    await fetch(`${API_BASE_URL}/api/external-io/test/${signal}`, { method: 'POST' }),
  )
  return result.sentTo
}

export type ExternalLogDirection = 'Rx' | 'Tx' | 'Info' | 'Error'

export interface ExternalLogEntry {
  seq: number
  timeUtc: string
  direction: ExternalLogDirection
  remote: string | null
  text: string
  signal: ExternalSignalId | null
}

export interface ExternalLogPage {
  entries: ExternalLogEntry[]
  /** Sequence number of the newest entry; pass it back as `after` to get only what is newer. */
  last: number
}

export async function getExternalLog(after: number): Promise<ExternalLogPage> {
  return json(await fetch(`${API_BASE_URL}/api/external-io/log?after=${after}`))
}

/** Sends text to the connected controllers as typed; resolves to how many received it. */
export async function sendExternalText(text: string): Promise<number> {
  const result = await json<{ sentTo: number }>(
    await fetch(`${API_BASE_URL}/api/external-io/send`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text }),
    }),
  )
  return result.sentTo
}

// ---------------------------------------------------------------------------
// Teaching limits from OK / NG samples
// ---------------------------------------------------------------------------

export interface SampleReading {
  windowId: string
  value: number
}

export interface SampleResult {
  capturedUtc: string
  isGood: boolean
  readings: SampleReading[]
}

/**
 * Measures every window on one frame. All windows must read the same picture, so teaching a part
 * is a single call rather than a loop of separate measurements.
 */
export async function sampleWindows(windows: InspectionWindow[], isGood: boolean): Promise<SampleResult> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/sample`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        windows: windows.map((w) => ({
          id: w.id,
          region: w.region,
          tool: w.tool,
          expectedText: w.expectedText,
          ocrLanguage: w.ocrLanguage,
        })),
        isGood,
      }),
    }),
  )
}

export interface AutoLimits {
  lowerLimit: number
  upperLimit: number
  /** False when the samples cannot be separated by a threshold on this measurement. */
  separable: boolean
  message: string
}

export async function computeAutoLimits(samples: ToolSample[]): Promise<AutoLimits> {
  return json(
    await fetch(`${API_BASE_URL}/api/inspection/auto-limits`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ samples }),
    }),
  )
}
