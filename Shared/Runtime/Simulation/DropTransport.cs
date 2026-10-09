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

        // 기능: 어떤 서버 Tick(소수 가능)에 수송기가 있는 위치를 낸다. StartTick..EndTick 사이는 직선 보간, 밖은 양 끝에 고정.
        // 입력: tick - 서버 Tick(NaN이면 시작점).
        // 출력: 고도 Altitude의 수송기 위치. DurationTicks가 0이면 항상 끝점.
        // Where the transport is at a (fractional) server tick: StartTick..EndTick along the line, held at the ends.
        public Vector3 PositionAt(double tick)
        {
            double t = DurationTicks == 0 ? 1.0 : (tick - StartTick) / DurationTicks;
            if (!(t > 0.0)) t = 0.0;   // also NaN
            if (t > 1.0) t = 1.0;
            float s = (float)t;
            return new Vector3(StartX + (EndX - StartX) * s, Altitude, StartZ + (EndZ - StartZ) * s);
        }

        // 기능: 수송기가 외벽에서 TransportJumpMargin 이상 안쪽에 있는 Tick 구간(점프 창)을 낸다(D5).
        // 입력: first - 점프가 허용되는 첫 Tick, last - 아직 타고 있으면 강제로 떨어지는 Tick.
        // 출력: 반환값 없음. first·last가 채워진다. 그 사각형에 한 번도 들어오지 않거나 DurationTicks가 0이면 둘 다 EndTick.
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

        // 기능: 한 축에서 start + delta * t가 ±limit 안에 드는 t 구간으로 [enter, leave]를 좁힌다.
        // 입력: start - 축의 시작 좌표, delta - 축의 이동량, limit - 허용 반폭, enter·leave - 좁힐 경로 비율 구간.
        // 출력: 반환값 없음. enter·leave가 좁혀진다. 움직임이 없고 밖에 있으면 enter = 2(절대 안 들어옴).
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
        // 기능: Transport 모드의 캐릭터를 그 Tick의 경로 위치에 두고 시선만 돌린다. 점프 창 안의 점프 입력 또는 창의 끝이면
        //   속도 0으로 그 자리에서 Freefall로 바꾼다(낙하는 다음 Tick의 Step부터. 같은 입력이 글라이더까지 열지 않는다).
        // 입력: state - 이동 상태, input - 이번 Tick 입력(Yaw·Jump), route - 수송기 경로, tick - 시뮬레이션할 서버 Tick.
        // 출력: 타고 있었으면 true(위치·속도·Yaw, 필요하면 Mode가 바뀐다). Transport 모드가 아니면 false이고 아무것도 바꾸지 않는다.
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
