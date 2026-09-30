using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    public class WorldItemListTests
    {
        private static WorldItemData Item(ushort id, ushort amount = 10) => new WorldItemData
        {
            ItemId = id,
            Kind = ItemKind.Ammo,
            DefId = (byte)AmmoType.Light,
            Amount = amount,
            Position = new NVector3(id, 0f, 0f),
        };

        [Test]
        public void Upsert_AddsNew_UpdatesKnown()
        {
            var list = new WorldItemList();
            Assert.IsTrue(list.Upsert(Item(5), out int index, out bool added));
            Assert.IsTrue(added);
            Assert.AreEqual(0, index);

            Assert.IsTrue(list.Upsert(Item(5, amount: 3), out index, out added));   // ItemSpawned as an amount change
            Assert.IsFalse(added);
            Assert.AreEqual(0, index);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(3, list[0].Amount);
        }

        // The views array follows the list through movedFrom.
        [Test]
        public void Remove_MovesTheLastIntoTheHole_AndSaysSo()
        {
            var list = new WorldItemList();
            for (ushort id = 1; id <= 3; id++) list.Upsert(Item(id), out _, out _);

            Assert.IsTrue(list.Remove(1, out int removed, out int movedFrom));
            Assert.AreEqual(0, removed);
            Assert.AreEqual(2, movedFrom);
            Assert.AreEqual(3, list[0].ItemId);

            Assert.IsTrue(list.Remove(2, out removed, out movedFrom));
            Assert.AreEqual(1, removed);
            Assert.AreEqual(-1, movedFrom);   // it was the last one
            Assert.AreEqual(1, list.Count);

            Assert.IsFalse(list.Remove(42, out _, out _));   // a late or unknown id is ignored
        }

        // D13: never more than the server's 256.
        [Test]
        public void Full_RefusesANewId_ButStillUpdatesKnownOnes()
        {
            var list = new WorldItemList();
            for (int i = 1; i <= WorldItemList.Capacity; i++) Assert.IsTrue(list.Upsert(Item((ushort)i), out _, out _));
            Assert.IsFalse(list.Upsert(Item(1000), out _, out _));
            Assert.AreEqual(WorldItemList.Capacity, list.Count);
            Assert.IsTrue(list.Upsert(Item(7, amount: 1), out int index, out bool added));
            Assert.IsFalse(added);
            Assert.AreEqual(1, list[index].Amount);
        }

        [Test]
        public void Clear_EmptiesTheList()
        {
            var list = new WorldItemList();
            list.Upsert(Item(1), out _, out _);
            list.Clear();
            Assert.AreEqual(0, list.Count);
            Assert.AreEqual(-1, list.IndexOf(1));
        }
    }
}
