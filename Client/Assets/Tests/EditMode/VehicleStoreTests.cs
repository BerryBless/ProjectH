using System;
using System.Text;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Client.Game.Audio;
using ProjectH.Client.Qa;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Phase 19 D4, D11, D15: VehicleStates on the client (old ticks, the 1 s hide, interpolation, seats at the render tick), the
    // vehicle sounds and their baseline rule, the footstep skip for seated players, the seated pose and the QA vehicle fields.
    public class VehicleStoreTests
    {
        private const int SimHz = 30;

        private static VehicleRecord Car(byte id, float x, float z = 0f, float heading = 0f, float speed = 0f, ushort driver = 0,
            ushort passenger = 0, VehicleState state = VehicleState.Active, ushort health = VehiclePrompt.MaxHealth) =>
            new VehicleRecord { Id = id, State = state, Position = new Num.Vector3(x, 0f, z), Heading = heading, Speed = speed,
                Driver = driver, Passenger = passenger, Health = health };

        private static bool Apply(VehicleStore store, uint tick, float now, params VehicleRecord[] records) =>
            store.Apply(tick, 0, records, records.Length, now, SimHz);

        [Test]
        public void APacketNotNewerThanTheLastApplied_IsDropped()
        {
            var store = new VehicleStore();
            Assert.IsTrue(Apply(store, 10, 0f, Car(1, 0f)));
            Assert.IsFalse(Apply(store, 8, 0.1f, Car(1, 5f)));
            Assert.IsFalse(Apply(store, 10, 0.1f, Car(1, 5f)));
            Assert.AreEqual(2, store.DroppedOld);
            Assert.AreEqual(0f, store.Latest[0].Position.X);
            Assert.IsTrue(Apply(store, 12, 0.2f, Car(1, 5f)));
            Assert.AreEqual(5f, store.Latest[0].Position.X);
        }

        [Test]
        public void Interpolates_AtTheRenderTick_WithoutExtrapolating()
        {
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f, heading: 350f));
            Apply(store, 12, 0.07f, Car(1, 2f, heading: 10f));
            Assert.IsTrue(store.TrySample(0, 11, out VehicleRecord mid));
            Assert.AreEqual(1f, mid.Position.X, 1e-4f);
            Assert.AreEqual(0f, Math.Min(mid.Heading, 360f - mid.Heading), 1e-3f);   // the short way round 0
            store.TrySample(0, 20, out VehicleRecord late);
            Assert.AreEqual(2f, late.Position.X, 1e-4f);
            store.TrySample(0, 5, out VehicleRecord early);
            Assert.AreEqual(0f, early.Position.X, 1e-4f);
        }

        [Test]
        public void AVehicleMissingForASecond_IsHidden_AndComesBackAsABaseline()
        {
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f), Car(2, 10f));
            Apply(store, 12, 0.5f, Car(1, 0f));                 // 2 left the interest range
            store.Expire(1.2f);
            Assert.AreEqual(1, store.VisibleCount);             // 2 last seen at 0 s
            // 2 comes back wrecked: a baseline, so no explosion.
            Apply(store, 40, 1.3f, Car(1, 0f), Car(2, 10f, state: VehicleState.Wrecked, health: 0));
            Assert.AreEqual(2, store.VisibleCount);
            Assert.AreEqual(0, store.ChangeCount);
            // No packet at all for a second: everything goes, and nobody counts as seated any more.
            Apply(store, 42, 1.4f, Car(1, 0f, driver: 7));
            Assert.IsTrue(store.IsSeated(7));
            store.Expire(2.5f);
            Assert.AreEqual(0, store.VisibleCount);
            Assert.AreEqual(0, store.Latest.Length);
            Assert.IsFalse(store.IsSeated(7));
        }

        [Test]
        public void Changes_FollowTheBaselineRule()
        {
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f, driver: 5));        // first record: baseline
            Assert.AreEqual(0, store.ChangeCount);
            Apply(store, 12, 0.07f, Car(1, 0f, driver: 5, passenger: 6));
            Assert.AreEqual(1, store.ChangeCount);
            Assert.AreEqual(VehicleSounds.Entered, store.Change(0).Sounds);
            Apply(store, 14, 0.14f, Car(1, 0f, passenger: 6));
            Assert.AreEqual(VehicleSounds.Exited, store.Change(0).Sounds);
            Apply(store, 16, 0.2f, Car(1, 0f, state: VehicleState.Wrecked, health: 0));
            Assert.AreEqual(VehicleSounds.Wrecked, store.Change(0).Sounds);   // its occupant's exit is not heard

            // A join or resume: the next record is a baseline again.
            store.Reset();
            Assert.IsTrue(Apply(store, 5, 0.3f, Car(1, 0f, driver: 9)));       // the tick comparison restarts too
            Assert.AreEqual(0, store.ChangeCount);
        }

        [Test]
        public void ImpactSounds_OnlyForASpeedDropBeyondBraking()
        {
            VehicleRecord moving = Car(1, 0f, speed: 15f);
            Assert.AreEqual(VehicleSounds.Impact, AudioEventRules.VehicleSoundsFor(moving, Car(1, 0f, speed: 0.5f), false, 2f / SimHz));
            // Full braking for two ticks takes off 1.6 m/s.
            Assert.AreEqual(VehicleSounds.None, AudioEventRules.VehicleSoundsFor(moving, Car(1, 0f, speed: 13.4f), false, 2f / SimHz));
            // Too slow before, a gap too long, or a baseline: nothing.
            Assert.AreEqual(VehicleSounds.None, AudioEventRules.VehicleSoundsFor(Car(1, 0f, speed: 4f), Car(1, 0f), false, 2f / SimHz));
            Assert.AreEqual(VehicleSounds.None, AudioEventRules.VehicleSoundsFor(moving, Car(1, 0f), false, 1f));
            Assert.AreEqual(VehicleSounds.None, AudioEventRules.VehicleSoundsFor(moving, Car(1, 0f), true, 2f / SimHz));
            // Reversing into a wall counts the same.
            Assert.AreEqual(VehicleSounds.Impact,
                AudioEventRules.VehicleSoundsFor(Car(1, 0f, speed: -6f), Car(1, 0f), false, 2f / SimHz));
        }

        [Test]
        public void ASeat_CountsFromTheRenderTick()
        {
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f));
            Apply(store, 12, 0.07f, Car(1, 0f, driver: 5));
            Assert.IsTrue(store.IsSeated(5));                   // the newest packet already says so
            store.Render(11);
            Assert.IsFalse(store.TrySeatAt(5, out _, out _));   // but it is drawn sitting only from tick 12
            store.Render(12);
            Assert.IsTrue(store.TrySeatAt(5, out VehicleRecord vehicle, out int seat));
            Assert.AreEqual(VehicleSettings.DriverSeat, seat);
            Assert.AreEqual(1, vehicle.Id);
        }

        [Test]
        public void MyOwnSeat_UsesTheNewestPacketsVehicle_BeforeTheRenderTickShowsMe()
        {
            // Phase 19 review: right after boarding the sample at the render tick does not name us yet.
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f), Car(2, 20f));
            Apply(store, 12, 0.07f, Car(1, 2f, passenger: 5), Car(2, 20f));
            store.Render(11);
            Assert.IsFalse(store.TrySeatAt(5, out _, out _));   // remote players still sit from the render tick on
            Assert.IsTrue(store.TryGetOwnSeat(5, out VehicleRecord drawn, out int seat));
            Assert.AreEqual(1, drawn.Id);
            Assert.AreEqual(1f, drawn.Position.X, 1e-4f);       // this frame's sample of that vehicle, not the newest record
            Assert.AreEqual(VehicleSettings.PassengerSeat, seat);
            Assert.IsFalse(store.TryGetOwnSeat(6, out _, out _));
            store.Render(12);
            Assert.IsTrue(store.TryGetOwnSeat(5, out drawn, out seat));
            Assert.AreEqual(2f, drawn.Position.X, 1e-4f);
        }

        [Test]
        public void AnEndedPrediction_EasesFromWhereItWasDrawn_ToTheSample()
        {
            // Phase 19 review: getting out while moving, the car was drawn at its predicted place, ahead of the sample.
            const float Frame = 1f / 60f;
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f, driver: 5));
            Apply(store, 12, 0.07f, Car(1, 2f, driver: 5));
            store.Render(11);                                   // sample at x = 1
            store.OverrideDrawn(1, new VehicleMove { Position = new Num.Vector3(4f, 0f, 0f) });
            store.Render(11);                                   // the next frame: the prediction has ended
            store.DrawHandoff(Frame);
            Assert.IsTrue(store.TryGetDrawn(0, out VehicleRecord first));
            Assert.AreEqual(4f, first.Position.X, 1e-4f);       // no jump back on the first frame
            Assert.AreEqual(3f, store.HandoffOffset.X, 1e-4f);
            float last = first.Position.X;
            for (int i = 0; i < 10; i++)
            {
                store.Render(11);
                store.DrawHandoff(Frame);
                store.TryGetDrawn(0, out VehicleRecord r);
                Assert.Less(r.Position.X, last);
                Assert.Greater(r.Position.X, 1f);
                last = r.Position.X;
            }
            for (int i = 0; i < 120; i++)
            {
                store.Render(11);
                store.DrawHandoff(Frame);
            }
            store.TryGetDrawn(0, out VehicleRecord settled);
            Assert.AreEqual(1f, settled.Position.X, 1e-4f);     // ended: the sample as it is
            Assert.AreEqual(0f, store.HandoffOffset.X);

            // Farther than MaxHandoffOffset is no prediction lead: not smoothed.
            store.Render(11);
            store.OverrideDrawn(1, new VehicleMove { Position = new Num.Vector3(50f, 0f, 0f) });
            store.Render(11);
            store.DrawHandoff(Frame);
            store.TryGetDrawn(0, out VehicleRecord far);
            Assert.AreEqual(1f, far.Position.X, 1e-4f);

            // A reset forgets a pending handoff.
            store.Render(11);
            store.OverrideDrawn(1, new VehicleMove { Position = new Num.Vector3(4f, 0f, 0f) });
            store.Reset();
            Apply(store, 10, 1f, Car(1, 0f));
            store.Render(10);
            store.DrawHandoff(Frame);
            store.TryGetDrawn(0, out VehicleRecord after);
            Assert.AreEqual(0f, after.Position.X, 1e-4f);
            Assert.AreEqual(0f, store.HandoffOffset.X);
        }

        [Test]
        public void AnEndedPrediction_OfAMovingCar_NeverGoesBackward()
        {
            // Got out at 15 m/s: the car keeps rolling in the samples while the prediction was 0.25 s ahead of the render tick.
            const float Speed = 15f;
            const float Lead = 0.25f;
            const double FrameTicks = SimHz / 60.0;
            var store = new VehicleStore();
            for (uint tick = 10; tick <= 24; tick += 2)   // SampleCapacity (8) records
                Apply(store, tick, (tick - 10) / (float)SimHz, Car(1, Speed * (tick - 10) / SimHz, speed: Speed, driver: 5));
            double renderTick = 12;
            store.Render(renderTick);
            Assert.IsTrue(store.TryGetDrawn(0, out VehicleRecord sample));
            store.OverrideDrawn(1, new VehicleMove { Position = new Num.Vector3(sample.Position.X + Speed * Lead, 0f, 0f) });
            float last = sample.Position.X + Speed * Lead;
            for (int i = 0; i < 20; i++)   // up to render tick 22, inside the samples
            {
                renderTick += FrameTicks;
                store.Render(renderTick);
                store.DrawHandoff(1f / 60f);
                store.TryGetDrawn(0, out VehicleRecord r);
                Assert.GreaterOrEqual(r.Position.X, last - 1e-4f, "frame " + i);
                last = r.Position.X;
            }
        }

        [Test]
        public void TheDrivenVehicle_IsDrawnWhereItIsPredicted()
        {
            var store = new VehicleStore();
            Apply(store, 10, 0f, Car(1, 0f, driver: 5, passenger: 6));
            store.Render(10);
            store.OverrideDrawn(1, new VehicleMove { Position = new Num.Vector3(3f, 0f, 0f), Heading = 90f, Speed = 4f });
            Assert.IsTrue(store.TrySeatAt(6, out VehicleRecord drawn, out int seat));
            Assert.AreEqual(3f, drawn.Position.X);
            Assert.AreEqual(90f, drawn.Heading);
            Assert.AreEqual(VehicleSettings.PassengerSeat, seat);
        }

        [Test]
        public void NonFiniteRecords_AreIgnored_AndThePoolNeverGrows()
        {
            var store = new VehicleStore();
            var bad = Car(1, 0f);
            bad.Position.X = float.NaN;
            Apply(store, 10, 0f, bad);
            Assert.AreEqual(0, store.VisibleCount);
            for (uint t = 0; t < 40; t++)
            {
                byte id = (byte)(1 + t % 20);
                Apply(store, 20 + t, t * 0.07f, Car(id, 0f));
            }
            Assert.LessOrEqual(store.VisibleCount, VehicleSettings.MaxVehicles);
        }

        [Test]
        public void SeatedPlayers_MakeNoFootsteps_AndTheExitJumpIsNoStep()
        {
            var model = new FootstepModel();
            float t = 0f;
            Num.Vector3 feet = Num.Vector3.Zero;
            int Sample(bool seated, float step)
            {
                model.BeginFrame();
                feet.X += step;
                t += 0.1f;
                FootstepGait gait = model.Sample(7, feet, MovementMode.Ground, false, FootstepModel.Audible(true, seated), t, 0.1f);
                model.EndFrame();
                return gait == FootstepGait.None ? 0 : 1;
            }
            int walking = 0;
            for (int i = 0; i < 20; i++) walking += Sample(false, 0.4f);   // 4 m/s on foot
            Assert.Greater(walking, 0);
            int seated = 0;
            for (int i = 0; i < 20; i++) seated += Sample(true, 0.8f);     // 8 m/s in a seat: below the teleport guard
            Assert.AreEqual(0, seated);
            Assert.AreEqual(0, Sample(false, 2f));                         // the 2 m exit: a new starting point
            Assert.IsFalse(FootstepModel.Audible(false, false));
            Assert.IsTrue(FootstepModel.Audible(true, false));
        }

        [Test]
        public void SeatedPose_IsUpright_AndNeverHit()
        {
            PlayerPose pose = PlayerPose.For(MovementMode.Crouch, true, true, true);
            Assert.IsTrue(pose.Seated);
            Assert.AreEqual(PlayerPose.SeatedBody, pose.BodyHeight);
            Assert.AreEqual(0f, pose.Lean);
            Assert.IsFalse(PlayerPose.For(MovementMode.Ground, false, false, true).Seated);   // dead: lying, not sitting
            Assert.IsFalse(PlayerPose.For(MovementMode.Ground, false, true).Seated);
        }

        [Test]
        public void QaStatus_VehicleFields_OnlyWhenSet()
        {
            var map = new QaMapStatus { MinimapSelfWorldX = float.NaN, MinimapSelfWorldZ = float.NaN, ZoneCenterWorldX = float.NaN,
                ZoneCenterWorldZ = float.NaN, ZoneRadiusWorld = float.NaN, LootPrompt = "none", Projectiles = 0 };
            var sb = new StringBuilder();
            QaResponses.AppendStatus(sb, "qa1", true, true, "InGame", false, false, true, 100, 60, 42, "Weapon", "none", false, map);
            StringAssert.EndsWith("\"projectiles\":0}", sb.ToString());

            map.Vehicle = new QaVehicleStatus { Seated = true, VehicleId = 3, Seat = 0, Speed = 12.345f, Health = 380, VisibleVehicles = 2 };
            sb.Clear();
            QaResponses.AppendStatus(sb, "qa1", true, true, "InGame", false, false, true, 100, 60, 42, "Weapon", "none", false, map);
            StringAssert.EndsWith("\"projectiles\":0,\"vehicle\":{\"seated\":true,\"vehicleId\":3,\"seat\":0,\"speed\":12.35,\"health\":380," +
                                  "\"visibleVehicles\":2}}", sb.ToString());
        }
    }
}
