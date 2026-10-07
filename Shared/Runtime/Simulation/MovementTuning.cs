namespace ProjectH.Shared.Simulation
{
    // Phase 12 D2, D3: every number of the Phase 12 movement in one place. Client prediction and the server read these
    // same constants, so they are code, not server config (a value changed on one side only would make every
    // prediction wrong). Values are per second; MovementSimulation converts them to ticks with its deltaTime. The older
    // walk, sprint, gravity, jump and size values stay in MoveSettings.
    public static class MovementTuning
    {
        // Energy (D3, D7). MoveState keeps the spent energy in hundredths (MoveState.EnergySpent), so a default state is
        // rested and the wire value (SnapshotSelf.Energy, x100) is exact.
        public const float MaxEnergy = 100f;
        public const float SprintEnergyCostPerSecond = 20f;
        public const float EnergyRecoveryPerSecond = 25f;
        public const float EnergyRecoveryDelaySeconds = 1f;
        // After running out, sprint stays off until the energy is back at this level.
        public const float SprintResumeEnergy = 20f;
        public const int EnergyScale = 100;   // hundredths

        // Crouch (D3, D7). Standing up needs the standing box (MoveSettings.Height) free: CanStand.
        public const float CrouchHeight = 1.2f;
        public const float CrouchSpeed = 2.5f;

        // Slide (D3, D7): starts from a sprint (horizontal speed at least SlideMinStartSpeed) when crouch is pressed.
        public const float SlideMinStartSpeed = 6f;
        public const float SlideStartSpeed = 9f;
        public const float SlideFriction = 5f;          // m/s lost per second
        public const float SlideMinSpeed = 3f;          // below this the slide ends in a crouch
        public const float SlideSlopeFactor = 0.6f;     // downhill: slope x gravity x this is added per second
        public const float SlideMaxSpeed = 13f;
        // Starting a slide costs this much energy (and restarts the recovery delay), so a Sprint+Crouch hop chain runs out.
        public const float SlideStartEnergyCost = 15f;

        // Air (D3): a sprinting jump takes off at SprintSpeed x this; in the air the input accelerates the horizontal
        // velocity by AirAcceleration, never above the larger of the speed it had and WalkSpeed.
        public const float SprintJumpSpeedScale = 1.1f;
        public const float AirAcceleration = 12f;

        // Vault (D3, D8): only on a jump press, on the ground, moving forward, with an obstacle within VaultReach.
        public const float VaultReach = 0.8f;
        // An obstacle counts when its bottom is at most this far above the feet (it stands on the character's level, also
        // when the character comes up a slope).
        public const float VaultBaseTolerance = 0.3f;
        public const float HurdleMinHeight = 0.5f;
        public const float HurdleMaxHeight = 1.1f;
        public const float HurdleMinSpeed = 6f;
        public const float HurdleSeconds = 0.2f;
        // A hurdle clears an obstacle at most this deep along the move, and lands this far past its far side. A deeper
        // obstacle, or no room behind it, puts the character on its top instead.
        public const float HurdleMaxDepth = 2.2f;
        public const float HurdleLandingGap = 0.1f;
        public const float MantleMaxHeight = 2.1f;
        public const float MantleSeconds = 0.4f;
        public const float MantleInset = 0.4f;          // a mantle stands this far inside the top's edge

        // Freefall (D6): gravity up to a terminal speed; the input accelerates the horizontal velocity towards these
        // limits in the character's own frame (forward, side, back).
        public const float FreefallTerminalSpeed = 30f;
        public const float FreefallAcceleration = 20f;
        public const float FreefallForwardSpeed = 15f;
        public const float FreefallSideSpeed = 10f;
        public const float FreefallBackSpeed = 6f;

        // Glide (D6): opened by a jump press in freefall, or by the simulation when the ground (terrain or a box top
        // under the feet) is this close. A steady descent; it never closes again.
        public const float GlideAutoDeployHeight = 30f;
        public const float GlideFallSpeed = 5f;
        public const float GlideAcceleration = 10f;
        public const float GlideForwardSpeed = 14f;
        public const float GlideSideSpeed = 10f;
        public const float GlideBackSpeed = 4f;

        // Drop transport (D5): a straight route through the map's centre at this altitude and speed, starting and ending
        // TransportOutsideMargin outside the outer walls. Jumps count only while the transport is at least
        // TransportJumpMargin inside the walls; whoever is still aboard when it leaves that square is dropped there.
        public const float TransportAltitude = 90f;
        public const float TransportSpeed = 20f;
        public const float TransportOutsideMargin = 20f;
        public const float TransportJumpMargin = 10f;

        // Doors (D9): E toggles the nearest door within this distance (feet to the door's centre, across the ground) and
        // this angle either side of where the character faces. The server rule is DoorRules, the client's copy DoorRule;
        // both copies read these, so they are Shared constants (game-core-rules §4 exception 3).
        public const float DoorInteractRange = 2.5f;
        public const float DoorInteractHalfAngle = 60f;

        // Phase 13 D2: on the ground, a box whose top is at most this far above the feet does not stop a walk: the feet step
        // onto it. Building pieces need it where a ramp meets a floor or a wall top (the ramp's surface under the footprint
        // reaches the level's height only past the wall's half thickness: 0.075 m short). The map's boxes never have a top
        // this close above walkable ground (GameMapTests), so their collision is unchanged.
        public const float StepUpHeight = 0.1f;

        // Phase 14 D4: a downed (DBNO) character crawls at CrawlSpeed with a DownedHeight collision and hit box. Its eye
        // (where its view and a revive's line of sight start) is the server's CombatRules.DownedEyeHeight and the client's
        // AimSolver copy, not here: no shot starts from a downed character.
        public const float CrawlSpeed = 1.5f;
        public const float DownedHeight = 0.9f;

        // Fall damage (D3, D10) is a server rule: its numbers are CombatRules.FallDamage*, not here.
    }
}
