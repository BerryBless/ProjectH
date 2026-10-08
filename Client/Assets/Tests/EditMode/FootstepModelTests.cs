using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Client.Game.Audio;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 18 D5: footstep decisions (mode, speed, interval, air, teleport guard) and the surface under the feet.
    public class FootstepModelTests
    {
        private const float Dt = 0.02f;

        // 기능: 한 플레이어를 +X로 일정 속도로 seconds 동안 걷게 하고 난 발소리를 센다(나온 걸음은 expect여야 한다).
        // 입력: model - 모델, id - 플레이어, mode·sprinting - 모드·질주, speed - m/s, seconds - 시간, now·feet - 이어 쓰는 시각·발, expect - 기대 걸음.
        // 출력: 발소리 수.
        private static int Walk(FootstepModel model, ushort id, MovementMode mode, bool sprinting, float speed, float seconds, ref float now,
            ref Vector3 feet, FootstepGait expect)
        {
            int count = 0;
            for (float t = 0f; t < seconds; t += Dt)
            {
                model.BeginFrame();
                now += Dt;
                feet.X += speed * Dt;
                FootstepGait gait = model.Sample(id, feet, mode, sprinting, true, now, Dt);
                model.EndFrame();
                if (gait == FootstepGait.None) continue;
                Assert.AreEqual(expect, gait);
                count++;
            }
            return count;
        }

        [Test]
        public void FirstSample_IsSkipped()
        {
            var model = new FootstepModel();
            model.BeginFrame();
            Assert.AreEqual(FootstepGait.None, model.Sample(1, Vector3.Zero, MovementMode.Ground, false, true, 0f, Dt));
            model.EndFrame();
        }

        [TestCase(MovementMode.Ground, false, 4f, FootstepGait.Walk, FootstepModel.WalkInterval)]
        [TestCase(MovementMode.Ground, true, 7f, FootstepGait.Sprint, FootstepModel.SprintInterval)]
        [TestCase(MovementMode.Crouch, false, 2.5f, FootstepGait.Crouch, FootstepModel.CrouchInterval)]
        [TestCase(MovementMode.Downed, false, 1.5f, FootstepGait.Drag, FootstepModel.DragInterval)]
        public void Gait_AndInterval_FollowTheMode(MovementMode mode, bool sprinting, float speed, FootstepGait gait, float interval)
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            int steps = Walk(model, 1, mode, sprinting, speed, 6f, ref now, ref feet, gait);
            int expected = (int)(6f / interval);
            Assert.That(steps, Is.InRange(expected - 1, expected + 1));
        }

        [TestCase(MovementMode.Freefall)]
        [TestCase(MovementMode.Glide)]
        [TestCase(MovementMode.Transport)]
        [TestCase(MovementMode.Vault)]
        public void AirModes_MakeNoStep(MovementMode mode)
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Assert.AreEqual(0, Walk(model, 1, mode, false, 6f, 3f, ref now, ref feet, FootstepGait.Walk));
        }

        [Test]
        public void BelowHalfAMetrePerSecond_MakesNoStep()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Assert.AreEqual(0, Walk(model, 1, MovementMode.Ground, false, 0.4f, 3f, ref now, ref feet, FootstepGait.Walk));
        }

        [Test]
        public void Slide_SoundsOnceWhenItStarts()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Walk(model, 1, MovementMode.Ground, true, 7f, 1f, ref now, ref feet, FootstepGait.Sprint);
            Assert.AreEqual(1, Walk(model, 1, MovementMode.Slide, false, 9f, 1f, ref now, ref feet, FootstepGait.Slide));
        }

        [Test]
        public void TeleportFasterThanTwelveMetresPerSecond_IsNotAStep()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Walk(model, 1, MovementMode.Ground, false, 4f, 0.2f, ref now, ref feet, FootstepGait.Walk);
            now += 1f;   // let the interval pass
            model.BeginFrame();
            feet.X += 30f;
            Assert.AreEqual(FootstepGait.None, model.Sample(1, feet, MovementMode.Ground, false, true, now, Dt));
            model.EndFrame();
        }

        [Test]
        public void TransportToFreefall_MakesNoStep_AndLandingWalksAgain()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Assert.AreEqual(0, Walk(model, 1, MovementMode.Transport, false, 20f * 0.5f, 1f, ref now, ref feet, FootstepGait.Walk));
            Assert.AreEqual(0, Walk(model, 1, MovementMode.Freefall, false, 10f, 1f, ref now, ref feet, FootstepGait.Walk));
            Assert.Greater(Walk(model, 1, MovementMode.Ground, false, 4f, 2f, ref now, ref feet, FootstepGait.Walk), 0);
        }

        [Test]
        public void Forget_AndASlotNotSampledThisFrame_SkipTheNextSample()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            Walk(model, 1, MovementMode.Ground, false, 4f, 1f, ref now, ref feet, FootstepGait.Walk);
            model.Forget(1);
            Assert.AreEqual(0, model.Count);
            now += 1f;
            model.BeginFrame();
            Assert.AreEqual(FootstepGait.None, model.Sample(1, feet, MovementMode.Ground, false, true, now, Dt));
            model.EndFrame();
            Assert.AreEqual(1, model.Count);
            model.BeginFrame();   // not sampled (out of range, despawned)
            model.EndFrame();
            Assert.AreEqual(0, model.Count);
        }

        [Test]
        public void Dead_MakesNoStep()
        {
            var model = new FootstepModel();
            float now = 0f;
            var feet = Vector3.Zero;
            for (int i = 0; i < 100; i++)
            {
                model.BeginFrame();
                now += Dt;
                feet.X += 4f * Dt;
                Assert.AreEqual(FootstepGait.None, model.Sample(1, feet, MovementMode.Ground, false, false, now, Dt));
                model.EndFrame();
            }
        }

        [Test]
        public void KnownSpeed_IsUsedForTheLocalPlayer()
        {
            var model = new FootstepModel();
            int steps = 0;
            float now = 0f;
            for (int i = 0; i < 100; i++)
            {
                model.BeginFrame();
                now += Dt;
                // The render position stands still (no displacement), the prediction says 4 m/s.
                if (model.Sample(1, Vector3.Zero, MovementMode.Ground, false, true, now, Dt, 4f) != FootstepGait.None) steps++;
                model.EndFrame();
            }
            Assert.Greater(steps, 0);
        }

        [Test]
        public void Capacity_HoldsEverySnapshotEntityAndTheLocalPlayer()
        {
            Assert.AreEqual(ProtocolConstants.MaxSnapshotEntities + 1, FootstepModel.Capacity);
        }

        [Test]
        public void Footstep_KindFollowsGaitAndSurface()
        {
            Assert.AreEqual(SoundKind.StepWoodSprint, AudioCatalog.FootstepFor(FootstepGait.Sprint, SurfaceMaterial.Wood));
            Assert.AreEqual(SoundKind.StepMetalCrouch, AudioCatalog.FootstepFor(FootstepGait.Crouch, SurfaceMaterial.Metal));
            Assert.AreEqual(SoundKind.StepGroundWalk, AudioCatalog.FootstepFor(FootstepGait.Walk, SurfaceMaterial.Ground));
            Assert.AreEqual(SoundKind.StepStoneWalk, AudioCatalog.FootstepFor(FootstepGait.Walk, SurfaceMaterial.Stone));
            Assert.AreEqual(SoundKind.Slide, AudioCatalog.FootstepFor(FootstepGait.Slide, SurfaceMaterial.Metal));
            Assert.AreEqual(SoundKind.DownedDrag, AudioCatalog.FootstepFor(FootstepGait.Drag, SurfaceMaterial.Wood));
        }

        [Test]
        public void Surface_OnTheTerrain_IsGround_AboveItIsAir()
        {
            float x = 10f, z = 10f;
            float h = GameMap.Terrain.Height(x, z);
            var store = new BuildStore();
            Assert.IsTrue(SurfaceProbe.TryFind(new Vector3(x, h, z), store, out SurfaceMaterial m));
            Assert.AreEqual(SurfaceMaterial.Ground, m);
            Assert.IsFalse(SurfaceProbe.TryFind(new Vector3(x, h + 2f, z), store, out _));
        }

        [Test]
        public void Surface_OnAMapBox_IsStone()
        {
            Box box = default;
            bool found = false;
            foreach (Box b in GameMap.Boxes)
            {
                // A box wide enough to stand on, with its top above the terrain under its middle.
                if (b.Max.X - b.Min.X < 2f || b.Max.Z - b.Min.Z < 2f) continue;
                Vector3 c = b.Center;
                if (b.Max.Y < GameMap.Terrain.Height(c.X, c.Z) + 1f) continue;
                box = b;
                found = true;
                break;
            }
            Assume.That(found, "the map has no box to stand on");
            Vector3 top = new Vector3(box.Center.X, box.Max.Y, box.Center.Z);
            Assert.IsTrue(SurfaceProbe.TryFind(top, null, out SurfaceMaterial m));
            Assert.AreEqual(SurfaceMaterial.Stone, m);
        }

        [Test]
        public void Surface_OnAConfirmedFloor_IsItsMaterial()
        {
            var store = new BuildStore();
            store.ApplyInterest(ulong.MaxValue);
            // A metal floor ten levels up, so it is above the terrain wherever the cell is.
            int cx = BuildGrid.CellsX / 2, cz = BuildGrid.CellsZ / 2;
            Assert.IsTrue(BuildGrid.TryNormalize(BuildPieceType.Floor, cx, 10, cz, 0, out BuildPieceShape shape));
            store.ApplyPiece(new BuildPieceRecord { Id = 5, Shape = shape, Material = BuildMaterialType.Metal, Owner = 1 }, 1);
            Assert.AreEqual(1, store.Count);
            float x = BuildGrid.CellMinX(cx) + BuildGrid.CellSize * 0.5f;
            float z = BuildGrid.CellMinZ(cz) + BuildGrid.CellSize * 0.5f;
            float y = BuildGrid.LevelBase(10);
            Assert.IsTrue(SurfaceProbe.TryFind(new Vector3(x, y, z), store, out SurfaceMaterial m));
            Assert.AreEqual(SurfaceMaterial.Metal, m);
        }
    }
}
