using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 12 D9: which doors are open, and the collision world that makes: GameMap.Boxes followed by the closed doors.
// The world lives in one fixed array; only the door part is rewritten, and only when a door changes, so a tick
// allocates nothing. Owned by Match on the game loop thread.
public sealed class DoorSet
{
    private readonly Box[] _world = new Box[GameMap.Boxes.Length + GameMap.DoorCount];
    // World index - GameMap.Boxes.Length -> door index, for the closed doors in the world.
    private readonly int[] _doorOfSlot = new int[GameMap.DoorCount];
    private int _length;

    // 기능: 맵 박스를 충돌 월드 배열에 복사하고 문 부분을 채운다.
    // 입력: 없음.
    // 출력: 모든 문이 닫힌 상태(OpenMask 0)의 DoorSet.
    public DoorSet()
    {
        GameMap.Boxes.CopyTo(_world);
        Rebuild();
    }

    // Bit i = GameMap.Doors[i] is open (the DoorStates packet).
    public byte OpenMask { get; private set; }

    // What every shot and drop of this match collides with. Phase 13 D3: moves gather their own world around the
    // character (CollisionWorld), with the doors' OpenMask.
    public ReadOnlySpan<Box> World => new(_world, 0, _length);

    // 기능: 문이 열려 있는지 OpenMask에서 확인한다.
    // 입력: door - GameMap.Doors 안의 문 번호.
    // 출력: 열려 있으면 true, 닫혀 있으면 false.
    public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

    // 기능: World 안의 박스 번호가 어느 문인지 찾는다.
    // 입력: worldIndex - World 안의 박스 번호.
    // 출력: 그 박스가 닫힌 문이면 문 번호, 맵 박스이거나 World 범위 밖이면 -1.
    // The door whose box is at this index of World, or -1 for a map box (or an index outside World).
    public int DoorAt(int worldIndex)
    {
        int slot = worldIndex - GameMap.Boxes.Length;
        return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
    }

    // 기능: 이동 한 걸음을 막은 문을 찾는다(먼저 막은 충돌체, 문이 아니면 Z 방향 Sweep의 충돌체).
    // 입력: step - 이동 시뮬레이션 한 걸음의 결과.
    // 출력: 막은 것이 문이면 문 번호, 아니면 -1.
    // D9: the door a step was stopped by: the collider that stopped it, or when that is no door, the Z sweep's (a sprint
    // into a doorway a little off-centre meets the jamb on one axis and the door on the other). -1 = no door. Phase 13 D3:
    // the step names colliders by kind and id (CollisionWorld), so a door is Door i wherever it was gathered. The client's
    // PredictedDoors.DoorBlocking is the same.
    public int DoorBlocking(in StepResult step)
    {
        if (step.BlockedBy.Kind == ColliderKind.Door) return (int)step.BlockedBy.Id;
        return step.BlockedByZ.Kind == ColliderKind.Door ? (int)step.BlockedByZ.Id : -1;
    }

    // 기능: 문 하나를 열거나 닫는다.
    // 입력: door - 문 번호, open - 열면 true, 닫으면 false.
    // 출력: 반환값 없음. 상태가 바뀌면 OpenMask와 충돌 월드가 갱신되고, 같으면 아무것도 바뀌지 않는다.
    public void Set(int door, bool open)
    {
        int bit = 1 << door;
        byte mask = (byte)(open ? OpenMask | bit : OpenMask & ~bit);
        if (mask == OpenMask) return;
        OpenMask = mask;
        Rebuild();
    }

    // 기능: 모든 문을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. OpenMask가 0이 되고 충돌 월드에 모든 문이 다시 들어간다(이미 모두 닫혀 있으면 변화 없음).
    // D9: a round starts with every door closed.
    public void CloseAll()
    {
        if (OpenMask == 0) return;
        OpenMask = 0;
        Rebuild();
    }

    // 기능: 맵 박스 뒤의 문 영역을 현재 닫힌 문들로 다시 채운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 충돌 월드의 문 부분, 슬롯별 문 번호, World 길이가 갱신된다.
    private void Rebuild()
    {
        int length = GameMap.Boxes.Length;
        ReadOnlySpan<Box> doors = GameMap.Doors;
        for (int i = 0; i < doors.Length; i++)
        {
            if (IsOpen(i)) continue;
            _doorOfSlot[length - GameMap.Boxes.Length] = i;
            _world[length++] = doors[i];
        }
        _length = length;
    }
}

// Phase 12 D9: which door E acts on. Server rule; the client keeps a copy (Client/Assets/Scripts/Game/DoorRule.cs) only to
// predict the door and show the prompt, and DoorTests.TheClientsCopy_PicksTheSameDoor keeps the two the same.
public static class DoorRules
{
    // 기능: 플레이어가 상호작용(E)할 문을 고른다.
    // 입력: feet - 플레이어 발 위치, yaw - 바라보는 방향(도, 0 = +Z), doors - 후보 문 상자 목록.
    // 출력: 조건을 만족하는 가장 가까운 문 번호, 없으면 -1.
    // The nearest door whose centre is within DoorInteractRange of the feet across the ground and within
    // DoorInteractHalfAngle of the facing direction (yaw 0 = +Z), on the door's floor (the feet between 1 m below its
    // bottom and its top), or -1. Pure, no allocation.
    public static int FindTarget(Vector3 feet, float yaw, ReadOnlySpan<Box> doors)
    {
        float radians = yaw * (MathF.PI / 180f);
        float forwardX = MathF.Sin(radians);
        float forwardZ = MathF.Cos(radians);
        float cosLimit = MathF.Cos(MovementTuning.DoorInteractHalfAngle * (MathF.PI / 180f));
        int best = -1;
        float bestSq = MovementTuning.DoorInteractRange * MovementTuning.DoorInteractRange;
        for (int i = 0; i < doors.Length; i++)
        {
            ref readonly Box door = ref doors[i];
            if (feet.Y < door.Min.Y - 1f || feet.Y > door.Max.Y) continue;
            float dx = (door.Min.X + door.Max.X) * 0.5f - feet.X;
            float dz = (door.Min.Z + door.Max.Z) * 0.5f - feet.Z;
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq > bestSq) continue;
            float distance = MathF.Sqrt(distanceSq);
            if (distance > 1e-4f && dx * forwardX + dz * forwardZ < cosLimit * distance) continue;
            best = i;
            bestSq = distanceSq;
        }
        return best;
    }
}
