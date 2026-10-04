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
        // The server clamps pitch to the same range.
        public const float MaxPitch = 89f;
        private const float MinDistance = 0.01f;

        // 기능: 이동 모드에 맞는 눈 높이를 서버 CombatRules.EyeHeightOf와 같은 값으로 돌려준다.
        // 입력: mode - 캐릭터의 이동 모드.
        // 출력: 발 기준 눈 높이(m). Crouch·Slide면 CrouchEyeHeight, 그 외에는 EyeHeight.
        // Phase 12 D13: the eye height of a mode, as the server's CombatRules.EyeHeightOf.
        public static float EyeHeightOf(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight : EyeHeight;

        // 기능: 눈 위치에서 목표 지점을 향하는 yaw·pitch를 계산한다.
        // 입력: eye - 캐릭터 눈 위치, target - 조준선 아래의 목표 지점, yaw - 계산된 수평 각도(0~360도), pitch - 계산된 수직 각도(아래가 양수, ±MaxPitch로 제한).
        // 출력: 방향을 구할 수 있으면 true와 yaw·pitch, 목표가 눈에 너무 가까우면 false와 0.
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
