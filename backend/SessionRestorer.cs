using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;

namespace MTCVision.Backend;

/// <summary>
/// Puts the server back where it was after a restart: reconnects the camera and, if a run was
/// going, starts it again.
///
/// Runs in the background rather than blocking startup, so the console is reachable while the
/// camera is still being found — an operator whose camera is unplugged should still get a working
/// screen that tells them so, not a server that never finishes starting.
/// </summary>
public sealed class SessionRestorer(
    SessionStore sessions,
    ICameraService camera,
    InspectionRunner runner,
    ILogger<SessionRestorer> logger) : BackgroundService
{
    /// <summary>
    /// A camera that was on the same power circuit as this machine is still booting when the
    /// service starts, so the first attempt is expected to fail.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var state = sessions.Read();

        // Independent of the camera: single inspections and triggers from outside are judged
        // against the recipe the console was left on.
        await runner.SetActiveRecipeAsync(state.ActiveRecipeId);

        if (state.CameraIp is null || state.LocalIp is null)
        {
            return;
        }

        logger.LogInformation("Önceki oturum geri yükleniyor: kamera {CameraIp}, reçete {RecipeId}", state.CameraIp, state.RunningRecipeId ?? "(yok)");

        foreach (var delay in RetryDelays)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(delay, stoppingToken);

            try
            {
                await camera.ConnectAsync(state.CameraIp, state.LocalIp, stoppingToken);
                logger.LogInformation("Kameraya yeniden bağlanıldı: {CameraIp}", state.CameraIp);
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Yeniden bağlanma denemesi başarısız: {Reason}", ex.Message);
            }
        }

        if (state.RunningRecipeId is null)
        {
            return;
        }

        if (!camera.IsConnected)
        {
            logger.LogWarning("Kamera bağlanamadığı için denetim yeniden başlatılmadı.");
            return;
        }

        try
        {
            // Give the stream a moment to deliver its first frame; a free-running recipe judges the
            // newest frame and there is not one yet.
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await runner.StartAsync(state.RunningRecipeId);
            logger.LogInformation("Denetim yeniden başlatıldı: {RecipeId}", state.RunningRecipeId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Denetim yeniden başlatılamadı.");
        }
    }
}
