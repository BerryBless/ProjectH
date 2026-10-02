using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Qa;

public sealed record QaTickMetrics(int WindowSeconds, int Samples, double TickP50Ms, double TickP95Ms, double TickP99Ms, double TickMaxMs,
    double QaCommandMs, double QaCommandP99Ms, double QaCommandMaxMs, double RateSeconds, double PktInPerSec, double PktOutPerSec,
    double CpuPercent);

// QA-1 /qa/metrics: the QA module's own rings (TickMetrics is reset by every stats line, so it cannot answer a window).
// Per tick: the tick's duration and the QA time inside it (§150). Per second: the packet totals and the process CPU time,
// so rates over a window are a difference of two samples. Fixed arrays sized at construction (MetricsWindowSeconds at
// SimHz); nothing grows. Game loop thread only: queries run as work items.
internal sealed class QaMetrics
{
    private readonly int _simHz;
    private readonly double[] _tickMs;
    private readonly double[] _qaMs;
    private readonly double[] _scratch;
    private int _next;
    private int _count;

    // One sample per SimHz ticks, one more than the window so a full window has both ends.
    private const int SecondSlots = QaOptions.MetricsWindowSeconds + 1;
    private readonly long[] _secTimestamp = new long[SecondSlots];
    private readonly long[] _secPacketsIn = new long[SecondSlots];
    private readonly long[] _secPacketsOut = new long[SecondSlots];
    private readonly TimeSpan[] _secCpu = new TimeSpan[SecondSlots];
    private int _secNext;
    private int _secCount;
    private int _ticksToSample;

    public QaMetrics(int simHz)
    {
        _simHz = simHz;
        int capacity = QaOptions.MetricsWindowSeconds * simHz;
        _tickMs = new double[capacity];
        _qaMs = new double[capacity];
        _scratch = new double[capacity];
    }

    public int Count => _count;

    public void Record(double tickMs, double qaMs)
    {
        _tickMs[_next] = tickMs;
        _qaMs[_next] = qaMs;
        _next = (_next + 1) % _tickMs.Length;
        if (_count < _tickMs.Length) _count++;
    }

    // Every tick; samples once per second of ticks.
    public void SampleSecond(long packetsIn, long packetsOut)
    {
        if (_ticksToSample-- > 0) return;
        _ticksToSample = _simHz - 1;
        _secTimestamp[_secNext] = Stopwatch.GetTimestamp();
        _secPacketsIn[_secNext] = packetsIn;
        _secPacketsOut[_secNext] = packetsOut;
        _secCpu[_secNext] = Environment.CpuUsage.TotalTime;
        _secNext = (_secNext + 1) % SecondSlots;
        if (_secCount < SecondSlots) _secCount++;
    }

    public QaTickMetrics Snapshot(int windowSeconds, long packetsIn, long packetsOut)
    {
        int n = Math.Min(_count, windowSeconds * _simHz);
        double p50 = 0, p95 = 0, p99 = 0, max = 0, qaMean = 0, qaMax = 0, qaP99 = 0;
        if (n > 0)
        {
            Copy(_tickMs, n);
            Array.Sort(_scratch, 0, n);
            p50 = Percentile(n, 0.50);
            p95 = Percentile(n, 0.95);
            p99 = Percentile(n, 0.99);
            max = _scratch[n - 1];
            Copy(_qaMs, n);
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _scratch[i];
            qaMean = sum / n;
            Array.Sort(_scratch, 0, n);
            qaP99 = Percentile(n, 0.99);
            qaMax = _scratch[n - 1];
        }

        // The oldest per-second sample inside the window (or the oldest there is) against the totals now.
        double pktIn = 0, pktOut = 0, cpu = 0, seconds = 0;
        if (_secCount > 0)
        {
            long now = Stopwatch.GetTimestamp();
            int oldest = (_secNext - _secCount + SecondSlots) % SecondSlots;
            int pick = oldest;
            for (int i = 0; i < _secCount; i++)
            {
                int index = (oldest + i) % SecondSlots;
                if (Stopwatch.GetElapsedTime(_secTimestamp[index], now).TotalSeconds <= windowSeconds)
                {
                    pick = index;
                    break;
                }
            }
            seconds = Stopwatch.GetElapsedTime(_secTimestamp[pick], now).TotalSeconds;
            if (seconds > 0.001)
            {
                pktIn = (packetsIn - _secPacketsIn[pick]) / seconds;
                pktOut = (packetsOut - _secPacketsOut[pick]) / seconds;
                cpu = (Environment.CpuUsage.TotalTime - _secCpu[pick]).TotalSeconds / (seconds * Environment.ProcessorCount) * 100.0;
            }
        }

        return new QaTickMetrics(windowSeconds, n, p50, p95, p99, max, qaMean, qaP99, qaMax, seconds, pktIn, pktOut, cpu);
    }

