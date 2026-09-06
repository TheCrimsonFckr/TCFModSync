using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TCFModSync.Shared.Hashing;
using TCFModSync.Shared.Models;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Client.Sync
{
    public sealed class LocalScanResult
    {
        public Dictionary<string, string> Hashes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Paths whose file sits in a ".disabled" container instead of the live one. Present, but
        // deliberately turned off, so the diff must not download over them or delete them.
        public HashSet<string> DisabledPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class LocalScanner
    {
        private const int MaxConcurrentHashes = 4;

        private readonly string _gameRootDirectory;

        public LocalScanner(string gameRootDirectory)
        {
            _gameRootDirectory = gameRootDirectory;
        }

        public async Task<LocalScanResult> HashKnownPathsAsync(
            Manifest manifest, ClientConfig clientConfig, Action<string>? log = null, CancellationToken ct = default)
        {
            var scan = new LocalScanResult();

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                if (f.Root == SptRootKind.Game) candidates.Add(f.RelativePath);
            }

            foreach (var p in clientConfig.TrackedFiles.Keys) candidates.Add(p);

            var existing = new List<(string rel, string abs)>();

            foreach (var rel in candidates)
            {
                var abs = Path.Combine(_gameRootDirectory, SptPaths.ToNative(rel));
                if (File.Exists(abs))
                {
                    existing.Add((rel, abs));
                    continue;
                }

                if (SptPaths.TryGetCounterpart(rel, out var disabledRel)
                    && File.Exists(Path.Combine(_gameRootDirectory, SptPaths.ToNative(disabledRel))))
                {
                    scan.DisabledPaths.Add(rel);
                }
            }

            var totalBytes = existing.Sum(x => new FileInfo(x.abs).Length);
            log?.Invoke($"[TCF-ModSync] Hashing {existing.Count} local file(s), {totalBytes / 1024.0 / 1024.0:F0} MB total...");

            var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            var reportLock = new object();
            var lastReport = DateTime.UtcNow;

            using var throttle = new SemaphoreSlim(MaxConcurrentHashes);

            var hashTasks = existing.Select(async item =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var hash = await FileHasher.HashFileAsync(item.abs, ct).ConfigureAwait(false);
                    result[item.rel] = hash;

                    var current = Interlocked.Increment(ref done);
                    lock (reportLock)
                    {
                        if ((DateTime.UtcNow - lastReport).TotalSeconds >= 5)
                        {
                            log?.Invoke($"[TCF-ModSync] Hashed {current}/{existing.Count} local file(s)...");
                            lastReport = DateTime.UtcNow;
                        }
                    }
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(hashTasks).ConfigureAwait(false);

            log?.Invoke($"[TCF-ModSync] Local scan complete ({existing.Count} file(s) hashed).");

            scan.Hashes = new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
            return scan;
        }
    }
}
