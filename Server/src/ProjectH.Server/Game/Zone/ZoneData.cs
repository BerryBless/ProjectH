using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace ProjectH.Server.Game.Zone;

// One zone phase (D6, D7): wait, then shrink to TargetRadius. DamagePerSecond applies outside the circle for
// the whole phase (its wait and its shrink). Tick values are already in simulation ticks.
public readonly struct ZonePhase
{
    // 기능: 검증이 끝난 단계 하나를 담는다.
    // 입력: waitTicks - 대기 Tick, shrinkTicks - 축소 Tick, targetRadius - 목표 반지름, damagePerSecond - 원 밖 초당 피해.
    // 출력: 값이 채워진 ZonePhase.
    public ZonePhase(uint waitTicks, uint shrinkTicks, float targetRadius, ushort damagePerSecond)
    {
        WaitTicks = waitTicks;
        ShrinkTicks = shrinkTicks;
        TargetRadius = targetRadius;
        DamagePerSecond = damagePerSecond;
    }

    public uint WaitTicks { get; }
    public uint ShrinkTicks { get; }
    public float TargetRadius { get; }
    public ushort DamagePerSecond { get; }
}

// zones.json (D6, spec §1): the first circle, the arena bound for zone centers and the phases. Loaded and
// validated once at startup; a bad file stops the server. Immutable afterwards, so the game loop reads it
// without locks. Centers are on the ground plane: Vector2.X = world X, Vector2.Y = world Z.
public sealed class ZoneData
{
    public const int MaxPhases = 16;   // the wire Phase is a byte; 16 keeps the data readable

    private readonly ZonePhase[] _phases;

    // 기능: 검증을 마친 값들로 자기장 데이터를 만든다(TryParse만 호출한다).
    // 입력: initialCenter - 첫 원 중심(X, Z), initialRadius - 첫 원 반지름, arenaHalfSize - 중심이 머무는 경기장 반폭,
    //   phases - 단계들, simHz - 변환에 쓴 Tick 속도.
    // 출력: 바뀌지 않는 ZoneData.
    private ZoneData(Vector2 initialCenter, float initialRadius, float arenaHalfSize, ZonePhase[] phases, int simHz)
    {
        InitialCenter = initialCenter;
        InitialRadius = initialRadius;
        ArenaHalfSize = arenaHalfSize;
        _phases = phases;
        SimHz = simHz;
    }

    public Vector2 InitialCenter { get; }
    public float InitialRadius { get; }
    // A zone center never leaves [-ArenaHalfSize, ArenaHalfSize] on X and Z (D6: the inside of the arena walls).
    public float ArenaHalfSize { get; }
    public int PhaseCount => _phases.Length;
    // Tick values were converted with this rate; GameData refuses zones built for another SimHz.
    public int SimHz { get; }

    // 기능: 단계 하나의 수치를 돌려준다.
    // 입력: index - 단계 색인(0 = 단계 1).
    // 출력: 그 단계의 ZonePhase.
    public ZonePhase Phase(int index) => _phases[index];

