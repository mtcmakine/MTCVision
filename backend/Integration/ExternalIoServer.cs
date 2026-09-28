using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;

namespace MTCVision.Backend.Integration;

/// <summary>How often a signal has been sent since start, and how long ago (null: never).</summary>
public sealed record ExternalSignalStatus(ExternalSignal Signal, int Count, long? AgeMs);

/// <summary>
/// The levels a controller could read straight off the console: what is true right now, as opposed
/// to the signals, which are events announcing a change.
/// </summary>
public sealed record ExternalIoState(bool Run, bool Busy, bool Ready);

public sealed record ExternalIoStatus(
    bool Listening,
    int Port,
    IReadOnlyList<string> Clients,
    string? Error,
    DateTime? LastTriggerUtc,
    IReadOnlyList<ExternalSignalStatus> Signals,
    ExternalIoState State);

public sealed record ExternalIoInfo(ExternalIoSettings Settings, ExternalIoStatus Status);

/// <summary>
/// The TCP/IP interface for a PLC or robot: it connects to a port on this machine, sends the
/// trigger message to start an inspection, and is sent Ready / Busy / Run / Error / AllOK / AllNok /
/// ResultAvailable as the console's state changes.
///
/// One text line per message. Signals are events, sent when something happens, and a controller
/// that connects late is told the current state straight away. A trigger that cannot be carried
/// out (Program mode, camera down, an inspection already running) is answered with a signal, not
/// silence, so the controller is never left waiting on a trigger that was dropped.
/// </summary>
public sealed class ExternalIoServer(
    ExternalIoSettingsStore store,
    InspectionRunner runner,
    InspectionActivity activity,
    OperatingMode mode,
    ICameraService camera,
    ExternalIoLog log,
    ILogger<ExternalIoServer> logger) : IHostedService
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>Longest text the console may send by hand; a real message is far shorter.</summary>
    private const int MaxRawLength = 256;

    private readonly object _gate = new();
    private readonly List<Client> _clients = [];
    private readonly Dictionary<ExternalSignal, int> _signalCounts = [];
    private readonly Dictionary<ExternalSignal, DateTime> _signalTimes = [];

    private ExternalIoSettings _settings = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private string? _error;
    private DateTime? _lastTriggerUtc;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        activity.Started += OnStarted;
        activity.Completed += OnCompleted;
        activity.Failed += OnFailed;
        mode.Changed += OnModeChanged;

        Apply(store.Read());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        activity.Started -= OnStarted;
        activity.Completed -= OnCompleted;
        activity.Failed -= OnFailed;
        mode.Changed -= OnModeChanged;

        lock (_gate)
        {
            StopListeningLocked();
        }

        return Task.CompletedTask;
    }

    /// <summary>Puts new settings into effect: stops listening, drops the controllers, and starts again if enabled.</summary>
    public void Apply(ExternalIoSettings settings)
    {
        lock (_gate)
        {
            StopListeningLocked();
            _settings = settings;
            _error = null;

            if (!settings.Enabled)
            {
                log.Add(ExternalIoDirection.Info, "TCP/IP arayüzü kapalı.");
                return;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Any, settings.Port);
                listener.Start();

                _listener = listener;
                _listenerCancellation = new CancellationTokenSource();
                var token = _listenerCancellation.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, token));
                logger.LogInformation("TCP/IP arayüzü {Port} portunu dinliyor.", settings.Port);
                log.Add(ExternalIoDirection.Info, $"{settings.Port} portu dinleniyor.");
            }
            catch (SocketException ex)
            {
                _error = $"{settings.Port} portu dinlenemedi: {ex.Message}";
                logger.LogError("TCP/IP arayüzü başlatılamadı: {Reason}", ex.Message);
                log.Add(ExternalIoDirection.Error, _error);
            }
        }
    }

    public ExternalIoInfo Snapshot()
    {
        var now = DateTime.UtcNow;
        var isRun = mode.IsRun;
        var state = new ExternalIoState(isRun, runner.IsBusy, isRun && runner.CanTrigger);

        lock (_gate)
        {
            var signals = Enum.GetValues<ExternalSignal>()
                .Select(signal => new ExternalSignalStatus(
                    signal,
                    _signalCounts.GetValueOrDefault(signal),
                    _signalTimes.TryGetValue(signal, out var at) ? (long)(now - at).TotalMilliseconds : null))
                .ToList();

            return new ExternalIoInfo(
                _settings,
                new ExternalIoStatus(
                    _listener is not null,
                    _settings.Port,
                    _clients.Select(c => c.Remote).ToList(),
                    _error,
                    _lastTriggerUtc,
                    signals,
                    state));
        }
    }

    /// <summary>Sends one signal's message to every connected controller, for commissioning. Returns how many got it.</summary>
    public int SendTest(ExternalSignal signal)
    {
        Broadcast(signal);
        lock (_gate)
        {
            return _clients.Count;
        }
    }

    /// <summary>
    /// Sends arbitrary text to every connected controller, so the console can be used to try out a
    /// controller's parsing with messages that are not one of the status signals. Returns how many got it.
    /// </summary>
    public int SendRaw(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxRawLength || text.Contains('\r') || text.Contains('\n'))
        {
            throw new ArgumentException($"Mesaj boş olamaz, en fazla {MaxRawLength} karakter olabilir ve satır sonu içeremez.");
        }

        ExternalIoSettings settings;
        Client[] targets;
        lock (_gate)
        {
            settings = _settings;
            targets = _clients.ToArray();
        }

        log.Add(ExternalIoDirection.Tx, text, DescribeTargets(targets));
        var data = Encoding.UTF8.GetBytes(text + settings.TerminatorText);
        foreach (var target in targets)
        {
            target.Enqueue(data);
        }

        return targets.Length;
    }

    private static string DescribeTargets(Client[] targets) => targets.Length switch
    {
        0 => "bağlı istemci yok",
        1 => targets[0].Remote,
        _ => $"{targets.Length} istemci",
    };

    private void StopListeningLocked()
    {
        _listenerCancellation?.Cancel();
        _listenerCancellation = null;

        _listener?.Stop();
        _listener = null;

        foreach (var client in _clients)
        {
            client.Close();
        }

        _clients.Clear();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var client = new Client(tcp);
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    private async Task ServeAsync(Client client, CancellationToken ct)
    {
        lock (_gate)
        {
            _clients.Add(client);
        }

        logger.LogInformation("TCP/IP arayüzüne bağlanıldı: {Remote}", client.Remote);
        log.Add(ExternalIoDirection.Info, "Bağlandı.", client.Remote);
        _ = client.WriteLoopAsync(ct);
        SendCurrentState(client);

        try
        {
            var buffer = new byte[1024];
            var pending = new StringBuilder();
            var stream = client.Stream;

            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                foreach (var message in TakeMessages(pending))
                {
                    OnMessage(client.Remote, message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The controller went away, or the interface was reconfigured underneath it.
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
            }

            client.Close();
            logger.LogInformation("TCP/IP arayüzünden ayrıldı: {Remote}", client.Remote);
            log.Add(ExternalIoDirection.Info, "Bağlantı kapandı.", client.Remote);
        }
    }

    /// <summary>
    /// Splits what has arrived into messages. Any CR or LF ends one, whatever terminator is set for
    /// outgoing messages, since controllers are inconsistent about it; with no terminator, each
    /// burst of bytes is one message.
    /// </summary>
    private IEnumerable<string> TakeMessages(StringBuilder pending)
    {
        if (_settings.Terminator == ExternalIoSettings.TerminatorNone)
        {
            var whole = pending.ToString().Trim();
            pending.Clear();
            if (whole.Length > 0)
            {
                yield return whole;
            }

            yield break;
        }

        var text = pending.ToString();
        var lastBreak = text.LastIndexOfAny(LineBreaks);
        if (lastBreak < 0)
        {
            // No line yet; bound the wait so a stream with no line breaks cannot grow without limit.
            if (pending.Length > 1024)
            {
                pending.Clear();
            }

            yield break;
        }

        pending.Remove(0, lastBreak + 1);
        foreach (var line in text[..(lastBreak + 1)].Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private void OnMessage(string remote, string message)
    {
        var settings = _settings;
        log.Add(ExternalIoDirection.Rx, message, remote);

        var isTrigger = settings.Terminator == ExternalIoSettings.TerminatorNone
            ? message.Contains(settings.TriggerMessage, StringComparison.OrdinalIgnoreCase)
            : message.Equals(settings.TriggerMessage, StringComparison.OrdinalIgnoreCase);

        if (!isTrigger)
        {
            logger.LogDebug("TCP/IP arayüzü tanınmayan mesaj aldı: {Message}", message);
            log.Add(ExternalIoDirection.Info, $"Tanınmayan mesaj yok sayıldı (tetik mesajı: {settings.TriggerMessage}).", remote);
            return;
        }

        _lastTriggerUtc = DateTime.UtcNow;
        _ = TriggerAsync(remote);
    }

    /// <summary>Held while a Program-mode grab is in progress, so a second trigger is refused rather than queued.</summary>
    private readonly SemaphoreSlim _grabLock = new(1, 1);

    /// <summary>
    /// With TCP/IP chosen as the camera's trigger source, a message in Program mode takes one
    /// picture and judges nothing: it is what keeps a live view to teach against on a camera that
    /// otherwise waits for a trigger.
    /// </summary>
    private async Task GrabAsync(string remote)
    {
        if (!camera.IsConnected)
        {
            log.Add(ExternalIoDirection.Error, "Tetik reddedildi: kamera bağlı değil.", remote);
            Broadcast(ExternalSignal.Error);
            return;
        }

        if (!await _grabLock.WaitAsync(0))
        {
            log.Add(ExternalIoDirection.Error, "Tetik reddedildi: görüntü alma zaten sürüyor.", remote);
            Broadcast(ExternalSignal.Busy);
            return;
        }

        try
        {
            Broadcast(ExternalSignal.Busy);
            await camera.TriggerAsync();
            log.Add(ExternalIoDirection.Info, "Görüntü alındı (Program modu, denetim yapılmadı).", remote);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Dış tetikle görüntü alınamadı: {Reason}", ex.Message);
            log.Add(ExternalIoDirection.Error, $"Görüntü alınamadı: {ex.Message}", remote);
            Broadcast(ExternalSignal.Error);
        }
        finally
        {
            _grabLock.Release();
            Broadcast(ExternalSignal.Ready);
        }
    }

    private async Task TriggerAsync(string remote)
    {
        if (!mode.IsRun && mode.TcpTrigger)
        {
            await GrabAsync(remote);
            return;
        }

        if (!mode.IsRun)
        {
            logger.LogWarning("Dış tetik reddedildi: konsol Program modunda.");
            log.Add(ExternalIoDirection.Error, "Tetik reddedildi: konsol Program modunda.", remote);
            Broadcast(ExternalSignal.Error);
            return;
        }

        try
        {
            await runner.RunOnceAsync();
        }
        catch (InspectionBusyException)
        {
            logger.LogWarning("Dış tetik reddedildi: bir denetim zaten sürüyor.");
            log.Add(ExternalIoDirection.Error, "Tetik reddedildi: bir denetim zaten sürüyor.", remote);
            Broadcast(ExternalSignal.Busy);
        }
        catch (CameraConfigurationException ex)
        {
            logger.LogWarning("Dış tetik reddedildi: {Reason}", ex.Message);
            log.Add(ExternalIoDirection.Error, $"Tetik reddedildi: {ex.Message}", remote);
            Broadcast(ExternalSignal.Error);
        }
        catch (Exception ex)
        {
            // The inspection itself failed; the runner has already reported that as an Error.
            logger.LogWarning("Dış tetikle başlayan denetim başarısız oldu: {Reason}", ex.Message);
            log.Add(ExternalIoDirection.Error, $"Denetim başarısız oldu: {ex.Message}", remote);
        }
    }

    private void OnStarted(object? sender, EventArgs e) => Broadcast(ExternalSignal.Busy);

    private void OnCompleted(object? sender, InspectionResult result)
    {
        Broadcast(ExternalSignal.ResultAvailable);
        Broadcast(result.Passed ? ExternalSignal.AllOK : ExternalSignal.AllNok);
        BroadcastReadyAfterCycle();
    }

    private void OnFailed(object? sender, string message)
    {
        Broadcast(ExternalSignal.Error);
        BroadcastReadyAfterCycle();
    }

    private void OnModeChanged(object? sender, EventArgs e)
    {
        if (!mode.IsRun)
        {
            return;
        }

        Broadcast(ExternalSignal.Run);
        if (runner.CanTrigger)
        {
            Broadcast(ExternalSignal.Ready);
        }
    }

    /// <summary>
    /// The cycle that just finished still holds the runner's busy flag when this is called, so the
    /// readiness check cannot ask the runner whether it is busy.
    /// </summary>
    private void BroadcastReadyAfterCycle()
    {
        if (mode.IsRun && camera.IsConnected && runner.HasActiveRecipe && !runner.IsRunning)
        {
            Broadcast(ExternalSignal.Ready);
        }
    }

    /// <summary>What a controller that just connected needs to know to be in step with the console.</summary>
    private void SendCurrentState(Client client)
    {
        ExternalIoSettings settings;
        lock (_gate)
        {
            settings = _settings;
        }

        void Send(ExternalSignal signal)
        {
            var message = settings.MessageFor(signal);
            if (!string.IsNullOrEmpty(message))
            {
                log.Add(ExternalIoDirection.Tx, message, client.Remote, signal);
                client.Enqueue(Encoding.UTF8.GetBytes(message + settings.TerminatorText));
            }
        }

        if (mode.IsRun)
        {
            Send(ExternalSignal.Run);
        }

        if (runner.IsBusy)
        {
            Send(ExternalSignal.Busy);
        }
        else if (mode.IsRun && runner.CanTrigger)
        {
            Send(ExternalSignal.Ready);
        }
    }

    private void Broadcast(ExternalSignal signal)
    {
        ExternalIoSettings settings;
        Client[] targets;
        lock (_gate)
        {
            settings = _settings;
            targets = _clients.ToArray();
        }

        var message = settings.MessageFor(signal);
        if (string.IsNullOrEmpty(message))
        {
            log.Add(ExternalIoDirection.Info, $"{signal} sinyali kapalı (mesajı boş), gönderilmedi.");
            return;
        }

        // Counted even with nobody connected: the console still raised the signal, and the panel
        // showing it is how an operator sees that without a controller attached.
        lock (_gate)
        {
            _signalCounts[signal] = _signalCounts.GetValueOrDefault(signal) + 1;
            _signalTimes[signal] = DateTime.UtcNow;
        }

        log.Add(ExternalIoDirection.Tx, message, DescribeTargets(targets), signal);
        if (targets.Length == 0)
        {
            return;
        }

        var data = Encoding.UTF8.GetBytes(message + settings.TerminatorText);
        foreach (var target in targets)
        {
            target.Enqueue(data);
        }
    }

    /// <summary>
    /// One connected controller. Outgoing messages go through a queue drained by a single writer, so
    /// they reach it in the order they were produced and a slow controller never stalls an inspection.
    /// </summary>
    private sealed class Client
    {
        private readonly TcpClient _tcp;
        private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();

        public Client(TcpClient tcp)
        {
            _tcp = tcp;
            Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        }

        public string Remote { get; }

        public NetworkStream Stream => _tcp.GetStream();

        public void Enqueue(byte[] data) => _outbox.Writer.TryWrite(data);

        public async Task WriteLoopAsync(CancellationToken ct)
        {
            try
            {
                var stream = _tcp.GetStream();
                await foreach (var data in _outbox.Reader.ReadAllAsync(ct))
                {
                    await stream.WriteAsync(data, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is gone; the reader side notices and cleans up.
            }
        }

        public void Close()
        {
            _outbox.Writer.TryComplete();
            _tcp.Dispose();
        }
    }
}
