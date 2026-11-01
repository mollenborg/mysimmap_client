using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace mysimmap_client;

public sealed record AppConfig(string Email, string ApiKey)
{
    private const string FileName = "config.conf";

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, FileName);

    public static async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig("", "");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in await File.ReadAllLinesAsync(ConfigPath))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
                continue;

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();

            values[key] = value;
        }

        return new AppConfig(
            values.GetValueOrDefault("email", ""),
            values.GetValueOrDefault("apiKey", ""));
    }

    public async Task SaveAsync()
    {
        var lines = new[]
        {
            $"email={Email}",
            $"apiKey={ApiKey}"
        };

        await File.WriteAllLinesAsync(ConfigPath, lines);
    }
}