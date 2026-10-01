using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace ProjectH.Server.Persistence;

// Phase 9 D5-D8: the only code that talks to MySQL, on its own async task (never the game loop thread). At start it
// creates the schema; if the database cannot be reached the server keeps running and the records are discarded and
// counted (the game must not depend on the database, §36). Each record is saved with up to MaxAttempts tries. On
// shutdown the queue is completed and whatever is left is saved for at most ShutdownDrainSeconds; a save cut off by
// that limit and the records still queued are logged and counted as Discarded.
public sealed class MatchHistoryWriter : BackgroundService
{
    private readonly MatchHistoryQueue _queue;
    private readonly PersistenceOptions _options;
    private readonly ILogger<MatchHistoryWriter> _logger;
    private readonly CancellationTokenSource _abort = new();
    private MatchStore? _store;
    private long _saved;
    private long _failed;
    private long _discarded;

    public MatchHistoryWriter(MatchHistoryQueue queue, IOptions<PersistenceOptions> options, ILogger<MatchHistoryWriter> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    public long Saved => Interlocked.Read(ref _saved);
    public long Failed => Interlocked.Read(ref _failed);
    public long Discarded => Interlocked.Read(ref _discarded);

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

    // A record that will not be saved: counted and logged with what identifies it.
    private void Discard(MatchRecord record)
    {
        Interlocked.Increment(ref _discarded);
        _logger.LogWarning("Match history: lost round {Round} ({Players} players); it was not saved.", record.Round, record.Players.Count);
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    private async Task<MatchStore?> OpenStoreAsync(CancellationToken token)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Match history: persistence disabled (Persistence:Enabled = false).");
            return null;
        }
        try
        {
            var store = new MatchStore(_options.ConnectionString);
            await store.EnsureSchemaAsync(token);
            _logger.LogInformation("Match history: connected, schema ready.");
            return store;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Match history: database unavailable; this run will not save matches.");
            return null;
        }
    }

    private async Task SaveWithRetryAsync(MatchStore store, MatchRecord record, CancellationToken token)
    {
        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
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
