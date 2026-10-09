using System.Collections.Generic;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 13.5 D4, D9-D11: edit mode: starting only on our own piece in reach, tile picking, click and drag, the preview
    // (red when invalid), confirm -> prediction -> rollback / Ok / timeout, Reset, cancelling, Fire kept out of the inputs,
    // and the sequence shared with placements with each result routed to its owner.
    public class BuildEditControllerTests
    {
        private const uint WallId = 5;
        private const ushort Me = 7;
        // A south wall of cell (10, 13): x -30..-25, y 0..3 on z = -15. The player stands 2.5 m south of its middle.
        private static readonly BuildPieceShape Wall = new BuildPieceShape(BuildPieceType.Wall, 10, 0, 13, 0);
        private static readonly NVector3 Eye = new NVector3(-27.5f, 1.6f, -17.5f);

        private readonly List<BuildEditRequest> _sent = new List<BuildEditRequest>();
        private BuildRequestCounter _counter;

        // 기능: 보낸 편집 요청을 _sent에 모으는 시험용 BuildEditController를 새 Sequence Counter와 함께 만든다.
        // 입력: 없음.
        // 출력: _sent를 비우고 _counter를 새로 만든 뒤 만든 BuildEditController.
        private BuildEditController Controller()
        {
            _sent.Clear();
            _counter = new BuildRequestCounter();
            return new BuildEditController(r => _sent.Add(r), _counter);
        }

        // 기능: 모든 관심 영역이 켜진 BuildStore에 나무 조각 하나를 확정 상태로 넣어 만든다.
        // 입력: shape - 조각 모양, owner - 조각 소유자 ID, id - 조각 ID.
        // 출력: 조각 하나가 들어 있는 BuildStore.
        private static BuildStore Store(BuildPieceShape shape, ushort owner = Me, uint id = WallId)
        {
            var store = new BuildStore();
            store.ApplyInterest(ulong.MaxValue);
            store.ApplyPiece(new BuildPieceRecord { Id = id, Shape = shape, Owner = owner, Material = BuildMaterialType.Wood }, 1);
            return store;
        }

        // 기능: 시험용 벽(Wall) 면 위 3x3 타일 한 칸의 중심 좌표를 구한다.
        // 입력: column - 타일 열(0..2, 서쪽부터), row - 타일 행(0..2, 아래부터).
        // 출력: 벽 면(z = -15) 위 타일 중심의 월드 좌표.
        // The middle of a wall tile on the wall's face (column, row).
        private static NVector3 WallTile(int column, int row) =>
            new NVector3(-30f + (column + 0.5f) * BuildGrid.CellSize / 3f, (row + 0.5f) * BuildGrid.LevelHeight / 3f, -15f);

        // 기능: 눈 위치에서 목표점을 바라보며 편집 입력 한 프레임을 Update로 넣는다(사거리 7 m, 행동 가능 상태).
        // 입력: edit - 대상 편집 컨트롤러, store - 조각 저장소, at - 바라볼 월드 좌표, pressed - 이 프레임에 클릭했는지, held - 버튼을 누르고 있는지, eye - 눈 위치(null이면 Eye).
        // 출력: 반환값 없음. 편집 컨트롤러의 Hover·선택·활성 상태가 갱신된다.
        private static void Aim(BuildEditController edit, BuildStore store, NVector3 at, bool pressed, bool held = true, NVector3? eye = null)
        {
            NVector3 from = eye ?? Eye;
            edit.Update(true, store, from, 7f, from, at - from, pressed, held);
        }

        // 기능: 시험용 벽(WallId)에 대해 편집 모드를 시작하고 Started로 시작됐는지 단언한다.
        // 입력: edit - 대상 편집 컨트롤러, store - 벽이 들어 있는 조각 저장소.
        // 출력: 편집 모드가 켜진 같은 컨트롤러.
        private static BuildEditController Begin(BuildEditController edit, BuildStore store)
        {
            Assert.AreEqual(EditBeginResult.Started, edit.TryBegin(WallId, store, Me, Eye, 7f));
            return edit;
        }

        [Test]
        public void EditMode_StartsOnlyOnOurOwnPiece_InReach()
        {
            BuildEditController edit = Controller();
            Assert.AreEqual(EditBeginResult.NotOwner, edit.TryBegin(WallId, Store(Wall, owner: 8), Me, Eye, 7f));
            Assert.AreEqual(EditBeginResult.OutOfRange, edit.TryBegin(WallId, Store(Wall), Me, Eye + new NVector3(0f, 0f, -20f), 7f));
            Assert.AreEqual(EditBeginResult.NoPiece, edit.TryBegin(99, Store(Wall), Me, Eye, 7f));
            Assert.IsFalse(edit.Active);
            Assert.AreEqual(EditBeginResult.Started, edit.TryBegin(WallId, Store(Wall), Me, Eye, 7f));
            Assert.IsTrue(edit.Active);
            Assert.AreEqual(0, edit.Selection);
            Assert.IsTrue(edit.PreviewValid);
        }

        [Test]
        public void AClick_TogglesTheTileUnderTheCrosshair_AndDraggingPaintsTheSameValue()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Aim(edit, store, WallTile(1, 1), pressed: true);
            Assert.AreEqual(4, edit.HoveredTile);
            Assert.AreEqual(1 << 4, edit.Selection);
            Assert.IsTrue(edit.PreviewValid);
            Assert.AreEqual(1 << 4, edit.Preview.Edit);   // a window
            Aim(edit, store, WallTile(1, 0), pressed: false);   // held, dragged down
            Assert.AreEqual((1 << 4) | (1 << 1), edit.Selection);
            Aim(edit, store, WallTile(1, 0), pressed: false, held: false);
            Aim(edit, store, WallTile(1, 1), pressed: true);   // a new click on a chosen tile clears it
            Assert.AreEqual(1 << 1, edit.Selection);
        }

        [Test]
        public void AnInvalidSelection_IsRed_AndCannotBeConfirmed()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Aim(edit, store, WallTile(0, 0), pressed: true);
            Aim(edit, store, WallTile(0, 0), pressed: false, held: false);
            Aim(edit, store, WallTile(2, 0), pressed: true);   // two holes that do not touch
            Assert.IsFalse(edit.PreviewValid);
            Assert.AreEqual(EditSendResult.Invalid, edit.Confirm(0f, store));
            Assert.IsTrue(edit.Active);
            Assert.AreEqual(0, _sent.Count);
        }

        [Test]
        public void Confirm_PredictsAtOnce_AndARefusalRollsBack()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Aim(edit, store, WallTile(1, 1), pressed: true);
            Assert.AreEqual(EditSendResult.Sent, edit.Confirm(0f, store));
            Assert.IsFalse(edit.Active);
            Assert.AreEqual(1, _sent.Count);
            Assert.AreEqual(1, _sent[0].Sequence);
            Assert.AreEqual(WallId, _sent[0].PieceId);
            Assert.AreEqual(BuildEdit.PackState(1 << 4, 0), _sent[0].State);
            Assert.IsTrue(store.TryGet(WallId, out BuildPieceRecord shown));
            Assert.AreEqual(1 << 4, shown.Shape.Edit);
            Assert.IsTrue(store.Grid.TryGet(WallId, out BuildPieceShape colliding));
            Assert.AreEqual(1 << 4, colliding.Edit);   // prediction walks through the new window
            Assert.IsTrue(store.TryGetConfirmed(WallId, out BuildPieceRecord confirmed));
            Assert.AreEqual(0, confirmed.Shape.Edit);

            Assert.IsTrue(edit.OnResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Blocked }, store, 0.1f));
            Assert.IsTrue(store.TryGet(WallId, out shown));
            Assert.AreEqual(0, shown.Shape.Edit);
            Assert.IsTrue(store.Grid.TryGet(WallId, out colliding));
            Assert.AreEqual(0, colliding.Edit);
            Assert.AreEqual(1, edit.Refused);
            Assert.AreEqual(BuildResultCode.Blocked, edit.LastRefusal);
        }

        [Test]
        public void AnOk_KeepsThePrediction_UntilTheEditedRecordArrives()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Aim(edit, store, WallTile(1, 1), pressed: true);
            edit.Confirm(0f, store);
            edit.OnResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Ok, PieceId = WallId }, store, 0.1f);
            Assert.IsTrue(store.IsPredicted(WallId));
            Assert.IsTrue(store.ApplyEdited(WallId, BuildEdit.PackState(1 << 4, 0), 2));
            Assert.IsFalse(store.IsPredicted(WallId));
            Assert.IsTrue(store.TryGetConfirmed(WallId, out BuildPieceRecord confirmed));
            Assert.AreEqual(1 << 4, confirmed.Shape.Edit);
        }

        [Test]
        public void APrediction_GoesAfterTheTimeout_WithoutAnAnswer()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Aim(edit, store, WallTile(1, 1), pressed: true);
            edit.Confirm(0f, store);
            store.ExpirePredictions(BuildStore.PredictionTimeoutSeconds - 0.01f);
            Assert.IsTrue(store.IsPredicted(WallId));
            store.ExpirePredictions(BuildStore.PredictionTimeoutSeconds + 0.01f);
            Assert.IsFalse(store.IsPredicted(WallId));
            Assert.IsTrue(store.TryGet(WallId, out BuildPieceRecord shown));
            Assert.AreEqual(0, shown.Shape.Edit);
        }

        [Test]
        public void PlacementsAndEdits_ShareOneSequence_AndEachResultGoesToItsOwner()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            var placements = new List<BuildRequest>();
            var build = new BuildController(r => placements.Add(r), _counter)
            {
                Catalog = new BuildCatalogData { BuildRange = 7f, MinBuildIntervalTicks = 3 },
                Resources = new ResourcesState { Wood = 100 },
            };
            // A placement on the next cell (x 11), then the edit.
            var feet = new NVector3(-22.5f, 0f, -17.5f);
            Assert.IsTrue(build.Update(0f, true, true, true, feet, feet + new NVector3(0f, 1.6f, 0f), 0f, 0f, store));
            Aim(edit, store, WallTile(1, 1), pressed: true);
            Assert.AreEqual(EditSendResult.Sent, edit.Confirm(0f, store));
            Assert.AreEqual(1, placements[0].Sequence);
            Assert.AreEqual(2, _sent[0].Sequence);

            // GameClient's routing: an edit's Ok must not settle the placement waiting on another sequence.
            var editOk = new BuildResult { Sequence = 2, Code = BuildResultCode.Ok, PieceId = WallId };
            if (!edit.OnResult(editOk, store, 0.1f)) build.OnResult(editOk);
            Assert.AreEqual(1, build.PendingCount);
            Assert.AreEqual(0u, build.PendingAt(0).AcceptedId);
            var placementRefused = new BuildResult { Sequence = 1, Code = BuildResultCode.Blocked };
            Assert.IsFalse(edit.OnResult(placementRefused, store, 0.2f));
            build.OnResult(placementRefused);
            Assert.AreEqual(0, build.PendingCount);
            Assert.AreEqual(0, edit.Refused);
        }

        [Test]
        public void InEditMode_FireIsKeptOutOfTheInputs()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Controller();
            const InputButtons held = InputButtons.Fire | InputButtons.Sprint;
            Assert.AreEqual(held, edit.MaskHeld(held));
            Begin(edit, store);
            Assert.AreEqual(InputButtons.Sprint, edit.MaskHeld(held));
            edit.Cancel();
            Assert.AreEqual(held, edit.MaskHeld(held));   // back to the tool at once
        }

        [Test]
        public void RightClick_SendsAReset_ButNotForARamp()
        {
            BuildStore store = Store(Wall.WithEdit(1 << 4, 0));
            BuildEditController edit = Begin(Controller(), store);
            Assert.AreEqual(1 << 4, edit.Selection);   // the current holes are the starting selection
            Assert.AreEqual(EditSendResult.Sent, edit.ResetPiece(0f, store));
            Assert.AreEqual(BuildEdit.PackState(0, 0), _sent[0].State);
            Assert.IsFalse(edit.Active);

            var ramp = new BuildPieceShape(BuildPieceType.Ramp, 10, 0, 12, 1);
            BuildStore rampStore = Store(ramp);
            Assert.AreEqual(EditBeginResult.Started, edit.TryBegin(WallId, rampStore, Me, Eye, 7f));
            Assert.AreEqual(EditSendResult.NoChange, edit.ResetPiece(0.5f, rampStore));
            Assert.IsTrue(edit.Active);
            Assert.AreEqual(1, _sent.Count);
        }

        [Test]
        public void ConfirmingTheCurrentState_SendsNothing_AndEndsEditMode()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            Assert.AreEqual(EditSendResult.NoChange, edit.Confirm(0f, store));
            Assert.IsFalse(edit.Active);
            Assert.AreEqual(0, _sent.Count);
        }

        [Test]
        public void EditMode_IsCancelled_WhenThePieceGoes_OrWeLeaveReach_OrCannotAct()
        {
            BuildStore store = Store(Wall);
            BuildEditController edit = Begin(Controller(), store);
            store.ApplyDestroyed(WallId, 2);
            Aim(edit, store, WallTile(1, 1), pressed: false, held: false);
            Assert.IsFalse(edit.Active);

            store = Store(Wall);
            Begin(edit, store);
            Aim(edit, store, WallTile(1, 1), pressed: false, held: false, eye: Eye + new NVector3(0f, 0f, -20f));
            Assert.IsFalse(edit.Active);

            Begin(edit, store);
            edit.Update(false, store, Eye, 7f, Eye, WallTile(1, 1) - Eye, false, false);
            Assert.IsFalse(edit.Active);
        }

        [Test]
        public void TheStartingSelection_MeansTheCurrentState_ForEveryRoofAndRamp()
        {
            for (int e = 0; e <= BuildEdit.RoofPassage; e++)
            {
                var roof = new BuildPieceShape(BuildPieceType.Roof, 3, 0, 3, 0, e);
                Assert.IsTrue(BuildEdit.FromSelection(BuildPieceType.Roof, BuildEditController.SelectionOf(roof), 0, out int edit, out _));
                Assert.AreEqual(e, edit, "roof " + e);
            }
            for (int r = 0; r < 4; r++)
            {
                var ramp = new BuildPieceShape(BuildPieceType.Ramp, 3, 0, 3, r);
                Assert.IsTrue(BuildEdit.FromSelection(BuildPieceType.Ramp, BuildEditController.SelectionOf(ramp), r, out int edit, out int rotation));
                Assert.AreEqual(0, edit);
                Assert.AreEqual(r, rotation, "ramp " + r);
            }
        }

        [Test]
        public void AFloorTile_IsPickedOnTheGridAboveIt()
        {
            var floor = new BuildPieceShape(BuildPieceType.Floor, 10, 1, 13, 0);   // x -30..-25, z -15..-10, top y 3
            var eye = new NVector3(-27.5f, 4.8f, -17.5f);
            // Quadrant 3 (+X, +Z) seen from the south, above the floor.
            Assert.AreEqual(3, BuildEditController.PickTile(floor, eye, new NVector3(-26.25f, BuildEditController.GridHeight(floor), -11.25f) - eye));
            // Looking away from the floor picks nothing.
            Assert.AreEqual(-1, BuildEditController.PickTile(floor, eye, new NVector3(0f, 1f, 0f)));
        }
    }
}
