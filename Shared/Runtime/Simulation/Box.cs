using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Axis-aligned solid box of the collision world (D1). Immutable so a ReadOnlySpan<Box> over a
    // static array can be shared by every simulation step without copies or allocation.
    public readonly struct Box
    {
        public readonly Vector3 Min;
        public readonly Vector3 Max;

        // 기능: 최소·최대 모서리로 축 정렬 상자를 만든다(min <= max인지 검사하지 않는다).
        // 입력: min - 최소 모서리, max - 최대 모서리.
        // 출력: 두 모서리를 그대로 담은 상자.
        public Box(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        // 기능: 중심과 크기로 상자를 만든다.
        // 입력: center - 상자 중심, size - 각 축의 전체 길이.
        // 출력: 중심에서 크기의 절반만큼 양쪽으로 뻗은 상자.
        public static Box FromCenterSize(Vector3 center, Vector3 size)
        {
            Vector3 half = size * 0.5f;
            return new Box(center - half, center + half);
        }
    }
}
