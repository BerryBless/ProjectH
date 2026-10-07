using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Squad;

// Phase 14 D11: squad.json, the knock-down, revive and reboot numbers. Loaded and validated once at startup like the other
// data files (a bad file stops the server); immutable afterwards, so the game loop reads it without locks. Tick values are
// already in simulation ticks. The crawl speed and the downed box are not here: the client's prediction needs them, so
// they are Shared MovementTuning constants.
public sealed class SquadCatalog
{
    public const string FileName = "squad.json";

    private SquadCatalog(int simHz, SquadJson root, uint bleedOutTicks, uint reviveTicks, uint rebootTicks, uint cardLifetimeTicks,
        uint stationCooldownTicks, StartingLoadout rebootLoadout)
    {
        SimHz = simHz;
        FriendlyFire = root.FriendlyFire;
        DownedHealth = root.DownedHealth;
        BleedOutTicks = bleedOutTicks;
        ReviveTicks = reviveTicks;
        ReviveRange = (float)root.ReviveRange;
        ReviveHealth = root.ReviveHealth;
        ReviveCancelOnDamage = root.ReviveCancelOnDamage;
        RebootTicks = rebootTicks;
        RebootRange = (float)root.RebootRange;
        CardLifetimeTicks = cardLifetimeTicks;
        MaxCardsHeld = root.MaxCardsHeld;
        StationCooldownTicks = stationCooldownTicks;
        RebootLoadout = rebootLoadout;
    }

    public int SimHz { get; }
    // D3: only false is implemented (a shot passes through teammates); true is refused at load.
    public bool FriendlyFire { get; }
    // D5: a knocked-down player's health, which bleeds to 0 over BleedOutTicks (a revive pauses it).
    public int DownedHealth { get; }
    public uint BleedOutTicks { get; }
    // D8: a revive lasts ReviveTicks within ReviveRange (it is cancelled past ReviveRange + ReviveCancelSlack) and stands
    // the target up with ReviveHealth and no shield.
    public uint ReviveTicks { get; }
    public float ReviveRange { get; }
    public int ReviveHealth { get; }
    public bool ReviveCancelOnDamage { get; }
    // D10: a reboot lasts RebootTicks within RebootRange of a station, which then cools down for StationCooldownTicks.
    public uint RebootTicks { get; }
    public float RebootRange { get; }
    // D9: a reboot card lies in the world for CardLifetimeTicks; a player carries at most MaxCardsHeld.
    public uint CardLifetimeTicks { get; }
    public int MaxCardsHeld { get; }
    public uint StationCooldownTicks { get; }
    // D10 (request §45): what a rebooted player comes back with (its old inventory is not restored).
    public StartingLoadout RebootLoadout { get; }

    // D8: a channel goes on while the actor stays within its range plus this much (a small step back does not cancel it).
    public const float ChannelCancelSlack = 0.5f;
    // D10: a station is used from at most this much above or below it (the range itself is measured across the ground).
    public const float StationHeightRange = 2f;

    // 기능: 배포하는 기본값(squad.json과 같다, SquadCatalogTests가 둘을 묶는다)으로 카탈로그를 만든다. 파일 없이 GameData를 만드는 테스트용.
    // 입력: simHz - 서버 Tick 속도.
    // 출력: 기본 수치의 SquadCatalog. 기본값이 틀리면 예외.
    public static SquadCatalog Default(int simHz)
    {
        if (!TryParse(DefaultJson, simHz, out var catalog, out string? error))
            throw new InvalidOperationException("The default squad data is invalid: " + error);
        return catalog!;
    }

