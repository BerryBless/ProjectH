using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 12 D5: the drop transport's straight route at a fixed altitude. The server plans it at the match start and
    // sends it once (TransportRoute); from then on both sides compute where the transport is from these values with the
    // same code, so the transport is never sent per tick. Pure math, no allocation.
    public struct DropRoute
    {
        public float StartX;
        public float StartZ;
        public float EndX;
        public float EndZ;
        public float Altitude;
        public uint StartTick;
        public uint DurationTicks;   // at least 1

        public uint EndTick => StartTick + DurationTicks;

        // Where the transport is at a (fractional) server tick: StartTick..EndTick along the line, held at the ends.
        public Vector3 PositionAt(double tick)
        {
            double t = DurationTicks == 0 ? 1.0 : (tick - StartTick) / DurationTicks;
            if (!(t > 0.0)) t = 0.0;   // also NaN
            if (t > 1.0) t = 1.0;
            float s = (float)t;
            return new Vector3(StartX + (EndX - StartX) * s, Altitude, StartZ + (EndZ - StartZ) * s);
        }

        // D5: the ticks while the transport is at least TransportJumpMargin inside the outer walls. A jump counts only
        // from first on; whoever is still aboard at last is dropped. A route that never enters that square (only a
        // broken route can) gives first = last = EndTick.
        public void JumpWindow(out uint first, out uint last)
        {
            const float limit = GameMap.HalfSize - MovementTuning.TransportJumpMargin;
            double enter = 0.0;
            double leave = 1.0;
            Clip(StartX, EndX - StartX, limit, ref enter, ref leave);
            Clip(StartZ, EndZ - StartZ, limit, ref enter, ref leave);
            if (DurationTicks == 0 || enter > leave)
            {
                first = EndTick;
                last = EndTick;
                return;
            }
            first = StartTick + (uint)Math.Ceiling(enter * DurationTicks);
            last = StartTick + (uint)Math.Floor(leave * DurationTicks);
            if (last < first) last = first;
        }

        // Narrows [enter, leave] (fractions of the route) to where start + delta * t is within +-limit.
        private static void Clip(float start, float delta, float limit, ref double enter, ref double leave)
        {
            if (Math.Abs(delta) < 1e-6f)
            {
                if (start < -limit || start > limit) enter = 2.0;   // never inside
                return;
            }
            double a = (-limit - start) / (double)delta;
            double b = (limit - start) / (double)delta;
            if (a > b)
            {
                double swap = a;
                a = b;
                b = swap;
            }
            if (a > enter) enter = a;
            if (b < leave) leave = b;
        }
    }

    // D5, D6: riding the transport. Server and prediction call Ride for a character in Transport mode instead of
    // MovementSimulation.Step, with the server tick that input is simulated at.
    public static class DropTransport
    {
        // Places a rider on the route at tick and lets it look around. A jump press inside the jump window, or the end of
        // the window, drops it into Freefall right there with no velocity; the fall starts with the next tick's Step, so
        // the same press does not also open the glider. Returns false (and does nothing) when the character is not riding.
        public static bool Ride(ref MoveState state, in InputCommand input, in DropRoute route, uint tick)
        {
            if (state.Mode != MovementMode.Transport) return false;
            MovementSimulation.ApplyYaw(ref state, input.Yaw);
            state.Position = route.PositionAt(tick);
            state.VelocityY = 0f;
            state.HorizontalVelocity = Vector2.Zero;
            route.JumpWindow(out uint first, out uint last);
            bool jump = (input.Buttons & InputButtons.Jump) != 0 && tick >= first;
            if (jump || tick >= last) state.Mode = MovementMode.Freefall;
            return true;
        }
    }
}
