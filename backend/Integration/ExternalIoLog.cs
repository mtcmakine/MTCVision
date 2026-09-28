namespace MTCVision.Backend.Integration;

/// <summary>Which way a console line went, or that it is a note from the interface itself.</summary>
public enum ExternalIoDirection
{
    /// <summary>Received from a controller.</summary>
    Rx,

    /// <summary>Sent to the controllers.</summary>
    Tx,

    /// <summary>The interface reporting on itself: listening, connections, refusals.</summary>
    Info,

    Error,
}

/// <summary>One line of the TCP/IP console.</summary>
/// <param name="Seq">Increases by one per entry, so a reader can ask for only what it has not seen.</param>
/// <param name="Remote">The controller it concerns, or null when it concerns all of them.</param>
/// <param name="Signal">The status signal this line sent, when it sent one.</param>
public sealed record ExternalIoLogEntry(
    long Seq,
    DateTime TimeUtc,
    ExternalIoDirection Direction,
    string? Remote,
    string Text,
    ExternalSignal? Signal);

public sealed record ExternalIoLogPage(IReadOnlyList<ExternalIoLogEntry> Entries, long Last);

/// <summary>
/// What crossed the TCP/IP interface, kept in memory for the console. A ring: on a line that runs
/// for weeks the console only ever needs the recent past, and a log that grew without limit would
/// be a leak. It is not a record of production, only of the conversation with the controller.
/// </summary>
public sealed class ExternalIoLog
{
    private const int Capacity = 500;

    private readonly object _gate = new();
    private readonly Queue<ExternalIoLogEntry> _entries = new();
    private long _seq;

    public void Add(ExternalIoDirection direction, string text, string? remote = null, ExternalSignal? signal = null)
    {
        lock (_gate)
        {
            _entries.Enqueue(new ExternalIoLogEntry(++_seq, DateTime.UtcNow, direction, remote, text, signal));
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>Everything after <paramref name="after"/>; pass 0 for everything still held.</summary>
    public ExternalIoLogPage Since(long after)
    {
        lock (_gate)
        {
            return new ExternalIoLogPage(_entries.Where(e => e.Seq > after).ToList(), _seq);
        }
    }
}
