using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

// A stand-in for the PLC / robot: connects to Vision's TCP/IP interface, sends the trigger
// message and prints every status signal that comes back. Meant for trying the interface out
// without any real controller.

var options = Options.Parse(args);
if (options is null)
{
    Options.PrintUsage();
    return 1;
}

Console.OutputEncoding = Encoding.UTF8;
Log.Info($"Vision demo terminal — {options.Host}:{options.Port}");
Log.Info("Komutlar:  t = tetik gönder | a <ms> = otomatik tetik | a = otomatiği durdur | s = durum | q = çıkış");
Log.Info("Başka bir şey yazıp Enter'a basarsan olduğu gibi gönderilir.");

var terminal = new Terminal(options);
using var quit = new CancellationTokenSource();
var connection = Task.Run(() => terminal.RunConnectionAsync(quit.Token));

string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (!terminal.HandleCommand(line.Trim()))
    {
        break;
    }
}

quit.Cancel();
await Task.WhenAny(connection, Task.Delay(1000));
return 0;

sealed class Terminal(Options options)
{
    private readonly object _gate = new();
    private readonly Stopwatch _sinceTrigger = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private TcpClient? _client;
    private string _lastSignal = "-";
    private CancellationTokenSource? _auto;

    public async Task RunConnectionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(options.Host, options.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                client.Dispose();
                Log.Error($"Bağlanılamadı ({ex.Message}). 3 sn sonra tekrar denenecek — Vision açık mı, TCP/IP arayüzü etkin mi?");
                await SafeDelay(3000, ct);
                continue;
            }

            lock (_gate)
            {
                _client = client;
            }

            Log.Info($"Bağlandı: {client.Client.LocalEndPoint} → {client.Client.RemoteEndPoint}");
            await ReadAsync(client, ct);

            lock (_gate)
            {
                _client = null;
            }

            client.Dispose();
            if (!ct.IsCancellationRequested)
            {
                Log.Error("Bağlantı koptu. 3 sn sonra tekrar denenecek.");
                await SafeDelay(3000, ct);
            }
        }
    }

    private async Task ReadAsync(TcpClient client, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var pending = new StringBuilder();
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    return;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = pending.ToString();
                var lastBreak = text.LastIndexOfAny(['\r', '\n']);
                if (lastBreak < 0)
                {
                    // Terminator "None" on the console side: treat each burst as one message.
                    if (options.NoTerminator)
                    {
                        pending.Clear();
                        OnMessage(text.Trim());
                    }

                    continue;
                }

                pending.Remove(0, lastBreak + 1);
                foreach (var message in text[..(lastBreak + 1)].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    OnMessage(message.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection closed or the terminal is exiting.
        }
    }

    private void OnMessage(string message)
    {
        if (message.Length == 0)
        {
            return;
        }

        var suffix = string.Empty;
        lock (_gate)
        {
            _lastSignal = message;
            _counts[message] = _counts.GetValueOrDefault(message) + 1;

            if (_sinceTrigger.IsRunning && IsVerdict(message))
            {
                suffix = $"   (tetikten {_sinceTrigger.ElapsedMilliseconds} ms sonra)";
                _sinceTrigger.Reset();
            }
        }

        Log.Rx(message, suffix);
    }

    private static bool IsVerdict(string message) =>
        message.Equals("AllOK", StringComparison.OrdinalIgnoreCase) ||
        message.Equals("AllNok", StringComparison.OrdinalIgnoreCase) ||
        message.Equals("Error", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns false when the terminal should exit.</summary>
    public bool HandleCommand(string command)
    {
        if (command.Length == 0)
        {
            return true;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "q" or "quit" or "exit":
                StopAuto();
                return false;

            case "t" or "trigger":
                Send(options.TriggerMessage, restartClock: true);
                return true;

            case "a" or "auto":
                if (parts.Length > 1 && int.TryParse(parts[1], out var interval) && interval >= 50)
                {
                    StartAuto(interval);
                }
                else
                {
                    StopAuto();
                }

                return true;

            case "s" or "status":
                PrintStatus();
                return true;

            default:
                Send(command, restartClock: false);
                return true;
        }
    }

    private void Send(string message, bool restartClock)
    {
        TcpClient? client;
        lock (_gate)
        {
            client = _client;
            if (client is not null && restartClock)
            {
                _sinceTrigger.Restart();
            }
        }

        if (client is null)
        {
            Log.Error("Bağlı değil, gönderilemedi.");
            return;
        }

        try
        {
            var data = Encoding.UTF8.GetBytes(message + options.TerminatorText);
            client.GetStream().Write(data);
            Log.Tx(message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Error($"Gönderilemedi: {ex.Message}");
        }
    }

    private void StartAuto(int intervalMs)
    {
        StopAuto();
        var cts = new CancellationTokenSource();
        _auto = cts;
        Log.Info($"Otomatik tetik: her {intervalMs} ms. Durdurmak için 'a'.");
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Send(options.TriggerMessage, restartClock: true);
                await SafeDelay(intervalMs, cts.Token);
            }
        });
    }

    private void StopAuto()
    {
        if (_auto is null)
        {
            return;
        }

        _auto.Cancel();
        _auto = null;
        Log.Info("Otomatik tetik durdu.");
    }

    private void PrintStatus()
    {
        lock (_gate)
        {
            var counts = _counts.Count == 0
                ? "-"
                : string.Join(", ", _counts.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"));
            Log.Info($"Bağlı: {(_client is not null ? "evet" : "hayır")} | son sinyal: {_lastSignal} | sayaçlar: {counts}");
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: the caller checks the token.
        }
    }
}

