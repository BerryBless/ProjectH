using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Axis-aligned solid box of the collision world (D1). Immutable so a ReadOnlySpan<Box> over a
    // static array can be shared by every simulation step without copies or allocation.
    public readonly struct Box
    {
        public readonly Vector3 Min;
        public readonly Vector3 Max;

        public Box(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public static Box FromCenterSize(Vector3 center, Vector3 size)
        {
            Vector3 half = size * 0.5f;
            return new Box(center - half, center + half);
        }
    }
}
