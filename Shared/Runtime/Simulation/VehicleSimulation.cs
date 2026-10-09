using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 19 D2: everything VehicleSimulation needs to continue from one step to the next.
    public struct VehicleMove
    {
        // The car's centre on the ground: Y is the highest terrain under the centre and both axles (no air state).
        public Vector3 Position;
        // Degrees 0..360, Unity convention: 0 faces +Z, 90 faces +X.
        public float Heading;
        // m/s along the heading; negative = reversing.
        public float Speed;
        // -1..1, the last steering input (the client smooths it for display only).
        public float Steer;
    }

    // Phase 19 D2, D3: what one step reports besides the new state.
    public struct VehicleStepResult
    {
        // The move (and turn) of this tick was cancelled: a box, a slope, too steep a rise or the map bound.
        public bool Blocked;
        // |Speed| the car was moving at when it was blocked (0 when not blocked). The server's impact damage reads it.
        public float ImpactSpeed;
        // The collider that blocked it (None: not blocked, or the terrain or the map bound).
        public ColliderId BlockedBy;
    }

    // Phase 19 D2: the car's motion. Server and the driver's prediction run exactly this code (game-core-rules §4 exception:
    // vehicle motion). Pure math on the terrain, the gathered boxes and slopes; no allocation, no engine types.
    // Input: MoveY throttle (+ forward, - reverse), MoveX steering, Jump brake, Sprint boost. No driver = a brake every tick.
    // A blocked tick restores the position and the heading and stops the car: a car turned into a wall would otherwise see
    // that wall as "already overlapping" next tick and drive through it. Boxes the car already overlaps at the start of the
    // step are ignored (so it can always drive out of a piece built onto it). Cars do not block each other or players.
    public static class VehicleSimulation
    {
        private const float DegToRad = 0.017453292f;

        // 기능: 모은 충돌 세계(CollisionWorld.Gather를 Step 전 차량 위치에서 부른 결과)로 한 Tick을 계산한다.
        // 입력: state - 이어서 계산할 상태, input - 운전자 입력(검증 전), hasDriver - 운전자가 있는지(false면 제동), deltaTime - Tick 길이(초),
        //   world - Step 전 위치에서 모은 충돌체, terrain - 지형.
        // 출력: 반환값 없음. state가 다음 Tick 상태로 바뀌고 result에 막힘·충돌 속도·막은 충돌체가 담긴다.
        public static void Step(ref VehicleMove state, in InputCommand input, bool hasDriver, float deltaTime, CollisionWorld world,
            HeightField terrain, out VehicleStepResult result)
        {
            Run(ref state, input, hasDriver, deltaTime, world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain, out result);
        }

        // 기능: 상자·경사면 목록으로 한 Tick을 계산한다(테스트용. 상자 이름은 Static i, 경사면 이름은 None).
        // 입력: state - 상태, input - 운전자 입력, hasDriver - 운전자 유무, deltaTime - Tick 길이, boxes·slopes - 충돌체, terrain - 지형.
        // 출력: 반환값 없음. state와 result가 채워진다.
        public static void Step(ref VehicleMove state, in InputCommand input, bool hasDriver, float deltaTime, ReadOnlySpan<Box> boxes,
            ReadOnlySpan<Slope> slopes, HeightField terrain, out VehicleStepResult result)
        {
            Run(ref state, input, hasDriver, deltaTime, boxes, default, slopes, default, terrain, out result);
        }

        // 기능: 속도·회전·이동을 계산하고 경계·경사·발자국 겹침이면 이동과 회전을 되돌리고 멈춘다(D2).
        // 입력: state·input·hasDriver·deltaTime·terrain - Step과 같다, boxes·boxIds·slopes·slopeIds - 충돌체와 이름(상자 이름이 없으면 Static i,
        //   경사면 이름이 없으면 None).
        // 출력: 반환값 없음. state와 result가 채워진다.
        private static void Run(ref VehicleMove state, in InputCommand input, bool hasDriver, float deltaTime, ReadOnlySpan<Box> boxes,
            ReadOnlySpan<ColliderId> boxIds, ReadOnlySpan<Slope> slopes, ReadOnlySpan<ColliderId> slopeIds, HeightField terrain,
            out VehicleStepResult result)
        {
            result = new VehicleStepResult();
            if (!(deltaTime > 0f)) return;

            // Untrusted input: non-finite becomes 0, both axes clamped to -1..1.
            float throttle = Clamp1(Finite(input.MoveY));
            float steer = Clamp1(Finite(input.MoveX));
            bool brake = (input.Buttons & InputButtons.Jump) != 0;
            bool boost = (input.Buttons & InputButtons.Sprint) != 0;
            if (!hasDriver)
            {
                throttle = 0f;
                steer = 0f;
                brake = true;
                boost = false;
            }
            state.Steer = steer;
            state.Speed = NextSpeed(Finite(state.Speed), throttle, brake, boost, deltaTime);

            VehicleMove start = state;
            float speed = state.Speed;
            float turn = steer * VehicleSettings.TurnRateDegrees * MathF.Min(1f, MathF.Abs(speed) / VehicleSettings.FullTurnSpeed) * MathF.Sign(speed);
            float heading = NormalizeHeading(state.Heading + turn * deltaTime);
            if (speed == 0f && heading == start.Heading) return;   // standing still: nothing moves, nothing can block

            float rad = heading * DegToRad;
            var forward = new Vector2(MathF.Sin(rad), MathF.Cos(rad));
            float x = start.Position.X + forward.X * speed * deltaTime;
            float z = start.Position.Z + forward.Y * speed * deltaTime;

            ColliderId blocker = ColliderId.None;
            bool blocked = x < -VehicleSettings.MapBound || x > VehicleSettings.MapBound || z < -VehicleSettings.MapBound || z > VehicleSettings.MapBound;
            if (!blocked && speed != 0f)
            {
                // D2: the rise along the travel direction (backwards when reversing) at the new centre.
                Vector2 gradient = terrain.Gradient(x, z);
                float rise = (gradient.X * forward.X + gradient.Y * forward.Y) * MathF.Sign(speed);
                blocked = rise > VehicleSettings.MaxClimbSlope;
            }
            float y = GroundHeight(x, z, heading, terrain);
            if (!blocked)
                blocked = Overlaps(new Vector3(x, y, z), heading, start.Position, start.Heading, boxes, boxIds, slopes, slopeIds, out blocker);

            if (blocked)
            {
                state.Position = start.Position;
                state.Heading = start.Heading;
                state.Speed = 0f;
                result.Blocked = true;
                result.ImpactSpeed = MathF.Abs(speed);
                result.BlockedBy = blocker;
                return;
            }
            state.Position = new Vector3(x, y, z);
            state.Heading = heading;
        }

        // 기능: 입력으로 다음 속도를 정한다(가속·부스트·후진·제동·굴러감, 반대 방향 입력은 제동, 최고 속도 위면 굴러감으로 줄인다).
        // 입력: speed - 지금 속도, throttle - -1..1, brake - 제동, boost - 부스트, deltaTime - Tick 길이.
        // 출력: 다음 속도(m/s).
        private static float NextSpeed(float speed, float throttle, bool brake, bool boost, float deltaTime)
        {
            if (brake) return Toward(speed, 0f, VehicleSettings.BrakeDeceleration * deltaTime);
            if (throttle > 0f)
            {
                if (speed < 0f) return Toward(speed, 0f, VehicleSettings.BrakeDeceleration * deltaTime);
                float max = boost ? VehicleSettings.BoostMaxSpeed : VehicleSettings.MaxForwardSpeed;
                if (speed > max) return MathF.Max(max, speed - VehicleSettings.CoastDeceleration * deltaTime);
                float acceleration = boost ? VehicleSettings.BoostAcceleration : VehicleSettings.Acceleration;
                return MathF.Min(max, speed + acceleration * throttle * deltaTime);
            }
            if (throttle < 0f)
            {
                if (speed > 0f) return Toward(speed, 0f, VehicleSettings.BrakeDeceleration * deltaTime);
                if (speed < -VehicleSettings.MaxReverseSpeed)
                    return MathF.Min(-VehicleSettings.MaxReverseSpeed, speed + VehicleSettings.CoastDeceleration * deltaTime);
                return MathF.Max(-VehicleSettings.MaxReverseSpeed, speed + VehicleSettings.Acceleration * throttle * deltaTime);
            }
            return Toward(speed, 0f, VehicleSettings.CoastDeceleration * deltaTime);
        }

        // 기능: 차량 높이를 정한다(D2: 차체 중심과 앞·뒤 축 지형 높이 중 가장 높은 값).
        // 입력: x·z - 중심, heading - 방향(도), terrain - 지형.
        // 출력: 차량 Y.
        public static float GroundHeight(float x, float z, float heading, HeightField terrain)
        {
            float rad = heading * DegToRad;
            float ax = MathF.Sin(rad) * VehicleSettings.AxleOffset;
            float az = MathF.Cos(rad) * VehicleSettings.AxleOffset;
            float h = terrain.Height(x, z);
            h = MathF.Max(h, terrain.Height(x + ax, z + az));
            return MathF.Max(h, terrain.Height(x - ax, z - az));
        }

        // 기능: 발자국 정사각형 하나(앞 축 0, 뒤 축 1)의 상자를 돌려준다(축 정렬, 차량 높이 + BodyBottom ~ + BodyTop).
        // 입력: position - 차량 중심, heading - 방향(도), axle - 0 앞 축, 1 뒤 축.
        // 출력: 그 축의 차체 상자.
        public static Box FootprintBox(Vector3 position, float heading, int axle)
        {
            float rad = heading * DegToRad;
            float sign = axle == 0 ? 1f : -1f;
            float cx = position.X + MathF.Sin(rad) * VehicleSettings.AxleOffset * sign;
            float cz = position.Z + MathF.Cos(rad) * VehicleSettings.AxleOffset * sign;
            const float half = VehicleSettings.BodySquareSize * 0.5f;
            return new Box(new Vector3(cx - half, position.Y + VehicleSettings.BodyBottom, cz - half),
                new Vector3(cx + half, position.Y + VehicleSettings.BodyTop, cz + half));
        }

        // 기능: 발에서 차체(두 발자국 상자 중 가까운 것)까지의 거리를 잰다(D6 타기 거리, 서버 검증과 Client 안내가 같이 쓴다).
        // 입력: feet - 발 위치, position - 차량 중심, heading - 방향(도).
        // 출력: 가장 가까운 상자 표면까지 거리(m, 안이면 0).
        public static float DistanceToBody(Vector3 feet, Vector3 position, float heading)
        {
            float a = DistanceToBox(feet, FootprintBox(position, heading, 0));
            float b = DistanceToBox(feet, FootprintBox(position, heading, 1));
            return MathF.Min(a, b);
        }

        // 기능: 좌석에 앉은 플레이어의 발 위치를 구한다(D5).
        // 입력: position - 차량 중심, heading - 방향(도), seat - 좌석 번호.
        // 출력: 월드 발 위치.
        public static Vector3 SeatPosition(Vector3 position, float heading, int seat)
        {
            Vector3 local = VehicleSettings.SeatOffset(seat);
            float rad = heading * DegToRad;
            float sin = MathF.Sin(rad);
            float cos = MathF.Cos(rad);
            // right = (cos, 0, -sin), forward = (sin, 0, cos), the same frame as MovementSimulation.
            return new Vector3(position.X + cos * local.X + sin * local.Z, position.Y + local.Y, position.Z - sin * local.X + cos * local.Z);
        }

        // 기능: 새 발자국이 상자·경사면 경계와 겹치는지 본다. 시작 발자국이 이미 겹친 것은 무시한다(D2 끼임 방지).
        // 입력: to·heading - 새 위치와 방향, from·fromHeading - 시작 위치와 방향, boxes·boxIds·slopes·slopeIds - 충돌체, blocker - 결과.
        // 출력: 새로 겹치는 것이 있으면 true와 그 충돌체 이름(목록 순서상 첫 번째).
        private static bool Overlaps(Vector3 to, float heading, Vector3 from, float fromHeading, ReadOnlySpan<Box> boxes, ReadOnlySpan<ColliderId> boxIds,
            ReadOnlySpan<Slope> slopes, ReadOnlySpan<ColliderId> slopeIds, out ColliderId blocker)
        {
            blocker = ColliderId.None;
            Box front = FootprintBox(to, heading, 0);
            Box rear = FootprintBox(to, heading, 1);
            Box startFront = FootprintBox(from, fromHeading, 0);
            Box startRear = FootprintBox(from, fromHeading, 1);
            for (int i = 0; i < boxes.Length; i++)
            {
                ref readonly Box box = ref boxes[i];
                if (!Touches(front, box) && !Touches(rear, box)) continue;
                if (Touches(startFront, box) || Touches(startRear, box)) continue;
                blocker = i < boxIds.Length ? boxIds[i] : new ColliderId(ColliderKind.Static, (uint)i);
                return true;
            }
            for (int i = 0; i < slopes.Length; i++)
            {
                Box bounds = BoundsOf(slopes[i]);
                if (!Touches(front, bounds) && !Touches(rear, bounds)) continue;
                if (Touches(startFront, bounds) || Touches(startRear, bounds)) continue;
                blocker = i < slopeIds.Length ? slopeIds[i] : ColliderId.None;
                return true;
            }
            return false;
        }

        // 기능: 경사면의 경계 상자(칸 범위, 고체 바닥 ~ 꼭대기)를 돌려준다(차량은 경사면을 오르지 않고 경계를 벽처럼 본다).
        // 입력: slope - 경사면.
        // 출력: 경계 상자.
        public static Box BoundsOf(in Slope slope) =>
            new Box(new Vector3(slope.MinX, slope.BaseY - BuildGrid.SlopeThickness, slope.MinZ), new Vector3(slope.MaxX, slope.Top, slope.MaxZ));

        // 기능: 두 상자가 모든 축에서 0보다 크게 겹치는지 본다(면이 닿기만 하면 겹침이 아니다).
        // 입력: a, b - 상자.
        // 출력: 겹치면 true.
        public static bool Touches(in Box a, in Box b) =>
            a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y && a.Min.Z < b.Max.Z && a.Max.Z > b.Min.Z;

        // 기능: 점에서 상자까지의 거리를 잰다.
        // 입력: p - 점, box - 상자.
        // 출력: 거리(안이면 0).
        public static float DistanceToBox(Vector3 p, in Box box)
        {
            float dx = MathF.Max(0f, MathF.Max(box.Min.X - p.X, p.X - box.Max.X));
            float dy = MathF.Max(0f, MathF.Max(box.Min.Y - p.Y, p.Y - box.Max.Y));
            float dz = MathF.Max(0f, MathF.Max(box.Min.Z - p.Z, p.Z - box.Max.Z));
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // 기능: 방향을 0..360으로 맞춘다(유한하지 않으면 0).
        // 입력: heading - 도.
        // 출력: 0 이상 360 미만의 도.
        public static float NormalizeHeading(float heading)
        {
            if (float.IsNaN(heading) || float.IsInfinity(heading)) return 0f;
            float h = heading % 360f;
            if (h < 0f) h += 360f;
            return h >= 360f ? 0f : h;
        }

        // 기능: 값을 목표 쪽으로 최대 step만큼 옮긴다(넘지 않는다).
        // 입력: value - 지금 값, target - 목표, step - 최대 변화량.
        // 출력: 옮긴 값.
        private static float Toward(float value, float target, float step) =>
            value > target ? MathF.Max(target, value - step) : MathF.Min(target, value + step);

        // 기능: 유한하지 않은 값을 0으로 바꾼다(검증 전 입력).
        // 입력: value - 값.
        // 출력: 유한하면 그대로, 아니면 0.
        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;

        // 기능: 값을 -1..1로 자른다.
        // 입력: value - 값.
        // 출력: 자른 값.
        private static float Clamp1(float value) => value > 1f ? 1f : value < -1f ? -1f : value;
    }
}