static class Log
{
    private static readonly object Gate = new();

    public static void Info(string text) => Write(ConsoleColor.Gray, "···", text);

    public static void Error(string text) => Write(ConsoleColor.Red, "!!!", text);

    public static void Tx(string text) => Write(ConsoleColor.Cyan, "TX→", text);

    public static void Rx(string text, string suffix)
    {
        var color = text.ToLowerInvariant() switch
        {
            "allok" => ConsoleColor.Green,
            "allnok" or "error" => ConsoleColor.Red,
            "busy" => ConsoleColor.Yellow,
            "ready" or "run" => ConsoleColor.DarkGreen,
            _ => ConsoleColor.White,
        };
        Write(color, "←RX", text + suffix);
    }

    private static void Write(ConsoleColor color, string tag, string text)
    {
        lock (Gate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{DateTime.Now:HH:mm:ss.fff} ");
            Console.ForegroundColor = color;
            Console.WriteLine($"{tag} {text}");
            Console.ResetColor();
        }
    }
}

sealed record Options(string Host, int Port, string TriggerMessage, string Terminator)
{
    public bool NoTerminator => Terminator.Equals("none", StringComparison.OrdinalIgnoreCase);

    public string TerminatorText => Terminator.ToUpperInvariant() switch
    {
        "CRLF" => "\r\n",
        "LF" => "\n",
        "CR" => "\r",
        _ => string.Empty,
    };

    public static void PrintUsage() => Console.WriteLine(
        """
        Kullanım: MTCVision.DemoTerminal [--host 127.0.0.1] [--port 5001] [--trigger TriggerRequest] [--terminator CRLF|LF|CR|None]

        Değerler Vision'daki Haberleşme sayfasıyla aynı olmalı.
        """);

    public static Options? Parse(string[] args)
    {
        string host = "127.0.0.1", trigger = "TriggerRequest", terminator = "CRLF";
        var port = 5001;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help" or "/?" || i + 1 >= args.Length)
            {
                return null;
            }

            var name = args[i].ToLowerInvariant();
            var value = args[++i];
            switch (name)
            {
                case "--host": host = value; break;
                case "--port" when int.TryParse(value, out var p): port = p; break;
                case "--trigger": trigger = value; break;
                case "--terminator": terminator = value; break;
                default: return null;
            }
        }

        return terminator.ToUpperInvariant() is "CRLF" or "LF" or "CR" or "NONE"
            ? new Options(host, port, trigger, terminator)
            : null;
    }
}
