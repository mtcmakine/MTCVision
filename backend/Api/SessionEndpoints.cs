using MTCVision.Backend.Inspection;
using MTCVision.Backend.Integration;

namespace MTCVision.Backend.Api;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/session");

        // What the console should reopen on: the mode it was left in and the recipe it was on.
        group.MapGet("", (OperatingMode mode) =>
            Results.Ok(new { mode = mode.Mode, activeRecipeId = mode.ActiveRecipeId }));

        group.MapPut("/mode", async (ModeRequest request, OperatingMode mode, InspectionRunner runner) =>
        {
            var next = request.Mode?.ToLowerInvariant();
            if (next is not (OperatingMode.Run or OperatingMode.Program))
            {
                return Results.Problem("Mod 'run' veya 'program' olmalı.", statusCode: 400);
            }

            var changed = next != mode.Mode;

            // The continuous loop belongs to Run mode; leaving it stops the loop, rather than
            // leaving the camera being inspected behind a screen that says Program.
            if (next == OperatingMode.Program)
            {
                await runner.StopAsync();
            }

            await runner.SetActiveRecipeAsync(request.RecipeId);

            // A mode change starts the tally afresh, the way entering Run on the reference console does.
            if (changed)
            {
                await runner.ClearResultAsync();
            }

            mode.Set(next, request.RecipeId);
            return Results.Ok(new { mode = mode.Mode, activeRecipeId = mode.ActiveRecipeId });
        });
    }

    public sealed record ModeRequest(string? Mode, string? RecipeId);
}