    // 기능: zones.json 파일을 읽어 검증한 데이터를 만든다(시작 시 한 번).
    // 입력: path - 파일 경로, simHz - Tick 속도.
    // 출력: 검증된 ZoneData. 파일이 없거나 내용이 틀리면 InvalidOperationException.
    public static ZoneData LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Zone data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var data, out string? error))
            throw new InvalidOperationException($"Invalid zone data {path}: {error}");
        return data!;
    }

    // 기능: zones.json을 읽고 검증한다: 첫 원이 경기장 안, 단계 1..MaxPhases개, 반지름은 단계마다 줄고 마지막은 0에 피해 > 0(D7).
    // 입력: json - 파일 내용, simHz - Tick 속도, data·error - 결과.
    // 출력: 맞으면 true와 데이터, 틀리면 false와 이유.
    public static bool TryParse(string json, int simHz, out ZoneData? data, out string? error)
    {
        data = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }
        if (!DataJson.TryDeserialize(json, out ZonesJson? root, out error)) return false;

        double[]? center = root!.InitialCenter;
        if (center == null || center.Length != 2 || !double.IsFinite(center[0]) || !double.IsFinite(center[1]))
        {
            error = "\"initialCenter\" must be [x, z] with two finite numbers.";
            return false;
        }
        if (!double.IsFinite(root.ArenaHalfSize) || root.ArenaHalfSize <= 0 || root.ArenaHalfSize > 10000)
        {
            error = "\"arenaHalfSize\" must be above 0 and at most 10000.";
            return false;
        }
        var initialCenter = new Vector2((float)center[0], (float)center[1]);
        float half = (float)root.ArenaHalfSize;
        if (MathF.Abs(initialCenter.X) > half || MathF.Abs(initialCenter.Y) > half)
        {
            error = "\"initialCenter\" must lie within arenaHalfSize on both axes.";
            return false;
        }
        if (!double.IsFinite(root.InitialRadius) || root.InitialRadius <= 0 || root.InitialRadius > 10000)
        {
            error = "\"initialRadius\" must be above 0 and at most 10000.";
            return false;
        }

        List<PhaseJson?>? list = root.Phases;
        if (list == null || list.Count < 1 || list.Count > MaxPhases)
        {
            error = $"\"phases\" must hold 1-{MaxPhases} entries.";
            return false;
        }

        var phases = new ZonePhase[list.Count];
        float previousRadius = (float)root.InitialRadius;
        for (int i = 0; i < list.Count; i++)
        {
            PhaseJson? p = list[i];
            if (p == null)
            {
                error = $"phases[{i}]: entry is null.";
                return false;
            }
            if (!DataJson.TryTicks(p.WaitSeconds, simHz, out ushort waitTicks))
            {
                error = $"phases[{i}]: waitSeconds must be positive and finite (at most 65535 ticks).";
                return false;
            }
            if (!DataJson.TryTicks(p.ShrinkSeconds, simHz, out ushort shrinkTicks))
            {
                error = $"phases[{i}]: shrinkSeconds must be positive and finite (at most 65535 ticks).";
                return false;
            }
            if (!double.IsFinite(p.TargetRadius) || p.TargetRadius < 0)
            {
                error = $"phases[{i}]: targetRadius must be 0 or more.";
                return false;
            }
            float radius = (float)p.TargetRadius;
            // The first target may equal the first circle (initialRadius >= the first target radius); after that
            // every phase must shrink.
            if (i == 0 ? radius > previousRadius : radius >= previousRadius)
            {
                error = i == 0
                    ? "phases[0]: targetRadius must be at most initialRadius."
                    : $"phases[{i}]: targetRadius must be below the previous phase's.";
                return false;
            }
            if (p.DamagePerSecond < 0 || p.DamagePerSecond > ushort.MaxValue)
            {
                error = $"phases[{i}]: damagePerSecond must be 0-65535.";
                return false;
            }
            phases[i] = new ZonePhase(waitTicks, shrinkTicks, radius, (ushort)p.DamagePerSecond);
            previousRadius = radius;
        }

        // Every match must end (D7): the last circle closes to radius 0 and hurts, so nobody survives it.
        ZonePhase last = phases[phases.Length - 1];
        if (last.TargetRadius != 0f || last.DamagePerSecond == 0)
        {
            error = "the last phase must have targetRadius 0 and damagePerSecond above 0, so every match ends.";
            return false;
        }

        data = new ZoneData(initialCenter, (float)root.InitialRadius, half, phases, simHz);
        error = null;
        return true;
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class ZonesJson
    {
        public double[]? InitialCenter { get; set; }
        public double InitialRadius { get; set; }
        public double ArenaHalfSize { get; set; }
        public List<PhaseJson?>? Phases { get; set; }
    }

    private sealed class PhaseJson
    {
        public double WaitSeconds { get; set; }
        public double ShrinkSeconds { get; set; }
        public double TargetRadius { get; set; }
        public int DamagePerSecond { get; set; }
    }
}
