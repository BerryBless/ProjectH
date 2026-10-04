using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D9 (request §44-§48): the placement checks that need no match state. Pure, no allocation.
public static class BuildRules
{
    // A piece may overlap a map box, a door or a harvestable this deep on every axis (a wall on a house roof slab overlaps
    // it 0.25 m), but not more (request §47, §82).
    public const float MaxMapOverlap = 0.3f;
    // A piece whose top is this far below the terrain at its centre is buried.
    public const float BuriedTolerance = 0.3f;
    // A wall or floor box closer than this along the eye's line to it than a map box or the terrain is "behind" it.
    private const float LineOfSightSlack = 0.05f;

    // 기능: 조각을 놓을 격자 슬롯이 이미 차 있거나 같은 공간을 쓰는 다른 조각이 있는지 검사한다.
    // 입력: world - 현재 Match의 건설 조각 목록, shape - 놓으려는 조각의 격자 위치와 종류.
    // 출력: 슬롯이 차 있거나 겹치는 바닥/지붕이 있으면 true, 비어 있으면 false.
    // D1: the slot is taken, or the piece would share its space with another: a floor and the roof of the level below
    // fill the same slab.
    public static bool Occupied(BuildWorld world, in BuildPieceShape shape)
    {
        if (world.IdAtSlotKey(BuildGrid.SlotKey(shape)) != 0) return true;
        if (shape.Type == BuildPieceType.Floor && shape.Y > 0)
            return world.IdAtSlotKey(BuildGrid.SlotKey(shape.X, shape.Y - 1, shape.Z, BuildSlotKind.Roof)) != 0;
        if (shape.Type == BuildPieceType.Roof && shape.Y + 1 < BuildGrid.Levels)
            return world.IdAtSlotKey(BuildGrid.SlotKey(shape.X, shape.Y + 1, shape.Z, BuildSlotKind.Floor)) != 0;
        return false;
    }

    // 기능: 조각이 건설 사거리 안에 있고 조준 방향이 그 조각을 향하는지 검사한다.
    // 입력: eye - 건설자의 눈 위치, aim - 조준 방향 단위 벡터, shape - 놓으려는 조각, range - 건설 사거리, viewAngleDegrees - 허용 조준 각도(도).
    // 출력: 사거리 안이고 조준선이 조각에 닿거나 중심이 허용 각도 안이면 true, 아니면 false.
    // D9: the piece's centre is within range (plus its own radius) of the eye, and the aim points at it: the aim ray meets
    // its bounds within reach, or its centre is within the view angle of the aim. aim is a unit vector.
    public static bool InReach(Vector3 eye, Vector3 aim, in BuildPieceShape shape, float range, float viewAngleDegrees)
    {
        Box bounds = BuildGrid.BoundsOf(shape);
        Vector3 center = bounds.Center;
        float radius = bounds.Size.Length() * 0.5f;
        Vector3 toCenter = center - eye;
        float distance = toCenter.Length();
        if (!(distance <= range + radius)) return false;
        if (HitScan.IntersectAabb(eye, aim, bounds.Min, bounds.Max, range + 2f * radius, out _)) return true;
        if (distance < 1e-4f) return true;
        float cos = Vector3.Dot(aim, toCenter / distance);
        return cos >= MathF.Cos(viewAngleDegrees * (MathF.PI / 180f));
    }

    // 기능: 눈에서 조각 중심까지의 직선이 조각보다 먼저 벽·닫힌 문·지형에 막히는지 검사한다.
    // 입력: eye - 건설자의 눈 위치, shape - 놓으려는 조각, blockers - 맵 상자와 닫힌 문 상자, terrain - 높이 격자 지형.
    // 출력: 조각에 닿기 전에 막히면(벽 뒤 건설) true, 막히지 않으면 false.
    // D9: the straight line from the eye to the piece's centre meets a map box, a closed door or the terrain before it
    // reaches the piece (building behind a wall). blockers: the map boxes and the closed doors.
    public static bool BehindAWall(Vector3 eye, in BuildPieceShape shape, ReadOnlySpan<Box> blockers, HeightField terrain)
    {
        Box bounds = BuildGrid.BoundsOf(shape);
        Vector3 toCenter = bounds.Center - eye;
        float distance = toCenter.Length();
        if (distance < 1e-4f) return false;
        Vector3 direction = toCenter / distance;
        if (!HitScan.IntersectAabb(eye, direction, bounds.Min, bounds.Max, distance, out float entry)) entry = distance;
        float wall = HitScan.TraceWorld(eye, direction, entry, blockers, terrain);
        return wall < entry - LineOfSightSlack;
    }

