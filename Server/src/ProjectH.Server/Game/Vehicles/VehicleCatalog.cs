using System;
using System.IO;

namespace ProjectH.Server.Game.Vehicles;

// Phase 19 D12: vehicles.json, the server-only vehicle numbers. Loaded and validated once at startup like the other data
// files (a bad file stops the server); immutable afterwards, so the game loop reads it without locks. Tick values are
// already in simulation ticks. The motion numbers, the enter range and the seats are Shared VehicleSettings (the driver's
// prediction needs them).
public sealed class VehicleCatalog
{
    public const string FileName = "vehicles.json";

    private VehicleCatalog(int simHz, VehicleJson root, uint runOverCooldownTicks, uint wreckTicks, uint wreckCreditTicks)
    {
        SimHz = simHz;
        MaxHealth = root.MaxHealth;
        ImpactMinSpeed = (float)root.ImpactMinSpeed;
        ImpactDamagePerMps = (float)root.ImpactDamagePerMps;
        RunOverMinSpeed = (float)root.RunOverMinSpeed;
        RunOverDamagePerMps = (float)root.RunOverDamagePerMps;
        RunOverCooldownTicks = runOverCooldownTicks;
        WreckTicks = wreckTicks;
        WreckOccupantDamage = root.WreckOccupantDamage;
        WreckCreditTicks = wreckCreditTicks;
        InterestRange = (float)root.InterestRange;
    }

    public int SimHz { get; }
    // D7: a vehicle's health; at 0 it is wrecked.
    public int MaxHealth { get; }
    // D3: a block faster than ImpactMinSpeed hurts the vehicle (speed - min) x per-m/s.
    public float ImpactMinSpeed { get; }
    public float ImpactDamagePerMps { get; }
    // D3: a vehicle faster than RunOverMinSpeed hurts a non-team player it overlaps speed x per-m/s, once per cooldown.
    public float RunOverMinSpeed { get; }
    public float RunOverDamagePerMps { get; }
    public uint RunOverCooldownTicks { get; }
    // D7: a wreck stays WreckTicks; its occupants take WreckOccupantDamage, credited to the last enemy who damaged the vehicle
    // within WreckCreditTicks.
    public uint WreckTicks { get; }
    public int WreckOccupantDamage { get; }
    public uint WreckCreditTicks { get; }
    // D4: VehicleStates carries the vehicles within this range of the recipient (plus its own).
    public float InterestRange { get; }

    // 기능: 배포하는 기본값(vehicles.json과 같다, VehicleCatalogTests가 둘을 묶는다)으로 카탈로그를 만든다. 파일 없이 GameData를 만드는 테스트용.
    // 입력: simHz - 서버 Tick 속도.
    // 출력: 기본 수치의 VehicleCatalog. 기본값이 틀리면 예외.
    public static VehicleCatalog Default(int simHz)
    {
        if (!TryParse(DefaultJson, simHz, out var catalog, out string? error))
            throw new InvalidOperationException("The default vehicle data is invalid: " + error);
        return catalog!;
    }

    // 기능: vehicles.json 파일을 읽고 검증한다(시작 때 한 번).
    // 입력: path - 파일 경로, simHz - 서버 Tick 속도.
    // 출력: 검증된 VehicleCatalog. 파일이 없거나 틀리면 InvalidOperationException(서버가 시작하지 않는다).
    public static VehicleCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Vehicle data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid vehicle data {path}: {error}");
        return catalog!;
    }

    // 기능: vehicles.json 내용을 읽고 모든 값을 범위 검사한다.
    // 입력: json - 파일 내용, simHz - 서버 Tick 속도.
    // 출력: 성공하면 true와 카탈로그, 실패하면 false와 이유.
    public static bool TryParse(string json, int simHz, out VehicleCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1) return Fail("SimHz must be positive.", out error);
        if (!DataJson.TryDeserialize(json, out VehicleJson? root, out error)) return false;
        // Health goes on the wire as u16 (VehicleRecord.Health).
        if (root!.MaxHealth < 1 || root.MaxHealth > ushort.MaxValue) return Fail($"maxHealth must be 1-{ushort.MaxValue}.", out error);
        if (!InRange(root.ImpactMinSpeed, 0, 50)) return Fail("impactMinSpeed must be 0-50.", out error);
        if (!InRange(root.ImpactDamagePerMps, 0, 1000)) return Fail("impactDamagePerMps must be 0-1000.", out error);
        if (!InRange(root.RunOverMinSpeed, 0, 50)) return Fail("runOverMinSpeed must be 0-50.", out error);
        if (!InRange(root.RunOverDamagePerMps, 0, 1000)) return Fail("runOverDamagePerMps must be 0-1000.", out error);
        if (!Seconds(root.RunOverCooldownSeconds, 0.1, 60, simHz, out uint cooldown)) return Fail("runOverCooldownSeconds must be 0.1-60.", out error);
        if (!Seconds(root.WreckSeconds, 0.5, 600, simHz, out uint wreck)) return Fail("wreckSeconds must be 0.5-600.", out error);
        if (root.WreckOccupantDamage < 0 || root.WreckOccupantDamage > 1000) return Fail("wreckOccupantDamage must be 0-1000.", out error);
        if (!Seconds(root.WreckCreditSeconds, 0.1, 600, simHz, out uint credit)) return Fail("wreckCreditSeconds must be 0.1-600.", out error);
        if (!InRange(root.InterestRange, 10, 1000)) return Fail("interestRange must be 10-1000.", out error);
        catalog = new VehicleCatalog(simHz, root, cooldown, wreck, credit);
        error = null;
        return true;
    }

    // 기능: 값이 유한하고 [min, max] 안인지 본다.
    // 입력: value - 값, min·max - 범위.
    // 출력: 안이면 true.
    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    // 기능: [min, max] 안의 초를 정수 Tick으로 바꾼다(최소 1).
    // 입력: seconds - 초, min·max - 범위, simHz - Tick 속도, ticks - 결과.
    // 출력: 범위 안이면 true와 Tick 수.
    private static bool Seconds(double seconds, double min, double max, int simHz, out uint ticks)
    {
        ticks = 0;
        if (!InRange(seconds, min, max)) return false;
        ticks = (uint)Math.Max(1, Math.Round(seconds * simHz, MidpointRounding.AwayFromZero));
        return true;
    }

    // 기능: 검증 실패 이유를 담고 false를 돌려준다.
    // 입력: message - 이유, error - 결과.
    // 출력: 항상 false.
    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    // The shipped file without its comment (vehicles.json).
    public const string DefaultJson = """
        {
          "maxHealth": 400,
          "impactMinSpeed": 8,
          "impactDamagePerMps": 6,
          "runOverMinSpeed": 6,
          "runOverDamagePerMps": 2,
          "runOverCooldownSeconds": 1,
          "wreckSeconds": 5,
          "wreckOccupantDamage": 25,
          "wreckCreditSeconds": 10,
          "interestRange": 120
        }
        """;

    // Missing numbers stay 0 and fail validation where 0 is out of range.
    private sealed class VehicleJson
    {
        public int MaxHealth { get; set; }
        public double ImpactMinSpeed { get; set; }
        public double ImpactDamagePerMps { get; set; }
        public double RunOverMinSpeed { get; set; }
        public double RunOverDamagePerMps { get; set; }
        public double RunOverCooldownSeconds { get; set; }
        public double WreckSeconds { get; set; }
        public int WreckOccupantDamage { get; set; }
        public double WreckCreditSeconds { get; set; }
        public double InterestRange { get; set; }
    }
}
