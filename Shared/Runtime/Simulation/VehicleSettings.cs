using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 19 D1, D2, D6: every vehicle number the driver's prediction and the server both need. Constants, not server
    // config: a value changed on one side only would make every prediction wrong (like MoveSettings). The server-only
    // numbers (health, impact and run-over damage, the wreck time, the interest range) are the server's vehicles.json.
    // One vehicle kind: a four-wheeled car with two seats.
    public static class VehicleSettings
    {
        // D1: at most this many vehicles in a match (VehicleStates carries all of them in one packet).
        public const int MaxVehicles = 8;
        // D1: seat 0 drives, seat 1 rides along (no actions). An array, so more seats are only more entries.
        public const int SeatCount = 2;
        public const int DriverSeat = 0;
        public const int PassengerSeat = 1;

        // D2 motion. Speed in m/s (sign = forward/back), accelerations in m/s^2.
        public const float Acceleration = 8f;
        public const float BoostAcceleration = 12f;
        public const float MaxForwardSpeed = 20f;
        public const float BoostMaxSpeed = 26f;
        public const float MaxReverseSpeed = 6f;
        // A brake (Jump), or an input against the motion, slows by this.
        public const float BrakeDeceleration = 24f;
        // No throttle: the car rolls to a stop at this rate.
        public const float CoastDeceleration = 4f;
        // Turn rate = Steer x TurnRateDegrees x min(1, |Speed| / FullTurnSpeed) x sign(Speed): a standing car does not turn.
        public const float TurnRateDegrees = 75f;
        public const float FullTurnSpeed = 5f;
        // The steepest terrain rise (along the travel direction) the car drives up; steeper blocks like a wall.
        public const float MaxClimbSlope = 0.8f;
        // The car's centre stays within this of the map centre on X and Z (the outer walls are at GameMap.HalfSize).
        public const float MapBound = 78f;

        // D2 footprint: two axis-aligned squares of BodySquareSize around the front and rear axle centres (AxleOffset ahead of
        // and behind the centre along the heading), from BodyBottom to BodyTop above the car's height. Axis-aligned squares do
        // not turn with the car, so collision stays the same box-overlap math as the rest of the world.
        public const float AxleOffset = 1.1f;
        public const float BodySquareSize = 2.2f;
        public const float BodyBottom = 0.5f;
        public const float BodyTop = 1.8f;

        // D6: a player enters from at most this far (feet to the nearest footprint square).
        public const float EnterRange = 1.5f;

        // D5: where a seated player's feet are, in the car's frame: X right, Y up from the car's height, Z forward.
        private static readonly Vector3[] s_seats =
        {
            new Vector3(-0.5f, 0.6f, 0.2f),   // driver, left
            new Vector3(0.5f, 0.6f, 0.2f),    // passenger, right
        };

        // 기능: 좌석 하나의 차량 기준 위치를 돌려준다.
        // 입력: seat - 좌석 번호(0..SeatCount-1, 범위 밖은 운전석).
        // 출력: 차량 기준(X 오른쪽, Y 위, Z 앞) 발 위치.
        public static Vector3 SeatOffset(int seat) => seat >= 0 && seat < s_seats.Length ? s_seats[seat] : s_seats[DriverSeat];
    }
}
