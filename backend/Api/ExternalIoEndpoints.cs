using MTCVision.Backend.Integration;

namespace MTCVision.Backend.Api;

public sealed record SendRawRequest(string? Text);

public static class ExternalIoEndpoints
{
    public static void MapExternalIoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/external-io");

        // The settings plus what the interface is doing right now: listening or not, who is
        // connected, and why it could not start if it could not.
        group.MapGet("", (ExternalIoServer server) => Results.Ok(server.Snapshot()));

        group.MapPut("", (ExternalIoSettings settings, ExternalIoServer server, ExternalIoSettingsStore store) =>
        {
            var problem = settings.Validate();
            if (problem is not null)
            {
                return Results.Problem(problem, statusCode: 400);
            }

            store.Write(settings);
            server.Apply(settings);

            // A port that could not be opened is reported in the status, not as a failed request:
            // the settings themselves were valid and are saved.
            return Results.Ok(server.Snapshot());
        });

        // Sends one signal's message to whoever is connected, so a controller's wiring can be
        // checked without having to provoke a real inspection.
        group.MapPost("/test/{signal}", (string signal, ExternalIoServer server) =>
        {
            if (!Enum.TryParse<ExternalSignal>(signal, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.Problem($"Bilinmeyen sinyal: '{signal}'.", statusCode: 400);
            }

            return Results.Ok(new { sentTo = server.SendTest(parsed) });
        });

        // The console: what crossed the interface after `after` (0 for all that is still held).
        // Polled with the last sequence number seen, so each poll carries only what is new.
        group.MapGet("/log", (long? after, ExternalIoLog log) => Results.Ok(log.Since(after ?? 0)));

        // Sends text typed into the console to whoever is connected, as-is, plus the terminator.
        group.MapPost("/send", (SendRawRequest request, ExternalIoServer server) =>
        {
            try
            {
                return Results.Ok(new { sentTo = server.SendRaw(request.Text ?? string.Empty) });
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });
    }
}
