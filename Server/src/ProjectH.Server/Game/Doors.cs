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

    public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

    // The door whose box is at this index of World, or -1 for a map box (or an index outside World).
    public int DoorAt(int worldIndex)
    {
        int slot = worldIndex - GameMap.Boxes.Length;
        return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
    }

    // D9: the door a step was stopped by: the collider that stopped it, or when that is no door, the Z sweep's (a sprint
    // into a doorway a little off-centre meets the jamb on one axis and the door on the other). -1 = no door. Phase 13 D3:
    // the step names colliders by kind and id (CollisionWorld), so a door is Door i wherever it was gathered. The client's
    // PredictedDoors.DoorBlocking is the same.
    public int DoorBlocking(in StepResult step)
    {
        if (step.BlockedBy.Kind == ColliderKind.Door) return (int)step.BlockedBy.Id;
        return step.BlockedByZ.Kind == ColliderKind.Door ? (int)step.BlockedByZ.Id : -1;
    }

    public void Set(int door, bool open)
    {
        int bit = 1 << door;
        byte mask = (byte)(open ? OpenMask | bit : OpenMask & ~bit);
        if (mask == OpenMask) return;
        OpenMask = mask;
        Rebuild();
    }

    // D9: a round starts with every door closed.
    public void CloseAll()
    {
        if (OpenMask == 0) return;
        OpenMask = 0;
        Rebuild();
    }

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
