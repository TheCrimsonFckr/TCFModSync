using System.Text.Json.Serialization;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Shared.Models;

public sealed class ManifestEntry
{
    public string RelativePath { get; set; } = "";

    // Absent in manifests from servers older than config 1.1.0, which served game content only.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SptRootKind Root { get; set; } = SptRootKind.Game;

    public long Size { get; set; }

    public string Hash { get; set; } = "";
}

public sealed class Manifest
{
    public string ServerConfigVersion { get; set; } = "";
    public List<ManifestEntry> Files { get; set; } = new();
    public List<string> FileHashBlacklist { get; set; } = new();

    // Game-root paths only - what a client would otherwise keep tracking. Server-root exclusions
    // are not reported, since nothing on a client tracks server content.
    public List<string> ExcludedPaths { get; set; } = new();

    public string SptVersion { get; set; } = "";
}
