using System.Text;
using NUnit.Framework;
using ProjectH.Client.Game.Audio;
using ProjectH.Client.Qa;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 18 D4, D6-D9, D12: event -> sound mapping, the source table (one event = one sound), the baseline rule of every
    // state-change sound, the projectile resend rule and the one-collapse rule; and the QA audio JSON.
    public class AudioEventRulesTests
    {
        private const ushort Me = 7;
        private const int Hz = 30;

        // 기능: 시험용 무기 정보를 만든다.
        // 입력: ammo - 탄, automatic - 자동, pellets - 산탄 수, projectile - 투사체, id - 무기 id.
        // 출력: 무기 정보.
        private static WeaponInfo Weapon(AmmoType ammo, bool automatic = false, byte pellets = 1, ProjectileKind projectile = ProjectileKind.None, byte id = 1) =>
            new WeaponInfo { WeaponId = id, AmmoType = ammo, Automatic = automatic, Pellets = pellets, Projectile = projectile };

        [Test]
        public void Gunshot_FollowsTheWeaponsProperties()
        {
            Assert.AreEqual(SoundKind.GunAR, AudioCatalog.GunshotFor(Weapon(AmmoType.Medium, true)));
            Assert.AreEqual(SoundKind.GunSniper, AudioCatalog.GunshotFor(Weapon(AmmoType.Heavy)));
            Assert.AreEqual(SoundKind.GunSMG, AudioCatalog.GunshotFor(Weapon(AmmoType.Light, true)));
            Assert.AreEqual(SoundKind.GunPistol, AudioCatalog.GunshotFor(Weapon(AmmoType.Light)));
            Assert.AreEqual(SoundKind.GunShotgun, AudioCatalog.GunshotFor(Weapon(AmmoType.Shells, false, 8)));
            Assert.AreEqual(SoundKind.RocketLaunch, AudioCatalog.GunshotFor(Weapon(AmmoType.Rockets, false, 1, ProjectileKind.Rocket)));
        }

        [Test]
        public void GunshotForId_UnknownIdOrNoCatalog_IsTheDefault()
        {
            var catalog = new[] { Weapon(AmmoType.Heavy, id: 2), Weapon(AmmoType.Light, true, id: 3) };
            Assert.AreEqual(SoundKind.GunSniper, AudioCatalog.GunshotForId(catalog, 2));
            Assert.AreEqual(SoundKind.GunSMG, AudioCatalog.GunshotForId(catalog, 3));
            Assert.AreEqual(SoundKind.GunAR, AudioCatalog.GunshotForId(catalog, 99));
            Assert.AreEqual(SoundKind.GunAR, AudioCatalog.GunshotForId(null, 2));
        }

        [Test]
        public void DamageFlags_PickShieldHealthOrBreak()
        {
            Assert.AreEqual(SoundKind.HealthHit, AudioEventRules.DamageSound(new DamageTaken { Flags = 0 }));
            Assert.AreEqual(SoundKind.ShieldHit, AudioEventRules.DamageSound(new DamageTaken { Flags = DamageTaken.ShieldHitFlag }));
            Assert.AreEqual(SoundKind.ShieldBreak,
                AudioEventRules.DamageSound(new DamageTaken { Flags = DamageTaken.ShieldHitFlag | DamageTaken.ShieldBrokenFlag }));
        }

        [Test]
        public void SourceTable_OurShotsAndBuildsAreNotHeardTwice()
        {
            // Our ShotFired is ignored (the prediction sounded); another player's is heard.
            Assert.IsFalse(AudioEventRules.RemoteShotSounds(Me, Me));
            Assert.IsTrue(AudioEventRules.RemoteShotSounds(3, Me));
            // Our Placed / Edited records are silent (BuildResult Ok / the confirmed edit sounded).
            Assert.IsFalse(AudioEventRules.OthersBuildSounds(Me, Me));
            Assert.IsTrue(AudioEventRules.OthersBuildSounds(3, Me));
        }

        [Test]
        public void Launch_OurRocketIsSilent_GrenadeAlwaysSounds_OurGrenadeIs2D()
        {
            const uint join = 1000;
            var rocket = new ProjectileSpawned { Id = 1, Kind = ProjectileKind.Rocket, OwnerId = Me, StartTick = join + 5 };
            Assert.IsFalse(AudioEventRules.LaunchSound(rocket, Me, join, out _, out _));
            rocket.OwnerId = 3;
            Assert.IsTrue(AudioEventRules.LaunchSound(rocket, Me, join, out SoundKind kind, out bool own));
            Assert.AreEqual(SoundKind.RocketLaunch, kind);
            Assert.IsFalse(own);

            var grenade = new ProjectileSpawned { Id = 2, Kind = ProjectileKind.Grenade, OwnerId = Me, StartTick = join + 5 };
            Assert.IsTrue(AudioEventRules.LaunchSound(grenade, Me, join, out kind, out own));
            Assert.AreEqual(SoundKind.GrenadeThrow, kind);
            Assert.IsTrue(own);
        }

        [Test]
        public void Launch_OwnerZeroOrStartAtTheJoinTick_IsAResend()
        {
            // The server resends live projectiles with StartTick = its last finished tick = the join answer's ServerTick; a live
            // launch carries the tick being simulated (ServerTick + 1).
            const uint join = 1000;
            var grenade = new ProjectileSpawned { Id = 2, Kind = ProjectileKind.Grenade, OwnerId = 0, StartTick = join + 1 };
            Assert.IsFalse(AudioEventRules.LaunchSound(grenade, Me, join, out _, out _));
            grenade.OwnerId = 3;
            grenade.StartTick = join;
            Assert.IsFalse(AudioEventRules.LaunchSound(grenade, Me, join, out _, out _));        // resend
            grenade.StartTick = join - 40;
            Assert.IsFalse(AudioEventRules.LaunchSound(grenade, Me, join, out _, out _));        // resend of an older launch
            grenade.StartTick = join + 1;
            Assert.IsTrue(AudioEventRules.LaunchSound(grenade, Me, join, out _, out _));         // live, right after the join
        }

        [Test]
        public void HealthSound_OnlyWhenDamageGrows()
        {
            Assert.IsTrue(AudioEventRules.HealthDropped(10, 30));
            Assert.IsFalse(AudioEventRules.HealthDropped(30, 30));
            Assert.IsFalse(AudioEventRules.HealthDropped(30, 0));
        }

        [Test]
        public void Reboot_IsAGroundRespawnInAMatch_AfterDeathOrAtAStation()
        {
            Assert.IsTrue(AudioEventRules.IsReboot(true, false, MovementMode.Ground, true, MatchFlowState.Playing));
            Assert.IsTrue(AudioEventRules.IsReboot(false, true, MovementMode.Ground, true, MatchFlowState.FinalPhase));
            Assert.IsFalse(AudioEventRules.IsReboot(true, false, MovementMode.Transport, true, MatchFlowState.Playing));   // match start
            Assert.IsFalse(AudioEventRules.IsReboot(true, false, MovementMode.Ground, true, MatchFlowState.Finished));     // round reset
            Assert.IsFalse(AudioEventRules.IsReboot(true, false, MovementMode.Ground, false, MatchFlowState.WaitingForPlayers));   // dev sandbox
            Assert.IsFalse(AudioEventRules.IsReboot(false, false, MovementMode.Ground, true, MatchFlowState.Playing));
            Vector3 station = RebootStations.All[0];
            Assert.IsTrue(AudioEventRules.NearRebootStation(station + new Vector3(1.2f, 0f, 0f)));
            Assert.IsFalse(AudioEventRules.NearRebootStation(station + new Vector3(10f, 0f, 0f)));
        }

        [Test]
        public void Doors_JoinBaselineIsSilent_PredictionThenEqualServerIsOneSound_RemoteChangeSounds()
        {
            var doors = new DoorSoundTracker();
            doors.Reset();                       // disconnected: all closed heard
            doors.ArmBaseline(0f, false);        // join
            Assert.IsFalse(doors.OnServerState(0b101, 0.1f, out _, out _));   // the join state is a baseline
            Assert.IsFalse(doors.Poll(0b101, 0.1f, out _, out _));

            // Our predicted open of door 1: one sound now, none when the server agrees.
            Assert.IsTrue(doors.Poll(0b111, 0.2f, out int opened, out int closed));
            Assert.AreEqual(0b010, opened);
            Assert.AreEqual(0, closed);
            Assert.IsFalse(doors.OnServerState(0b111, 0.3f, out _, out _));
            Assert.IsFalse(doors.Poll(0b111, 0.3f, out _, out _));

            // Another player closes door 0: heard when its DoorStates arrives.
            Assert.IsTrue(doors.OnServerState(0b110, 0.5f, out opened, out closed));
            Assert.AreEqual(0, opened);
            Assert.AreEqual(0b001, closed);
            Assert.IsFalse(doors.Poll(0b110, 0.5f, out _, out _));
        }

        [Test]
        public void Doors_OneKeyPressIsOneSound_WhenAnotherDoorsStateArrivesFirst()
        {
            var doors = new DoorSoundTracker();
            doors.Reset();
            doors.ArmBaseline(0f, false);
            doors.OnServerState(0, 0f, out _, out _);

            Assert.IsTrue(doors.Poll(0b001, 1f, out int opened, out _));   // we open door 0 (predicted)
            Assert.AreEqual(0b001, opened);
            // Door 2 changes on the server first: ApplyServer drops our prediction, door 0 shows closed for a moment.
            Assert.IsTrue(doors.OnServerState(0b100, 1.1f, out opened, out int closed));
            Assert.AreEqual(0b100, opened);
            Assert.AreEqual(0, closed);
            Assert.IsFalse(doors.Poll(0b100, 1.1f, out _, out _));     // door 0 not heard closing
            // Our open is confirmed: silent.
            Assert.IsFalse(doors.OnServerState(0b101, 1.2f, out _, out _));
            Assert.IsFalse(doors.Poll(0b101, 1.2f, out _, out _));

            // Mirror case: we close door 0, an unrelated state shows it open again, then ours is confirmed closed.
            Assert.IsTrue(doors.Poll(0b100, 3f, out _, out closed));
            Assert.AreEqual(0b001, closed);
            Assert.IsFalse(doors.OnServerState(0b101, 3.1f, out _, out _));
            Assert.IsFalse(doors.Poll(0b101, 3.1f, out _, out _));
            Assert.IsFalse(doors.OnServerState(0b100, 3.2f, out _, out _));
        }

        [Test]
        public void Doors_APredictionTheServerNeverMakesIsHeardGoingBack()
        {
            var doors = new DoorSoundTracker();
            doors.Reset();
            doors.ArmBaseline(0f, false);
            doors.OnServerState(0, 0f, out _, out _);
            Assert.IsTrue(doors.Poll(0b010, 1f, out _, out _));
            Assert.IsFalse(doors.Poll(0, 1.5f, out _, out _));   // inside the window: not heard
            Assert.IsTrue(doors.Poll(0, 1f + DoorSoundTracker.PredictionSeconds + 0.1f, out _, out int closed));
            Assert.AreEqual(0b010, closed);
        }

        [Test]
        public void Doors_MatchStartBaselineExpires()
        {
            var doors = new DoorSoundTracker();
            doors.Reset();
            doors.ArmBaseline(0f, false);
            doors.OnServerState(0b001, 0f, out _, out _);
            doors.ArmBaseline(10f, true);                                  // the match start (CloseAll)
            Assert.IsFalse(doors.OnServerState(0, 10.1f, out _, out _));   // its DoorStates: silent
            Assert.IsFalse(doors.Poll(0, 10.1f, out _, out _));

            doors.ArmBaseline(20f, true);                                  // every door already closed: no DoorStates comes
            Assert.IsTrue(doors.OnServerState(0b100, 25f, out int opened, out _));   // a real change later is not swallowed
            Assert.AreEqual(0b100, opened);
        }

        [Test]
        public void Containers_FirstStateIsBaseline_OnlyASpawnedOneOpeningSounds()
        {
            var containers = new ContainerSoundTracker();
            Assert.AreEqual(0UL, containers.Apply(0b1111, 0b0011));   // join: two already open
            Assert.AreEqual(0b0100UL, containers.Apply(0b1111, 0b0111));
            Assert.AreEqual(0UL, containers.Apply(0b1111, 0b0111));   // a resend
            Assert.AreEqual(0UL, containers.Apply(0, 0));             // round reset
            Assert.AreEqual(0UL, containers.Apply(0b11, 0b01));       // spawned and opened in one state: not an opening we saw
            containers.Reset();
            Assert.AreEqual(0UL, containers.Apply(0b11, 0b11));       // resume
        }

        [Test]
        public void SupplyDrops_OnlyAFallingDropThatLandsSounds()
        {
            var drops = new SupplyDropSoundTracker();
            var list = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
            list[0] = new SupplyDropInfo { Id = 0, State = SupplyDropState.Landed };
            list[1] = new SupplyDropInfo { Id = 1, State = SupplyDropState.Falling };
            Assert.AreEqual(0, drops.Apply(list, 2));   // join: drop 0 already landed
            list[1].State = SupplyDropState.Landed;
            Assert.AreEqual(0b10, drops.Apply(list, 2));
            Assert.AreEqual(0, drops.Apply(list, 2));   // a resend
            list[1].State = SupplyDropState.Opened;
            Assert.AreEqual(0, drops.Apply(list, 2));
            Assert.AreEqual(0, drops.Apply(list, 0));   // round reset: empty list
            list[0] = new SupplyDropInfo { Id = 0, State = SupplyDropState.Landed };
            Assert.AreEqual(0, drops.Apply(list, 1));   // a new drop first seen landed
        }

        [Test]
        public void ZoneShrink_OnlyWhenTheClientSeesTheTickPass()
        {
            var watch = new ZoneShrinkWatch();
            var zone = new ZoneState { Phase = 1, ShrinkStartTick = 1000 };
            watch.OnZoneState(zone, 900);
            Assert.IsFalse(watch.Update(950, true));
            Assert.IsTrue(watch.Update(1001, true));
            Assert.IsFalse(watch.Update(1100, true));   // once

            watch.OnZoneState(zone, 1200);              // a join after the shrink started
            Assert.IsFalse(watch.Update(1201, true));

            var fresh = new ZoneShrinkWatch();          // a join mid-shrink, the tick known from the join answer
            fresh.OnZoneState(zone, 1150);
            Assert.IsFalse(fresh.Update(1152, true));
            var unknown = new ZoneShrinkWatch();        // no tick at all: never armed
            unknown.OnZoneState(zone, 0);
            Assert.IsFalse(unknown.Update(1001, true));

            watch.OnZoneState(new ZoneState { Phase = 2, ShrinkStartTick = 2000 }, 1900);
            Assert.IsFalse(watch.Update(2001, false));  // not in a match: silent
            watch.OnZoneState(new ZoneState { Phase = 0, ShrinkStartTick = 3000 }, 2900);
            Assert.IsFalse(watch.Update(3001, true));   // phase 0: no zone
        }

        [Test]
        public void Collapse_ManyPiecesMakeOneSound_TheNearest()
        {
            var collapse = new CollapseCollector();
            var listener = Vector3.Zero;
            collapse.Add(new Vector3(30f, 0f, 0f), listener);
            collapse.Add(new Vector3(5f, 0f, 0f), listener);
            collapse.Add(new Vector3(12f, 0f, 0f), listener);
            Assert.IsTrue(collapse.TryTake(out Vector3 nearest));
            Assert.AreEqual(5f, nearest.X);
            Assert.IsFalse(collapse.TryTake(out _));
        }

        [Test]
        public void ZoneDamage_TicksEverySecondWhileOutside()
        {
            var ticker = new ZoneDamageTicker();
            Assert.IsFalse(ticker.Update(true, 0f));
            Assert.IsFalse(ticker.Update(true, 0.5f));
            Assert.IsTrue(ticker.Update(true, 1f));
            Assert.IsTrue(ticker.Update(true, 2f));
            Assert.IsFalse(ticker.Update(false, 2.5f));
            Assert.IsFalse(ticker.Update(true, 3f));
        }

        [Test]
        public void Reload_SoundsOnTheRisingEdge()
        {
            var edge = new RisingEdge();
            Assert.IsTrue(edge.Update(true));
            Assert.IsFalse(edge.Update(true));
            Assert.IsFalse(edge.Update(false));
            Assert.IsTrue(edge.Update(true));
        }

        [Test]
        public void OneEvent_IsOneSound_ThroughTheMixer()
        {
            // audio_no_duplicate in miniature: a predicted shot (2D) and the server's own ShotFired (ignored) make one play.
            var mixer = new AudioMixerModel();
            mixer.Enqueue(new AudioEvent { Kind = SoundKind.GunAR, Spatial = false, Source = Me });
            if (AudioEventRules.RemoteShotSounds(Me, Me)) mixer.Enqueue(new AudioEvent { Kind = SoundKind.GunAR, Spatial = true, Source = Me });
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(1, mixer.Plays[(int)SoundKind.GunAR]);
        }

        [Test]
        public void QaStatus_WritesTheAudioObjectAfterProjectiles()
        {
            var sb = new StringBuilder();
            var map = new QaMapStatus
            {
                LootPrompt = "none",
                Projectiles = 0,
                Audio = new QaAudioStatus
                {
                    KindNames = new[] { "GunAR", "UiClick" },
                    Plays = new[] { 3, 1 },
                    Active = 2,
                    DroppedBudget = 4,
                    DroppedDuplicate = 5,
                    DroppedDistance = 6,
                    QueueOverflow = 7,
                },
            };
            QaResponses.AppendStatus(sb, "p", true, true, "game", false, false, true, 100, 60, 1, "Weapon", "none", true, map);
            string json = sb.ToString();
            StringAssert.Contains(
                "\"projectiles\":0,\"audio\":{\"plays\":{\"GunAR\":3,\"UiClick\":1},\"active\":2,\"droppedBudget\":4,\"droppedDuplicate\":5,\"droppedDistance\":6,\"queueOverflow\":7}}",
                json);

            sb.Clear();
            map.Audio = null;
            QaResponses.AppendStatus(sb, "p", true, true, "game", false, false, true, 100, 60, 1, "Weapon", "none", true, map);
            StringAssert.DoesNotContain("audio", sb.ToString());
        }

        [Test]
        public void KindNames_CoverEveryKind()
        {
            Assert.AreEqual((int)SoundKind.Count, ProjectH.Client.Game.Audio.GameAudio.KindNames.Length);
            Assert.AreEqual("GunAR", ProjectH.Client.Game.Audio.GameAudio.KindNames[0]);
        }
    }
}