    private void Copy(double[] ring, int n)
    {
        // The newest n samples, in any order (they are sorted next).
        for (int i = 0; i < n; i++) _scratch[i] = ring[(_next - 1 - i + ring.Length) % ring.Length];
    }

    // Nearest rank, as TickMetrics.
    private double Percentile(int n, double fraction)
    {
        int index = (int)Math.Ceiling(fraction * n) - 1;
        return _scratch[Math.Clamp(index, 0, n - 1)];
    }
}

public sealed record QaEvent(long Seq, uint Tick, DateTime Utc, string Type, string? Player, IReadOnlyDictionary<string, object?>? Data);

// QA-1 D6, §39: events made by comparing each tick with the one before (no hook inside Match), kept in a ring of
// EventCapacity: the oldest is overwritten and counted. Game loop thread only. Cost per tick: one pass over the players
// (at most MaxPlayers) and a few counters; allocation only when an event happens or a player appears.
internal sealed class QaEvents
{
    private sealed class Seen
    {
        public bool Graced;
        public bool Alive;
        public int Health;
        public int Shield;
        public int Kills;
        public int Inventory;
        public bool Present;
    }

    private readonly QaEvent?[] _ring = new QaEvent?[QaOptions.EventCapacity];
    private long _nextSeq = 1;
    private int _count;

    // Removed when the player leaves the match (not Present after a pass) or the match object is replaced.
    private readonly Dictionary<PlayerEntity, Seen> _players = new(ReferenceEqualityComparer.Instance);
    private readonly List<PlayerEntity> _gone = new();
    private readonly List<PlayerEntity> _killed = new();
    private PlayerEntity? _killer;
    private int _killers;
    private Match? _match;
    private MatchFlowState _state;
    private int _zonePhase;
    private uint _nextPieceId;
    private long _piecesDestroyed;
    private long _piecesCollapsed;

    public long NextSeq => _nextSeq;
    public int Count => _count;
    public long Oldest => _nextSeq - _count;
    // Overwritten since the start.
    public long DroppedTotal => _nextSeq - 1 - _count;

    public void Add(uint tick, string type, string? player, IReadOnlyDictionary<string, object?>? data = null)
    {
        _ring[(_nextSeq - 1) % _ring.Length] = new QaEvent(_nextSeq, tick, DateTime.UtcNow, type, player, data);
        _nextSeq++;
        if (_count < _ring.Length) _count++;
    }

    // after is exclusive. next = the last returned seq (or after when nothing came), to be passed back as after. missed =
    // events after `after` that were already overwritten.
    public object Query(long after, int max)
    {
        if (after < 0) after = 0;
        long first = Math.Max(after + 1, Oldest);
        long missed = Math.Max(0, Oldest - (after + 1));
        var events = new List<QaEvent>();
        long seq = first;
        for (; seq < _nextSeq && events.Count < max; seq++) events.Add(_ring[(seq - 1) % _ring.Length]!);
        long next = events.Count > 0 ? events[^1].Seq : after;
        return new { next, oldest = Oldest, latest = _nextSeq - 1, dropped = missed, droppedTotal = DroppedTotal, events };
    }

