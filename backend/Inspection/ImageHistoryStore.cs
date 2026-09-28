using System.Text.Json;
using System.Text.Json.Serialization;
using MTCVision.Backend;
using MTCVision.Backend.Camera;

namespace MTCVision.Backend.Inspection;

/// <summary>
/// The saved-image history: JPEGs on disk with a metadata sidecar, capped at a maximum count and
/// discarding the oldest once full.
///
/// A ring rather than an ever-growing folder because this runs unattended on a line: 1000 full-frame
/// JPEGs is about 1 GB, and an operator who comes back after a week should find the most recent
/// images rather than a full disk. The folder and the cap are settings, because where the images go
/// depends on the machine, not on the software.
/// </summary>
public sealed class ImageHistoryStore
{
    public const int MaxCapacity = 10000;
    public const int DefaultCapacity = 1000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ImageHistorySettings _settings;

    public ImageHistoryStore(DataPaths paths)
    {
        var defaultDirectory = paths.Images;
        _settingsPath = paths.File("image-history.json");

        // Settings outlive the process: a line configured to keep only rejects should still be
        // doing that after a restart, without anyone reconfiguring it.
        _settings = LoadSettings() ?? new ImageHistorySettings(defaultDirectory, DefaultCapacity, ImageSaveMode.Manual);
        Directory.CreateDirectory(_settings.Directory);
    }

    private ImageHistorySettings? LoadSettings()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<ImageHistorySettings>(File.ReadAllText(_settingsPath), SerializerOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"[Images] Ayarlar okunamadı, varsayılana dönülüyor: {ex.Message}");
            return null;
        }
    }

    private void PersistSettings()
    {
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, SerializerOptions));
        }
        catch (IOException ex)
        {
            // Not being able to remember the setting must not stop it taking effect now.
            Console.WriteLine($"[Images] Ayarlar kaydedilemedi: {ex.Message}");
        }
    }

    public ImageHistorySettings Settings => _settings;

    public ImageHistorySettings UpdateSettings(ImageHistorySettingsUpdate update)
    {
        var directory = update.Directory is null ? _settings.Directory : ResolveDirectory(update.Directory);
        var capacity = update.Capacity is null
            ? _settings.Capacity
            : Math.Clamp(update.Capacity.Value, 1, MaxCapacity);

        Directory.CreateDirectory(directory);
        _settings = new ImageHistorySettings(directory, capacity, update.Mode ?? _settings.Mode);
        PersistSettings();

        Prune();
        return _settings;
    }

    /// <summary>
    /// Decides whether a finished inspection should be kept, according to the configured mode.
    /// Manual means only an explicit save writes anything.
    /// </summary>
    public bool ShouldSave(bool passed) => _settings.Mode switch
    {
        ImageSaveMode.All => true,
        ImageSaveMode.FailuresOnly => !passed,
        _ => false,
    };

    public async Task<SavedImage> SaveAsync(CameraFrame frame, SavedImageContext context)
    {
        var jpeg = FrameJpegEncoder.Encode(frame, quality: 90);

        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_settings.Directory);

            // Sortable and unique: history is always read newest-first, and two inspections in the
            // same millisecond would otherwise collide.
            var id = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}";
            var metadata = new SavedImage(
                Id: id,
                SavedAtUtc: DateTime.UtcNow,
                Width: frame.Width,
                Height: frame.Height,
                SizeBytes: jpeg.Length,
                Verdict: context.Verdict,
                RecipeId: context.RecipeId,
                RecipeName: context.RecipeName,
                Note: context.Note);

            await File.WriteAllBytesAsync(ImagePath(id), jpeg);
            await File.WriteAllTextAsync(MetadataPath(id), JsonSerializer.Serialize(metadata, SerializerOptions));

            Prune();
            return metadata;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<SavedImage> List()
    {
        if (!Directory.Exists(_settings.Directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_settings.Directory, "*.json")
            .Select(ReadMetadata)
            .OfType<SavedImage>()
            .OrderByDescending(image => image.Id, StringComparer.Ordinal)
            .ToList();
    }

    public byte[]? ReadImage(string id)
    {
        var path = ImagePath(SanitizeId(id));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool Delete(string id)
    {
        var safeId = SanitizeId(id);
        var image = ImagePath(safeId);
        if (!File.Exists(image))
        {
            return false;
        }

        File.Delete(image);
        var metadata = MetadataPath(safeId);
        if (File.Exists(metadata))
        {
            File.Delete(metadata);
        }

        return true;
    }

    public int Clear()
    {
        var deleted = 0;
        foreach (var image in List())
        {
            if (Delete(image.Id))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>Drops the oldest images until the folder is back within its cap.</summary>
    private void Prune()
    {
        var images = List();
        foreach (var stale in images.Skip(_settings.Capacity))
        {
            Delete(stale.Id);
        }
    }

    private static SavedImage? ReadMetadata(string path)
    {
        try
        {
            var image = JsonSerializer.Deserialize<SavedImage>(File.ReadAllText(path), SerializerOptions);

            // A metadata file whose image is gone would show as a broken thumbnail forever.
            return image is not null && File.Exists(Path.ChangeExtension(path, ".jpg")) ? image : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private string ImagePath(string id) => Path.Combine(_settings.Directory, $"{id}.jpg");

    private string MetadataPath(string id) => Path.Combine(_settings.Directory, $"{id}.json");

    /// <summary>
    /// Ids become file names, so anything that could escape the image folder is rejected rather
    /// than rewritten.
    /// </summary>
    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > 64
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Geçersiz görüntü kimliği.", nameof(id));
        }

        return id;
    }

    /// <summary>
    /// A save folder comes from the operator, so it has to be a real, absolute, writable path
    /// before anything is written to it.
    /// </summary>
    private static string ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Kayıt klasörü boş olamaz.", nameof(directory));
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{directory}' geçerli bir klasör yolu değil.", nameof(directory));
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"'{full}' klasörü oluşturulamadı: {ex.Message}", nameof(directory));
        }

        return full;
    }
}

public sealed record ImageHistorySettings(string Directory, int Capacity, ImageSaveMode Mode);

public sealed record ImageHistorySettingsUpdate(string? Directory = null, int? Capacity = null, ImageSaveMode? Mode = null);

public enum ImageSaveMode
{
    /// <summary>Nothing is written unless the operator asks for it.</summary>
    Manual,

    /// <summary>Every inspection is kept — useful while proving a recipe out.</summary>
    All,

    /// <summary>Only rejects are kept, which is what a running line normally wants.</summary>
    FailuresOnly,
}

public sealed record SavedImage(
    string Id,
    DateTime SavedAtUtc,
    int Width,
    int Height,
    long SizeBytes,
    /// <summary>"OK", "NG", or null when the image was saved outside an inspection.</summary>
    string? Verdict,
    string? RecipeId,
    string? RecipeName,
    string? Note);

public sealed record SavedImageContext(string? Verdict = null, string? RecipeId = null, string? RecipeName = null, string? Note = null);
