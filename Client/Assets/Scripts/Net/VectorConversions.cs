namespace ProjectH.Client.Net
{
    public static class VectorConversions
    {
        public static UnityEngine.Vector3 ToUnity(this System.Numerics.Vector3 value)
        {
            return new UnityEngine.Vector3(value.X, value.Y, value.Z);
        }

        public static System.Numerics.Vector3 ToNumerics(this UnityEngine.Vector3 value)
        {
            return new System.Numerics.Vector3(value.x, value.y, value.z);
        }
    }
}
