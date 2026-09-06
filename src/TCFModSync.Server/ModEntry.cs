using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using TCFModSync.Server.Config;
using TCFModSync.Server.Http;
using TCFModSync.Server.Sync;
using TCFModSync.Shared.Logging;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Server;

[Injectable]
public class ModEntry : IOnLoad
{
    private readonly ISptLogger<ModEntry> _logger;
    private readonly ServerConfigService _configService = new();
    private readonly ManifestBuilder _manifestBuilder = new();

    private TCFModSync.Shared.Models.ServerConfig? _config;
    private string _gameRootDirectory = "";
    private string? _serverRootDirectory;
    private FileLog? _fileLog;

    public ModEntry(ISptLogger<ModEntry> logger)
    {
        _logger = logger;
    }

    /// <summary>Logs to both the SPT server's shared log and this mod's own
    /// TCFModSync.Server.log, so a sync issue can be debugged from one file without wading
    /// through everything else the server logs.</summary>
    private void LogInfo(string message)
    {
        _logger.Info(message);
        _fileLog?.Write(message);
    }

    private void LogWarning(string message)
    {
        _logger.Warning(message);
        _fileLog?.Write($"WARN: {message}");
    }

    private void LogError(string message)
    {
        _logger.Error(message);
        _fileLog?.Write($"ERROR: {message}");
    }

    private void LogSuccess(string message)
    {
        _logger.Success(message);
        _fileLog?.Write(message);
    }

    private static string Describe(SptRootResult result)
    {
        var name = result.Kind == SptRootKind.Game ? "game root" : "server root";
        var setting = result.Kind == SptRootKind.Game ? "GameRootDirectory" : "ServerRootDirectory";

        return result.Problem switch
        {
            SptRootProblem.ConfiguredDirectoryMissing =>
                $"{setting} in serverConfig.json points at '{result.ConfiguredDirectory}', which does not exist.",
            SptRootProblem.StartDirectoryMissing =>
                $"Could not look for the {name}: '{result.StartDirectory}' does not exist.",
            _ =>
                $"Could not auto-detect the {name} above '{result.StartDirectory}'. " +
                $"Set {setting} in serverConfig.json to the folder you want to serve from."
        };
    }

    public Task OnLoad()
    {
        try
        {
            var modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                                ?? throw new InvalidOperationException("Could not resolve mod directory.");

            _fileLog = new FileLog(Path.Combine(modDirectory, "TCFModSync.Server.log"));
            FileLog.Current = _fileLog;

            _config = _configService.LoadOrCreate(modDirectory);

            if (_config.VerboseLogging)
            {
                try
                {
                    var location = Assembly.GetExecutingAssembly().Location;
                    LogInfo(string.IsNullOrEmpty(location)
                        ? "[TCF-ModSync] Assembly has no on-disk location; build time unknown."
                        : $"[TCF-ModSync] Build timestamp {File.GetLastWriteTime(location):yyyy-MM-dd HH:mm:ss} ({location})");
                }
                catch (Exception ex)
                {
                    LogInfo($"[TCF-ModSync] Could not read build timestamp: {ex.Message}");
                }
            }

            var gameRoot = SptRootResolver.ResolveGameRoot(modDirectory, _config.EffectiveGameRootDirectory);
            if (!gameRoot.Found)
            {
                throw new InvalidOperationException($"[TCF-ModSync] {Describe(gameRoot)}");
            }

            _gameRootDirectory = gameRoot.Directory!;

            var serverRoot = SptRootResolver.ResolveServerRoot(modDirectory, _config.ServerRootDirectory);
            if (serverRoot.Found)
            {
                _serverRootDirectory = serverRoot.Directory;
            }
            else if (_config.ServerIncludePatterns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[TCF-ModSync] ServerIncludePatterns is set but the server root could not be resolved. " +
                    Describe(serverRoot));
            }
            else if (_config.VerboseLogging)
            {
                LogInfo($"[TCF-ModSync] No server root resolved, and no server patterns are set. " +
                        Describe(serverRoot));
            }

            if (_config.VerboseLogging)
            {
                LogInfo($"[TCF-ModSync] Game root: '{_gameRootDirectory}'.");
                LogInfo($"[TCF-ModSync] Server root: '{_serverRootDirectory ?? "(none)"}'" +
                        (serverRoot.ServerExePath != null ? $", from '{serverRoot.ServerExePath}'." : "."));
                LogInfo($"[TCF-ModSync] Include patterns: {string.Join(", ", _config.IncludePatterns)}");
                LogInfo($"[TCF-ModSync] Exclude patterns: {string.Join(", ", _config.ExcludePatterns)}");
                LogInfo($"[TCF-ModSync] Server include patterns: {string.Join(", ", _config.ServerIncludePatterns)}");
            }

            SptRouteListener.Handler = new SyncRequestHandler(
                _gameRootDirectory, _serverRootDirectory, _config, _manifestBuilder,
                msg => LogInfo($"[TCF-ModSync] {msg}"));

            var gameCount = ManifestBuilder.ResolveGamePaths(_gameRootDirectory, _config, false, out _).Count;
            var serverCount = ManifestBuilder.ResolveServerPaths(_serverRootDirectory, _config).Count;

            if (gameCount == 0)
            {
                LogWarning(
                    $"[TCF-ModSync] Manifest is EMPTY - scanned '{_gameRootDirectory}' and no file matched " +
                    "the include patterns (or everything matched was excluded).");

                foreach (var line in _manifestBuilder.Diagnose(_gameRootDirectory, _config.IncludePatterns))
                {
                    LogWarning($"[TCF-ModSync] {line}");
                }
            }

            if (serverCount == 0 && _config.ServerIncludePatterns.Count > 0)
            {
                LogWarning(
                    $"[TCF-ModSync] No server file matched - scanned '{_serverRootDirectory}'.");

                foreach (var line in _manifestBuilder.Diagnose(_serverRootDirectory!, _config.ServerIncludePatterns))
                {
                    LogWarning($"[TCF-ModSync] {line}");
                }
            }

            LogSuccess(
                $"[TCF-ModSync] Ready - sharing {gameCount} game file(s) and reporting {serverCount} " +
                "server file(s) on the SPT server's own port.");
        }
        catch (Exception ex)
        {
            LogError($"[TCF-ModSync] Failed to start: {ex}");
        }

        return Task.CompletedTask;
    }
}
