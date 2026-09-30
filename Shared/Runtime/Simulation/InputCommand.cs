using System;

namespace ProjectH.Shared.Simulation
{
    [Flags]
    public enum InputButtons : byte
    {
        None = 0,
        Jump = 1,
        Sprint = 2,
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
    }
}
