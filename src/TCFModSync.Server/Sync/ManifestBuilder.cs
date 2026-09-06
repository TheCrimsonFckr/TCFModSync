using TCFModSync.Shared.Globbing;
using TCFModSync.Shared.Hashing;
using TCFModSync.Shared.Models;
using TCFModSync.Shared.Paths;

namespace TCFModSync.Server.Sync;

public sealed class ManifestBuilder
{
    private sealed class CachedHash
    {
        public long Size;
        public DateTime LastWriteUtc;
        public string Hash = "";
    }

    private readonly Dictionary<string, CachedHash> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    private string GetHash(string cacheKey, string absolutePath, FileInfo info)
    {
        lock (_cacheLock)
        {
            if (_hashCache.TryGetValue(cacheKey, out var cached)
                && cached.Size == info.Length
                && cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.Hash;
            }
        }

        var hash = FileHasher.HashFile(absolutePath);

        lock (_cacheLock)
        {
            _hashCache[cacheKey] = new CachedHash
            {
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Hash = hash
            };
        }

        return hash;
    }

    public List<string> Diagnose(string rootDirectory, IEnumerable<string> includePatterns)
    {
        var lines = new List<string>();

        foreach (var pattern in includePatterns)
        {
            var normalized = pattern.Replace('\\', '/');
            var wildcardAt = normalized.IndexOfAny(new[] { '*', '?' });

            if (wildcardAt < 0)
            {
                var literalPath = SptPaths.Combine(rootDirectory, normalized);
                lines.Add(File.Exists(literalPath)
                    ? $"  '{pattern}' -> file exists"
                    : $"  '{pattern}' -> FILE NOT FOUND at {literalPath}");
                continue;
            }

            var lastSlash = normalized.LastIndexOf('/', Math.Max(wildcardAt - 1, 0));
            var prefix = lastSlash > 0 ? normalized.Substring(0, lastSlash) : "";
            var prefixPath = string.IsNullOrEmpty(prefix)
                ? rootDirectory
                : SptPaths.Combine(rootDirectory, prefix);

            if (!Directory.Exists(prefixPath))
            {
                lines.Add($"  '{pattern}' -> DIRECTORY DOES NOT EXIST: {prefixPath}");
                continue;
            }

            var fileCount = Directory.EnumerateFiles(prefixPath, "*", SearchOption.AllDirectories).Count();
            lines.Add(fileCount == 0
                ? $"  '{pattern}' -> directory exists but is EMPTY: {prefixPath}"
                : $"  '{pattern}' -> directory contains {fileCount} file(s); all were excluded or unmatched: {prefixPath}");
        }

        return lines;
    }

    // Game-root paths offered to clients, after exclusions and the headless narrowing.
    public static List<string> ResolveGamePaths(string gameRootDirectory, ServerConfig config, bool headless,
        out List<string> excludedPaths)
    {
        var candidatePaths = GlobMatcher.ResolveIncludedFiles(
            gameRootDirectory, config.IncludePatterns, Enumerable.Empty<string>());

        var relativePaths = GlobMatcher.FilterOutExcluded(candidatePaths, config.ExcludePatterns);

        excludedPaths = candidatePaths
            .Except(relativePaths, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!headless) return relativePaths;

        var headlessIncludeMatcher = new PatternMatcher(config.HeadlessIncludePatterns);
        var headlessExcludeMatcher = new PatternMatcher(config.HeadlessExcludePatterns);

        var beforeHeadlessFilter = relativePaths;
        relativePaths = beforeHeadlessFilter
            .Where(path => headlessIncludeMatcher.Matches(path))
            .Where(path => !headlessExcludeMatcher.Matches(path))
            .ToList();

        var droppedByHeadlessFilter = beforeHeadlessFilter
            .Except(relativePaths, StringComparer.OrdinalIgnoreCase);

        excludedPaths = excludedPaths
            .Concat(droppedByHeadlessFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return relativePaths;
    }

    // Server-root paths. Empty whenever no server root was resolved or nothing opted in.
    public static List<string> ResolveServerPaths(string? serverRootDirectory, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(serverRootDirectory)) return new List<string>();
        if (config.ServerIncludePatterns.Count == 0) return new List<string>();
        if (!Directory.Exists(serverRootDirectory)) return new List<string>();

        var candidates = GlobMatcher.ResolveIncludedFiles(
            serverRootDirectory!, config.ServerIncludePatterns, Enumerable.Empty<string>());

        return GlobMatcher.FilterOutExcluded(candidates, config.ServerExcludePatterns);
    }

    public Manifest Build(string gameRootDirectory, string? serverRootDirectory, ServerConfig config,
        string sptVersion, bool headless = false)
    {
        var gamePaths = ResolveGamePaths(gameRootDirectory, config, headless, out var excludedPaths);
        var serverPaths = ResolveServerPaths(serverRootDirectory, config);

        var files = new List<ManifestEntry>(gamePaths.Count + serverPaths.Count);

        AddEntries(files, SptRootKind.Game, gameRootDirectory, gamePaths);

        if (serverPaths.Count > 0)
        {
            AddEntries(files, SptRootKind.Server, serverRootDirectory!, serverPaths);
        }

        return new Manifest
        {
            ServerConfigVersion = config.ConfigVersion,
            Files = files,
            FileHashBlacklist = config.FileHashBlacklist,
            ExcludedPaths = excludedPaths,
            SptVersion = sptVersion
        };
    }

    private void AddEntries(List<ManifestEntry> files, SptRootKind root, string rootDirectory,
        List<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var absolutePath = SptPaths.Combine(rootDirectory, relativePath);
            var info = new FileInfo(absolutePath);
            if (!info.Exists) continue;

            files.Add(new ManifestEntry
            {
                RelativePath = relativePath,
                Root = root,
                Size = info.Length,
                Hash = GetHash($"{root}:{relativePath}", absolutePath, info)
            });
        }
    }
}
