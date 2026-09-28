using MTCVision.Backend.Inspection;

namespace MTCVision.Backend.Integration;

/// <summary>
/// Announces what the inspection is doing, so the TCP/IP interface can report it to a PLC without
/// the runner knowing anything about that interface. Listeners must not block: an inspection cycle
/// is waiting on the call.
/// </summary>
public sealed class InspectionActivity
{
    public event EventHandler? Started;
    public event EventHandler<InspectionResult>? Completed;
    public event EventHandler<string>? Failed;

    public void ReportStarted() => Started?.Invoke(this, EventArgs.Empty);

    public void ReportCompleted(InspectionResult result) => Completed?.Invoke(this, result);

    public void ReportFailed(string message) => Failed?.Invoke(this, message);
}
