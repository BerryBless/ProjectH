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

    public StatsQueryService(StatsQueryQueue queue, IOptions<PersistenceOptions> options, ILogger<StatsQueryService> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    // D8: how long one query (both statements) may take. Test seam.
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

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

    private static async Task<StatsResponse> ReadAsync(MatchStore store, string devPlayerId, CancellationToken token)
    {
        PlayerStats? stats = await store.GetStatsAsync(devPlayerId, token);
        IReadOnlyList<MatchHistoryEntry> history = stats == null
            ? Array.Empty<MatchHistoryEntry>()
            : await store.GetHistoryAsync(devPlayerId, StatsResponse.MaxRows, token);
        return BuildResponse(stats, history);
    }

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

    private static uint Clamp(long value) => value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
}
