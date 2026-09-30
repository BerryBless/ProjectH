using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Everything MovementSimulation needs to continue from one step to the next.
    public struct MoveState
    {
        public Vector3 Position;   // feet position, ground plane at y = 0
        public float VelocityY;
        public float Yaw;          // degrees 0..360
    }
}
