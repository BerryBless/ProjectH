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
    // Pieces also go into a PieceGrid that client prediction collides with (only these confirmed pieces, never predicted
    // placements). Changed ids are listed for the views (each at most once until taken). Bounded by MaxPieces.
    // Phase 13.5 D11: an edit the player confirmed is laid over its confirmed piece at once (an EditPrediction, at most
    // MaxPredictions, one per piece). TryGet, the views and the Grid (so the predicted movement too: walking through a door
    // just opened) see the overlaid shape; TryGetConfirmed sees the server's. An overlay goes when its request is refused
    // (rollback), when the server's state for the piece arrives after an Ok (Edited, Placed or Sync) or already equals it,
    // after PredictionTimeoutSeconds, and with its piece (destroyed, out of the window, reset). This deliberately extends
    // game-core-rules §4's "confirmed pieces only" to edits of confirmed pieces (Spec D11); predicted placements still
    // never collide. Main thread only; no UnityEngine (the server tests compile this file by source link).
    public sealed class BuildStore
    {
        // The server's default piece limit per match (building.json maxPiecesPerMatch). Fixed, not taken from the catalog:
        // the collision grid is sized once. A window can hold at most the match's pieces, so with the shipped data this is
        // never reached; a server set higher would have pieces past it ignored (counted in Ignored), never stored.
        public const int MaxPieces = 20000;
        public const int CellsPerSide = 8;    // 20 m interest cells (the shipped building.json)
        // D11: the server's request queue holds 8; an overlay lives at most PredictionTimeoutSeconds (BuildController's rule).
        public const int MaxPredictions = 8;
        public const float PredictionTimeoutSeconds = 1f;

        private readonly Dictionary<uint, BuildPieceRecord> _pieces = new Dictionary<uint, BuildPieceRecord>();
        private readonly List<uint> _changed = new List<uint>();
        private readonly HashSet<uint> _changedSet = new HashSet<uint>();
        private readonly List<uint> _scratch = new List<uint>();
        // D11: the edit overlays, packed at the front (order does not matter). Bounded: MaxPredictions.
        private readonly EditPrediction[] _predictions = new EditPrediction[MaxPredictions];
        private int _predictionCount;

        public PieceGrid Grid { get; } = new PieceGrid(MaxPieces);
        public ulong Cells { get; private set; }
        public uint Version { get; private set; }
        public int Count => _pieces.Count;
        // Events or syncs that did nothing because the piece was unknown or outside the window (debug).
        public int Ignored { get; private set; }
        // Interest cell size in build cells (from the BuildCatalog: 20 m = 4).
        public int CellsPerInterest { get; set; } = 4;
        public int PredictionCount => _predictionCount;

        // 기능: 화면에 보일 조각 기록을 낸다(Phase 13.5 D11: 편집 예측이 있으면 그 모양을 덧씌운다).
        // 입력: id - 조각 id, piece - 결과.
        // 출력: 저장된 조각이면 true와 기록(모양은 예측 모양일 수 있다), 없으면 false.
        public bool TryGet(uint id, out BuildPieceRecord piece)
        {
            if (!_pieces.TryGetValue(id, out piece)) return false;
            int p = FindPrediction(id);
            if (p >= 0) piece.Shape = _predictions[p].Shape;
            return true;
        }

        // 기능: 서버가 확정한 조각 기록을 낸다(예측을 덧씌우지 않는다).
        // 입력: id - 조각 id, piece - 결과.
        // 출력: 저장된 조각이면 true와 확정 기록, 없으면 false.
        public bool TryGetConfirmed(uint id, out BuildPieceRecord piece) => _pieces.TryGetValue(id, out piece);

        // 기능: 조각에 편집 예측이 걸려 있는지 본다(디버그·테스트).
        // 입력: id - 조각 id.
        // 출력: 예측이 있으면 true.
        public bool IsPredicted(uint id) => FindPrediction(id) >= 0;

        public IReadOnlyList<uint> Changed => _changed;

        public void ClearChanged()
        {
            _changed.Clear();
            _changedSet.Clear();
        }

        // The interest cell (bit index) a piece lies in.
        public int CellOf(in BuildPieceShape shape)
        {
            int per = CellsPerInterest < 1 ? 1 : CellsPerInterest;
            int side = BuildGrid.CellsX / per;
            return shape.X / per + side * (shape.Z / per);
        }

        // 기능: Reset Sync(참가·재개·라운드)나 새 연결: 모든 조각과 편집 예측, 디버그 수치를 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 지운 조각들이 변경 목록에 오른다.
        public void Reset()
        {
            foreach (uint id in _pieces.Keys) MarkChanged(id);
            _pieces.Clear();
            _predictionCount = 0;
            Grid.Clear();
            Cells = 0;
            Version = 0;
            Ignored = 0;
        }

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

        // 기능: Placed 또는 Sync로 온 조각 하나를 넣거나 갱신한다(Phase 13.5: 기록의 편집 상태 포함).
        // 입력: piece - 조각 기록, version - 건설 스트림 버전.
        // 출력: 반환값 없음. 조각·Grid·변경 목록이 바뀐다. 이미 아는 id면 그 자리에서 갱신하고, Ok를 받은 편집 예측이나
        //   이 상태와 같은 예측은 지운다(서버 상태가 왔다).
        public void ApplyPiece(in BuildPieceRecord piece, uint version)
        {
            if (version > Version) Version = version;
            if ((Cells & (1UL << CellOf(piece.Shape))) == 0)
            {
                Ignored++;
                return;
            }
            if (_pieces.ContainsKey(piece.Id))
            {
                _pieces[piece.Id] = piece;
                SettlePrediction(piece.Id, piece.Shape);
                ShowShape(piece.Id);
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

        // 기능: Edited 기록(Phase 13.5 D8)을 확정 조각에 적용한다. Id·소유자·재료·건설 시작·피해는 그대로이고 모양만 바뀐다.
        // 입력: id - 조각 id, state - 편집 뒤 상태(BuildEdit.PackState), version - 건설 스트림 버전.
        // 출력: 적용했거나 모르는 id라 무시했으면 true. 그 조각에 맞지 않는 상태(BuildEdit.TryApply 실패: 잘못된 기록)면
        //   false이고 모양은 바뀌지 않는다. 둘 다 무시한 경우 Ignored가 는다.
        public bool ApplyEdited(uint id, ushort state, uint version)
        {
            if (version > Version) Version = version;
            if (!_pieces.TryGetValue(id, out BuildPieceRecord piece))
            {
                Ignored++;
                return true;
            }
            if (!BuildEdit.TryApply(piece.Shape, state, out BuildPieceShape edited))
            {
                Ignored++;
                return false;
            }
            piece.Shape = edited;
            _pieces[id] = piece;
            SettlePrediction(id, edited);
            ShowShape(id);
            return true;
        }

        public void ApplyDestroyed(uint id, uint version)
        {
            if (version > Version) Version = version;
            if (!Remove(id)) Ignored++;
        }

        // 기능: 확정한 편집을 예측으로 바로 덧씌운다(D11). 같은 조각의 이전 예측은 새 것으로 바뀐다.
        // 입력: id - 조각 id, state - 보낸 상태(BuildEdit.PackState), sequence - 보낸 요청의 순번, now - 보낸 시각(초).
        // 출력: 덧씌웠으면 true. 모르는 조각, 그 조각에 맞지 않는 상태, 예측이 이미 MaxPredictions개면 false(아무것도 바뀌지 않는다).
        public bool PredictEdit(uint id, ushort state, ushort sequence, float now)
        {
            if (!_pieces.TryGetValue(id, out BuildPieceRecord piece)) return false;
            if (!BuildEdit.TryApply(piece.Shape, state, out BuildPieceShape shape)) return false;
            int p = FindPrediction(id);
            if (p < 0)
            {
                if (_predictionCount >= MaxPredictions) return false;
                p = _predictionCount++;
            }
            _predictions[p] = new EditPrediction { Id = id, Sequence = sequence, Shape = shape, SentAt = now, Accepted = false };
            ShowShape(id);
            return true;
        }

        // 기능: 편집 요청의 BuildResult를 그 순번의 예측에 반영한다. 거절이면 롤백하고, Ok면 서버 상태를 기다린다(이미 같으면 바로 지운다).
        // 입력: result - 서버 결과(순번으로 예측을 찾는다).
        // 출력: 그 순번의 예측이 있었으면 true. 없으면(시간 초과로 지웠거나 같은 조각의 새 편집이 덮었으면) false이고 아무것도 바뀌지 않는다.
        public bool OnEditResult(in BuildResult result)
        {
            for (int p = 0; p < _predictionCount; p++)
            {
                if (_predictions[p].Sequence != result.Sequence) continue;
                uint id = _predictions[p].Id;
                if (result.Code == BuildResultCode.Ok)
                {
                    // Already the server's state (an Ok for no change, or its Edited came first): nothing left to wait for.
                    if (_pieces.TryGetValue(id, out BuildPieceRecord piece) && piece.Shape.Equals(_predictions[p].Shape)) RemovePredictionAt(p);
                    else _predictions[p].Accepted = true;
                }
                else
                {
                    RemovePredictionAt(p);
                }
                ShowShape(id);
                return true;
            }
            return false;
        }

        // 기능: 보낸 지 PredictionTimeoutSeconds가 지난 예측을 지운다(답이나 서버 상태가 오지 않은 경우).
        // 입력: now - 현재 시각(초).
        // 출력: 반환값 없음. 지운 조각은 확정 모양으로 돌아간다.
        public void ExpirePredictions(float now)
        {
            for (int p = _predictionCount - 1; p >= 0; p--)
            {
                if (now - _predictions[p].SentAt < PredictionTimeoutSeconds) continue;
                uint id = _predictions[p].Id;
                RemovePredictionAt(p);
                ShowShape(id);
            }
        }

        // 기능: 조각 하나를 저장소·Grid·편집 예측에서 지운다.
        // 입력: id - 조각 id.
        // 출력: 있었으면 true. 변경 목록에 오른다.
        private bool Remove(uint id)
        {
            if (!_pieces.Remove(id)) return false;
            Grid.Remove(id);
            int p = FindPrediction(id);
            if (p >= 0) RemovePredictionAt(p);
            MarkChanged(id);
            return true;
        }

        private void MarkChanged(uint id)
        {
            if (_changedSet.Add(id)) _changed.Add(id);
        }

        // 기능: 서버 상태가 온 조각의 예측을 정리한다. Ok를 받은 예측, 서버 상태와 같은 예측, 자리·종류가 달라진 예측은 지운다.
        //   답이 아직 없는 다른 상태의 예측은 남긴다(그 요청이 처리되기 전의 상태일 수 있다).
        // 입력: id - 조각 id, confirmed - 서버가 확정한 모양.
        // 출력: 반환값 없음.
        private void SettlePrediction(uint id, in BuildPieceShape confirmed)
        {
            int p = FindPrediction(id);
            if (p < 0) return;
            BuildPieceShape predicted = _predictions[p].Shape;
            if (_predictions[p].Accepted || predicted.Equals(confirmed) || predicted.Type != confirmed.Type ||
                BuildGrid.SlotKey(predicted) != BuildGrid.SlotKey(confirmed))
                RemovePredictionAt(p);
        }

        // 기능: 보이는 모양(예측이 있으면 예측, 아니면 확정)을 Grid에 맞추고, 바뀌었으면 변경 목록에 올린다.
        // 입력: id - 저장된 조각 id(없으면 아무것도 하지 않는다).
        // 출력: 반환값 없음. 같은 슬롯이면 SetShape로 slot을 유지하고, 다르면 지우고 다시 넣는다.
        private void ShowShape(uint id)
        {
            if (!TryGet(id, out BuildPieceRecord shown)) return;
            if (Grid.TryGet(id, out BuildPieceShape current))
            {
                if (current.Equals(shown.Shape)) return;
                if (!Grid.SetShape(id, shown.Shape))
                {
                    Grid.Remove(id);
                    Grid.TryAdd(id, shown.Shape, out _);
                }
            }
            else
            {
                Grid.TryAdd(id, shown.Shape, out _);
            }
            MarkChanged(id);
        }

        // 기능: 조각의 편집 예측 위치를 찾는다(최대 MaxPredictions개를 차례로 본다).
        // 입력: id - 조각 id.
        // 출력: 예측 배열의 위치, 없으면 -1.
        private int FindPrediction(uint id)
        {
            for (int p = 0; p < _predictionCount; p++)
            {
                if (_predictions[p].Id == id) return p;
            }
            return -1;
        }

        // 기능: 예측 하나를 지운다(마지막 것을 그 자리로 옮긴다: 순서는 의미가 없다).
        // 입력: p - 예측 배열의 위치.
        // 출력: 반환값 없음. 예측 수가 하나 준다.
        private void RemovePredictionAt(int p) => _predictions[p] = _predictions[--_predictionCount];

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

        // Phase 13.5 D11: one predicted edit laid over a confirmed piece.
        private struct EditPrediction
        {
            public uint Id;
            public ushort Sequence;
            public BuildPieceShape Shape;   // the confirmed shape with the sent state applied (BuildEdit.TryApply)
            public float SentAt;
            public bool Accepted;           // its BuildResult was Ok: goes with the server's next state for the piece
        }
    }
}
