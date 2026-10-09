using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Tests.Persistence;

// Runs only when PROJECTH_TEST_MYSQL holds a connection string (Docs/Database.md: `docker compose up -d`, then
// PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev").
// Without it the test is reported as skipped, so `dotnet test` still passes on a machine with no database.
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string Variable = "PROJECTH_TEST_MYSQL";

    // 기능: PROJECTH_TEST_MYSQL이 비어 있으면 테스트를 건너뛰도록 Skip 사유를 적는다.
    // 입력: 없음.
    // 출력: 연결 문자열이 없을 때 Skip이 설정된 Fact Attribute.
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a MySQL connection string to run the database tests.";
    }

    public static string ConnectionString => Environment.GetEnvironmentVariable(Variable)!;
}

// Phase 9 against a real MySQL. Every test uses its own random DevPlayerIds, so tests never see each other's rows.
public class MySqlTests
{
    // 기능: 다른 테스트와 겹치지 않는 임의의 DevPlayerId를 만든다.
    // 입력: tag - id 앞에 붙일 짧은 표시.
    // 출력: "t{tag}-{guid}"를 32자로 자른 id.
    private static string NewId(string tag) => $"t{tag}-{Guid.NewGuid():N}"[..32];

    // 기능: 4분 전에 시작해 지금 끝난 경기 기록을 만든다. Placement 1인 참가자가 우승자다.
    // 입력: round - 라운드 번호, players - 참가자 기록들.
    // 출력: 그 참가자들의 MatchRecord(Placement 1이 없으면 우승자 null).
    private static MatchRecord Match(int round, params PlayerRecord[] players)
    {
        string? winner = null;
        foreach (PlayerRecord p in players) if (p.Placement == 1) winner = p.DevPlayerId;
        DateTime end = DateTime.UtcNow;
        return new MatchRecord(round, end.AddMinutes(-4), end, winner, players);
    }

    // 기능: 테스트 MySQL에 붙는 MatchStore를 만들고 Schema를 두 번 보장한다(멱등 확인).
    // 입력: 없음.
    // 출력: Schema가 준비된 MatchStore.
    private static async Task<MatchStore> StoreAsync()
    {
        var store = new MatchStore(MySqlFactAttribute.ConnectionString);
        await store.EnsureSchemaAsync(CancellationToken.None);
        await store.EnsureSchemaAsync(CancellationToken.None);   // idempotent
        return store;
    }

    [MySqlFact]
    public async Task SavingMatches_WritesHistory_AndAddsUpStatistics()
    {
        MatchStore store = await StoreAsync();
        string a = NewId("a"), b = NewId("b"), c = NewId("c");

        await store.SaveAsync(Match(1, new PlayerRecord(a, 1, 2, 300, 240000), new PlayerRecord(b, 2, 1, 150, 200000),
            new PlayerRecord(c, 3, 0, 20, 60000)), CancellationToken.None);
        await store.SaveAsync(Match(2, new PlayerRecord(a, 2, 0, 50, 90000), new PlayerRecord(b, 1, 3, 400, 250000)),
            CancellationToken.None);

        PlayerStats? statsA = await store.GetStatsAsync(a, CancellationToken.None);
        Assert.NotNull(statsA);
        Assert.Equal(2, statsA!.Matches);
        Assert.Equal(1, statsA.Wins);
        Assert.Equal(2, statsA.Kills);
        Assert.Equal(1, statsA.Deaths);
        Assert.Equal(350, statsA.Damage);
        Assert.Equal(330000, statsA.SurvivalMs);

        PlayerStats? statsC = await store.GetStatsAsync(c, CancellationToken.None);
        Assert.Equal(1, statsC!.Matches);
        Assert.Equal(0, statsC.Wins);

        IReadOnlyList<MatchHistoryEntry> history = await store.GetHistoryAsync(a, 10, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].MatchId > history[1].MatchId);   // newest first
        Assert.Equal(2, history[0].Round);
        Assert.Equal(2, history[0].Placement);
        Assert.Equal(3, history[1].Players);
        Assert.Equal(1, history[1].Placement);
        Assert.Equal(300, history[1].Damage);

