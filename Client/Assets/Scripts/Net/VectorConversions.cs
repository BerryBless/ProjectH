namespace ProjectH.Client.Net
{
    public static class VectorConversions
    {
        // 기능: System.Numerics 벡터를 Unity 벡터로 바꾼다.
        // 입력: value - 변환할 Numerics 벡터.
        // 출력: 같은 X, Y, Z의 UnityEngine.Vector3.
        public static UnityEngine.Vector3 ToUnity(this System.Numerics.Vector3 value)
        {
            return new UnityEngine.Vector3(value.X, value.Y, value.Z);
        }

        // 기능: Unity 벡터를 System.Numerics 벡터로 바꾼다.
        // 입력: value - 변환할 Unity 벡터.
        // 출력: 같은 x, y, z의 System.Numerics.Vector3.
        public static System.Numerics.Vector3 ToNumerics(this UnityEngine.Vector3 value)
        {
            return new System.Numerics.Vector3(value.x, value.y, value.z);
        }
    }
}
