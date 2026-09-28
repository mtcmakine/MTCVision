namespace MTCVision.Backend;

/// <summary>
/// Where recipes, images and session state live.
///
/// Not beside the binary: the same install is run from the source tree during development and from
/// a publish folder as a Windows service, and a path relative to the binary means the two see
/// different recipes. A fixed machine-level folder keeps one set of data whichever way the server
/// was started, and is somewhere a service account can actually write.
/// </summary>
public sealed class DataPaths
{
    /// <summary>Overrides the location, for a machine that keeps line data on another drive.</summary>
    public const string OverrideVariable = "MTCVISION_DATA";

    /// <summary>
    /// The data root, without needing the container — logging has to be configured before the
    /// service provider exists.
    /// </summary>
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariable);
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MTCVision")
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(root);
        return root;
    }

    public DataPaths(IWebHostEnvironment environment)
    {
        Root = Resolve();
        MigrateFrom(Path.Combine(environment.ContentRootPath, "Data"));
    }

    public string Root { get; }

    public string Recipes => EnsureSubdirectory("Recipes");

    public string Images => EnsureSubdirectory("Images");

    /// <summary>Registered master images, one per recipe.</summary>
    public string Masters => EnsureSubdirectory("Masters");

    /// <summary>Drop folder for the embedded FTP server the Zebra camera backend pushes images into.</summary>
    public string ZebraIncoming => EnsureSubdirectory("ZebraIncoming");

    public string File(string name) => Path.Combine(Root, name);

    private string EnsureSubdirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Moves data written by an earlier build that stored it beside the binary. Only runs while the
    /// new location is still empty, so it can never overwrite live data, and copies rather than
    /// moves so the old folder stays as a fallback if something goes wrong.
    /// </summary>
    private void MigrateFrom(string legacyRoot)
    {
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        foreach (var folder in new[] { "Recipes", "Images" })
        {
            var source = Path.Combine(legacyRoot, folder);
            var destination = Path.Combine(Root, folder);
            if (!Directory.Exists(source) || Directory.Exists(destination))
            {
                continue;
            }

            var files = Directory.GetFiles(source, "*.json").Concat(Directory.GetFiles(source, "*.jpg")).ToList();
            if (files.Count == 0)
            {
                continue;
            }

            Directory.CreateDirectory(destination);
            foreach (var file in files)
            {
                System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }

            Console.WriteLine($"[Data] {files.Count} dosya {source} konumundan {destination} konumuna taşındı.");
        }
    }
}
