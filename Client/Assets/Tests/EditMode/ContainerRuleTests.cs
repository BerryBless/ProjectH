using System;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 16 D4: the client's copy of the container rule (the server tests compare it with ContainerRules): reach and
    // angle like the door rule, the nearest target (containers before supply drops on a tie), and the door-or-container pick.
    public class ContainerRuleTests
    {
        private static readonly Vector3 Origin = new Vector3(10f, 2f, 10f);

        private static LootContainer Chest(float x, float z) => new LootContainer(LootContainerKind.Chest, new Vector3(x, 2f, z), 0f);

        [Test]
        public void InReach_RangeHeightAndAngle()
        {
            var target = Origin + new Vector3(0f, 0f, 2f);
            Assert.IsTrue(ContainerRule.InReach(Origin, 0f, target, out float sq));
            Assert.AreEqual(4f, sq, 1e-4f);
            Assert.IsTrue(ContainerRule.InReach(Origin, 0f, Origin + new Vector3(0f, 0f, 2.5f), out _));   // exactly at range
            Assert.IsFalse(ContainerRule.InReach(Origin, 0f, Origin + new Vector3(0f, 0f, 2.6f), out sq));
            Assert.AreEqual(2.6f * 2.6f, sq, 1e-3f);   // the distance comes back even when out of reach
            Assert.IsFalse(ContainerRule.InReach(Origin, 180f, target, out _));   // behind
            Assert.IsTrue(ContainerRule.InReach(Origin, 59f, target, out _));
            Assert.IsFalse(ContainerRule.InReach(Origin, 61f, target, out _));
            Assert.IsTrue(ContainerRule.InReach(Origin + new Vector3(0f, 1f, 0f), 0f, target, out _));   // feet 1 m above its bottom
            Assert.IsFalse(ContainerRule.InReach(Origin + new Vector3(0f, 1.1f, 0f), 0f, target, out _));
            Assert.IsFalse(ContainerRule.InReach(Origin - new Vector3(0f, 1.1f, 0f), 0f, target, out _));
            Assert.IsTrue(ContainerRule.InReach(Origin, 180f, Origin, out _));   // standing on it: no direction to check
        }

        [Test]
        public void FindTarget_OnlyClosedOnes_TheNearest()
        {
            LootContainer[] containers = { Chest(10f, 12f), Chest(10f, 11f), Chest(10f, 30f) };
            Vector3[] drops = new Vector3[4];
            Assert.AreEqual(1, ContainerRule.FindTarget(Origin, 0f, containers, 0b111, drops, 0, out float sq));
            Assert.AreEqual(1f, sq, 1e-4f);
            Assert.AreEqual(0, ContainerRule.FindTarget(Origin, 0f, containers, 0b101, drops, 0, out sq));   // 1 opened or not spawned
            Assert.AreEqual(4f, sq, 1e-4f);
            Assert.AreEqual(-1, ContainerRule.FindTarget(Origin, 0f, containers, 0b100, drops, 0, out sq));   // 2 is out of reach
            Assert.AreEqual(0f, sq);
            Assert.AreEqual(-1, ContainerRule.FindTarget(Origin, 180f, containers, 0b111, drops, 0, out _));
        }

        [Test]
        public void FindTarget_SupplyDrops_LandedClosedOnly_AndContainersWinTies()
        {
            LootContainer[] containers = { Chest(10f, 12f) };
            Vector3[] drops = { new Vector3(50f, 2f, 50f), new Vector3(10f, 2f, 11f), new Vector3(10f, 2f, 12f), default };
            Assert.AreEqual(ContainerRule.SupplyDropTargetBase + 1, ContainerRule.FindTarget(Origin, 0f, containers, 1, drops, 0b0010, out float sq));
            Assert.AreEqual(1f, sq, 1e-4f);
            Assert.AreEqual(0, ContainerRule.FindTarget(Origin, 0f, containers, 1, drops, 0b0100, out _));   // same distance: the container
            Assert.AreEqual(ContainerRule.SupplyDropTargetBase + 2, ContainerRule.FindTarget(Origin, 0f, containers, 0, drops, 0b0100, out _));
            Assert.AreEqual(0, ContainerRule.FindTarget(Origin, 0f, containers, 1, drops, 0b0001, out _));   // drop 0 is far
        }

        [Test]
        public void FindTarget_SameDistance_TheSmallerId()
        {
            LootContainer[] containers = { Chest(11f, 11f), Chest(9f, 11f) };
            Assert.AreEqual(0, ContainerRule.FindTarget(Origin, 0f, containers, 0b11, ReadOnlySpan<Vector3>.Empty, 0, out _));
            Assert.AreEqual(1, ContainerRule.FindTarget(Origin, 0f, containers, 0b10, ReadOnlySpan<Vector3>.Empty, 0, out _));
        }

        [Test]
        public void PreferContainer_TheNearerOne_DoorOnATie()
        {
            // A 1 m door whose centre is 1.5 m in front of the feet.
            Box[] doors = { new Box(new Vector3(9.5f, 2f, 11.4f), new Vector3(10.5f, 4f, 11.6f)) };
            Assert.IsFalse(ContainerRule.PreferContainer(Origin, 0, doors, 4f));      // container 2 m away: the door
            Assert.IsTrue(ContainerRule.PreferContainer(Origin, 0, doors, 1f));       // container 1 m away
            Assert.IsFalse(ContainerRule.PreferContainer(Origin, 0, doors, 2.25f));   // a tie: the door
            Assert.IsTrue(ContainerRule.PreferContainer(Origin, -1, doors, 100f));    // no door
        }

        [Test]
        public void FindTarget_MapContainer_FromInFront()
        {
            // The rule works on the real map data: stand 1.5 m in front of container 0, facing it.
            LootContainer c = LootContainers.All[0];
            var feet = c.Position + new Vector3(0f, 0f, -1.5f);
            Assert.AreEqual(0, ContainerRule.FindTarget(feet, 0f, LootContainers.All, 1UL, new Vector3[4], 0, out float sq));
            Assert.AreEqual(2.25f, sq, 1e-3f);
        }
    }
}
