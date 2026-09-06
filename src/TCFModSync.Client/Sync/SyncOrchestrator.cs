using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TCFModSync.Client.Config;
using TCFModSync.Client.Handoff;
using TCFModSync.Shared.Diffing;
using TCFModSync.Shared.Models;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Client.Sync
{
    public sealed class SyncOrchestrator
    {
        private readonly string _gameRootDirectory;
        private readonly string _clientConfigPath;
        private readonly Action<string> _log;
        private readonly ClientConfigService _configService = new ClientConfigService();

        private ClientConfig? _pendingConfig;

        public string RelaunchTarget { get; set; } = "None";

        public bool IsHeadless { get; set; }

        public SyncOrchestrator(string gameRootDirectory, string clientConfigPath, Action<string> log)
        {
            _gameRootDirectory = gameRootDirectory;
            _clientConfigPath = clientConfigPath;
            _log = log;
        }

        public async Task<(Manifest manifest, ClientConfig config, List<DiffResult> diff)> CheckAsync()
        {
            var config = _configService.LoadOrCreate(_clientConfigPath);
            var transport = new SptTransport(_log);

            _log($"[TCF-ModSync] Requesting {(IsHeadless ? "headless " : "")}manifest...");
            var manifest = await FetchManifestWithRetriesAsync(transport).ConfigureAwait(false);

            var offeredCount = manifest.Files.Count(f => f.Root == SptRootKind.Game);
            var reportedCount = manifest.Files.Count - offeredCount;
            _log($"[TCF-ModSync] Manifest received: {offeredCount} file(s) offered" +
                 (reportedCount > 0 ? $", and {reportedCount} server-side file(s) reported only." : "."));

            var scanner = new LocalScanner(_gameRootDirectory);
            var scan = await scanner.HashKnownPathsAsync(manifest, config, _log).ConfigureAwait(false);

            if (scan.DisabledPaths.Count > 0)
            {
                _log($"[TCF-ModSync] {scan.DisabledPaths.Count} offered file(s) are present but disabled - " +
                     "leaving them as they are.");
            }

            var diff = DiffEngine.BuildDiff(manifest, scan.Hashes, config, scan.DisabledPaths);
            _log($"[TCF-ModSync] Diff complete: {diff.Count} action(s) proposed.");
            return (manifest, config, diff);
        }

        private async Task<Manifest> FetchManifestWithRetriesAsync(SptTransport transport)
        {
            const int attempts = 3;
            var delay = TimeSpan.FromSeconds(3);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await transport.GetManifestAsync(IsHeadless).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    _log($"[TCF-ModSync] Manifest request failed (attempt {attempt} of {attempts}): " +
                         $"{Describe(ex)}. Retrying in {delay.TotalSeconds:F0}s...");
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
            }
        }

        public static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (var current = ex; current != null; current = current.InnerException)
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
            }

            return string.Join(" -> ", parts);
        }

        private const int MaxConcurrentDownloads = 4;

        public async Task<PendingOperations?> DownloadAsync(
            ClientConfig config,
            List<DiffResult> diff, HashSet<string> acceptedPaths, SyncProgress progress,
            PendingOperations? previouslyStaged = null)
        {
            var stagingDir = Path.Combine(Path.GetTempPath(), "TCFModSync", "staging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            var pending = new PendingOperations
            {
                RelaunchExecutable = FindRelaunchExecutable()
            };
            pending.StagingDirectories.Add(stagingDir);

            var reusable = new Dictionary<string, PendingOperation>(StringComparer.OrdinalIgnoreCase);
            if (previouslyStaged != null)
            {
                foreach (var op in previouslyStaged.Operations)
                {
                    if (op.Kind != PendingOpKind.CopyFromStaging) continue;
                    if (string.IsNullOrEmpty(op.RelativePath) || string.IsNullOrEmpty(op.ExpectedHash)) continue;
                    reusable[op.RelativePath + "|" + op.ExpectedHash] = op;
                }

                foreach (var directory in previouslyStaged.StagingDirectories)
                {
                    if (!pending.StagingDirectories.Contains(directory))
                        pending.StagingDirectories.Add(directory);
                }
            }

            var toDownload = diff
                .Where(d => (d.Action == FileAction.Add || d.Action == FileAction.Update)
                            && (!d.UserCanDecline || acceptedPaths.Contains(d.RelativePath)))
                .ToList();

            progress.FilesTotal = toDownload.Count;
            progress.BytesTotal = toDownload.Sum(d => d.Size ?? 0);

            var reusedCount = 0;
            var syncLock = new object();
            var toFetch = new List<DiffResult>();

            try
            {
                foreach (var item in diff)
                {
                    var accepted = !item.UserCanDecline || acceptedPaths.Contains(item.RelativePath);

                    if (!accepted)
                    {
                        if (!config.ExcludePatterns.Contains(item.RelativePath))
                            config.ExcludePatterns.Add(item.RelativePath);
                        continue;
                    }

                    var destination = Path.Combine(
                        _gameRootDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                    switch (item.Action)
                    {
                        case FileAction.Add:
                        case FileAction.Update:
                            var reuseKey = item.RelativePath + "|" + (item.ServerHash ?? "");
                            if (reusable.TryGetValue(reuseKey, out var alreadyStaged))
                            {
                                _log($"[TCF-ModSync] Reusing previously downloaded {item.RelativePath}.");
                                pending.Operations.Add(alreadyStaged);
                                reusedCount++;
                                progress.FilesDone++;
                                progress.BytesDone += item.Size ?? 0;
                                config.TrackedFiles[item.RelativePath] = item.ServerHash ?? "";
                            }
                            else
                            {
                                toFetch.Add(item);
                            }
                            break;

                        case FileAction.Delete:
                        case FileAction.Blacklist:
                            pending.Operations.Add(new PendingOperation
                            {
                                Kind = PendingOpKind.DeleteFile,
                                RelativePath = item.RelativePath,
                                DestinationAbsolutePath = destination
                            });
                            config.TrackedFiles.Remove(item.RelativePath);
                            break;

                        case FileAction.Adopt:
                            config.TrackedFiles[item.RelativePath] = item.ServerHash ?? "";
                            break;

                        case FileAction.Untrack:
                            config.TrackedFiles.Remove(item.RelativePath);
                            break;
                    }
                }
                if (toFetch.Count > 0)
                {
                    var transport = new SptTransport(_log);
                    using var throttle = new SemaphoreSlim(MaxConcurrentDownloads);
                    using var _ = SptTransport.ExtendedDownloadTimeout();

                    var downloadTasks = toFetch.Select(async item =>
                    {
                        await throttle.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var stagedPath = Path.Combine(
                                stagingDir, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                            var destination = Path.Combine(
                                _gameRootDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                            _log($"[TCF-ModSync] Downloading {item.RelativePath}...");
                            await transport.DownloadToAsync(item.RelativePath, stagedPath).ConfigureAwait(false);

                            var operation = new PendingOperation
                            {
                                Kind = PendingOpKind.CopyFromStaging,
                                RelativePath = item.RelativePath,
                                StagedAbsolutePath = stagedPath,
                                DestinationAbsolutePath = destination,
                                ExpectedHash = item.ServerHash
                            };

                            lock (syncLock)
                            {
                                pending.Operations.Add(operation);
                                progress.CurrentFile = item.RelativePath;
                                config.TrackedFiles[item.RelativePath] = item.ServerHash ?? "";
                            }

                            Interlocked.Increment(ref progress.FilesDone);
                            Interlocked.Add(ref progress.BytesDone, item.Size ?? 0);
                        }
                        finally
                        {
                            throttle.Release();
                        }
                    });

                    await Task.WhenAll(downloadTasks).ConfigureAwait(false);
                }
            }
            catch
            {
                progress.Error = "Download failed.";
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
                catch { }
                throw;
            }

            progress.Complete = true;
            if (reusedCount > 0)
                _log($"[TCF-ModSync] Reused {reusedCount} file(s) downloaded in a previous session.");

            if (pending.Operations.Count == 0)
            {
                _configService.Save(_clientConfigPath, config);
                Directory.Delete(stagingDir, recursive: true);
                return null;
            }

            _pendingConfig = config;
            return pending;
        }

        public bool CommitAndLaunchUpdater(PendingOperations pending)
        {
            if (_pendingConfig != null)
            {
                _configService.Save(_clientConfigPath, _pendingConfig);
                _pendingConfig = null;
            }

            var launcher = new UpdaterLauncher(_gameRootDirectory, _log);
            launcher.LaunchUpdaterAndPrepareForExit(pending);
            return true;
        }

        public int ResetExclusions()
        {
            var config = _configService.LoadOrCreate(_clientConfigPath);
            var removed = config.ExcludePatterns.Count;
            if (removed == 0) return 0;

            config.ExcludePatterns.Clear();
            _configService.Save(_clientConfigPath, config);
            _log($"[TCF-ModSync] Cleared {removed} exclusion(s); previously declined files will be offered again.");
            return removed;
        }

        public int CountExclusions()
        {
            try { return _configService.LoadOrCreate(_clientConfigPath).ExcludePatterns.Count; }
            catch { return 0; }
        }

        private string FindRelaunchExecutable()
        {
            var game = Path.Combine(_gameRootDirectory, "EscapeFromTarkov.exe");
            var launcher = FindLauncher();

            if (IsHeadless)
            {
                _log("[TCF-ModSync] Headless client will close after applying updates; " +
                     "FikaHeadlessManager will restart it.");
                return "";
            }

            switch (RelaunchTarget)
            {
                case "None":
                    return "";

                case "Game":
                    return File.Exists(game) ? game : "";

                case "Launcher":
                    if (!string.IsNullOrEmpty(launcher)) return launcher;
                    _log("[TCF-ModSync] RelaunchTarget is 'Launcher' but no launcher was found; using the game instead.");
                    return File.Exists(game) ? game : "";

                default:
                    return "";
            }
        }

        private string FindLauncher()
        {
            string[] candidates = { "SPT-Fika Launcher.exe", "SPT.Launcher.exe", "Aki.Launcher.exe", "SPT_Launcher.exe" };
            foreach (var name in candidates)
            {
                var path = Path.Combine(_gameRootDirectory, name);
                if (File.Exists(path)) return path;
            }
            return "";
        }
    }
}
