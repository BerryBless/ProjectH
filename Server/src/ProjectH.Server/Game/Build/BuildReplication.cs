using System;
using System.Numerics;
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

    // 기능: 한 Tick의 건설 이벤트 버퍼와 관심 격자 크기를 준비한다.
    // 입력: world - 현재 Match의 건설 조각 목록, catalog - 관심 칸 크기를 담은 건설 카탈로그, maxPlayers - Match 최대 인원(Tick당 배치 이벤트 상한).
    // 출력: 이벤트가 없고 Version이 0인 BuildReplication.
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
    // D14: sync packets a client gets per tick at most (about 4.8 kB): a big window arrives over a few ticks instead of in
    // one burst queued behind LiteNetLib's reliable window.
    public const int MaxSyncPacketsPerTick = 4;

    // 기능: 맵 전체 관심 칸을 덮는 비트 마스크를 만든다.
    // 입력: 없음.
    // 출력: 모든 관심 칸 비트가 켜진 마스크(64칸 이상이면 전체 비트).
    // Every interest cell (a dead player or a spectator watches the whole map, D14).
    public ulong AllCells => _interestPerSide * _interestPerSide >= 64 ? ulong.MaxValue : (1UL << (_interestPerSide * _interestPerSide)) - 1;

    // 기능: 월드 위치가 속한 관심 칸을 구한다.
    // 입력: position - 월드 위치.
    // 출력: 관심 칸 번호(맵 밖 위치는 가장자리 칸으로 맞춘다).
    // The interest cell a world position lies in (clamped to the map).
    public int InterestCellAt(Vector3 position)
    {
        int bx = Math.Clamp(BuildGrid.CellX(position.X), 0, BuildGrid.CellsX - 1);
        int bz = Math.Clamp(BuildGrid.CellZ(position.Z), 0, BuildGrid.CellsZ - 1);
        return InterestCell(bx, bz);
    }

    // 기능: 한 관심 칸에서 Chebyshev 거리 radius 안의 관심 칸들을 마스크로 만든다.
    // 입력: cell - 중심 관심 칸 번호, radius - 칸 단위 반경.
    // 출력: 반경 안 관심 칸 비트가 켜진 마스크(맵 밖은 제외).
    // The cells within radius (Chebyshev) of a cell.
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

    // 기능: 살아 있는 플레이어 Client가 유지할 관심 창을 계산한다.
    // 입력: cell - 플레이어가 있는 관심 칸, current - 지금 유지 중인 관심 창, radius - 창 반경, keepMargin - 기존 칸을 더 유지할 여유 칸 수.
    // 출력: 반경 안 칸과, 기존 창 중 radius + keepMargin 안에 남은 칸을 합친 마스크.
    // D14: the window a living player's client keeps: the cells within radius of its cell, plus those it already keeps
    // that are still within radius + keepMargin (so walking along a cell border does not drop and resend pieces).
    public ulong WindowFor(int cell, ulong current, int radius, int keepMargin) =>
        Window(cell, radius) | (current & Window(cell, radius + keepMargin));

    // 기능: 한 Client에게 보낼 다음 BuildSync Packet을 현재 조각들로 만든다.
    // 입력: buffer - Packet을 쓸 버퍼, pending - 아직 보내지 않은 관심 칸 마스크, cell·column·afterId - 이어서 쓸 위치(칸, 칸 안 열, 마지막으로 보낸 id), center - Client가 있는 관심 칸.
    // 출력: 쓴 Packet 길이, 보낼 조각이 없으면 0. pending·cell·column·afterId가 다음 호출 위치로 갱신된다.
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

    // 기능: 아직 보내지 않은 관심 칸 중 중심에 가장 가까운 칸을 고른다.
    // 입력: pending - 보낼 관심 칸 마스크, center - 기준 관심 칸.
    // 출력: 가장 가까운 칸 번호(거리가 같으면 번호가 작은 칸), pending이 비었으면 -1.
    // The pending cell nearest to center (Chebyshev, in interest cells), the lowest index among equals. At most 64 cells.
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

    // 기능: 서버 조각 상태를 Client에 보낼 조각 레코드로 옮긴다.
    // 입력: piece - 서버가 가진 조각.
    // 출력: id·모양·재료·소유자·생성 Tick·피해를 담은 BuildPieceRecord(접지 여부는 보내지 않음).
    public static BuildPieceRecord Record(in BuildPiece piece) => new()
    {
        Id = piece.Id, Shape = piece.Shape, Material = piece.Material, Owner = piece.Owner, CreatedTick = piece.CreatedTick, Damage = piece.Damage,
    };

    // 기능: 건설 칸이 속한 관심 칸을 구한다.
    // 입력: buildX, buildZ - 건설 칸 좌표.
    // 출력: 관심 칸 번호(64비트 관심 창의 비트 위치).
    // D14: the interest cell (bit index in a 64-bit window) of a build cell.
    public int InterestCell(int buildX, int buildZ) => buildX / _cellsPerInterest + _interestPerSide * (buildZ / _cellsPerInterest);

    // 기능: 조각이 놓인 건설 칸의 관심 칸을 구한다.
    // 입력: shape - 조각 모양(건설 칸 X, Z 사용).
    // 출력: 관심 칸 번호(64비트 관심 창의 비트 위치).
    public int InterestCell(in BuildPieceShape shape) => InterestCell(shape.X, shape.Z);

    // 기능: 이번 Tick에 놓인 조각을 배치 이벤트로 기록한다.
    // 입력: record - 새로 놓인 조각의 레코드.
    // 출력: 반환값 없음. 배치 이벤트가 추가되고 Version이 1 오른다.
    public void Placed(in BuildPieceRecord record)
    {
        if (_placedCount == _placed.Length) return;   // unreachable: one per player per tick
        _placed[_placedCount] = record;
        _placedCell[_placedCount++] = (byte)InterestCell(record.Shape);
        Version++;
    }

    // 기능: 이번 Tick에 피해를 입은 조각을 한 번만 표시해 둔다.
    // 입력: slot - 피해를 입은 조각의 저장 슬롯 번호.
    // 출력: 반환값 없음. 처음 표시되는 슬롯이면 피해 목록에 추가된다.
    // A piece took damage this tick (its Health record goes out at the end of the tick, once).
    public void Damaged(int slot)
    {
        if (slot >= _damagedFlag.Length) Array.Resize(ref _damagedFlag, Grown(_damagedFlag.Length, slot + 1));
        if (_damagedFlag[slot]) return;
        _damagedFlag[slot] = true;
        if (_damagedCount == _damagedSlots.Length) Array.Resize(ref _damagedSlots, Grown(_damagedSlots.Length, _damagedCount + 1));
        _damagedSlots[_damagedCount++] = slot;
    }

    // 기능: 이벤트 목록 배열을 늘릴 새 크기를 정한다.
    // 입력: length - 현재 배열 길이, need - 최소로 필요한 길이.
    // 출력: 두 배와 need 중 큰 값을 Match 최대 조각 수로 제한한 크기.
    // Doubling, at least to need, never past the match's piece limit (no list holds more than every piece).
    private int Grown(int length, int need) => Math.Min(_world.Capacity, Math.Max(need, length * 2));

    // 기능: 이번 Tick에 사라진 조각(파괴·붕괴)을 파괴 이벤트로 기록한다.
    // 입력: slot - 사라진 조각의 저장 슬롯 번호, id - 그 조각 id, shape - 관심 칸을 구할 조각 모양.
    // 출력: 반환값 없음. 그 조각의 대기 중인 피해 표시가 지워지고 파괴 이벤트가 추가되며 Version이 1 오른다.
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

    // 기능: Tick 끝에 피해 표시된 조각들의 현재 피해로 체력 이벤트를 만든다.
    // 입력: 없음.
    // 출력: 반환값 없음. 남아 있는 조각마다 체력 이벤트가 추가되고(Version 증가) 피해 목록이 비워진다.
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

    // 기능: 수신자의 관심 칸에 속한 이벤트로 다음 BuildEvents Packet을 만든다(배치, 체력, 파괴 순).
    // 입력: buffer - Packet을 쓸 버퍼, cursor - 이 수신자에게 이어서 쓸 이벤트 위치, cells - 수신자의 관심 칸 마스크.
    // 출력: 쓴 Packet 길이, 더 보낼 이벤트가 없으면 0. cursor가 다음 위치로 갱신된다.
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

    // 기능: 모든 수신자에게 보낸 뒤 이번 Tick의 이벤트를 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 배치·체력·파괴 이벤트 수가 0이 된다(Version은 유지).
    // After every recipient got its packets.
    public void Clear()
    {
        _placedCount = 0;
        _healthCount = 0;
        _destroyedCount = 0;
    }

    // 기능: 라운드 리셋 때 대기 중인 피해 표시와 이벤트를 모두 버린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 피해 목록과 이벤트가 비워진다(Version은 유지).
    // A round reset: nothing pending survives, and the damaged flags start clean.
    public void Reset()
    {
        for (int i = 0; i < _damagedCount; i++) _damagedFlag[_damagedSlots[i]] = false;
        _damagedCount = 0;
        Clear();
    }
}
