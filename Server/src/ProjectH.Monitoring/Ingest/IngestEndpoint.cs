using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Ingest;

// Monitoring D8: POST /api/ingest/metrics. Order: body size (Kestrel, 413) → token (401) → JSON or a body Kestrel refused
// while reading it (its own status: 400 truncated/broken, 408 too slow, 413 over the limit) → values (400) → store (429 when a new server would exceed MaxServers) → 202. Logs happen outside the
// store's lock, and only validated text (ServerId, Version) reaches a log line.
public static class IngestEndpoint
{
    // 기능: Ingest 경로를 등록한다.
    // 입력: app - 앱.
    // 출력: 반환값 없음. POST IngestPath가 HandleAsync로 연결된다.
    public static void MapIngest(this WebApplication app)
    {
        app.MapPost(MonitoringContract.IngestPath, HandleAsync);
    }

    // 기능: Snapshot 하나를 받아 검증하고 저장한다. 어떤 입력에도 예외로 끝나지 않는다.
    // 입력: context - 요청, options·store·time·log·logger - 서비스, cancellation - 요청 취소.
    // 출력: 202(저장), 400(JSON·값 오류, 잘린 본문·깨진 chunked), 401(Token), 413(본문 초과), 429(MaxServers 초과).
    //       본문을 읽다 난 BadHttpRequestException은 Kestrel이 정한 그 StatusCode로 답한다(400 잘림·깨진 chunked, 408 너무 느림, 413 초과).
    // internal: IngestEndpointTests calls it with a DefaultHttpContext to pin the 413 decision without a socket.
    internal static async Task<IResult> HandleAsync(HttpContext context, MonitoringServerOptions options, MetricStore store, TimeProvider time,
        IngestLog log, ILogger<MetricStore> logger, CancellationToken cancellation)
    {
        // A declared length over the limit is refused before anything is read (Kestrel would refuse the read anyway).
        // Kestrel then aborts the connection because it will not drain an over-limit body, so a client may see a reset
        // instead of this 413. Either way nothing is read or stored.
        if (context.Request.ContentLength > MonitoringContract.MaxBodyBytes)
        {
            log.Invalid("body too large", null);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        if (options.IngestToken.Length > 0 && !TokenMatches(context, options.IngestToken))
        {
            log.Invalid("unauthorized", null);
            return Results.Unauthorized();
        }

        ServerMonitoringSnapshot? snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<ServerMonitoringSnapshot>(context.Request.Body, JsonSerializerOptions.Web, cancellation);
        }
        catch (JsonException ex)
        {
            log.Invalid("invalid json: " + ex.Message, null);
            return Results.BadRequest(new { error = "invalid json" });
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel's own verdict on the body: 413 when it passed the limit while being read (no Content-Length),
            // 400 when it was shorter than Content-Length or its chunked encoding was broken, 408 when it arrived below
            // Kestrel's minimum data rate. Answer with that status
            // (as QaHttpService does).
            log.Invalid(ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "body too large" : "bad request body: " + Shorten(ex.Message), null);
            return Results.StatusCode(ex.StatusCode);
        }
        if (snapshot == null)
        {
            log.Invalid("empty body", null);
            return Results.BadRequest(new { error = "empty body" });
        }

        DateTimeOffset now = time.GetUtcNow();
        string? error = SnapshotValidator.Validate(snapshot, now, options.MaxClockSkewSeconds);
        if (error != null)
        {
            // A rejected ServerId can be ~16 KB with CR/LF or escape codes: only a valid one goes into the log.
            log.Invalid(error, MonitoringContract.IsValidServerId(snapshot.ServerId) ? snapshot.ServerId : null);
            return Results.BadRequest(new { error });
        }

        // store.Add has returned (its lock released) before any log call below.
        switch (store.Add(snapshot, now))
        {
            case IngestOutcome.FirstSeen:
                logger.LogInformation("Server {ServerId} first seen (version {Version}, protocol {Protocol})", snapshot.ServerId, snapshot.Version, snapshot.ProtocolVersion);
                break;
            case IngestOutcome.Returned:
                logger.LogInformation("Server {ServerId} online again", snapshot.ServerId);
                break;
            case IngestOutcome.TooManyServers:
                log.Invalid($"more than {options.MaxServers} servers", snapshot.ServerId);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        return Results.Accepted();
    }

    // 기능: 로그에 넣을 예외 메시지를 짧게 자른다(Kestrel 메시지는 고정 문장이지만 길이를 묶어 둔다).
    // 입력: message - 예외 메시지.
    // 출력: 최대 120자의 문자열.
    private static string Shorten(string message) => message.Length <= 120 ? message : message[..120];

    // 기능: 헤더의 Token을 설정값과 고정 시간으로 비교한다.
    // 입력: context - 요청, expected - 설정된 Token.
    // 출력: 헤더가 있고 값이 같으면 true.
    private static bool TokenMatches(HttpContext context, string expected)
    {
        if (!context.Request.Headers.TryGetValue(MonitoringContract.TokenHeader, out var values)) return false;
        byte[] given = Encoding.UTF8.GetBytes(values.ToString());
        byte[] wanted = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(given, wanted);
    }
}