        Assert.Null(await store.GetStatsAsync(NewId("nobody"), CancellationToken.None));
    }

    // Review fix C7: a database of the first schema (no schema_version table, match_player without the eight anti-cheat
    // columns) is moved to version 2 by EnsureSchemaAsync: the columns are added (only the missing ones, so a half-done
    // migration finishes), version 2 is written, a second run changes nothing, and a match then saves the counters. Runs in
    // this class, whose tests run one at a time, because it changes the shared tables.
    [MySqlFact]
    public async Task AV1Database_IsMigratedToV2_AndTheColumnsAreSaved()
    {
        MatchStore store = await StoreAsync();
        await using (var connection = new MySqlConnection(MySqlFactAttribute.ConnectionString))
        {
            await connection.OpenAsync();
            await Execute(connection, "DROP TABLE IF EXISTS schema_version");
            foreach (string column in MatchStore.AntiCheatColumns) await Execute(connection, $"ALTER TABLE match_player DROP COLUMN {column}");
            // Half done: one column was added before the server stopped.
            await Execute(connection, "ALTER TABLE match_player ADD COLUMN shots INT NOT NULL DEFAULT 0");
        }

        await store.EnsureSchemaAsync(CancellationToken.None);
        await store.EnsureSchemaAsync(CancellationToken.None);   // twice: nothing left to do

        await using (var connection = new MySqlConnection(MySqlFactAttribute.ConnectionString))
        {
            await connection.OpenAsync();
            await using var columns = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'match_player' " +
                "AND COLUMN_NAME IN ('shots', 'pellets', 'hits', 'rewind_ticks', 'rewind_clamped', 'max_hit_distance_cm', " +
                "'movement_anomalies', 'max_aim_turn')", connection);
            Assert.Equal(8L, Convert.ToInt64(await columns.ExecuteScalarAsync()));
            await using var version = new MySqlCommand("SELECT version FROM schema_version WHERE id = 1", connection);
            Assert.Equal(MatchStore.SchemaVersion, Convert.ToInt32(await version.ExecuteScalarAsync()));
        }

        string a = NewId("v");
        long matchId = await store.SaveAsync(Match(7, new PlayerRecord(a, 1, 1, 150, 9000, Shots: 5, Pellets: 40, Hits: 12, RewindTicks: 30,
            RewindClamped: 2, MaxHitDistanceCm: 4321, MovementAnomalies: 1, MaxAimTurn: 1745)), CancellationToken.None);
        await using (var connection = new MySqlConnection(MySqlFactAttribute.ConnectionString))
        {
            await connection.OpenAsync();
            await using var row = new MySqlCommand(
                "SELECT p.shots, p.pellets, p.hits, p.rewind_ticks, p.rewind_clamped, p.max_hit_distance_cm, p.movement_anomalies, p.max_aim_turn " +
                "FROM match_player p JOIN account a ON a.id = p.account_id WHERE p.match_id = @match AND a.dev_player_id = @dev", connection);
            row.Parameters.AddWithValue("@match", matchId);
            row.Parameters.AddWithValue("@dev", a);
            await using MySqlDataReader reader = await row.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(new[] { 5, 40, 12, 30, 2, 4321, 1, 1745 }, new[]
            {
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7),
            });
        }
    }

    // 기능: SQL 한 문장을 실행한다(시험 준비용).
    // 입력: connection - 열린 연결, sql - 문장.
    // 출력: 반환값 없음. 문장이 실행된 뒤 끝난다.
    private static async Task Execute(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // One bad row rolls the whole match back: no match row, no history and no statistics for anyone in it.
    [MySqlFact]
    public async Task AFailingSave_ChangesNothing()
    {
        MatchStore store = await StoreAsync();
        string good = NewId("g");
        string tooLong = new string('x', 40);   // dev_player_id is VARCHAR(32): strict mode rejects it
        await Assert.ThrowsAsync<MySqlException>(() =>
            store.SaveAsync(Match(1, new PlayerRecord(good, 1, 1, 100, 1000), new PlayerRecord(tooLong, 2, 0, 0, 500)), CancellationToken.None));
        Assert.Null(await store.GetStatsAsync(good, CancellationToken.None));
        Assert.Empty(await store.GetHistoryAsync(good, 10, CancellationToken.None));

        // The account insert for `good` ran inside the transaction before the failure: it must be rolled back too.
        await using var connection = new MySqlConnection(MySqlFactAttribute.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT COUNT(*) FROM account WHERE dev_player_id = @dev", connection);
        command.Parameters.AddWithValue("@dev", good);
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    // Two clients with the same DevPlayerId: one row per account (the best placement), the match is kept. A trailing-space
    // variant is a different account (NO PAD collation).
    [MySqlFact]
    public async Task ADuplicatePlayer_IsSavedOnce_AndTheMatchIsKept()
    {
        MatchStore store = await StoreAsync();
        string a = NewId("d"), b = NewId("e")[..30];
        await store.SaveAsync(Match(3, new PlayerRecord(a, 1, 2, 100, 5000), new PlayerRecord(b, 2, 0, 10, 4000),
            new PlayerRecord(a, 3, 9, 999, 1)), CancellationToken.None);
        await store.SaveAsync(Match(4, new PlayerRecord(b, 1, 1, 10, 100), new PlayerRecord(b + " ", 2, 0, 0, 50)), CancellationToken.None);

        IReadOnlyList<MatchHistoryEntry> history = await store.GetHistoryAsync(a, 10, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal(1, history[0].Placement);
        Assert.Equal(2, history[0].Players);
        PlayerStats? stats = await store.GetStatsAsync(a, CancellationToken.None);
        Assert.Equal(1, stats!.Matches);
        Assert.Equal(2, stats.Kills);
        Assert.Equal(2, (await store.GetStatsAsync(b, CancellationToken.None))!.Matches);
        // Under a PAD SPACE collation "b " would be b's account (2 matches); NO PAD keeps it apart.
        Assert.Equal(1, (await store.GetStatsAsync(b + " ", CancellationToken.None))!.Matches);
    }

    // A duplicate whose later record is the better one: that record is written, so the win and the winner are kept.
    // Placement 0 (unranked) loses to any real placement.
    [MySqlFact]
    public async Task ADuplicatePlayer_KeepsItsBestPlacement()
    {
        MatchStore store = await StoreAsync();
        string c = NewId("c"), d = NewId("d"), e = NewId("u");
        long matchId = await store.SaveAsync(Match(5, new PlayerRecord(c, 3, 0, 10, 100), new PlayerRecord(d, 2, 1, 50, 200),
            new PlayerRecord(c, 1, 4, 200, 300)), CancellationToken.None);
        await store.SaveAsync(Match(6, new PlayerRecord(e, 0, 7, 70, 70), new PlayerRecord(e, 2, 1, 20, 400)), CancellationToken.None);

        PlayerStats? stats = await store.GetStatsAsync(c, CancellationToken.None);
        Assert.Equal(1, stats!.Matches);
        Assert.Equal(1, stats.Wins);
        Assert.Equal(0, stats.Deaths);
        Assert.Equal(4, stats.Kills);
        MatchHistoryEntry entry = Assert.Single(await store.GetHistoryAsync(c, 10, CancellationToken.None));
        Assert.Equal(1, entry.Placement);
        Assert.Equal(2, entry.Players);

        await using (var connection = new MySqlConnection(MySqlFactAttribute.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "SELECT a.dev_player_id FROM game_match m JOIN account a ON a.id = m.winner_account_id WHERE m.id = @id", connection);
            command.Parameters.AddWithValue("@id", matchId);
            Assert.Equal(c, Convert.ToString(await command.ExecuteScalarAsync()));
        }

        MatchHistoryEntry unranked = Assert.Single(await store.GetHistoryAsync(e, 10, CancellationToken.None));
        Assert.Equal(2, unranked.Placement);
        Assert.Equal(1, unranked.Kills);
    }

    // Phase 10 D8: the writer starts while the database is unreachable, then the database appears (a TCP forwarder to
    // the test MySQL opens on the port the writer uses): the next record creates the schema and is saved.
    [MySqlFact]
    public async Task TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears()
    {
        var target = new MySqlConnectionStringBuilder(MySqlFactAttribute.ConnectionString);
        int port = UnreachableDatabaseTests.FreePort();
        var through = new MySqlConnectionStringBuilder(MySqlFactAttribute.ConnectionString)
        {
            Server = "127.0.0.1", Port = (uint)port, Pooling = false, ConnectionTimeout = 2,
        };
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = through.ConnectionString, MaxAttempts = 1 });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(queue.TryEnqueue(Match(10, new PlayerRecord(NewId("n"), 1, 0, 0, 1000))));
        await UnreachableDatabaseTests.WaitFor(() => writer.Failed == 1);
        Assert.Equal(1, writer.Failed);

        using var forwarder = new TcpForwarder(port, target.Server, (int)target.Port);
        string a = NewId("y");
        Assert.True(queue.TryEnqueue(Match(11, new PlayerRecord(a, 1, 2, 50, 2000))));
        await UnreachableDatabaseTests.WaitFor(() => writer.Saved == 1);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.Saved);
        Assert.Equal(1, writer.Failed);
        Assert.Equal(2, (await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(a, CancellationToken.None))!.Kills);
    }

    // Accepts on 127.0.0.1:listenPort and pipes each connection to host:port, until disposed.
    private sealed class TcpForwarder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        // 기능: 127.0.0.1:listenPort에서 듣기 시작하고 연결 받기를 배경에서 돌린다.
        // 입력: listenPort - 들을 포트, host·port - 연결을 넘길 목적지.
        // 출력: 동작 중인 TcpForwarder(Dispose가 멈춘다).
        public TcpForwarder(int listenPort, string host, int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, listenPort);
            _listener.Start();
            _ = AcceptAsync(host, port);
        }

        // 기능: Dispose될 때까지 들어오는 TCP 연결을 받아 각각 목적지로 잇는 Pipe를 띄운다.
        // 입력: host·port - 연결을 넘길 목적지.
        // 출력: 반환값 없음. 중단 요청으로 끝나며 그때의 예외는 삼킨다.
        private async Task AcceptAsync(string host, int port)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = PipeAsync(inbound, host, port);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        // 기능: 받은 연결을 목적지에 이어 양방향으로 바이트를 복사하고, 한쪽이 닫히면 둘 다 닫는다.
        // 입력: inbound - 받은 연결, host·port - 목적지.
        // 출력: 반환값 없음. 연결 실패·종료 예외는 삼키고 두 소켓이 닫힌다.
        private async Task PipeAsync(TcpClient inbound, string host, int port)
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                try
                {
                    await outbound.ConnectAsync(host, port, _stop.Token);
                    NetworkStream a = inbound.GetStream(), b = outbound.GetStream();
                    await Task.WhenAny(a.CopyToAsync(b, _stop.Token), b.CopyToAsync(a, _stop.Token));
                }
                catch (Exception)
                {
                    // A closed side ends the pipe.
                }
            }
        }

        // 기능: 받기와 모든 Pipe를 취소하고 Listener를 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 더 이상 연결을 받지 않는다.
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    // The hosted writer: start, enqueue, the record lands in the database, stop drains.
    [MySqlFact]
    public async Task TheWriter_SavesWhatTheGameEnqueues()
    {
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = MySqlFactAttribute.ConnectionString });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        string a = NewId("w"), b = NewId("x");
        Assert.True(queue.TryEnqueue(Match(7, new PlayerRecord(a, 1, 1, 150, 30000), new PlayerRecord(b, 2, 0, 0, 20000))));
        // No polling: StopAsync right away must drain the queue (D8).
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.Saved);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, (await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(a, CancellationToken.None))!.Wins);
    }

    // D8: a save still running when the drain limit fires is aborted and counted as Discarded (not Failed).
    [MySqlFact]
    public async Task TheWriter_CountsASaveCutOffByTheDrainLimit_AsDiscarded()
    {
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions
        {
            Enabled = true, ConnectionString = MySqlFactAttribute.ConnectionString, ShutdownDrainSeconds = 1,
        });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        // A first save proves the schema is ready, so the blocker below cannot hold up EnsureSchemaAsync instead.
        Assert.True(queue.TryEnqueue(Match(8, new PlayerRecord(NewId("r"), 1, 0, 0, 1000))));
        var clock = Stopwatch.StartNew();
        while (writer.Saved == 0 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        Assert.Equal(1, writer.Saved);

        // An uncommitted insert of the same account makes the writer's account insert wait on its row lock.
        string blocked = NewId("k");
        await using var blocker = new MySqlConnection(MySqlFactAttribute.ConnectionString);
        await blocker.OpenAsync();
        await using MySqlTransaction hold = await blocker.BeginTransactionAsync();
        await using (var insert = new MySqlCommand("INSERT INTO account (dev_player_id, created_at) VALUES (@dev, UTC_TIMESTAMP(3))", blocker, hold))
        {
            insert.Parameters.AddWithValue("@dev", blocked);
            await insert.ExecuteNonQueryAsync();
        }

        Assert.True(queue.TryEnqueue(Match(9, new PlayerRecord(blocked, 1, 0, 0, 1000))));
        clock.Restart();
        while (queue.Reader.Count > 0 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        Assert.Equal(0, queue.Reader.Count);   // taken by the writer: in flight, not queued
        await Task.Delay(200);

        await writer.StopAsync(CancellationToken.None);
        await hold.RollbackAsync();

        Assert.Equal(1, writer.Saved);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, writer.Discarded);
        Assert.Null(await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(blocked, CancellationToken.None));
    }

    // 기능: 실제 DB에 붙은 StatsQueryService를 띄워 한 플레이어의 전적 조회를 넣고 답을 기다린 뒤 서비스를 멈춘다.
    // 입력: devPlayerId - 조회할 플레이어 id.
    // 출력: 서비스가 돌려준 StatsResponse. 10초 안에 답이 없으면 TimeoutException.
    // Phase 11 D8: what a player asks for, through the service as the server runs it.
    private static async Task<StatsResponse> AskAsync(string devPlayerId)
    {
        var queue = new StatsQueryQueue();
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = MySqlFactAttribute.ConnectionString });
        using var service = new StatsQueryService(queue, options, NullLogger<StatsQueryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(new StatsQuery(1, null!, devPlayerId, Environment.TickCount64)));
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10000)
            {
                if (queue.TryTakeReply(out StatsReply reply)) return reply.Response;
                await Task.Delay(20);
            }
            throw new TimeoutException("no reply");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [MySqlFact]
    public async Task AStatsQuery_AfterSavedMatches_AnswersOk_WithTotalsAndTheNewestMatchFirst()
    {
        MatchStore store = await StoreAsync();
        string a = NewId("q"), b = NewId("r");
        await store.SaveAsync(Match(21, new PlayerRecord(a, 1, 2, 300, 240000), new PlayerRecord(b, 2, 1, 150, 200000)), CancellationToken.None);
        await store.SaveAsync(Match(22, new PlayerRecord(a, 2, 0, 50, 90500), new PlayerRecord(b, 1, 3, 400, 250000)), CancellationToken.None);

        StatsResponse r = await AskAsync(a);

        Assert.Equal(StatsStatus.Ok, r.Status);
        Assert.Equal(2u, r.Summary.Matches);
        Assert.Equal(1u, r.Summary.Wins);
        Assert.Equal(2u, r.Summary.Kills);
        Assert.Equal(1u, r.Summary.Deaths);
        Assert.Equal(350u, r.Summary.Damage);
        Assert.Equal(330u, r.Summary.SurvivalSeconds);
        Assert.Equal(2, r.Rows.Length);
        Assert.Equal(22u, r.Rows[0].Round);
        Assert.Equal(2, r.Rows[0].Placement);
        Assert.Equal(90500u, r.Rows[0].SurvivalMs);
        Assert.Equal(21u, r.Rows[1].Round);
        Assert.Equal(1, r.Rows[1].Placement);
        Assert.Equal(2, r.Rows[1].Players);
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - r.Rows[0].EndedUnixSeconds) < 600);
    }

    [MySqlFact]
    public async Task AStatsQuery_ForAnIdWithNoMatch_AnswersNoRecord()
    {
        await StoreAsync();
        StatsResponse r = await AskAsync(NewId("none"));
        Assert.Equal(StatsStatus.NoRecord, r.Status);
        Assert.Empty(r.Rows);
    }
}

