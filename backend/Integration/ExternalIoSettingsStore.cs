using System.Text.Json;

namespace MTCVision.Backend.Integration;

/// <summary>The TCP/IP interface's settings, kept in Data/external-io.json so a restart keeps them.</summary>
public sealed class ExternalIoSettingsStore(DataPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path = paths.File("external-io.json");

    public ExternalIoSettings Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ExternalIoSettings();
            }

            var settings = JsonSerializer.Deserialize<ExternalIoSettings>(File.ReadAllText(_path), SerializerOptions);
            return settings is not null && settings.Validate() is null ? settings : new ExternalIoSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"[ExternalIo] Ayarlar okunamadı, varsayılanlar kullanılıyor: {ex.Message}");
            return new ExternalIoSettings();
        }
    }

    public void Write(ExternalIoSettings settings)
    {
        // Beside the target and swapped in, so a power cut mid-write cannot leave half a file.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}
