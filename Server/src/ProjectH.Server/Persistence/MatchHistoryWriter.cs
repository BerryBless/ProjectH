using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace ProjectH.Server.Persistence;

// Phase 9 D5-D8: the only code that talks to MySQL, on its own async task (never the game loop thread). At start it
// creates the schema. Phase 10 D8: if the database cannot be reached then, the writer keeps going and tries again
// with every record (a developer may start the database after the server), so a record without a database counts as
// Failed after its attempts; the game never depends on the database (§36). Each record is saved with up to MaxAttempts
// tries. On shutdown the queue is completed and whatever is left is saved for at most ShutdownDrainSeconds; a save cut
// off by that limit and the records still queued are logged and counted as Discarded.
public sealed class MatchHistoryWriter : BackgroundService
{
    private readonly MatchHistoryQueue _queue;
    private readonly PersistenceOptions _options;
    private readonly ILogger<MatchHistoryWriter> _logger;
    private readonly CancellationTokenSource _abort = new();
    private MatchStore? _store;
    // D8: false until EnsureSchemaAsync succeeded once. Only the ExecuteAsync task reads or writes it.
    private bool _schemaReady;
    private long _saved;
    private long _failed;
    private long _discarded;

    // 기능: 경기 기록 Writer 서비스를 만든다(DB 연결은 ExecuteAsync에서 연다).
    // 입력: queue - Game Loop가 기록을 넣는 큐, options - Persistence 설정, logger - 로그.
    // 출력: 시작 전의 MatchHistoryWriter.
    public MatchHistoryWriter(MatchHistoryQueue queue, IOptions<PersistenceOptions> options, ILogger<MatchHistoryWriter> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    public long Saved => Interlocked.Read(ref _saved);
    public long Failed => Interlocked.Read(ref _failed);
    public long Discarded => Interlocked.Read(ref _discarded);

    // Phase 10 D9: the four totals for the Health line and the Meter (any thread).
    public PersistenceCounts Counts => new(Saved, Failed, Discarded, _queue.Dropped);

    // Nothing escapes: a faulted ExecuteAsync would stop the whole host (BackgroundService default), and the game must
    // not depend on the database. On an unexpected error the writer stops reading; StopAsync discards what is left.
    // 기능: 저장소를 열고(스키마 준비 실패는 치명이 아님) 큐가 완료될 때까지 기록을 하나씩 재시도하며 저장한다. 저장이 꺼져 있으면 Discarded로 센다. 예외를 밖으로 내보내지 않는다.
    // 입력: stoppingToken - 호스트 중지 토큰(읽기 루프는 이것이 아니라 _abort를 따른다).
    // 출력: 큐가 완료·비워지거나, 드레인 시한 또는 예상 못 한 오류로 멈추면 끝난다. 저장 중이던 기록은 Discarded로 센다.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The read loop follows _abort, not stoppingToken: StopAsync completes the queue first and lets it drain.
        CancellationToken token = _abort.Token;
        // The record being saved right now. Only this task reads or writes it.
        MatchRecord? inFlight = null;
        try
        {
            _store = await OpenStoreAsync(token);
            await foreach (MatchRecord record in _queue.Reader.ReadAllAsync(token))
            {
                if (_store == null)
                {
                    // Persistence is disabled: nothing will ever be saved.
                    Interlocked.Increment(ref _discarded);
                    continue;
                }
                inFlight = record;
                await SaveWithRetryAsync(_store, record, token);
                inFlight = null;   // counted as Saved or Failed
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Drain timeout (D8). The aborted save is lost like the records still queued (those are logged in StopAsync).
            if (inFlight != null) Discard(inFlight);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Match history: the writer stopped on an unexpected error; records left are discarded at shutdown.");
            if (inFlight != null) Discard(inFlight);
        }
    }

    // 기능: 큐를 완료하고 ExecuteAsync가 남은 기록을 저장하도록 최대 ShutdownDrainSeconds 기다린다. 시한을 넘기면 저장을 중단시키고, 읽히지 않은 기록은 Discarded로 센 뒤 합계를 남긴다.
    // 입력: cancellationToken - 호스트 종료 시한 토큰.
    // 출력: 드레인이 끝나면 완료. Writer가 멈추고 합계 로그 한 줄이 남는다.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        Task? running = ExecuteTask;
        if (running != null)
        {
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownDrainSeconds));
            try
            {
                await running.WaitAsync(drain.Token);
            }
            catch (OperationCanceledException)
            {
                _abort.Cancel();
                await running;   // ExecuteAsync catches everything, so this only waits for the aborted save to unwind
                _logger.LogWarning("Match history: shutdown drain timed out after {Seconds} s; unsaved records are lost.", _options.ShutdownDrainSeconds);
            }
        }
        // What the writer will never read: left after a drain timeout, or after the writer stopped on an error. The queue
        // is completed, so TryRead ends when it is empty.
        while (_queue.Reader.TryRead(out MatchRecord? lost)) Discard(lost);
        _logger.LogInformation("Match history: saved={Saved} failed={Failed} discarded={Discarded} droppedQueueFull={Dropped}",
            Saved, Failed, Discarded, _queue.Dropped);
    }

    // 기능: 저장되지 않을 기록 하나를 Discarded로 세고 라운드·인원과 함께 Warning으로 남긴다.
    // 입력: record - 버리는 기록.
    // 출력: 반환값 없음.
    // A record that will not be saved: counted and logged with what identifies it.
    private void Discard(MatchRecord record)
    {
        Interlocked.Increment(ref _discarded);
        _logger.LogWarning("Match history: lost round {Round} ({Players} players); it was not saved.", record.Round, record.Players.Count);
    }

    // 기능: 중단 토큰 소스와 BackgroundService 자원을 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    // 기능: 저장이 켜져 있으면 MatchStore를 만들고 스키마 준비를 시도한다(실패해도 Warning만, 기록마다 다시 시도).
    // 입력: token - 중단 토큰.
    // 출력: 만든 MatchStore. 저장이 꺼져 있으면(Persistence:Enabled=false) null.
    private async Task<MatchStore?> OpenStoreAsync(CancellationToken token)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Match history: persistence disabled (Persistence:Enabled = false).");
            return null;
        }
        var store = new MatchStore(_options.ConnectionString);
        try
        {
            await EnsureSchemaAsync(store, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // D8: not fatal. Each record tries again first (SaveWithRetryAsync).
            _logger.LogWarning(e, "Match history: database unavailable at start; each finished match will try again.");
        }
        return store;
    }

    // 기능: 스키마를 만들고(이동 포함) 준비됨을 표시한다. 실패하면 예외가 그대로 올라간다.
    // 입력: store - 대상 저장소, token - 중단 토큰.
    // 출력: 성공하면 끝나고 _schemaReady가 true가 된다.
    private async Task EnsureSchemaAsync(MatchStore store, CancellationToken token)
    {
        await store.EnsureSchemaAsync(token);
        _schemaReady = true;
        _logger.LogInformation("Match history: connected, schema ready.");
    }

    // 기능: 기록 하나를 최대 MaxAttempts번 저장한다(시도 사이 attempt초 대기, 스키마가 아직이면 먼저 준비). 일시적이지 않은 MySQL 오류는 바로 포기한다.
    // 입력: store - 저장소, record - 저장할 기록, token - 중단 토큰(중단으로 난 예외는 밖으로 올라가 Discarded가 된다).
    // 출력: 저장되면 Saved, 포기하면 Failed가 하나 늘고 끝난다.
    private async Task SaveWithRetryAsync(MatchStore store, MatchRecord record, CancellationToken token)
    {
        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                // D8: a database that came up after the server gets its schema here, within this record's attempts.
                if (!_schemaReady) await EnsureSchemaAsync(store, token);
                long matchId = await store.SaveAsync(record, token);
                Interlocked.Increment(ref _saved);
                _logger.LogInformation("Match history: saved match {MatchId} (round {Round}, {Players} players).", matchId, record.Round, record.Players.Count);
                return;
            }
            // An error caused by the drain abort (MySqlConnector may report a killed query as a MySqlException) is not a
            // failed save: it goes up to ExecuteAsync, which counts the record as Discarded.
            catch (Exception e) when (e is not OperationCanceledException && !token.IsCancellationRequested)
            {
                // Retries are at-least-once: if a COMMIT succeeded but its acknowledgement was lost, the retry saves the match twice.
                // A non-transient MySqlException (constraint or data error) fails the same way every time, so it is not retried.
                if (attempt == _options.MaxAttempts || e is MySqlException { IsTransient: false })
                {
                    Interlocked.Increment(ref _failed);
                    _logger.LogError(e, "Match history: giving up on round {Round} ({Players} players) after {Attempts} attempts.",
                        record.Round, record.Players.Count, attempt);
                    return;
                }
                _logger.LogWarning("Match history: save of round {Round} failed (attempt {Attempt}): {Message}", record.Round, attempt, e.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
    }
}

// Phase 10 D9: what the writer did with the match records so far (Dropped: refused by the full queue).
public readonly record struct PersistenceCounts(long Saved, long Failed, long Discarded, long Dropped);
