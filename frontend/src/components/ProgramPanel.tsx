import type {
  InspectionToolKind,
  InspectionWindow,
  MasterImage,
  OcrLanguage,
  OutputAssignment,
  PositionAdjustment,
  WindowMeasurement,
} from '../api/client'
import type { DrawMode } from './RoiEditor'

/** Short names for the window list, where the full explanation would not fit. */
const TOOL_NAMES: Record<InspectionToolKind, string> = {
  Ocr: 'OCR',
}

interface ProgramPanelProps {
  windows: InspectionWindow[]
  selectedWindowId: string | null
  busy: string | null
  onSelectWindow: (id: string | null) => void
  onPatchWindow: (id: string, patch: Partial<InspectionWindow>) => void
  onDeleteWindow: (id: string) => void
  onAddTool: (tool: InspectionToolKind) => void
  onTeachLimits: () => void
  /** Runs the selected window's tool now and shows what it read, without judging it. */
  onTryTool: () => void
  trialReading: WindowMeasurement | null
  /** Recognition models installed on the server; a window picks the script its text is in. */
  ocrLanguages: OcrLanguage[]
  /** Records one reading per window from the current frame, labelled good or bad. */
  onTeachSample: (isGood: boolean) => void
  onComputeLimits: () => void
  onClearSamples: () => void
  onOpenCamera: () => void
  /** The registered picture of a good part, or null if none has been taken. */
  master: MasterImage | null
  onRegisterMaster: () => void
  onDeleteMaster: () => void
  position: PositionAdjustment
  onPositionChange: (position: PositionAdjustment) => void
  output: OutputAssignment
  onOutputChange: (output: OutputAssignment) => void
  /** What a drag on the image draws right now. */
  drawMode: DrawMode
  onDrawModeChange: (mode: DrawMode) => void
  /** A master can only be registered once the recipe exists to hang it on. */
  recipeSaved: boolean
}

