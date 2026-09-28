using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;
using MTCVision.Backend.Recipes;
using MTCVision.Backend.Vision;

namespace MTCVision.Backend.Api;

public static class InspectionEndpoints
{
    public static void MapInspectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/inspection");

        // Judges the newest frame against a stored recipe.
        group.MapPost("/run/{recipeId}", async (
            string recipeId,
            RecipeStore store,
            LatestFrameStore frames,
            ImageHistoryStore images,
            InspectionEngine engine) =>
        {
            var recipe = await store.GetAsync(recipeId);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            try
            {
                var frame = frames.Require();
                var result = engine.Run(recipe, frame);
                await ArchiveAsync(images, frame, recipe, result);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // The production shape: trigger a frame and judge that exact frame, rather than whichever
        // free-running frame happened to be newest when the request arrived.
        group.MapPost("/trigger-run/{recipeId}", async (
            string recipeId,
            RecipeStore store,
            ICameraService cameraService,
            ImageHistoryStore images,
            InspectionEngine engine) =>
        {
            var recipe = await store.GetAsync(recipeId);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            try
            {
                var frame = await cameraService.TriggerAsync();
                var result = engine.Run(recipe, frame);
                await ArchiveAsync(images, frame, recipe, result);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // One inspection through the server's runner: trigger, judge, count. Used by the operator's
        // "single inspection" so its result and tally are the same ones a trigger arriving over
        // TCP/IP produces, and both show up on every screen watching the run.
        group.MapPost("/run-once/{recipeId}", async (string recipeId, InspectionRunner runner) =>
        {
            try
            {
                return Results.Ok(await runner.RunOnceAsync(recipeId));
            }
            catch (Exception ex) when (ex is InspectionBusyException or CameraConfigurationException
                                          or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // The language packs installed, so the recipe editor can only offer scripts that will
        // actually load when the line is running.
        group.MapGet("/ocr-languages", (OcrEngine ocr) => Results.Ok(new
        {
            languages = ocr.Languages.Select(language => new
            {
                language.Id,
                language.Name,
                language.Description,
                language.IsDefault,
            }),
            error = ocr.CatalogueError,
        }));

        // The continuous run lives on the server, so closing the browser does not stop the line.
        group.MapGet("/run", (InspectionRunner runner) => Results.Ok(runner.GetState()));

        group.MapPost("/run/start/{recipeId}", async (string recipeId, InspectionRunner runner) =>
        {
            try
            {
                return Results.Ok(await runner.StartAsync(recipeId));
            }
            catch (CameraConfigurationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapPost("/run/stop", async (InspectionRunner runner) => Results.Ok(await runner.StopAsync()));

        group.MapPost("/run/reset-statistics", async (InspectionRunner runner) =>
        {
            await runner.ResetStatisticsAsync();
            return Results.Ok(runner.GetState());
        });

        // Measures every window on one frame at once. All windows must sample the same picture, so
        // teaching a part cannot be a loop of separate /measure calls.
        group.MapPost("/sample", (SampleRequest request, LatestFrameStore frames, InspectionEngine engine) =>
        {
            try
            {
                var frame = frames.Require();
                var readings = request.Windows
                    .Select(window => new
                    {
                        windowId = window.Id,
                        value = engine.Measure(
                            window.Region, window.Tool, frame, window.ExpectedText, true, window.OcrLanguage).Value,
                    })
                    .ToList();

                return Results.Ok(new { capturedUtc = DateTime.UtcNow, isGood = request.IsGood, readings });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Turns taught samples into limits, and says so when they cannot be separated.
        group.MapPost("/auto-limits", (AutoLimitsRequest request) =>
            Results.Ok(ToolAutoTuner.Compute(request.Samples)));

        // Measures a region on the newest frame without judging it — this is what turns "draw a box
        // on a known-good part" into the numbers its limits should be set from.
        group.MapPost("/measure", (MeasureRequest request, LatestFrameStore frames, InspectionEngine engine) =>
        {
            try
            {
                return Results.Ok(engine.Measure(
                    request.Region, request.Tool, frames.Require(),
                    request.ExpectedText, request.IgnoreDiacritics, request.OcrLanguage, request.Mask,
                    request.Options, request.RecipeId));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });
    }

    public sealed record MeasureRequest(
        Roi Region,
        InspectionToolKind Tool = InspectionToolKind.Ocr,
        string? ExpectedText = null,
        bool IgnoreDiacritics = false,
        IReadOnlyList<Roi>? Mask = null,
        ToolOptions? Options = null,
        /// <summary>Which recipe's master to compare against, for the tools that need one.</summary>
        string? RecipeId = null,
        string? OcrLanguage = null);

    public sealed record SampleWindow(
        string Id,
        Roi Region,
        InspectionToolKind Tool,
        string? ExpectedText = null,
        string? OcrLanguage = null);

    public sealed record SampleRequest(IReadOnlyList<SampleWindow> Windows, bool IsGood);

    public sealed record AutoLimitsRequest(IReadOnlyList<ToolSample> Samples);

    /// <summary>
    /// Keeps the inspected frame if the image history is configured to. Failures here are swallowed
    /// on purpose: a full or unwritable image folder must not turn a good inspection into an error.
    /// </summary>
    private static async Task ArchiveAsync(
        ImageHistoryStore images,
        CameraFrame frame,
        Recipe recipe,
        InspectionResult result)
    {
        if (!images.ShouldSave(result.Passed))
        {
            return;
        }

        try
        {
            await images.SaveAsync(frame, new SavedImageContext(
                Verdict: result.Passed ? "OK" : "NG",
                RecipeId: recipe.Id,
                RecipeName: recipe.Name));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Images] Kayıt başarısız: {ex.Message}");
        }
    }
}
