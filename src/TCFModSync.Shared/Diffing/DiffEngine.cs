using TCFModSync.Shared.Globbing;
using TCFModSync.Shared.Models;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Shared.Diffing;

public sealed class DiffResult
{
    public string RelativePath { get; set; } = "";
    public FileAction Action { get; set; }
    public string? ServerHash { get; set; }
    public long? Size { get; set; }

    public bool UserCanDecline => Action != FileAction.Blacklist;
}

public static class DiffEngine
{
    // disabledPaths are files the client holds inside a ".disabled" container. They are present, so
    // nothing is downloaded over them, and deliberately turned off, so nothing deletes them either.
    public static List<DiffResult> BuildDiff(
        Manifest manifest,
        IReadOnlyDictionary<string, string> localFileHashes,
        ClientConfig clientConfig,
        ISet<string>? disabledPaths = null)
    {
        var results = new List<DiffResult>();

        var offeredFiles = manifest.Files.Where(f => f.Root == SptRootKind.Game).ToList();
        var offeredPaths = new HashSet<string>(offeredFiles.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var excludedByServer = new HashSet<string>(manifest.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        var disabled = disabledPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hasAllowList = clientConfig.IncludePatterns.Count > 0;
        var excludeMatcher = new PatternMatcher(clientConfig.ExcludePatterns);
        var includeMatcher = new PatternMatcher(clientConfig.IncludePatterns);

        foreach (var entry in offeredFiles)
        {
            if (disabled.Contains(entry.RelativePath)) continue;

            var isBlacklisted = manifest.FileHashBlacklist.Contains(entry.Hash, StringComparer.OrdinalIgnoreCase);
            var isUserExcluded = excludeMatcher.Matches(entry.RelativePath);
            var existsLocally = localFileHashes.TryGetValue(entry.RelativePath, out var localHash);

            FileAction action;

            var isAllowed = !hasAllowList || includeMatcher.Matches(entry.RelativePath);

            if (isBlacklisted && existsLocally)
            {
                action = FileAction.Blacklist;
            }
            else if (!isAllowed)
            {
                if (!clientConfig.TrackedFiles.ContainsKey(entry.RelativePath)) continue;

                action = existsLocally ? FileAction.Delete : FileAction.Untrack;
            }
            else if (isUserExcluded)
            {
                action = FileAction.Untrack;
            }
            else if (!existsLocally)
            {
                action = FileAction.Add;
            }
            else if (!string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                action = FileAction.Update;
            }
            else if (!clientConfig.TrackedFiles.ContainsKey(entry.RelativePath))
            {
                action = FileAction.Adopt;
            }
            else
            {
                continue;
            }

            results.Add(new DiffResult
            {
                RelativePath = entry.RelativePath,
                Action = action,
                ServerHash = entry.Hash,
                Size = entry.Size
            });
        }

        foreach (var trackedPath in clientConfig.TrackedFiles.Keys)
        {
            if (offeredPaths.Contains(trackedPath)) continue;
            if (disabled.Contains(trackedPath)) continue;

            var isUserExcluded = excludeMatcher.Matches(trackedPath);
            if (isUserExcluded) continue;

            if (excludedByServer.Contains(trackedPath))
            {
                results.Add(new DiffResult { RelativePath = trackedPath, Action = FileAction.Untrack });
                continue;
            }

            var existsLocally = localFileHashes.ContainsKey(trackedPath);

            results.Add(new DiffResult
            {
                RelativePath = trackedPath,
                Action = existsLocally ? FileAction.Delete : FileAction.Untrack
            });
        }

        return results.OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
