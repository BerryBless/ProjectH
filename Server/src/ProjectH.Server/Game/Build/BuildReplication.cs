using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D13: one tick's building events, collected while the tick runs and written at its end as BuildEvents packets
// (placed, then edited (Phase 13.5 D8, coalesced like health: one record per piece per tick), then health, then
// destroyed; at most MaxPacketSize bytes each). Each event knows its interest cell (D14), so
// a packet holds only the events a client's window covers. Health is coalesced: a piece hit several times in a tick
// sends one record with its damage at the end (request §85). Placed holds one piece per player (a player places at most
// one per tick); the damaged, health and destroyed lists grow with the pieces up to the match's piece limit (a collapse
// can take every piece in one tick). Game loop thread only.
public sealed class BuildReplication
{
    private readonly BuildWorld _world;
    private BuildPieceRecord[] _placed;
    private byte[] _placedCell;
    private int _placedCount;
    private int[] _damagedSlots;
    private bool[] _damagedFlag;
    private int _damagedCount;
    // Written by Collect from _damagedSlots at the end of the tick: the pieces still standing.
    private uint[] _healthIds;
    private ushort[] _healthDamage;
    private byte[] _healthCell;
    private int _healthCount;
    // Phase 13.5 D8: the pieces edited this tick (each slot once), and written by Collect: the ones still standing with
    // their state then. Same growth and bound as the damaged and health lists (at most every piece).
    private int[] _editedSlots;
    private bool[] _editedFlag;
    private int _editedSlotCount;
    private uint[] _editedIds;
    private ushort[] _editedStates;
    private byte[] _editedCell;
    private int _editedCount;
    private uint[] _destroyed;
    private byte[] _destroyedCell;
    // Phase 18 D6: why each destroyed piece left (same length and growth as _destroyed).
    private BuildDestroyReason[] _destroyedReason;
    private int _destroyedCount;
    private readonly int _cellsPerInterest;
    private readonly int _interestPerSide;

