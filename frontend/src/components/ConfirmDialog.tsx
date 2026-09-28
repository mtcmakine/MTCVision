interface ConfirmDialogProps {
  title: string
  message: string
  confirmLabel: string
  onConfirm: () => void
  onCancel: () => void
}

/**
 * A stop before something irreversible. Deleting a recipe throws away windows and the samples they
 * were taught from, none of which can be recovered from the camera, so it is worth a keystroke.
 */
export function ConfirmDialog({ title, message, confirmLabel, onConfirm, onCancel }: ConfirmDialogProps) {
  return (
    <div className="modal__scrim" onPointerDown={(e) => e.target === e.currentTarget && onCancel()}>
      <div className="modal" style={{ width: 'min(440px, calc(100vw - 40px))' }}>
        <div className="modal__title">
          <span>{title}</span>
        </div>
        <div className="modal__body">
          <p style={{ margin: 0, fontSize: 13 }}>{message}</p>
          <p className="hint-text" style={{ marginTop: 8 }}>
            Bu işlem geri alınamaz.
          </p>
        </div>
        <div className="modal__footer">
          <button type="button" className="btn" onClick={onCancel}>
            Vazgeç
          </button>
          <button type="button" className="btn is-danger" onClick={onConfirm}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
