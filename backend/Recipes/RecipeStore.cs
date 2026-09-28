using MTCVision.Backend;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MTCVision.Backend.Recipes;

/// <summary>
/// Recipes on disk, one JSON file each under Data/Recipes.
///
/// A file per recipe rather than one index file: recipes are edited one at a time on a machine that
/// can lose power mid-shift, and a corrupt write should cost one recipe rather than all of them.
/// </summary>
public sealed class RecipeStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RecipeStore(DataPaths paths)
    {
        _directory = paths.Recipes;
    }

    public async Task<IReadOnlyList<Recipe>> ListAsync()
    {
        var recipes = new List<Recipe>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var recipe = await ReadFileAsync(path);
            if (recipe is not null)
            {
                recipes.Add(recipe);
            }
        }

        return recipes.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Recipe?> GetAsync(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? await ReadFileAsync(path) : null;
    }

    public async Task<Recipe> SaveAsync(Recipe recipe)
    {
        var stamped = recipe with
        {
            CreatedUtc = recipe.CreatedUtc == default ? DateTime.UtcNow : recipe.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow,
        };

        await _writeLock.WaitAsync();
        try
        {
            var path = PathFor(stamped.Id);

            // Write beside the target and swap, so an interrupted write cannot truncate the
            // recipe that was already there.
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stamped, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }

        return stamped;
    }

    public Task<bool> DeleteAsync(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>Tools that no longer exist; a window using one cannot be judged, so it is dropped on load.</summary>
    private static readonly string[] RetiredTools = ["Brightness", "Contrast"];

    private static async Task<Recipe?> ReadFileAsync(string path)
    {
        try
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path));
            DropRetiredTools(document, path);
            return document.Deserialize<Recipe>(SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // One unreadable file must not hide every other recipe from the operator.
            Console.WriteLine($"[Recipes] '{Path.GetFileName(path)}' okunamadı: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Without this a recipe taught with a contrast or brightness window would fail to deserialize
    /// and vanish from the list. The file on disk is left alone until the recipe is saved again.
    /// </summary>
    private static void DropRetiredTools(JsonNode? recipe, string path)
    {
        if (recipe?["windows"] is not JsonArray windows)
        {
            return;
        }

        var retired = windows
            .Where(window => window?["tool"] is JsonValue tool
                && tool.TryGetValue<string>(out var name)
                && RetiredTools.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var window in retired)
        {
            windows.Remove(window);
        }

        if (retired.Count > 0)
        {
            Console.WriteLine(
                $"[Recipes] '{Path.GetFileName(path)}': kontrast/parlaklık aracı kullanan {retired.Count} pencere kaldırıldı (bu araçlar artık yok).");
        }
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{SanitizeId(id)}.json");

    /// <summary>
    /// Ids reach us from HTTP and become file names, so anything that could escape the recipe
    /// directory or collide with a device name is rejected rather than rewritten.
    /// </summary>
    public static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > 64
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException(
                "Reçete kimliği yalnızca harf, rakam, '-' ve '_' içerebilir (en fazla 64 karakter).",
                nameof(id));
        }

        return id;
    }

    /// <summary>Turns a display name into an id that is safe as a file name and readable in a URL.</summary>
    public static string NewIdFor(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('-');
        }

        // A Turkish name can be entirely non-ASCII, which leaves nothing usable behind.
        var prefix = slug.Length == 0 ? "recete" : slug;
        return $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
    }
}
