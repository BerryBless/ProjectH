using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 13 D16: the preview's grid math (where each piece goes from the feet and the look) and the turbo gate.
    public class BuildTargetingTests
    {
        // The centre of cell (10, 12) on the ground: x = -80 + 52.5, z = -80 + 62.5.
        private static readonly NVector3 Feet = new NVector3(-27.5f, 0f, -17.5f);

        private static BuildPieceShape Pick(BuildPieceType piece, float yaw, float pitch = 0f, int offset = 0)
        {
            return PickAt(Feet, piece, yaw, pitch, offset);
        }

        private static BuildPieceShape PickAt(NVector3 feet, BuildPieceType piece, float yaw, float pitch = 0f, int offset = 0)
        {
            Assert.IsTrue(BuildTargeting.TryPick(piece, feet, yaw, pitch, offset, out BuildPieceShape shape));
            return shape;
        }

        private static BuildPieceShape Shape(BuildPieceType type, int x, int y, int z, int rotation) => new BuildPieceShape(type, x, y, z, rotation);

        [TestCase(0f, 0)]
        [TestCase(44f, 0)]
        [TestCase(46f, 1)]
        [TestCase(90f, 1)]
        [TestCase(180f, 2)]
        [TestCase(-90f, 3)]
        [TestCase(315f, 0)]
        [TestCase(720f, 0)]
        public void Direction_IsTheYawsNearestAxis(float yaw, int direction)
        {
            Assert.AreEqual(direction, BuildTargeting.Direction(yaw));
        }

        [Test]
        public void AWall_GoesOnTheEdgeThePlayerFaces()
        {
            Assert.AreEqual(Shape(BuildPieceType.Wall, 10, 0, 13, 0), Pick(BuildPieceType.Wall, 0f));    // north: the south edge of z 13
            Assert.AreEqual(Shape(BuildPieceType.Wall, 11, 0, 12, 1), Pick(BuildPieceType.Wall, 90f));   // east: the west edge of x 11
            Assert.AreEqual(Shape(BuildPieceType.Wall, 10, 0, 12, 0), Pick(BuildPieceType.Wall, 180f));
            Assert.AreEqual(Shape(BuildPieceType.Wall, 10, 0, 12, 1), Pick(BuildPieceType.Wall, 270f));
            Assert.AreEqual(Shape(BuildPieceType.Wall, 10, 1, 13, 0), Pick(BuildPieceType.Wall, 0f, -60f));   // looking up: one higher
        }

        [Test]
        public void R_TurnsAWallRoundThePlayersCell()
        {
            Assert.AreEqual(Shape(BuildPieceType.Wall, 11, 0, 12, 1), Pick(BuildPieceType.Wall, 0f, 0f, 1));
            Assert.AreEqual(Shape(BuildPieceType.Wall, 10, 0, 12, 0), Pick(BuildPieceType.Wall, 0f, 0f, 2));
        }

        [Test]
        public void AFloor_GoesAhead_DownUnderTheFeet_UpOverTheHead()
        {
            Assert.AreEqual(Shape(BuildPieceType.Floor, 10, 0, 13, 0), Pick(BuildPieceType.Floor, 0f));
            Assert.AreEqual(Shape(BuildPieceType.Floor, 10, 0, 12, 0), Pick(BuildPieceType.Floor, 0f, 60f));
            Assert.AreEqual(Shape(BuildPieceType.Floor, 10, 1, 12, 0), Pick(BuildPieceType.Floor, 0f, -60f));
            Assert.AreEqual(Shape(BuildPieceType.Floor, 10, 0, 13, 0), Pick(BuildPieceType.Floor, 0f, 0f, 1));   // R does nothing
        }

        [Test]
        public void ARamp_RisesTheWayThePlayerLooks_AndRTurnsItInPlace()
        {
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 11, 0, 12, 1), Pick(BuildPieceType.Ramp, 90f));
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 10, 0, 12, 1), Pick(BuildPieceType.Ramp, 90f, 60f));
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 11, 1, 12, 1), Pick(BuildPieceType.Ramp, 90f, -60f));
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 11, 0, 12, 2), Pick(BuildPieceType.Ramp, 90f, 0f, 1));
        }

        [Test]
        public void HalfWayUpARamp_TheNextLevelIsAimedAt()
        {
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 10, 1, 13, 0), PickAt(new NVector3(-27.5f, 1.6f, -17.5f), BuildPieceType.Ramp, 0f));
            Assert.AreEqual(Shape(BuildPieceType.Ramp, 10, 0, 13, 0), PickAt(new NVector3(-27.5f, 1.4f, -17.5f), BuildPieceType.Ramp, 0f));
        }

        [Test]
        public void ARoof_GoesAhead_OrOverThePlayer()
        {
            Assert.AreEqual(Shape(BuildPieceType.Roof, 10, 0, 13, 0), Pick(BuildPieceType.Roof, 0f));
            Assert.AreEqual(Shape(BuildPieceType.Roof, 10, 0, 12, 0), Pick(BuildPieceType.Roof, 0f, -60f));
        }

        [Test]
        public void OffTheGrid_OrNotANumber_HasNoTarget()
        {
            var edge = new NVector3(77.5f, 0f, 77.5f);   // cell (31, 31): nothing ahead to the north
            Assert.IsFalse(BuildTargeting.TryPick(BuildPieceType.Floor, edge, 0f, 0f, 0, out _));
            Assert.IsFalse(BuildTargeting.TryPick(BuildPieceType.Floor, new NVector3(float.NaN, 0f, 0f), 0f, 0f, 0, out _));
            Assert.IsFalse(BuildTargeting.TryPick(BuildPieceType.Floor, Feet, float.PositiveInfinity, 0f, 0, out _));
            Assert.IsFalse(BuildTargeting.TryPick(BuildPieceType.Floor, new NVector3(-27.5f, 60f, -17.5f), 0f, 0f, 0, out _));
        }

        [Test]
        public void Turbo_APressSendsAtOnce_HoldingStillNeverResendsOneSlot()
        {
            var gate = new TurboGate { Interval = 0.1f };
            Assert.IsTrue(gate.ShouldSend(0f, true, true, true, 7));
            Assert.IsFalse(gate.ShouldSend(0.05f, false, true, true, 8));   // within the interval
            Assert.IsFalse(gate.ShouldSend(0.2f, false, true, true, 7));    // the same slot
            Assert.IsFalse(gate.ShouldSend(0.5f, false, true, true, 7));
            Assert.IsTrue(gate.ShouldSend(0.6f, false, true, true, 8));     // moved: the next slot
            Assert.IsFalse(gate.ShouldSend(0.65f, false, true, true, 9));
            Assert.IsTrue(gate.ShouldSend(0.71f, false, true, true, 9));
        }

        [Test]
        public void Turbo_ReleasingForgetsTheSlot_AndAnInvalidCandidateIsNotSent()
        {
            var gate = new TurboGate { Interval = 0.1f };
            Assert.IsTrue(gate.ShouldSend(0f, true, true, true, 7));
            Assert.IsFalse(gate.ShouldSend(0.3f, false, false, false, 7));   // released
            Assert.IsTrue(gate.ShouldSend(0.31f, true, true, true, 7));      // pressed again: the same slot is tried again
            Assert.IsFalse(gate.ShouldSend(0.5f, false, true, false, 8));    // not valid
            Assert.IsTrue(gate.ShouldSend(0.6f, false, true, true, 8));
        }
    }
}
