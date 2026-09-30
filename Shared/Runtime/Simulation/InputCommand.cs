using System;

namespace ProjectH.Shared.Simulation
{
    [Flags]
    public enum InputButtons : byte
    {
        None = 0,
        Jump = 1,
        Sprint = 2,
        Fire = 4,
        Reload = 8,
        Slot1 = 16,
        Slot2 = 32,
    }

    // One fixed-tick input. Seq increases by one per client simulation step and is how the
    // server acknowledges inputs back to the client for reconciliation.
    public struct InputCommand
    {
        public uint Seq;
        public float MoveX;   // strafe, -1..1 (sanitized by MovementSimulation)
        public float MoveY;   // forward, -1..1
        public float Yaw;     // degrees, camera heading
        public InputButtons Buttons;

        // Phase 3 (D2): direction from the eye (feet + 1.6 m) to the crosshair target, in degrees.
        // Same convention as the camera: yaw 0 faces +Z, positive pitch looks down. Only the server's
        // combat code reads these; MovementSimulation ignores them.
        public float AimYaw;
        public float AimPitch;

        // Server tick the client was rendering remote players at when this input was made (D6).
        public float ViewTick;
    }
}
