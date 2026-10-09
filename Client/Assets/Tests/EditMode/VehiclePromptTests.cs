using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Phase 19 D6, D15: the client's copy of the server's enter rule (the server test compares the two on the same records).
    public class VehiclePromptTests
    {
        // 기능: 체력이 가득한 시험용 차량 레코드를 만든다.
        // 입력: id - 차량 ID, x - 위치 x, z - 위치 z, heading - 방향(도), driver - 운전자 ID(0이면 빈 자리), passenger - 동승자 ID(0이면 빈 자리), state - 차량 상태.
        // 출력: 높이 0에 놓인 VehicleRecord.
        private static VehicleRecord Car(byte id, float x, float z, float heading = 0f, ushort driver = 0, ushort passenger = 0,
            VehicleState state = VehicleState.Active) =>
            new VehicleRecord { Id = id, State = state, Position = new Num.Vector3(x, 0f, z), Heading = heading, Driver = driver,
                Passenger = passenger, Health = VehiclePrompt.MaxHealth };

        [Test]
        public void PicksTheNearestActiveVehicleWithAFreeSeat()
        {
            // Heading 0: the footprint squares span x -1.1..1.1, z -2.2..2.2 around the centre.
            var records = new[] { Car(1, 0f, 0f), Car(2, 3.5f, 0f) };
            Assert.AreEqual(0, VehiclePrompt.FindEnterTarget(new Num.Vector3(1.5f, 0.5f, 0f), records, 2));   // 0.4 m vs 0.9 m
            Assert.AreEqual(1, VehiclePrompt.FindEnterTarget(new Num.Vector3(2.3f, 0.5f, 0f), records, 2));   // 1.2 m vs 0.1 m
        }

        [Test]
        public void SkipsWreckedAndFullVehicles_AndTheOnesOutOfRange()
        {
            var feet = new Num.Vector3(1.6f, 0.5f, 0f);   // 0.5 m from the side
            Assert.AreEqual(-1, VehiclePrompt.FindEnterTarget(feet, new[] { Car(1, 0f, 0f, state: VehicleState.Wrecked) }, 1));
            Assert.AreEqual(-1, VehiclePrompt.FindEnterTarget(feet, new[] { Car(1, 0f, 0f, driver: 3, passenger: 4) }, 1));
            Assert.AreEqual(0, VehiclePrompt.FindEnterTarget(feet, new[] { Car(1, 0f, 0f, driver: 3) }, 1));   // the passenger seat
            // Straight below the body (its bottom is 0.5 m above the car's height): exact distances.
            Assert.AreEqual(-1, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f - VehicleSettings.EnterRange - 0.01f, 0f),
                new[] { Car(1, 0f, 0f) }, 1));
            Assert.AreEqual(0, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f - VehicleSettings.EnterRange, 0f),
                new[] { Car(1, 0f, 0f) }, 1));   // inclusive
        }

        [Test]
        public void ATie_GoesToTheLowerId_AndCountIsClamped()
        {
            var records = new[] { Car(9, -2.5f, 0f), Car(4, 2.5f, 0f) };   // 1.4 m each
            Assert.AreEqual(1, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f, 0f), records, 2));
            Assert.AreEqual(0, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f, 0f), records, 1));
            Assert.AreEqual(1, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f, 0f), records, 99));
            Assert.AreEqual(-1, VehiclePrompt.FindEnterTarget(new Num.Vector3(0f, 0.5f, 0f), records, -1));
        }

        [Test]
        public void MaxHealth_IsTheVehiclesJsonDefault()
        {
            Assert.AreEqual(400, VehiclePrompt.MaxHealth);
        }
    }
}
