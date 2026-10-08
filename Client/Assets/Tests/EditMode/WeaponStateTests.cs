using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Same numbers as the server's test catalog (TestWeapons) and combat loadout (TestGameData.CombatLoadout):
    // slot 0 "Test Auto" (automatic, 3-tick interval, 6 rounds, 30-tick reload, Medium), slot 1 "Test Semi"
    // (15-tick interval, 2 rounds, 60-tick reload, Heavy), slot 2 empty; reserves Medium 60, Heavy 30.
    public class WeaponStateTests
    {
        private static WeaponInfo[] Catalog() => new[]
        {
            new WeaponInfo { WeaponId = 1, Name = "Test Auto", Damage = 30, FireIntervalTicks = 3, MagazineSize = 6, ReloadTicks = 30, Range = 100f, Automatic = true, AmmoType = AmmoType.Medium },
            new WeaponInfo { WeaponId = 2, Name = "Test Semi", Damage = 90, FireIntervalTicks = 15, MagazineSize = 2, ReloadTicks = 60, Range = 300f, Automatic = false, AmmoType = AmmoType.Heavy },
            new WeaponInfo { WeaponId = 3, Name = "Test Light", Damage = 10, FireIntervalTicks = 3, MagazineSize = 10, ReloadTicks = 15, Range = 50f, Automatic = true, AmmoType = AmmoType.Light },
        };

        private static InventoryState Loadout(int medium = 60, int heavy = 30) => new InventoryState
        {
            Slot0 = new InventorySlotState { WeaponId = 1, MagAmmo = 6 },
            Slot1 = new InventorySlotState { WeaponId = 2, MagAmmo = 2 },
            MediumAmmo = (ushort)medium,
            HeavyAmmo = (ushort)heavy,
        };

        private uint _seq;

        // NUnit reuses one fixture instance for every test in the class.
        [SetUp]
        public void ResetSeq() => _seq = 0;

        private bool Step(WeaponState state, InputButtons buttons) => state.Step(++_seq, buttons);

        private static WeaponState Armed(int medium = 60, int heavy = 30)
        {
            var state = new WeaponState(Catalog());
            state.ApplyInventory(Loadout(medium, heavy));
            return state;
        }

        [Test]
        public void New_IsEmptyHanded_AndNeverFires()
        {
            var state = new WeaponState(Catalog());
            Assert.IsFalse(state.HasWeapon);
            Assert.AreEqual(0, state.Ammo);
            for (int i = 0; i < 10; i++) Assert.IsFalse(Step(state, InputButtons.Fire | InputButtons.Reload));
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void Auto_Held_FiresOncePerInterval()
        {
            var state = Armed();
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
        public void Auto_EmptyMagazine_ReloadsFromTheReserve_ThenFiresAgain()
        {
            var state = Armed();
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
            Assert.AreEqual(54, state.Reserve);
        }

        [Test]
        public void EmptyReserve_NoReload()
        {
            var state = Armed(medium: 0);
            for (int i = 0; i <= 60; i++) Step(state, InputButtons.Fire);
            Assert.AreEqual(0, state.Ammo);
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void ShortReserve_FillsWhatItHas()
        {
            var state = Armed(medium: 2);
            for (int i = 0; i <= 45; i++) Step(state, InputButtons.Fire);   // 6 shots, then a reload of 2
            Assert.AreEqual(1, state.Ammo);                                 // 2 in, one fired at step 45
            Assert.AreEqual(0, state.Reserve);
        }

        [Test]
        public void Semi_Held_FiresOnce()
        {
            var state = Armed();
            Assert.IsTrue(Step(state, InputButtons.Slot2 | InputButtons.Fire));
            for (int i = 0; i < 40; i++) Assert.IsFalse(Step(state, InputButtons.Fire));
            Assert.IsFalse(Step(state, InputButtons.None));
            Assert.IsTrue(Step(state, InputButtons.Fire));
        }

        // Phase 12 final review C9: gated (aboard, in the air, vaulting) the input acts on nothing, but the held fire button
        // is followed like the server's FireHeld: landing with Fire held does not fire the semi-automatic weapon.
        [Test]
        public void Semi_FireHeldThroughTheLanding_DoesNotFire_UntilPressedAgain()
        {
            var state = Armed();
            Assert.IsFalse(Step(state, InputButtons.Slot2));
            Assert.AreEqual(1, state.Slot);
            for (int i = 0; i < 5; i++) Assert.IsFalse(state.Step(++_seq, InputButtons.Fire, actionsAllowed: false));
            Assert.AreEqual(2, state.Ammo);
            Assert.IsFalse(Step(state, InputButtons.Fire));    // landed, still held
            Assert.IsFalse(Step(state, InputButtons.None));
            Assert.IsTrue(Step(state, InputButtons.Fire));     // a new press
        }

        [Test]
        public void ReloadButton_And_Switch_FollowServerOrder()
        {
            var state = Armed();
            Assert.IsTrue(Step(state, InputButtons.Fire));
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.Slot2);                       // switching cancels the reload
            Assert.AreEqual(1, state.Slot);
            Assert.IsFalse(state.Reloading);
            Step(state, InputButtons.Slot1 | InputButtons.Slot2);  // several: ignored
            Assert.AreEqual(1, state.Slot);
            Step(state, InputButtons.Slot1);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual("Test Auto", state.Current.Name);
        }

        // D10 decision, same as the server: an empty slot can be selected, and nothing fires there.
        [Test]
        public void Slot3_Empty_CanBeSelected_ButNeverFires()
        {
            var state = Armed();
            Assert.IsFalse(Step(state, InputButtons.Slot3 | InputButtons.Fire));
            Assert.AreEqual(2, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ApplyInventory_FillsSlots_AndReserves()
        {
            var state = Armed();
            Assert.IsTrue(state.TryGetSlot(1, out WeaponInfo semi, out int rarity, out int ammo));
            Assert.AreEqual("Test Semi", semi.Name);
            Assert.AreEqual(0, rarity);
            Assert.AreEqual(2, ammo);
            Assert.IsFalse(state.TryGetSlot(2, out _, out _, out _));
            Assert.AreEqual(30, state.GetReserve(AmmoType.Heavy));

            var withLight = Loadout();
            withLight.Slot2 = new InventorySlotState { WeaponId = 3, Rarity = 4, MagAmmo = 7 };
            state.ApplyInventory(withLight);
            Assert.IsTrue(state.TryGetSlot(2, out WeaponInfo light, out rarity, out ammo));
            Assert.AreEqual("Test Light", light.Name);
            Assert.AreEqual(4, rarity);
            Assert.AreEqual(7, ammo);
        }

        // Review trap: the InventoryState (Reliable) and snapshots (Sequenced) arrive in any order. If an
        // InventoryState set the current slot's magazine, a later matching snapshot would never correct it,
        // so the current slot's magazine is left to the snapshot while the weapon in it is unchanged.
        [Test]
        public void ApplyInventory_LeavesTheCurrentMagazineToTheSnapshot_TakesTheOthers()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);   // local: 5 left in slot 0
            var older = Loadout();
            older.Slot0.MagAmmo = 6;          // an inventory from before that shot
            older.Slot1.MagAmmo = 1;
            state.ApplyInventory(older);

            Assert.AreEqual(5, state.Ammo);
            Assert.IsTrue(state.TryGetSlot(1, out _, out _, out int semiAmmo));
            Assert.AreEqual(1, semiAmmo);
        }

        [Test]
        public void ApplyInventory_NewWeaponInTheCurrentSlot_TakesItsMagazine_AndStopsTheReload()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);

            var swapped = Loadout();
            swapped.Slot0 = new InventorySlotState { WeaponId = 3, Rarity = 1, MagAmmo = 4 };   // swap pickup
            state.ApplyInventory(swapped);

            Assert.AreEqual("Test Light", state.Current.Name);
            Assert.AreEqual(4, state.Ammo);
            Assert.IsFalse(state.Reloading);

            var dropped = Loadout();
            dropped.Slot0 = default;                                                          // G
            state.ApplyInventory(dropped);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ApplyServer_MatchingValues_KeepsNewerLocalSteps()
        {
            var state = Armed();
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
            var state = Armed();
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
            var state = Armed();
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
            var state = Armed();
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
            Assert.AreEqual(58, state.Reserve);
        }

        // A replay that re-runs a reload must not take its rounds from the reserve twice.
        [Test]
        public void ApplyServer_ReplayAcrossAReload_TakesTheReserveOnce()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);                                 // seq 1: 5 left
            Step(state, InputButtons.Reload);                               // seq 2: reload until step 31
            for (int i = 0; i < 31; i++) Step(state, InputButtons.None);    // seq 3..33: the reload finished locally
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(59, state.Reserve);

            // Ack 1 with a different magazine forces a replay of seq 2..33, reload included.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 4 }, 1);
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(58, state.Reserve);   // the server's 4 + 2 from the reserve, once
        }

        // An InventoryState is authoritative for the reserves when it arrives: a later mismatch replay that starts
        // from an older ack must not bring back the reserves from before it (e.g. an ammo pickup undone).
        [Test]
        public void ApplyInventory_NewReserve_SurvivesALaterMismatchReplay()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);                         // seq 1: 5 left, reserve 60 recorded
            state.ApplyInventory(Loadout(medium: 120));             // picked up 60
            Assert.AreEqual(120, state.Reserve);

            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 1);   // mismatch: replay from ack 1
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(120, state.Reserve);
        }

        // The same, with a reload predicted after the ack: the replay re-runs it from the new reserves and takes its
        // rounds once (server: 60 - 2 for the reload + 60 picked up = 118).
        [Test]
        public void ApplyInventory_AfterALocalReload_ReplayTakesTheReserveOnce()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);                                 // seq 1: 5 left
            Step(state, InputButtons.Reload);                               // seq 2: reload until step 31
            for (int i = 0; i < 31; i++) Step(state, InputButtons.None);    // seq 3..33: finished locally, reserve 59
            state.ApplyInventory(Loadout(medium: 119));                     // picked up 60 after the reload
            Assert.AreEqual(119, state.Reserve);

            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 4 }, 1);   // replay of seq 2..33, reload included
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(118, state.Reserve);
        }

        [Test]
        public void ApplyServer_IgnoresAckZero_AndSlotOutsideTheInventory()
        {
            var state = Armed();
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 1 }, 0);
            Assert.AreEqual(6, state.Ammo);

            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 3, Ammo = 1 }, 1);
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
        }

        [Test]
        public void ApplyServer_OnAnEmptySlot_DoesNotTouchAWeapon()
        {
            var state = Armed();
            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 2, Ammo = 0 }, 1);   // the server is on slot 3
            Assert.AreEqual(2, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            var state = Armed();
            Step(state, InputButtons.Slot2 | InputButtons.Fire);
            state.Clear();
            Assert.AreEqual(0, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.AreEqual(0, state.GetReserve(AmmoType.Medium));
            Assert.IsFalse(state.Reloading);
        }

        // Phase 17 D13: Shells and Rockets are reserves like the first three (index = type - 1).
        private static WeaponInfo[] Phase17Catalog() => new[]
        {
            new WeaponInfo { WeaponId = 4, Name = "Test Shotgun", Damage = 11, FireIntervalTicks = 27, MagazineSize = 5, ReloadTicks = 30, Range = 35f, AmmoType = AmmoType.Shells, Pellets = 8, SpreadDegrees = 6f },
            new WeaponInfo { WeaponId = 6, Name = "Test Rocket", Damage = 75, FireIntervalTicks = 30, MagazineSize = 1, ReloadTicks = 90, Range = 160f, AmmoType = AmmoType.Rockets, Pellets = 1, Projectile = ProjectileKind.Rocket },
        };

        [Test]
        public void FiveAmmoTypes_ApplyInventory_TakesShellsAndRockets()
        {
            var state = new WeaponState(Phase17Catalog());
            state.ApplyInventory(new InventoryState
            {
                Slot0 = new InventorySlotState { WeaponId = 4, MagAmmo = 5 },
                Slot1 = new InventorySlotState { WeaponId = 6, MagAmmo = 1 },
                LightAmmo = 1, MediumAmmo = 2, HeavyAmmo = 3, ShellsAmmo = 10, RocketsAmmo = 2,
            });
            Assert.AreEqual(1, state.GetReserve(AmmoType.Light));
            Assert.AreEqual(2, state.GetReserve(AmmoType.Medium));
            Assert.AreEqual(3, state.GetReserve(AmmoType.Heavy));
            Assert.AreEqual(10, state.GetReserve(AmmoType.Shells));
            Assert.AreEqual(2, state.GetReserve(AmmoType.Rockets));
            Assert.AreEqual(10, state.Reserve);   // the shotgun in hand
            Assert.AreEqual(0, state.GetReserve((AmmoType)6));   // outside the five
            state.Clear();
            Assert.AreEqual(0, state.GetReserve(AmmoType.Shells));
            Assert.AreEqual(0, state.GetReserve(AmmoType.Rockets));
        }

        [Test]
        public void Rockets_EmptyMagazineReloadsFromTheRocketReserve()
        {
            var state = new WeaponState(Phase17Catalog());
            state.ApplyInventory(new InventoryState
            {
                Slot0 = new InventorySlotState { WeaponId = 6, MagAmmo = 1 },
                RocketsAmmo = 2,
            });
            Assert.IsTrue(Step(state, InputButtons.Fire));    // the only rocket; the empty magazine starts the reload
            Assert.IsTrue(state.Reloading);
            for (int i = 0; i < 91; i++) Step(state, InputButtons.None);
            Assert.AreEqual(1, state.Ammo);
            Assert.AreEqual(1, state.GetReserve(AmmoType.Rockets));
        }

        // Phase 17: in a match state the server launches no projectile in (Starting, Finished, Closing), a rocket shot is not
        // predicted: no round, no fire interval, no automatic reload; the press is spent like the server's FireHeld.
        [Test]
        public void Rocket_LaunchBlocked_PredictsNoShot_HitscanStillFires()
        {
            var state = new WeaponState(Phase17Catalog());
            state.ApplyInventory(new InventoryState
            {
                Slot0 = new InventorySlotState { WeaponId = 6, MagAmmo = 1 },
                Slot1 = new InventorySlotState { WeaponId = 4, MagAmmo = 5 },
                RocketsAmmo = 2, ShellsAmmo = 10,
            });
            Assert.IsFalse(state.Step(++_seq, InputButtons.Fire, true, false));
            Assert.AreEqual(1, state.Ammo);
            Assert.IsFalse(state.Reloading);
            Assert.IsFalse(state.Step(++_seq, InputButtons.Fire, true, true));   // still held: a semi-automatic needs a new press
            Assert.IsFalse(state.Step(++_seq, InputButtons.None, true, true));
            Assert.IsTrue(state.Step(++_seq, InputButtons.Fire, true, true));    // allowed again: fires at once (no interval spent)
            Assert.AreEqual(0, state.Ammo);

            Step(state, InputButtons.Slot2);
            Assert.IsTrue(state.Step(++_seq, InputButtons.Fire, true, false));   // a hitscan weapon is not affected
            Assert.AreEqual(4, state.Ammo);
        }

        // The flag is part of the history: a mismatch replay re-runs the blocked step as blocked.
        [Test]
        public void Rocket_LaunchBlocked_IsReplayedAsBlocked()
        {
            var state = new WeaponState(Phase17Catalog());
            state.ApplyInventory(new InventoryState { Slot0 = new InventorySlotState { WeaponId = 6, MagAmmo = 1 }, RocketsAmmo = 2 });
            Step(state, InputButtons.None);                                       // seq 1
            state.Step(++_seq, InputButtons.Fire, true, false);                  // seq 2: blocked
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 0 }, 1);  // mismatch at ack 1 (server says 0): replay seq 2
            Assert.AreEqual(0, state.Ammo);
            Assert.IsFalse(state.Reloading);   // the blocked step started no automatic reload in the replay either
        }

        [Test]
        public void LaunchAllowed_FollowsTheServersMatchStates()
        {
            Assert.IsTrue(WeaponState.LaunchAllowed(false, MatchFlowState.Finished));   // no MatchState: dev sandbox
            Assert.IsTrue(WeaponState.LaunchAllowed(true, MatchFlowState.WaitingForPlayers));
            Assert.IsTrue(WeaponState.LaunchAllowed(true, MatchFlowState.Playing));
            Assert.IsTrue(WeaponState.LaunchAllowed(true, MatchFlowState.FinalPhase));
            Assert.IsFalse(WeaponState.LaunchAllowed(true, MatchFlowState.Starting));
            Assert.IsFalse(WeaponState.LaunchAllowed(true, MatchFlowState.Finished));
            Assert.IsFalse(WeaponState.LaunchAllowed(true, MatchFlowState.Closing));
        }

        // A mismatch replay restores the Shells reserve recorded at the ack (a three-type history would leave it as is),
        // and a pickup between the two is counted once.
        [Test]
        public void Shells_SurviveAMismatchReplay_AndAPickupCountsOnce()
        {
            var state = new WeaponState(Phase17Catalog());
            var loadout = new InventoryState { Slot0 = new InventorySlotState { WeaponId = 4, MagAmmo = 5 }, ShellsAmmo = 10 };
            state.ApplyInventory(loadout);
            Step(state, InputButtons.Fire);                                 // seq 1: 4 left
            Step(state, InputButtons.None);                                 // seq 2: trigger released
            Step(state, InputButtons.Reload);                               // seq 3: reload until step 33
            for (int i = 0; i < 31; i++) Step(state, InputButtons.None);    // finished locally: 5 in, shells 9
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual(9, state.GetReserve(AmmoType.Shells));

            loadout.ShellsAmmo = 19;                                        // picked up 10 after the reload
            state.ApplyInventory(loadout);
            Assert.AreEqual(19, state.GetReserve(AmmoType.Shells));

            // Ack 1 with a different magazine (3): replay seq 2.. from the moved record, the reload takes 2 shells.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 3 }, 1);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual(18, state.GetReserve(AmmoType.Shells));   // 20 at the ack (10 + 10 picked up) - 2 put in
        }
    }
}
