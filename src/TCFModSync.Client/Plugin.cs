using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using TCFModSync.Client.Sync;
using TCFModSync.Client.UI;
using TCFModSync.Shared.Logging;
using TCFModSync.Shared.Paths;
using UnityEngine;

namespace TCFModSync.Client
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.thecrimsonfuckr.tcfmodsync.client";
        public const string PluginName = "TCF-ModSync";
        public const string PluginVersion = "2.0.0";

        private ConfigEntry<bool> _headlessAutoAccept = null!;
        private ConfigEntry<string> _relaunchTarget = null!;
        private ConfigEntry<bool> _resetExclusions = null!;

        private SyncOrchestrator _orchestrator = null!;
        private StagedStore _stagedStore = null!;
        private Shared.Models.PendingOperations? _previouslyStaged;
        private bool _isHeadless;
        private SyncWindow _window = null!;
        private string _gameRootDirectory = "";
        private string _clientConfigPath = "";
        private FileLog _fileLog = null!;

        /// <summary>Logs to both BepInEx's shared LogOutput.log and this plugin's own
        /// TCFModSync.Client.log, so a sync issue can be debugged from one file without wading
        /// through every other mod's log output.</summary>
        private void LogInfo(string message)
        {
            Logger.LogInfo(message);
            _fileLog.Write(message);
        }

        private void LogWarning(string message)
        {
            Logger.LogWarning(message);
            _fileLog.Write($"WARN: {message}");
        }

        private void LogError(string message)
        {
            Logger.LogError(message);
            _fileLog.Write($"ERROR: {message}");
        }

        private void Awake()
        {
            var pluginDir = Path.GetDirectoryName(Info.Location) ?? "";
            _fileLog = new FileLog(Path.Combine(pluginDir, "TCFModSync.Client.log"));

            _headlessAutoAccept = Config.Bind("Behaviour", "HeadlessAutoAccept", false,
                "Force unattended syncing - no window, every offer accepted. Headless clients are " +
                "detected automatically, so this is only needed to force the behaviour on a client " +
                "that isn't detected as one.");

            var gameRoot = SptRootResolver.ResolveGameRoot(pluginDir);
            if (!gameRoot.Found)
            {
                throw new InvalidOperationException(
                    $"Could not locate the game root above '{pluginDir}'. Expected a folder containing " +
                    "EscapeFromTarkov.exe or BepInEx.");
            }

            _gameRootDirectory = gameRoot.Directory!;
            _clientConfigPath = Path.Combine(pluginDir, "clientConfig.json");

            _relaunchTarget = Config.Bind("Behaviour", "RelaunchTarget", "None",
                "What to start after files are applied. 'None' (default) closes the game and leaves it " +
                "closed - relaunch through the SPT launcher as normal. 'Launcher' starts the SPT " +
                "launcher for you. 'Game' restarts the game directly reusing its launch arguments; " +
                "this often fails because SPT's profile token is only accepted once.");

            _resetExclusions = Config.Bind("Behaviour", "ResetExclusionsOnNextLaunch", false,
                "Tick this and restart the game to clear every file you have previously declined, so " +
                "they are offered again. Resets itself once applied.");

            _orchestrator = new SyncOrchestrator(_gameRootDirectory, _clientConfigPath, LogInfo)
            {
                RelaunchTarget = _relaunchTarget.Value
            };

            if (_resetExclusions.Value)
            {
                var cleared = _orchestrator.ResetExclusions();
                _resetExclusions.Value = false;
                LogInfo($"[TCF-ModSync] ResetExclusionsOnNextLaunch was set - cleared {cleared} exclusion(s).");
            }

            _window = new SyncWindow { Log = LogInfo };

            _isHeadless = HeadlessDetector.IsHeadless(pluginDir, out var headlessReason);
            if (_isHeadless)
            {
                _orchestrator.IsHeadless = true;
                LogInfo($"[TCF-ModSync] Headless client detected ({headlessReason}) - syncing " +
                        "unattended, no confirmation will be requested.");
            }

            _stagedStore = new StagedStore(pluginDir, LogInfo);
            _previouslyStaged = _stagedStore.Load();
            if (_previouslyStaged != null)
            {
                LogInfo($"[TCF-ModSync] Found {_previouslyStaged.Operations.Count} file(s) staged in a " +
                        "previous session; they will be reused instead of downloaded again.");
            }

            _ = RunStartupCheckAsync();
        }

        private void Update()
        {
            MainThreadDispatcher.Drain(LogError);
        }

        private void OnGUI()
        {
            _window?.Draw();
        }

        private void OnDestroy()
        {
            _fileLog?.Dispose();
        }

        private async Task RunStartupCheckAsync()
        {
            try
            {
                LogInfo("[TCF-ModSync] Checking server for updates...");
                var (manifest, config, diff) = await _orchestrator.CheckAsync();

                if (diff.Count == 0)
                {
                    LogInfo("[TCF-ModSync] Nothing to sync.");
                    return;
                }

                LogInfo($"[TCF-ModSync] {diff.Count} item(s) proposed.");

                if (_isHeadless || _headlessAutoAccept.Value || config.HeadlessAutoAccept)
                {
                    var acceptedPaths = new System.Collections.Generic.HashSet<string>(
                        System.Linq.Enumerable.Select(diff, d => d.RelativePath), StringComparer.OrdinalIgnoreCase);
                    await RunHeadlessAsync(config, diff, acceptedPaths);
                    return;
                }

                _window.OnAccept = () => _ = OnWindowAcceptedAsync(manifest, config, diff);
                _window.OnClose = () => LogInfo("[TCF-ModSync] Sync skipped for this session.");
                _window.ExcludedCount = _orchestrator.CountExclusions();
                _window.ServerSptVersion = manifest.SptVersion;
                _window.OnResetExclusions = () =>
                {
                    var cleared = _orchestrator.ResetExclusions();
                    LogInfo($"[TCF-ModSync] Cleared {cleared} exclusion(s); re-checking.");
                    _window.Close();
                    _ = RunStartupCheckAsync();
                };
                MainThreadDispatcher.Enqueue(() => _window.Open(diff));
            }
            catch (Exception ex)
            {
                LogWarning($"[TCF-ModSync] Sync check failed - {SyncOrchestrator.Describe(ex)}");
                LogWarning("[TCF-ModSync] Continuing without syncing.");
            }
        }

        private async Task RunHeadlessAsync(
            Shared.Models.ClientConfig config,
            System.Collections.Generic.List<Shared.Diffing.DiffResult> diff,
            System.Collections.Generic.HashSet<string> acceptedPaths)
        {
            var progress = new SyncProgress();
            var pending = await _orchestrator.DownloadAsync(config, diff, acceptedPaths, progress, _previouslyStaged);

            if (pending == null)
            {
                LogInfo("[TCF-ModSync] Headless: nothing needed downloading.");
                return;
            }

            LogInfo($"[TCF-ModSync] Headless: applying {pending.Operations.Count} operation(s) and restarting.");
            _orchestrator.CommitAndLaunchUpdater(pending);
            _stagedStore.Discard();
            await Task.Delay(500);
            MainThreadDispatcher.Enqueue(Application.Quit);
        }

        private async Task OnWindowAcceptedAsync(
            Shared.Models.Manifest manifest, Shared.Models.ClientConfig config,
            System.Collections.Generic.List<Shared.Diffing.DiffResult> diff)
        {
            await DownloadThenConfirmAsync(config, diff, _window.Accepted);
        }

        private async Task DownloadThenConfirmAsync(
            Shared.Models.ClientConfig config,
            System.Collections.Generic.List<Shared.Diffing.DiffResult> diff,
            System.Collections.Generic.HashSet<string> acceptedPaths)
        {
            var progress = new SyncProgress();
            MainThreadDispatcher.Enqueue(() => _window.ShowDownloading(progress));

            var startedAt = DateTime.UtcNow;

            Shared.Models.PendingOperations? pending;
            try
            {
                pending = await _orchestrator.DownloadAsync(config, diff, acceptedPaths, progress, _previouslyStaged);
            }
            catch (Exception ex)
            {
                LogError($"[TCF-ModSync] Download failed: {ex}");
                var message = SyncOrchestrator.Describe(ex);
                MainThreadDispatcher.Enqueue(() => _window.ShowError(message));
                return;
            }

            if (pending == null)
            {
                LogInfo("[TCF-ModSync] Nothing needed downloading; config updated.");
                MainThreadDispatcher.Enqueue(() => _window.Close());
                return;
            }

            var elapsed = DateTime.UtcNow - startedAt;
            var minimumVisible = TimeSpan.FromSeconds(1.5);
            if (elapsed < minimumVisible)
            {
                await Task.Delay(minimumVisible - elapsed);
            }

            LogInfo($"[TCF-ModSync] Download complete. {pending.Operations.Count} operation(s) staged, " +
                    "awaiting confirmation to apply.");

            _window.OnConfirmRestart = () =>
            {
                LogInfo("[TCF-ModSync] Restart confirmed. Launching updater and closing game.");
                _orchestrator.CommitAndLaunchUpdater(pending);
                _stagedStore.Discard();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    MainThreadDispatcher.Enqueue(Application.Quit);
                });
            };

            _window.OnDeclineRestart = () =>
            {
                LogInfo("[TCF-ModSync] Restart declined; keeping downloads for next launch.");
                _stagedStore.Save(pending);
                _previouslyStaged = pending;
            };

            MainThreadDispatcher.Enqueue(() => _window.ShowConfirmRestart());
        }
    }
}
