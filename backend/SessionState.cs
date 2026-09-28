using System.Text.Json;

namespace MTCVision.Backend;

/// <summary>
/// What the server was doing, kept on disk so it can pick it up again.
///
/// An operator who starts a run and walks away expects to come back to a running line, not to a
/// stopped one because the machine rebooted overnight. Holding this only in memory meant a restart
/// silently ended production; the state is small, so it is written every time it changes rather
/// than on a timer.
/// </summary>
public sealed class SessionStore(DataPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path = paths.File("session.json");
    private readonly object _writeLock = new();

    public SessionState Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_path), SerializerOptions) ?? new SessionState()
                : new SessionState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"[Session] Durum okunamadı: {ex.Message}");
            return new SessionState();
        }
    }

    public void Update(Func<SessionState, SessionState> change)
    {
        lock (_writeLock)
        {
            var updated = change(Read());
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(updated, SerializerOptions));
            }
            catch (IOException ex)
            {
                // Failing to remember must not stop the thing being remembered from happening.
                Console.WriteLine($"[Session] Durum kaydedilemedi: {ex.Message}");
            }
        }
    }
}

public sealed record SessionState
{
    /// <summary>The camera to reconnect to on startup, and the local interface that reaches it.</summary>
    public string? CameraIp { get; init; }

    public string? LocalIp { get; init; }

    /// <summary>Which kind of camera the operator picked ("Gige" or "Zebra"); null until they pick one.</summary>
    public string? CameraBackend { get; init; }

    /// <summary>The mode the console was left in, "run" or "program"; it reopens in the same one.</summary>
    public string? Mode { get; init; }

    /// <summary>
    /// The camera's trigger source is "TCP/IP": it waits in software-trigger mode and a message on
    /// the TCP/IP interface is what fires it. On the camera itself this is indistinguishable from
    /// the internal (software) trigger, so the choice has to be remembered here.
    /// </summary>
    public bool TcpTrigger { get; init; }

    /// <summary>The recipe on screen when the console was last used, whether or not a run was going.</summary>
    public string? ActiveRecipeId { get; init; }

    /// <summary>The recipe that was running, or null if the operator had stopped it.</summary>
    public string? RunningRecipeId { get; init; }
}
