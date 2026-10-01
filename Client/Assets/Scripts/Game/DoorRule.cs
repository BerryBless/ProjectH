using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Copy of the server's door rule (Server DoorRules.FindTarget, Phase 12 D9), used to predict E on a door and to show
    // the "[E] 문" prompt. The server still decides. Keep the two in step (DoorTests.TheClientsCopy_PicksTheSameDoor compares them): the nearest
    // door whose centre is within DoorInteractRange of the feet across the ground and within DoorInteractHalfAngle of
    // the facing direction, on the door's floor. Pure, no allocation, no UnityEngine (the server tests compile it).
    public static class DoorRule
    {
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
}
