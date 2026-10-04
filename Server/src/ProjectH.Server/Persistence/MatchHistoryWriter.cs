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

    // 기능: 매치 기록 저장 Background Service를 만든다. DB 연결은 ExecuteAsync에서 연다.
    // 입력: queue - 게임 루프가 기록을 넣는 Queue, options - Persistence 설정, logger - 로그 출력.
    // 출력: 스키마 미준비, 모든 카운터가 0인 MatchHistoryWriter 객체.
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

    // 기능: 저장소를 열고 Queue에서 매치 기록을 하나씩 읽어 DB에 저장하는 저장 루프를 실행한다.
    // 입력: stoppingToken - Host 종료 신호(읽기 루프는 대신 내부 _abort 신호를 따른다).
    // 출력: 반환값 없음(Queue가 완료되어 비거나, 종료 대기 시간 초과·예기치 않은 오류로 멈추면 끝난다). 기록마다 Saved·Failed·Discarded 중 하나가 증가하고 중단된 저장 중 기록은 Discarded로 센다. 예외를 밖으로 던지지 않는다.
    // Nothing escapes: a faulted ExecuteAsync would stop the whole host (BackgroundService default), and the game must
    // not depend on the database. On an unexpected error the writer stops reading; StopAsync discards what is left.
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

    // 기능: 종료 시 Queue를 완료하고 남은 기록을 ShutdownDrainSeconds 동안 저장하게 기다린 뒤, 시간이 넘으면 저장을 중단시킨다.
    // 입력: cancellationToken - Host가 주는 종료 제한 신호.
    // 출력: 반환값 없음. 저장 루프가 끝나고, 읽히지 않은 기록은 Discarded로 세어 로그에 남으며 최종 카운터가 로그에 기록된다.
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

    // 기능: 저장되지 못할 매치 기록을 버린 것으로 처리한다.
    // 입력: record - 버려지는 매치 기록.
    // 출력: 반환값 없음. Discarded가 1 증가하고 라운드·인원 수가 경고 로그에 남는다.
    // A record that will not be saved: counted and logged with what identifies it.
    private void Discard(MatchRecord record)
    {
        Interlocked.Increment(ref _discarded);
        _logger.LogWarning("Match history: lost round {Round} ({Players} players); it was not saved.", record.Round, record.Players.Count);
    }

    // 기능: 저장 중단용 CancellationTokenSource와 기본 서비스 자원을 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _abort와 BackgroundService 자원이 해제된다.
    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    // 기능: 저장이 켜져 있으면 MatchStore를 만들고 스키마 생성을 한 번 시도한다.
    // 입력: token - 저장 중단 신호.
    // 출력: 저장이 꺼져 있으면 null, 켜져 있으면 MatchStore(DB에 연결할 수 없어도 반환하고 기록마다 다시 시도한다).
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

    // 기능: DB 스키마를 만들고 성공을 기록한다.
    // 입력: store - 매치 저장소, token - 저장 중단 신호.
    // 출력: 반환값 없음. 성공하면 _schemaReady가 true가 된다. 실패하면 DB 예외를 그대로 던진다.
    private async Task EnsureSchemaAsync(MatchStore store, CancellationToken token)
    {
        await store.EnsureSchemaAsync(token);
        _schemaReady = true;
        _logger.LogInformation("Match history: connected, schema ready.");
    }

    // 기능: 매치 기록 하나를 최대 MaxAttempts번(시도 사이 1초, 2초... 대기) 저장한다. 스키마가 아직 없으면 먼저 만든다.
    // 입력: store - 매치 저장소, record - 저장할 매치 기록, token - 저장 중단 신호.
    // 출력: 반환값 없음. 성공하면 Saved, 모든 시도 실패나 일시적이지 않은 MySqlException이면 Failed가 1 증가한다. 중단 신호로 인한 예외는 호출자에게 전달된다.
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
