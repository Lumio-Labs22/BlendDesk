using System.IO;
using System.Text.Json;

namespace BlenderLibrary;

internal sealed record LibrarySettings
{
    public List<string> Folders { get; set; } = [];
    public List<string> Files { get; set; } = [];
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProjectDetails> Details { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ProjectSort Sort { get; set; }
    public string? BlenderPath { get; set; }
    public bool ShowCards { get; set; } = true;
    public bool LiquidGlass { get; set; }
    public ColorMode ColorMode { get; set; } = ColorMode.Dark;
    public string? BackgroundPath { get; set; }
}

internal enum ProjectSort { Modified, Name, Size, LastOpened }

internal sealed record ProjectDetails(string Notes = "", DateTimeOffset? LastOpened = null);

internal sealed record LibrarySource(string Path, bool IsFile)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public string Kind => IsFile ? "Blend file" : "Folder";
}

internal sealed record Project(string Path, string Name, DateTime Modified, long Size, bool Favorite)
{
    public ScenePreview Preview { get; init; } = new();
    public ProjectDetails Details { get; init; } = new();
    public DateTimeOffset LastOpened => Details.LastOpened ?? DateTimeOffset.MinValue;
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string FolderName => System.IO.Path.GetFileName(Folder) is { Length: > 0 } name ? name : Folder;
    public string SizeText => Size >= 1024L * 1024
        ? $"{Size / (1024.0 * 1024):0.#} MB"
        : $"{Size / 1024.0:0.#} KB";
}

internal sealed record ScanResult(List<Project> Projects, int SkippedFolders, int MissingFiles);

internal static class Library
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumio Labs", "BlenderLibrary", "settings.json");

    public static LibrarySettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new();

        var settings = JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(SettingsFile))
            ?? new LibrarySettings();
        settings.Folders = (settings.Folders ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Files = (settings.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Favorites = new HashSet<string>(settings.Favorites ?? [], StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, ProjectDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in settings.Details ?? [])
            if (entry is not null)
                details[path] = entry with { Notes = entry.Notes ?? "" };
        settings.Details = details;
        return settings;
    }

    public static void Save(LibrarySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static ScanResult Scan(string[] folders, string[] files, CancellationToken token)
    {
        var directories = new Stack<string>(folders);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<Project>();
        int skipped = 0;
        int missing = 0;

        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    AddProject(file);
                else
                    missing++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                missing++;
            }
        }

        while (directories.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(folder))
                continue;

            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo child)
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                            directories.Push(child.FullName);
                    }
                    else if (entry is FileInfo file)
                        AddProject(file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return new(projects, skipped, missing);

        void AddProject(FileInfo file)
        {
            if (!file.Extension.Equals(".blend", StringComparison.OrdinalIgnoreCase) || seen.Contains(file.FullName))
                return;

            projects.Add(new Project(file.FullName, Path.GetFileNameWithoutExtension(file.Name),
                file.LastWriteTime, file.Length, false));
            seen.Add(file.FullName);
        }
    }

    public static string? FindBlender()
    {
        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Blender Foundation");
        if (!Directory.Exists(programFiles))
            return null;

        return Directory.EnumerateDirectories(programFiles, "Blender*")
            .OrderByDescending(folder => Version.TryParse(Path.GetFileName(folder).Replace("Blender ", ""), out var version)
                ? version : new Version())
            .Select(folder => Path.Combine(folder, "blender.exe"))
            .FirstOrDefault(File.Exists);
    }
}
