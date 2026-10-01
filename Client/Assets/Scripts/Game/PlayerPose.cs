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
