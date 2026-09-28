namespace MTCVision.Backend.Integration;

/// <summary>The status signals the console reports to an outside system over TCP/IP.</summary>
public enum ExternalSignal
{
    /// <summary>Armed and idle: a trigger sent now would be accepted.</summary>
    Ready,

    /// <summary>An inspection is in progress.</summary>
    Busy,

    /// <summary>The console is in Run mode.</summary>
    Run,

    /// <summary>A trigger could not be carried out, or the inspection it started failed.</summary>
    Error,

    /// <summary>The inspection finished and every counted window passed.</summary>
    AllOK,

    /// <summary>The inspection finished and at least one counted window failed.</summary>
    AllNok,

    /// <summary>A new result exists; sent just before the verdict itself.</summary>
    ResultAvailable,
}

/// <summary>
/// How the TCP/IP interface for a PLC or robot is set up: the port the console listens on, the
/// text that means "inspect now", and the text sent back for each status signal.
///
/// Messages are plain text lines rather than fixed constants because every controller has its own
/// idea of what a message looks like; an empty message turns that signal off.
/// </summary>
public sealed record ExternalIoSettings
{
    public const string TerminatorCrLf = "CRLF";
    public const string TerminatorLf = "LF";
    public const string TerminatorCr = "CR";
    public const string TerminatorNone = "None";

    public bool Enabled { get; init; }

    public int Port { get; init; } = 5001;

    /// <summary>What ends a message sent to the controller: CRLF, LF, CR or None.</summary>
    public string Terminator { get; init; } = TerminatorCrLf;

    /// <summary>What the controller sends to start an inspection.</summary>
    public string TriggerMessage { get; init; } = "TriggerRequest";

    public string ReadyMessage { get; init; } = "Ready";

    public string BusyMessage { get; init; } = "Busy";

    public string RunMessage { get; init; } = "Run";

    public string ErrorMessage { get; init; } = "Error";

    public string AllOkMessage { get; init; } = "AllOK";

    public string AllNokMessage { get; init; } = "AllNok";

    public string ResultAvailableMessage { get; init; } = "ResultAvailable";

    public string TerminatorText => Terminator switch
    {
        TerminatorCrLf => "\r\n",
        TerminatorLf => "\n",
        TerminatorCr => "\r",
        _ => string.Empty,
    };

    public string MessageFor(ExternalSignal signal) => signal switch
    {
        ExternalSignal.Ready => ReadyMessage,
        ExternalSignal.Busy => BusyMessage,
        ExternalSignal.Run => RunMessage,
        ExternalSignal.Error => ErrorMessage,
        ExternalSignal.AllOK => AllOkMessage,
        ExternalSignal.AllNok => AllNokMessage,
        ExternalSignal.ResultAvailable => ResultAvailableMessage,
        _ => string.Empty,
    };

    /// <summary>Why these settings cannot be used, or null when they can.</summary>
    public string? Validate()
    {
        if (Port is < 1024 or > 65535)
        {
            return "Port 1024 ile 65535 arasında olmalı.";
        }

        if (Terminator is not (TerminatorCrLf or TerminatorLf or TerminatorCr or TerminatorNone))
        {
            return "Sonlandırıcı CRLF, LF, CR veya None olmalı.";
        }

        if (string.IsNullOrWhiteSpace(TriggerMessage))
        {
            return "Tetik mesajı boş olamaz.";
        }

        foreach (var message in new[]
                 {
                     TriggerMessage, ReadyMessage, BusyMessage, RunMessage,
                     ErrorMessage, AllOkMessage, AllNokMessage, ResultAvailableMessage,
                 })
        {
            if (message is null || message.Length > 64 || message.Contains('\r') || message.Contains('\n'))
            {
                return "Mesajlar en fazla 64 karakter olabilir ve satır sonu içeremez.";
            }
        }

        return null;
    }
}
