using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Bots;

// A hand-made BotView for brain tests: joined, alive, at the origin of the plaza, two weapons in the catalog
// (an automatic with Medium rounds and a semi-automatic with Heavy rounds), nothing held.
internal static class BotTestView
{
    public const byte AutoId = 1;
    public const byte SemiId = 2;

    // 기능: 접속·생존 상태로 두 무기 목록과 탄약·소모품 Catalog를 가진 빈손의 BotView를 만든다.
    // 입력: position - 봇 위치(없으면 원점).
    // 출력: ServerTick 1000, 체력 100, 인벤토리가 빈 BotView.
    public static BotView Create(Vector3? position = null)
    {
        var view = new BotView
        {
            Joined = true,
            MyId = 1,
            HasSnapshot = true,
            Alive = true,
            MyPosition = position ?? Vector3.Zero,
            ServerTick = 1000,
            Self = new SnapshotSelf { Health = 100, Shield = 0 },
            HasInventory = true,
            Weapons = new[]
            {
                new WeaponInfo { WeaponId = AutoId, Name = "Auto", Damage = 20, FireIntervalTicks = 3, MagazineSize = 30, ReloadTicks = 60, Range = 100f, Automatic = true, AmmoType = AmmoType.Medium },
                new WeaponInfo { WeaponId = SemiId, Name = "Semi", Damage = 90, FireIntervalTicks = 30, MagazineSize = 5, ReloadTicks = 75, Range = 300f, Automatic = false, AmmoType = AmmoType.Heavy },
            },
            Catalog = new ItemCatalogData
            {
                Rarities = Array.Empty<RarityInfo>(),
                Ammo = new[]
                {
                    new AmmoInfo { Type = AmmoType.Light, Name = "L", Max = 180 },
                    new AmmoInfo { Type = AmmoType.Medium, Name = "M", Max = 150 },
                    new AmmoInfo { Type = AmmoType.Heavy, Name = "H", Max = 30 },
                },
                Consumables = new[]
                {
                    new ConsumableInfo { Type = ConsumableType.Medkit, Name = "Medkit", MaxStack = 3 },
                    new ConsumableInfo { Type = ConsumableType.ShieldCell, Name = "Cell", MaxStack = 6 },
                },
            },
        };
        return view;
    }

    // 기능: 슬롯 0에 탄창이 가득한 자동 소총을 든 BotView를 만든다.
    // 입력: position - 봇 위치(없으면 원점).
    // 출력: 현재 슬롯 0, Self.Ammo 30인 BotView.
    // Slot 0 holds the automatic with a full magazine (the snapshot self block carries the current magazine).
    public static BotView Armed(Vector3? position = null)
    {
        BotView view = Create(position);
        view.Inventory.Slot0 = new InventorySlotState { WeaponId = AutoId, MagAmmo = 30 };
        view.Inventory.CurrentSlot = 0;
        view.Self.Ammo = 30;
        return view;
    }

    // 기능: View의 다른 Entity 목록에 하나를 덧붙인다.
    // 입력: view - 봇 View, id - Entity id, position - 위치, alive - 생존 여부.
    // 출력: 반환값 없음. Others와 OtherCount가 늘어난다.
    public static void AddOther(BotView view, ushort id, Vector3 position, bool alive = true)
    {
        view.Others[view.OtherCount++] = new SnapshotEntity { EntityId = id, Position = position, Flags = alive ? SnapshotEntity.AliveFlag : (byte)0 };
    }

    // 기능: View에 월드 아이템 하나를 적용한다.
    // 입력: view - 봇 View, id - 아이템 id, kind - 아이템 종류, defId - 정의 id, position - 위치, amount - 수량.
    // 출력: 반환값 없음. view.Items에 아이템이 들어간다.
    public static void AddItem(BotView view, ushort id, ItemKind kind, byte defId, Vector3 position, ushort amount = 1)
    {
        view.ApplyItem(new WorldItemData { ItemId = id, Kind = kind, DefId = defId, Amount = amount, Position = position });
    }

    // 기능: start부터 1/30초 간격으로 Brain을 Tick한다.
    // 입력: brain - 시험할 BotBrain, view - 봇 View, start - 시작 시각(초), ticks - Tick 수.
    // 출력: Tick마다의 입력 명령 배열(보내지 않은 Tick은 default).
    // Ticks the brain once per 1/30 s from `start`, returning the commands (a dead tick gives default).
    public static InputCommand[] Run(BotBrain brain, BotView view, float start, int ticks)
    {
        var commands = new InputCommand[ticks];
        for (int i = 0; i < ticks; i++) brain.Tick(view, start + i / 30f, out commands[i]);
        return commands;
    }

    // 기능: 두 각도의 최소 차이를 구한다.
    // 입력: a, b - 각도(도).
    // 출력: 0~180도 범위의 차이.
    public static float AngleBetween(float a, float b)
    {
        float d = MathF.Abs((a - b) % 360f);
        return d > 180f ? 360f - d : d;
    }
}
