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
    }
}
