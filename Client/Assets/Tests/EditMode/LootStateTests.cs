using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 16 D3, D7, D8: the client's loot state: masks applied as sent, the supply drop list replaced whole, when E may
    // open anything, and the fall height drawn from SupplyDropFall.
    public class LootStateTests
    {
        // 기능: 시험용 보급 상자 정보를 만든다.
        // 입력: id - 보급 상자 ID, state - 낙하 상태, x - 착지 x 좌표, z - 착지 z 좌표, landY - 착지 높이, start - 낙하 시작 Tick, land - 착지 Tick.
        // 출력: SupplyDropInfo.
        private static SupplyDropInfo Drop(byte id, SupplyDropState state, float x, float z, float landY = 1f, uint start = 100, uint land = 550) =>
            new SupplyDropInfo { Id = id, State = state, X = x, Z = z, LandY = landY, StartTick = start, LandTick = land };

        [Test]
        public void ApplyContainers_ClosedIsSpawnedAndNotOpened_VersionOnChange()
        {
            var loot = new LootState();
            Assert.IsTrue(loot.ApplyContainers(0b1011, 0b0010));
            Assert.AreEqual(0b1001UL, loot.ClosedMask);
            int version = loot.ContainerVersion;
            Assert.IsFalse(loot.ApplyContainers(0b1011, 0b0010));   // the same masks again (a resend)
            Assert.AreEqual(version, loot.ContainerVersion);
            Assert.IsTrue(loot.ApplyContainers(0, 0));               // a round reset
            Assert.AreEqual(0UL, loot.ClosedMask);
            Assert.AreEqual(version + 1, loot.ContainerVersion);
        }

        [Test]
        public void ApplyDrops_ReplacesTheWholeList_PositionsAndLandedMaskById()
        {
            var loot = new LootState();
            var packet = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
            packet[0] = Drop(0, SupplyDropState.Opened, 1f, 2f);
            packet[1] = Drop(1, SupplyDropState.Landed, 3f, 4f, 5f);
            packet[2] = Drop(2, SupplyDropState.Falling, 6f, 7f);
            loot.ApplyDrops(packet, 3);
            Assert.AreEqual(3, loot.DropCount);
            Assert.AreEqual(0b010, loot.LandedClosedDropMask);
            Assert.AreEqual(new Vector3(3f, 5f, 4f), loot.DropPositions[1]);
            Assert.AreEqual(SupplyDropState.Falling, loot.Drop(2).State);

            int version = loot.DropVersion;
            packet[0] = Drop(2, SupplyDropState.Landed, 6f, 7f);   // a shorter list: only drop 2, landed
            loot.ApplyDrops(packet, 1);
            Assert.AreEqual(1, loot.DropCount);
            Assert.AreEqual(0b100, loot.LandedClosedDropMask);
            Assert.AreEqual(version + 1, loot.DropVersion);
            Assert.AreEqual(default(SupplyDropInfo).Id, loot.Drop(1).Id);   // the rest is cleared

            loot.ApplyDrops(packet, 0);   // a round reset: the empty list
            Assert.AreEqual(0, loot.DropCount);
            Assert.AreEqual(0, loot.LandedClosedDropMask);
        }

        [Test]
        public void ApplyDrops_CountIsClampedToTheArrays()
        {
            var loot = new LootState();
            var packet = new SupplyDropInfo[2];
            packet[0] = Drop(0, SupplyDropState.Landed, 1f, 1f);
            packet[1] = Drop(1, SupplyDropState.Landed, 2f, 2f);
            loot.ApplyDrops(packet, 9);
            Assert.AreEqual(2, loot.DropCount);
            loot.ApplyDrops(null, 3);
            Assert.AreEqual(0, loot.DropCount);
        }

        [Test]
        public void FindTarget_ContainersAndLandedDrops_OnlyWhileActive()
        {
            var loot = new LootState();
            LootContainer c = LootContainers.All[0];
            var feet = c.Position + new Vector3(0f, 0f, -1.5f);
            Assert.AreEqual(-1, loot.FindTarget(feet, 0f, out _));   // nothing spawned
            loot.ApplyContainers(1UL, 0UL);
            Assert.AreEqual(0, loot.FindTarget(feet, 0f, out float sq));
            Assert.AreEqual(2.25f, sq, 1e-3f);
            Assert.AreEqual(LootTargetKind.Chest, LootState.KindOf(0));

            loot.SetMatch(true, MatchFlowState.Finished);   // the result screen: the server opens nothing
            Assert.IsFalse(loot.Active);
            Assert.AreEqual(-1, loot.FindTarget(feet, 0f, out sq));
            Assert.AreEqual(0f, sq);
            loot.SetMatch(true, MatchFlowState.FinalPhase);
            Assert.AreEqual(0, loot.FindTarget(feet, 0f, out _));
            loot.SetMatch(false, MatchFlowState.WaitingForPlayers);   // no match at all: the dev sandbox
            Assert.IsTrue(loot.Active);

            loot.ApplyContainers(1UL, 1UL);   // opened
            Assert.AreEqual(-1, loot.FindTarget(feet, 0f, out _));
            var packet = new[] { Drop(3, SupplyDropState.Landed, feet.X, feet.Z + 1f, feet.Y) };
            loot.ApplyDrops(packet, 1);
            Assert.AreEqual(ContainerRule.SupplyDropTargetBase + 3, loot.FindTarget(feet, 0f, out _));
            Assert.AreEqual(LootTargetKind.SupplyDrop, LootState.KindOf(ContainerRule.SupplyDropTargetBase + 3));
            packet[0].State = SupplyDropState.Falling;   // still in the air: not openable
            loot.ApplyDrops(packet, 1);
            Assert.AreEqual(-1, loot.FindTarget(feet, 0f, out _));
        }

        [Test]
        public void KindOf_AmmoBoxAndOutOfRange()
        {
            int ammo = -1;
            for (int i = 0; i < LootContainers.Count; i++)
            {
                if (LootContainers.All[i].Kind == LootContainerKind.AmmoBox)
                {
                    ammo = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(ammo, 0);
            Assert.AreEqual(LootTargetKind.AmmoBox, LootState.KindOf(ammo));
            Assert.AreEqual(LootTargetKind.None, LootState.KindOf(-1));
            Assert.AreEqual(LootTargetKind.None, LootState.KindOf(LootContainers.Count));
        }

        [Test]
        public void HeightOf_FallsFromStartHeight_ThenStaysOnTheGround()
        {
            SupplyDropInfo falling = Drop(0, SupplyDropState.Falling, 0f, 0f, 3f, 100, 550);
            Assert.AreEqual(3f + SupplyDropFall.StartHeight, LootState.HeightOf(falling, 0), 1e-4f);
            Assert.AreEqual(3f + SupplyDropFall.StartHeight, LootState.HeightOf(falling, 100), 1e-4f);
            Assert.AreEqual(3f + SupplyDropFall.StartHeight * 0.5f, LootState.HeightOf(falling, 325), 1e-3f);
            Assert.AreEqual(3f, LootState.HeightOf(falling, 550), 1e-4f);
            Assert.AreEqual(3f, LootState.HeightOf(falling, 9000), 1e-4f);
            // Landed or opened: on the ground whatever the tick (the packet may come before the drawn tick reaches LandTick).
            Assert.AreEqual(3f, LootState.HeightOf(Drop(0, SupplyDropState.Landed, 0f, 0f, 3f, 100, 550), 200), 1e-4f);
            Assert.AreEqual(3f, LootState.HeightOf(Drop(0, SupplyDropState.Opened, 0f, 0f, 3f, 100, 550), 200), 1e-4f);
        }

        [Test]
        public void Clear_EmptiesEverything_AndGoesBackToActive()
        {
            var loot = new LootState();
            loot.ApplyContainers(0b11, 0b01);
            loot.ApplyDrops(new[] { Drop(1, SupplyDropState.Landed, 1f, 1f) }, 1);
            loot.SetMatch(true, MatchFlowState.Starting);
            int drops = loot.DropVersion;
            loot.Clear();
            Assert.AreEqual(0UL, loot.SpawnedMask);
            Assert.AreEqual(0UL, loot.OpenedMask);
            Assert.AreEqual(0, loot.DropCount);
            Assert.AreEqual(0, loot.LandedClosedDropMask);
            Assert.AreEqual(Vector3.Zero, loot.DropPositions[1]);
            Assert.IsTrue(loot.Active);
            Assert.AreEqual(drops + 1, loot.DropVersion);
        }
    }
}
