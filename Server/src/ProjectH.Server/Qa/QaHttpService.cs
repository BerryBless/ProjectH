using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Qa;

// QA-1 D4: the QA Control HTTP endpoint. A hosted service registered after GameServerService only in QA mode, so it
// starts once the UDP port is bound and, on shutdown, stops before the game loop (no request waits on a stopped loop).
//
// The Kestrel app is built from an empty builder: no configuration source, so ASPNETCORE_URLS, --urls, HTTP_PORTS and
// Kestrel:Endpoints cannot add an endpoint; the one listener is bound in code to 127.0.0.1 (every interface only with
// Qa:AllowRemote). Handlers never touch the match: each request becomes a work item (QaControl.SubmitAsync).
// Lifetime: this service owns the app; StopAsync stops it and DisposeAsync releases it (the host disposes this service).
public sealed class QaHttpService : IHostedService, IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A local tool API, never rendered as HTML: keep quotes and '+' readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        // No NaN may break a response: written as "NaN" instead of throwing.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly QaControl _qa;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private WebApplication? _app;
    // QA-3: stops the server when the QA tool that launched it is gone. Disposed with this service.
    private QaParentWatchdog? _parentWatch;

    // 기능: QA HTTP 서비스를 만든다(앱은 StartAsync에서 만든다).
    // 입력: qa - QA Control, lifetime - 호스트 Lifetime(정지 요청용), logger - 로그.
    // 출력: 아직 듣지 않는 QaHttpService.
    public QaHttpService(QaControl qa, IHostApplicationLifetime lifetime, ILogger<QaHttpService> logger)
    {
        _qa = qa;
        _lifetime = lifetime;
        _logger = logger;
    }

    // The listener's bound addresses (tests).
    public IReadOnlyList<string> BoundAddresses { get; private set; } = Array.Empty<string>();

    // 기능: Kestrel 앱을 만들어 QA 경로를 등록하고 127.0.0.1(AllowRemote면 모든 인터페이스)에 묶은 뒤 QA_READY 줄을 출력한다.
    // 입력: cancellationToken - 시작 취소 토큰.
    // 출력: 반환값 없음. 앱이 듣기 시작하고 BoundAddresses·QaPort가 정해지며 ParentPid가 있으면 부모 감시가 시작된다.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        QaOptions options = _qa.Options;
        IPAddress address = options.AllowRemote ? IPAddress.Any : IPAddress.Loopback;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(QaHttpService).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = _qa.EnvironmentName,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = QaOptions.MaxBodyBytes;
            kestrel.Listen(address, options.Port);
        });
        builder.Services.AddRoutingCore();
        // The server's host owns Ctrl+C and SIGTERM; this inner app only follows StartAsync / StopAsync.
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();

        WebApplication app = builder.Build();
        Map(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        BoundAddresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
                         ?? Array.Empty<string>();
        int port = BoundAddresses.Select(a => new Uri(a.Replace("*", "localhost").Replace("+", "localhost")).Port).FirstOrDefault();
        _qa.QaPort = port;

        _logger.LogWarning("QA mode is ON: QA Control listens on {Addresses} (environment {Environment}). Never run this on a live server.",
            string.Join(", ", BoundAddresses), _qa.EnvironmentName);
        if (options.ParentPid > 0)
            _parentWatch = new QaParentWatchdog(options.ParentPid, TimeSpan.FromSeconds(1), _lifetime.StopApplication, _logger);
        if (options.AllowRemote) _logger.LogWarning("Qa:AllowRemote is on: QA Control accepts connections from other machines");
        // D12: the QA tool waits for this line (after both listeners are bound) and reads the ports from it.
        Console.Out.WriteLine($"QA_READY gamePort={_qa.GamePort} qaPort={port}");
        Console.Out.Flush();
    }

    // 기능: 부모 감시를 멈추고 HTTP 앱을 멈춘다(게임 루프보다 먼저).
    // 입력: cancellationToken - 정지 취소 토큰.
    // 출력: 반환값 없음. 앱이 더 이상 요청을 받지 않는다.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _parentWatch?.Dispose();
        if (_app != null) await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // 기능: 부모 감시와 HTTP 앱을 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _parentWatch와 _app이 null이 된다.
    public async ValueTask DisposeAsync()
    {
        _parentWatch?.Dispose();
        _parentWatch = null;
        if (_app != null) await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    // 기능: QA HTTP 경로를 모두 등록한다(Phase 16: GET /qa/loot, Phase 17: GET /qa/projectiles, Phase 19: GET /qa/vehicles).
    // 입력: app - Kestrel 앱.
    // 출력: 반환값 없음. 앱에 GET·POST 경로가 등록된다.
    private void Map(WebApplication app)
    {
        app.MapGet("/qa/health", ctx => Run(ctx, t => QaResult.Data(QaQueries.Health(t))));
        app.MapGet("/qa/players", ctx => Run(ctx, t => QaResult.Data(QaQueries.Players(t.Match))));
        app.MapGet("/qa/players/{devPlayerId}", ctx =>
        {
            string id = (string)ctx.Request.RouteValues["devPlayerId"]!;
            return Run(ctx, t =>
            {
                Game.PlayerEntity? p = QaCommands.FindPlayer(t.Match, id);
                return p == null ? QaResult.Error(404, $"No player '{id}' in the match.") : QaResult.Data(QaQueries.Player(t.Match, p));
            });
        });
        app.MapGet("/qa/match", ctx => Run(ctx, t => QaResult.Data(QaQueries.Match(t.Match, t.Loop.Options.SimHz))));
        app.MapGet("/qa/build", ctx =>
        {
            if (!Query(ctx, "x", -1000, 1000, out double? x, out string? error) || !Query(ctx, "z", -1000, 1000, out double? z, out error) ||
                !Query(ctx, "radius", 0, 1000, out double? radius, out error) || !Query(ctx, "max", 1, QaQueries.MaxPieces, out double? max, out error))
                return Write(ctx, QaResult.Error(400, error!));
            if ((x.HasValue || z.HasValue || radius.HasValue) && !(x.HasValue && z.HasValue && radius.HasValue))
                return Write(ctx, QaResult.Error(400, "x, z and radius go together."));
            return Run(ctx, t => QaResult.Data(QaQueries.Build(t.Match, (float?)x, (float?)z, (float?)radius, (int)(max ?? QaQueries.MaxPieces))));
        });
        // Phase 16: the loot containers, supply drops and the world items around a point (kind, rarity, amount).
        app.MapGet("/qa/loot", ctx =>
        {
            if (!Query(ctx, "x", -1000, 1000, out double? x, out string? error) || !Query(ctx, "z", -1000, 1000, out double? z, out error) ||
                !Query(ctx, "radius", 0, 1000, out double? radius, out error) || !Query(ctx, "max", 1, QaQueries.MaxLootItems, out double? max, out error))
                return Write(ctx, QaResult.Error(400, error!));
            if ((x.HasValue || z.HasValue || radius.HasValue) && !(x.HasValue && z.HasValue && radius.HasValue))
                return Write(ctx, QaResult.Error(400, "x, z and radius go together."));
            return Run(ctx, t => QaResult.Data(QaQueries.Loot(t.Match, (float?)x, (float?)z, (float?)radius, (int)(max ?? QaQueries.MaxLootItems))));
        });
        // Phase 17 D17: live projectiles and the recent explosions.
        app.MapGet("/qa/projectiles", ctx => Run(ctx, t => QaResult.Data(QaQueries.Projectiles(t.Match))));
        // Phase 19 D14: the vehicles and the vehicle counters.
        app.MapGet("/qa/vehicles", ctx => Run(ctx, t => QaResult.Data(QaQueries.Vehicles(t.Match))));
        app.MapGet("/qa/metrics", ctx =>
        {
            if (!Query(ctx, "windowSeconds", 1, QaOptions.MetricsWindowSeconds, out double? window, out string? error))
                return Write(ctx, QaResult.Error(400, error!));
            return Run(ctx, t => QaResult.Data(Metrics(t, (int)(window ?? 10))));
        });
        app.MapGet("/qa/events", ctx =>
        {
            if (!Query(ctx, "after", 0, long.MaxValue, out double? after, out string? error) ||
                !Query(ctx, "max", 1, QaOptions.EventCapacity, out double? max, out error))
                return Write(ctx, QaResult.Error(400, error!));
            return Run(ctx, t => QaResult.Data(t.Qa.Events.Query((long)(after ?? 0), (int)(max ?? 200))));
        });
        app.MapPost("/qa/command", Command);
        app.MapPost("/qa/server/stop", async ctx =>
        {
            // QA-3: answered directly (not through the game loop), so a stuck loop can still be stopped. The QA tool times
            // the shutdown from here to the process exit; this listener stops first, so /qa/health is gone from now on.
            DateTime requested = DateTime.UtcNow;
            _logger.LogWarning("QA stop request: stopping the server ({Peers} connections)", _qa.Peers);
            await Write(ctx, QaResult.Ok(new
            {
                requestedAtUtc = requested,
                activeSessions = _qa.Peers,
                // The normal shutdown path's limits: the loop thread join, then the ServerShutdown notices, then the database drain.
                threadJoinTimeoutMs = (int)GameLoop.ThreadJoinTimeout.TotalMilliseconds,
                shutdownNoticeTimeoutMs = (int)GameLoop.ShutdownNoticeTimeout.TotalMilliseconds,
            })).ConfigureAwait(false);
            // After the answer is written; the normal shutdown path (clients get ServerShutdown).
            _ = Task.Run(_lifetime.StopApplication);
        });
    }

    // 기능: GET /qa/metrics 본문을 만든다(Tick 백분위, 패킷 속도, CPU, 메모리·GC, 세션·경기 수, DB 상태, Health 수치).
    // 입력: t - QA Tick 문맥, windowSeconds - 측정 창(초).
    // 출력: 익명 객체(게임 루프 스레드에서 만든 복사본).
    internal static object Metrics(QaTick t, int windowSeconds)
    {
        GameLoop loop = t.Loop;
        Diagnostics.HealthCounters h = loop.Health;
        QaDbStatus? db = t.Qa.Database?.Invoke();
        QaTickMetrics ticks = t.Qa.Metrics.Snapshot(windowSeconds, QaControl.NetTotals(loop));
        Game.Match m = t.Match;
        return new
        {
            ok = true,
            serverTick = (long)t.Match.ServerTick,
            ticks,
            // Flat copies of the most used numbers (the QA tool's metric paths).
            tickP50Ms = ticks.TickP50Ms,
            tickP95Ms = ticks.TickP95Ms,
            tickP99Ms = ticks.TickP99Ms,
            tickMaxMs = ticks.TickMaxMs,
            qaCommandMs = ticks.QaCommandMs,
            pktInPerSec = ticks.PktInPerSec,
            pktOutPerSec = ticks.PktOutPerSec,
            bytesInPerSec = ticks.BytesInPerSec,
            bytesOutPerSec = ticks.BytesOutPerSec,
            cpuPercent = ticks.CpuPercent,
            workingSetMB = Environment.WorkingSet / 1048576.0,
            // Stress runs (D36): the managed heap, everything allocated and every GC pause since the process started. Read at
            // query time only; nothing is sampled per tick.
            managedMB = GC.GetTotalMemory(false) / 1048576.0,
            allocatedMBTotal = GC.GetTotalAllocatedBytes(false) / 1048576.0,
            gcPauseMsTotal = GC.GetTotalPauseDuration().TotalMilliseconds,
            gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
            activeSessions = loop.PeerCount,
            players = m.PlayerCount,
            // Before the match the connected count, during it the participants still in (MatchState's Alive).
            alive = (int)m.Flow.ToWire(m.PlayerCount).Alive,
            buildPieces = m.BuildPieces,
            stalls = h.Stalls,
            db,
            dbQueueLength = db?.QueueLength ?? 0,
            health = new
            {
                connections = h.Connections, joins = h.Joins, resumes = h.Resumes, graceStarts = h.GraceStarts, graceExpiries = h.GraceExpiries,
                tickFailures = h.TickFailures, loopFailures = h.LoopFailures, matchResets = h.MatchResets, stalls = h.Stalls,
                movementAnomalies = h.MovementAnomalies, badPackets = BadPackets(h),
            },
        };
    }

    // 기능: 이유별 잘못된 패킷 수를 모두 더한다.
    // 입력: h - 시작부터의 합계.
    // 출력: 잘못된 패킷 총수.
    private static long BadPackets(Diagnostics.HealthCounters h)
    {
        long total = 0;
        for (int i = 0; i < (int)Diagnostics.BadPacketReason.Count; i++) total += h.BadPackets((Diagnostics.BadPacketReason)i);
        return total;
    }

    // 기능: POST /qa/command를 처리한다(본문 크기·JSON 검증 후 게임 루프에 명령을 넘기고 결과를 쓴다).
    // 입력: ctx - HTTP 요청 문맥(본문: command, player·runId·args 선택).
    // 출력: 반환값 없음. 응답이 쓰인다(검증 실패는 400·413, 아니면 명령의 결과).
    private async Task Command(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > QaOptions.MaxBodyBytes)
        {
            await Write(ctx, QaResult.Error(413, $"The body is over {QaOptions.MaxBodyBytes} bytes.")).ConfigureAwait(false);
            return;
        }
        string command;
        string? player;
        string? runId;
        JsonElement args;
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The body must be a JSON object.");
            command = String(root, "command", 64) ?? throw new JsonException("'command' is required.");
            player = String(root, "player", 128);
            runId = String(root, "runId", 128);
            args = root.TryGetProperty("args", out JsonElement a) && a.ValueKind != JsonValueKind.Null ? a.Clone() : default;
            if (args.ValueKind != JsonValueKind.Undefined && args.ValueKind != JsonValueKind.Object)
                throw new JsonException("'args' must be an object.");
        }
        catch (JsonException ex)
        {
            await Write(ctx, QaResult.Error(400, "Bad request: " + ex.Message)).ConfigureAwait(false);
            return;
        }
        catch (BadHttpRequestException ex)
        {
            await Write(ctx, QaResult.Error(ex.StatusCode, "Bad request: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        ILogger logger = _logger;
        QaResult result = await _qa.SubmitAsync(t => QaCommands.Execute(t, command, player, runId, args, logger), ctx.RequestAborted)
            .ConfigureAwait(false);
        // §69: every command is in the server log with its run, so a failed scenario can be followed there.
        _logger.LogInformation("QA command {Command} player={Player} run={RunId}: {Status}", command, player ?? "-", runId ?? "-", result.Status);
        await Write(ctx, result).ConfigureAwait(false);
    }

    // 기능: JSON 객체에서 문자열 속성을 읽는다.
    // 입력: root - JSON 객체, name - 속성 이름, maxLength - 허용 길이.
    // 출력: 읽은 문자열, 없거나 null이면 null. 문자열이 아니거나 길면 JsonException.
    private static string? String(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException($"'{name}' must be a string.");
        string text = value.GetString()!;
        if (text.Length > maxLength) throw new JsonException($"'{name}' is longer than {maxLength} characters.");
        return text;
    }

    // 기능: Query String의 수 인자를 [min, max] 범위로 읽는다.
    // 입력: ctx - HTTP 요청 문맥, name - 인자 이름, min·max - 허용 범위, value - 읽은 수(없으면 null), error - 오류 메시지.
    // 출력: 없거나 올바르면 true, 수가 아니거나 범위 밖이면 false와 error.
    private static bool Query(HttpContext ctx, string name, double min, double max, out double? value, out string? error)
    {
        value = null;
        error = null;
        string? text = ctx.Request.Query[name];
        if (string.IsNullOrEmpty(text)) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) ||
            number < min || number > max)
        {
            error = $"'{name}' must be a number in {min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }
        value = number;
        return true;
    }

    // 기능: 작업을 게임 루프에 넘기고 그 결과를 응답으로 쓴다.
    // 입력: ctx - HTTP 요청 문맥, work - 게임 루프에서 실행할 작업.
    // 출력: 반환값 없음. 응답이 쓰인다.
    private async Task Run(HttpContext ctx, QaWork work)
    {
        QaResult result = await _qa.SubmitAsync(work, ctx.RequestAborted).ConfigureAwait(false);
        await Write(ctx, result).ConfigureAwait(false);
    }

    // 기능: QaResult를 HTTP 응답(상태 코드와 JSON 본문)으로 쓴다.
    // 입력: ctx - HTTP 요청 문맥, result - 쓸 결과.
    // 출력: 반환값 없음. 응답이 쓰인다.
    private static async Task Write(HttpContext ctx, QaResult result)
    {
        ctx.Response.StatusCode = result.Status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, result.Body, result.Body.GetType(), Json, ctx.RequestAborted).ConfigureAwait(false);
    }

    // An IHostLifetime that waits for nothing and hooks no signal.
    private sealed class PassiveLifetime : IHostLifetime
    {
        // 기능: 호스트 시작을 기다리지 않는다(IHostLifetime).
        // 입력: cancellationToken - 사용하지 않음.
        // 출력: 바로 완료된 Task.
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        // 기능: 정지 시 아무것도 하지 않는다(IHostLifetime).
        // 입력: cancellationToken - 사용하지 않음.
        // 출력: 바로 완료된 Task.
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
