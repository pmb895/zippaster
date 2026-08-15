using System.Text.Json;
using System.Text.Json.Serialization;
using ZipPaster.Interop;

namespace ZipPaster.Data;

/// <summary>A website/campaign the user is filling forms for.</summary>
public sealed class Project
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>ZIPs already used on this site. Order is not meaningful.</summary>
    public HashSet<string> UsedZips { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Pastes in the order they happened, so Undo can step back reliably even
    /// after filtering or re-sorting. Capped; see <see cref="AppState.MaxHistory"/>.
    /// </summary>
    public List<string> History { get; set; } = [];
}

/// <summary>User-configurable behavior.</summary>
public sealed class Settings
{
    public string ZipHotkey { get; set; } = "Ctrl+Alt+Z";
    public string CityHotkey { get; set; } = "Ctrl+Alt+C";
    public string StateHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>Paste the state as "TX" when true, "Texas" when false.</summary>
    public bool StateAsAbbreviation { get; set; } = true;

    /// <summary>Marks the ZIP used and moves to the next one after a ZIP paste.</summary>
    public bool AutoAdvance { get; set; } = true;

    public PasteMode PasteMode { get; set; } = PasteMode.Clipboard;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Shows a tray balloon on each paste. Off by default; it gets noisy.</summary>
    public bool NotifyOnPaste { get; set; }

    public string? ActiveProjectId { get; set; }

    // Persisted view state, so the app reopens where the user left off.
    public string LastStateFilter { get; set; } = "";
    public bool HideUsed { get; set; }
    public ZipSort Sort { get; set; } = ZipSort.Zip;
    public bool SortDescending { get; set; }
}

/// <summary>
/// Everything that persists between runs, stored as a single JSON file.
/// </summary>
/// <remarks>
/// Deliberately not a database. The ZIP catalog is read-only and lives in memory,
/// so the only mutable state is a handful of projects, their used-ZIP sets, and
/// settings -- small enough that a JSON file is simpler and drops a native
/// dependency that carried an unpatched CVE.
/// </remarks>
public sealed class AppState
{
    /// <summary>Bounds the undo history so a long session cannot grow unbounded.</summary>
    public const int MaxHistory = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public List<Project> Projects { get; set; } = [];

    [JsonIgnore]
    public string FilePath { get; private set; } = DefaultFilePath;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZipPaster");

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "data.json");

    public static AppState Load() => Load(DefaultFilePath);

    public static AppState Load(string path)
    {
        AppState state;

        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                state = JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Never let a corrupt file block startup: preserve it for
                // inspection and continue with defaults.
                TryBackupCorruptFile(path);
                state = new AppState();
            }
        }
        else
        {
            state = new AppState();
        }

        state.FilePath = path;

        // A project must always exist so the used-tracking has somewhere to go.
        if (state.Projects.Count == 0)
            state.Projects.Add(NewProject("My First Project"));

        if (state.ActiveProject is null)
            state.Settings.ActiveProjectId = state.Projects[0].Id;

        return state;
    }

    /// <summary>
    /// Writes via a temp file and an atomic replace, so a crash mid-write cannot
    /// leave a truncated file where the user's project history used to be.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));

        if (File.Exists(FilePath))
            File.Replace(temp, FilePath, null);
        else
            File.Move(temp, FilePath);
    }

    [JsonIgnore]
    public Project? ActiveProject =>
        Projects.FirstOrDefault(p => p.Id == Settings.ActiveProjectId);

    public static Project NewProject(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
    };

    /// <summary>Records a ZIP as used, keeping the capped undo history in step.</summary>
    public void MarkUsed(Project project, string zip)
    {
        if (!project.UsedZips.Add(zip))
            return;

        project.History.Add(zip);

        if (project.History.Count > MaxHistory)
            project.History.RemoveRange(0, project.History.Count - MaxHistory);
    }

    /// <summary>Reverses the most recent paste. Returns the ZIP, or null if empty.</summary>
    public string? UndoLast(Project project)
    {
        if (project.History.Count == 0)
            return null;

        string zip = project.History[^1];
        project.History.RemoveAt(project.History.Count - 1);
        project.UsedZips.Remove(zip);

        return zip;
    }

    private static void TryBackupCorruptFile(string path)
    {
        try
        {
            string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
        }
        catch (IOException)
        {
            // If we cannot even move it, defaults still let the app start.
        }
    }
}
