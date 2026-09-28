using MTCVision.Backend.Camera;
using OpenCvSharp;

namespace MTCVision.Backend.Recipes;

/// <summary>
/// The registered picture of a good part, one per recipe.
///
/// Kept as a file beside the recipe rather than inside it: a recipe is rewritten on every edit and
/// a frame is megabytes. PNG rather than JPEG because this image is measured against, and JPEG
/// ringing around a laser mark is exactly the kind of difference an inspection is looking for.
/// </summary>
public sealed class MasterImageStore(DataPaths paths)
{
    private readonly object _gate = new();

    /// <summary>The decoded master, cached so a running line does not read it off disk per frame.</summary>
    private readonly Dictionary<string, CachedMaster> _cache = [];

    public string PathFor(string recipeId) => Path.Combine(paths.Masters, $"{RecipeStore.SanitizeId(recipeId)}.png");

    public bool Exists(string recipeId) => File.Exists(PathFor(recipeId));

    public byte[]? ReadBytes(string recipeId)
    {
        var path = PathFor(recipeId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// The master as greyscale pixels. The returned Mat belongs to the cache and must not be
    /// disposed by the caller - it is shared by every frame that measures against it.
    /// </summary>
    public Mat? Load(string recipeId)
    {
        var path = PathFor(recipeId);
        if (!File.Exists(path))
        {
            Forget(recipeId);
            return null;
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_cache.TryGetValue(recipeId, out var cached) && cached.WrittenUtc == stamp)
            {
                return cached.Image;
            }

            var image = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (image.Empty())
            {
                image.Dispose();
                return null;
            }

            if (_cache.Remove(recipeId, out var stale))
            {
                stale.Image.Dispose();
            }

            _cache[recipeId] = new CachedMaster(image, stamp);
            return image;
        }
    }

    public MasterImage Save(string recipeId, Mat image, string source)
    {
        var path = PathFor(recipeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the target and moved into place, so a half-written master can never be
        // what a running line measures against. The temporary name keeps the .png extension:
        // OpenCV picks its encoder from the extension and refuses to write anything else.
        var temporary = Path.ChangeExtension(path, ".tmp.png");
        if (!Cv2.ImWrite(temporary, image))
        {
            throw new IOException($"Master görüntü yazılamadı: {path}");
        }

        File.Move(temporary, path, overwrite: true);
        Forget(recipeId);

        return new MasterImage(DateTime.UtcNow, image.Width, image.Height, source);
    }

    public MasterImage SaveFrom(string recipeId, CameraFrame frame, string source)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);
        return Save(recipeId, image, source);
    }

    public bool Delete(string recipeId)
    {
        var path = PathFor(recipeId);
        Forget(recipeId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private void Forget(string recipeId)
    {
        lock (_gate)
        {
            if (_cache.Remove(recipeId, out var cached))
            {
                cached.Image.Dispose();
            }
        }
    }

    private sealed record CachedMaster(Mat Image, DateTime WrittenUtc);
}
