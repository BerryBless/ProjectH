using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // The prompt must point at the item the server will take: same cases as the server's
    // WorldItemsTests.FindNearest_* (2 m on the ground plane, 2 m up or down, nearest, ties to the lower id).
    public class PickupRuleTests
    {
        private static WorldItemData Item(ushort id, NVector3 position) => new WorldItemData
        {
            ItemId = id,
            Kind = ItemKind.Ammo,
            DefId = (byte)AmmoType.Light,
            Amount = 10,
            Position = position,
        };

        private static WorldItemList ListOf(params WorldItemData[] items)
        {
            var list = new WorldItemList();
            foreach (var item in items) list.Upsert(item, out _, out _);
            return list;
        }

        [TestCase(2.0f, 0f, 0f, true)]
        [TestCase(2.001f, 0f, 0f, false)]
        [TestCase(1.4f, 0f, 1.4f, true)]
        [TestCase(1.42f, 0f, 1.42f, false)]
        [TestCase(0f, 2.0f, 0f, true)]
        [TestCase(0f, 2.01f, 0f, false)]
        [TestCase(0f, -2.0f, 0f, true)]
        [TestCase(0f, -2.01f, 0f, false)]
        public void RangeBoundary_MatchesTheServer(float dx, float dy, float dz, bool expectedFound)
        {
            var feet = new NVector3(5f, 1f, -3f);
            var list = ListOf(Item(1, feet + new NVector3(dx, dy, dz)));
            Assert.AreEqual(expectedFound, PickupRule.FindNearest(list, feet) >= 0);
        }

        [Test]
        public void Nearest_TiesGoToTheLowerId_WhateverTheListOrder()
        {
            var feet = NVector3.Zero;
            // Stored high id first: the rule must not depend on storage order.
            var list = ListOf(Item(9, new NVector3(0f, 0f, 1f)), Item(4, new NVector3(0f, 0f, -1f)), Item(2, new NVector3(1.5f, 0f, 0f)));
            Assert.AreEqual(4, list[PickupRule.FindNearest(list, feet)].ItemId);

            list.Remove(4, out _, out _);
            Assert.AreEqual(9, list[PickupRule.FindNearest(list, feet)].ItemId);
            list.Remove(9, out _, out _);
            Assert.AreEqual(2, list[PickupRule.FindNearest(list, feet)].ItemId);
            list.Remove(2, out _, out _);
            Assert.AreEqual(-1, PickupRule.FindNearest(list, feet));
        }
    }
}
