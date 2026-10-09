using System;
using System.Collections.Generic;
using System.IO;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D4 (request §7): one building material's numbers. Tick values are already in simulation ticks.
public sealed class BuildMaterialConfig
{
    // 기능: 재료 하나의 수치를 담고 초기 체력(최대 x 비율, 최소 1)을 계산한다.
    // 입력: type - 재료, resourceCost - 조각 하나의 자원 비용, maxHealth - 최대 체력, initialHealthRatio - 초기 체력 비율,
    //   constructionTicks - 건설 Tick 수, structureDamageMultiplier - 구조물 피해 배율, harvestToolDamageMultiplier - 채집 도구 피해 배율.
    // 출력: 값이 채워진 BuildMaterialConfig.
    public BuildMaterialConfig(BuildMaterialType type, int resourceCost, int maxHealth, float initialHealthRatio, ushort constructionTicks,
        float structureDamageMultiplier, float harvestToolDamageMultiplier)
    {
        Type = type;
        ResourceCost = resourceCost;
        MaxHealth = maxHealth;
        InitialHealthRatio = initialHealthRatio;
        InitialHealth = Math.Max(1, (int)MathF.Round(maxHealth * initialHealthRatio));
        ConstructionTicks = constructionTicks;
        StructureDamageMultiplier = structureDamageMultiplier;
        HarvestToolDamageMultiplier = harvestToolDamageMultiplier;
    }

    public BuildMaterialType Type { get; }
    public int ResourceCost { get; }
    public int MaxHealth { get; }
    public float InitialHealthRatio { get; }
    // The health a piece is placed with (D10: it grows to MaxHealth over ConstructionTicks).
    public int InitialHealth { get; }
    public ushort ConstructionTicks { get; }
    public float StructureDamageMultiplier { get; }
    public float HarvestToolDamageMultiplier { get; }
}

// Phase 13 D4, D6: one kind of harvestable object.
public sealed class HarvestableConfig
{
    // 기능: 채집물 종류 하나의 수치를 담는다.
    // 입력: kind - 채집물 종류, health - 체력, baseResourcePerHit - 타격당 기본 자원, destroyBonus - 파괴 시 추가 자원.
    // 출력: 값이 채워진 HarvestableConfig.
    public HarvestableConfig(HarvestKind kind, int health, int baseResourcePerHit, int destroyBonus)
    {
        Kind = kind;
        Health = health;
        BaseResourcePerHit = baseResourcePerHit;
        DestroyBonus = destroyBonus;
    }

    public HarvestKind Kind { get; }
    public int Health { get; }
    public int BaseResourcePerHit { get; }
    public int DestroyBonus { get; }
}

// Phase 13 D4: building.json, loaded and validated once at startup like the other data files (a bad file stops the
// server). Immutable afterwards, so the game loop reads it without locks. The client and the bots get what they need of
// it at join (BuildCatalog packet); only the server decides with it.
public sealed class BuildingCatalog
{
    public const string FileName = "building.json";
    public const int MaxResourceLimit = ushort.MaxValue;

    private readonly BuildMaterialConfig[] _materials;
    private readonly HarvestableConfig[] _harvestables;

    // 기능: 검증을 마친 값들로 카탈로그를 만든다(TryParse만 호출한다).
    // 입력: materials - 재료별 수치(Enum 순서), harvestables - 채집물별 수치(Enum 순서), simHz - Tick 속도, root - 파싱된 JSON,
    //   cooldownTicks - 채집 도구 쿨다운 Tick, intervalTicks - 최소 건설 간격 Tick.
    // 출력: 모든 값이 채워진 불변 BuildingCatalog.
    private BuildingCatalog(BuildMaterialConfig[] materials, HarvestableConfig[] harvestables, int simHz, BuildingJson root, ushort cooldownTicks,
        ushort intervalTicks)
    {
        _materials = materials;
        _harvestables = harvestables;
        SimHz = simHz;
        MaxResource = root.MaxResource;
        HarvestToolJson tool = root.HarvestTool!;
        HarvestRange = (float)tool.Range;
        HarvestCooldownTicks = cooldownTicks;
        EnvironmentDamage = tool.EnvironmentDamage;
        HarvestStructureDamage = tool.StructureDamage;
        WeakPointRadius = (float)tool.WeakPointRadius;
        WeakPointMultiplier = tool.WeakPointMultiplier;
        BuildJson build = root.Build!;
        BuildRange = (float)build.Range;
        ViewAngleDegrees = (float)build.ViewAngleDegrees;
        MinBuildIntervalTicks = intervalTicks;
        MaxPiecesPerMatch = build.MaxBuildPiecesPerMatch;
        MaxPiecesPerPlayer = build.MaxBuildPiecesPerPlayer;
        MaxRequestsPerSecond = build.MaxRequestsPerSecond;
        InterestJson interest = root.Interest!;
        InterestCellSize = (float)interest.CellSize;
        InterestRadius = interest.Radius;
        InterestKeepMargin = interest.KeepMargin;
    }

