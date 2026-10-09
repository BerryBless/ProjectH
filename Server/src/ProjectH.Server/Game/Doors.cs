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

    // 기능: 맵 상자를 복사하고 모든 문이 닫힌 충돌 세계를 만든다.
    // 입력: 없음.
    // 출력: OpenMask 0, World = GameMap.Boxes + 닫힌 문 전부인 DoorSet.
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

    // 기능: 문이 열려 있는지 OpenMask의 비트로 확인한다.
    // 입력: door - GameMap.Doors의 문 index.
    // 출력: 열려 있으면 true, 닫혀 있으면 false.
    public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

    // 기능: World의 한 index가 어느 문의 상자인지 찾는다.
    // 입력: worldIndex - World 안의 상자 index.
    // 출력: 닫힌 문의 상자면 그 문 index, 맵 상자이거나 World 밖이면 -1.
    // The door whose box is at this index of World, or -1 for a map box (or an index outside World).
    public int DoorAt(int worldIndex)
    {
        int slot = worldIndex - GameMap.Boxes.Length;
        return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
    }

    // 기능: 이동 Step을 막은 충돌체가 문이면 어느 문인지 찾는다(막은 충돌체가 문이 아니면 Z 축 Sweep의 충돌체를 본다).
    // 입력: step - 이동 Step 결과.
    // 출력: 막은 문의 index, 문에 막히지 않았으면 -1.
    // D9: the door a step was stopped by: the collider that stopped it, or when that is no door, the Z sweep's (a sprint
    // into a doorway a little off-centre meets the jamb on one axis and the door on the other). -1 = no door. Phase 13 D3:
    // the step names colliders by kind and id (CollisionWorld), so a door is Door i wherever it was gathered. The client's
    // PredictedDoors.DoorBlocking is the same.
    public int DoorBlocking(in StepResult step)
    {
        if (step.BlockedBy.Kind == ColliderKind.Door) return (int)step.BlockedBy.Id;
        return step.BlockedByZ.Kind == ColliderKind.Door ? (int)step.BlockedByZ.Id : -1;
    }

    // 기능: 문 하나를 열거나 닫고, 상태가 바뀌었을 때만 충돌 세계를 다시 만든다.
    // 입력: door - 문 index, open - true면 열기, false면 닫기.
    // 출력: 반환값 없음. OpenMask와 World가 갱신된다(이미 같은 상태면 그대로).
    public void Set(int door, bool open)
    {
        int bit = 1 << door;
        byte mask = (byte)(open ? OpenMask | bit : OpenMask & ~bit);
        if (mask == OpenMask) return;
        OpenMask = mask;
        Rebuild();
    }

    // 기능: 모든 문을 닫는다(라운드 시작).
    // 입력: 없음.
    // 출력: 반환값 없음. OpenMask가 0이 되고 World에 모든 문이 들어간다(이미 전부 닫혀 있으면 그대로).
    // D9: a round starts with every door closed.
    public void CloseAll()
    {
        if (OpenMask == 0) return;
        OpenMask = 0;
        Rebuild();
    }

    // 기능: World의 문 부분을 현재 OpenMask로 다시 쓴다(맵 상자 뒤에 닫힌 문만 순서대로).
    // 입력: 없음.
    // 출력: 반환값 없음. _world의 문 구간, _doorOfSlot, _length가 갱신된다. 할당 없음.
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
    // 기능: E 키가 작용할 문을 고른다: 발 위치에서 수평 거리 DoorInteractRange 안, 바라보는 방향의 DoorInteractHalfAngle 안, 같은 층에 있는 가장 가까운 문.
    // 입력: feet - 플레이어 발 위치, yaw - 바라보는 방향(도, 0 = +Z), doors - 문 상자 목록(GameMap.Doors).
    // 출력: 대상 문의 index, 없으면 -1. 순수 함수, 할당 없음.
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
