using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace ProjectH.Server.Persistence;

public sealed record PlayerStats(string DevPlayerId, int Matches, int Wins, int Kills, int Deaths, long Damage, long SurvivalMs);

public sealed record MatchHistoryEntry(long MatchId, int Round, DateTime EndedUtc, int Players, byte Placement, int Kills, int Damage, int SurvivalMs);

// Phase 9 (§36-37): the MySQL side. Every call opens a pooled connection and returns it on the way out
// (await using), and a match is saved in one short transaction: accounts, profiles, the match, its players and the
// running statistics, or nothing. Only MatchHistoryWriter calls SaveAsync (one writer, so no row-lock ordering
// issues between transactions); the read methods are for tools and tests. Never called from the game loop.
public sealed class MatchStore
{
    // D3: idempotent schema, created at startup. Table names avoid MATCH (a reserved word).
    private static readonly string[] Schema =
    {
        """
        CREATE TABLE IF NOT EXISTS account (
          id BIGINT NOT NULL AUTO_INCREMENT,
          dev_player_id VARCHAR(32) NOT NULL,
          created_at DATETIME(3) NOT NULL,
          PRIMARY KEY (id),
          UNIQUE KEY ux_account_dev_player_id (dev_player_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS player_profile (
          account_id BIGINT NOT NULL,
          display_name VARCHAR(32) NOT NULL,
          updated_at DATETIME(3) NOT NULL,
          PRIMARY KEY (account_id),
          CONSTRAINT fk_player_profile_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS player_stats (
          account_id BIGINT NOT NULL,
          matches INT NOT NULL,
          wins INT NOT NULL,
          kills INT NOT NULL,
          deaths INT NOT NULL,
          damage BIGINT NOT NULL,
          survival_ms BIGINT NOT NULL,
          updated_at DATETIME(3) NOT NULL,
          PRIMARY KEY (account_id),
          CONSTRAINT fk_player_stats_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS game_match (
          id BIGINT NOT NULL AUTO_INCREMENT,
          round_no INT NOT NULL,
          started_at DATETIME(3) NOT NULL,
          ended_at DATETIME(3) NOT NULL,
          players TINYINT UNSIGNED NOT NULL,
          winner_account_id BIGINT NULL,
          PRIMARY KEY (id),
          CONSTRAINT fk_game_match_winner FOREIGN KEY (winner_account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS match_player (
          match_id BIGINT NOT NULL,
          account_id BIGINT NOT NULL,
          placement TINYINT UNSIGNED NOT NULL,
          kills SMALLINT UNSIGNED NOT NULL,
          damage INT NOT NULL,
          survival_ms INT NOT NULL,
          PRIMARY KEY (match_id, account_id),
          KEY ix_match_player_account (account_id, match_id),
          CONSTRAINT fk_match_player_match FOREIGN KEY (match_id) REFERENCES game_match (id),
          CONSTRAINT fk_match_player_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
        // Review fix C7: one row (id 1), the schema version the migrations below brought this database to. A database without
        // the table is version 1 (Phase 9).
        """
        CREATE TABLE IF NOT EXISTS schema_version (
          id TINYINT UNSIGNED NOT NULL,
          version INT NOT NULL,
          updated_at DATETIME(3) NOT NULL,
          PRIMARY KEY (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin
        """,
    };

    // Review fix C7: the version this code writes and reads, and the match_player columns version 2 adds (the anti-cheat
    // counters of PlayerRecord, in that order).
    public const int SchemaVersion = 2;
    public static readonly string[] AntiCheatColumns =
    {
        "shots", "pellets", "hits", "rewind_ticks", "rewind_clamped", "max_hit_distance_cm", "movement_anomalies", "max_aim_turn",
    };
    private const int DuplicateColumnError = 1060;   // ER_DUP_FIELDNAME: another server added the column first

    private readonly string _connectionString;

    public MatchStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    // 기능: 스키마를 만들고 최신 버전으로 옮긴다(Phase 9 D3, 리뷰 수정 C7). CREATE IF NOT EXISTS 뒤 schema_version을 읽고, 2보다 낮으면(표 없음 =
    //   기존 DB = 1) match_player에 없는 Anti-cheat 열만 information_schema로 확인해 ALTER로 더하고 버전 2를 적는다. 몇 번을 실행해도,
    //   중간에 끊긴 이동을 다시 실행해도 안전하다. DDL은 MySQL에서 Transaction 밖이다(열마다 자동 Commit). 더 높은 버전이면 건드리지 않는다.
    // 입력: cancellationToken - 취소.
    // 출력: 완료되면 끝나는 Task. 연결은 반환된다.
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string sql in Schema)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        int version;
        await using (var command = new MySqlCommand("SELECT version FROM schema_version WHERE id = 1", connection))
        {
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            version = value == null || value is DBNull ? 1 : Convert.ToInt32(value);
        }
        if (version >= SchemaVersion) return;

        foreach (string column in AntiCheatColumns)
        {
            if (await HasColumnAsync(connection, "match_player", column, cancellationToken)) continue;
            // The name comes from AntiCheatColumns (a constant list), never from input.
            await using var command = new MySqlCommand($"ALTER TABLE match_player ADD COLUMN {column} INT NOT NULL DEFAULT 0", connection);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException e) when (e.Number == DuplicateColumnError)
            {
                // Added by another server between the check and the ALTER: the column is there, which is all this needs.
            }
        }
        await using (var command = new MySqlCommand(
            "INSERT INTO schema_version (id, version, updated_at) VALUES (1, @version, UTC_TIMESTAMP(3)) AS new " +
            "ON DUPLICATE KEY UPDATE version = GREATEST(schema_version.version, new.version), updated_at = new.updated_at", connection))
        {
            command.Parameters.AddWithValue("@version", SchemaVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // 기능: 지금 데이터베이스의 표에 열이 있는지 information_schema로 본다(리뷰 수정 C7).
    // 입력: connection - 열린 연결, table·column - 표와 열 이름, cancellationToken - 취소.
    // 출력: 있으면 true.
    private static async Task<bool> HasColumnAsync(MySqlConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column",
            connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    // 기능: 끝난 경기 하나를 한 Transaction으로 저장한다(계정·프로필·경기·참가자(리뷰 수정 C7: Anti-cheat 열 포함)·누적 통계, 아니면 아무것도).
    // 입력: record - 경기 기록, cancellationToken - 취소.
    // 출력: 새 game_match id. 실패하면 예외(Rollback).
    // One transaction for the whole match. Returns the new game_match id.
    public async Task<long> SaveAsync(MatchRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Two clients can share a DevPlayerId (utf8mb4_0900_bin is NO PAD and binary, so two ids share an account exactly
        // when they are ordinally equal; a PAD SPACE collation would also merge ids differing by trailing spaces). Per
        // account only the record with the best placement is written (lowest non-zero; 0 = unranked counts as worst; ties:
        // the first), so the (match_id, account_id) key is never hit, the match is not lost, and a duplicate who won keeps
        // the win.
        int count = record.Players.Count;
        var accountIds = new long[count];
        var kept = new Dictionary<long, int>(count);   // account id -> index of the record written for it
        for (int i = 0; i < count; i++)
        {
            accountIds[i] = await EnsureAccountAsync(connection, transaction, record.Players[i].DevPlayerId, cancellationToken);
            if (!kept.TryGetValue(accountIds[i], out int best) || PlacementRank(record.Players[i]) < PlacementRank(record.Players[best]))
                kept[accountIds[i]] = i;
        }
        var write = new bool[count];
        long? winnerAccountId = null;
        foreach (int i in kept.Values)
        {
            write[i] = true;
            if (record.Players[i].DevPlayerId == record.WinnerDevPlayerId) winnerAccountId = accountIds[i];
        }
        int written = kept.Count;

        long matchId;
        await using (var command = new MySqlCommand(
            "INSERT INTO game_match (round_no, started_at, ended_at, players, winner_account_id) " +
            "VALUES (@round, @started, @ended, @players, @winner); SELECT LAST_INSERT_ID();", connection, transaction))
        {
            command.Parameters.AddWithValue("@round", record.Round);
            command.Parameters.AddWithValue("@started", record.StartedUtc);
            command.Parameters.AddWithValue("@ended", record.EndedUtc);
            command.Parameters.AddWithValue("@players", written);
            command.Parameters.AddWithValue("@winner", winnerAccountId.HasValue ? winnerAccountId.Value : DBNull.Value);
            matchId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        for (int i = 0; i < record.Players.Count; i++)
        {
            if (!write[i]) continue;
            PlayerRecord player = record.Players[i];
            // Review fix C7: plus the eight anti-cheat counters (schema v2).
            await using (var command = new MySqlCommand(
                "INSERT INTO match_player (match_id, account_id, placement, kills, damage, survival_ms, shots, pellets, hits, rewind_ticks, " +
                "rewind_clamped, max_hit_distance_cm, movement_anomalies, max_aim_turn) " +
                "VALUES (@match, @account, @placement, @kills, @damage, @survival, @shots, @pellets, @hits, @rewindTicks, @rewindClamped, " +
                "@maxHitDistance, @movementAnomalies, @maxAimTurn)", connection, transaction))
            {
                command.Parameters.AddWithValue("@match", matchId);
                command.Parameters.AddWithValue("@account", accountIds[i]);
                command.Parameters.AddWithValue("@placement", player.Placement);
                command.Parameters.AddWithValue("@kills", Math.Min(player.Kills, ushort.MaxValue));
                command.Parameters.AddWithValue("@damage", player.Damage);
                command.Parameters.AddWithValue("@survival", player.SurvivalMs);
                command.Parameters.AddWithValue("@shots", player.Shots);
                command.Parameters.AddWithValue("@pellets", player.Pellets);
                command.Parameters.AddWithValue("@hits", player.Hits);
                command.Parameters.AddWithValue("@rewindTicks", player.RewindTicks);
                command.Parameters.AddWithValue("@rewindClamped", player.RewindClamped);
                command.Parameters.AddWithValue("@maxHitDistance", player.MaxHitDistanceCm);
                command.Parameters.AddWithValue("@movementAnomalies", player.MovementAnomalies);
                command.Parameters.AddWithValue("@maxAimTurn", player.MaxAimTurn);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = new MySqlCommand(
                "INSERT INTO player_stats (account_id, matches, wins, kills, deaths, damage, survival_ms, updated_at) " +
                "VALUES (@account, 1, @win, @kills, @death, @damage, @survival, UTC_TIMESTAMP(3)) AS new " +
                "ON DUPLICATE KEY UPDATE matches = player_stats.matches + 1, wins = player_stats.wins + new.wins, " +
                "kills = player_stats.kills + new.kills, deaths = player_stats.deaths + new.deaths, " +
                "damage = player_stats.damage + new.damage, survival_ms = player_stats.survival_ms + new.survival_ms, " +
                "updated_at = new.updated_at", connection, transaction))
            {
                bool won = player.Placement == 1;
                command.Parameters.AddWithValue("@account", accountIds[i]);
                command.Parameters.AddWithValue("@win", won ? 1 : 0);
                command.Parameters.AddWithValue("@kills", player.Kills);
                command.Parameters.AddWithValue("@death", won ? 0 : 1);
                command.Parameters.AddWithValue("@damage", player.Damage);
                command.Parameters.AddWithValue("@survival", player.SurvivalMs);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return matchId;
    }

    public async Task<PlayerStats?> GetStatsAsync(string devPlayerId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            "SELECT s.matches, s.wins, s.kills, s.deaths, s.damage, s.survival_ms FROM player_stats s " +
            "JOIN account a ON a.id = s.account_id WHERE a.dev_player_id = @dev", connection);
        command.Parameters.AddWithValue("@dev", devPlayerId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PlayerStats(devPlayerId, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt64(4), reader.GetInt64(5));
    }

    // Newest first, at most limit (1-100) entries.
    public async Task<IReadOnlyList<MatchHistoryEntry>> GetHistoryAsync(string devPlayerId, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            "SELECT m.id, m.round_no, m.ended_at, m.players, p.placement, p.kills, p.damage, p.survival_ms " +
            "FROM match_player p JOIN account a ON a.id = p.account_id JOIN game_match m ON m.id = p.match_id " +
            "WHERE a.dev_player_id = @dev ORDER BY p.match_id DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("@dev", devPlayerId);
        command.Parameters.AddWithValue("@limit", limit);
        var entries = new List<MatchHistoryEntry>(limit);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MatchHistoryEntry(reader.GetInt64(0), reader.GetInt32(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), reader.GetByte(3),
                reader.GetByte(4), reader.GetUInt16(5), reader.GetInt32(6), reader.GetInt32(7)));
        }
        return entries;
    }

    // Lower is better. Placement 0 (unranked) sorts after every real placement.
    private static int PlacementRank(PlayerRecord player) => player.Placement == 0 ? int.MaxValue : player.Placement;

    // The account id for a DevPlayerId, created (with its profile) on first sight. LAST_INSERT_ID(id) on the duplicate
    // path makes one statement return the existing id too.
    private static async Task<long> EnsureAccountAsync(MySqlConnection connection, MySqlTransaction transaction, string devPlayerId,
        CancellationToken cancellationToken)
    {
        long accountId;
        await using (var command = new MySqlCommand(
            "INSERT INTO account (dev_player_id, created_at) VALUES (@dev, UTC_TIMESTAMP(3)) " +
            "ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id); SELECT LAST_INSERT_ID();", connection, transaction))
        {
            command.Parameters.AddWithValue("@dev", devPlayerId);
            accountId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        await using (var command = new MySqlCommand(
            "INSERT IGNORE INTO player_profile (account_id, display_name, updated_at) VALUES (@account, @name, UTC_TIMESTAMP(3))",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@name", devPlayerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return accountId;
    }
}