    public void Diff(Match match)
    {
        uint tick = match.ServerTick;
        if (!ReferenceEquals(match, _match))
        {
            // The first pass sets the baseline; a later new match object is a reset (GameLoop.ResetMatch).
            if (_match != null) Add(tick, "MatchReset", null);
            _match = match;
            _players.Clear();
            _state = match.Flow.State;
            _zonePhase = match.Zone.Phase;
            _nextPieceId = match.Build.NextId;
            _piecesDestroyed = match.PiecesDestroyed;
            _piecesCollapsed = match.PiecesCollapsed;
        }

        // The match start and the round reset set health and shield anew for everyone: no damage or heal event then.
        MatchFlowState state = match.Flow.State;
        bool stateChanged = state != _state;
        if (stateChanged)
        {
            Add(tick, "MatchStateChanged", null, new Dictionary<string, object?> { ["from"] = _state.ToString(), ["to"] = state.ToString(), ["round"] = (int)match.Flow.Round });
            _state = state;
        }

        foreach (Seen seen in _players.Values) seen.Present = false;
        _killed.Clear();
        _killer = null;
        _killers = 0;
        for (int i = 0; i < match.PlayerCount; i++)
        {
            PlayerEntity p = match.PlayerAt(i);
            int inventory = InventoryHash(p);
            if (!_players.TryGetValue(p, out Seen? seen))
            {
                _players.Add(p, new Seen
                {
                    Graced = p.IsGraced, Alive = p.Alive, Health = p.Health, Shield = p.Shield, Kills = p.Kills, Inventory = inventory, Present = true,
                });
                Add(tick, "PlayerJoined", p.DevPlayerId, new Dictionary<string, object?> { ["entityId"] = (int)p.EntityId, ["alive"] = p.Alive });
                continue;
            }
            seen.Present = true;
            if (seen.Graced != p.IsGraced) Add(tick, p.IsGraced ? "PlayerGraced" : "PlayerResumed", p.DevPlayerId);

            int before = seen.Health + seen.Shield;
            int now = p.Health + p.Shield;
            if (!stateChanged && now < before)
                Add(tick, "PlayerDamaged", p.DevPlayerId, new Dictionary<string, object?> { ["amount"] = before - now, ["health"] = p.Health, ["shield"] = p.Shield });
            else if (!stateChanged && now > before && seen.Alive && p.Alive)
                Add(tick, "PlayerHealed", p.DevPlayerId, new Dictionary<string, object?> { ["amount"] = now - before, ["health"] = p.Health, ["shield"] = p.Shield });

            if (seen.Alive && !p.Alive) _killed.Add(p);
            else if (!seen.Alive && p.Alive) Add(tick, "PlayerRespawned", p.DevPlayerId);
            if (p.Kills > seen.Kills)
            {
                _killer = p;
                _killers++;
            }
            if (seen.Inventory != inventory) Add(tick, "InventoryChanged", p.DevPlayerId);

            seen.Graced = p.IsGraced;
            seen.Alive = p.Alive;
            seen.Health = p.Health;
            seen.Shield = p.Shield;
            seen.Kills = p.Kills;
            seen.Inventory = inventory;
        }

        // D6: the killer is a guess: the one player whose kills rose this tick (none or several: unknown).
        string? killer = _killers == 1 ? _killer!.DevPlayerId : null;
        foreach (PlayerEntity victim in _killed)
            Add(tick, "PlayerKilled", victim.DevPlayerId, new Dictionary<string, object?> { ["killer"] = killer, ["placement"] = (int)victim.Placement });

        _gone.Clear();
        foreach (var pair in _players)
        {
            if (!pair.Value.Present) _gone.Add(pair.Key);
        }
        foreach (PlayerEntity p in _gone)
        {
            _players.Remove(p);
            Add(tick, "PlayerLeft", p.DevPlayerId);
        }

        int phase = match.Zone.Phase;
        if (phase != _zonePhase)
        {
            Add(tick, "ZoneChanged", null, new Dictionary<string, object?> { ["phase"] = phase, ["from"] = _zonePhase });
            _zonePhase = phase;
        }

        // Pieces by counters, not per id: ids only grow, so new ids = placements; destroyed and collapsed are totals.
        uint nextId = match.Build.NextId;
        if (nextId != _nextPieceId)
        {
            Add(tick, "BuildPlaced", null, new Dictionary<string, object?> { ["count"] = (long)(nextId - _nextPieceId), ["pieces"] = match.BuildPieces, ["lastId"] = (long)(nextId - 1) });
            _nextPieceId = nextId;
        }
        if (match.PiecesDestroyed != _piecesDestroyed)
        {
            Add(tick, "BuildDestroyed", null, new Dictionary<string, object?>
            {
                ["count"] = match.PiecesDestroyed - _piecesDestroyed, ["collapsed"] = match.PiecesCollapsed - _piecesCollapsed, ["pieces"] = match.BuildPieces,
            });
            _piecesDestroyed = match.PiecesDestroyed;
            _piecesCollapsed = match.PiecesCollapsed;
        }
    }

    // What InventoryState and ResourcesState carry, without the magazines (a shot is not an inventory change, D14).
    private static int InventoryHash(PlayerEntity p)
    {
        var inv = p.Inventory;
        var hash = new HashCode();
        for (int i = 0; i < Game.Items.Inventory.SlotCount; i++)
        {
            hash.Add(inv.Slots[i].Weapon?.Id ?? 0);
            hash.Add(inv.Slots[i].Rarity);
        }
        hash.Add(inv.CurrentSlot);
        hash.Add(inv.GetAmmo(AmmoType.Light));
        hash.Add(inv.GetAmmo(AmmoType.Medium));
        hash.Add(inv.GetAmmo(AmmoType.Heavy));
        hash.Add(inv.Medkits);
        hash.Add(inv.ShieldCells);
        hash.Add(inv.Resource(ProjectH.Shared.Simulation.BuildMaterialType.Wood));
        hash.Add(inv.Resource(ProjectH.Shared.Simulation.BuildMaterialType.Stone));
        hash.Add(inv.Resource(ProjectH.Shared.Simulation.BuildMaterialType.Metal));
        return hash.ToHashCode();
    }
}
