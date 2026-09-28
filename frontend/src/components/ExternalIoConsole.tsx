import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  getExternalLog,
  sendExternalText,
  type ExternalLogDirection,
  type ExternalLogEntry,
} from '../api/client'

/** How often new console lines are fetched while the console is open. */
const LOG_POLL_MS = 500

/** The server keeps 500; the screen holds a little more so a busy line does not scroll history away too fast. */
const MAX_LINES = 1000

const ARROWS: Record<ExternalLogDirection, string> = {
  Rx: '←',
  Tx: '→',
  Info: '·',
  Error: '!',
}

type Filter = 'Rx' | 'Tx' | 'Info'

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'Rx', label: 'Gelen' },
  { id: 'Tx', label: 'Giden' },
  { id: 'Info', label: 'Bilgi' },
]

function formatTime(iso: string): string {
  const at = new Date(iso)
  const ms = String(at.getMilliseconds()).padStart(3, '0')
  return `${at.toLocaleTimeString('tr-TR', { hour12: false })}.${ms}`
}

interface ExternalIoConsoleProps {
  /** Whether there is anything to send to: the interface must be listening. */
  canSend: boolean
}

/**
 * What crossed the TCP/IP interface, as a terminal: messages from the controller, the signals sent
 * back, and the interface's own notes (connections, refused triggers). A line typed at the bottom
 * is sent to the connected controllers as-is, to try out a controller's parsing.
 *
 * The server keeps the recent past, so opening the console shows history rather than starting empty.
 */
export function ExternalIoConsole({ canSend }: ExternalIoConsoleProps) {
  const [entries, setEntries] = useState<ExternalLogEntry[]>([])
  const [filters, setFilters] = useState<Record<Filter, boolean>>({ Rx: true, Tx: true, Info: true })
  const [autoScroll, setAutoScroll] = useState(true)
  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const cursor = useRef(0)
  const screen = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false

    const poll = () => {
      getExternalLog(cursor.current)
        .then((page) => {
          if (cancelled) {
            return
          }

          if (page.last < cursor.current) {
            // The server restarted and its numbering began again: start over from what it holds now.
            cursor.current = 0
            setEntries([])
            return
          }

          // Polls can overlap when the server is slow, so lines already shown are dropped by number.
          const fresh = page.entries.filter((entry) => entry.seq > cursor.current)
          if (fresh.length === 0) {
            return
          }

          cursor.current = page.last
          setEntries((current) => [...current, ...fresh].slice(-MAX_LINES))
        })
        .catch(() => undefined)
    }

    poll()
    const timer = setInterval(poll, LOG_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  useEffect(() => {
    if (autoScroll && screen.current) {
      screen.current.scrollTop = screen.current.scrollHeight
    }
  }, [entries, filters, autoScroll])

  const visible = entries.filter((entry) => entry.direction === 'Error' || filters[entry.direction as Filter])

  const send = async (event: FormEvent) => {
    event.preventDefault()
    const message = text.trim()
    if (!message) {
      return
    }

    setSending(true)
    setError(null)
    try {
      await sendExternalText(message)
      setText('')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Gönderilemedi')
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="console">
      <div className="console__toolbar">
        {FILTERS.map((filter) => (
          <label key={filter.id}>
            <input
              type="checkbox"
              checked={filters[filter.id]}
              onChange={(e) => setFilters((current) => ({ ...current, [filter.id]: e.target.checked }))}
            />
            {filter.label}
          </label>
        ))}
        <label>
          <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} />
          Otomatik kaydır
        </label>
        <button type="button" className="btn" onClick={() => setEntries([])}>
          Temizle
        </button>
      </div>

      <div className="console__screen" ref={screen} role="log" aria-label="TCP/IP konsolu">
        {visible.length === 0 ? (
          <span className="console__empty">Henüz mesaj yok.</span>
        ) : (
          visible.map((entry) => (
            <div key={entry.seq} className={`console__line is-${entry.direction.toLowerCase()}`}>
              <span className="console__time">{formatTime(entry.timeUtc)}</span>
              <span className="console__arrow">{ARROWS[entry.direction]}</span>
              {entry.remote && <span className="console__remote">{entry.remote}</span>}
              <span className="console__text">{entry.text}</span>
              {entry.signal && entry.signal.toLowerCase() !== entry.text.toLowerCase() && (
                <span className="console__tag">{entry.signal}</span>
              )}
            </div>
          ))
        )}
      </div>

      <form className="console__input" onSubmit={(e) => void send(e)}>
        <input
          className="field"
          aria-label="Gönderilecek mesaj"
          placeholder={canSend ? 'Bağlı kontrolcülere gönderilecek metin…' : 'Arayüz dinlemiyor'}
          maxLength={256}
          disabled={!canSend}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <button type="submit" className="btn" disabled={!canSend || sending || text.trim().length === 0}>
          Gönder
        </button>
      </form>
      {error && <p className="error-text">{error}</p>}
    </div>
  )
}
