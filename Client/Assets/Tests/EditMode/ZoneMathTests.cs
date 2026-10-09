using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // D11, D14: the client draws the circle the server judges with. The same inputs and expected values are pinned
    // on the server side for SafeZone (ZoneMathParityTests compiles this ZoneMath and compares it with SafeZone).
    public class ZoneMathTests
    {
        // 기능: 서버 ZoneMathParityTests와 같은 수치의 시험용 자기장 상태를 만든다(Phase 2, (2,-4) r20 → (6,0) r12, Tick 1000..1360).
        // 입력: 없음.
        // 출력: ZoneState.
        private static ZoneState Zone() => new ZoneState
        {
            Phase = 2,
            FromX = 2f,
            FromZ = -4f,
            FromRadius = 20f,
            ToX = 6f,
            ToZ = 0f,
            ToRadius = 12f,
            ShrinkStartTick = 1000,
            ShrinkEndTick = 1360,
            DamagePerSecond = 2,
        };

        [Test]
        public void BeforeTheShrink_IsTheFromCircle()
        {
            ZoneMath.Sample(Zone(), 999.5, out float x, out float z, out float r);
            Assert.AreEqual((2f, -4f, 20f), (x, z, r));
            ZoneMath.Sample(Zone(), 1000, out x, out z, out r);
            Assert.AreEqual((2f, -4f, 20f), (x, z, r));
        }

        [Test]
        public void DuringTheShrink_MovesLinearly()
        {
            ZoneMath.Sample(Zone(), 1180, out float x, out float z, out float r);   // halfway
            Assert.AreEqual(4f, x, 1e-5f);
            Assert.AreEqual(-2f, z, 1e-5f);
            Assert.AreEqual(16f, r, 1e-5f);
            ZoneMath.Sample(Zone(), 1090, out _, out _, out r);                      // a quarter
            Assert.AreEqual(18f, r, 1e-5f);
        }

        [Test]
        public void AfterTheShrink_IsTheToCircle()
        {
            ZoneMath.Sample(Zone(), 1360, out float x, out float z, out float r);
            Assert.AreEqual((6f, 0f, 12f), (x, z, r));
            ZoneMath.Sample(Zone(), 99999, out x, out z, out r);
            Assert.AreEqual((6f, 0f, 12f), (x, z, r));
        }

        [Test]
        public void NoShrinkTime_JumpsWithoutDividingByZero()
        {
            ZoneState zone = Zone();
            zone.ShrinkEndTick = zone.ShrinkStartTick;
            ZoneMath.Sample(zone, 1000, out _, out _, out float r);
            Assert.AreEqual(20f, r);
            ZoneMath.Sample(zone, 1000.01, out _, out _, out r);
            Assert.AreEqual(12f, r);
        }

        [Test]
        public void IsOutside_IsHorizontal_TheEdgeIsInside_AndRadiusZeroHasNoInside()
        {
            ZoneState zone = Zone();
            Assert.IsFalse(ZoneMath.IsOutside(zone, 6f, 0f, 2000));
            Assert.IsFalse(ZoneMath.IsOutside(zone, 18f, 0f, 2000));   // exactly on the edge
            Assert.IsTrue(ZoneMath.IsOutside(zone, 18.01f, 0f, 2000));
            Assert.IsFalse(ZoneMath.IsOutside(zone, 18.01f, 0f, 500));  // the bigger From circle

            zone.ToRadius = 0f;
            Assert.IsTrue(ZoneMath.IsOutside(zone, 6f, 0f, 2000));      // on the final center
        }

        [Test]
        public void Hint_CountsDownToTheShrink_ThenSaysClosing()
        {
            ZoneState zone = Zone();
            Assert.AreEqual(ZoneHint.ShrinksIn, ZoneMath.Hint(zone, 640, 30, out int seconds));
            Assert.AreEqual(12, seconds);
            Assert.AreEqual(ZoneHint.ShrinksIn, ZoneMath.Hint(zone, 999.9, 30, out seconds));
            Assert.AreEqual(1, seconds);
            Assert.AreEqual(ZoneHint.Closing, ZoneMath.Hint(zone, 1000, 30, out _));
            Assert.AreEqual(ZoneHint.None, ZoneMath.Hint(zone, 1360, 30, out _));

            zone.Phase = 0;   // before the match
            Assert.AreEqual(ZoneHint.None, ZoneMath.Hint(zone, 640, 30, out _));
        }
    }
}
