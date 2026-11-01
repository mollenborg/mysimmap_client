using System;

namespace mysimmap_client;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public static class XPlaneText
{
    public static async Task<string?> ReadAsync(HttpClient http, string datarefName)
    {
        // 1. Look up the dataref id by name
        var lookupUrl = $"http://localhost:8086/api/v2/datarefs?filter[name]={datarefName}";
        using var lookup = JsonDocument.Parse(await http.GetStringAsync(lookupUrl));
        var items = lookup.RootElement.GetProperty("data");
        if (items.GetArrayLength() == 0) return null;
        var id = items[0].GetProperty("id").GetInt64();

        // 2. Fetch the value – byte datarefs are returned as base64
        using var value = JsonDocument.Parse(
            await http.GetStringAsync($"http://localhost:8086/api/v2/datarefs/{id}/value"));
        var base64 = value.RootElement.GetProperty("data").GetString();
        if (string.IsNullOrEmpty(base64)) return null;

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64))
            .TrimEnd('\0')
            .Trim();
        return text.Length == 0 ? null : text;
    }
}