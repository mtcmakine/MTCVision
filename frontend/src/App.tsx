import { useEffect, useState } from 'react'
import {
  applyRecipeToCamera,
  createRecipe,
  deleteMaster,
  deleteRecipe,
  getCameraSettings,
  getCameraStatus,
  getHealth,
  listRecipes,
  getRunState,
  getSession,
  computeAutoLimits,
  listOcrLanguages,
  measureRegion,
  registerMasterFromLive,
  resetRunStatistics,
  sampleWindows,
  runInspectionOnce,
  setSessionMode,
  startRun,
  stopRun,
  triggerFrame,
  updateRecipe,
  type CameraIdentity,
  type CameraInformation,
  type CameraSettings,
  type CameraStatistics,
  type InspectionResult,
  type InspectionToolKind,
  type InspectionWindow,
  type HealthResponse,
  type MasterImage,
  type OcrLanguage,
  type OutputAssignment,
  type PositionAdjustment,
  type Recipe,
  type Roi,
  type RunStatistics,
  type WindowMeasurement,
} from './api/client'
import { useLiveStream } from './hooks/useLiveStream'
import { AboutDialog } from './components/AboutDialog'
import { ConfirmDialog } from './components/ConfirmDialog'
import { CameraDialog } from './components/CameraDialog'
import { ExternalIoDialog } from './components/ExternalIoDialog'
import { ImageHistoryDialog } from './components/ImageHistoryDialog'
import { ImageStage } from './components/ImageStage'
import type { DrawMode } from './components/RoiEditor'
import { StartupScreen } from './components/StartupScreen'
import type { Zoom } from './components/zoom'
import { ProgramPanel } from './components/ProgramPanel'
import { RunPanel } from './components/RunPanel'
import './components/shell.css'

type Mode = 'run' | 'program'

/** Short tool names, used when naming a newly added window. */
const TOOL_NAMES: Record<InspectionToolKind, string> = {
  Ocr: 'OCR',
}

/**
 * What the chrome needs to name the connected camera. It arrives either from discovery (a fresh
 * connect) or from the server's own identity read (a restored session), so it is normalised here.
 */
interface ConnectedDevice {
  model: string
  serialNumber: string
  deviceVersion: string
  ip: string
  mac: string
  localIp: string | null
  manufacturerName: string
  userDefinedName: string
}

function toConnectedDevice(
  source: CameraInformation | CameraIdentity | null,
  fallbackIp: string | null = null,
): ConnectedDevice | null {
  if (!source) {
    return fallbackIp
      ? {
          model: 'Kamera',
          serialNumber: '—',
          deviceVersion: '—',
          ip: fallbackIp,
          mac: '—',
          localIp: null,
          manufacturerName: '—',
          userDefinedName: '—',
        }
      : null
  }

  if ('localIp' in source) {
    return { ...source }
  }

  return {
    model: source.modelName ?? 'Kamera',
    serialNumber: source.serialNumber ?? '—',
    deviceVersion: source.deviceVersion ?? '—',
    ip: source.currentIp ?? fallbackIp ?? '—',
    mac: source.mac ?? '—',
    localIp: null,
    manufacturerName: source.manufacturerName ?? '—',
    userDefinedName: source.userId ?? '—',
  }
}

/** How often the camera's reception counters are refreshed while it is connected. */
const STATISTICS_POLL_MS = 1000

/** How often the server-side run is polled for its latest verdict and counters. */
const RUN_POLL_MS = 500

const EMPTY_RUN_STATISTICS: RunStatistics = {
  total: 0,
  ok: 0,
  ng: 0,
  lastMs: 0,
  minMs: 0,
  maxMs: 0,
  sumMs: 0,
}

/** The reference console's internal-trigger interval range. */
const MIN_TRIGGER_INTERVAL_MS = 1
const MAX_TRIGGER_INTERVAL_MS = 10000

/** A new recipe does not follow the part until someone teaches it a pattern to follow. */
const DEFAULT_POSITION: PositionAdjustment = {
  enabled: false,
  pattern: null,
  searchMargin: 80,
  minScore: 60,
}

/** Every counted window has to pass: the normal shape of an inspection. */
const DEFAULT_OUTPUT: OutputAssignment = { logic: 'All' }