    // 기능: 한 경기의 건설 이벤트 수집기를 만든다(목록은 조각 256개 또는 경기 상한까지로 시작해 상한까지 자란다).
    // 입력: world - 경기의 조각 저장소, catalog - 관심 칸 크기를 담은 건설 수치, maxPlayers - Tick당 Placed 칸 수.
    // 출력: 이벤트가 없는 BuildReplication(Phase 18: Destroyed 이유 배열 포함).
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
        _editedSlots = new int[pieces];
        _editedFlag = new bool[pieces];
        _editedIds = new uint[pieces];
        _editedStates = new ushort[pieces];
        _editedCell = new byte[pieces];
        _destroyed = new uint[pieces];
        _destroyedCell = new byte[pieces];
        _destroyedReason = new BuildDestroyReason[pieces];
        _cellsPerInterest = (int)(catalog.InterestCellSize / BuildGrid.CellSize);
        _interestPerSide = BuildGrid.CellsX / _cellsPerInterest;
    }

    // D14: how many building events the match has had (BuildEvents.Version).
    public uint Version { get; private set; }
    // Phase 17–19 review: how many times a piece left the world or changed its shape (Destroyed, Edited), counted at once.
    // Only these can take away what a resting grenade lies on (a placement or damage cannot), so Match re-checks the resting
    // grenades' support only when this moves. Only grows (Clear and Reset leave it), so a change is never missed.
    public uint StructureVersion { get; private set; }
    public int PlacedCount => _placedCount;
    public int HealthCount => _healthCount;
    public int EditedCount => _editedCount;
    public int DestroyedCount => _destroyedCount;
    public bool HasEvents => _placedCount > 0 || _editedCount > 0 || _healthCount > 0 || _destroyedCount > 0;
    // The interest grid: InterestPerSide x InterestPerSide cells of CellsPerInterest x CellsPerInterest build cells.
    public int InterestPerSide => _interestPerSide;
    // D14: sync packets a client gets per tick at most (about 4.8 kB): a big window arrives over a few ticks instead of in
    // one burst queued behind LiteNetLib's reliable window.
    public const int MaxSyncPacketsPerTick = 4;

    // Every interest cell (a dead player or a spectator watches the whole map, D14).
    public ulong AllCells => _interestPerSide * _interestPerSide >= 64 ? ulong.MaxValue : (1UL << (_interestPerSide * _interestPerSide)) - 1;

    // 기능: World 위치가 속한 관심 칸을 구한다(맵 밖이면 가장자리 칸으로 Clamp).
    // 입력: position - World 위치.
    // 출력: 관심 칸 Index(64비트 창의 비트 위치).
    public int InterestCellAt(Vector3 position)
    {
        int bx = Math.Clamp(BuildGrid.CellX(position.X), 0, BuildGrid.CellsX - 1);
        int bz = Math.Clamp(BuildGrid.CellZ(position.Z), 0, BuildGrid.CellsZ - 1);
        return InterestCell(bx, bz);
    }

    // 기능: 한 관심 칸에서 Chebyshev 거리 radius 안의 칸들을 비트 마스크로 만든다.
    // 입력: cell - 중심 관심 칸, radius - 칸 단위 반지름.
    // 출력: 범위 안 칸들의 비트 마스크(맵 밖 칸은 빠진다).
    public ulong Window(int cell, int radius)
    {
        int cx = cell % _interestPerSide;
        int cz = cell / _interestPerSide;
        ulong mask = 0;
        for (int z = Math.Max(0, cz - radius); z <= Math.Min(_interestPerSide - 1, cz + radius); z++)
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(_interestPerSide - 1, cx + radius); x++)
                mask |= 1UL << (x + _interestPerSide * z);
        return mask;
    }

    // 기능: 살아 있는 플레이어 Client가 유지할 관심 창을 구한다(D14): radius 안 칸 + 이미 갖고 있고 radius + keepMargin 안인 칸.
    // 입력: cell - 플레이어의 관심 칸, current - 지금 갖고 있는 창, radius - 기본 반지름, keepMargin - 유지 여유 칸 수.
    // 출력: 새 관심 창 비트 마스크(칸 경계를 오가도 조각을 버렸다 다시 보내지 않는다).
    // D14: the window a living player's client keeps: the cells within radius of its cell, plus those it already keeps
    // that are still within radius + keepMargin (so walking along a cell border does not drop and resend pieces).
    public ulong WindowFor(int cell, ulong current, int radius, int keepMargin) =>
        Window(cell, radius) | (current & Window(cell, radius + keepMargin));

    // 기능: Client 하나의 다음 BuildSync 패킷을 현재 조각들로 쓴다(D14). 대기 칸을 center에 가까운 순으로, 칸 안은 열·id 순으로
    //   이어 쓰며 시작한 칸은 끝까지 쓴 뒤 다음 칸으로 넘어간다. 패킷당 최대 BuildSyncPacket.MaxRecords개.
    // 입력: buffer - 쓸 곳, pending - 아직 보낼 칸 비트(끝낸 칸은 지워진다), cell - 쓰던 칸(-1이면 새로 고른다), column - 쓰던 열,
    //   afterId - 그 열에서 마지막으로 쓴 조각 id, center - Client의 관심 칸.
    // 출력: 패킷 길이. 남은 조각이 없으면 0. pending·cell·column·afterId는 이어 쓸 위치로 갱신된다.
    // D14: the next BuildSync packet for one client, from the current pieces (a piece destroyed meanwhile is simply not
    // in it): the pending cells nearest first (Chebyshev distance to center, the client's own interest cell; ties by
    // index), each cell's build columns in order, each column in id order, resuming after (cell, column, afterId). A cell
    // started is finished before the next is picked. A finished cell leaves pending. Returns the length, or 0 when nothing
    // is left; the packet holds at most BuildSyncPacket.MaxRecords pieces.
    public int NextSyncPacket(Span<byte> buffer, ref ulong pending, ref int cell, ref int column, ref uint afterId, int center)
    {
        var writer = new PacketWriter(buffer);
        BuildSyncPacket.WriteHeader(ref writer, Version, reset: false, count: 0);
        int count = 0;
        int columns = _cellsPerInterest * _cellsPerInterest;
        PieceGrid grid = _world.Grid;
        while (count < BuildSyncPacket.MaxRecords && (pending != 0 || cell >= 0))
        {
            if (cell < 0 || (pending & (1UL << cell)) == 0)
            {
                if (pending == 0)
                {
                    cell = -1;
                    break;
                }
                cell = NearestPending(pending, center);
                column = 0;
                afterId = 0;
            }
            int baseX = cell % _interestPerSide * _cellsPerInterest;
            int baseZ = cell / _interestPerSide * _cellsPerInterest;
            bool full = false;
            for (; column < columns; column++)
            {
                int x = baseX + column % _cellsPerInterest;
                int z = baseZ + column / _cellsPerInterest;
                for (int slot = grid.First(x, z); slot >= 0; slot = grid.Next(slot))
                {
                    uint id = grid.IdAt(slot);
                    if (id <= afterId) continue;
                    if (count == BuildSyncPacket.MaxRecords)
                    {
                        full = true;
                        break;
                    }
                    BuildPieceRecord.WriteSync(ref writer, Record(_world.At(slot)));
                    afterId = id;
                    count++;
                }
                if (full) break;
                afterId = 0;
            }
            if (full) break;
            pending &= ~(1UL << cell);
            cell = -1;
        }
        if (count == 0) return 0;
        buffer[BuildSyncPacket.HeaderSize - 1] = (byte)count;
        return writer.Length;
    }

    // 기능: 대기 칸 중 center에 Chebyshev 거리로 가장 가까운 칸을 고른다(같으면 Index가 낮은 칸). 최대 64칸.
    // 입력: pending - 대기 칸 비트 마스크, center - 기준 관심 칸.
    // 출력: 고른 칸 Index. pending이 비어 있으면 -1.
    public int NearestPending(ulong pending, int center)
    {
        int cx = center % _interestPerSide;
        int cz = center / _interestPerSide;
        int best = -1;
        int bestDistance = int.MaxValue;
        for (ulong rest = pending; rest != 0; rest &= rest - 1)
        {
            int c = BitOperations.TrailingZeroCount(rest);
            int distance = Math.Max(Math.Abs(c % _interestPerSide - cx), Math.Abs(c / _interestPerSide - cz));
            if (distance >= bestDistance) continue;
            best = c;
            bestDistance = distance;
        }
        return best;
    }

    // 기능: 서 있는 조각을 전송용 기록으로 바꾼다.
    // 입력: piece - 조각.
    // 출력: id·모양·재질·소유자·생성 Tick·누적 피해를 담은 BuildPieceRecord.
    public static BuildPieceRecord Record(in BuildPiece piece) => new()
    {
        Id = piece.Id, Shape = piece.Shape, Material = piece.Material, Owner = piece.Owner, CreatedTick = piece.CreatedTick, Damage = piece.Damage,
    };

    // 기능: 건설 칸 좌표가 속한 관심 칸을 구한다(D14).
    // 입력: buildX - 건설 칸 X, buildZ - 건설 칸 Z.
    // 출력: 관심 칸 Index(64비트 창의 비트 위치).
    public int InterestCell(int buildX, int buildZ) => buildX / _cellsPerInterest + _interestPerSide * (buildZ / _cellsPerInterest);

    // 기능: 조각 모양의 칸 좌표가 속한 관심 칸을 구한다.
    // 입력: shape - 조각 모양.
    // 출력: 관심 칸 Index.
    public int InterestCell(in BuildPieceShape shape) => InterestCell(shape.X, shape.Z);

    // 기능: 이번 Tick에 놓인 조각을 Placed 목록에 기록한다(플레이어당 하나, QA 배치는 목록을 상한까지 늘린다).
    // 입력: record - 놓인 조각의 기록.
    // 출력: 반환값 없음. Placed 목록이 하나 늘고 Version이 오른다(상한을 넘으면 기록하지 않는다).
    public void Placed(in BuildPieceRecord record)
    {
        // One per player per tick, plus pieces placed between ticks without a player (QA-1 spawnBuildPiece): then the list
        // grows like the others, never past the match's piece limit, so no placement goes untold.
        if (_placedCount == _placed.Length)
        {
            if (_placed.Length >= _world.Capacity) return;   // unreachable: more placements than pieces in one tick
            int length = Grown(_placed.Length, _placedCount + 1);
            Array.Resize(ref _placed, length);
            Array.Resize(ref _placedCell, length);
        }
        _placed[_placedCount] = record;
        _placedCell[_placedCount++] = (byte)InterestCell(record.Shape);
        Version++;
    }

    // 기능: 조각이 이번 Tick에 피해를 입었음을 표시한다(Health 기록은 Tick 끝 Collect에서 조각마다 한 번 나간다).
    // 입력: slot - 피해를 입은 조각의 slot.
    // 출력: 반환값 없음. 처음 표시된 slot만 대기 목록에 들어간다.
    public void Damaged(int slot)
    {
        if (slot >= _damagedFlag.Length) Array.Resize(ref _damagedFlag, Grown(_damagedFlag.Length, slot + 1));
        if (_damagedFlag[slot]) return;
        _damagedFlag[slot] = true;
        if (_damagedCount == _damagedSlots.Length) Array.Resize(ref _damagedSlots, Grown(_damagedSlots.Length, _damagedCount + 1));
        _damagedSlots[_damagedCount++] = slot;
    }

    // 기능: 조각이 이번 Tick에 편집되었음을 기록한다(Phase 13.5 D8). Edited 기록은 Tick 끝에 조각마다 한 번, 그때 상태로 간다.
    //   모양이 바뀌었으므로 StructureVersion을 바로 올린다(Phase 17–19 리뷰: 멈춘 수류탄 받침 재확인).
    // 입력: slot - 편집된 조각의 slot.
    // 출력: 반환값 없음.
    public void Edited(int slot)
    {
        StructureVersion++;
        if (slot >= _editedFlag.Length) Array.Resize(ref _editedFlag, Grown(_editedFlag.Length, slot + 1));
        if (_editedFlag[slot]) return;
        _editedFlag[slot] = true;
        if (_editedSlotCount == _editedSlots.Length) Array.Resize(ref _editedSlots, Grown(_editedSlots.Length, _editedSlotCount + 1));
        _editedSlots[_editedSlotCount++] = slot;
    }

    // 기능: 목록의 새 길이를 정한다(두 배 또는 need 중 큰 쪽, 경기 조각 상한을 넘지 않는다).
    // 입력: length - 지금 길이, need - 최소로 필요한 길이.
    // 출력: 새 길이.
    private int Grown(int length, int need) => Math.Min(_world.Capacity, Math.Max(need, length * 2));

    // 기능: 조각이 이번 Tick에 World를 떠났음을 기록한다(파괴 또는 붕괴). 그 slot의 대기 중인 Health·Edited 기록은 버린다
    //   (같은 Tick에 slot이 새 조각에 다시 쓰여도 옛 조각의 기록이 새 조각에 붙지 않게).
    // 입력: slot - 조각의 slot, id - 조각 id, shape - 관심 칸을 정할 모양, reason - 피해로 부서졌는지(Destroyed)
    //   지지를 잃고 무너졌는지(Collapsed, Phase 18 D6).
    // 출력: 반환값 없음. Destroyed 기록이 하나 늘고 StructureVersion이 오른다.
    public void Destroyed(int slot, uint id, in BuildPieceShape shape, BuildDestroyReason reason)
    {
        StructureVersion++;
        Unflag(_damagedFlag, _damagedSlots, ref _damagedCount, slot);
        Unflag(_editedFlag, _editedSlots, ref _editedSlotCount, slot);
        if (_destroyedCount == _destroyed.Length)
        {
            if (_destroyed.Length == _world.Capacity) return;   // unreachable: at most every piece once per tick
            int size = Grown(_destroyed.Length, _destroyedCount + 1);
            Array.Resize(ref _destroyed, size);
            Array.Resize(ref _destroyedCell, size);
            Array.Resize(ref _destroyedReason, size);
        }
        _destroyed[_destroyedCount] = id;
        _destroyedReason[_destroyedCount] = reason;
        _destroyedCell[_destroyedCount++] = (byte)InterestCell(shape);
        Version++;
    }

    // 기능: slot의 표시를 지우고 대기 목록에서 뺀다(순서는 지키지 않는다).
    // 입력: flags - slot별 표시, slots - 대기 목록, count - 목록 길이, slot - 지울 slot.
    // 출력: 반환값 없음.
    private static void Unflag(bool[] flags, int[] slots, ref int count, int slot)
    {
        if (slot >= flags.Length || !flags[slot]) return;
        flags[slot] = false;
        for (int i = 0; i < count; i++)
        {
            if (slots[i] != slot) continue;
            slots[i] = slots[--count];
            break;
        }
    }

    // 기능: Tick 끝에 대기 중인 Edited·Health 기록을 지금 상태로 만든다(아직 서 있는 조각만).
    // 입력: 없음.
    // 출력: 반환값 없음. Edited·Health 목록이 채워지고 대기 표시가 지워지며 기록마다 Version이 오른다.
    public void Collect()
    {
        if (_editedSlotCount > _editedIds.Length)
        {
            int size = Grown(_editedIds.Length, _editedSlotCount);
            Array.Resize(ref _editedIds, size);
            Array.Resize(ref _editedStates, size);
            Array.Resize(ref _editedCell, size);
        }
        for (int i = 0; i < _editedSlotCount; i++)
        {
            int slot = _editedSlots[i];
            _editedFlag[slot] = false;
            ref BuildPiece edited = ref _world.At(slot);
            if (edited.Id == 0) continue;
            _editedIds[_editedCount] = edited.Id;
            _editedStates[_editedCount] = BuildEdit.StateOf(edited.Shape);
            _editedCell[_editedCount++] = (byte)InterestCell(edited.Shape);
            Version++;
        }
        _editedSlotCount = 0;
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
        public int Edited;
        public int Health;
        public int Destroyed;
    }

    // 기능: cursor부터 다음 BuildEvents 패킷을 쓴다. 관심 칸이 cells에 든 이벤트만, Placed → Edited → Health → Destroyed 순서로.
    // 입력: buffer - 쓸 곳(최대 길이), cursor - 종류별 다음 위치(갱신된다), cells - 받는 Client의 관심 창.
    // 출력: 패킷 길이. 이 칸들에 남은 이벤트가 없으면 0.
    public int NextPacket(Span<byte> buffer, ref Cursor cursor, ulong cells)
    {
        var writer = new PacketWriter(buffer);
        BuildEventsPacket.WriteHeader(ref writer, Version);
        int placed = 0;
        int edited = 0;
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
            for (; cursor.Edited < _editedCount && edited < 255; cursor.Edited++)
            {
                if ((cells & (1UL << _editedCell[cursor.Edited])) == 0) continue;
                if (room < BuildEventsPacket.EditedSize) break;
                BuildEventsPacket.WriteEdited(ref writer, _editedIds[cursor.Edited], _editedStates[cursor.Edited]);
                room -= BuildEventsPacket.EditedSize;
                edited++;
            }
        }
        if (cursor.Placed == _placedCount && cursor.Edited == _editedCount)
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
        if (cursor.Placed == _placedCount && cursor.Edited == _editedCount && cursor.Health == _healthCount)
        {
            for (; cursor.Destroyed < _destroyedCount && destroyed < 255; cursor.Destroyed++)
            {
                if ((cells & (1UL << _destroyedCell[cursor.Destroyed])) == 0) continue;
                if (room < BuildEventsPacket.DestroyedSize) break;
                BuildEventsPacket.WriteDestroyed(ref writer, _destroyed[cursor.Destroyed], _destroyedReason[cursor.Destroyed]);
                room -= BuildEventsPacket.DestroyedSize;
                destroyed++;
            }
        }
        if (placed + edited + health + destroyed == 0) return 0;
        BuildEventsPacket.Patch(buffer, placed, edited, health, destroyed);
        return writer.Length;
    }

    // 기능: 모든 수신자에게 패킷을 보낸 뒤 이번 Tick의 Placed·Edited·Health·Destroyed 목록을 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 네 목록의 개수가 0이 된다(Version·StructureVersion은 그대로).
    public void Clear()
    {
        _placedCount = 0;
        _editedCount = 0;
        _healthCount = 0;
        _destroyedCount = 0;
    }

    // 기능: 라운드 Reset: 대기 중인 피해·편집 표시를 지우고 이벤트 목록을 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 대기 표시와 네 목록이 비워진다(Version·StructureVersion은 그대로).
    public void Reset()
    {
        for (int i = 0; i < _damagedCount; i++) _damagedFlag[_damagedSlots[i]] = false;
        _damagedCount = 0;
        for (int i = 0; i < _editedSlotCount; i++) _editedFlag[_editedSlots[i]] = false;
        _editedSlotCount = 0;
        Clear();
    }
}
