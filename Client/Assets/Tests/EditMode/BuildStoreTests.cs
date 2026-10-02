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
