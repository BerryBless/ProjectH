using System;

namespace ProjectH.Shared.Simulation
{
    // 2 bytes on the wire since Phase 4 (D14). Values are the wire format: never renumber.
    [Flags]
    public enum InputButtons : ushort
    {
        None = 0,
        Jump = 1,
        Sprint = 2,
        Fire = 4,
        Reload = 8,
        Slot1 = 16,
        Slot2 = 32,
        Slot3 = 64,
        Interact = 128,       // E: pick up the nearest item (the server chooses it, D8)
        Drop = 256,           // G: drop the current weapon (D12)
        UseMedkit = 512,      // 4
        UseShieldCell = 1024, // 5
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
