/** Running totals for the current run, reset by the operator. */
export interface RunStatistics {
  total: number
  ok: number
  ng: number
  lastMs: number
  minMs: number
  maxMs: number
  sumMs: number
}

export const EMPTY_RUN_STATISTICS: RunStatistics = {
  total: 0,
  ok: 0,
  ng: 0,
  lastMs: 0,
  minMs: 0,
  maxMs: 0,
  sumMs: 0,
}

export function accumulate(stats: RunStatistics, passed: boolean, elapsedMs: number): RunStatistics {
  return {
    total: stats.total + 1,
    ok: stats.ok + (passed ? 1 : 0),
    ng: stats.ng + (passed ? 0 : 1),
    lastMs: elapsedMs,
    // The first cycle is both the fastest and the slowest so far.
    minMs: stats.total === 0 ? elapsedMs : Math.min(stats.minMs, elapsedMs),
    maxMs: Math.max(stats.maxMs, elapsedMs),
    sumMs: stats.sumMs + elapsedMs,
  }
}
