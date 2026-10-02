using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D13: one tick's building events, collected while the tick runs and written at its end as BuildEvents packets
// (placed, then health, then destroyed; at most MaxPacketSize bytes each). Each event knows its interest cell (D14), so
// a packet holds only the events a client's window covers. Health is coalesced: a piece hit several times in a tick
// sends one record with its damage at the end (request §85). Placed holds one piece per player (a player places at most
// one per tick); the damaged, health and destroyed lists grow with the pieces up to the match's piece limit (a collapse
// can take every piece in one tick). Game loop thread only.
public sealed class BuildReplication
{
    private readonly BuildWorld _world;
    private readonly BuildPieceRecord[] _placed;
    private readonly byte[] _placedCell;
    private int _placedCount;
    private int[] _damagedSlots;
    private bool[] _damagedFlag;
    private int _damagedCount;
    // Written by Collect from _damagedSlots at the end of the tick: the pieces still standing.
    private uint[] _healthIds;
    private ushort[] _healthDamage;
    private byte[] _healthCell;
    private int _healthCount;
    private uint[] _destroyed;
    private byte[] _destroyedCell;
    private int _destroyedCount;
    private readonly int _cellsPerInterest;
    private readonly int _interestPerSide;

    public BuildReplication(BuildWorld world, BuildingCatalog catalog, int maxPlayers)
    {
        _world = world;
        int pieces = Math.Min(world.Capacity, 256);
        _placed = new BuildPieceRecord[Math.Max(1, maxPlayers)];
        _placedCell = new byte[_placed.Length];
        _damagedSlots = new int[pieces];
        _damagedFlag = new bool[pieces];
        _healthIds = new uint[pieces];
        _healthDamage = new ushort[pieces];
        _healthCell = new byte[pieces];
        _destroyed = new uint[pieces];
        _destroyedCell = new byte[pieces];
        _cellsPerInterest = (int)(catalog.InterestCellSize / BuildGrid.CellSize);
        _interestPerSide = BuildGrid.CellsX / _cellsPerInterest;
    }

    // D14: how many building events the match has had (BuildEvents.Version).
    public uint Version { get; private set; }
    public int PlacedCount => _placedCount;
    public int HealthCount => _healthCount;
    public int DestroyedCount => _destroyedCount;
    public bool HasEvents => _placedCount > 0 || _healthCount > 0 || _destroyedCount > 0;
    // The interest grid: InterestPerSide x InterestPerSide cells of CellsPerInterest x CellsPerInterest build cells.
    public int InterestPerSide => _interestPerSide;

    // D14: the interest cell (bit index in a 64-bit window) of a build cell.
    public int InterestCell(int buildX, int buildZ) => buildX / _cellsPerInterest + _interestPerSide * (buildZ / _cellsPerInterest);

    public int InterestCell(in BuildPieceShape shape) => InterestCell(shape.X, shape.Z);

    public void Placed(in BuildPieceRecord record)
    {
        if (_placedCount == _placed.Length) return;   // unreachable: one per player per tick
        _placed[_placedCount] = record;
        _placedCell[_placedCount++] = (byte)InterestCell(record.Shape);
        Version++;
    }

    // A piece took damage this tick (its Health record goes out at the end of the tick, once).
    public void Damaged(int slot)
    {
        if (slot >= _damagedFlag.Length) Array.Resize(ref _damagedFlag, Grown(_damagedFlag.Length, slot + 1));
        if (_damagedFlag[slot]) return;
        _damagedFlag[slot] = true;
        if (_damagedCount == _damagedSlots.Length) Array.Resize(ref _damagedSlots, Grown(_damagedSlots.Length, _damagedCount + 1));
        _damagedSlots[_damagedCount++] = slot;
    }

    // Doubling, at least to need, never past the match's piece limit (no list holds more than every piece).
    private int Grown(int length, int need) => Math.Min(_world.Capacity, Math.Max(need, length * 2));