    public int SimHz { get; }
    public int MaxResource { get; }
    public float HarvestRange { get; }
    public ushort HarvestCooldownTicks { get; }
    public int EnvironmentDamage { get; }
    public int HarvestStructureDamage { get; }
    public float WeakPointRadius { get; }
    public int WeakPointMultiplier { get; }
    // D9: from the eye to a piece's centre at most BuildRange plus the piece's radius, within ViewAngleDegrees of the aim.
    public float BuildRange { get; }
    public float ViewAngleDegrees { get; }
    public ushort MinBuildIntervalTicks { get; }
    public int MaxPiecesPerMatch { get; }
    public int MaxPiecesPerPlayer { get; }
    public int MaxRequestsPerSecond { get; }
    // D14: the interest cells (InterestCellSize metres square), the window radius in cells, and how many cells further
    // a piece is kept before the client forgets it.
    public float InterestCellSize { get; }
    public int InterestRadius { get; }
    public int InterestKeepMargin { get; }

    // 기능: 재료의 수치를 찾는다.
    // 입력: type - 재료.
    // 출력: 그 재료의 BuildMaterialConfig.
    public BuildMaterialConfig Material(BuildMaterialType type) => _materials[(int)type];

    // 기능: 채집물 종류의 수치를 찾는다.
    // 입력: kind - 채집물 종류.
    // 출력: 그 종류의 HarvestableConfig.
    public HarvestableConfig Harvestable(HarvestKind kind) => _harvestables[(int)kind];

    // 기능: 내장 기본값(building.json과 같은 내용)으로 카탈로그를 만든다. 파일이 없을 때(테스트) GameData가 쓴다.
    // 입력: simHz - Tick 속도.
    // 출력: 기본값 카탈로그. 기본값이 검증에 실패하면 InvalidOperationException.
    // The shipped numbers (the same as building.json; BuildingCatalogTests pins the two). GameData uses them when no file is
    // given (tests).
    public static BuildingCatalog Default(int simHz)
    {
        if (!TryParse(DefaultJson, simHz, out var catalog, out string? error))
            throw new InvalidOperationException("The default building data is invalid: " + error);
        return catalog!;
    }