function App() {
  const [health, setHealth] = useState<HealthResponse | null>(null)
  const [healthError, setHealthError] = useState<string | null>(null)
  const [showAbout, setShowAbout] = useState(false)
  const [mode, setMode] = useState<Mode>('program')

  // The console opens on the connection screen, the way the reference tool's activation menu does.
  const [hasStarted, setHasStarted] = useState(false)
  const [device, setDevice] = useState<ConnectedDevice | null>(null)
  const [cameraSettings, setCameraSettings] = useState<CameraSettings | null>(null)
  const [statistics, setStatistics] = useState<CameraStatistics | null>(null)
  const [showCameraDialog, setShowCameraDialog] = useState(false)
  const [showImageHistory, setShowImageHistory] = useState(false)
  const [showExternalIo, setShowExternalIo] = useState(false)

  const [recipes, setRecipes] = useState<Recipe[]>([])
  const [recipeId, setRecipeId] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [windows, setWindows] = useState<InspectionWindow[]>([])
  const [selectedWindowId, setSelectedWindowId] = useState<string | null>(null)
  const [master, setMaster] = useState<MasterImage | null>(null)
  const [position, setPosition] = useState<PositionAdjustment>(DEFAULT_POSITION)
  const [output, setOutput] = useState<OutputAssignment>(DEFAULT_OUTPUT)
  const [drawMode, setDrawMode] = useState<DrawMode>('window')
  const [isDirty, setIsDirty] = useState(false)

  const [result, setResult] = useState<InspectionResult | null>(null)
  const [runStats, setRunStats] = useState<RunStatistics>(EMPTY_RUN_STATISTICS)
  const [isRunning, setIsRunning] = useState(false)

  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [trialReading, setTrialReading] = useState<WindowMeasurement | null>(null)

  const [pendingDelete, setPendingDelete] = useState<Recipe | null>(null)
  const [ocrLanguages, setOcrLanguages] = useState<OcrLanguage[]>([])

  const [zoom, setZoom] = useState<Zoom>('fit')
  const [showWindows, setShowWindows] = useState(true)
  const [triggerIntervalMs, setTriggerIntervalMs] = useState(1000)

  const isConnected = device !== null
  const isTriggered = cameraSettings?.triggerMode === 'On'

  // A message on the TCP/IP interface is what triggers the camera, so the console must not also
  // fire it on a timer.
  const isTcpTrigger = cameraSettings?.triggerSource === 'TCP/IP'
  const { imageUrl } = useLiveStream(isConnected)
  const frameWidth = statistics?.width ?? 0
  const frameHeight = statistics?.height ?? 0

  useEffect(() => {
    getHealth()
      .then(setHealth)
      .catch((err: unknown) => setHealthError(err instanceof Error ? err.message : 'Bağlanamadı'))
  }, [])

  // Rejoin whatever the server is already doing: the camera may still be connected and a run may
  // still be going from before this browser was opened.
  useEffect(() => {
    if (!health) {
      return
    }

    let cancelled = false
    const restore = async () => {
      const status = await getCameraStatus().catch(() => null)
      if (cancelled || !status?.connected) {
        return
      }

      setDevice(toConnectedDevice(status.device, status.cameraIp))
      setStatistics(status.statistics)
      setCameraSettings(await getCameraSettings().catch(() => null))
      setHasStarted(true)

      if (!cancelled) {
        await restoreWorkspace()
      }
    }

    void restore()
    return () => {
      cancelled = true
    }
  }, [health])

  // Which scripts this install can read. Fetched once: adding a pack is a deployment step, not
  // something that changes while the console is open.
  useEffect(() => {
    listOcrLanguages()
      .then((result) => setOcrLanguages(result.languages))
      .catch(() => setOcrLanguages([]))
  }, [])

  useEffect(() => {
    listRecipes()
      .then(setRecipes)
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Reçeteler alınamadı'))
  }, [])

  // Polled here rather than in a panel: the frame size the overlay draws against and the counters
  // on the status bar have to describe the same stream.
  useEffect(() => {
    if (!isConnected) {
      return
    }

    const refresh = () =>
      getCameraStatus()
        .then((status) => setStatistics(status.statistics))
        .catch(() => setStatistics(null))

    void refresh()
    const timer = setInterval(refresh, STATISTICS_POLL_MS)
    return () => clearInterval(timer)
  }, [isConnected])

  const handleManualTrigger = () => void withBusy('grab', async () => void (await triggerFrame()))

  // Internal trigger, Program mode only: keeps a live picture to teach against while the camera is
  // otherwise idle waiting for a trigger. In Run mode the server-side loop does its own triggering.
  useEffect(() => {
    if (!isConnected || !isTriggered || isTcpTrigger || mode !== 'program' || triggerIntervalMs <= 0) {
      return
    }

    let cancelled = false
    const timer = setInterval(() => {
      if (!cancelled) {
        void triggerFrame().catch(() => undefined)
      }
    }, triggerIntervalMs)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [isConnected, isTriggered, isTcpTrigger, mode, triggerIntervalMs])

  // Goes through the server's runner, like a trigger arriving over TCP/IP does, so both leave the
  // same result and the same tally behind and every screen watching the run sees them.
  const inspectOnce = () =>
    withBusy('once', async () => {
      if (!recipeId) {
        return
      }

      setResult(await runInspectionOnce(recipeId))
      setRunStats((await getRunState()).statistics)
    })

  // The run itself lives on the server; this only mirrors it. Closing the browser leaves the line
  // running, and reopening it picks the display back up where the server got to. In Run mode it
  // is mirrored even between runs, since inspections also start from outside (a PLC's trigger).
  const isWatchingRun = isRunning || (mode === 'run' && isConnected)
  useEffect(() => {
    if (!isWatchingRun) {
      return
    }

    const poll = () =>
      getRunState()
        .then((state) => {
          setResult(state.lastResult)
          setRunStats(state.statistics)
          if (!state.isRunning) {
            setIsRunning(false)
          }

          setError(state.lastError)
        })
        .catch(() => undefined)

    void poll()
    const timer = setInterval(poll, RUN_POLL_MS)
    return () => clearInterval(timer)
  }, [isWatchingRun])

  const withBusy = async (label: string, action: () => Promise<void>) => {
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

  /** `remember` writes the choice to the server, so the console reopens on this recipe. */
  const loadRecipe = (recipe: Recipe | null, remember = true) => {
    if (remember) {
      void setSessionMode(mode, recipe?.id ?? null).catch(() => undefined)
    }

    setRecipeId(recipe?.id ?? null)
    setName(recipe?.name ?? '')
    setWindows(recipe?.windows ?? [])
    setMaster(recipe?.master ?? null)
    setPosition(recipe?.position ?? DEFAULT_POSITION)
    setOutput(recipe?.output ?? DEFAULT_OUTPUT)
    setDrawMode('window')
    setSelectedWindowId(null)
    setResult(null)
    setRunStats(EMPTY_RUN_STATISTICS)
    setIsDirty(false)
    setIsRunning(false)
  }

  const saveRecipe = async () => {
    const camera = await getCameraSettings().catch(() => null)
    const draft = {
      name: name.trim() || 'Adsız Reçete',
      camera: {
        exposureTimeUs: camera?.exposureTimeUs ?? null,
        gain: camera?.gain ?? null,
        frameRateHz: camera?.frameRateHz ?? null,
      },
      windows,
      position,
      output,
    }

    const saved = recipeId ? await updateRecipe(recipeId, draft) : await createRecipe(draft)
    setRecipes(await listRecipes())
    setRecipeId(saved.id)
    setName(saved.name)
    setMaster(saved.master)
    setIsDirty(false)
    return saved.id
  }

  /** For the buttons that only save: `withBusy` wants an action that resolves to nothing. */
  const saveRecipeOnly = async () => {
    await saveRecipe()
  }

  /**
   * Puts the console back the way it was left: the recipe it was on and the mode it was in. A run
   * still going on the server wins over the remembered mode, since the line is running.
   */
  const restoreWorkspace = async () => {
    const [session, run, stored] = await Promise.all([
      getSession().catch(() => null),
      getRunState().catch(() => null),
      listRecipes().catch(() => [] as Recipe[]),
    ])

    setRecipes(stored)
    const running = run?.isRunning && run.recipeId ? run : null
    const active = stored.find((r) => r.id === (running?.recipeId ?? session?.activeRecipeId))
    if (active) {
      loadRecipe(active, false)
    }

    if (running && active) {
      setMode('run')
      setResult(running.lastResult)
      setRunStats(running.statistics)
      setIsRunning(true)
    } else if (session?.mode === 'run' && active) {
      setMode('run')
      if (run) {
        setResult(run.lastResult)
        setRunStats(run.statistics)
      }
    } else {
      setMode('program')

      // The recipe it was left on is gone, so the remembered Run mode cannot be honoured; the
      // server must not go on believing it is armed.
      if (session?.mode === 'run') {
        void setSessionMode('program', active?.id ?? null).catch(() => undefined)
      }
    }
  }

  const handleSwitchMode = (next: Mode) =>
    withBusy('mode', async () => {
      // Leaving Program commits the setting, the way the reference console does — the alternative
      // is silently running an inspection against a recipe that is not what is on screen.
      let activeId = recipeId
      if (next === 'run' && isDirty) {
        activeId = await saveRecipe()
      }

      // Remembered on the server: the console reopens in the mode it was left in, and a trigger
      // from outside is only honoured in Run mode.
      await setSessionMode(next, activeId)

      setIsRunning(false)
      setResult(null)
      setRunStats(EMPTY_RUN_STATISTICS)
      setMode(next)
    })

  const patchWindow = (id: string, patch: Partial<InspectionWindow>) => {
    setWindows((current) => current.map((w) => (w.id === id ? { ...w, ...patch } : w)))
    setIsDirty(true)
  }

  /** Starts a fresh, unsaved recipe. Named up front so it can be saved without a detour. */
  const handleNewRecipe = () => {
    loadRecipe(null)
    setName(`Reçete ${recipes.length + 1}`)
    setIsDirty(true)
  }

  /**
   * A drag on the image means different things depending on what is being placed. Routing it here,
   * rather than in the editor, keeps the editor about geometry and the app about what geometry is
   * for.
   */
  const handleDrawRegion = (region: Roi) => {
    if (drawMode === 'pattern') {
      setPosition((current) => ({ ...current, pattern: region, enabled: true }))
      setDrawMode('window')
      setIsDirty(true)
      return
    }

    if (drawMode === 'mask') {
      const window = windows.find((w) => w.id === selectedWindowId)
      if (!window) {
        setError('Maske çizmek için önce bir araç seçin.')
        return
      }

      // Masks are stored relative to their window so they travel with it — both when the operator
      // drags the window and when position adjustment moves it to follow the part.
      const x = Math.max(0, region.x - window.region.x)
      const y = Math.max(0, region.y - window.region.y)
      const width = Math.min(region.width - (x - (region.x - window.region.x)), window.region.width - x)
      const height = Math.min(region.height - (y - (region.y - window.region.y)), window.region.height - y)

      if (width < 2 || height < 2) {
        setError('Maske seçili aracın penceresi içinde çizilmeli.')
        return
      }

      patchWindow(window.id, { mask: [...window.mask, { x, y, width, height }] })
      return
    }

    handleCreateWindow(region)
  }

  const handleRegisterMaster = () =>
    withBusy('master', async () => {
      if (!recipeId) {
        setError('Master görüntü kaydetmek için önce reçeteyi kaydedin.')
        return
      }

      const saved = await registerMasterFromLive(recipeId)
      setMaster(saved.master)
      setRecipes(await listRecipes())
    })

  const handleDeleteMaster = () =>
    withBusy('master', async () => {
      if (!recipeId) {
        return
      }

      const saved = await deleteMaster(recipeId)
      setMaster(saved.master)
      setRecipes(await listRecipes())
    })

  const handleCreateWindow = (region: Roi, tool: InspectionToolKind = 'Ocr') => {
    setTrialReading(null)
    const id = `w${Date.now().toString(36)}`
    setWindows((current) => [
      ...current,
      {
        id,
        name: `${TOOL_NAMES[tool]} ${current.length + 1}`,
        region,
        tool,
        expectedText: null,
        ignoreDiacritics: false,
        ocrLanguage: null,
        mask: [],
        affectsTotal: true,
        followsPosition: true,
        // Wide open until taught from a good part; a fresh window should not fail just for existing.
        // The OCR reading is a 0–100 match percentage.
        lowerLimit: 0,
        upperLimit: 100,
        samples: [],
      },
    ])
    setSelectedWindowId(id)
    setIsDirty(true)
  }

  /**
   * Adds a tool without dragging, over the middle of the frame. Dragging a rectangle is precise but
   * assumes the operator already knows where the feature is; this gets a tool on screen first and
   * lets them move it afterwards.
   */
  const handleAddTool = (tool: InspectionToolKind) => {
    if (frameWidth === 0 || frameHeight === 0) {
      setError('Araç eklemek için önce kameradan görüntü gelmeli.')
      return
    }

    const width = Math.round(frameWidth * 0.25)
    const height = Math.round(frameHeight * 0.25)
    handleCreateWindow(
      {
        x: Math.round((frameWidth - width) / 2),
        y: Math.round((frameHeight - height) / 2),
        width,
        height,
      },
      tool,
    )
  }

  /**
   * Records one reading per window from a single frame, labelled by the operator. Every window has
   * to read the same picture, so this is one call rather than a loop of measurements.
   */
  const handleTeachSample = (isGood: boolean) =>
    withBusy(isGood ? 'teach-ok' : 'teach-ng', async () => {
      if (windows.length === 0) {
        return
      }

      const sample = await sampleWindows(windows, isGood)
      setWindows((current) =>
        current.map((window) => {
          const reading = sample.readings.find((r) => r.windowId === window.id)
          return reading
            ? {
                ...window,
                samples: [
                  ...window.samples,
                  {
                    id: `s${Date.now().toString(36)}${window.id}`,
                    capturedUtc: sample.capturedUtc,
                    value: reading.value,
                    isGood,
                  },
                ],
              }
            : window
        }),
      )
      setIsDirty(true)
    })

  /** Turns each window's samples into limits, naming any window whose samples cannot be separated. */
  const handleComputeLimits = () =>
    withBusy('auto-limits', async () => {
      const notSeparable: string[] = []
      const computed = await Promise.all(
        windows.map(async (window) => {
          if (window.samples.length === 0) {
            return window
          }

          const limits = await computeAutoLimits(window.samples)
          if (!limits.separable) {
            notSeparable.push(`${window.name}: ${limits.message}`)
          }

          return { ...window, lowerLimit: limits.lowerLimit, upperLimit: limits.upperLimit }
        }),
      )

      setWindows(computed)
      setIsDirty(true)
      setError(notSeparable.length > 0 ? notSeparable.join(' · ') : null)
    })

  const handleClearSamples = () => {
    setWindows((current) => current.map((window) => ({ ...window, samples: [] })))
    setIsDirty(true)
  }

  /** Runs the selected window's tool on the current frame and reports what it read, without judging. */
  const handleTryTool = () =>
    withBusy('try', async () => {
      const selected = windows.find((w) => w.id === selectedWindowId)
      if (!selected) {
        return
      }

      const measurement = await measureRegion(
        selected.region,
        selected.tool,
        selected.expectedText,
        selected.ignoreDiacritics,
        selected.ocrLanguage,
        selected.mask,
      )
      setTrialReading(measurement)
    })

  const handleTeachLimits = () =>
    withBusy('teach', async () => {
      const selected = windows.find((w) => w.id === selectedWindowId)
      if (!selected) {
        return
      }

      const measurement = await measureRegion(
        selected.region,
        selected.tool,
        selected.expectedText,
        selected.ignoreDiacritics,
        selected.ocrLanguage,
        selected.mask,
      )
      const tolerance = Math.max(2, Math.abs(measurement.value) * 0.2)
      patchWindow(selected.id, {
        lowerLimit: Math.round((measurement.value - tolerance) * 100) / 100,
        upperLimit: Math.round((measurement.value + tolerance) * 100) / 100,
      })
    })

  const meta = [
    isTriggered ? (isTcpTrigger ? 'Tetiklemeli (TCP/IP)' : 'Tetiklemeli (Software)') : 'Sürekli alım',
    statistics ? `${statistics.fps.toFixed(1)} kare/sn` : null,
    result ? `SÜRE ${Math.round(runStats.lastMs)} ms` : null,
  ]
    .filter(Boolean)
    .join('   ·   ')

  if (!hasStarted) {
    return (
      <StartupScreen
        health={health}
        healthError={healthError}
        onConnected={(connected, connectedSettings) => {
          setDevice(toConnectedDevice(connected))
          setCameraSettings(connectedSettings)
          setHasStarted(true)
          void restoreWorkspace()
        }}
        onSkip={() => setHasStarted(true)}
      />
    )
  }

  return (
    <div className="app">
      <div className="app__menubar">
        <button type="button" onClick={() => setShowCameraDialog(true)} disabled={!isConnected}>
          Kamera Ayarları
        </button>
        <button type="button" onClick={handleNewRecipe} disabled={mode !== 'program'}>
          Yeni Reçete
        </button>
        <button
          type="button"
          onClick={() => void withBusy('save', saveRecipeOnly)}
          disabled={mode !== 'program' || busy !== null}
        >
          Kaydet
        </button>
        <button
          type="button"
          disabled={!recipeId || mode !== 'program'}
          onClick={() => setPendingDelete(recipes.find((r) => r.id === recipeId) ?? null)}
        >
          Reçeteyi Sil
        </button>
        <button type="button" onClick={() => setShowImageHistory(true)}>
          Görüntü Geçmişi
        </button>
        <button type="button" onClick={() => setShowExternalIo(true)}>
          Haberleşme
        </button>
        <button type="button" onClick={() => setShowAbout(true)}>
          Hakkında
        </button>
        <span className={`menu-status ${healthError ? 'is-error' : ''}`}>
          {healthError ? `Sunucu: ${healthError}` : health ? 'Sunucu bağlı' : 'Sunucu kontrol ediliyor…'}
        </span>
      </div>

      <div className="app__connbar">
        <span>
          <span className={`dot ${isConnected ? 'is-on' : ''}`} />
          {isConnected ? `Bağlı: ${device.model} — ${device.ip}` : 'Kamera bağlı değil'}
        </span>
        <button
          type="button"
          className="btn"
          onClick={() => (isConnected ? setShowCameraDialog(true) : setHasStarted(false))}
        >
          {isConnected ? 'Kamera Ayarları' : 'Kamera Bağla'}
        </button>
        {isConnected && (
          <button type="button" className="btn" onClick={() => setHasStarted(false)}>
            Kamerayı Değiştir
          </button>
        )}
      </div>

      <div className="modebar">
        <button
          type="button"
          className={`modebar__mode ${mode === 'run' ? 'is-active' : ''}`}
          onClick={() => void handleSwitchMode('run')}
          // A recipe that has never been saved is still runnable: switching modes saves it first,
          // so all that is actually required is a name to save it under.
          disabled={!isConnected || (!recipeId && !name.trim()) || busy !== null}
        >
          ▶ Çalıştır
        </button>
        <button
          type="button"
          className={`modebar__mode ${mode === 'program' ? 'is-active' : ''}`}
          onClick={() => void handleSwitchMode('program')}
          disabled={busy !== null}
        >
          ⚙ Program
        </button>

        <div className="modebar__group">
          <span className="legend">Reçete</span>
          <select
            className="field"
            style={{ minWidth: 200 }}
            value={recipeId ?? ''}
            disabled={mode === 'run'}
            onChange={(e) => loadRecipe(recipes.find((r) => r.id === e.target.value) ?? null)}
          >
            <option value="">— seçilmedi —</option>
            {recipes.map((recipe) => (
              <option key={recipe.id} value={recipe.id}>
                {recipe.name}
              </option>
            ))}
          </select>
          <input
            className="field"
            style={{ width: 160 }}
            value={name}
            placeholder="Reçete adı"
            disabled={mode === 'run'}
            onChange={(e) => {
              setName(e.target.value)
              setIsDirty(true)
            }}
          />
          <button type="button" className="btn" disabled={mode === 'run'} onClick={handleNewRecipe}>
            Yeni
          </button>
          <button
            type="button"
            className="btn"
            disabled={mode === 'run' || busy !== null || !isDirty}
            onClick={() => void withBusy('save', saveRecipeOnly)}
          >
            {busy === 'save' ? 'Kaydediliyor…' : 'Kaydet'}
          </button>
          <button
            type="button"
            className="btn is-danger"
            disabled={mode === 'run' || !recipeId || busy !== null}
            onClick={() => setPendingDelete(recipes.find((r) => r.id === recipeId) ?? null)}
          >
            Sil
          </button>
          {isDirty && <span style={{ color: 'var(--accent-line)', fontSize: 11 }}>kaydedilmedi</span>}
        </div>

        {isTriggered && !isTcpTrigger && (
          <div className="modebar__group">
            <span className="legend">Dahili tetik</span>
            <input
              className="field"
              style={{ width: 90 }}
              type="number"
              min={MIN_TRIGGER_INTERVAL_MS}
              max={MAX_TRIGGER_INTERVAL_MS}
              step={100}
              value={triggerIntervalMs}
              onChange={(e) =>
                setTriggerIntervalMs(
                  Math.min(MAX_TRIGGER_INTERVAL_MS, Math.max(MIN_TRIGGER_INTERVAL_MS, Number(e.target.value) || 0)),
                )
              }
            />
            <span style={{ fontSize: 11, color: 'var(--text)' }}>ms</span>
          </div>
        )}

        <span className="modebar__spacer" />

        <div className="modebar__actions">
          {mode === 'run' ? (
            <>
              <button
                type="button"
                className="btn"
                onClick={() => void withBusy('once', inspectOnce)}
                disabled={!recipeId || isRunning || busy !== null}
              >
                Tek Denetim
              </button>
              <button
                type="button"
                className={`btn ${isRunning ? 'is-danger' : 'is-primary'}`}
                disabled={!recipeId || busy !== null}
                onClick={() =>
                  void withBusy('run', async () => {
                    if (isRunning) {
                      const state = await stopRun()
                      setRunStats(state.statistics)
                      setIsRunning(false)
                    } else if (recipeId) {
                      const state = await startRun(recipeId)
                      setRunStats(state.statistics)
                      setIsRunning(true)
                    }
                  })
                }
              >
                {isRunning ? '■ Durdur' : '▶ Sürekli Başlat'}
              </button>
            </>
          ) : (
            <button
              type="button"
              className="btn"
              onClick={() =>
                void withBusy('apply', async () => {
                  if (recipeId) {
                    setCameraSettings(await applyRecipeToCamera(recipeId))
                  }
                })
              }
              disabled={!recipeId || !isConnected || busy !== null}
            >
              Reçetenin Kamera Ayarlarını Yükle
            </button>
          )}
        </div>
      </div>

      <div className="app__body">
        <div className="app__stage">
          <ImageStage
            imageUrl={imageUrl}
            frameWidth={frameWidth}
            frameHeight={frameHeight}
            windows={windows}
            results={mode === 'run' ? (result?.windows ?? null) : null}
            selectedId={selectedWindowId}
            editable={mode === 'program'}
            onSelect={setSelectedWindowId}
            onCreate={handleDrawRegion}
            drawMode={drawMode}
            pattern={position.pattern}
            searchMargin={position.searchMargin}
            positionEnabled={position.enabled}
            onChange={(id, region) => patchWindow(id, { region })}
            onPatternChange={(region) => {
              setPosition((current) => ({ ...current, pattern: region }))
              setIsDirty(true)
            }}
            verdict={mode === 'run' && result ? (result.passed ? 'OK' : 'NG') : null}
            headline={mode === 'run' ? 'HAZIR' : 'PROGRAM'}
            meta={meta}
            zoom={zoom}
            onZoomChange={setZoom}
            showWindows={showWindows}
            onShowWindowsChange={setShowWindows}
            // In trigger mode the camera is idle, so both teaching and a spot check need a way to
            // ask for one frame.
            onManualTrigger={isTriggered && isConnected && !isRunning ? handleManualTrigger : null}
            isTriggering={busy === 'grab'}
            hint={mode === 'program' ? 'Sürükleyerek araç çizin · taşı · sağ alt köşeden boyutlandır' : undefined}
          />
          {error && (
            <p className="error-text" style={{ margin: 8 }}>
              {error}
            </p>
          )}
        </div>

        <div className="app__side">
          {mode === 'run' ? (
            <RunPanel
              windows={windows}
              result={result}
              statistics={runStats}
              selectedWindowId={selectedWindowId}
              onSelectWindow={setSelectedWindowId}
              onResetStatistics={() =>
                void resetRunStatistics()
                  .then((state) => setRunStats(state.statistics))
                  .catch(() => setRunStats(EMPTY_RUN_STATISTICS))
              }
            />
          ) : (
            <ProgramPanel
              windows={windows}
              selectedWindowId={selectedWindowId}
              busy={busy}
              onSelectWindow={setSelectedWindowId}
              onPatchWindow={patchWindow}
              onDeleteWindow={(id) => {
                setWindows((current) => current.filter((w) => w.id !== id))
                setSelectedWindowId(null)
                setIsDirty(true)
              }}
              onAddTool={handleAddTool}
              onTeachLimits={handleTeachLimits}
              onTryTool={handleTryTool}
              trialReading={trialReading}
              ocrLanguages={ocrLanguages}
              onTeachSample={handleTeachSample}
              onComputeLimits={handleComputeLimits}
              onClearSamples={handleClearSamples}
              onOpenCamera={() => setShowCameraDialog(true)}
              master={master}
              onRegisterMaster={handleRegisterMaster}
              onDeleteMaster={handleDeleteMaster}
              position={position}
              onPositionChange={(next) => {
                setPosition(next)
                setIsDirty(true)
              }}
              output={output}
              onOutputChange={(next) => {
                setOutput(next)
                setIsDirty(true)
              }}
              drawMode={drawMode}
              onDrawModeChange={setDrawMode}
              recipeSaved={recipeId !== null}
            />
          )}
        </div>
      </div>

      <div className="app__statusbar">
        <span>
          Model: <b>{device?.model ?? '—'}</b>
        </span>
        <span>
          Seri No: <b>{device?.serialNumber ?? '—'}</b>
        </span>
        <span>
          IP: <b>{device?.ip ?? '—'}</b>
        </span>
        <span>
          MAC: <b>{device?.mac ?? '—'}</b>
        </span>
        <span>
          Çözünürlük: <b>{frameWidth > 0 ? `${frameWidth}×${frameHeight}` : '—'}</b>
        </span>
        <span>
          Kare: <b>{statistics?.framesCompleted.toLocaleString('tr-TR') ?? '—'}</b>
        </span>
        <span>
          Eksik: <b>{statistics?.framesIncomplete ?? '—'}</b>
        </span>
      </div>

      {showImageHistory && (
        <ImageHistoryDialog
          recipeId={recipeId}
          isCameraConnected={isConnected}
          onClose={() => setShowImageHistory(false)}
        />
      )}

      {showExternalIo && <ExternalIoDialog onClose={() => setShowExternalIo(false)} />}

      {pendingDelete && (
        <ConfirmDialog
          title="Reçeteyi Sil"
          message={`"${pendingDelete.name}" reçetesi ve içindeki ${pendingDelete.windows.length} araç kalıcı olarak silinecek.`}
          confirmLabel="Sil"
          onCancel={() => setPendingDelete(null)}
          onConfirm={() =>
            void withBusy('delete', async () => {
              await deleteRecipe(pendingDelete.id)
              setRecipes(await listRecipes())
              loadRecipe(null)
              setPendingDelete(null)
            })
          }
        />
      )}

      {showAbout && (
        <AboutDialog health={health} healthError={healthError} device={device} onClose={() => setShowAbout(false)} />
      )}

      {showCameraDialog && (
        <CameraDialog
          settings={cameraSettings}
          onSettingsChange={setCameraSettings}
          onDisconnected={() => {
            setDevice(null)
            setStatistics(null)
            setCameraSettings(null)
            setIsRunning(false)
            setShowCameraDialog(false)
            setHasStarted(false)
          }}
          onClose={() => setShowCameraDialog(false)}
        />
      )}
    </div>
  )
}

export default App
