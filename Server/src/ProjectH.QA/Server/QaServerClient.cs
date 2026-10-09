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
    // Phase 19: GET /qa/vehicles (the vehicles, also keyed by id, and the vehicle counters).
    Task<JsonElement> GetVehiclesAsync(CancellationToken token);
    Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token);
    Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token);
    Task StopServerAsync(CancellationToken token);
}

public sealed record CommandResponse(bool Ok, string? Error, JsonElement? Result, int StatusCode);

// The API answered with an error status (anything but the documented ok/404 cases), or not at all.
public sealed class QaApiException : Exception
{
    // 기능: QA API 오류 예외를 만든다.
    // 입력: message - 오류 설명, statusCode - HTTP 상태 코드(응답이 없으면 0), inner - 원인 예외.
    // 출력: StatusCode가 설정된 QaApiException.
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

    // 기능: QA Control API용 HttpClient를 만든다(전체 Timeout은 무한, 요청마다 Linked로 제한).
    // 입력: baseAddress - 서버 QA API 기본 주소(http://127.0.0.1:port/).
    // 출력: 응답 크기가 MaxResponseBytes로 제한된 QaServerClient.
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

    // 기능: GET /qa/health를 부른다.
    // 입력: token - 취소 토큰.
    // 출력: 서버 상태 JSON. 오류 상태·전송 실패·시간 초과면 QaApiException.
    public Task<JsonElement> GetHealthAsync(CancellationToken token) => GetAsync("qa/health", token);

    // 기능: POST /qa/command로 QA 명령을 보내고 응답을 CommandResponse로 정리한다.
    // 입력: command - 명령 이름, player - 대상 플레이어 ID(없으면 null), args - 명령 인자 JSON, runId - 실행 ID, token - 취소 토큰.
    // 출력: Ok(성공 상태이고 ok=false가 아님)·Error(실패 시 서버 메시지 또는 "HTTP n")·Result·StatusCode. 전송 실패·시간 초과면 QaApiException.
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

    // 기능: GET /qa/players/{id}로 플레이어 상태를 조회한다.
    // 입력: devPlayerId - 조회할 Dev 플레이어 ID, token - 취소 토큰.
    // 출력: 플레이어 JSON. 서버에 없으면(404) null. 그 밖의 오류는 QaApiException.
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

    // 기능: GET /qa/players를 부른다.
    // 입력: token - 취소 토큰.
    // 출력: 플레이어 목록 JSON. 실패하면 QaApiException.
    public Task<JsonElement> GetPlayersAsync(CancellationToken token) => GetAsync("qa/players", token);

    // 기능: GET /qa/match를 부른다.
    // 입력: token - 취소 토큰.
    // 출력: Match 상태 JSON. 실패하면 QaApiException.
    public Task<JsonElement> GetMatchAsync(CancellationToken token) => GetAsync("qa/match", token);

    // 기능: GET /qa/build를 부른다(x·z·radius·max가 있으면 쿼리로 붙인다).
    // 입력: x·z·radius - 조각을 볼 원(없으면 전체), max - 최대 조각 수, token - 취소 토큰.
    // 출력: 건설 조각 JSON. 실패하면 QaApiException.
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

    // 기능: Phase 19: GET /qa/vehicles를 부른다.
    // 입력: token - 취소.
    // 출력: 응답 JSON의 data.
    public Task<JsonElement> GetVehiclesAsync(CancellationToken token) => GetAsync("qa/vehicles", token);

    // 기능: GET /qa/metrics를 부른다.
    // 입력: windowSeconds - 집계 창(초, null이면 서버 기본), token - 취소 토큰.
    // 출력: 서버 지표 JSON. 실패하면 QaApiException.
    public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) =>
        GetAsync(windowSeconds == null ? "qa/metrics" : $"qa/metrics?windowSeconds={windowSeconds.Value}", token);

    // 기능: GET /qa/events로 seq가 after보다 큰 이벤트를 max개까지 받는다.
    // 입력: after - 마지막으로 받은 seq(exclusive), max - 최대 개수, token - 취소 토큰.
    // 출력: events·next·dropped가 담긴 JSON. 실패하면 QaApiException.
    public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token) =>
        GetAsync($"qa/events?after={after}&max={max}", token);

    // 기능: POST /qa/server/stop으로 서버 종료를 요청한다.
    // 입력: token - 취소 토큰.
    // 출력: 반환값 없음. 성공 상태가 아니거나 전송 실패·시간 초과면 QaApiException.
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

    // 기능: HttpClient(와 그 Handler)를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 이후 요청은 ObjectDisposedException이 QaApiException으로 바뀌어 실패한다.
    public void Dispose() => _http.Dispose();

    // 기능: 요청별 Timeout을 걸고 GET을 보낸 뒤 성공 응답의 JSON을 돌려준다.
    // 입력: path - 상대 경로, token - 취소 토큰.
    // 출력: 응답 JSON 루트. 오류 상태·빈 응답·전송 실패면 QaApiException.
    private async Task<JsonElement> GetAsync(string path, CancellationToken token)
    {
        using CancellationTokenSource cts = Linked(token);
        using HttpResponseMessage response = await Send(path, cts.Token, token).ConfigureAwait(false);
        return await ReadSuccess(response, path, cts.Token).ConfigureAwait(false);
    }

    // 기능: GET 요청을 보내고 전송 실패·시간 초과를 QaApiException으로 바꾼다(호출자 취소는 그대로 전파).
    // 입력: path - 상대 경로, linked - RequestTimeout이 묶인 토큰, caller - 호출자 토큰.
    // 출력: 응답 메시지(상태 검사는 호출자 몫). 전송 실패면 QaApiException.
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

    // 기능: 응답을 JSON으로 읽고, 성공 상태가 아니면 서버 error 메시지를 담아 예외를 던진다.
    // 입력: response - 응답 메시지, path - 메시지용 경로, token - 취소 토큰.
    // 출력: 응답 JSON 루트. 오류 상태이거나 본문이 비었으면 QaApiException.
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

    // 기능: 응답 본문을 JSON으로 파싱한다.
    // 입력: response - 응답 메시지, token - 취소 토큰.
    // 출력: 파싱된 루트 요소 복제본. 본문이 비었거나 JSON이 아니면 null.
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

    // 기능: 호출자 토큰에 RequestTimeout을 묶은 토큰 소스를 만든다.
    // 입력: token - 호출자 토큰.
    // 출력: 호출자가 Dispose해야 하는 CancellationTokenSource.
    private static CancellationTokenSource Linked(CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    // 기능: 전송 예외를 사람이 읽을 메시지로 바꾼다.
    // 입력: e - 전송 중 잡은 예외.
    // 출력: 시간 초과·Client 닫힘·그 외(e.Message) 설명 문자열.
    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => $"no answer within {RequestTimeout.TotalSeconds:0} s",
        ObjectDisposedException => "this QA client was closed (its server was stopped or replaced by a restart)",
        _ => e.Message,
    };
}
