using MTCVision.Backend;
using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;
using MTCVision.Backend.Recipes;

namespace MTCVision.Backend.Api;

public static class CameraEndpoints
{
    public static void MapCameraEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/camera");

        group.MapGet("/network-interfaces", () => Results.Ok(LocalNetworkInterfaces.List()));

        // Which kind of camera the console talks to. Chosen by the operator rather than fixed in a
        // config file, because one machine may be moved between a GigE Vision camera and the Zebra.
        group.MapGet("/backend", (SwitchableCameraService camera) =>
            Results.Ok(new { current = camera.Current.ToString(), backends = SwitchableCameraService.Available }));

        group.MapPut("/backend", async (BackendRequest request, SwitchableCameraService camera, InspectionRunner runner, LatestFrameStore frames) =>
        {
            if (!Enum.TryParse<CameraBackendKind>(request.Backend, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                return Results.Problem($"Bilinmeyen kamera tipi: '{request.Backend}'.", statusCode: 400);
            }

            // Switching drops the connection, so a run in progress would start failing every cycle.
            if (kind != camera.Current && runner.IsRunning)
            {
                return Results.Problem(
                    "Çalışan bir denetim varken kamera tipi değiştirilemez. Önce denetimi durdurun.",
                    statusCode: 409);
            }

            try
            {
                await camera.SwitchToAsync(kind);

                // A frame from the other camera is not the newest image any more.
                frames.Clear();
                return Results.Ok(new { current = camera.Current.ToString() });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        // localIp is optional: without it every IPv4 interface is scanned, and each camera comes
        // back tagged with the interface that can actually stream from it.
        group.MapGet("/discover", async (string? localIp, ICameraService cameraService) =>
        {
            try
            {
                return Results.Ok(await cameraService.DiscoverAsync(localIp));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapPost("/connect", async (ConnectRequest request, ICameraService cameraService, SessionStore sessions) =>
        {
            Console.WriteLine($"[Camera] /connect requested at {DateTime.Now:HH:mm:ss.fff}");
            try
            {
                await cameraService.ConnectAsync(request.CameraIp, request.LocalIp);

                // Remembered so a restart reconnects on its own instead of waiting for someone to
                // open the console.
                sessions.Update(state => state with { CameraIp = request.CameraIp, LocalIp = request.LocalIp });
                return Results.Ok(new { connected = true, cameraIp = request.CameraIp });
            }
            catch (CameraConfigurationException ex)
            {
                // Rejected before touching the camera: the request itself cannot work, e.g. an
                // interface that has no route to the camera's subnet.
                Console.WriteLine($"[Camera] Connect rejected: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera] Connect failed: {ex}");
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        group.MapPost("/disconnect", async (ICameraService cameraService, SessionStore sessions) =>
        {
            Console.WriteLine($"[Camera] /disconnect requested at {DateTime.Now:HH:mm:ss.fff}");
            await cameraService.DisconnectAsync();

            // Disconnecting is deliberate: a restart should not go looking for the camera again.
            sessions.Update(state => state with { CameraIp = null, LocalIp = null, RunningRecipeId = null });
            return Results.Ok(new { connected = false });
        });

        // The current frame as a JPEG, for teaching a recipe against a still rather than a moving
        // preview, and for anything that cannot hold a WebSocket open.
        group.MapGet("/snapshot", (LatestFrameStore frames) =>
        {
            var frame = frames.Latest;
            return frame is null
                ? Results.Problem("Henüz kameradan görüntü alınmadı.", statusCode: 409)
                : Results.File(FrameJpegEncoder.Encode(frame, quality: 92), "image/jpeg");
        });

        group.MapGet("/settings", async (ICameraService cameraService) =>
        {
            try
            {
                return Results.Ok(await cameraService.GetSettingsAsync());
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapPut("/settings", async (CameraSettingsUpdate update, ICameraService cameraService) =>
        {
            try
            {
                await cameraService.ApplySettingsAsync(update);
                return Results.Ok(await cameraService.GetSettingsAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera] Settings update failed: {ex}");
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Fires one software trigger and reports the frame it produced. In trigger mode the camera
        // is otherwise idle, so this is how a frame is obtained at all.
        group.MapPost("/trigger", async (ICameraService cameraService) =>
        {
            try
            {
                var frame = await cameraService.TriggerAsync();
                return Results.Ok(new
                {
                    frame.Width,
                    frame.Height,
                    pixelFormat = frame.PixelFormat.ToString(),
                    frame.TimestampUtc,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // The reference console's "Automatic Brightness Adjustment": let the camera regulate for a
        // moment, then freeze what it found.
        group.MapPost("/auto-brightness", async (AutoBrightnessRequest request, AutoBrightnessTuner tuner) =>
        {
            try
            {
                return Results.Ok(await tuner.RunAsync(request.Target, request.MinFrameRateHz));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Focus assist: a number to maximise while turning the lens ring.
        group.MapPost("/focus", (FocusRequest request, LatestFrameStore frames) =>
        {
            try
            {
                return Results.Ok(ImageStatistics.Focus(frames.Require(), request.Region));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapGet("/identity", async (ICameraService cameraService) =>
        {
            try
            {
                return Results.Ok(await cameraService.GetIdentityAsync());
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapPut("/identity", async (CameraIdentityUpdate update, ICameraService cameraService) =>
        {
            try
            {
                await cameraService.UpdateIdentityAsync(update);
                var identity = await cameraService.GetIdentityAsync();

                // Address changes only take hold at the camera's next boot, and saying so is the
                // difference between "it did not work" and "power-cycle the camera".
                var addressChanged = update.PersistentIp is not null
                    || update.PersistentSubnetMask is not null
                    || update.PersistentGateway is not null
                    || update.IpConfiguration is not null;

                return Results.Ok(new
                {
                    identity,
                    rebootRequired = addressChanged,
                    message = addressChanged
                        ? "Ağ ayarları kameraya yazıldı. Yeni adres kamera yeniden başlatıldığında geçerli olur."
                        : null,
                });
            }
            catch (CameraConfigurationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Persists the current settings inside the camera so they survive a power cycle.
        group.MapPost("/save-to-camera", async (ICameraService cameraService) =>
        {
            try
            {
                await cameraService.SaveToCameraAsync();
                return Results.Ok(new { saved = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Escape hatch for tuning against the camera's full GenICam tree.
        group.MapGet("/feature/{name}", async (string name, ICameraService cameraService) =>
        {
            try
            {
                return Results.Ok(new { name, value = await cameraService.GetFeatureAsync(name) });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapPut("/feature/{name}", async (string name, FeatureValue body, ICameraService cameraService) =>
        {
            try
            {
                await cameraService.SetFeatureAsync(name, body.Value);
                return Results.Ok(new { name, value = await cameraService.GetFeatureAsync(name) });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Includes the device's own identity so a client that reconnects can restore its display
        // without asking the operator to pick the camera again.
        group.MapGet("/status", async (ICameraService cameraService) =>
        {
            CameraIdentity? identity = null;
            if (cameraService.IsConnected)
            {
                try
                {
                    identity = await cameraService.GetIdentityAsync();
                }
                catch (Exception)
                {
                    // A camera that is connected but momentarily unreadable still counts as connected.
                }
            }

            return Results.Ok(new
            {
                connected = cameraService.IsConnected,
                cameraIp = cameraService.ConnectedIp,
                statistics = cameraService.Statistics,
                device = identity,
            });
        });
    }

    public sealed record ConnectRequest(string CameraIp, string LocalIp);

    public sealed record BackendRequest(string Backend);

    public sealed record FeatureValue(double Value);

    /// <summary>
    /// Target mean grey level, 0-255 (the reference console aims for a mid-grey), and the slowest
    /// frame rate the adjustment may fall back to in order to reach it.
    /// </summary>
    public sealed record AutoBrightnessRequest(int Target = 110, double MinFrameRateHz = 1.0);

    public sealed record FocusRequest(Roi? Region = null);
}
