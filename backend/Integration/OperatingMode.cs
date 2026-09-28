namespace MTCVision.Backend.Integration;

/// <summary>
/// Whether the console is in Run or Program mode, and which recipe it is on.
///
/// Kept on the server and written to the session file, not held by the browser: the mode has to
/// survive a restart (the line reopens the way it was left) and an outside system that triggers
/// inspections must be able to tell whether the console is armed for production.
/// </summary>
public sealed class OperatingMode
{
    public const string Run = "run";
    public const string Program = "program";

    private readonly SessionStore _sessions;

    public OperatingMode(SessionStore sessions)
    {
        _sessions = sessions;

        var state = sessions.Read();
        Mode = state.Mode == Run ? Run : Program;
        ActiveRecipeId = state.ActiveRecipeId;
        TcpTrigger = state.TcpTrigger;
    }

    /// <summary>The camera is triggered by a message on the TCP/IP interface (see <see cref="SessionState.TcpTrigger"/>).</summary>
    public bool TcpTrigger { get; private set; }

    public void SetTcpTrigger(bool enabled)
    {
        if (TcpTrigger == enabled)
        {
            return;
        }

        TcpTrigger = enabled;
        _sessions.Update(state => state with { TcpTrigger = enabled });
    }

    public string Mode { get; private set; }

    public string? ActiveRecipeId { get; private set; }

    public bool IsRun => Mode == Run;

    public event EventHandler? Changed;

    public void Set(string mode, string? recipeId)
    {
        if (mode is not (Run or Program))
        {
            throw new ArgumentException($"Bilinmeyen mod: '{mode}'.", nameof(mode));
        }

        var changed = Mode != mode || ActiveRecipeId != recipeId;
        Mode = mode;
        ActiveRecipeId = recipeId;
        _sessions.Update(state => state with { Mode = mode, ActiveRecipeId = recipeId });

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
