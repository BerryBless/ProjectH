using NUnit.Framework;
using ProjectH.Client.CameraControl;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class ShoulderCameraMathTests
    {
        private const float Eps = 1e-4f;

        // Scripted sphere casts: the first call is the pivot -> shoulder cast, the second the
        // shoulder -> camera cast. A negative distance means "no hit".
        private sealed class FakeCaster : ISphereCaster
        {
            public float ShoulderHit = -1f;
            public float BackHit = -1f;
            public int Calls;
            public Vector3 FirstOrigin, FirstDirection, SecondOrigin, SecondDirection;
            public float FirstMaxDistance, SecondMaxDistance;

            public bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
            {
                Calls++;
                float scripted = Calls == 1 ? ShoulderHit : BackHit;
                if (Calls == 1)
                {
                    FirstOrigin = origin;
                    FirstDirection = direction;
                    FirstMaxDistance = maxDistance;
                }
                else if (Calls == 2)
                {
                    SecondOrigin = origin;
                    SecondDirection = direction;
                    SecondMaxDistance = maxDistance;
                }
                if (scripted >= 0f && scripted < maxDistance)
                {
                    hitDistance = scripted;
                    return true;
                }
                hitDistance = 0f;
                return false;
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Eps, "x");
            Assert.AreEqual(expected.y, actual.y, Eps, "y");
            Assert.AreEqual(expected.z, actual.z, Eps, "z");
        }

        [Test]
        public void HipPose_Yaw0_IsBehindRightShoulder()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            AssertVector(new Vector3(0.55f, 1.6f, 0f), pose.Shoulder);
            AssertVector(new Vector3(0.55f, 1.6f, -3.5f), pose.Position);
            AssertVector(Vector3.forward, pose.Forward);
            Assert.AreEqual(60f, pose.FieldOfView, Eps);
        }

        [Test]
        public void AimPose_IsCloserWithNarrowerFov()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 1f, 3.5f, 1f / 60f, new FakeCaster());

            // Moving in is never delayed: 1.6 < 3.5 applies at once.
            AssertVector(new Vector3(0.65f, 1.6f, -1.6f), pose.Position);
            Assert.AreEqual(42f, pose.FieldOfView, Eps);
        }

        [Test]
        public void HipPose_Yaw90_RotatesOffsets()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(new Vector3(10f, 0f, 0f), 90f, 0f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            // Facing +X: right is -Z, behind is -X.
            AssertVector(new Vector3(10f, 1.6f, -0.55f), pose.Shoulder);
            AssertVector(new Vector3(6.5f, 1.6f, -0.55f), pose.Position);
        }

        [Test]
        public void PitchDown_RaisesCamera()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 30f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            // Forward (0, -0.5, 0.866): the camera sits 3.5 * 0.5 = 1.75 m above the shoulder.
            Assert.AreEqual(1.6f + 1.75f, pose.Position.y, Eps);
        }

        [Test]
        public void WallBehind_PullsCameraInImmediately()
        {
            var caster = new FakeCaster { BackHit = 1f };
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, caster);

            Assert.AreEqual(1f, pose.Distance, Eps);
            AssertVector(new Vector3(0.55f, 1.6f, -1f), pose.Position);
        }

        [Test]
        public void WallCleared_CameraEasesBackOut()
        {
            var caster = new FakeCaster();
            float distance = 1f;
            float previous = distance;
            for (int frame = 0; frame < 120; frame++)
            {
                caster.Calls = 0;
                distance = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, distance, 1f / 60f, caster).Distance;
                Assert.Greater(distance, previous);   // strictly moving out, no jump
                Assert.LessOrEqual(distance, 3.5f);
                if (frame == 0) Assert.Less(distance, 1.5f);
                previous = distance;
            }
            Assert.AreEqual(3.5f, distance, 0.01f);   // 2 s at sharpness 6: e^-12 of the gap remains
        }

        // Review Focus: standing against a wall on the right puts the shoulder point inside the wall.
        // The second cast must start from the shortened shoulder, or it would miss that wall.
        [Test]
        public void ShoulderInsideWall_SecondCastStartsFromShortenedShoulder()
        {
            var caster = new FakeCaster { ShoulderHit = 0.1f };
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, caster);

            // Shortened to the hit minus the clearance: 0.1 - 0.02 = 0.08.
            Assert.AreEqual(2, caster.Calls);
            AssertVector(new Vector3(0.08f, 1.6f, 0f), caster.SecondOrigin);
            AssertVector(new Vector3(0.08f, 1.6f, 0f), pose.Shoulder);
            AssertVector(new Vector3(0.08f, 1.6f, -3.5f), pose.Position);
        }

        // Stage 2 must start clear of the wall stage 1 hit (SphereCast ignores colliders it starts
        // inside). Yaw 300 / pitch 20 so right and forward are not axis-aligned.
        [TestCase(0.3f)]
        [TestCase(0.01f)]   // closer than the clearance: clamped to the pivot, never behind it
        public void ShoulderHit_SecondCastStartsClearOfWall(float d)
        {
            const float yaw = 300f;
            const float pitch = 20f;
            var feet = new Vector3(2f, 0f, -1f);
            var caster = new FakeCaster { ShoulderHit = d };
            ShoulderCameraMath.Solve(feet, yaw, pitch, 0f, 3.5f, 1f / 60f, caster);

            Vector3 pivot = feet + new Vector3(0f, ShoulderCameraMath.PivotHeight, 0f);
            Vector3 right = ShoulderCameraMath.Right(yaw);
            float reach = Mathf.Max(0f, d - 0.02f);   // literal, so a zero clearance would fail
            AssertVector(pivot + right * reach, caster.SecondOrigin);
        }

        [Test]
        public void Casts_UseRightThenBackWithTargetLengths()
        {
            const float yaw = 300f;
            const float pitch = 20f;
            var caster = new FakeCaster();
            ShoulderCameraMath.Solve(Vector3.zero, yaw, pitch, 1f, 3.5f, 1f / 60f, caster);

            Assert.AreEqual(2, caster.Calls);
            AssertVector(new Vector3(0f, ShoulderCameraMath.PivotHeight, 0f), caster.FirstOrigin);
            AssertVector(ShoulderCameraMath.Right(yaw), caster.FirstDirection);
            Assert.AreEqual(ShoulderCameraMath.AimShoulder, caster.FirstMaxDistance, Eps);
            AssertVector(-ShoulderCameraMath.Forward(yaw, pitch), caster.SecondDirection);
            Assert.AreEqual(ShoulderCameraMath.AimDistance, caster.SecondMaxDistance, Eps);
        }

        [Test]
        public void Forward_MatchesUnityEulerConvention()
        {
            // Quaternion.Euler(0, 90, 0) * forward = +X; Euler(90, 0, 0) * forward = -Y.
            AssertVector(Vector3.right, ShoulderCameraMath.Forward(90f, 0f));
            AssertVector(Vector3.down, ShoulderCameraMath.Forward(0f, 90f));
            AssertVector(new Vector3(0f, 0f, -1f), ShoulderCameraMath.Right(90f));
        }
    }
}