    // 기능: building.json 파일을 읽어 검증한 카탈로그를 만든다(시작 시 한 번).
    // 입력: path - 파일 경로, simHz - Tick 속도.
    // 출력: 검증된 카탈로그. 파일이 없거나 내용이 틀리면 InvalidOperationException.
    public static BuildingCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Building data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid building data {path}: {error}");
        return catalog!;
    }

    // 기능: building.json을 읽고 검증한다(리뷰 수정 D3: 관심 영역 칸 크기는 Client 파서와 같은 ProtocolLimits.InterestCellSizes만).
    // 입력: json - 파일 내용, simHz - Tick 속도, catalog·error - 결과.
    // 출력: 맞으면 true와 카탈로그, 아니면 false와 이유.
    public static bool TryParse(string json, int simHz, out BuildingCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }
        if (!DataJson.TryDeserialize(json, out BuildingJson? root, out error)) return false;

        List<MaterialJson?>? list = root!.Materials;
        if (list == null || list.Count != 3)
        {
            error = "\"materials\" must list Wood, Stone and Metal once each.";
            return false;
        }
        var materials = new BuildMaterialConfig[3];
        for (int i = 0; i < list.Count; i++)
        {
            MaterialJson? m = list[i];
            if (m == null || !Enum.TryParse(m.Type, false, out BuildMaterialType type) || !Enum.IsDefined(type) || materials[(int)type] != null)
            {
                error = $"materials[{i}]: type must be Wood, Stone or Metal, each once.";
                return false;
            }
            if (m.ResourceCost < 1 || m.ResourceCost > 1000) return Fail($"materials[{i}]: resourceCost must be 1-1000.", out error);
            if (m.MaxHealth < 1 || m.MaxHealth > ushort.MaxValue) return Fail($"materials[{i}]: maxHealth must be 1-65535.", out error);
            if (!(m.InitialHealthRatio > 0 && m.InitialHealthRatio <= 1)) return Fail($"materials[{i}]: initialHealthRatio must be above 0, at most 1.", out error);
            if (!DataJson.TryTicks(m.ConstructionSeconds, simHz, out ushort constructionTicks))
                return Fail($"materials[{i}]: constructionSeconds must be positive and finite.", out error);
            if (!PositiveFinite(m.StructureDamageMultiplier) || !PositiveFinite(m.HarvestToolDamageMultiplier))
                return Fail($"materials[{i}]: the damage multipliers must be positive and finite.", out error);
            materials[(int)type] = new BuildMaterialConfig(type, m.ResourceCost, m.MaxHealth, (float)m.InitialHealthRatio, constructionTicks,
                (float)m.StructureDamageMultiplier, (float)m.HarvestToolDamageMultiplier);
        }
        if (root.MaxResource < 1 || root.MaxResource > MaxResourceLimit) return Fail("maxResource must be 1-65535.", out error);

        HarvestToolJson? tool = root.HarvestTool;
        if (tool == null) return Fail("\"harvestTool\" is required.", out error);
        if (!PositiveFinite(tool.Range) || tool.Range > 10) return Fail("harvestTool.range must be above 0, at most 10.", out error);
        if (!DataJson.TryTicks(tool.CooldownSeconds, simHz, out ushort cooldownTicks)) return Fail("harvestTool.cooldownSeconds must be positive.", out error);
        if (tool.EnvironmentDamage < 1 || tool.StructureDamage < 1 || tool.EnvironmentDamage > ushort.MaxValue || tool.StructureDamage > ushort.MaxValue)
            return Fail("harvestTool damages must be 1-65535.", out error);
        if (!PositiveFinite(tool.WeakPointRadius) || tool.WeakPointRadius > 2) return Fail("harvestTool.weakPointRadius must be above 0, at most 2.", out error);
        if (tool.WeakPointMultiplier < 1 || tool.WeakPointMultiplier > 10) return Fail("harvestTool.weakPointMultiplier must be 1-10.", out error);

        List<HarvestableJson?>? kinds = root.Harvestables;
        int kindCount = Enum.GetValues<HarvestKind>().Length;
        if (kinds == null || kinds.Count != kindCount) return Fail("\"harvestables\" must list Tree, Rock, Wreck and Crate once each.", out error);
        var harvestables = new HarvestableConfig[kindCount];
        for (int i = 0; i < kinds.Count; i++)
        {
            HarvestableJson? h = kinds[i];
            if (h == null || !Enum.TryParse(h.Kind, false, out HarvestKind kind) || !Enum.IsDefined(kind) || harvestables[(int)kind] != null)
                return Fail($"harvestables[{i}]: kind must be Tree, Rock, Wreck or Crate, each once.", out error);
            if (h.Health < 1 || h.Health > ushort.MaxValue) return Fail($"harvestables[{i}]: health must be 1-65535.", out error);
            if (h.BaseResourcePerHit < 0 || h.BaseResourcePerHit > 100 || h.DestroyBonus < 0 || h.DestroyBonus > 1000)
                return Fail($"harvestables[{i}]: baseResourcePerHit must be 0-100 and destroyBonus 0-1000.", out error);
            // Final review C: HarvestHit.Gained is one byte, so the most one hit can give (a weak point hit that destroys) fits.
            if (h.BaseResourcePerHit * tool.WeakPointMultiplier + h.DestroyBonus > byte.MaxValue)
                return Fail($"harvestables[{i}]: baseResourcePerHit x weakPointMultiplier + destroyBonus must be at most 255.", out error);
            harvestables[(int)kind] = new HarvestableConfig(kind, h.Health, h.BaseResourcePerHit, h.DestroyBonus);
        }

        BuildJson? build = root.Build;
        if (build == null) return Fail("\"build\" is required.", out error);
        if (!PositiveFinite(build.Range) || build.Range > 30) return Fail("build.range must be above 0, at most 30.", out error);
        if (!(build.ViewAngleDegrees > 0 && build.ViewAngleDegrees <= 180)) return Fail("build.viewAngleDegrees must be above 0, at most 180.", out error);
        if (!DataJson.TryTicks(build.MinimumBuildInterval, simHz, out ushort intervalTicks)) return Fail("build.minimumBuildInterval must be positive.", out error);
        if (build.MaxBuildPiecesPerMatch < 1 || build.MaxBuildPiecesPerMatch > 100_000) return Fail("build.maxBuildPiecesPerMatch must be 1-100000.", out error);
        if (build.MaxBuildPiecesPerPlayer < 1 || build.MaxBuildPiecesPerPlayer > build.MaxBuildPiecesPerMatch)
            return Fail("build.maxBuildPiecesPerPlayer must be 1 to maxBuildPiecesPerMatch.", out error);
        if (build.MaxRequestsPerSecond < 1 || build.MaxRequestsPerSecond > 1000) return Fail("build.maxRequestsPerSecond must be 1-1000.", out error);

        InterestJson? interest = root.Interest;
        if (interest == null) return Fail("\"interest\" is required.", out error);
        // The window is a 64-bit mask over the interest cells: at most 8 x 8 cells, and each a whole number of build cells.
        // Review fix D3: the sizes the clients' parser accepts (ProtocolLimits.InterestCellSizes: whole build cells, at most
        // 8 x 8 interest cells over the map).
        if (!ProtocolLimits.IsInterestCellSize((float)interest.CellSize) || interest.CellSize != (float)interest.CellSize)
            return Fail("interest.cellSize must be 20, 40, 80 or 160 (whole build cells, at most 8 x 8 interest cells).", out error);
        if (interest.Radius < 0 || interest.Radius > 7 || interest.KeepMargin < 0 || interest.KeepMargin > 7)
            return Fail("interest.radius and interest.keepMargin must be 0-7.", out error);

        catalog = new BuildingCatalog(materials, harvestables, simHz, root, cooldownTicks, intervalTicks);
        error = null;
        return true;
    }

    // 기능: 값이 유한한 양수인지 본다.
    // 입력: value - 검사할 값.
    // 출력: 유한하고 0보다 크면 true.
    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;

    // 기능: 검증 실패 이유를 error에 담고 false를 돌려준다(TryParse의 한 줄 반환용).
    // 입력: message - 실패 이유, error - 결과.
    // 출력: 항상 false와 error = message.
    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    // The shipped file without its comment (building.json).
    public const string DefaultJson = """
        {
          "materials": [
            { "type": "Wood", "resourceCost": 10, "maxHealth": 150, "initialHealthRatio": 0.3, "constructionSeconds": 1.5, "structureDamageMultiplier": 1.0, "harvestToolDamageMultiplier": 1.0 },
            { "type": "Stone", "resourceCost": 10, "maxHealth": 240, "initialHealthRatio": 0.3, "constructionSeconds": 3.0, "structureDamageMultiplier": 1.0, "harvestToolDamageMultiplier": 1.0 },
            { "type": "Metal", "resourceCost": 10, "maxHealth": 360, "initialHealthRatio": 0.3, "constructionSeconds": 5.0, "structureDamageMultiplier": 1.0, "harvestToolDamageMultiplier": 1.0 }
          ],
          "maxResource": 500,
          "harvestTool": { "range": 2.5, "cooldownSeconds": 0.4, "environmentDamage": 25, "structureDamage": 50, "weakPointRadius": 0.4, "weakPointMultiplier": 2 },
          "harvestables": [
            { "kind": "Tree", "health": 150, "baseResourcePerHit": 6, "destroyBonus": 10 },
            { "kind": "Crate", "health": 100, "baseResourcePerHit": 6, "destroyBonus": 10 },
            { "kind": "Rock", "health": 200, "baseResourcePerHit": 5, "destroyBonus": 10 },
            { "kind": "Wreck", "health": 250, "baseResourcePerHit": 4, "destroyBonus": 10 }
          ],
          "build": { "range": 7.0, "viewAngleDegrees": 75, "minimumBuildInterval": 0.1, "maxBuildPiecesPerMatch": 20000, "maxBuildPiecesPerPlayer": 500, "maxRequestsPerSecond": 20 },
          "interest": { "cellSize": 20, "radius": 2, "keepMargin": 1 }
        }
        """;

    private sealed class BuildingJson
    {
        public List<MaterialJson?>? Materials { get; set; }
        public int MaxResource { get; set; }
        public HarvestToolJson? HarvestTool { get; set; }
        public List<HarvestableJson?>? Harvestables { get; set; }
        public BuildJson? Build { get; set; }
        public InterestJson? Interest { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class MaterialJson
    {
        public string? Type { get; set; }
        public int ResourceCost { get; set; }
        public int MaxHealth { get; set; }
        public double InitialHealthRatio { get; set; }
        public double ConstructionSeconds { get; set; }
        public double StructureDamageMultiplier { get; set; }
        public double HarvestToolDamageMultiplier { get; set; }
    }

    private sealed class HarvestToolJson
    {
        public double Range { get; set; }
        public double CooldownSeconds { get; set; }
        public int EnvironmentDamage { get; set; }
        public int StructureDamage { get; set; }
        public double WeakPointRadius { get; set; }
        public int WeakPointMultiplier { get; set; }
    }

    private sealed class HarvestableJson
    {
        public string? Kind { get; set; }
        public int Health { get; set; }
        public int BaseResourcePerHit { get; set; }
        public int DestroyBonus { get; set; }
    }

    private sealed class BuildJson
    {
        public double Range { get; set; }
        public double ViewAngleDegrees { get; set; }
        public double MinimumBuildInterval { get; set; }
        public int MaxBuildPiecesPerMatch { get; set; }
        public int MaxBuildPiecesPerPlayer { get; set; }
        public int MaxRequestsPerSecond { get; set; }
    }

    private sealed class InterestJson
    {
        public double CellSize { get; set; }
        public int Radius { get; set; }
        public int KeepMargin { get; set; }
    }
}
