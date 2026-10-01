using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Everything MovementSimulation needs to continue from one step to the next.
    // Phase 12 D1: the mode and what the new modes carry from tick to tick. The default value is a rested character
    // standing in Ground mode, as before Phase 12.
    public struct MoveState
    {
        public Vector3 Position;   // feet position, ground plane at y = 0
        public float VelocityY;
        public float Yaw;          // degrees 0..360

        public MovementMode Mode;
        // X and Z velocity in m/s. On the ground the input sets it every tick; in the air, a slide and a vault it carries over.
        public Vector2 HorizontalVelocity;
        // Energy used, in hundredths (0 = MovementTuning.MaxEnergy left). Spent rather than left, so a default state is
        // rested; integer, so the wire value is exact (Energy).
        public ushort EnergySpent;
        // Ticks to wait before energy recovers (set when a sprint stops).
        public byte EnergyDelayTicks;
        // Vault ticks left (0 in every other mode).
        public byte ModeTicks;
        // D3: the energy ran out; sprint stays off until it is back at MovementTuning.SprintResumeEnergy.
        public bool Exhausted;

        // Energy left, 0..MaxEnergy.
        public float Energy => (MaxEnergyHundredths - EnergySpent) / (float)MovementTuning.EnergyScale;

        public const int MaxEnergyHundredths = (int)(MovementTuning.MaxEnergy * MovementTuning.EnergyScale);
    }
}
