using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 13.5 D10, D11 (request §13-§23, §29): edit mode's local side. H on one of our own pieces in reach starts it;
    // the piece's tile grid is shown (BuildEditOverlay) and the crosshair picks a tile where the aim ray meets the piece's
    // face; a left click toggles it and dragging paints the same value. The selection becomes the preview state through
    // BuildEdit.FromSelection (the same rule the server checks); a selection with no valid state is shown red and cannot be
    // confirmed. H again sends the state, right click sends a Reset (not for a ramp), and either ends edit mode at once so
    // the tool in hand shoots again. Esc, a weapon key, Q, F, losing the piece, leaving reach, dying or leaving the ground
    // cancel it. Edit mode is the client's alone: the server's tool does not change (GameClient keeps Fire and Aim out of
    // the inputs while it is on).
    // Requests take their sequence from the BuildRequestCounter placements use (D4), and the confirmed state is laid over
    // the piece in BuildStore at once (D11). OwnsSequence tells GameClient which BuildResults are an edit's. Pure, no
    // UnityEngine; sending is a delegate. Main thread only; no allocation after construction.
    public sealed class BuildEditController
    {
        // A result older than this is no longer routed here (it would only be a late answer; BuildController ignores it).
        public const float ResultWindowSeconds = 5f;
        // How far outside the face the aim may point and still pick its nearest tile (m).
        public const float PickMargin = 0.25f;
        // The horizontal tile grid of a floor, roof or ramp floats this far above the piece's top (BuildEditOverlay too).
        public const float GridLift = 0.05f;
        private const int RecentCount = 16;

        private readonly Action<BuildEditRequest> _send;
        private readonly BuildRequestCounter _counter;
        // The newest edit sequences and when they were sent (ring): which results are ours.
        private readonly ushort[] _recentSequences = new ushort[RecentCount];
        private readonly float[] _recentTimes = new float[RecentCount];
        private int _recentTotal;
        private bool _painting;
        private bool _paintValue;

        // 기능: 편집 모드 컨트롤러를 만든다.
        // 입력: send - 편집 요청을 보내는 함수, counter - 배치와 같이 쓰는 순번·전송 상한.
        // 출력: 편집 중이 아닌 컨트롤러.
        public BuildEditController(Action<BuildEditRequest> send, BuildRequestCounter counter)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        }

        public bool Active { get; private set; }
        public uint TargetId { get; private set; }
        // The target as shown (an earlier predicted edit included), refreshed every frame.
        public BuildPieceShape Target { get; private set; }
        // Chosen tiles (bit = tile, BuildEdit's numbering) and the tile under the crosshair (-1: none).
        public int Selection { get; private set; }
        public int HoveredTile { get; private set; } = -1;
        // The state the selection means (BuildEdit.FromSelection); PreviewValid false = red, cannot be confirmed.
        public bool PreviewValid { get; private set; }
        public BuildPieceShape Preview { get; private set; }
        // Changes whenever anything the overlay draws changes (it redraws only then).
        public int Version { get; private set; }
        // Debug (F1): edit requests sent and refused.
        public int Sent { get; private set; }
        public int Refused { get; private set; }
        public BuildResultCode LastRefusal { get; private set; }

        // 기능: 조준한 조각의 편집 모드를 시작한다(D10). 내 조각이고 사거리 안일 때만 시작하며, 서버가 다시 검증한다.
        // 입력: id - 조준 Raycast가 맞힌 조각 id, store - 조각 저장소(보이는 모양을 쓴다), myEntityId - 내 Entity id,
        //   eye - 서버 기준 눈 위치, range - 건설 사거리(BuildCatalog.BuildRange).
        // 출력: 시작했으면 Started. 조각이 없으면 NoPiece, 남의 조각이면 NotOwner, 멀면 OutOfRange(모두 시작하지 않는다).
        //   시작하면 선택은 지금 상태를 나타내는 칸들이다(바로 H를 누르면 바뀌는 것이 없다).
        public EditBeginResult TryBegin(uint id, BuildStore store, ushort myEntityId, Vector3 eye, float range)
        {
            if (id == 0 || store == null || !store.TryGet(id, out BuildPieceRecord piece)) return EditBeginResult.NoPiece;
            if (piece.Owner != myEntityId || myEntityId == 0) return EditBeginResult.NotOwner;
            if (!InReach(piece.Shape, eye, range)) return EditBeginResult.OutOfRange;
            Active = true;
            TargetId = id;
            Target = piece.Shape;
            Selection = SelectionOf(piece.Shape);
            HoveredTile = -1;
            _painting = false;
            RefreshPreview();
            Version++;
            return EditBeginResult.Started;
        }

        // 기능: 편집 모드 한 프레임: 대상·사거리를 확인하고, 조준 광선으로 칸을 고르고, 클릭·드래그로 선택을 바꾼다.
        // 입력: canAct - 살아서 지상에 있고 화면이 막지 않음, store - 조각 저장소, eye - 서버 기준 눈 위치, range - 건설 사거리,
        //   rayOrigin·rayDirection - 카메라 조준 광선, pressed·held - 왼쪽 버튼(이번 프레임 눌림, 누르고 있음).
        // 출력: 반환값 없음. 행동할 수 없거나 대상이 사라졌거나 사거리를 벗어나면 편집 모드가 취소된다. 할당 없음.
        public void Update(bool canAct, BuildStore store, Vector3 eye, float range, Vector3 rayOrigin, Vector3 rayDirection, bool pressed, bool held)
        {
            if (!Active) return;
            if (!canAct || store == null || !store.TryGet(TargetId, out BuildPieceRecord piece) || !InReach(piece.Shape, eye, range))
            {
                Cancel();
                return;
            }
            if (!piece.Shape.Equals(Target))
            {
                // A confirmed or rolled-back state of the same piece: keep the selection, show it against the new shape.
                Target = piece.Shape;
                RefreshPreview();
                Version++;
            }
            int hovered = PickTile(Target, rayOrigin, rayDirection);
            if (hovered != HoveredTile)
            {
                HoveredTile = hovered;
                Version++;
            }
            if (!held) _painting = false;
            if (hovered < 0) return;
            int bit = 1 << hovered;
            if (pressed)
            {
                _painting = true;
                _paintValue = (Selection & bit) == 0;
            }
            else if (!_painting || !held)
            {
                return;
            }
            int selection = _paintValue ? Selection | bit : Selection & ~bit;
            if (selection == Selection) return;
            Selection = selection;
            RefreshPreview();
            Version++;
        }

        // 기능: H를 다시 눌러 지금 미리보기 상태를 확정한다. 보내면 예측을 덧씌우고 편집 모드를 끝낸다.
        // 입력: now - 현재 시각(초), store - 조각 저장소(예측을 넣는다).
        // 출력: Sent(보냄, 끝남), NoChange(지금 상태와 같아 보내지 않고 끝남), Invalid(잘못된 선택: 계속 편집),
        //   Busy(1초 상한이나 예측 상한에 걸림: 계속 편집), NotActive(편집 중이 아님).
        public EditSendResult Confirm(float now, BuildStore store)
        {
            if (!Active) return EditSendResult.NotActive;
            if (!PreviewValid) return EditSendResult.Invalid;
            return SendState(now, store, BuildEdit.StateOf(Preview));
        }

        // 기능: 오른쪽 클릭: 대상을 원래 모양(Edit 0)으로 되돌리는 요청을 바로 보낸다(경사로는 해당 없음).
        // 입력: now - 현재 시각(초), store - 조각 저장소.
        // 출력: Confirm과 같다. 경사로면 NoChange이고 편집 모드는 계속된다. 이미 원래 모양이면 보내지 않고 끝난다.
        public EditSendResult ResetPiece(float now, BuildStore store)
        {
            if (!Active) return EditSendResult.NotActive;
            if (Target.Type == BuildPieceType.Ramp) return EditSendResult.NoChange;
            return SendState(now, store, BuildEdit.PackState(0, Target.Rotation));
        }

        // 기능: 편집 모드를 보내지 않고 끝낸다(Esc, 무기 키, Q, F, 대상 상실, 사거리 이탈, 사망 등).
        // 입력: 없음.
        // 출력: 반환값 없음. 편집 모드가 꺼진다(이미 꺼져 있으면 아무것도 하지 않는다).
        public void Cancel()
        {
            if (!Active) return;
            Active = false;
            TargetId = 0;
            HoveredTile = -1;
            _painting = false;
            Version++;
        }

        // 기능: 서버로 보낼 누르고 있는 버튼에서 편집 모드 중의 Fire를 뺀다(D10: 편집 중 왼쪽 클릭은 칸 선택이라 서버가 쏘면 안 된다).
        //   Aim은 입력 비트가 아니라 카메라 확대뿐이라 GameClient가 따로 막는다.
        // 입력: held - 이번 프레임의 누르고 있는 버튼.
        // 출력: 편집 중이면 Fire를 뺀 버튼, 아니면 그대로.
        public InputButtons MaskHeld(InputButtons held) => Active ? held & ~InputButtons.Fire : held;

        // 기능: 이 BuildResult가 편집 요청의 답인지 보고, 그렇다면 예측에 반영한다(D9: 순번으로 주인을 가른다).
        // 입력: result - 서버 결과, store - 조각 저장소, now - 현재 시각(초).
        // 출력: 최근 ResultWindowSeconds 안에 보낸 편집의 순번이면 true(거절이면 롤백되고 Refused가 는다). 아니면 false(배치의 답).
        public bool OnResult(in BuildResult result, BuildStore store, float now)
        {
            if (!OwnsSequence(result.Sequence, now)) return false;
            store?.OnEditResult(result);
            if (result.Code != BuildResultCode.Ok)
            {
                Refused++;
                LastRefusal = result.Code;
            }
            return true;
        }

        // 기능: 순번이 최근에 보낸 편집 요청의 것인지 본다.
        // 입력: sequence - BuildResult의 순번, now - 현재 시각(초).
        // 출력: 최근 RecentCount개의 편집 중 ResultWindowSeconds 안에 보낸 것이면 true.
        public bool OwnsSequence(ushort sequence, float now)
        {
            int count = Math.Min(_recentTotal, RecentCount);
            for (int i = 0; i < count; i++)
            {
                if (_recentSequences[i] == sequence && now - _recentTimes[i] < ResultWindowSeconds) return true;
            }
            return false;
        }

        // 기능: 새 연결·경기 상태 정리: 편집 모드와 최근 순번 기록을 비운다(카운터는 BuildController.Reset이 처음으로 되돌린다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            Cancel();
            _recentTotal = 0;
        }

        // 기능: 조각이 사거리 안인지 본다(BuildController.Judge, 서버 InReach와 같은 거리 기준: 사거리 + 조각 크기의 반).
        // 입력: shape - 조각 모양, eye - 눈 위치, range - 건설 사거리.
        // 출력: 안이면 true.
        public static bool InReach(in BuildPieceShape shape, Vector3 eye, float range)
        {
            Box bounds = BuildGrid.BoundsOf(shape);
            return Vector3.Distance(eye, bounds.Center) <= range + bounds.Size.Length() * 0.5f;
        }

        // 기능: 조각의 지금 상태를 나타내는 칸 선택을 낸다(FromSelection의 역: 편집을 시작할 때의 선택).
        //   Wall·Floor: 뚫린 칸 그대로. Roof: 사각뿔 0칸, 한쪽 경사 = 낮은 쪽 두 칸, 평지붕 4칸, 통로 대각 두 칸.
        //   Ramp: 낮은 쪽 두 칸(오르는 방향의 반대쪽).
        // 입력: shape - 조각 모양.
        // 출력: 칸 선택 비트. FromSelection에 넣으면 같은 편집 상태와 회전이 나온다.
        public static int SelectionOf(in BuildPieceShape shape)
        {
            switch (shape.Type)
            {
                case BuildPieceType.Wall:
                case BuildPieceType.Floor:
                    return shape.Edit;
                case BuildPieceType.Roof:
                    if (shape.Edit >= BuildEdit.RoofSlopeFirst && shape.Edit <= BuildEdit.RoofSlopeLast)
                        return LowSideOf(shape.Edit - BuildEdit.RoofSlopeFirst);
                    if (shape.Edit == BuildEdit.RoofFlat) return 0b1111;
                    if (shape.Edit == BuildEdit.RoofPassage) return 0b1001;
                    return 0;
                default:
                    return LowSideOf(shape.Rotation);
            }
        }

        // 기능: 조준 광선이 조각의 칸 격자 면과 만나는 곳의 칸을 낸다. 벽은 벽 면, 나머지는 조각 위 GridLift 높이의 수평면이다.
        //   맞은 점(hit.point)을 쓰지 않는다: 뚫린 칸으로는 광선이 벽 너머에 닿기 때문이다.
        // 입력: shape - 조각 모양, origin·direction - 조준 광선(direction은 길이가 0이 아니면 된다).
        // 출력: 칸 번호. 면과 평행하거나 뒤쪽이거나 면에서 PickMargin보다 멀면 -1.
        public static int PickTile(in BuildPieceShape shape, Vector3 origin, Vector3 direction)
        {
            float x0 = BuildGrid.CellMinX(shape.X);
            float z0 = BuildGrid.CellMinZ(shape.Z);
            Vector3 normal;
            Vector3 onPlane;
            if (shape.Type == BuildPieceType.Wall)
            {
                normal = shape.Rotation == 0 ? Vector3.UnitZ : Vector3.UnitX;
                onPlane = new Vector3(x0, 0f, z0);
            }
            else
            {
                normal = Vector3.UnitY;
                onPlane = new Vector3(x0, GridHeight(shape), z0);
            }
            float denominator = Vector3.Dot(direction, normal);
            if (MathF.Abs(denominator) < 1e-5f) return -1;
            float t = Vector3.Dot(onPlane - origin, normal) / denominator;
            if (!(t >= 0f)) return -1;
            Vector3 point = origin + direction * t;
            if (shape.Type == BuildPieceType.Wall)
            {
                float u = shape.Rotation == 0 ? point.X - x0 : point.Z - z0;
                float v = point.Y - BuildGrid.LevelBase(shape.Y);
                if (u < -PickMargin || u > BuildGrid.CellSize + PickMargin || v < -PickMargin || v > BuildGrid.LevelHeight + PickMargin) return -1;
            }
            else
            {
                float dx = point.X - x0;
                float dz = point.Z - z0;
                if (dx < -PickMargin || dx > BuildGrid.CellSize + PickMargin || dz < -PickMargin || dz > BuildGrid.CellSize + PickMargin) return -1;
            }
            return BuildEdit.TileAt(shape, point);
        }

        // 기능: 바닥·지붕·경사로의 수평 칸 격자 높이를 낸다(조각 꼭대기 + GridLift).
        // 입력: shape - 조각 모양(벽이 아님).
        // 출력: 격자 면의 높이(m).
        public static float GridHeight(in BuildPieceShape shape) => BuildGrid.BoundsOf(shape).Max.Y + GridLift;

        // 기능: 상태를 보내고(순번, 예측, 전송) 편집 모드를 끝낸다. 지금 상태와 같으면 보내지 않고 끝낸다.
        // 입력: now - 현재 시각(초), store - 조각 저장소, state - 보낼 상태(BuildEdit.PackState).
        // 출력: Sent, NoChange 또는 Busy(1초 상한이나 예측 상한: 편집 모드는 계속).
        private EditSendResult SendState(float now, BuildStore store, ushort state)
        {
            if (state == BuildEdit.StateOf(Target))
            {
                Cancel();
                return EditSendResult.NoChange;
            }
            if (!_counter.Allows(now) || store == null || (store.PredictionCount >= BuildStore.MaxPredictions && !store.IsPredicted(TargetId)))
                return EditSendResult.Busy;
            ushort sequence = _counter.Next(now);
            int slot = _recentTotal % RecentCount;
            _recentSequences[slot] = sequence;
            _recentTimes[slot] = now;
            _recentTotal++;
            store.PredictEdit(TargetId, state, sequence, now);
            var request = new BuildEditRequest { Sequence = sequence, PieceId = TargetId, State = state };
            Sent++;
            Cancel();
            _send(request);
            return EditSendResult.Sent;
        }

        // 기능: 지금 선택을 미리보기 상태로 바꾼다(BuildEdit.FromSelection).
        // 입력: 없음(Target, Selection).
        // 출력: 반환값 없음. PreviewValid와 Preview가 바뀐다(잘못된 선택이면 Preview는 대상 모양 그대로).
        private void RefreshPreview()
        {
            PreviewValid = BuildEdit.FromSelection(Target.Type, Selection, Target.Rotation, out int edit, out int rotation);
            Preview = PreviewValid ? Target.WithEdit(edit, rotation) : Target;
        }

        // 기능: 2 x 2에서 높아지는 방향의 반대쪽(낮은 쪽) 두 칸을 낸다(BuildEdit의 표를 거꾸로 읽는다).
        // 입력: direction - 높아지는 방향(0 +Z, 1 +X, 2 -Z, 3 -X).
        // 출력: 칸 선택 비트.
        private static int LowSideOf(int direction)
        {
            switch (direction & 3)
            {
                case 0: return 0b0011;
                case 1: return 0b0101;
                case 2: return 0b1100;
                default: return 0b1010;
            }
        }
    }

    // Phase 13.5 D10: why H did (not) start edit mode.
    public enum EditBeginResult : byte
    {
        Started = 0,
        NoPiece = 1,
        NotOwner = 2,
        OutOfRange = 3,
    }

    // Phase 13.5 D10: what a confirm (H) or a reset (right click) did.
    public enum EditSendResult : byte
    {
        NotActive = 0,
        Sent = 1,
        NoChange = 2,
        Invalid = 3,
        Busy = 4,
    }
}
