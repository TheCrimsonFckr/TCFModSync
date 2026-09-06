namespace TCFModSync.Shared.Models;

public sealed class ServerConfig
{
    public string ConfigVersion { get; set; } = "1.1.0";

    public List<string> IncludePatterns { get; set; } = new()
    {
        "TCFModSync.Updater.exe",
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "clrjit.dll",
        "clrcompression.dll",
        "BepInEx/patchers/**/*",
        "BepInEx/plugins/**/*"
    };

    public List<string> ExcludePatterns { get; set; } = new()
    {
        "**/*.log",
        "BepInEx/plugins/SAIN/**/*.json",
        "BepInEx/patchers/spt-prepatch.dll",
        "BepInEx/plugins/spt/**/*",
        "BepInEx/plugins/DynamicMaps/**/*"
    };

    // Resolved against the server root. Empty offers clients no server-side content at all.
    public List<string> ServerIncludePatterns { get; set; } = new();

    public List<string> ServerExcludePatterns { get; set; } = new()
    {
        "**/*.log"
    };

    public List<string> FileHashBlacklist { get; set; } = new();

    public List<string> HeadlessIncludePatterns { get; set; } = new();

    public List<string> HeadlessExcludePatterns { get; set; } = new();

    public string GameRootDirectory { get; set; } = "";

    public string ServerRootDirectory { get; set; } = "";

    // Legacy name for GameRootDirectory. Read only when GameRootDirectory is empty.
    public string SptRootDirectory { get; set; } = "";

    public bool VerboseLogging { get; set; } = false;

    public string SptVersion { get; set; } = "4.0.13";

    public string EffectiveGameRootDirectory
        => string.IsNullOrWhiteSpace(GameRootDirectory) ? SptRootDirectory : GameRootDirectory;
}
