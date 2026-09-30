namespace ProjectH.Client.Game
{
    // The strings of the inventory HUD and the pickup prompt (D15), built only when a shown value changes, so
    // a HUD that shows the same thing every frame allocates nothing. Pure (no UnityEngine): InventoryHud puts
    // the strings on screen when a Set* call returns true. Names are the catalog's strings (received once per
    // join), compared by reference.
    public sealed class InventoryHudText
    {
        private struct SlotValues
        {
            public bool Selected;
            public string Weapon;   // null = empty slot
            public string Rarity;
            public int Ammo;
            public int Reserve;
        }

        private readonly SlotValues[] _slots = new SlotValues[WeaponState.SlotCount];
        private readonly string[] _slotText = new string[WeaponState.SlotCount];
        private int _medkits = -1;
        private int _shieldCells = -1;
        private ushort _promptItem;
        private ushort _promptAmount;

        public InventoryHudText()
        {
            for (int i = 0; i < _slotText.Length; i++) _slotText[i] = string.Empty;
            // Force the first Set of each slot to build its text.
            for (int i = 0; i < _slots.Length; i++) _slots[i].Ammo = -1;
        }

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Consumables { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public string Slot(int slot) => _slotText[slot];

        // "> 1  Vesper AR [Rare]  30 / 120" for the slot in hand, "  3  -" for an empty one.
        public bool SetSlot(int slot, bool selected, string weapon, string rarity, int ammo, int reserve)
        {
            ref SlotValues v = ref _slots[slot];
            if (v.Selected == selected && ReferenceEquals(v.Weapon, weapon) && ReferenceEquals(v.Rarity, rarity) &&
                v.Ammo == ammo && v.Reserve == reserve)
                return false;
            v.Selected = selected;
            v.Weapon = weapon;
            v.Rarity = rarity;
            v.Ammo = ammo;
            v.Reserve = reserve;
            string mark = selected ? "> " : "  ";
            _slotText[slot] = weapon == null
                ? mark + (slot + 1) + "  -"
                : mark + (slot + 1) + "  " + weapon + " [" + rarity + "]  " + ammo + " / " + reserve;
            Rebuilds++;
            return true;
        }

        public bool SetConsumables(int medkits, int shieldCells)
        {
            if (medkits == _medkits && shieldCells == _shieldCells) return false;
            _medkits = medkits;
            _shieldCells = shieldCells;
            Consumables = "[4] Medkit x" + medkits + "    [5] Shield Cell x" + shieldCells;
            Rebuilds++;
            return true;
        }

        // itemId 0 = nothing in reach. rarity null = a stack (ammo, heal), shown with its amount:
        // "[E] Pick up Vesper AR [Rare]", "[E] Pick up Light Rounds x60". The text changes with the target
        // or its amount (a partial pickup leaves a smaller stack).
        public bool SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (itemId == _promptItem && amount == _promptAmount) return false;
            _promptItem = itemId;
            _promptAmount = amount;
            if (itemId == 0) Prompt = string.Empty;
            else if (rarity != null) Prompt = "[E] Pick up " + name + " [" + rarity + "]";
            else Prompt = "[E] Pick up " + name + " x" + amount;
            Rebuilds++;
            return true;
        }
    }
}
