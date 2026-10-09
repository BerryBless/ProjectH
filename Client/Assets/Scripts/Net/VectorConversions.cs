namespace ProjectH.Client.Net
{
    public static class VectorConversions
    {
        // 기능: Shared(System.Numerics) 벡터를 Unity 벡터로 바꾼다.
        // 입력: value - 바꿀 System.Numerics.Vector3.
        // 출력: 같은 X·Y·Z를 가진 UnityEngine.Vector3.
        public static UnityEngine.Vector3 ToUnity(this System.Numerics.Vector3 value)
        {
            return new UnityEngine.Vector3(value.X, value.Y, value.Z);
        }

        // 기능: Unity 벡터를 Shared(System.Numerics) 벡터로 바꾼다.
        // 입력: value - 바꿀 UnityEngine.Vector3.
        // 출력: 같은 x·y·z를 가진 System.Numerics.Vector3.
        public static System.Numerics.Vector3 ToNumerics(this UnityEngine.Vector3 value)
        {
            return new System.Numerics.Vector3(value.x, value.y, value.z);
        }
    }
}