    // 기능: squad.json 파일을 읽고 검증한다(시작 때 한 번).
    // 입력: path - 파일 경로, simHz - 서버 Tick 속도.
    // 출력: 검증된 SquadCatalog. 파일이 없거나 틀리면 InvalidOperationException(서버가 시작하지 않는다).
    public static SquadCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Squad data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid squad data {path}: {error}");
        return catalog!;
    }

    // 기능: squad.json 내용을 읽고 모든 값을 범위 검사한다. 무기 id와 탄 상한은 GameData가 카탈로그와 맞춰 본다.
    // 입력: json - 파일 내용, simHz - 서버 Tick 속도.
    // 출력: 성공하면 true와 카탈로그, 실패하면 false와 이유.
    public static bool TryParse(string json, int simHz, out SquadCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1) return Fail("SimHz must be positive.", out error);
        if (!DataJson.TryDeserialize(json, out SquadJson? root, out error)) return false;
        if (root!.FriendlyFire) return Fail("friendlyFire must be false (only the off policy is implemented, Phase 14 D3).", out error);
        if (root.DownedHealth < 1 || root.DownedHealth > CombatRules.MaxHealth) return Fail($"downedHealth must be 1-{CombatRules.MaxHealth}.", out error);
        if (!Seconds(root.BleedOutSeconds, 1, 600, simHz, out uint bleed)) return Fail("bleedOutSeconds must be 1-600.", out error);
        if (!Seconds(root.ReviveSeconds, 0.5, 60, simHz, out uint revive)) return Fail("reviveSeconds must be 0.5-60.", out error);
        if (!(root.ReviveRange >= 0.5 && root.ReviveRange <= 10)) return Fail("reviveRange must be 0.5-10.", out error);
        if (root.ReviveHealth < 1 || root.ReviveHealth > CombatRules.MaxHealth) return Fail($"reviveHealth must be 1-{CombatRules.MaxHealth}.", out error);
        if (!Seconds(root.RebootSeconds, 0.5, 60, simHz, out uint reboot)) return Fail("rebootSeconds must be 0.5-60.", out error);
        if (!(root.RebootRange >= 0.5 && root.RebootRange <= 10)) return Fail("rebootRange must be 0.5-10.", out error);
        if (!Seconds(root.CardLifetimeSeconds, 1, 3600, simHz, out uint lifetime)) return Fail("cardLifetimeSeconds must be 1-3600.", out error);
        if (root.MaxCardsHeld < 1 || root.MaxCardsHeld > SquadConstants.MaxCardsHeld)
            return Fail($"maxCardsHeld must be 1-{SquadConstants.MaxCardsHeld} (InventoryState.RebootCards).", out error);
        if (!Seconds(root.StationCooldownSeconds, 1, 3600, simHz, out uint cooldown)) return Fail("stationCooldownSeconds must be 1-3600.", out error);

        LoadoutJson? l = root.RebootLoadout;
        if (l == null) return Fail("\"rebootLoadout\" is required.", out error);
        var weapons = l.Weapons ?? Array.Empty<LoadoutWeaponJson>();
        if (weapons.Length > Inventory.SlotCount) return Fail($"rebootLoadout.weapons: at most {Inventory.SlotCount}.", out error);
        var list = new LoadoutWeapon[weapons.Length];
        for (int i = 0; i < weapons.Length; i++)
        {
            LoadoutWeaponJson? w = weapons[i];
            if (w == null || w.Id < 1 || w.Id > byte.MaxValue || w.Rarity < 0 || w.Rarity >= ItemConstants.RarityCount)
                return Fail($"rebootLoadout.weapons[{i}]: id must be 1-255 and rarity 0-{ItemConstants.RarityCount - 1}.", out error);
            list[i] = new LoadoutWeapon((byte)w.Id, (byte)w.Rarity);
        }
        var loadout = new StartingLoadout
        {
            Shield = l.Shield, Weapons = list, LightAmmo = l.LightAmmo, MediumAmmo = l.MediumAmmo, HeavyAmmo = l.HeavyAmmo,
            Medkits = l.Medkits, ShieldCells = l.ShieldCells,
        };
        catalog = new SquadCatalog(simHz, root, bleed, revive, reboot, lifetime, cooldown, loadout);
        error = null;
        return true;
    }

    // Seconds within [min, max] to whole ticks (at least 1).
    private static bool Seconds(double seconds, double min, double max, int simHz, out uint ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds < min || seconds > max) return false;
        ticks = (uint)Math.Max(1, Math.Round(seconds * simHz, MidpointRounding.AwayFromZero));
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    // The shipped file without its comment (squad.json).
    public const string DefaultJson = """
        {
          "friendlyFire": false,
          "downedHealth": 100,
          "bleedOutSeconds": 30,
          "reviveSeconds": 5,
          "reviveRange": 2.0,
          "reviveHealth": 30,
          "reviveCancelOnDamage": true,
          "rebootSeconds": 5,
          "rebootRange": 3.0,
          "cardLifetimeSeconds": 90,
          "maxCardsHeld": 3,
          "stationCooldownSeconds": 30,
          "rebootLoadout": { "shield": 0, "weapons": [ { "id": 3, "rarity": 0 } ], "lightAmmo": 30 }
        }
        """;

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class SquadJson
    {
        public bool FriendlyFire { get; set; }
        public int DownedHealth { get; set; }
        public double BleedOutSeconds { get; set; }
        public double ReviveSeconds { get; set; }
        public double ReviveRange { get; set; }
        public int ReviveHealth { get; set; }
        public bool ReviveCancelOnDamage { get; set; }
        public double RebootSeconds { get; set; }
        public double RebootRange { get; set; }
        public double CardLifetimeSeconds { get; set; }
        public int MaxCardsHeld { get; set; }
        public double StationCooldownSeconds { get; set; }
        public LoadoutJson? RebootLoadout { get; set; }
    }

    private sealed class LoadoutJson
    {
        public int Shield { get; set; }
        public LoadoutWeaponJson?[]? Weapons { get; set; }
        public int LightAmmo { get; set; }
        public int MediumAmmo { get; set; }
        public int HeavyAmmo { get; set; }
        public int Medkits { get; set; }
        public int ShieldCells { get; set; }
    }

    private sealed class LoadoutWeaponJson
    {
        public int Id { get; set; }
        public int Rarity { get; set; }
    }
}
