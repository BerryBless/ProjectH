using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    // The character is an axis-aligned box (MoveSettings.HalfWidth / Height) with its feet at
    // MoveState.Position; the world is the terrain (Phase 6 D2-D4) plus the boxes passed in. Every terrain slope is
    // walkable (GameMapTests), so the terrain never blocks a horizontal move: it only sets the floor height under the feet.
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;
        private const int AxisX = 0;
        private const int AxisY = 1;
        private const int AxisZ = 2;

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain)
        {
            // Untrusted input: non-finite values become 0 and the move vector is clamped to length 1,
            // so no input can exceed the configured speed.
            float moveX = Finite(input.MoveX);
            float moveY = Finite(input.MoveY);
            float lengthSq = moveX * moveX + moveY * moveY;
            if (lengthSq > 1f)
            {
                float inv = 1f / MathF.Sqrt(lengthSq);
                moveX *= inv;
                moveY *= inv;
            }

            if (IsFinite(input.Yaw)) state.Yaw = NormalizeYaw(input.Yaw);

            // Unity convention: yaw rotates around +Y and yaw 0 faces +Z.
            // right = (cos, 0, -sin), forward = (sin, 0, cos).
            float yawRad = state.Yaw * DegToRad;
            float sin = MathF.Sin(yawRad);
            float cos = MathF.Cos(yawRad);
            float speed = (input.Buttons & InputButtons.Sprint) != 0 ? MoveSettings.SprintSpeed : MoveSettings.WalkSpeed;
            float velocityX = (cos * moveX + sin * moveY) * speed;
            float velocityZ = (-sin * moveX + cos * moveY) * speed;

            Vector3 position = state.Position;

            // 1) Leave any box we start inside (reconcile snap, rounding): sweeps assume a free start (D4).
            Depenetrate(ref position, world, terrain);

            // 2) Stateless ground check (D3). Snapping Y onto the surface keeps a standing player at an
            //    exact height, so grounded/airborne never alternates from rounding.
            bool walking = false;
            if (state.VelocityY <= 0f && TryFindGround(position, world, terrain, out float groundY))
            {
                position.Y = groundY;
                bool jump = (input.Buttons & InputButtons.Jump) != 0;
                state.VelocityY = jump ? MoveSettings.JumpSpeed : 0f;
                walking = !jump;
            }
            else
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
            }

            // 3) Axis-separated sweeps X -> Z (D2): a blocked axis stops, the other keeps moving.
            float startX = position.X;
            float startZ = position.Z;
            position.X += Sweep(position, AxisX, velocityX * deltaTime, world, terrain);
            position.Z += Sweep(position, AxisZ, velocityZ * deltaTime, world, terrain);

            // 4) Phase 6 D4: uphill the terrain lifts the feet; downhill a walking character follows the slope instead
            //    of leaving the ground for a tick. MaxSlope bounds the drop over the distance moved, and the Y sweep
            //    stops on a box top on the way down.
            float floor = terrain.Height(position.X, position.Z);
            if (position.Y < floor)
            {
                position.Y = floor;
            }
            else if (walking)
            {
                float dx = position.X - startX;
                float dz = position.Z - startZ;
                float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
                float drop = position.Y - floor;
                if (drop > 0f && drop <= reach) position.Y += Sweep(position, AxisY, -drop, world, terrain);
            }

            // 5) Y sweep, the floor being the terrain.
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, AxisY, wantY, world, terrain);
            if (movedY != wantY) state.VelocityY = 0f;   // landed or hit a ceiling
            position.Y += movedY;

            state.Position = position;
        }

        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world, HeightField terrain)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, world, terrain, out _);
        }

        // True if the character box at these feet overlaps any box by more than zero on every axis.
        // Touching faces (as after a sweep or a ground snap) do not count.
        public static bool OverlapsAny(Vector3 feet, ReadOnlySpan<Box> world)
        {
            GetBounds(feet, out Vector3 min, out Vector3 max);
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (min.X < box.Max.X && max.X > box.Min.X &&
                    min.Y < box.Max.Y && max.Y > box.Min.Y &&
                    min.Z < box.Max.Z && max.Z > box.Min.Z)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Depenetrate(ref Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            if (feet.Y < floor) feet.Y = floor;

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                GetBounds(feet, out Vector3 min, out Vector3 max);

                float overlapX = MathF.Min(max.X, box.Max.X) - MathF.Max(min.X, box.Min.X);
                float overlapY = MathF.Min(max.Y, box.Max.Y) - MathF.Max(min.Y, box.Min.Y);
                float overlapZ = MathF.Min(max.Z, box.Max.Z) - MathF.Max(min.Z, box.Min.Z);
                if (overlapX <= MoveSettings.Skin || overlapY <= MoveSettings.Skin || overlapZ <= MoveSettings.Skin) continue;

                // Distance to clear each face. The smallest wins; ties keep the first in this fixed
                // order (-X, +X, -Z, +Z, +Y, -Y), so the result is deterministic.
                float best = max.X - box.Min.X;
                int direction = 0;
                float push = box.Max.X - min.X;
                if (push < best) { best = push; direction = 1; }
                push = max.Z - box.Min.Z;
                if (push < best) { best = push; direction = 2; }
                push = box.Max.Z - min.Z;
                if (push < best) { best = push; direction = 3; }
                push = box.Max.Y - min.Y;
                if (push < best) { best = push; direction = 4; }
                push = max.Y - box.Min.Y;
                // Pushing down is only allowed while the feet stay above the floor.
                if (push < best && feet.Y - push - MoveSettings.Skin >= floor) { best = push; direction = 5; }

                float distance = best + MoveSettings.Skin;
                switch (direction)
                {
                    case 0: feet.X -= distance; break;
                    case 1: feet.X += distance; break;
                    case 2: feet.Z -= distance; break;
                    case 3: feet.Z += distance; break;
                    case 4: feet.Y += distance; break;
                    default: feet.Y -= distance; break;
                }
            }
        }

        // Highest floor or box top within GroundProbe of the feet, under the character's footprint. The terrain floor
        // is its height under the feet (Phase 6 D4).
        private static bool TryFindGround(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain, out float groundY)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            bool found = feet.Y <= floor + MoveSettings.GroundProbe;
            groundY = floor;

            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= minX || box.Min.X >= maxX || box.Max.Z <= minZ || box.Min.Z >= maxZ) continue;

                float top = box.Max.Y;
                if (top < feet.Y - MoveSettings.GroundProbe || top > feet.Y + MoveSettings.GroundProbe) continue;
                if (!found || top > groundY)
                {
                    groundY = top;
                    found = true;
                }
            }
            return found;
        }

        // How far the character may move along one axis (same sign as delta, |result| <= |delta|).
        // Every box that overlaps on the other two axes and lies ahead limits the move to its near
        // face minus Skin, whatever the distance, so a fast fall cannot pass through a thin box.
        private static float Sweep(Vector3 feet, int axis, float delta, ReadOnlySpan<Box> world, HeightField terrain)
        {
            if (delta == 0f) return 0f;

            GetBounds(feet, out Vector3 min, out Vector3 max);
            float limit = MathF.Abs(delta);
            if (axis == AxisY && delta < 0f)
            {
                // The terrain under the feet is the floor, no Skin.
                float above = min.Y - terrain.Height(feet.X, feet.Z);
                if (above < 0f) above = 0f;
                if (above < limit) limit = above;
            }

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (!OverlapsOnOtherAxes(min, max, box, axis)) continue;

                float gap;
                if (delta > 0f)
                {
                    float near = Component(box.Min, axis);
                    float front = Component(max, axis);
                    if (near < front - MoveSettings.Skin) continue;   // behind us or already overlapping
                    gap = near - front - MoveSettings.Skin;
                }
                else
                {
                    float near = Component(box.Max, axis);
                    float front = Component(min, axis);
                    if (near > front + MoveSettings.Skin) continue;
                    gap = front - near - MoveSettings.Skin;
                }

                if (gap < 0f) gap = 0f;
                if (gap < limit) limit = gap;
            }
            return delta > 0f ? limit : -limit;
        }

        private static bool OverlapsOnOtherAxes(Vector3 min, Vector3 max, in Box box, int axis)
        {
            bool x = axis == AxisX || (min.X < box.Max.X && max.X > box.Min.X);
            bool y = axis == AxisY || (min.Y < box.Max.Y && max.Y > box.Min.Y);
            bool z = axis == AxisZ || (min.Z < box.Max.Z && max.Z > box.Min.Z);
            return x && y && z;
        }

        private static void GetBounds(Vector3 feet, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + MoveSettings.Height, feet.Z + MoveSettings.HalfWidth);
        }

        // System.Numerics.Vector3 has no indexer in netstandard2.1.
        private static float Component(Vector3 v, int axis) => axis == AxisX ? v.X : axis == AxisY ? v.Y : v.Z;

        private static float Finite(float value) => IsFinite(value) ? value : 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
