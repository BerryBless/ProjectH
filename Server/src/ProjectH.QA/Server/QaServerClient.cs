using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectH.QA;

// The server's QA Control API (design doc "QA Control API"). An interface only so runner tests can use a fake server.
public interface IQaServerClient
{
    Task<JsonElement> GetHealthAsync(CancellationToken token);
    Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token);
    // Null when the server has no such player (404).
    Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token);
    Task<JsonElement> GetPlayersAsync(CancellationToken token);
    Task<JsonElement> GetMatchAsync(CancellationToken token);
    Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token);
    // Phase 16: GET /qa/loot (containers, supply drops, world items within radius of x/z when given).
    Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token);
    // Phase 17: GET /qa/projectiles (live projectiles, the recent explosions and the projectile counters).
    Task<JsonElement> GetProjectilesAsync(CancellationToken token);
    Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token);
    Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token);
    Task StopServerAsync(CancellationToken token);
}

public sealed record CommandResponse(bool Ok, string? Error, JsonElement? Result, int StatusCode);

// The API answered with an error status (anything but the documented ok/404 cases), or not at all.
public sealed class QaApiException : Exception
{
    public QaApiException(string message, int statusCode = 0, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

// HttpClient over 127.0.0.1. One per run; disposed with the run (it owns its handler). Every request has its own
// timeout (RequestTimeout) linked to the caller's token, so a hung server cannot stall a step beyond its limit.
public sealed class QaServerClient : IQaServerClient, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxResponseBytes = 8 * 1024 * 1024;   // players/events/build lists are bounded server side

    private readonly HttpClient _http;

    public QaServerClient(Uri baseAddress)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,   // per request below
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public Task<JsonElement> GetHealthAsync(CancellationToken token) => GetAsync("qa/health", token);

    public async Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token)
    {
        var body = new Dictionary<string, object?> { ["runId"] = runId, ["command"] = command, ["args"] = args };
        if (player != null) body["player"] = player;
        using CancellationTokenSource cts = Linked(token);
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("qa/command", body, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or ObjectDisposedException || (e is OperationCanceledException && !token.IsCancellationRequested))
        {
            throw new QaApiException($"POST /qa/command {command}: {Describe(e)}", 0, e);
        }
        using (response)
        {
            JsonElement? json = await ReadJson(response, cts.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            bool ok = response.IsSuccessStatusCode && (json == null || JsonPath.Child(json.Value, "ok") is not { ValueKind: JsonValueKind.False });
            string? error = json != null && JsonPath.Child(json.Value, "error") is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
            if (!ok && error == null) error = $"HTTP {status}";
            return new CommandResponse(ok, error, json != null ? JsonPath.Child(json.Value, "result") : null, status);
        }
    }

    public async Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token)
    {
        using CancellationTokenSource cts = Linked(token);
        string path = "qa/players/" + Uri.EscapeDataString(devPlayerId);
        HttpResponseMessage response = await Send(path, cts.Token, token).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            return await ReadSuccess(response, path, cts.Token).ConfigureAwait(false);
        }
    }

    public Task<JsonElement> GetPlayersAsync(CancellationToken token) => GetAsync("qa/players", token);

    public Task<JsonElement> GetMatchAsync(CancellationToken token) => GetAsync("qa/match", token);

    public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token)
    {
        var query = new List<string>();
        if (x != null) query.Add($"x={x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (z != null) query.Add($"z={z.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (radius != null) query.Add($"radius={radius.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (max != null) query.Add($"max={max.Value}");
        return GetAsync(query.Count == 0 ? "qa/build" : "qa/build?" + string.Join('&', query), token);
    }

    // 기능: Phase 16: GET /qa/loot를 부른다(x·z·radius는 함께 주거나 모두 뺀다).
    // 입력: x·z·radius - 월드 아이템을 볼 원, token - 취소.
    // 출력: 응답 JSON의 data.
    public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token)
    {
        var query = new List<string>();
        if (x != null) query.Add($"x={x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (z != null) query.Add($"z={z.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (radius != null) query.Add($"radius={radius.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return GetAsync(query.Count == 0 ? "qa/loot" : "qa/loot?" + string.Join('&', query), token);
    }

    // 기능: Phase 17: GET /qa/projectiles를 부른다.
    // 입력: token - 취소.
    // 출력: 응답 JSON의 data.
    public Task<JsonElement> GetProjectilesAsync(CancellationToken token) => GetAsync("qa/projectiles", token);

    public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) =>
        GetAsync(windowSeconds == null ? "qa/metrics" : $"qa/metrics?windowSeconds={windowSeconds.Value}", token);

    public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token) =>
        GetAsync($"qa/events?after={after}&max={max}", token);

    public async Task StopServerAsync(CancellationToken token)
    {
        using CancellationTokenSource cts = Linked(token);
        try
        {
            using HttpResponseMessage response = await _http.PostAsync("qa/server/stop", null, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new QaApiException($"POST /qa/server/stop: HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or ObjectDisposedException || (e is OperationCanceledException && !token.IsCancellationRequested))
        {
            throw new QaApiException($"POST /qa/server/stop: {Describe(e)}", 0, e);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> GetAsync(string path, CancellationToken token)
    {
        using CancellationTokenSource cts = Linked(token);
        using HttpResponseMessage response = await Send(path, cts.Token, token).ConfigureAwait(false);
        return await ReadSuccess(response, path, cts.Token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> Send(string path, CancellationToken linked, CancellationToken caller)
    {
        try
        {
            return await _http.GetAsync(path, linked).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or ObjectDisposedException || (e is OperationCanceledException && !caller.IsCancellationRequested))
        {
            throw new QaApiException($"GET /{path}: {Describe(e)}", 0, e);
        }
    }

    private static async Task<JsonElement> ReadSuccess(HttpResponseMessage response, string path, CancellationToken token)
    {
        JsonElement? json = await ReadJson(response, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string error = json != null && JsonPath.Child(json.Value, "error") is { ValueKind: JsonValueKind.String } e ? e.GetString()! : string.Empty;
            throw new QaApiException($"GET /{path}: HTTP {(int)response.StatusCode} {error}".TrimEnd(), (int)response.StatusCode);
        }
        return json ?? throw new QaApiException($"GET /{path}: empty response", (int)response.StatusCode);
    }

    private static async Task<JsonElement?> ReadJson(HttpResponseMessage response, CancellationToken token)
    {
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CancellationTokenSource Linked(CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => $"no answer within {RequestTimeout.TotalSeconds:0} s",
        ObjectDisposedException => "this QA client was closed (its server was stopped or replaced by a restart)",
        _ => e.Message,
    };
}
