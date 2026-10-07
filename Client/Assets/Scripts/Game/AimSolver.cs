using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D2: the aim sent to the server is the direction from the character's eye to the point under the
    // crosshair, not the camera's own direction. The server shoots from the same eye along it, so the
    // shoulder camera's offset does not move the hit. Pure math (no Physics), testable outside Unity.
    public static class AimSolver
    {
        // Must equal the server's CombatRules.EyeHeight (feet + 1.6 m, also ShoulderCameraMath.PivotHeight).
        public const float EyeHeight = 1.6f;
        // Phase 12 D13: crouched or sliding. Must equal the server's CombatRules.CrouchEyeHeight.
        public const float CrouchEyeHeight = 1.0f;
        // Phase 14 D4: downed (DBNO). Must equal the server's CombatRules.DownedEyeHeight (no shot starts there; the view and
        // the build and edit eye use it).
        public const float DownedEyeHeight = 0.6f;
        // The server clamps pitch to the same range.
        public const float MaxPitch = 89f;
        private const float MinDistance = 0.01f;

        // 기능: 모드의 눈높이를 서버 CombatRules.EyeHeightOf와 같게 돌려준다(Phase 12 D13, Phase 14 D4).
        // 입력: mode - 이동 모드.
        // 출력: 웅크리기·슬라이드 1.0 m, 기절 0.6 m, 나머지 1.6 m.
        public static float EyeHeightOf(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight
            : mode == MovementMode.Downed ? DownedEyeHeight : EyeHeight;

        // yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down (ShoulderCameraMath.Forward).
        // False when the target is too close to the eye to give a direction.
        public static bool TrySolve(Vector3 eye, Vector3 target, out float yaw, out float pitch)
        {
            Vector3 d = target - eye;
            float horizontal = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            if (horizontal < MinDistance && Mathf.Abs(d.y) < MinDistance)
            {
                yaw = 0f;
                pitch = 0f;
                return false;
            }

            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (yaw < 0f) yaw += 360f;
            pitch = Mathf.Clamp(-Mathf.Atan2(d.y, horizontal) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
            return true;
        }
    }
}
