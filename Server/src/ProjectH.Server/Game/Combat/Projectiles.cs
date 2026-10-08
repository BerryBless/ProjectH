using System;
using System.Numerics;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Phase 17 D6: one live projectile (a grenade or a rocket), a slot of ProjectileSet. Position and Velocity are its state at
// the end of the last simulated tick. The owner is kept by entity id (for ProjectileSpawned), JoinOrder (the kill credit and
// the self check: entity ids are reused, join orders never) and the team it had at the launch (teams are made again at
// every match start, and every projectile is cleared then).
public struct Projectile
{
    public bool Active;
    public ushort Id;
    public ProjectileDefinition Definition;
    public Vector3 Position;
    public Vector3 Velocity;
    public bool Resting;          // a grenade that came to rest: no gravity, no movement until it explodes
    public uint ExplodeTick;      // the tick its fuse or lifetime ends at (it explodes in that tick's update)
    public ushort OwnerEntityId;
    public uint OwnerJoinOrder;
    public byte OwnerTeam;
    public float DamageMultiplier; // the launcher's rarity (a grenade: Common, 1)
}

// Phase 17 D6: the match's projectiles, a fixed array of Capacity slots. Full = no new projectile (the launch fails and spends
// nothing). A slot is freed when its projectile explodes, and every slot at a match start, a round reset and the match end.
// Game loop thread only, no allocation after construction.
public sealed class ProjectileSet
{
    public const int Capacity = 32;

    private readonly Projectile[] _slots = new Projectile[Capacity];
    private ushort _nextId;

    public int Count { get; private set; }
    public bool HasFree => Count < Capacity;

    // 기능: slot번째 칸을 돌려준다(Active가 false면 빈 칸).
    // 입력: slot - 0..Capacity-1.
    // 출력: 칸 참조.
    public ref Projectile this[int slot] => ref _slots[slot];

    // 기능: 빈 칸 하나에 새 투사체를 넣는다(id는 1부터 증가·순환, 0은 쓰지 않는다).
    // 입력: projectile - 넣을 값(Active·Id는 여기서 정한다), slot - 결과 칸.
    // 출력: 넣었으면 true와 칸, 가득 찼으면 false.
    public bool TryAdd(in Projectile projectile, out int slot)
    {
        slot = -1;
        if (Count >= Capacity) return false;
        for (int i = 0; i < Capacity; i++)
        {
            if (_slots[i].Active) continue;
            if (++_nextId == 0) _nextId = 1;
            _slots[i] = projectile;
            _slots[i].Active = true;
            _slots[i].Id = _nextId;
            Count++;
            slot = i;
            return true;
        }
        return false;   // not reached: Count < Capacity means a free slot
    }

    // 기능: 주인 JoinOrder가 같은 살아 있는 투사체 수를 센다(리뷰 수정 C5). 32칸을 훑는다. 할당 없음.
    // 입력: ownerJoinOrder - 주인의 JoinOrder.
    // 출력: 그 주인의 투사체 수.
    public int CountOwnedBy(uint ownerJoinOrder)
    {
        int count = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (_slots[i].Active && _slots[i].OwnerJoinOrder == ownerJoinOrder) count++;
        }
        return count;
    }

    // 기능: 칸을 비운다.
    // 입력: slot - 칸.
    // 출력: 반환값 없음.
    public void Remove(int slot)
    {
        if (!_slots[slot].Active) return;
        _slots[slot] = default;
        Count--;
    }

    // 기능: 모든 칸을 비운다(경기 시작·라운드 리셋·경기 끝, 아무것도 보내지 않는다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Clear()
    {
        Array.Clear(_slots);
        Count = 0;
    }
}

