using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 13 D13, D14: the confirmed pieces are applied by id, so repeats and late events change nothing, and the
    // interest window decides what is kept.
    public class BuildStoreTests
    {
        private const ulong All = ulong.MaxValue;

        private static BuildPieceRecord Piece(uint id, int x, int z, BuildPieceType type = BuildPieceType.Floor) => new BuildPieceRecord
        {
            Id = id,
            Shape = new BuildPieceShape(type, x, 0, z, 0),
            Material = BuildMaterialType.Wood,
            CreatedTick = 10,
        };

        private static BuildStore Store(ulong cells = All)
        {
            var store = new BuildStore();
            store.ApplyInterest(cells);
            return store;
        }

        // Final review C: a reset starts the debug numbers over too (a new connection's versions start from 0).
        [Test]
        public void AReset_ClearsTheVersionAndTheIgnoredCount()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3), 7);
            store.ApplyHealth(99, 5, 8);   // unknown: ignored
            Assert.AreEqual(8u, store.Version);
            Assert.AreEqual(1, store.Ignored);
            store.Reset();
            Assert.AreEqual(0u, store.Version);
            Assert.AreEqual(0, store.Ignored);
            Assert.AreEqual(0, store.Count);
        }

        [Test]
        public void ARepeatedPlacement_ChangesNothing()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3), 1);
            store.ApplyPiece(Piece(1, 3, 3), 1);
            Assert.AreEqual(1, store.Count);
            Assert.AreEqual(1, store.Grid.Count);
            Assert.AreEqual(1, store.Changed.Count);
        }

        [Test]
        public void HealthAndDestroyed_ForAnUnknownPiece_AreIgnored()
        {
            BuildStore store = Store();
            store.ApplyHealth(9, 50, 2);
            store.ApplyDestroyed(9, 3);
            Assert.AreEqual(0, store.Count);
            Assert.AreEqual(2, store.Ignored);
            Assert.AreEqual(3u, store.Version);
        }

        [Test]
        public void ADestroyedPiece_StaysGone_WhenALateHealthEventFollows()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3), 1);
            store.ApplyHealth(1, 20, 2);
            Assert.IsTrue(store.TryGet(1, out BuildPieceRecord hurt));
            Assert.AreEqual(20, hurt.Damage);
            store.ApplyDestroyed(1, 3);
            store.ApplyDestroyed(1, 3);
            store.ApplyHealth(1, 30, 3);
            Assert.IsFalse(store.TryGet(1, out _));
            Assert.AreEqual(0, store.Grid.Count);
        }

        [Test]
        public void APieceOutsideTheWindow_IsNotKept()
        {
            BuildStore store = Store(1UL);   // interest cell 0 only: build cells 0-3 by 0-3
            store.ApplyPiece(Piece(1, 2, 2), 1);
            store.ApplyPiece(Piece(2, 4, 0), 1);   // interest cell 1
            Assert.IsTrue(store.TryGet(1, out _));
            Assert.IsFalse(store.TryGet(2, out _));
            Assert.AreEqual(1, store.Ignored);
        }

        [Test]
        public void LeavingACell_DropsItsPieces_AndEnteringAgainTakesTheSync()
        {
            BuildStore store = Store(0b11UL);
            store.ApplyPiece(Piece(1, 2, 2), 1);
            store.ApplyPiece(Piece(2, 5, 1), 1);
            store.ClearChanged();
            store.ApplyInterest(0b01UL);
            Assert.IsTrue(store.TryGet(1, out _));
            Assert.IsFalse(store.TryGet(2, out _));
            Assert.AreEqual(1, store.Grid.Count);
            CollectionAssert.AreEqual(new[] { 2u }, store.Changed);
            store.ApplyInterest(0b11UL);
            store.ApplyPiece(Piece(2, 5, 1), 4);   // the server's sync for the cell entered
            Assert.IsTrue(store.TryGet(2, out _));
        }

        [Test]
        public void AResetSync_DropsEverything()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 2, 2), 1);
            store.ApplyPiece(Piece(2, 5, 1), 1);
            store.Reset();
            Assert.AreEqual(0, store.Count);
            Assert.AreEqual(0, store.Grid.Count);
            Assert.AreEqual(0UL, store.Cells);
            store.ApplyPiece(Piece(3, 2, 2), 2);   // nothing is kept until the window comes again
            Assert.AreEqual(0, store.Count);
        }

        [Test]
        public void Occupied_SeesTheSameSlot_AndAFloorOverARoof()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 2, 2, BuildPieceType.Roof), 1);
            Assert.IsTrue(store.Occupied(new BuildPieceShape(BuildPieceType.Roof, 2, 0, 2, 0)));
            Assert.IsTrue(store.Occupied(new BuildPieceShape(BuildPieceType.Floor, 2, 1, 2, 0)));
            Assert.IsFalse(store.Occupied(new BuildPieceShape(BuildPieceType.Floor, 2, 0, 2, 0)));
            Assert.IsFalse(store.Occupied(new BuildPieceShape(BuildPieceType.Roof, 3, 0, 2, 0)));
        }

        // Phase 13.5 D8: an Edited record changes the shape in place; one that does not fit the piece changes nothing.
        [Test]
        public void AnEditedRecord_ChangesTheShapeInPlace_AndABadOneIsIgnored()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3, BuildPieceType.Wall), 1);
            store.ApplyHealth(1, 40, 2);
            store.ClearChanged();
            int slot = store.Grid.SlotOf(1);
            Assert.IsTrue(store.ApplyEdited(1, BuildEdit.PackState(0b000010010, 0), 3));   // a door
            Assert.IsTrue(store.TryGet(1, out BuildPieceRecord door));
            Assert.AreEqual(0b000010010, door.Shape.Edit);
            Assert.AreEqual(40, door.Damage);
            Assert.AreEqual(10u, door.CreatedTick);
            Assert.AreEqual(slot, store.Grid.SlotOf(1));
            CollectionAssert.AreEqual(new[] { 1u }, store.Changed);
            Assert.IsFalse(store.ApplyEdited(1, BuildEdit.PackState(0b101, 0), 4));     // two holes apart
            Assert.IsFalse(store.ApplyEdited(1, BuildEdit.PackState(0, 1), 4));         // a wall's rotation never changes
            Assert.IsTrue(store.TryGet(1, out door));
            Assert.AreEqual(0b000010010, door.Shape.Edit);
            Assert.IsTrue(store.ApplyEdited(99, BuildEdit.PackState(1, 0), 5));         // unknown: ignored
            Assert.AreEqual(3, store.Ignored);
        }

        // Phase 13.5 D11: a refusal of an older edit does not roll back a newer one of the same piece.
        [Test]
        public void AnOlderRefusal_DoesNotRollBackANewerPrediction()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3, BuildPieceType.Wall), 1);
            Assert.IsTrue(store.PredictEdit(1, BuildEdit.PackState(1 << 4, 0), 1, 0f));
            Assert.IsTrue(store.PredictEdit(1, BuildEdit.PackState(1 << 7, 0), 2, 0.1f));
            Assert.AreEqual(1, store.PredictionCount);
            Assert.IsFalse(store.OnEditResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Blocked }));
            Assert.IsTrue(store.TryGet(1, out BuildPieceRecord shown));
            Assert.AreEqual(1 << 7, shown.Shape.Edit);
        }

        [Test]
        public void APrediction_GoesWithItsPiece_AndWithAReset()
        {
            BuildStore store = Store();
            store.ApplyPiece(Piece(1, 3, 3, BuildPieceType.Wall), 1);
            store.ApplyPiece(Piece(2, 5, 3, BuildPieceType.Wall), 1);
            store.PredictEdit(1, BuildEdit.PackState(1 << 4, 0), 1, 0f);
            store.PredictEdit(2, BuildEdit.PackState(1 << 4, 0), 2, 0f);
            store.ApplyDestroyed(1, 2);
            Assert.IsFalse(store.IsPredicted(1));
            Assert.AreEqual(1, store.PredictionCount);
            store.Reset();
            Assert.AreEqual(0, store.PredictionCount);
        }

        [Test]
        public void Predictions_AreBounded_AndAPlacedRecordAfterAnOkSettlesOne()
        {
            BuildStore store = Store();
            for (uint id = 1; id <= BuildStore.MaxPredictions + 1; id++) store.ApplyPiece(Piece(id, (int)id, 3, BuildPieceType.Wall), 1);
            for (uint id = 1; id <= BuildStore.MaxPredictions; id++)
                Assert.IsTrue(store.PredictEdit(id, BuildEdit.PackState(1 << 4, 0), (ushort)id, 0f));
            Assert.IsFalse(store.PredictEdit(BuildStore.MaxPredictions + 1, BuildEdit.PackState(1 << 4, 0), 99, 0f));
            Assert.IsFalse(store.PredictEdit(1, BuildEdit.PackState(0b101, 0), 100, 0f));   // not a valid wall state

            Assert.IsTrue(store.OnEditResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Ok, PieceId = 1 }));
            Assert.IsTrue(store.IsPredicted(1));
            BuildPieceRecord resent = Piece(1, 1, 3, BuildPieceType.Wall);
            store.ApplyPiece(resent, 2);   // a sync from before the edit, after the Ok: the server's word
            Assert.IsFalse(store.IsPredicted(1));
            Assert.IsTrue(store.TryGet(1, out BuildPieceRecord shown));
            Assert.AreEqual(0, shown.Shape.Edit);
        }

        [Test]
        public void CellOf_FollowsTheCatalogsInterestCellSize()
        {
            var store = new BuildStore();
            Assert.AreEqual(0, store.CellOf(new BuildPieceShape(BuildPieceType.Floor, 3, 0, 3, 0)));
            Assert.AreEqual(9, store.CellOf(new BuildPieceShape(BuildPieceType.Floor, 4, 0, 4, 0)));
            store.CellsPerInterest = 8;
            Assert.AreEqual(5, store.CellOf(new BuildPieceShape(BuildPieceType.Floor, 8, 0, 8, 0)));
        }
    }
}
