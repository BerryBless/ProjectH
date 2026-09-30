using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Same numbers as the server's test catalog (TestWeapons): slot 0 automatic, 3-tick interval,
    // 6 rounds, 30-tick reload; slot 1 semi-automatic, 15-tick interval, 2 rounds, 60-tick reload.
    public class WeaponStateTests
    {
        private static WeaponInfo[] Catalog() => new[]
        {
            new WeaponInfo { WeaponId = 1, Name = "Test Auto", Damage = 30, FireIntervalTicks = 3, MagazineSize = 6, ReloadTicks = 30, Range = 100f, Automatic = true },
            new WeaponInfo { WeaponId = 2, Name = "Test Semi", Damage = 90, FireIntervalTicks = 15, MagazineSize = 2, ReloadTicks = 60, Range = 300f, Automatic = false },
        };

        private uint _seq;

        // NUnit reuses one fixture instance for every test in the class.
        [SetUp]
        public void ResetSeq() => _seq = 0;

        private bool Step(WeaponState state, InputButtons buttons) => state.Step(++_seq, buttons);

        [Test]
        public void Auto_Held_FiresOncePerInterval()
        {
            var state = new WeaponState(Catalog());
            int shots = 0;
            for (int i = 0; i < 9; i++)
            {
                bool fired = Step(state, InputButtons.Fire);
                Assert.AreEqual(i % 3 == 0, fired, "step " + i);
                if (fired) shots++;
            }
            Assert.AreEqual(3, shots);
            Assert.AreEqual(3, state.Ammo);
        }

        [Test]
        public void Auto_EmptyMagazine_Reloads_ThenFiresAgain()
        {
            var state = new WeaponState(Catalog());
            int shots = 0;
            // 6 shots at steps 0..15, reload from 15 to 45, then the next shot at step 45.
            for (int i = 0; i <= 45; i++)
            {
                if (Step(state, InputButtons.Fire)) shots++;
                if (i == 15) Assert.IsTrue(state.Reloading);
                if (i > 15 && i < 45) Assert.AreEqual(0, state.Ammo);
            }
            Assert.AreEqual(7, shots);
            Assert.IsFalse(state.Reloading);
            Assert.AreEqual(5, state.Ammo);
        }

        [Test]
        public void Semi_Held_FiresOnce()
        {
            var state = new WeaponState(Catalog());
            Assert.IsTrue(Step(state, InputButtons.Slot2 | InputButtons.Fire));
            for (int i = 0; i < 40; i++) Assert.IsFalse(Step(state, InputButtons.Fire));
            Assert.IsFalse(Step(state, InputButtons.None));
            Assert.IsTrue(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ReloadButton_And_Switch_FollowServerOrder()
        {
            var state = new WeaponState(Catalog());
            Assert.IsTrue(Step(state, InputButtons.Fire));
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.Slot2);                       // switching cancels the reload
            Assert.AreEqual(1, state.Slot);
            Assert.IsFalse(state.Reloading);
            Step(state, InputButtons.Slot1 | InputButtons.Slot2);  // both: ignored
            Assert.AreEqual(1, state.Slot);
            Step(state, InputButtons.Slot1);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual("Test Auto", state.Current.Name);
        }

        [Test]
        public void ApplyServer_MatchingValues_KeepsNewerLocalSteps()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Fire);   // seq 1: 5 left
            Step(state, InputButtons.None);
            Step(state, InputButtons.None);
            Step(state, InputButtons.Fire);   // seq 4: 4 left, not acked yet

            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 5 }, 1);

            Assert.AreEqual(4, state.Ammo);
        }

        [Test]
        public void ApplyServer_DifferentValues_TakesTheServers()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Fire);   // local: 5 left after seq 1

            // The server rejected that shot (e.g. its input was lost) and is reloading slot 1.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 1, Ammo = 1, ReloadRemainingTicks = 10 }, 1);

            Assert.AreEqual(1, state.Slot);
            Assert.AreEqual(1, state.Ammo);
            Assert.IsTrue(state.Reloading);
            Assert.AreEqual(10, state.ReloadRemainingSteps);
        }

        [Test]
        public void ApplyServer_Mismatch_SnapsOnce_AndReplaysNewerSteps()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Fire);   // seq 1: fires, 5 left
            Step(state, InputButtons.None);   // seq 2
            Step(state, InputButtons.None);   // seq 3
            Step(state, InputButtons.Fire);   // seq 4: fires, 4 left

            // The server did not fire seq 1 (input lost): it still has 6 after ack 1.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 1);
            Assert.AreEqual(5, state.Ammo);   // server 6 minus the shot fired locally after the ack

            // Later snapshots with the server's (still unfired) values must not snap again.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 2);
            Assert.AreEqual(5, state.Ammo);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 3);
            Assert.AreEqual(5, state.Ammo);
        }

        [Test]
        public void ApplyServer_SnapDuringReload_EndsReloadOnTheServersStep()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Fire);     // seq 1
            Step(state, InputButtons.Reload);   // seq 2
            for (int i = 0; i < 3; i++) Step(state, InputButtons.None);   // seq 3..5, unacked

            // After tick of seq 2 the server has 20 reload ticks left (and one round less than this copy).
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 4, ReloadRemainingTicks = 20 }, 2);
            Assert.IsTrue(state.Reloading);

            // Ticks of seq 3..22 are the 20 remaining ticks; the reload completes at the tick of seq 23.
            while (_seq < 22) Step(state, InputButtons.None);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.None);     // seq 23
            Assert.IsFalse(state.Reloading);
            Assert.AreEqual(6, state.Ammo);
        }

        [Test]
        public void ApplyServer_IgnoresAckZero_AndSlotOutsideLoadout()
        {
            var state = new WeaponState(Catalog());
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 1 }, 0);
            Assert.AreEqual(6, state.Ammo);

            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 7, Ammo = 1 }, 1);
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
        }

        [Test]
        public void Refill_RestoresFullLoadout()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Slot2 | InputButtons.Fire);
            state.Refill();
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
            Assert.IsFalse(state.Reloading);
        }
    }
}
