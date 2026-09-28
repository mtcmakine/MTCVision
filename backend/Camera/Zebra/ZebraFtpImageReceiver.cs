using FubarDev.FtpServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// Runs the embedded FTP server Aurora Focus pushes captured images into, and turns each arriving
/// .jpg into a <see cref="CameraFrame"/>.
///
/// Aurora Focus is the FTP *client* here (its job's "Connect &gt; Save Image &gt; FTP" output), so
/// MTCVision has to be the server side. The upload itself is detected with a
/// <see cref="FileSystemWatcher"/> rather than any FubarDev-internal hook: that way image pickup
/// works the same regardless of which FTP command sequence a given client used to write the file.
/// </summary>
public sealed class ZebraFtpImageReceiver : IHostedService, IZebraImageSource, IDisposable
{
    private readonly IFtpServerHost _ftpServerHost;
    private readonly DataPaths _dataPaths;
    private readonly ILogger<ZebraFtpImageReceiver> _logger;
    private FileSystemWatcher? _watcher;

    public ZebraFtpImageReceiver(
        IFtpServerHost ftpServerHost,
        DataPaths dataPaths,
        ILogger<ZebraFtpImageReceiver> logger)
    {
        _ftpServerHost = ftpServerHost;
        _dataPaths = dataPaths;
        _logger = logger;
    }

    public event EventHandler<CameraFrame>? ImageReceived;
    public event EventHandler<string>? Status;

    // The FTP server runs for the life of the host (see StartAsync), whichever camera is connected.
    public void Start(string deviceIp)
    {
    }

    public void Stop()
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _ftpServerHost.StartAsync(cancellationToken);

        _watcher = new FileSystemWatcher(_dataPaths.ZebraIncoming, "*.jpg")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _watcher.Created += (_, e) => _ = HandleArrivedAsync(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        Status?.Invoke(this, $"Zebra FTP sunucusu dinliyor: klasör {_dataPaths.ZebraIncoming}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        await _ftpServerHost.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Aurora Focus is still writing the file when <see cref="FileSystemWatcher"/> reports it as
    /// created, so this waits until the file can be opened exclusively (the FTP server has closed
    /// its own handle) before reading it.
    /// </summary>
    private async Task HandleArrivedAsync(string path)
    {
        try
        {
            byte[]? bytes = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    bytes = await File.ReadAllBytesAsync(path);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }

            if (bytes is null)
            {
                Status?.Invoke(this, $"'{path}' 3 saniye içinde okunamadı, atlanıyor.");
                return;
            }

            var frame = ZebraFrameDecoder.Decode(bytes);
            if (frame is null)
            {
                Status?.Invoke(this, $"'{path}' bir JPEG olarak çözülemedi.");
                return;
            }

            ImageReceived?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zebra FTP görüntüsü işlenemedi: {Path}", path);
            Status?.Invoke(this, $"'{path}' işlenemedi: {ex.Message}");
        }
        finally
        {
            TryDelete(path);
            TryDelete(Path.ChangeExtension(path, ".json"));
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // An unattended line must not stop over a file it could not clean up; the next arrival
            // still gets processed, and a leftover .jpg here is harmless.
            _logger.LogWarning(ex, "Zebra FTP dosyası silinemedi: {Path}", path);
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
