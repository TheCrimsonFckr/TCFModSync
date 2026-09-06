using System.Text.Json;
using TCFModSync.Shared.Globbing;
using TCFModSync.Shared.Models;

namespace TCFModSync.Server.Config;

public sealed class ServerConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public ServerConfig LoadOrCreate(string modDirectory)
    {
        var configDir = Path.Combine(modDirectory, "config");
        var configPath = Path.Combine(configDir, "serverConfig.json");
        Directory.CreateDirectory(configDir);

        if (!File.Exists(configPath))
        {
            var defaultPath = Path.Combine(modDirectory, "config", "serverConfig.default.json");
            var defaultJson = File.Exists(defaultPath)
                ? File.ReadAllText(defaultPath)
                : JsonSerializer.Serialize(new ServerConfig(), JsonOptions);
            File.WriteAllText(configPath, defaultJson);
        }

        var json = File.ReadAllText(configPath);

        ServerConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ServerConfig>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"serverConfig.json is not valid JSON: {ex.Message}{Environment.NewLine}" +
                $"File: {configPath}{Environment.NewLine}" +
                (ex.LineNumber.HasValue
                    ? $"Look at line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1}."
                    : "Check for a missing quote, brace, or bracket."), ex);
        }

        if (config == null)
        {
            throw new InvalidOperationException(
                $"serverConfig.json parsed to nothing - it's probably empty or contains only 'null'. File: {configPath}");
        }

        ValidatePatterns(config);
        return config;
    }

    private static void ValidatePatterns(ServerConfig config)
    {
        var bad = config.IncludePatterns.Concat(config.ExcludePatterns)
            .Concat(config.ServerIncludePatterns).Concat(config.ServerExcludePatterns)
            .Concat(config.HeadlessIncludePatterns).Concat(config.HeadlessExcludePatterns)
            .Where(p => !GlobMatcher.IsSafeRelativePattern(p))
            .ToList();

        if (bad.Count > 0)
        {
            throw new InvalidOperationException(
                "serverConfig.json contains unsafe path pattern(s): " + string.Join(", ", bad) +
                ". Patterns must be relative to their root and cannot contain '..' or a drive letter.");
        }
    }
}
