namespace ProjectH.Shared.Simulation
{
    // Single source of truth for client prediction and server simulation. Changing a value on only
    // one side makes every prediction diverge, so these are constants rather than server config.
    public static class MoveSettings
    {
        public const float WalkSpeed = 4.5f;
        public const float SprintSpeed = 7f;
        public const float Gravity = -20f;
        public const float JumpSpeed = 7f;

        // Character collision box (D1): feet at MoveState.Position, HalfWidth on X and Z, Height up.
        public const float HalfWidth = 0.35f;
        public const float Height = 1.8f;

        // Gap kept between the character and a face it was stopped by. Overlaps up to Skin are
        // treated as touching, so float rounding never counts as penetration.
        public const float Skin = 0.001f;

        // A surface within this distance of the feet counts as ground (D3).
        public const float GroundProbe = 0.02f;

        // Phase 6 D4: the steepest terrain (rise over run) anywhere on the map; every slope is walkable. A walking
        // character follows a downhill slope while the drop is at most MaxSlope times the distance moved.
        public const float MaxSlope = 0.6f;
    }
}
