using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http.Headers;

namespace mysimmap_client;

public record AircraftPosition(
    double Latitude,
    double Longitude,
    double AltitudeFt,
    double HeadingDeg,
    double GroundSpeedKt,
    string AircraftDescription);

public sealed class PositionTracker
{
    private const string UploadUrl = "https://api.mysimmap.net/api/position";
    // private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(5);

    private const string AircraftDescriptionDataref = "sim/aircraft/view/acf_ICAO";
    
    private static readonly string[] DatarefNames =
    {
        "sim/flightmodel/position/latitude",     // degrees
        "sim/flightmodel/position/longitude",    // degrees
        "sim/flightmodel/position/elevation",    // meter MSL
        "sim/flightmodel/position/true_psi",     // degrees, true heading
        "sim/flightmodel/position/groundspeed",  // m/s
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _host;
    private readonly int _port;
    private readonly string _email;
    private readonly string _apiKey;
    private readonly string _callsign;
    private readonly string _departureIcao;
    private readonly string _arrivalIcao;

    private readonly double[] _values = new double[DatarefNames.Length];
    private readonly bool[] _received = new bool[DatarefNames.Length];
    private string _aircraftDescription = "";
    private long _aircraftDescriptionId;
    private readonly object _lock = new();

    public event Action<string>? StatusChanged;
    public event Action<AircraftPosition>? PositionUpdated;

    public PositionTracker(string host, int port, string email, string apiKey,
        string callsign, string departureIcao, string arrivalIcao)
    {
        _host = host;
        _port = port;
        _email = email;
        _apiKey = apiKey;
        _callsign = callsign;
        _departureIcao = departureIcao;
        _arrivalIcao = arrivalIcao;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke($"Connecting to X-Plane at {_host}:{_port}…");

        // 1. Look up dataref IDs (they are not stable between X-Plane sessions)
        var idToIndex = new Dictionary<long, int>();
        for (var i = 0; i < DatarefNames.Length; i++)
            idToIndex[await LookupIdAsync(DatarefNames[i], ct)] = i;
        
        _aircraftDescriptionId = await LookupIdAsync(AircraftDescriptionDataref, ct);
        
        // 2. Open WebSocket and subscribe
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/api/v2"), ct);

        var subscribeIds = idToIndex.Keys.Append(_aircraftDescriptionId);

        var subscribe = new
        {
            req_id = 1,
            type = "dataref_subscribe_values",
            @params = new { datarefs = subscribeIds.Select(id => new { id }).ToArray() }
        };
        
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(subscribe),
            WebSocketMessageType.Text, true, ct);

        StatusChanged?.Invoke("Connected – waiting for data…");

        // 3. Receive and upload in parallel; stop both if one fails
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(ws, idToIndex, linked.Token);
        var upload = UploadLoopAsync(linked.Token);

        var first = await Task.WhenAny(receive, upload);
        linked.Cancel();
        try { await Task.WhenAll(receive, upload); } catch { /* the real error is rethrown below */ }

        await CloseQuietlyAsync(ws);
        await first; // rethrows the original error (or OperationCanceledException on stop)
    }

    private async Task<long> LookupIdAsync(string name, CancellationToken ct)
    {
        var url = $"http://{_host}:{_port}/api/v2/datarefs?filter[name]={Uri.EscapeDataString(name)}";
        await using var stream = await Http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new InvalidOperationException($"Dataref not found: {name}");

        return data[0].GetProperty("id").GetInt64();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, Dictionary<long, int> idToIndex, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            ms.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("X-Plane closed the connection.");
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            HandleMessage(ms.ToArray(), idToIndex);
        }
    }

    private void HandleMessage(byte[] json, Dictionary<long, int> idToIndex)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        if (type == "result" &&
            root.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var msg = root.TryGetProperty("error_message", out var em) ? em.GetString() : "unknown error";
            throw new InvalidOperationException($"X-Plane rejected the subscription: {msg}");
        }

        if (type != "dataref_update_values") return;

        lock (_lock)
        {
            foreach (var prop in root.GetProperty("data").EnumerateObject())
            {
                if (!long.TryParse(prop.Name, out var id))
                    continue;

                if (id == _aircraftDescriptionId)
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        _aircraftDescription = DecodeXPlaneString(prop.Value.GetString());
                    continue;
                }

                if (idToIndex.TryGetValue(id, out var idx) &&
                    prop.Value.ValueKind == JsonValueKind.Number)
                {
                    _values[idx] = prop.Value.GetDouble();
                    _received[idx] = true;
                }
            }
        }

        if (TryGetPosition(out var pos))
            PositionUpdated?.Invoke(pos);
    }

    private sealed record UploadResponse(int IntervalSeconds, string? ShareUrl);
    
    public event Action<string>? ShareUrlChanged;
    private string? _shareUrl;

