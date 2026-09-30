using NUnit.Framework;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Game;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class AimSolverTests
    {
        private static readonly Vector3 Eye = new Vector3(2f, 1.6f, -1f);

        [TestCase(0f, 0f, 10f, 0f, 0f)]      // straight ahead (+Z)
        [TestCase(10f, 0f, 0f, 90f, 0f)]     // +X
        [TestCase(0f, 0f, -10f, 180f, 0f)]
        [TestCase(-10f, 0f, 0f, 270f, 0f)]   // yaw stays in 0..360
        [TestCase(0f, -10f, 10f, 0f, 45f)]   // below: positive pitch looks down
        [TestCase(0f, 10f, 10f, 0f, -45f)]
        public void Solve_GivesCameraConventionAngles(float dx, float dy, float dz, float yaw, float pitch)
        {
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + new Vector3(dx, dy, dz), out float solvedYaw, out float solvedPitch));
            Assert.AreEqual(yaw, solvedYaw, 1e-3f);
            Assert.AreEqual(pitch, solvedPitch, 1e-3f);
        }

        // The server rebuilds the direction with the camera's formula (CombatRules.TryAimDirection ==
        // ShoulderCameraMath.Forward). Solving and rebuilding must point at the same target.
        [TestCase(3f, -1.2f, 17f)]
        [TestCase(-8f, 0.4f, -2f)]
        [TestCase(0.5f, -1.5f, 0.2f)]
        public void Solve_RoundTripsThroughCameraForward(float dx, float dy, float dz)
        {
            var offset = new Vector3(dx, dy, dz);
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + offset, out float yaw, out float pitch));
            Vector3 forward = ShoulderCameraMath.Forward(yaw, pitch);
            Vector3 expected = offset.normalized;
            Assert.AreEqual(expected.x, forward.x, 1e-4f);
            Assert.AreEqual(expected.y, forward.y, 1e-4f);
            Assert.AreEqual(expected.z, forward.z, 1e-4f);
        }

        [Test]
        public void Solve_StraightUp_IsClampedTo89()
        {
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + new Vector3(0f, 5f, 0f), out _, out float pitch));
            Assert.AreEqual(-89f, pitch, 1e-4f);
        }

        [Test]
        public void Solve_TargetAtTheEye_ReturnsFalse()
        {
            Assert.IsFalse(AimSolver.TrySolve(Eye, Eye + new Vector3(0.001f, 0f, 0f), out _, out _));
        }
    }
}
