using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Persistence;

// Phase 11 D8: answers statistics requests off the game loop. The only reader of StatsQueryQueue.Requests: one query at
// a time on its own task, each answered within QueryTimeout (3 s), with its own MatchStore (the same connection string as the
// writer; every call opens a pooled connection and returns it). The answer goes to the reply queue; the game loop sends
// it. Nothing escapes (Phase 9 D7): a database error or a timeout answers Unavailable, and with Persistence:Enabled=false
// every request is answered Unavailable at once. On shutdown the host stops the game loop first (Program registers this
// service before it), so the requests left in the queue have nobody to answer and are simply not read.
public sealed class StatsQueryService : BackgroundService
{
    private readonly StatsQueryQueue _queue;
    private readonly PersistenceOptions _options;
    private readonly ILogger<StatsQueryService> _logger;
    // True while the database fails, so a failure is logged when it starts and the recovery once, not every request.
    // Only the ExecuteAsync task reads or writes it.
    private bool _failing;

    // 기능: 통계 조회 Background Service를 만든다. 저장소는 ExecuteAsync에서 만든다.
    // 입력: queue - 통계 요청·응답 Queue, options - Persistence 설정, logger - 로그 출력.
    // 출력: DB 실패 상태가 아닌 StatsQueryService 객체.
    public StatsQueryService(StatsQueryQueue queue, IOptions<PersistenceOptions> options, ILogger<StatsQueryService> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    // D8: how long one query (both statements) may take. Test seam.
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

    // 기능: 요청 Queue에서 통계 요청을 하나씩 읽어 DB를 조회하고 응답 Queue에 답을 넣는 루프를 실행한다.
    // 입력: stoppingToken - Host 종료 신호.
    // 출력: 반환값 없음(종료 신호 또는 예기치 않은 오류로 끝난다). 요청마다 응답이 응답 Queue에 들어간다. 저장이 꺼져 있거나 대기 시간이 MaxQueueAgeMs를 넘은 요청은 조회 없이 Unavailable로 답한다. 예외를 밖으로 던지지 않는다.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MatchStore? store = _options.Enabled ? new MatchStore(_options.ConnectionString) : null;
        if (store == null) _logger.LogInformation("Stats queries: persistence disabled; every request is answered Unavailable.");
        try
        {
            await foreach (StatsQuery query in _queue.Requests.ReadAllAsync(stoppingToken))
            {
                StatsResponse response;
                try
                {
                    // A request that waited longer than the client waits for its answer is not worth a query: the
                    // window already shows "no answer", and skipping it lets a backlog drain fast.
                    bool stale = Environment.TickCount64 - query.EnqueuedMs > StatsQueryQueue.MaxQueueAgeMs;
                    response = store == null || stale
                        ? StatsResponse.Of(StatsStatus.Unavailable)
                        : await QueryAsync(store, query.DevPlayerId, stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // QueryAsync handles the database's errors; this is a bug. The request still gets an answer.
                    _logger.LogError(e, "Stats queries: unexpected error");
                    response = StatsResponse.Of(StatsStatus.Unavailable);
                }
                _queue.TryReply(new StatsReply(query.PeerId, query.Peer, response));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception e)
        {
            // A faulted ExecuteAsync would stop the host. Requests are no longer read; the full queue answers Busy.
            _logger.LogError(e, "Stats queries: the service stopped on an unexpected error");
        }
    }

    // 기능: 한 플레이어의 통계 조회를 QueryTimeout 안에서 실행하고, DB 실패 시작·회복을 한 번씩 로그에 남긴다.
    // 입력: store - 매치 저장소, devPlayerId - 조회할 개발용 플레이어 ID, stoppingToken - Host 종료 신호.
    // 출력: 조회 결과 StatsResponse(Ok 또는 NoRecord). 시간 초과·연결 실패·쿼리 오류면 Unavailable. 시간을 넘긴 조회는 취소되고 연결은 나중에 반환된다.
    private async Task<StatsResponse> QueryAsync(MatchStore store, string devPlayerId, CancellationToken stoppingToken)
    {
        // Disposed when the query has ended (see finally): disposing it earlier would leave an abandoned query without
        // its cancellation.
        var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        limit.CancelAfter(QueryTimeout);
        Task<StatsResponse> query = ReadAsync(store, devPlayerId, limit.Token);
        try
        {
            // WaitAsync as well as the token: MySqlConnector does not cancel a connect that waits for the server's greeting
            // (measured: it waits for the connection string's Connection Timeout), so the token alone cannot keep the limit.
            StatsResponse response = await query.WaitAsync(QueryTimeout, stoppingToken);
            if (_failing)
            {
                _failing = false;
                _logger.LogInformation("Stats queries: the database answers again.");
            }
            return response;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            // The time limit, a connection failure or a query error: the player sees "unavailable".
            if (!_failing)
            {
                _failing = true;
                _logger.LogWarning("Stats queries: the database failed ({Message}); answering Unavailable until it recovers.", e.Message);
            }
            return StatsResponse.Of(StatsStatus.Unavailable);
        }
        finally
        {
            if (query.IsCompleted)
            {
                limit.Dispose();
            }
            else
            {
                // A query left behind by the limit or the shutdown is cancelled now (CancelAfter may not have fired yet
                // when WaitAsync gave up first), so a command already running stops instead of running to its own
                // timeout. CancelAsync runs the callbacks off this task: MySqlConnector's cancel opens a connection to
                // send KILL QUERY. A connect that waits for the server's greeting ignores the token and ends at its own
                // Connection Timeout; either way await using returns the connection. The token source is disposed once
                // both the query and the callbacks are done, and the query's error is observed there, so it is not
                // reported as unobserved.
                Task cancelled = limit.CancelAsync();
                _ = Task.WhenAll(query, cancelled).ContinueWith(static (all, state) =>
                {
                    var (abandoned, source) = ((Task, CancellationTokenSource))state!;
                    _ = all.Exception;
                    _ = abandoned.Exception;
                    source.Dispose();
                }, ((Task)query, limit), TaskScheduler.Default);
            }
        }
    }

    // 기능: 누적 통계를 조회하고, 있으면 최근 매치 기록(최대 StatsResponse.MaxRows개)도 조회해 응답을 만든다.
    // 입력: store - 매치 저장소, devPlayerId - 조회할 개발용 플레이어 ID, token - 시간 제한·종료 취소 신호.
    // 출력: 통계가 있으면 Ok 응답, 없으면 NoRecord 응답. DB 실패 시 예외를 던진다.
    private static async Task<StatsResponse> ReadAsync(MatchStore store, string devPlayerId, CancellationToken token)
    {
        PlayerStats? stats = await store.GetStatsAsync(devPlayerId, token);
        IReadOnlyList<MatchHistoryEntry> history = stats == null
            ? Array.Empty<MatchHistoryEntry>()
            : await store.GetHistoryAsync(devPlayerId, StatsResponse.MaxRows, token);
        return BuildResponse(stats, history);
    }

    // 기능: DB에서 읽은 통계와 매치 기록을 Client로 보낼 StatsResponse Packet 값으로 바꾼다.
    // 입력: stats - 누적 통계(없으면 null), history - 최신순 매치 기록.
    // 출력: stats가 null이면 NoRecord 응답, 아니면 uint 범위로 잘린 요약과 최대 MaxRows개 행을 담은 Ok 응답.
    // The wire form of what MatchStore returned. The database keeps 64-bit sums; the packet carries uint32 values,
    // clamped, and the total survival time in whole seconds. At most StatsResponse.MaxRows matches, newest first.
    internal static StatsResponse BuildResponse(PlayerStats? stats, IReadOnlyList<MatchHistoryEntry> history)
    {
        if (stats == null) return StatsResponse.Of(StatsStatus.NoRecord);
        int count = Math.Min(history.Count, StatsResponse.MaxRows);
        var rows = new StatsRow[count];
        for (int i = 0; i < count; i++)
        {
            MatchHistoryEntry e = history[i];
            rows[i] = new StatsRow
            {
                EndedUnixSeconds = Clamp(new DateTimeOffset(DateTime.SpecifyKind(e.EndedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()),
                Round = Clamp(e.Round),
                Players = (byte)Math.Clamp(e.Players, 0, byte.MaxValue),
                Placement = e.Placement,
                Kills = (ushort)Math.Clamp(e.Kills, 0, ushort.MaxValue),
                Damage = Clamp(e.Damage),
                SurvivalMs = Clamp(e.SurvivalMs),
            };
        }
        return new StatsResponse
        {
            Status = StatsStatus.Ok,
            Summary = new StatsSummary
            {
                Matches = Clamp(stats.Matches),
                Wins = Clamp(stats.Wins),
                Kills = Clamp(stats.Kills),
                Deaths = Clamp(stats.Deaths),
                Damage = Clamp(stats.Damage),
                SurvivalSeconds = Clamp(stats.SurvivalMs / 1000),
            },
            Rows = rows,
        };
    }

    // 기능: 64비트 값을 Packet의 uint 범위로 자른다.
    // 입력: value - 자를 값.
    // 출력: 0 이하이면 0, uint 최대값 이상이면 uint 최대값, 그 외는 같은 값.
    private static uint Clamp(long value) => value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
}