private async Task UploadLoopAsync(CancellationToken ct)
{
    // Conservative until the server tells us the user's interval
    var interval = TimeSpan.FromSeconds(30);

    // Wait for the first complete position and send it immediately
    while (!TryGetPosition(out _))
        await Task.Delay(500, ct);

    while (true)
    {
        TryGetPosition(out var p);

        var payload = new
        {
            email = _email,
            callsign = _callsign,
            departureIcao = _departureIcao,
            arrivalIcao = _arrivalIcao,
            latitude = p.Latitude,
            longitude = p.Longitude,
            altitudeFt = p.AltitudeFt,
            headingDeg = p.HeadingDeg,
            groundSpeedKt = p.GroundSpeedKt,
            aircraftDescription = p.AircraftDescription,
            timestampUtc = DateTime.UtcNow
        };

        // Console.WriteLine($"Sending aircraftDescription: '{p.AircraftDescription}'");
        
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl)
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await Http.SendAsync(req, ct);
            
            // Console.WriteLine($"Response: {await resp.Content.ReadAsStringAsync(ct)}");

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("The API key was rejected by mysimmap.net (401).");
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Access blocked (403) – probably Cloudflare, not the key.");

            interval = await ReadResponseAsync(resp, interval, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                StatusChanged?.Invoke($"Too fast – waiting {interval.TotalSeconds:F0} sec.");
            }
            else
            {
                resp.EnsureSuccessStatusCode();
                StatusChanged?.Invoke($"Sent {DateTime.Now:HH:mm:ss} (every {interval.TotalSeconds:F0} sec.)");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            StatusChanged?.Invoke($"Upload failed ({DateTime.Now:HH:mm:ss}): {ex.Message}");
        }

        await Task.Delay(interval, ct);
    }
}

// private static async Task<TimeSpan> ReadIntervalAsync(HttpResponseMessage resp, TimeSpan fallback, CancellationToken ct)
// {
//     try
//     {
//         var body = await resp.Content.ReadFromJsonAsync<IntervalResponse>(ct);
//         if (body is { IntervalSeconds: >= 1 and <= 3600 })
//             return TimeSpan.FromSeconds(body.IntervalSeconds);
//     }
//     catch (JsonException) { }          // empty response, e.g. from the rate limiter
//     catch (NotSupportedException) { }  // no JSON content type
//
//     return fallback;
// }

private async Task<TimeSpan> ReadResponseAsync(HttpResponseMessage resp, TimeSpan fallback, CancellationToken ct)
{
    try
    {
        var body = await resp.Content.ReadFromJsonAsync<UploadResponse>(ct);
        if (body is null) return fallback;

        if (!string.IsNullOrEmpty(body.ShareUrl) && body.ShareUrl != _shareUrl)
        {
            _shareUrl = body.ShareUrl;
            ShareUrlChanged?.Invoke(body.ShareUrl);   // only when the flight changes
        }

        if (body.IntervalSeconds is >= 1 and <= 3600)
            return TimeSpan.FromSeconds(body.IntervalSeconds);
    }
    catch (JsonException) { }
    catch (NotSupportedException) { }

    return fallback;
}

    private bool TryGetPosition(out AircraftPosition pos)
    {
        lock (_lock)
        {
            if (_received.Any(r => !r))
            {
                pos = default!;
                return false;
            }

            pos = new AircraftPosition(
                Latitude: _values[0],
                Longitude: _values[1],
                AltitudeFt: _values[2] * 3.28084,
                HeadingDeg: (_values[3] % 360 + 360) % 360,
                GroundSpeedKt: _values[4] * 1.943844,
                AircraftDescription: _aircraftDescription);
            return true;
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", timeout.Token);
        }
        catch { /* doesn't matter on shutdown */ }
    }
    
    private static string DecodeXPlaneString(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var nul = Array.IndexOf(bytes, (byte)0);         // X-Plane pads with 0 bytes
            var length = nul >= 0 ? nul : bytes.Length;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, length).Trim();
        }
        catch (FormatException)
        {
            return "";
        }
    }
}