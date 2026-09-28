using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;
using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Api;

public static class ImageHistoryEndpoints
{
    public static void MapImageHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/images");

        group.MapGet("/", (ImageHistoryStore store) => Results.Ok(new
        {
            settings = store.Settings,
            images = store.List(),
        }));

        group.MapGet("/settings", (ImageHistoryStore store) => Results.Ok(store.Settings));

        group.MapPut("/settings", (ImageHistorySettingsUpdate update, ImageHistoryStore store) =>
        {
            try
            {
                return Results.Ok(store.UpdateSettings(update));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        group.MapGet("/{id}", (string id, ImageHistoryStore store) =>
        {
            try
            {
                var jpeg = store.ReadImage(id);
                return jpeg is null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        // Saves whatever the camera has right now, outside any inspection.
        group.MapPost("/", async (SaveImageRequest? request, ImageHistoryStore store, LatestFrameStore frames) =>
        {
            try
            {
                var saved = await store.SaveAsync(frames.Require(), new SavedImageContext(Note: request?.Note));
                return Results.Ok(saved);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapDelete("/{id}", (string id, ImageHistoryStore store) =>
        {
            try
            {
                return store.Delete(id) ? Results.Ok(new { deleted = true }) : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        group.MapDelete("/", (ImageHistoryStore store) => Results.Ok(new { deleted = store.Clear() }));

        // Re-judges a stored image against a recipe — how an operator checks whether an edited
        // limit would have passed the part that was rejected an hour ago.
        group.MapPost("/{id}/inspect/{recipeId}", async (
            string id,
            string recipeId,
            ImageHistoryStore store,
            RecipeStore recipes,
            InspectionEngine engine) =>
        {
            var recipe = await recipes.GetAsync(recipeId);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            try
            {
                var jpeg = store.ReadImage(id);
                if (jpeg is null)
                {
                    return Results.NotFound();
                }

                using var image = Cv2.ImDecode(jpeg, ImreadModes.Grayscale);
                if (image.Empty())
                {
                    return Results.Problem("Kayıtlı görüntü çözülemedi.", statusCode: 409);
                }

                return Results.Ok(engine.Run(recipe, image, DateTime.UtcNow));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });
    }

    public sealed record SaveImageRequest(string? Note = null);
}
