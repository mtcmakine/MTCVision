namespace MTCVision.Backend.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            service = "MTCVision.Backend",
            timeUtc = DateTime.UtcNow
        }));
    }
}