export function ProgramPanel({
  windows,
  selectedWindowId,
  busy,
  onSelectWindow,
  onPatchWindow,
  onDeleteWindow,
  onAddTool,
  onTeachLimits,
  onTryTool,
  trialReading,
  ocrLanguages,
  onTeachSample,
  onComputeLimits,
  onClearSamples,
  onOpenCamera,
  master,
  onRegisterMaster,
  onDeleteMaster,
  position,
  onPositionChange,
  output,
  onOutputChange,
  drawMode,
  onDrawModeChange,
  recipeSaved,
}: ProgramPanelProps) {
  // Every window is sampled from the same frame, so the counts are the same for all of them.
  const samples = windows[0]?.samples ?? []
  const goodCount = samples.filter((sample) => sample.isGood).length
  const badCount = samples.length - goodCount
  const selected = windows.find((w) => w.id === selectedWindowId) ?? null

  return (
    <>
      <div className="pane">
        <div className="pane__title">
          <span>Kurulum</span>
        </div>
        <div className="pane__body">
          <button type="button" className="btn" style={{ width: '100%' }} onClick={onOpenCamera}>
            Kamera Ayarları
          </button>
          <p className="hint-text" style={{ marginTop: 6 }}>
            Pozlama, kazanç ve alım modu. Reçete kaydedilirken bu değerler de saklanır — ölçümler
            ancak öğretildikleri aydınlatma altında aynı anlama gelir.
          </p>
        </div>
      </div>

      <div className="pane">
        <div className="pane__title">
          <span>Referans ve Konum</span>
          {master && <span>master var</span>}
        </div>
        <div className="pane__body">
          <div className="row">
            <button
              type="button"
              className="btn is-primary"
              style={{ flex: 1 }}
              disabled={!recipeSaved || busy === 'master'}
              onClick={onRegisterMaster}
            >
              {master ? 'Master Görüntüyü Yenile' : 'Master Görüntüyü Kaydet'}
            </button>
            {master && (
              <button type="button" className="btn" disabled={busy === 'master'} onClick={onDeleteMaster}>
                Sil
              </button>
            )}
          </div>
          <p className="hint-text">
            {!recipeSaved
              ? 'Master görüntü için önce reçeteyi kaydedin.'
              : master
                ? `Kayıtlı: ${new Date(master.capturedUtc).toLocaleString('tr-TR')} · ${master.width}×${master.height}`
                : 'İyi bir parça kameranın önündeyken kaydedin. Konum düzeltme bu görüntüye göre arar.'}
          </p>

          <label className="labeled" style={{ marginTop: 10 }}>
            <span>
              <input
                type="checkbox"
                checked={position.enabled}
                onChange={(e) => onPositionChange({ ...position, enabled: e.target.checked })}
              />{' '}
              Konum düzeltme
            </span>
            <small>
              Parça her seferinde aynı yere gelmez. Desen bulunduğu yere göre bütün pencereler
              kaydırılır; limitleri gevşetmek yerine pencereler parçayı takip eder.
            </small>
          </label>

          <div className="row">
            <button
              type="button"
              className={`btn ${drawMode === 'pattern' ? 'is-primary' : ''}`}
              onClick={() => onDrawModeChange(drawMode === 'pattern' ? 'window' : 'pattern')}
            >
              {drawMode === 'pattern' ? 'Çizmeyi Bırak' : 'Desen Çiz'}
            </button>
            {position.pattern && (
              <button
                type="button"
                className="btn is-danger"
                // A pattern position correction cannot use is worse than none, so removing it
                // also switches the correction off.
                onClick={() => onPositionChange({ ...position, pattern: null, enabled: false })}
              >
                Deseni Sil
              </button>
            )}
            <span className="hint-text" style={{ flex: 1 }}>
              {position.pattern
                ? 'Görüntüde sürükleyerek taşıyın; seçiliyken sağ alt köşeden boyutlandırın.'
                : 'Desen seçilmedi — parçanın kendine ait, belirgin bir yerini seçin.'}
            </span>
          </div>

          {position.pattern && (
            <div className="row">
              {(
                [
                  ['x', 'X', 0],
                  ['y', 'Y', 0],
                  ['width', 'Genişlik', 1],
                  ['height', 'Yükseklik', 1],
                ] as const
              ).map(([key, label, min]) => (
                <label key={key} className="labeled" style={{ flex: 1 }}>
                  {label}
                  <input
                    className="field"
                    type="number"
                    min={min}
                    step={1}
                    value={position.pattern?.[key] ?? 0}
                    onChange={(e) =>
                      position.pattern &&
                      onPositionChange({
                        ...position,
                        pattern: {
                          ...position.pattern,
                          [key]: Math.max(min, Math.round(toNumber(e.target.value))),
                        },
                      })
                    }
                  />
                </label>
              ))}
            </div>
          )}

          <div className="row">
            <label className="labeled">
              Arama payı (px)
              <input
                className="field"
                type="number"
                min={0}
                step={10}
                value={position.searchMargin}
                onChange={(e) => onPositionChange({ ...position, searchMargin: toNumber(e.target.value) })}
              />
            </label>
            <label className="labeled">
              En düşük eşleşme (%)
              <input
                className="field"
                type="number"
                min={0}
                max={100}
                step={1}
                value={position.minScore}
                onChange={(e) => onPositionChange({ ...position, minScore: toNumber(e.target.value) })}
              />
            </label>
          </div>
          <p className="hint-text">
            Eşleşme bu değerin altında kalırsa parça bulunamamış sayılır ve sonuç NG olur — yanlış
            yerde ölçmektense ölçmemek doğrusu.
          </p>

          <label className="labeled" style={{ marginTop: 10 }}>
            Toplam yargı
            <select
              className="field"
              value={output.logic}
              onChange={(e) => onOutputChange({ logic: e.target.value as OutputAssignment['logic'] })}
            >
              <option value="All">Sayılan tüm araçlar geçmeli</option>
              <option value="Any">Sayılan araçlardan biri geçse yeter</option>
            </select>
          </label>
        </div>
      </div>

      <div className="pane pane--fill">
        <div className="pane__title">
          <span>Araçlar</span>
          <span>{windows.length}</span>
        </div>
        <div className="pane__body">
          <div className="row">
            <button type="button" className="btn is-primary" onClick={() => onAddTool('Ocr')}>
              OCR Aracı Ekle
            </button>
          </div>
          <p className="hint-text">
            Eklenen araç görüntünün ortasına yerleşir; sürükleyerek taşıyın. Doğrudan görüntü üzerinde
            yazının etrafını sürükleyerek de çizebilirsiniz.
          </p>

          {windows.length === 0 ? (
            <p className="empty">Henüz araç yok.</p>
          ) : (
            <ul className="window-list">
              {windows.map((window) => (
                <li key={window.id}>
                  <button
                    type="button"
                    className={window.id === selectedWindowId ? 'is-selected' : ''}
                    onClick={() => onSelectWindow(window.id)}
                  >
                    <b>{window.name}</b>
                    <small>
                      {TOOL_NAMES[window.tool]} · {window.lowerLimit} – {window.upperLimit}
                    </small>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <div className="pane">
        <div className="pane__title">
          <span>Örnekten Öğretme</span>
          <span>
            {goodCount} OK / {badCount} NG
          </span>
        </div>
        <div className="pane__body">
          {windows.length === 0 ? (
            <p className="empty">Önce bir araç ekleyin.</p>
          ) : (
            <>
              <div className="row">
                <button type="button" className="btn" disabled={busy !== null} onClick={() => onTeachSample(true)}>
                  {busy === 'teach-ok' ? 'Ölçülüyor…' : 'İyi Parça (OK)'}
                </button>
                <button type="button" className="btn" disabled={busy !== null} onClick={() => onTeachSample(false)}>
                  {busy === 'teach-ng' ? 'Ölçülüyor…' : 'Kötü Parça (NG)'}
                </button>
              </div>
              <div className="row">
                <button
                  type="button"
                  className="btn is-primary"
                  disabled={samples.length === 0 || busy !== null}
                  onClick={onComputeLimits}
                >
                  {busy === 'auto-limits' ? 'Hesaplanıyor…' : 'Limitleri Hesapla'}
                </button>
                <button type="button" className="btn" disabled={samples.length === 0} onClick={onClearSamples}>
                  Örnekleri Sil
                </button>
              </div>
              <p className="hint-text">
                Kameranın önüne bir parça koyup düğmeye basın; bütün araçlar aynı kareden ölçülür.
                Birkaç iyi ve birkaç kötü parça öğrettikten sonra limitler ikisinin ortasına kurulur.
              </p>
            </>
          )}
        </div>
      </div>

      <div className="pane">
        <div className="pane__title">
          <span>Limit Ayarı</span>
        </div>
        <div className="pane__body">
          {!selected ? (
            <p className="empty">Düzenlemek için bir araç seçin.</p>
          ) : (
            <>
              <div className="row">
                <label className="labeled" style={{ flex: 1 }}>
                  Araç adı
                  <input
                    className="field"
                    value={selected.name}
                    onChange={(e) => onPatchWindow(selected.id, { name: e.target.value })}
                  />
                </label>
              </div>

              {ocrLanguages.length > 0 && (
                <div className="row">
                  <label className="labeled" style={{ flex: 1 }}>
                    Yazının dili
                    <select
                      className="field"
                      value={selected.ocrLanguage ?? (ocrLanguages.find((l) => l.isDefault)?.id ?? '')}
                      onChange={(e) => onPatchWindow(selected.id, { ocrLanguage: e.target.value })}
                    >
                      {ocrLanguages.map((language) => (
                        <option key={language.id} value={language.id}>
                          {language.name}
                        </option>
                      ))}
                    </select>
                    <small>
                      {ocrLanguages.find((l) => l.id === (selected.ocrLanguage ?? ocrLanguages.find((x) => x.isDefault)?.id))
                        ?.description ?? ''}
                    </small>
                  </label>
                </div>
              )}

              {selected.tool === 'Ocr' && (
                <div className="row">
                  <label className="labeled" style={{ flex: 1 }}>
                    Beklenen metin
                    <input
                      className="field"
                      value={selected.expectedText ?? ''}
                      placeholder="ör. MTC-2026-A17"
                      onChange={(e) => onPatchWindow(selected.id, { expectedText: e.target.value || null })}
                    />
                    <small>ölçüm, okunan metnin buna benzerlik yüzdesidir (0–100)</small>
                  </label>
                </div>
              )}

              {selected.tool === 'Ocr' && (
                <div className="row">
                  <label className="labeled">
                    <span>
                      <input
                        type="checkbox"
                        checked={selected.ignoreDiacritics}
                        onChange={(e) => onPatchWindow(selected.id, { ignoreDiacritics: e.target.checked })}
                      />{' '}
                      Aksan toleransı
                    </span>
                    <small>
                      ı/i, ş/s, ğ/g, ü/u, é/e, ñ/n gibi ayrımları yok sayar. Eğitilmiş Türkçe paketi
                      bu harfleri doğru okuduğu için normalde kapalı kalmalı.
                    </small>
                  </label>
                </div>
              )}

              <div className="row">
                <label className="labeled">
                  <span>
                    <input
                      type="checkbox"
                      checked={selected.affectsTotal}
                      onChange={(e) => onPatchWindow(selected.id, { affectsTotal: e.target.checked })}
                    />{' '}
                    Sonuca dahil
                  </span>
                  <small>Kapalıysa ölçülür ve gösterilir ama hattı durdurmaz.</small>
                </label>
                <label className="labeled">
                  <span>
                    <input
                      type="checkbox"
                      checked={selected.followsPosition}
                      onChange={(e) => onPatchWindow(selected.id, { followsPosition: e.target.checked })}
                    />{' '}
                    Parçayı takip et
                  </span>
                  <small>Kapalıysa pencere kareye sabit kalır — parçaya değil düzeneğe bağlıysa.</small>
                </label>
              </div>

              <div className="row">
                <button
                  type="button"
                  className={`btn ${drawMode === 'mask' ? 'is-primary' : ''}`}
                  onClick={() => onDrawModeChange(drawMode === 'mask' ? 'window' : 'mask')}
                >
                  {drawMode === 'mask' ? 'Çizmeyi Bırak' : 'Maske Çiz'}
                </button>
                {selected.mask.length > 0 && (
                  <button type="button" className="btn" onClick={() => onPatchWindow(selected.id, { mask: [] })}>
                    Maskeleri Sil ({selected.mask.length})
                  </button>
                )}
              </div>
              <p className="hint-text">
                {selected.mask.length === 0
                  ? 'Pencereye giren ama ölçüme girmemesi gereken yerleri maskeleyin — panel kenarı, ikon, delik.'
                  : 'Maskelenen alanlar ölçüme girmez; OCR için çevrenin tonuyla boyanır.'}
              </p>

              <div className="row">
                <label className="labeled">
                  Alt limit
                  <input
                    className="field"
                    type="number"
                    step={0.1}
                    value={selected.lowerLimit}
                    onChange={(e) =>
                      onPatchWindow(selected.id, { lowerLimit: toNumber(e.target.value) })
                    }
                  />
                </label>
                <label className="labeled">
                  Üst limit
                  <input
                    className="field"
                    type="number"
                    step={0.1}
                    value={selected.upperLimit}
                    onChange={(e) =>
                      onPatchWindow(selected.id, { upperLimit: toNumber(e.target.value) })
                    }
                  />
                </label>
              </div>

              <div className="row">
                <button
                  type="button"
                  className="btn is-primary"
                  onClick={onTeachLimits}
                  disabled={busy !== null}
                >
                  {busy === 'teach' ? 'Ölçülüyor…' : 'İyi Parçadan Öğret'}
                </button>
                <button type="button" className="btn" disabled={busy !== null} onClick={onTryTool}>
                  {busy === 'try' ? 'Deneniyor…' : 'Aracı Dene'}
                </button>
                <button type="button" className="btn is-danger" onClick={() => onDeleteWindow(selected.id)}>
                  Aracı Sil
                </button>
              </div>

              {trialReading && (
                <div className="trial">
                  <b>{trialReading.value}</b>
                  {trialReading.text !== null && (
                    <>
                      <code>okunan: {trialReading.text || '(metin okunamadı)'}</code>
                      {selected.expectedText && trialReading.text !== selected.expectedText && (
                        <code className="is-expected">beklenen: {selected.expectedText}</code>
                      )}
                    </>
                  )}
                  <small>
                    parlaklık {trialReading.brightness} · kontrast {trialReading.contrast}
                  </small>
                  {trialReading.note && <small className="is-note">{trialReading.note}</small>}
                </div>
              )}

              <p className="hint-text">
                Alan: {selected.region.x}, {selected.region.y} · {selected.region.width}×
                {selected.region.height} px
              </p>

              {selected.samples.length > 0 && (
                <div className="samples">
                  {selected.samples.map((sample) => (
                    <span key={sample.id} className={sample.isGood ? 'is-ok' : 'is-ng'}>
                      {sample.value}
                    </span>
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </>
  )
}

function toNumber(value: string): number {
  const parsed = Number(value.replace(',', '.'))
  return Number.isFinite(parsed) ? parsed : 0
}
