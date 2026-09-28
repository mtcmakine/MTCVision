using MTCVision.Backend.Camera;

namespace MTCVision.Backend.Api;

public static class LiveStreamEndpoints
{
    public static void MapLiveStreamEndpoints(this WebApplication app)
    {
        app.Map("/ws/live", async (HttpContext context, LiveFrameBroadcaster broadcaster) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await broadcaster.HandleClientAsync(socket, context.RequestAborted);
        });
    }
}
