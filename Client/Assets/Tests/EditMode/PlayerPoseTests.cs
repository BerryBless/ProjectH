using NUnit.Framework;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 12 D13, D14: the placeholder pose of each mode, the remote hit box height, and the camera targets. Pure math:
    // these also run outside Unity (the EditTests tool).
    public class PlayerPoseTests
    {
        [Test]
        public void EachMode_HasItsPose_AndTheServersHitHeight()
        {
            PlayerPose ground = PlayerPose.For(MovementMode.Ground, false, true);
            Assert.AreEqual(PlayerPose.StandingBody, ground.BodyHeight);
            Assert.AreEqual(0f, ground.Lean);
            Assert.AreEqual(1.8f, ground.HitHeight);

            Assert.AreEqual(PlayerPose.SprintLean, PlayerPose.For(MovementMode.Ground, true, true).Lean);
            Assert.AreEqual(PlayerPose.CrouchedBody, PlayerPose.For(MovementMode.Crouch, false, true).BodyHeight);
            Assert.AreEqual(1.2f, PlayerPose.For(MovementMode.Crouch, false, true).HitHeight);
            PlayerPose slide = PlayerPose.For(MovementMode.Slide, false, true);
            Assert.AreEqual(PlayerPose.SlideLean, slide.Lean);
            Assert.AreEqual(1.2f, slide.HitHeight);
            Assert.AreEqual(PlayerPose.VaultLean, PlayerPose.For(MovementMode.Vault, false, true).Lean);
            Assert.IsTrue(PlayerPose.For(MovementMode.Freefall, false, true).Prone);
            Assert.IsTrue(PlayerPose.For(MovementMode.Glide, false, true).Wings);
            Assert.IsTrue(PlayerPose.For(MovementMode.Transport, false, true).Hidden);
        }

        [Test]
        public void TheDead_LieDown_WhateverTheMode()
        {
            PlayerPose dead = PlayerPose.For(MovementMode.Glide, true, false);
            Assert.IsTrue(dead.Prone);
            Assert.IsFalse(dead.Wings);
            Assert.IsFalse(dead.Hidden);
        }

        [Test]
        public void CameraTargets_FollowTheMode()
        {
            CameraTargets hip = ShoulderCameraMath.TargetsFor(MovementMode.Ground, false);
            Assert.AreEqual(ShoulderCameraMath.HipFov, hip.FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.PivotHeight, hip.PivotHeight);
            Assert.AreEqual(ShoulderCameraMath.SprintFov, ShoulderCameraMath.TargetsFor(MovementMode.Ground, true).FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.CrouchPivotHeight, ShoulderCameraMath.TargetsFor(MovementMode.Slide, false).PivotHeight);
            CameraTargets air = ShoulderCameraMath.TargetsFor(MovementMode.Glide, false);
            Assert.AreEqual(ShoulderCameraMath.AirDistance, air.Distance);
            Assert.AreEqual(ShoulderCameraMath.AirFov, air.FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.TransportDistance, ShoulderCameraMath.TargetsFor(MovementMode.Transport, false).Distance);
        }

        [Test]
        public void CameraTargets_AreEased_NotJumpedTo()
        {
            CameraTargets current = ShoulderCameraMath.Hip;
            CameraTargets target = ShoulderCameraMath.TargetsFor(MovementMode.Freefall, false);
            CameraTargets next = ShoulderCameraMath.Approach(current, target, 1f / 60f);
            Assert.Greater(next.Distance, current.Distance);
            Assert.Less(next.Distance, target.Distance);
            for (int i = 0; i < 300; i++) next = ShoulderCameraMath.Approach(next, target, 1f / 60f);
            Assert.AreEqual(target.Distance, next.Distance, 1e-3f);
        }

        [Test]
        public void Solve_UsesTheModesPivotAndFieldOfView()
        {
            CameraTargets crouch = ShoulderCameraMath.TargetsFor(MovementMode.Crouch, false);
            ShoulderPose pose = ShoulderCameraMath.Solve(UnityEngine.Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new NoHit(), crouch);
            Assert.AreEqual(ShoulderCameraMath.CrouchPivotHeight, pose.Shoulder.y, 1e-4f);
            CameraTargets sprint = ShoulderCameraMath.TargetsFor(MovementMode.Ground, true);
            pose = ShoulderCameraMath.Solve(UnityEngine.Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new NoHit(), sprint);
            Assert.AreEqual(ShoulderCameraMath.SprintFov, pose.FieldOfView, 1e-4f);
        }

        private sealed class NoHit : ISphereCaster
        {
            public bool Cast(UnityEngine.Vector3 origin, UnityEngine.Vector3 direction, float maxDistance, out float hitDistance)
            {
                hitDistance = 0f;
                return false;
            }
        }
    }
}
