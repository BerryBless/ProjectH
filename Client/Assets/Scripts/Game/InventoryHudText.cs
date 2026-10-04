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

        // 기능: 슬롯 문자열을 빈 문자열로 두고 첫 SetSlot이 반드시 문자열을 만들도록 캐시를 초기화한다.
        // 입력: 없음.
        // 출력: 모든 슬롯이 빈 문자열이고 Ammo 캐시가 -1인 InventoryHudText.
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

        // 기능: 슬롯 한 줄의 현재 문자열을 돌려준다.
        // 입력: slot - 슬롯 인덱스.
        // 출력: 마지막으로 만든 슬롯 문자열.
        public string Slot(int slot) => _slotText[slot];

        // 기능: 슬롯 값이 바뀌었을 때만 슬롯 문자열을 다시 만든다(이름은 참조로 비교).
        // 입력: slot - 슬롯 인덱스, selected - 손에 든 슬롯 여부, weapon - 무기 이름(빈 슬롯이면 null), rarity - 희귀도 이름, ammo - 탄창 탄약, reserve - 예비 탄약.
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
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

        // 기능: 소모품 개수가 바뀌었을 때만 소모품 문자열을 다시 만든다.
        // 입력: medkits - 구급상자 개수, shieldCells - 실드 셀 개수.
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
        public bool SetConsumables(int medkits, int shieldCells)
        {
            if (medkits == _medkits && shieldCells == _shieldCells) return false;
            _medkits = medkits;
            _shieldCells = shieldCells;
            Consumables = "[4] 구급상자 x" + medkits + "    [5] 실드 셀 x" + shieldCells;
            Rebuilds++;
            return true;
        }

        // 기능: 줍기 대상 ID나 수량이 바뀌었을 때만 줍기 안내 문자열을 다시 만든다.
        // 입력: itemId - 대상 아이템 ID(0이면 대상 없음), amount - 대상 수량, name - 아이템 이름, rarity - 희귀도 이름(묶음 아이템이면 null).
        // 출력: 문자열을 다시 만들었으면 true(Rebuilds 증가), 값이 같으면 false.
        // itemId 0 = nothing in reach. rarity null = a stack (ammo, heal), shown with its amount:
        // "[E] 줍기: Vesper AR [Rare]", "[E] 줍기: Light Rounds x60" (item names are the server catalog's). The text
        // changes with the target or its amount (a partial pickup leaves a smaller stack).
        public bool SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (itemId == _promptItem && amount == _promptAmount) return false;
            _promptItem = itemId;
            _promptAmount = amount;
            if (itemId == 0) Prompt = string.Empty;
            else if (rarity != null) Prompt = "[E] 줍기: " + name + " [" + rarity + "]";
            else Prompt = "[E] 줍기: " + name + " x" + amount;
            Rebuilds++;
            return true;
        }
    }
}