// Phase 17 D6: the pure motion and surface rules of projectiles. No allocation, no exceptions.
public static class ProjectileRules
{
    // D6: below this speed (m/s) after a bounce a grenade rests (only on a surface facing up, CanRest).
    public const float RestSpeed = 1f;
    // Review fix C5 (SEC-13): the most live projectiles one player may own (rockets and grenades together), so one player
    // cannot fill the 32 shared slots. More are refused before anything is spent, like full slots.
    public const int MaxPerOwner = 4;
    // Review fix: the least upward normal (cos 45 degrees) a grenade can rest on: floors, the terrain and ramps, not walls
    // or ceilings.
    public const float RestNormalY = 0.7f;

    // 기능: 튕긴 수류탄이 이 면에서 멈출지 본다: 튕긴 속도가 RestSpeed 아래이고 면이 위를 향할 때(normal.Y ≥ RestNormalY)만.
    // 입력: normal - 맞은 면의 단위 법선, bounced - 튕긴 속도.
    // 출력: 멈추면 true. 벽·천장은 늘 false(중력이 계속 작용한다).
    public static bool CanRest(Vector3 normal, Vector3 bounced) => normal.Y >= RestNormalY && bounced.Length() < RestSpeed;
    // After an impact the projectile is put this far out along the surface normal, so the next tick's segment does not
    // start inside what it hit and an explosion's line of sight starts in the open.
    public const float SurfaceOffset = 0.05f;
    private const float ParallelEpsilon = 1e-8f;

    // 기능: 한 Tick의 정확한 등가속 적분(p += v·dt + ½·a·dt², v += a·dt). Client의 외삽 공식과 같은 궤적이다.
    // 입력: position·velocity - 상태(갱신된다), gravity - 아래로 당기는 가속, dt - Tick 길이(초).
    // 출력: 반환값 없음.
    public static void Advance(ref Vector3 position, ref Vector3 velocity, float gravity, float dt)
    {
        var a = new Vector3(0f, -gravity, 0f);
        position += velocity * dt + a * (0.5f * dt * dt);
        velocity += a * dt;
    }

    // 기능: 맞은 면의 법선으로 속도를 반사하고 튕김 계수를 곱한다.
    // 입력: velocity - 맞을 때 속도, normal - 단위 면 법선(속도와 반대쪽), bounce - 계수.
    // 출력: 튕긴 속도.
    public static Vector3 Reflect(Vector3 velocity, Vector3 normal, float bounce) =>
        (velocity - 2f * Vector3.Dot(velocity, normal) * normal) * bounce;

    // 기능: 광선이 처음 맞히는 상자와 그 면의 법선(맵 상자·닫힌 문·서 있는 채집 대상).
    // 입력: origin·direction - 광선(단위 방향), range - 최대 거리, boxes - 상자들, normal - 결과 법선.
    // 출력: 맞은 거리, 없으면 range(normal = 0).
    public static float TraceBoxes(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> boxes, out Vector3 normal)
    {
        float nearest = range;
        normal = Vector3.Zero;
        for (int i = 0; i < boxes.Length; i++)
        {
            if (EnterBox(origin, direction, boxes[i].Min, boxes[i].Max, nearest, out float t, out Vector3 n))
            {
                nearest = t;
                normal = n;
            }
        }
        return nearest;
    }

    // 기능: 광선이 처음 맞히는 맵 표면(상자, y = 0 바닥면, 지형)과 법선. HitScan.TraceWorld와 같은 순서·규칙이다.
    // 입력: origin·direction - 광선, range - 최대 거리, boxes - 막는 상자들, terrain - 지형, normal - 결과 법선.
    // 출력: 맞은 거리, 없으면 range.
    public static float TraceWorld(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> boxes, HeightField terrain, out Vector3 normal)
    {
        float nearest = TraceBoxes(origin, direction, range, boxes, out normal);
        if (direction.Y < -ParallelEpsilon && origin.Y >= 0f)
        {
            float toFloor = -origin.Y / direction.Y;
            if (toFloor < nearest)
            {
                nearest = toFloor;
                normal = Vector3.UnitY;
            }
        }
        float toTerrain = HitScan.TraceTerrain(origin, direction, nearest, terrain);
        if (toTerrain < nearest)
        {
            nearest = toTerrain;
            Vector3 p = origin + direction * toTerrain;
            Vector2 g = terrain.Gradient(p.X, p.Z);
            normal = Vector3.Normalize(new Vector3(-g.X, 1f, -g.Y));
        }
        return nearest;
    }

