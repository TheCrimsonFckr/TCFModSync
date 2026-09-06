using TCFModSync.Shared.Models;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Server.Sync;

public sealed class SyncRequestHandler
{
    private readonly string _gameRootDirectory;
    private readonly string? _serverRootDirectory;
    private readonly ServerConfig _config;
    private readonly ManifestBuilder _manifestBuilder;
    private readonly Action<string> _log;

    private HashSet<string>? _servedPaths;

    public SyncRequestHandler(
        string gameRootDirectory, string? serverRootDirectory, ServerConfig config,
        ManifestBuilder manifestBuilder, Action<string> log)
    {
        _gameRootDirectory = Path.GetFullPath(gameRootDirectory);
        _serverRootDirectory = string.IsNullOrWhiteSpace(serverRootDirectory)
            ? null
            : Path.GetFullPath(serverRootDirectory!);
        _config = config;
        _manifestBuilder = manifestBuilder;
        _log = log;
    }

    public Manifest BuildManifest(bool headless)
    {
        RefreshServedPaths();

        var manifest = _manifestBuilder.Build(
            _gameRootDirectory, _serverRootDirectory, _config, _config.SptVersion, headless);

        if (manifest.Files.Count == 0)
        {
            _log($"Manifest is EMPTY - scanned '{_gameRootDirectory}' and nothing matched the include " +
                 "patterns (or everything matched was excluded).");

            foreach (var line in _manifestBuilder.Diagnose(_gameRootDirectory, _config.IncludePatterns))
            {
                _log(line);
            }
        }
        else if (_config.VerboseLogging)
        {
            var gameCount = manifest.Files.Count(f => f.Root == SptRootKind.Game);
            var serverCount = manifest.Files.Count - gameCount;
            var totalBytes = manifest.Files.Sum(f => f.Size);

            _log($"Manifest requested{(headless ? " (headless)" : "")}: {gameCount} game file(s), " +
                 $"{serverCount} server file(s), {totalBytes / 1024.0 / 1024.0:F1} MB.");
        }

        return manifest;
    }

    private void RefreshServedPaths()
    {
        var paths = ManifestBuilder.ResolveGamePaths(_gameRootDirectory, _config, false, out _);
        _servedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    // A file is downloadable only if the game-root patterns actually offer it. Containment alone is
    // not enough: on a bundled install the server folder, profiles included, sits inside the game
    // root. Server-root content is reported in the manifest and never served.
    public bool TryResolveSafePath(string relativePath, out string absolutePath)
    {
        absolutePath = "";

        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Split('/', '\\').Any(segment => segment == "..")) return false;

        if (_servedPaths == null) RefreshServedPaths();
        if (!_servedPaths!.Contains(SptPaths.ToRelative(relativePath))) return false;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(SptPaths.Combine(_gameRootDirectory, relativePath));
        }
        catch
        {
            return false;
        }

        var rootWithSeparator = _gameRootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _gameRootDirectory
            : _gameRootDirectory + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return false;

        absolutePath = candidate;
        return true;
    }
}
