namespace TCFModSync.Shared.Paths;

// The folder layouts an SPT install can have, and the ".disabled" container convention.
// Layouts are probed for, never derived from a version number.
public static class SptPaths
{
    public const string DisabledSuffix = ".disabled";

    public const string ServerExeWildcard = "*Server*.exe";

    // Marks a folder as a game root.
    public static readonly string[] GameRootMarkerFiles =
    {
        "EscapeFromTarkov.exe"
    };

    public static readonly string[] GameRootMarkerDirectories =
    {
        "BepInEx"
    };

    // Server exe locations across known layouts: 3.x/4.0 at the root or under SPT/, 4.1 under
    // SPT_Runtime/, plus the Aki-era names.
    public static readonly string[] ServerExeCandidates =
    {
        "SPT.Server.exe",
        "SPT_Runtime/SPT.Server.exe",
        "SPT/SPT.Server.exe",
        "Aki.Server.exe",
        "Aki.Server/Aki.Server.exe",
        "Server/Server.exe",
        "SPT/Server.exe"
    };

    // Searched with ServerExeWildcard when no named candidate matches. "" is the root itself.
    public static readonly string[] ServerExeSearchFolders =
    {
        "",
        "SPT_Runtime",
        "SPT"
    };

    // Server content, relative to a root that has not yet been narrowed to the server root.
    public static readonly string[] ServerModsLayouts =
    {
        "SPT_Runtime/user/mods",
        "SPT/user/mods",
        "user/mods"
    };

    // Folders whose immediate children are mods. Relative to their own root.
    public static readonly string[] ClientContainers =
    {
        "BepInEx/plugins",
        "BepInEx/patchers"
    };

    public static readonly string[] ServerContainers =
    {
        "user/mods"
    };

    public static readonly string[] AllContainers =
    {
        "BepInEx/plugins",
        "BepInEx/patchers",
        "user/mods"
    };

    public static string ToRelative(string path)
        => path.Replace('\\', '/').TrimStart('/');

    public static string ToNative(string relativePath)
        => relativePath.Replace('/', Path.DirectorySeparatorChar);

    public static string Combine(string rootDirectory, string relativePath)
        => Path.Combine(rootDirectory, ToNative(relativePath));

    // The container a path sits in, as it appears in that path - so "BepInEx/plugins.disabled"
    // when the container is disabled. False when the path is in no known container.
    public static bool TryFindContainer(string relativePath, out string container, out bool disabled)
    {
        container = "";
        disabled = false;

        var path = ToRelative(relativePath);

        var bestEnd = int.MaxValue;
        var bestDisabled = false;

        foreach (var name in AllContainers)
        {
            foreach (var disabledForm in new[] { false, true })
            {
                var form = disabledForm ? name + DisabledSuffix : name;
                var end = MatchLength(path, form);
                if (end >= 0 && end < bestEnd)
                {
                    bestEnd = end;
                    bestDisabled = disabledForm;
                }
            }
        }

        if (bestEnd == int.MaxValue) return false;

        container = path.Substring(0, bestEnd);
        disabled = bestDisabled;
        return true;
    }

    // Length of the prefix of <paramref name="path"/> ending in <paramref name="form"/> on segment
    // boundaries, or -1. Matches anywhere, so server content still matches under SPT_Runtime/.
    private static int MatchLength(string path, string form)
    {
        var from = 0;
        while (from <= path.Length - form.Length)
        {
            var at = path.IndexOf(form, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;

            var startsOnBoundary = at == 0 || path[at - 1] == '/';
            var end = at + form.Length;
            var endsOnBoundary = end == path.Length || path[end] == '/';

            if (startsOnBoundary && endsOnBoundary) return end;

            from = at + 1;
        }

        return -1;
    }

    public static bool IsInDisabledContainer(string relativePath)
        => TryFindContainer(relativePath, out _, out var disabled) && disabled;

    // The same file in the container's other state. False when the path is in no known container.
    public static bool TryGetCounterpart(string relativePath, out string counterpart)
    {
        counterpart = "";

        if (!TryFindContainer(relativePath, out var container, out var disabled)) return false;

        var path = ToRelative(relativePath);

        var swapped = disabled
            ? container.Substring(0, container.Length - DisabledSuffix.Length)
            : container + DisabledSuffix;

        counterpart = swapped + path.Substring(container.Length);
        return true;
    }

    // The path as it would read with every container enabled. Unchanged when already enabled or in
    // no known container, so it is safe to key a lookup on.
    public static string ToEnabledPath(string relativePath)
        => IsInDisabledContainer(relativePath) && TryGetCounterpart(relativePath, out var enabled)
            ? enabled
            : ToRelative(relativePath);
}
