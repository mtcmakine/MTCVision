using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection;
using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Api;

public static class RecipeEndpoints
{
    public static void MapRecipeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/recipes");

        group.MapGet("/", async (RecipeStore store) => Results.Ok(await store.ListAsync()));

        group.MapGet("/{id}", async (string id, RecipeStore store) =>
        {
            var recipe = await store.GetAsync(id);
            return recipe is null ? Results.NotFound() : Results.Ok(recipe);
        });

        group.MapPost("/", async (SaveRecipeRequest request, RecipeStore store) =>
        {
            try
            {
                var recipe = request.ToRecipe(RecipeStore.NewIdFor(request.Name));
                return Results.Ok(await store.SaveAsync(recipe));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        group.MapPut("/{id}", async (string id, SaveRecipeRequest request, RecipeStore store) =>
        {
            try
            {
                var existing = await store.GetAsync(id);
                if (existing is null)
                {
                    return Results.NotFound();
                }

                // Creation time belongs to the recipe's history, not to the caller's payload.
                // The master is registered through its own endpoint, so an ordinary save must not
                // drop it just because the editor does not send it back.
                var updated = request.ToRecipe(id) with
                {
                    CreatedUtc = existing.CreatedUtc,
                    Master = existing.Master,
                };
                return Results.Ok(await store.SaveAsync(updated));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
        });

        group.MapDelete("/{id}", async (string id, RecipeStore store, MasterImageStore masters) =>
        {
            if (!await store.DeleteAsync(id))
            {
                return Results.NotFound();
            }

            // The master belongs to the recipe; leaving it behind would attach itself to the next
            // recipe that happened to be given the same id.
            masters.Delete(id);
            return Results.Ok(new { deleted = true });
        });

        // Registers what a good part looks like. Everything that follows the part around the frame
        // is measured against this image, so it is taken under the recipe's own camera conditions.
        group.MapPost("/{id}/master", async (
            string id,
            RecipeStore store,
            MasterImageStore masters,
            LatestFrameStore frames) =>
        {
            var recipe = await store.GetAsync(id);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            try
            {
                var master = masters.SaveFrom(id, frames.Require(), "live");
                return Results.Ok(await store.SaveAsync(recipe with { Master = master }));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // From a stored frame instead of the live one, so a master can be registered from a part
        // that has already gone down the line.
        group.MapPost("/{id}/master/from-image/{imageId}", async (
            string id,
            string imageId,
            RecipeStore store,
            MasterImageStore masters,
            ImageHistoryStore images) =>
        {
            var recipe = await store.GetAsync(id);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            var bytes = images.ReadImage(imageId);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            try
            {
                using var image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                if (image.Empty())
                {
                    return Results.Problem("Kayıtlı görüntü çözülemedi.", statusCode: 409);
                }

                var master = masters.Save(id, image, $"image:{imageId}");
                return Results.Ok(await store.SaveAsync(recipe with { Master = master }));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        group.MapGet("/{id}/master", (string id, MasterImageStore masters) =>
        {
            var bytes = masters.ReadBytes(id);
            return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
        });

        group.MapDelete("/{id}/master", async (string id, RecipeStore store, MasterImageStore masters) =>
        {
            var recipe = await store.GetAsync(id);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            masters.Delete(id);
            return Results.Ok(await store.SaveAsync(recipe with { Master = null }));
        });

        // Pushes the recipe's taught camera conditions back onto the camera. Measurements only mean
        // what the recipe says they mean under the exposure and gain they were taught with.
        group.MapPost("/{id}/apply", async (string id, RecipeStore store, ICameraService cameraService) =>
        {
            var recipe = await store.GetAsync(id);
            if (recipe is null)
            {
                return Results.NotFound();
            }

            try
            {
                await cameraService.ApplySettingsAsync(new CameraSettingsUpdate(
                    ExposureTimeUs: recipe.Camera.ExposureTimeUs,
                    Gain: recipe.Camera.Gain,
                    FrameRateHz: recipe.Camera.FrameRateHz));

                return Results.Ok(await cameraService.GetSettingsAsync());
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });
    }

    /// <summary>
    /// The editable shape of a recipe. Id and timestamps are the store's to assign, so they are not
    /// part of what a caller sends.
    /// </summary>
    public sealed record SaveRecipeRequest(
        string Name,
        RecipeCameraSettings? Camera,
        IReadOnlyList<InspectionWindow>? Windows,
        PositionAdjustment? Position = null,
        OutputAssignment? Output = null)
    {
        public Recipe ToRecipe(string id)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new ArgumentException("Reçete adı boş olamaz.", nameof(Name));
            }

            var windows = Windows ?? [];
            foreach (var window in windows)
            {
                if (!window.Region.IsValid)
                {
                    throw new ArgumentException($"'{window.Name}' penceresinin alanı geçersiz.", nameof(Windows));
                }

                if (window.LowerLimit > window.UpperLimit)
                {
                    throw new ArgumentException(
                        $"'{window.Name}' penceresinde alt limit üst limitten büyük.", nameof(Windows));
                }
            }

            if (Position is { Enabled: true } position && position.Pattern is null)
            {
                throw new ArgumentException("Konum düzeltme açık ama desen seçilmemiş.", nameof(Position));
            }

            return new Recipe
            {
                Id = RecipeStore.SanitizeId(id),
                Name = Name.Trim(),
                Camera = Camera ?? new RecipeCameraSettings(),
                Windows = windows,
                Position = Position ?? new PositionAdjustment(),
                Output = Output ?? new OutputAssignment(),
            };
        }
    }
}