    // 기능: 조각의 윗면이 중심 위치의 지형 아래에 묻히는지 검사한다.
    // 입력: shape - 놓으려는 조각, terrain - 높이 격자 지형.
    // 출력: 지형 높이가 조각 윗면보다 BuriedTolerance 넘게 높으면 true, 아니면 false.
    // D9: the piece's top is buried under the terrain at its centre.
    public static bool Buried(in BuildPieceShape shape, HeightField terrain)
    {
        Box bounds = BuildGrid.BoundsOf(shape);
        Vector3 c = bounds.Center;
        return terrain.Height(c.X, c.Z) > bounds.Max.Y + BuriedTolerance;
    }

    // 기능: 조각 상자가 맵 상자와 허용치보다 깊게 겹치는지 검사한다.
    // 입력: shape - 놓으려는 조각, other - 비교할 맵 상자(정적 상자, 문, 채집 대상).
    // 출력: 세 축 모두 허용 겹침 깊이를 넘으면 true, 한 축이라도 넘지 않으면 false.
    // D9: the piece's box overlaps this map box (a static box, a door or a harvestable) deeper on every axis than
    // MaxMapOverlap or half the piece's own size on that axis, whichever is less: a thin wall cutting through a crate
    // counts, a wall standing on a roof slab 0.25 m deep does not.
    public static bool OverlapsTooMuch(in BuildPieceShape shape, in Box other)
    {
        Box b = BuildGrid.BoundsOf(shape);
        Vector3 size = b.Size;
        float x = MathF.Min(b.Max.X, other.Max.X) - MathF.Max(b.Min.X, other.Min.X);
        float y = MathF.Min(b.Max.Y, other.Max.Y) - MathF.Max(b.Min.Y, other.Min.Y);
        float z = MathF.Min(b.Max.Z, other.Max.Z) - MathF.Max(b.Min.Z, other.Min.Z);
        return x > MathF.Min(MaxMapOverlap, size.X * 0.5f) && y > MathF.Min(MaxMapOverlap, size.Y * 0.5f) &&
               z > MathF.Min(MaxMapOverlap, size.Z * 0.5f);
    }

    // 기능: 벽·바닥 조각의 상자 안에 캐릭터 몸 중심이 들어가는지 검사한다.
    // 입력: shape - 놓으려는 조각, feet - 캐릭터 발 위치, height - 캐릭터 키.
    // 출력: 벽·바닥이 몸 중심을 품으면 true, 경사면·지붕이거나 품지 않으면 false.
    // D9: a wall or floor whose box holds a character's body centre would cut through it. Ramps and roofs lift a character
    // standing in them onto their surface instead (D2), so they never count.
    public static bool HoldsBodyCentre(in BuildPieceShape shape, Vector3 feet, float height)
    {
        if (BuildGrid.IsSlope(shape.Type)) return false;
        Box b = BuildGrid.BoxOf(shape);
        Vector3 c = feet + new Vector3(0f, height * 0.5f, 0f);
        return c.X > b.Min.X && c.X < b.Max.X && c.Y > b.Min.Y && c.Y < b.Max.Y && c.Z > b.Min.Z && c.Z < b.Max.Z;
    }

    // 기능: 경사면·지붕 조각이 캐릭터 몸과 겹칠 때 캐릭터를 그 윗면으로 올릴 위치를 구한다.
    // 입력: shape - 놓으려는 조각, feet - 캐릭터 발 위치, height - 캐릭터 키.
    // 출력: 조각이 몸과 겹쳐 들어 올리면 true와 올라간 발 위치(lifted), 아니면 false와 원래 발 위치.
    // Final review B6: a ramp or roof whose slab holds part of a character's body lifts it onto its surface (D2). Returns
    // true and the feet up there when it does; the caller refuses the piece if the body does not fit there.
    public static bool Lifts(in BuildPieceShape shape, Vector3 feet, float height, out Vector3 lifted)
    {
        lifted = feet;
        if (!BuildGrid.IsSlope(shape.Type)) return false;
        Slope slope = BuildGrid.SlopeOf(shape);
        const float w = MoveSettings.HalfWidth;
        if (!slope.Range(feet.X - w, feet.Z - w, feet.X + w, feet.Z + w, out _, out float high, out float bottom)) return false;
        if (!(feet.Y < high - MoveSettings.Skin && feet.Y + height > bottom + MoveSettings.Skin)) return false;
        lifted.Y = high;
        return true;
    }
}
