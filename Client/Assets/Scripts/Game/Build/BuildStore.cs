using System.Collections.Generic;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 13 D13, D14 (request §93-§97, §150-§152): the pieces the server has confirmed in this client's interest window.
    // The building stream arrives in order on its own channel; every message is applied by id, so a repeat changes
    // nothing:
    //  - a placed (or synced) piece is added, or updated when its id is known;
    //  - health and destroyed for an unknown id are ignored (a piece of a cell not synced yet, or already gone);
    //  - a piece whose cell is outside the window (BuildInterest) is never kept, so a late event cannot bring it back;
    //  - BuildInterest drops every piece outside the new window; a reset sync drops everything (join, resume, round).
    // Pieces also go into a PieceGrid that client prediction collides with (only these confirmed pieces, never the
    // predicted ones). Changed ids are listed for the views (each at most once until taken). Bounded by MaxPieces.
    // Main thread only; no UnityEngine.
    public sealed class BuildStore
    {
        // The server's default piece limit per match (building.json maxPiecesPerMatch). Fixed, not taken from the catalog:
        // the collision grid is sized once. A window can hold at most the match's pieces, so with the shipped data this is
        // never reached; a server set higher would have pieces past it ignored (counted in Ignored), never stored.
        public const int MaxPieces = 20000;
        public const int CellsPerSide = 8;    // 20 m interest cells (the shipped building.json)

        private readonly Dictionary<uint, BuildPieceRecord> _pieces = new Dictionary<uint, BuildPieceRecord>();
        private readonly List<uint> _changed = new List<uint>();
        private readonly HashSet<uint> _changedSet = new HashSet<uint>();
        private readonly List<uint> _scratch = new List<uint>();

        public PieceGrid Grid { get; } = new PieceGrid(MaxPieces);
        public ulong Cells { get; private set; }
        public uint Version { get; private set; }
        public int Count => _pieces.Count;
        // Events or syncs that did nothing because the piece was unknown or outside the window (debug).
        public int Ignored { get; private set; }
        // Interest cell size in build cells (from the BuildCatalog: 20 m = 4).
        public int CellsPerInterest { get; set; } = 4;

        // 기능: ID로 확정 조각을 찾는다.
        // 입력: id - 조각 ID.
        // 출력: 있으면 true와 조각 기록, 없으면 false.
        public bool TryGet(uint id, out BuildPieceRecord piece) => _pieces.TryGetValue(id, out piece);

        public IReadOnlyList<uint> Changed => _changed;

        // 기능: View가 반영한 변경 ID 목록을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Changed가 빈다.
        public void ClearChanged()
        {
            _changed.Clear();
            _changedSet.Clear();
        }

        // 기능: 조각이 속한 관심 셀(Bit Index)을 계산한다.
        // 입력: shape - 조각 모양.
        // 출력: 관심 셀 Bit Index.
        // The interest cell (bit index) a piece lies in.
        public int CellOf(in BuildPieceShape shape)
        {
            int per = CellsPerInterest < 1 ? 1 : CellsPerInterest;
            int side = BuildGrid.CellsX / per;
            return shape.X / per + side * (shape.Z / per);
        }

        // 기능: 모든 조각, Grid, 관심 셀, 버전, 무시 수를 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 저장소가 비고, 지운 조각 ID는 Changed에 들어간다.
        // A reset sync (join, resume, round) or a new connection: everything goes, the debug counts too.
        public void Reset()
        {
            foreach (uint id in _pieces.Keys) MarkChanged(id);
            _pieces.Clear();
            Grid.Clear();
            Cells = 0;
            Version = 0;
            Ignored = 0;
        }

        // 기능: 새 관심 셀 집합을 적용하고 그 밖의 조각을 지운다.
        // 입력: cells - 관심 셀 Bit Mask.
        // 출력: 반환값 없음. Cells가 바뀌고 지운 조각은 Changed에 들어간다.
        public void ApplyInterest(ulong cells)
        {
            Cells = cells;
            _scratch.Clear();
            foreach (KeyValuePair<uint, BuildPieceRecord> kv in _pieces)
            {
                if ((cells & (1UL << CellOf(kv.Value.Shape))) == 0) _scratch.Add(kv.Key);
            }
            for (int i = 0; i < _scratch.Count; i++) Remove(_scratch[i]);
        }

        // 기능: 배치·동기화된 조각을 ID 기준으로 추가하거나 갱신한다.
        // 입력: piece - 조각 기록, version - 메시지의 건설 버전.
        // 출력: 반환값 없음. Version은 큰 값으로 갱신되며, 관심 셀 밖이거나 상한·Grid 추가에 실패하면 Ignored만 늘고, 아니면 조각·Grid·Changed가 갱신된다.
        // A placed or synced piece.
        public void ApplyPiece(in BuildPieceRecord piece, uint version)
        {
            if (version > Version) Version = version;
            if ((Cells & (1UL << CellOf(piece.Shape))) == 0)
            {
                Ignored++;
                return;
            }
            if (_pieces.TryGetValue(piece.Id, out BuildPieceRecord known))
            {
                if (!known.Shape.Equals(piece.Shape))
                {
                    Grid.Remove(piece.Id);
                    Grid.TryAdd(piece.Id, piece.Shape, out _);
                }
                _pieces[piece.Id] = piece;
                MarkChanged(piece.Id);
                return;
            }
            if (_pieces.Count >= MaxPieces || !Grid.TryAdd(piece.Id, piece.Shape, out _))
            {
                Ignored++;
                return;
            }
            _pieces.Add(piece.Id, piece);
            MarkChanged(piece.Id);
        }

        // 기능: 알려진 조각의 피해량을 갱신한다.
        // 입력: id - 조각 ID, damage - Server의 누적 피해량, version - 메시지의 건설 버전.
        // 출력: 반환값 없음. 조각의 Damage와 Changed가 갱신되고, 모르는 ID면 Ignored가 는다.
        public void ApplyHealth(uint id, ushort damage, uint version)
        {
            if (version > Version) Version = version;
            if (!_pieces.TryGetValue(id, out BuildPieceRecord piece))
            {
                Ignored++;
                return;
            }
            piece.Damage = damage;
            _pieces[id] = piece;
            MarkChanged(id);
        }

        // 기능: 파괴된 조각을 지운다.
        // 입력: id - 조각 ID, version - 메시지의 건설 버전.
        // 출력: 반환값 없음. 조각이 지워지고, 모르는 ID면 Ignored가 는다.
        public void ApplyDestroyed(uint id, uint version)
        {
            if (version > Version) Version = version;
            if (!Remove(id)) Ignored++;
        }

        // 기능: 조각을 저장소와 Grid에서 지운다.
        // 입력: id - 조각 ID.
        // 출력: 지웠으면 true, 없던 ID면 false.
        private bool Remove(uint id)
        {
            if (!_pieces.Remove(id)) return false;
            Grid.Remove(id);
            MarkChanged(id);
            return true;
        }

        // 기능: ID를 변경 목록에 한 번만 넣는다.
        // 입력: id - 조각 ID.
        // 출력: 반환값 없음. 처음이면 Changed에 추가된다.
        private void MarkChanged(uint id)
        {
            if (_changedSet.Add(id)) _changed.Add(id);
        }

        // 기능: Server BuildRules.Occupied와 같은 기준으로 슬롯이 확정 조각으로 차 있는지 확인한다.
        // 입력: shape - 확인할 조각 모양.
        // 출력: 같은 슬롯이 차 있거나 바닥·지붕이 같은 판을 공유하면 true, 아니면 false.
        // D1: the slot is taken (or shares the slab of a floor and the roof below it), as the server's BuildRules.Occupied.
        // Only the pieces of that cell's column are looked at (at most 16 levels x 5 slots).
        public bool Occupied(in BuildPieceShape shape)
        {
            uint key = BuildGrid.SlotKey(shape);
            for (int slot = Grid.First(shape.X, shape.Z); slot >= 0; slot = Grid.Next(slot))
            {
                BuildPieceShape s = Grid.ShapeAt(slot);
                if (BuildGrid.SlotKey(s) == key) return true;
                if (shape.Type == BuildPieceType.Floor && s.Type == BuildPieceType.Roof && s.X == shape.X && s.Z == shape.Z && s.Y + 1 == shape.Y) return true;
                if (shape.Type == BuildPieceType.Roof && s.Type == BuildPieceType.Floor && s.X == shape.X && s.Z == shape.Z && s.Y == shape.Y + 1) return true;
            }
            return false;
        }
    }
}
