using System.Collections.Generic;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 13 D8, D16: build mode's requests: numbered from 1, shown as pending until their result or a timeout, never
    // two for one slot, and judged before sending.
    public class BuildControllerTests
    {
        private static readonly NVector3 Feet = new NVector3(-27.5f, 0f, -17.5f);
        private static readonly NVector3 Eye = Feet + new NVector3(0f, 1.6f, 0f);

        private readonly List<BuildRequest> _sent = new List<BuildRequest>();

        private BuildController Controller(int wood = 100)
        {
            _sent.Clear();
            var c = new BuildController(r => _sent.Add(r))
            {
                Catalog = new BuildCatalogData { BuildRange = 7f, MinBuildIntervalTicks = 3 },
                Resources = new ResourcesState { Wood = (ushort)wood },
            };
            c.Catalog.ResourceCost[0] = 10;
            return c;
        }

        private static BuildStore Store()
        {
            var store = new BuildStore();
            store.ApplyInterest(ulong.MaxValue);
            return store;
        }

        private bool Press(BuildController c, float now, BuildStore store, bool inBuildMode = true) =>
            c.Update(now, inBuildMode, true, true, Feet, Eye, 0f, 0f, store);

        [Test]
        public void APress_SendsTheCandidate_AndShowsItPending()
        {
            BuildController c = Controller();
            Assert.IsTrue(Press(c, 0f, Store()));
            Assert.AreEqual(1, _sent.Count);
            Assert.AreEqual(1, _sent[0].Sequence);
            Assert.AreEqual(new BuildPieceShape(BuildPieceType.Wall, 10, 0, 13, 0), c.Candidate);
            Assert.AreEqual(1, c.PendingCount);
            Assert.AreEqual(90, c.ShownResource(BuildMaterialType.Wood));
        }

        [Test]
        public void APendingSlot_IsNotOfferedAgain_UntilItsResult()
        {
            BuildController c = Controller();
            BuildStore store = Store();
            Press(c, 0f, store);
            Assert.IsFalse(Press(c, 0.5f, store));
            Assert.AreEqual(BuildPreviewState.Invalid, c.CandidateState);
            c.OnResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Blocked });
            Assert.AreEqual(0, c.PendingCount);
            Assert.AreEqual(1, c.Refused);
            Assert.AreEqual(BuildResultCode.Blocked, c.LastRefusal);
            Assert.IsTrue(Press(c, 0.6f, store));
            Assert.AreEqual(2, _sent[1].Sequence);
        }

        [Test]
        public void APendingPlacement_GoesAfterTheTimeout()
        {
            BuildController c = Controller();
            Press(c, 0f, Store());
            c.Update(BuildController.TimeoutSeconds + 0.01f, false, false, false, Feet, Eye, 0f, 0f, Store());
            Assert.AreEqual(0, c.PendingCount);
        }

        [Test]
        public void NotAffordable_IsShownAndNotSent()
        {
            BuildController c = Controller(wood: 5);
            Assert.IsFalse(Press(c, 0f, Store()));
            Assert.AreEqual(BuildPreviewState.NoResource, c.CandidateState);
            Assert.AreEqual(0, _sent.Count);
        }

        [Test]
        public void ATakenSlot_IsInvalid()
        {
            BuildController c = Controller();
            BuildStore store = Store();
            store.ApplyPiece(new BuildPieceRecord { Id = 4, Shape = new BuildPieceShape(BuildPieceType.Wall, 10, 0, 13, 0) }, 1);
            Assert.IsFalse(Press(c, 0f, store));
            Assert.AreEqual(BuildPreviewState.Invalid, c.CandidateState);
        }

        [Test]
        public void OutOfBuildMode_ThereIsNoCandidate()
        {
            BuildController c = Controller();
            Assert.IsFalse(Press(c, 0f, Store(), inBuildMode: false));
            Assert.IsFalse(c.HasCandidate);
        }

        [Test]
        public void Reset_NumbersFromOneAgain()
        {
            BuildController c = Controller();
            Press(c, 0f, Store());
            c.Reset();
            Assert.AreEqual(0, c.PendingCount);
            c.Resources = new ResourcesState { Wood = 100 };
            Press(c, 5f, Store());
            Assert.AreEqual(1, _sent[_sent.Count - 1].Sequence);
        }

        [Test]
        public void AnAcceptedPlacement_StaysPending_UntilTheConfirmedPieceArrives()
        {
            BuildController c = Controller();
            BuildStore store = Store();
            Press(c, 0f, store);
            c.OnResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Ok, PieceId = 4 });
            c.Update(0.1f, false, false, false, Feet, Eye, 0f, 0f, store);
            Assert.AreEqual(1, c.PendingCount);
            Assert.AreEqual(100, c.ShownResource(BuildMaterialType.Wood));   // the server's number already has the cost
            store.ApplyPiece(new BuildPieceRecord { Id = 4, Shape = new BuildPieceShape(BuildPieceType.Wall, 10, 0, 13, 0) }, 1);
            c.Update(0.2f, false, false, false, Feet, Eye, 0f, 0f, store);
            Assert.AreEqual(0, c.PendingCount);
        }

        [Test]
        public void AnAcceptedPlacement_StillGoesAtTheTimeout()
        {
            BuildController c = Controller();
            Press(c, 0f, Store());
            c.OnResult(new BuildResult { Sequence = 1, Code = BuildResultCode.Ok, PieceId = 4 });
            c.Update(BuildController.TimeoutSeconds + 0.01f, false, false, false, Feet, Eye, 0f, 0f, Store());
            Assert.AreEqual(0, c.PendingCount);
        }

        [Test]
        public void HoldingTheButton_NeverSendsMoreThanTheCapInASecond()
        {
            BuildController c = Controller(wood: 10000);
            c.Catalog.MinBuildIntervalTicks = 1;
            BuildStore store = Store();
            float x = -27.5f;
            int sent = 0;
            for (int i = 0; i < 100; i++)
            {
                float now = i * 0.01f;   // 100 frames within one second, each a new slot
                var feet = new NVector3(x + (i % 20) * 5f, 0f, -17.5f);
                if (c.Update(now, true, true, true, feet, feet + new NVector3(0f, 1.6f, 0f), 0f, 0f, store)) sent++;
                c.OnResult(new BuildResult { Sequence = (ushort)sent, Code = BuildResultCode.Blocked });
            }
            Assert.AreEqual(BuildController.MaxRequestsPerSecond, sent);
            Assert.IsTrue(c.Update(1.01f, true, true, true, Feet + new NVector3(5f, 0f, 0f), Eye + new NVector3(5f, 0f, 0f), 0f, 0f, store));
        }
    }
}
