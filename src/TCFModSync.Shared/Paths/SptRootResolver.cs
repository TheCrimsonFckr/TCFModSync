namespace TCFModSync.Shared.Paths;

public enum SptRootKind
{
    Game,
    Server
}

// Why a root could not be resolved. The caller words it; nothing here composes a sentence.
public enum SptRootProblem
{
    None,

    // The directory the search was to start from does not exist. Carries StartDirectory.
    StartDirectoryMissing,

    // A root was set explicitly and is not there. Carries ConfiguredDirectory.
    ConfiguredDirectoryMissing,

    // Nothing above StartDirectory looks like this root. Carries StartDirectory.
    NotFound
}

public sealed class SptRootResult
{
    private SptRootResult(SptRootKind kind)
    {
        Kind = kind;
    }

    public SptRootKind Kind { get; }

    public string? Directory { get; private set; }

    public SptRootProblem Problem { get; private set; } = SptRootProblem.None;

    public string? StartDirectory { get; private set; }

    public string? ConfiguredDirectory { get; private set; }

    // The exe the server root was derived from. Null for a game root or a configured server root.
    public string? ServerExePath { get; private set; }

    public bool Found => Directory != null;

    internal static SptRootResult Resolved(SptRootKind kind, string directory, string? startDirectory,
        string? configuredDirectory = null, string? serverExePath = null)
        => new SptRootResult(kind)
        {
            Directory = directory,
            StartDirectory = startDirectory,
            ConfiguredDirectory = configuredDirectory,
            ServerExePath = serverExePath
        };

    internal static SptRootResult Failed(SptRootKind kind, SptRootProblem problem, string? startDirectory,
        string? configuredDirectory = null)
        => new SptRootResult(kind)
        {
            Problem = problem,
            StartDirectory = startDirectory,
            ConfiguredDirectory = configuredDirectory
        };
}

// Resolves the game root and the server root independently. They are the same folder on a bundled
// install and different on a standalone server, so neither is derived from the other.
public static class SptRootResolver
{
    public static SptRootResult ResolveGameRoot(string startDirectory, string? configuredDirectory = null)
    {
        if (TryUseConfigured(SptRootKind.Game, startDirectory, configuredDirectory, out var configured))
            return configured;

        if (!Directory.Exists(startDirectory))
            return SptRootResult.Failed(SptRootKind.Game, SptRootProblem.StartDirectoryMissing, startDirectory);

        foreach (var directory in SelfAndAncestors(startDirectory))
        {
            if (IsGameRoot(directory))
                return SptRootResult.Resolved(SptRootKind.Game, directory, startDirectory);
        }

        return SptRootResult.Failed(SptRootKind.Game, SptRootProblem.NotFound, startDirectory);
    }

    public static SptRootResult ResolveServerRoot(string startDirectory, string? configuredDirectory = null)
    {
        if (TryUseConfigured(SptRootKind.Server, startDirectory, configuredDirectory, out var configured))
            return configured;

        if (!Directory.Exists(startDirectory))
            return SptRootResult.Failed(SptRootKind.Server, SptRootProblem.StartDirectoryMissing, startDirectory);

        var ancestors = SelfAndAncestors(startDirectory).ToList();

        // A named exe anywhere above beats a wildcard guess anywhere above, so the named candidates
        // are exhausted across the whole chain before the fallback runs at all.
        foreach (var directory in ancestors)
        {
            if (TryFindNamedServerExe(directory, out var exePath))
                return ServerRootFor(exePath, startDirectory);
        }

        foreach (var directory in ancestors)
        {
            if (TryFindServerExeByWildcard(directory, out var exePath))
                return ServerRootFor(exePath, startDirectory);
        }

        return SptRootResult.Failed(SptRootKind.Server, SptRootProblem.NotFound, startDirectory);
    }

    // The mods folder under an already-resolved server root, or under an install root that still
    // has the server nested inside it.
    public static bool TryGetServerModsDirectory(string rootDirectory, out string modsDirectory)
    {
        modsDirectory = "";

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory)) return false;

        foreach (var layout in SptPaths.ServerModsLayouts)
        {
            var candidate = SptPaths.Combine(rootDirectory, layout);
            if (Directory.Exists(candidate) || Directory.Exists(candidate + SptPaths.DisabledSuffix))
            {
                modsDirectory = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsGameRoot(string directory)
    {
        foreach (var file in SptPaths.GameRootMarkerFiles)
        {
            if (File.Exists(Path.Combine(directory, file))) return true;
        }

        foreach (var folder in SptPaths.GameRootMarkerDirectories)
        {
            if (Directory.Exists(Path.Combine(directory, folder))) return true;
            if (Directory.Exists(Path.Combine(directory, folder) + SptPaths.DisabledSuffix)) return true;
        }

        return false;
    }

    public static bool TryFindNamedServerExe(string directory, out string exePath)
    {
        exePath = "";

        foreach (var candidate in SptPaths.ServerExeCandidates)
        {
            var path = SptPaths.Combine(directory, candidate);
            if (File.Exists(path))
            {
                exePath = path;
                return true;
            }
        }

        return false;
    }

    public static bool TryFindServerExeByWildcard(string directory, out string exePath)
    {
        exePath = "";

        foreach (var folder in SptPaths.ServerExeSearchFolders)
        {
            var searchDirectory = folder.Length == 0 ? directory : SptPaths.Combine(directory, folder);
            if (!Directory.Exists(searchDirectory)) continue;

            string? hit;
            try
            {
                hit = Directory.EnumerateFiles(searchDirectory, SptPaths.ServerExeWildcard, SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch
            {
                continue;
            }

            if (hit != null)
            {
                exePath = hit;
                return true;
            }
        }

        return false;
    }

    private static SptRootResult ServerRootFor(string exePath, string startDirectory)
    {
        var directory = Path.GetDirectoryName(exePath);

        return string.IsNullOrEmpty(directory)
            ? SptRootResult.Failed(SptRootKind.Server, SptRootProblem.NotFound, startDirectory)
            : SptRootResult.Resolved(SptRootKind.Server, directory!, startDirectory, serverExePath: exePath);
    }

    private static bool TryUseConfigured(SptRootKind kind, string startDirectory, string? configuredDirectory,
        out SptRootResult result)
    {
        result = null!;

        if (string.IsNullOrWhiteSpace(configuredDirectory)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(configuredDirectory!);
        }
        catch
        {
            result = SptRootResult.Failed(kind, SptRootProblem.ConfiguredDirectoryMissing, startDirectory,
                configuredDirectory);
            return true;
        }

        result = Directory.Exists(full)
            ? SptRootResult.Resolved(kind, full, startDirectory, configuredDirectory)
            : SptRootResult.Failed(kind, SptRootProblem.ConfiguredDirectoryMissing, startDirectory, full);

        return true;
    }

    private static IEnumerable<string> SelfAndAncestors(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
