using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Presentation only (D8, D10): over-the-right-shoulder camera with aim (ADS) zoom and wall
    // collision. It never writes simulation or network state; its Yaw is copied into each
    // InputCommand, the only way camera input reaches the server. Pitch stays local (D15).
    public sealed class ShoulderCamera
    {
        private const float Sensitivity = 0.1f;
        private const float MinPitch = -30f;
        private const float MaxPitch = 70f;
        private const float AimBlendSharpness = 12f;
        private const float CollisionRadius = 0.2f;
        // Below CollisionRadius, so the near plane cannot reach into a wall the sphere stopped at.
        private const float NearClip = 0.05f;

        private readonly Camera _camera;
        private readonly Transform _transform;
        private readonly PhysicsSphereCaster _caster = new PhysicsSphereCaster();
        private float _aimBlend;
        private float _distance = ShoulderCameraMath.HipDistance;

        public ShoulderCamera(Camera camera)
        {
            _camera = camera;
            _transform = camera.transform;
            _camera.nearClipPlane = NearClip;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 10f;

        // Screen-centre ray. It starts at the shoulder point, which lies on the camera's forward axis,
        // so geometry between the camera and the player is never picked as the aim point.
        public Ray AimRay { get; private set; }

        public void ApplyLook(Vector2 lookDelta, bool aiming)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            float sensitivity = aiming ? Sensitivity * ShoulderCameraMath.AimSensitivityScale : Sensitivity;
            Yaw = Mathf.Repeat(Yaw + lookDelta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - lookDelta.y * sensitivity, MinPitch, MaxPitch);
        }

        // Call from LateUpdate with the rendered feet position.
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, Pitch, _aimBlend, _distance, deltaTime, _caster);
            _distance = pose.Distance;
            _transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(Pitch, Yaw, 0f));
            _camera.fieldOfView = pose.FieldOfView;
            AimRay = new Ray(pose.Shoulder, pose.Forward);
        }

        // Single-result SphereCast: no allocation. Player views have no colliders, so only the world is hit.
        // The pivot (feet + 1.6 m) is inside the character's collision box, which the simulation keeps
        // out of every box, and 0.2 m < 0.35 m half-width, so stage 1 normally starts outside a wall.
        // The pose uses RenderPosition, which carries the decaying reconcile offset, so for about
        // 0.1-0.3 s after a misprediction near a wall the origin can still be inside a collider.
        private sealed class PhysicsSphereCaster : ISphereCaster
        {
            public bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
            {
                if (Physics.SphereCast(origin, CollisionRadius, direction, out RaycastHit hit, maxDistance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    hitDistance = hit.distance;
                    return true;
                }
                hitDistance = 0f;
                return false;
            }
        }
    }
}
