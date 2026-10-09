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
        private int _grenades = -1;
        private ushort _promptItem;
        private ushort _promptAmount;

        // 기능: 슬롯 문자열을 빈 문자열로 두고 첫 SetSlot이 반드시 문자열을 만들도록 기억 값을 초기화한다.
        // 입력: 없음.
        // 출력: 모든 줄이 비어 있고 Rebuilds 0인 상태.
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

        // 기능: 슬롯 줄의 현재 문자열을 돌려준다.
        // 입력: slot - 슬롯 번호(0..).
        // 출력: 마지막 SetSlot이 만든 문자열(아직 없으면 빈 문자열).
        public string Slot(int slot) => _slotText[slot];

        // 기능: 무기 슬롯 줄을 값이 바뀔 때만 만든다("> 1  Vesper AR [Rare]  30 / 120" 손에 든 슬롯, "  3  -" 빈 슬롯). 이름은 참조로 비교한다.
        // 입력: slot - 슬롯 번호(0..), selected - 손에 든 슬롯인지, weapon - 무기 이름(null = 빈 슬롯), rarity - 희귀도 이름, ammo - 탄창, reserve - 예비탄.
        // 출력: 문자열을 새로 만들었으면 true, 값이 같아 그대로면 false.
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

        // 기능: 소모품 줄을 바뀔 때만 만든다("[4] 구급상자 x2    [5] 실드 셀 x3    [6] 수류탄 x1", Phase 17 D9: 수류탄 수 포함).
        // 입력: medkits·shieldCells·grenades - 가진 개수.
        // 출력: 문자열을 새로 만들었으면 true, 값이 같아 그대로면 false.
        public bool SetConsumables(int medkits, int shieldCells, int grenades)
        {
            if (medkits == _medkits && shieldCells == _shieldCells && grenades == _grenades) return false;
            _medkits = medkits;
            _shieldCells = shieldCells;
            _grenades = grenades;
            Consumables = "[4] 구급상자 x" + medkits + "    [5] 실드 셀 x" + shieldCells + "    [6] 수류탄 x" + grenades;
            Rebuilds++;
            return true;
        }

        // 기능: 줍기 안내 줄을 대상이나 수량이 바뀔 때만 만든다("[E] 줍기: Vesper AR [Rare]", 묶음은 "[E] 줍기: Light Rounds x60"; 일부만 주우면
        //   수량이 줄어 다시 만든다).
        // 입력: itemId - 범위 안의 아이템 id(0 = 없음, 빈 문자열), amount - 묶음 수량, name - 서버 카탈로그의 이름, rarity - 희귀도 이름
        //   (null = 탄·치료 같은 묶음, 수량으로 표시).
        // 출력: 문자열을 새로 만들었으면 true, 대상·수량이 같아 그대로면 false.
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
