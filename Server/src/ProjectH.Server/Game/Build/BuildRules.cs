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

    // 기능: 조각의 slot이 이미 차 있거나 같은 공간을 다른 조각과 나누는지 본다(D1: 바닥과 아래 층 지붕은 같은 판을 채운다).
    // 입력: world - 경기의 조각 저장소, shape - 놓으려는 모양.
    // 출력: 자리가 막혀 있으면 true.
    public static bool Occupied(BuildWorld world, in BuildPieceShape shape)
    {
        if (world.IdAtSlotKey(BuildGrid.SlotKey(shape)) != 0) return true;
        if (shape.Type == BuildPieceType.Floor && shape.Y > 0)
            return world.IdAtSlotKey(BuildGrid.SlotKey(shape.X, shape.Y - 1, shape.Z, BuildSlotKind.Roof)) != 0;
        if (shape.Type == BuildPieceType.Roof && shape.Y + 1 < BuildGrid.Levels)
            return world.IdAtSlotKey(BuildGrid.SlotKey(shape.X, shape.Y + 1, shape.Z, BuildSlotKind.Floor)) != 0;
        return false;
    }

    // 기능: 조각 중심이 눈에서 사거리(+조각 반지름) 안에 있고 조준이 그 조각을 향하는지 본다(D9): 조준 Ray가 상자를 맞히거나
    //   중심이 시야각 안이면 된다.
    // 입력: eye - 눈 위치, aim - 조준 방향(단위 벡터), shape - 조각 모양, range - 사거리, viewAngleDegrees - 허용 시야각(도).
    // 출력: 손이 닿으면 true.
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

    // 기능: 눈에서 조각 중심까지의 직선이 조각에 닿기 전에 맵 상자·닫힌 문·지형에 먼저 막히는지 본다(D9: 벽 너머 건설).
    // 입력: eye - 눈 위치, shape - 조각 모양, blockers - 맵 상자와 닫힌 문 상자, terrain - 지형 높이.
    // 출력: 조각보다 먼저 막히면 true.
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

    // 기능: 눈에서 조각 중심까지의 선분이 대상이 아닌 다른 조각을 대상보다 먼저 맞히는지 본다(Phase 13.5 D5-6 편집 시선).
    //   대상 자신은 보지 않으므로 창·문 너머의 중심도 대상 탓에 막히지 않는다.
    // 입력: eye - 눈 위치, shape - 대상 모양, targetId - 대상 조각 id, world - 조각들.
    // 출력: 다른 조각이 먼저 맞으면 true.
    public static bool BehindAPiece(Vector3 eye, in BuildPieceShape shape, uint targetId, BuildWorld world)
    {
        Box bounds = BuildGrid.BoundsOf(shape);
        Vector3 toCenter = bounds.Center - eye;
        float distance = toCenter.Length();
        if (distance < 1e-4f) return false;
        Vector3 direction = toCenter / distance;
        if (!HitScan.IntersectAabb(eye, direction, bounds.Min, bounds.Max, distance, out float entry)) entry = distance;
        float range = entry - LineOfSightSlack;
        if (!(range > 0f)) return false;
        return PieceTrace.Trace(eye, direction, range, world, out _, out _, out _, targetId);
    }

    // 기능: 조각 중심에서 지형이 조각 윗면보다 BuriedTolerance 넘게 높은지 본다(D9: 땅에 묻힌 조각).
    // 입력: shape - 조각 모양, terrain - 지형 높이.
    // 출력: 묻혀 있으면 true.
    public static bool Buried(in BuildPieceShape shape, HeightField terrain)
    {
        Box bounds = BuildGrid.BoundsOf(shape);
        Vector3 c = bounds.Center;
        return terrain.Height(c.X, c.Z) > bounds.Max.Y + BuriedTolerance;
    }

    // 기능: 조각 상자가 맵 상자(정적 상자·문·채집물)와 세 축 모두 허용 깊이(MaxMapOverlap 또는 조각 크기의 절반 중 작은 쪽)보다
    //   깊게 겹치는지 본다(D9). 얇은 벽이 상자를 가르면 겹침, 지붕 판 위 0.25 m는 겹침이 아니다.
    // 입력: shape - 조각 모양, other - 맵 상자.
    // 출력: 너무 깊게 겹치면 true.
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

    // 기능: 조각의 상자 부분(PartsOf) 중 하나가 캐릭터의 몸 중심을 품는지 본다(D9: 그 몸을 가른다). 경사면(Ramp, 사각뿔·한쪽
    //   경사 지붕)은 몸을 표면 위로 올리므로(D2, Lifts) 세지 않는다. Phase 13.5: 편집된 벽은 남은 부분만 본다.
    // 입력: shape - 조각 모양, feet - 발 위치, height - 몸 높이.
    // 출력: 몸 중심이 상자 부분 안에 있으면 true.
    public static bool HoldsBodyCentre(in BuildPieceShape shape, Vector3 feet, float height)
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        int count = BuildGrid.PartsOf(shape, parts, out _);
        for (int i = 0; i < count; i++)
        {
            if (BoxHoldsBodyCentre(parts[i], feet, height)) return true;
        }
        return false;
    }

    // 기능: 상자 하나가 캐릭터의 몸 중심을 품는지 본다(경계는 열린 구간).
    // 입력: b - 상자, feet - 발 위치, height - 몸 높이.
    // 출력: 몸 중심이 상자 안에 있으면 true.
    public static bool BoxHoldsBodyCentre(in Box b, Vector3 feet, float height)
    {
        Vector3 c = feet + new Vector3(0f, height * 0.5f, 0f);
        return c.X > b.Min.X && c.X < b.Max.X && c.Y > b.Min.Y && c.Y < b.Max.Y && c.Z > b.Min.Z && c.Z < b.Max.Z;
    }

    // 기능: 플레이어가 이 조각을 편집할 수 있는지 본다(Phase 13.5 D5-3, 요청서 §12). 지금은 소유자 본인만. Phase 14 팀 공유는
    //   이 함수만 고친다.
    // 입력: editor - 편집하려는 플레이어의 Entity id, piece - 대상 조각.
    // 출력: 편집할 수 있으면 true.
    public static bool CanEdit(ushort editor, in BuildPiece piece) => piece.Owner != 0 && piece.Owner == editor;

    // 기능: 경사면 조각의 판이 캐릭터 몸 일부를 품으면 표면 위로 올린 발 위치를 낸다(Final review B6, D2). 호출자는 그 자리에
    //   몸이 들어가지 않으면 배치·편집을 거부한다. Phase 13.5: 평지붕·통로는 경사면이 아니다(HasSlope).
    // 입력: shape - 조각 모양, feet - 발 위치, height - 몸 높이, lifted - 결과.
    // 출력: 올리면 true와 올린 발 위치, 아니면 false(lifted = feet).
    public static bool Lifts(in BuildPieceShape shape, Vector3 feet, float height, out Vector3 lifted)
    {
        lifted = feet;
        if (!BuildGrid.HasSlope(shape)) return false;
        Slope slope = BuildGrid.SlopeOf(shape);
        const float w = MoveSettings.HalfWidth;
        if (!slope.Range(feet.X - w, feet.Z - w, feet.X + w, feet.Z + w, out _, out float high, out float bottom)) return false;
        if (!(feet.Y < high - MoveSettings.Skin && feet.Y + height > bottom + MoveSettings.Skin)) return false;
        lifted.Y = high;
        return true;
    }
}
