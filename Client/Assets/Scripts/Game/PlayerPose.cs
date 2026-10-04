using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 12 D14: how a character is drawn in each movement mode, as plain numbers; PlayerView applies them to the
    // placeholder capsule (no new assets). The hit box (the collider the aim ray uses) has the server's height for the
    // mode (D13), whatever the capsule shows. Pure (no UnityEngine), so EditMode tests also run outside Unity.
    public struct PlayerPose
    {
        public const float StandingBody = 2f;     // the capsule mesh's own height
        public const float CrouchedBody = 1.3f;
        public const float SprintLean = 10f;
        public const float SlideLean = -30f;      // leaning back
        public const float VaultLean = 20f;

        public float BodyHeight;   // metres
        public float Lean;         // degrees about the character's right axis; positive leans forward
        public bool Prone;         // lying along the facing direction (freefall, dead)
        public bool Wings;         // the glider over the head
        public bool Hidden;        // aboard the transport: neither drawn nor hit
        public float HitHeight;    // MovementSimulation.CollisionHeight of the mode

        // 기능: 이동 모드·질주·생존 여부로 캡슐의 표시 자세(몸 높이, 기울기, 엎드림, 날개, 숨김)와 서버와 같은 피격 높이를 정한다.
        // 입력: mode - 이동 모드, sprinting - 질주 중 여부(전용 자세가 없는 모드에서 앞으로 기울이는 데만 사용), alive - 생존 여부.
        // 출력: 해당 상태의 PlayerPose. 죽었으면 Prone만 켠 기본 자세.
        public static PlayerPose For(MovementMode mode, bool sprinting, bool alive)
        {
            var pose = new PlayerPose { BodyHeight = StandingBody, HitHeight = MovementSimulation.CollisionHeight(mode) };
            if (!alive)
            {
                pose.Prone = true;
                return pose;
            }
            switch (mode)
            {
                case MovementMode.Crouch:
                    pose.BodyHeight = CrouchedBody;
                    break;
                case MovementMode.Slide:
                    pose.BodyHeight = CrouchedBody;
                    pose.Lean = SlideLean;
                    break;
                case MovementMode.Vault:
                    pose.Lean = VaultLean;
                    break;
                case MovementMode.Freefall:
                    pose.Prone = true;
                    break;
                case MovementMode.Glide:
                    pose.Wings = true;
                    break;
                case MovementMode.Transport:
                    pose.Hidden = true;
                    break;
                default:
                    if (sprinting) pose.Lean = SprintLean;
                    break;
            }
            return pose;
        }
    }
}
