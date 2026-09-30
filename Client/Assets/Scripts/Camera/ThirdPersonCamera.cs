using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Presentation only: follows the rendered player position and never writes simulation or network
    // state. Its yaw is copied into each InputCommand, the only way camera input reaches the server.
    public sealed class ThirdPersonCamera
    {
        private const float Distance = 5f;
        private const float PivotHeight = 1.6f;
        private const float Sensitivity = 0.1f;
        private const float MinPitch = -30f;
        private const float MaxPitch = 70f;

        private readonly Transform _camera;

        public ThirdPersonCamera(Transform camera)
        {
            _camera = camera;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 15f;

        public void ApplyLook(Vector2 lookDelta)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            Yaw = Mathf.Repeat(Yaw + lookDelta.x * Sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - lookDelta.y * Sensitivity, MinPitch, MaxPitch);
        }

        public void Follow(Vector3 targetFeet)
        {
            Vector3 pivot = targetFeet + Vector3.up * PivotHeight;
            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            _camera.SetPositionAndRotation(pivot - rotation * Vector3.forward * Distance, rotation);
        }
    }
}