    // 기능: 광선이 맞힌 조각 면의 법선(벽·바닥 부분은 들어간 상자 면, Ramp·한쪽 경사 지붕은 경사 평면, 사각뿔 지붕은 맞은 쪽 면).
    // 입력: shape - 조각 모양, origin·direction - 광선, distance - PieceTrace가 낸 거리.
    // 출력: 광선 반대쪽을 향하는 단위 법선.
    public static Vector3 PieceNormal(in BuildPieceShape shape, Vector3 origin, Vector3 direction, float distance)
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        int count = BuildGrid.PartsOf(shape, parts, out bool slope);
        Vector3 normal;
        if (!slope)
        {
            TraceBoxes(origin, direction, distance + 0.01f, parts.Slice(0, count), out normal);
            if (normal == Vector3.Zero) normal = -direction;
        }
        else
        {
            Slope s = BuildGrid.SlopeOf(shape);
            Vector3 p = origin + direction * distance;
            if (s.IsPlane)
            {
                float rise = s.Rise / BuildGrid.CellSize;
                float ax = 0f, az = 0f;
                switch (s.Direction)
                {
                    case 0: az = 1f; break;
                    case 1: ax = 1f; break;
                    case 2: az = -1f; break;
                    default: ax = -1f; break;
                }
                normal = Vector3.Normalize(new Vector3(-rise * ax, 1f, -rise * az));
            }
            else
            {
                const float rise = BuildGrid.RampRise / BuildGrid.CellSize;
                float dx = p.X - (s.MinX + s.MaxX) * 0.5f;
                float dz = p.Z - (s.MinZ + s.MaxZ) * 0.5f;
                normal = MathF.Abs(dx) >= MathF.Abs(dz)
                    ? Vector3.Normalize(new Vector3(MathF.Sign(dx) * rise, 1f, 0f))
                    : Vector3.Normalize(new Vector3(0f, 1f, MathF.Sign(dz) * rise));
            }
        }
        return Vector3.Dot(normal, direction) > 0f ? -normal : normal;
    }

    // 기능: 광선이 상자에 들어가는 거리와 들어간 면의 법선(slab 검사). 시작점이 안이면 거리 0, 법선 = -방향.
    // 입력: origin·direction - 광선, min·max - 상자, maxDistance - 최대 거리, t·normal - 결과.
    // 출력: maxDistance 안에서 들어가면 true.
    private static bool EnterBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maxDistance, out float t, out Vector3 normal)
    {
        t = 0f;
        normal = Vector3.Zero;
        float tMin = 0f;
        float tMax = maxDistance;
        int axis = -1;
        float sign = 0f;
        for (int a = 0; a < 3; a++)
        {
            float o = a == 0 ? origin.X : a == 1 ? origin.Y : origin.Z;
            float d = a == 0 ? direction.X : a == 1 ? direction.Y : direction.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < ParallelEpsilon)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            float inverse = 1f / d;
            float t1 = (lo - o) * inverse;
            float t2 = (hi - o) * inverse;
            float entrySign = -1f;   // entering through the low face: the normal points to -axis
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
                entrySign = 1f;
            }
            if (t1 > tMin)
            {
                tMin = t1;
                axis = a;
                sign = entrySign;
            }
            if (t2 < tMax) tMax = t2;
            if (tMin > tMax) return false;
        }
        t = tMin;
        normal = axis switch
        {
            0 => new Vector3(sign, 0f, 0f),
            1 => new Vector3(0f, sign, 0f),
            2 => new Vector3(0f, 0f, sign),
            _ => -direction,
        };
        return true;
    }
}
