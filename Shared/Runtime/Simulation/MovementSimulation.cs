using System;

namespace ProjectH.Shared.Simulation
{
    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime)
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

            bool grounded = state.Position.Y <= 0f && state.VelocityY <= 0f;
            if (grounded)
            {
                state.VelocityY = (input.Buttons & InputButtons.Jump) != 0 ? MoveSettings.JumpSpeed : 0f;
            }
            else
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
            }

            var position = state.Position;
            position.X += velocityX * deltaTime;
            position.Z += velocityZ * deltaTime;
            position.Y += state.VelocityY * deltaTime;
            if (position.Y < 0f)
            {
                position.Y = 0f;
                state.VelocityY = 0f;
            }
            state.Position = position;
        }

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