// Without a database the writer must not fail the server. Phase 10 D8: it keeps trying, so every record without a
// database is a failed save, counted. Needs no MySQL.
public class UnreachableDatabaseTests
{
    // 기능: OS에서 빈 Loopback 포트를 받아 바로 놓아 준다.
    // 입력: 없음.
    // 출력: 지금 아무도 듣지 않는 포트 번호.
    // A loopback port nothing listens on (taken from the OS, then released).
    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // 기능: 조건이 참이 될 때까지 50ms마다 확인하며 최대 15초 기다린다.
    // 입력: condition - 기다릴 조건.
    // 출력: 반환값 없음. 조건이 참이 되거나 15초가 지나면 끝난다(실패를 알리지 않는다).
    internal static async Task WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 15000) await Task.Delay(50);
    }

    [Fact]
    public async Task TheWriter_WithNoDatabase_CountsEachRecordAsFailed_AndKeepsRunning()
    {
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions
        {
            Enabled = true,
            ConnectionString = $"Server=127.0.0.1;Port={FreePort()};Database=projecth;User ID=nobody;Password=none;Connection Timeout=1;Pooling=false",
            MaxAttempts = 1,
            ShutdownDrainSeconds = 5,
        });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(new MatchRecord(1, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>())));
        await WaitFor(() => writer.Failed == 1);
        Assert.True(queue.TryEnqueue(new MatchRecord(2, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>())));
        await WaitFor(() => writer.Failed == 2);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, writer.Failed);
        Assert.Equal(0, writer.Discarded);
        Assert.Equal(0, writer.Saved);
        Assert.Equal(TaskStatus.RanToCompletion, writer.ExecuteTask!.Status);
    }

    // The drain limit fires while the writer is still connecting (a server that accepts TCP but never greets): the
    // writer ends quietly instead of faulting (which would stop the host), and the queued record is counted.
    [Fact]
    public async Task TheWriter_AbortedWhileConnecting_EndsQuietly_AndCountsTheRecord()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var queue = new MatchHistoryQueue(4);
            var options = Options.Create(new PersistenceOptions
            {
                Enabled = true,
                ConnectionString = $"Server=127.0.0.1;Port={port};Database=projecth;User ID=nobody;Password=none;Connection Timeout=5",
                ShutdownDrainSeconds = 1,
            });
            using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
            await writer.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(new MatchRecord(1, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>())));

            await writer.StopAsync(CancellationToken.None);

            Assert.Equal(TaskStatus.RanToCompletion, writer.ExecuteTask!.Status);
            Assert.Equal(1, writer.Discarded);
            Assert.Equal(0, writer.Saved);
            Assert.Equal(0, writer.Failed);
        }
        finally
        {
            silent.Stop();
            silent.Dispose();
        }
    }
}
