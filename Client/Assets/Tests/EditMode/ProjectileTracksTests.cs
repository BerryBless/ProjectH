using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using Vec3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 17 D7: the projectile model (extrapolation, events, clear rules).
    public class ProjectileTracksTests
    {
        private const int SimHz = 30;
        private const float Gravity = 9.81f;

        private static ProjectileSpawned Spawned(ushort id, Vec3 position, Vec3 velocity, uint tick,
            ProjectileKind kind = ProjectileKind.Grenade) =>
            new ProjectileSpawned { Id = id, Kind = kind, OwnerId = 7, Position = position, Velocity = velocity, StartTick = tick };

        private static void AssertNear(Vec3 expected, Vec3 actual, float tolerance = 1e-4f)
        {
            Assert.AreEqual(expected.X, actual.X, tolerance);
            Assert.AreEqual(expected.Y, actual.Y, tolerance);
            Assert.AreEqual(expected.Z, actual.Z, tolerance);
        }

        [Test]
        public void Extrapolate_IsTheConstantAccelerationFlight()
        {
            ProjectileTracks.Extrapolate(new Vec3(1f, 2f, 3f), new Vec3(4f, 5f, 0f), Gravity, 0.5f, out Vec3 p, out Vec3 v);
            AssertNear(new Vec3(1f + 2f, 2f + 2.5f - 0.5f * Gravity * 0.25f, 3f), p);
            AssertNear(new Vec3(4f, 5f - Gravity * 0.5f, 0f), v);
        }

        [Test]
        public void Extrapolate_ZeroGravity_IsAStraightLine()
        {
            ProjectileTracks.Extrapolate(Vec3.Zero, new Vec3(0f, 0f, 40f), 0f, 1f, out Vec3 p, out Vec3 v);
            AssertNear(new Vec3(0f, 0f, 40f), p);
            AssertNear(new Vec3(0f, 0f, 40f), v);
        }

        [Test]
        public void Extrapolate_RestingOrBeforeTheEvent_StaysPut()
        {
            ProjectileTracks.Extrapolate(new Vec3(1f, 1f, 1f), Vec3.Zero, Gravity, 2f, out Vec3 p, out _);
            AssertNear(new Vec3(1f, 1f, 1f), p);   // zero velocity = resting: no gravity
            ProjectileTracks.Extrapolate(new Vec3(1f, 1f, 1f), new Vec3(5f, 0f, 0f), Gravity, -0.2f, out p, out _);
            AssertNear(new Vec3(1f, 1f, 1f), p);   // an event from slightly ahead of the drawn tick
            ProjectileTracks.Extrapolate(new Vec3(1f, 1f, 1f), new Vec3(5f, 0f, 0f), Gravity, float.NaN, out p, out _);
            AssertNear(new Vec3(1f, 1f, 1f), p);
        }

        [Test]
        public void Sample_UsesTheEventTickAndSimHz()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(5, new Vec3(0f, 10f, 0f), new Vec3(3f, 0f, 0f), 100), Gravity, 90);
            Assert.AreEqual(1, tracks.Count);
            int slot = SlotOf(tracks, 5);
            tracks.Sample(slot, 130, SimHz, out Vec3 p, out _);   // one second later
            AssertNear(new Vec3(3f, 10f - 0.5f * Gravity, 0f), p);
            Assert.AreEqual(ProjectileKind.Grenade, tracks.KindAt(slot));
            Assert.AreEqual(7, tracks.OwnerAt(slot));
        }

        [Test]
        public void State_ReplacesTheBase_ZeroVelocityRests()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(5, new Vec3(0f, 10f, 0f), new Vec3(3f, 0f, 0f), 100), Gravity, 90);
            Assert.IsTrue(tracks.ApplyState(new ProjectileState { Id = 5, Position = new Vec3(2f, 0.1f, 0f), Velocity = new Vec3(1f, 2f, 0f), Tick = 115 }));
            int slot = SlotOf(tracks, 5);
            tracks.Sample(slot, 145, SimHz, out Vec3 p, out _);
            AssertNear(new Vec3(3f, 0.1f + 2f - 0.5f * Gravity, 0f), p);

            Assert.IsTrue(tracks.ApplyState(new ProjectileState { Id = 5, Position = new Vec3(4f, 0.1f, 0f), Velocity = Vec3.Zero, Tick = 150 }));
            tracks.Sample(slot, 200, SimHz, out p, out _);
            AssertNear(new Vec3(4f, 0.1f, 0f), p);
        }

        [Test]
        public void State_ForAnUnknownId_IsIgnored()
        {
            var tracks = new ProjectileTracks();
            Assert.IsFalse(tracks.ApplyState(new ProjectileState { Id = 9, Position = Vec3.One, Tick = 1 }));
            Assert.AreEqual(0, tracks.Count);
        }

        [Test]
        public void Spawn_SameId_IsAnUpsert()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(5, Vec3.Zero, Vec3.One, 100), Gravity, 90);
            tracks.Spawn(Spawned(5, new Vec3(9f, 9f, 9f), Vec3.Zero, 140), Gravity, 90);   // resent at a resume
            Assert.AreEqual(1, tracks.Count);
            tracks.Sample(SlotOf(tracks, 5), 160, SimHz, out Vec3 p, out _);
            AssertNear(new Vec3(9f, 9f, 9f), p);
        }

        [Test]
        public void Remove_TakesItOut_UnknownIdIsFalse()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(5, Vec3.Zero, Vec3.One, 100), Gravity, 90);
            Assert.IsTrue(tracks.Remove(5));
            Assert.AreEqual(0, tracks.Count);
            Assert.IsFalse(tracks.Remove(5));
            Assert.AreEqual(-1, SlotOf(tracks, 5));
        }

        [Test]
        public void FullTable_ReplacesTheOldestEvent()
        {
            var tracks = new ProjectileTracks();
            for (int i = 0; i < ProjectileTracks.Capacity; i++)
                tracks.Spawn(Spawned((ushort)(i + 1), Vec3.Zero, Vec3.One, (uint)(200 + i)), Gravity, 90);
            Assert.AreEqual(ProjectileTracks.Capacity, tracks.Count);
            tracks.Spawn(Spawned(1000, Vec3.Zero, Vec3.One, 300), 0f, 120);
            Assert.AreEqual(ProjectileTracks.Capacity, tracks.Count);
            Assert.AreEqual(-1, SlotOf(tracks, 1));      // tick 200, the oldest
            Assert.IsTrue(SlotOf(tracks, 1000) >= 0);
            Assert.IsTrue(SlotOf(tracks, 2) >= 0);
        }

        [Test]
        public void Expire_DropsOnlyThosePastLifetimeAndGrace()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(1, Vec3.Zero, Vec3.One, 100), Gravity, 90);    // lives until 190 (+30 grace)
            tracks.Spawn(Spawned(2, Vec3.Zero, Vec3.One, 150), 0f, 120);        // until 270 (+30)
            Assert.AreEqual(0, tracks.Expire(220, 30));
            Assert.AreEqual(1, tracks.Expire(221, 30));
            Assert.AreEqual(-1, SlotOf(tracks, 1));
            Assert.AreEqual(1, tracks.Count);
            Assert.AreEqual(1, tracks.Expire(301, 30));
            Assert.AreEqual(0, tracks.Count);
        }

        [Test]
        public void Clear_RemovesEverything()
        {
            var tracks = new ProjectileTracks();
            tracks.Spawn(Spawned(1, Vec3.Zero, Vec3.One, 100), Gravity, 90);
            tracks.Spawn(Spawned(2, Vec3.Zero, Vec3.One, 100, ProjectileKind.Rocket), 0f, 120);
            tracks.Clear();
            Assert.AreEqual(0, tracks.Count);
            for (int i = 0; i < ProjectileTracks.Capacity; i++) Assert.IsFalse(tracks.IsActive(i));
        }

        [Test]
        public void ClearsOnMatchState_OnlyOnAChangeIntoWaitingStartingOrFinished()
        {
            // The first MatchState after a join is never a change (the join already cleared; a late one must not wipe the
            // projectiles just resent), whatever the zero-valued previous state says.
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(false, MatchFlowState.WaitingForPlayers, MatchFlowState.Starting));
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(false, MatchFlowState.WaitingForPlayers, MatchFlowState.Finished));
            // The same state again (every MatchState resend) clears nothing.
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Playing, MatchFlowState.Playing));
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Finished, MatchFlowState.Finished));
            // Into the match or its final phase: no.
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Starting, MatchFlowState.Playing));
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Playing, MatchFlowState.FinalPhase));
            Assert.IsFalse(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Finished, MatchFlowState.Closing));
            // A change into Waiting, Starting or Finished: yes.
            Assert.IsTrue(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Playing, MatchFlowState.Finished));
            Assert.IsTrue(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.FinalPhase, MatchFlowState.Finished));
            Assert.IsTrue(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.Finished, MatchFlowState.WaitingForPlayers));
            Assert.IsTrue(ProjectileTracks.ClearsOnMatchState(true, MatchFlowState.WaitingForPlayers, MatchFlowState.Starting));
        }

        // 기능: id가 든 칸을 찾는다(테스트 헬퍼).
        // 입력: tracks - 투사체 상태, id - 서버 투사체 id.
        // 출력: 칸 번호, 없으면 -1.
        private static int SlotOf(ProjectileTracks tracks, ushort id)
        {
            for (int i = 0; i < ProjectileTracks.Capacity; i++)
            {
                if (tracks.IsActive(i) && tracks.IdAt(i) == id) return i;
            }
            return -1;
        }
    }
}
