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
        // Phase 14 D14: a downed character is drawn flat, as tall as its hit box.
        public const float DownedBody = MovementTuning.DownedHeight;
        // Phase 19 D15: seated in a vehicle, a short upright capsule on the seat.
        public const float SeatedBody = 1.2f;

        public float BodyHeight;   // metres
        public float Lean;         // degrees about the character's right axis; positive leans forward
        public bool Prone;         // lying along the facing direction (freefall, dead)
        public bool Wings;         // the glider over the head
        public bool Hidden;        // aboard the transport: neither drawn nor hit
        public float HitHeight;    // MovementSimulation.CollisionHeight of the mode
        public bool Seated;        // Phase 19: in a vehicle seat: drawn sitting, never hit (shots pass seated players)

        // 기능: 모드·질주·생존으로 캡슐을 어떻게 그릴지 정한다(Phase 14: 기절은 높이 0.9 m로 납작하게).
        // 입력: mode - 이동 모드, sprinting - 질주 중인지, alive - 살아 있는지(죽으면 누운 회색).
        // 출력: 몸 높이·기울기·엎드림·날개·숨김·맞는 높이.
        public static PlayerPose For(MovementMode mode, bool sprinting, bool alive) => For(mode, sprinting, alive, false);

        // 기능: 모드·질주·생존·탑승으로 캡슐을 어떻게 그릴지 정한다(Phase 19 D15: 살아서 앉아 있으면 모드와 관계없이 짧게 똑바로 앉고 맞지 않는다).
        // 입력: mode - 이동 모드, sprinting - 질주 중인지, alive - 살아 있는지, seated - 차량 좌석에 앉아 있는지.
        // 출력: 몸 높이·기울기·엎드림·날개·숨김·맞는 높이·탑승.
        public static PlayerPose For(MovementMode mode, bool sprinting, bool alive, bool seated)
        {
            if (alive && seated)
                return new PlayerPose { BodyHeight = SeatedBody, HitHeight = MovementSimulation.CollisionHeight(MovementMode.Ground), Seated = true };
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
                case MovementMode.Downed:
                    pose.BodyHeight = DownedBody;
                    break;
                default:
                    if (sprinting) pose.Lean = SprintLean;
                    break;
            }
            return pose;
        }
    }
}
