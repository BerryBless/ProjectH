using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    // D15 / Client Hot Path: the HUD calls every Set* each frame; strings may only be built when a value
    // changed, so an unchanged HUD allocates nothing.
    public class InventoryHudTextTests
    {
        private const string Vesper = "Vesper AR";
        private const string Rare = "Rare";

        [Test]
        public void Slot_BuildsOnce_ForTheSameValues()
        {
            var text = new InventoryHudText();
            Assert.IsTrue(text.SetSlot(0, true, Vesper, Rare, 30, 120));
            Assert.AreEqual("> 1  Vesper AR [Rare]  30 / 120", text.Slot(0));
            string built = text.Slot(0);
            int rebuilds = text.Rebuilds;

            for (int frame = 0; frame < 100; frame++) Assert.IsFalse(text.SetSlot(0, true, Vesper, Rare, 30, 120));
            Assert.AreEqual(rebuilds, text.Rebuilds);
            Assert.AreSame(built, text.Slot(0));
        }

        [Test]
        public void Slot_RebuildsOnEachChange()
        {
            var text = new InventoryHudText();
            text.SetSlot(1, false, Vesper, Rare, 30, 120);
            Assert.IsTrue(text.SetSlot(1, false, Vesper, Rare, 29, 120));   // a shot
            Assert.IsTrue(text.SetSlot(1, true, Vesper, Rare, 29, 120));    // selected
            Assert.IsTrue(text.SetSlot(1, true, null, null, 0, 0));         // dropped
            Assert.AreEqual("> 2  -", text.Slot(1));
            Assert.AreEqual(4, text.Rebuilds);
        }

        [Test]
        public void Consumables_BuildOnlyOnChange()
        {
            var text = new InventoryHudText();
            Assert.IsTrue(text.SetConsumables(2, 3, 1));
            Assert.AreEqual("[4] 구급상자 x2    [5] 실드 셀 x3    [6] 수류탄 x1", text.Consumables);
            Assert.IsFalse(text.SetConsumables(2, 3, 1));
            Assert.IsTrue(text.SetConsumables(1, 3, 1));
            Assert.IsTrue(text.SetConsumables(1, 3, 0));   // Phase 17: a thrown grenade rebuilds the line
            Assert.AreEqual(3, text.Rebuilds);
        }

        [Test]
        public void Prompt_FollowsTheTargetAndItsAmount()
        {
            var text = new InventoryHudText();
            Assert.IsFalse(text.SetPrompt(0, 0, null, null));   // nothing in reach and nothing shown yet
            Assert.AreEqual(string.Empty, text.Prompt);

            Assert.IsTrue(text.SetPrompt(7, 30, Vesper, Rare));
            Assert.AreEqual("[E] 줍기: Vesper AR [Rare]", text.Prompt);
            Assert.IsFalse(text.SetPrompt(7, 30, Vesper, Rare));

            Assert.IsTrue(text.SetPrompt(9, 60, "Light Rounds", null));
            Assert.AreEqual("[E] 줍기: Light Rounds x60", text.Prompt);
            Assert.IsTrue(text.SetPrompt(9, 12, "Light Rounds", null));   // partial pickup left 12
            Assert.AreEqual("[E] 줍기: Light Rounds x12", text.Prompt);

            Assert.IsTrue(text.SetPrompt(0, 0, null, null));
            Assert.AreEqual(string.Empty, text.Prompt);
        }
    }
}