    // A piece left the world this tick (destroyed or collapsed). Its pending health record is dropped.
    public void Destroyed(int slot, uint id, in BuildPieceShape shape)
    {
        if (slot < _damagedFlag.Length && _damagedFlag[slot])
        {
            _damagedFlag[slot] = false;
            for (int i = 0; i < _damagedCount; i++)
            {
                if (_damagedSlots[i] != slot) continue;
                _damagedSlots[i] = _damagedSlots[--_damagedCount];
                break;
            }
        }
        if (_destroyedCount == _destroyed.Length)
        {
            if (_destroyed.Length == _world.Capacity) return;   // unreachable: at most every piece once per tick
            int size = Grown(_destroyed.Length, _destroyedCount + 1);
            Array.Resize(ref _destroyed, size);
            Array.Resize(ref _destroyedCell, size);
        }
        _destroyed[_destroyedCount] = id;
        _destroyedCell[_destroyedCount++] = (byte)InterestCell(shape);
        Version++;
    }

    // End of the tick: the damaged pieces' health records, from their damage now.
    public void Collect()
    {
        if (_damagedCount > _healthIds.Length)
        {
            int size = Grown(_healthIds.Length, _damagedCount);
            Array.Resize(ref _healthIds, size);
            Array.Resize(ref _healthDamage, size);
            Array.Resize(ref _healthCell, size);
        }
        for (int i = 0; i < _damagedCount; i++)
        {
            int slot = _damagedSlots[i];
            _damagedFlag[slot] = false;
            ref BuildPiece piece = ref _world.At(slot);
            if (piece.Id == 0) continue;
            _healthIds[_healthCount] = piece.Id;
            _healthDamage[_healthCount] = piece.Damage;
            _healthCell[_healthCount++] = (byte)InterestCell(piece.Shape);
            Version++;
        }
        _damagedCount = 0;
    }

    // Where the next packet starts (one cursor per recipient pass).
    public struct Cursor
    {
        public int Placed;
        public int Health;
        public int Destroyed;
    }

    // Writes the next BuildEvents packet into buffer from cursor, with only the events whose interest cell is in cells,
    // and returns its length (0: no more events for these cells). At most buffer.Length bytes.
    public int NextPacket(Span<byte> buffer, ref Cursor cursor, ulong cells)
    {
        var writer = new PacketWriter(buffer);
        BuildEventsPacket.WriteHeader(ref writer, Version);
        int placed = 0;
        int health = 0;
        int destroyed = 0;
        int room = buffer.Length - BuildEventsPacket.HeaderSize;
        for (; cursor.Placed < _placedCount && placed < 255; cursor.Placed++)
        {
            if ((cells & (1UL << _placedCell[cursor.Placed])) == 0) continue;
            if (room < BuildPieceRecord.PlacedSize) break;
            BuildPieceRecord.WritePlaced(ref writer, _placed[cursor.Placed]);
            room -= BuildPieceRecord.PlacedSize;
            placed++;
        }
        if (cursor.Placed == _placedCount)
        {
            for (; cursor.Health < _healthCount && health < 255; cursor.Health++)
            {
                if ((cells & (1UL << _healthCell[cursor.Health])) == 0) continue;
                if (room < BuildEventsPacket.HealthSize) break;
                BuildEventsPacket.WriteHealth(ref writer, _healthIds[cursor.Health], _healthDamage[cursor.Health]);
                room -= BuildEventsPacket.HealthSize;
                health++;
            }
        }
        if (cursor.Placed == _placedCount && cursor.Health == _healthCount)
        {
            for (; cursor.Destroyed < _destroyedCount && destroyed < 255; cursor.Destroyed++)
            {
                if ((cells & (1UL << _destroyedCell[cursor.Destroyed])) == 0) continue;
                if (room < BuildEventsPacket.DestroyedSize) break;
                BuildEventsPacket.WriteDestroyed(ref writer, _destroyed[cursor.Destroyed]);
                room -= BuildEventsPacket.DestroyedSize;
                destroyed++;
            }
        }
        if (placed + health + destroyed == 0) return 0;
        BuildEventsPacket.Patch(buffer, placed, health, destroyed);
        return writer.Length;
    }

    // After every recipient got its packets.
    public void Clear()
    {
        _placedCount = 0;
        _healthCount = 0;
        _destroyedCount = 0;
    }

    // A round reset: nothing pending survives, and the damaged flags start clean.
    public void Reset()
    {
        for (int i = 0; i < _damagedCount; i++) _damagedFlag[_damagedSlots[i]] = false;
        _damagedCount = 0;
        Clear();
    }
}
