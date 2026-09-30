using UnityEngine;

namespace ProjectH.Client.Bootstrap
{
    // Throwaway test map (flat 100 x 100 m ground + light). Replaced by a real map in Phase 6.
    public static class TestWorld
    {
        public static GameObject Build()
        {
            var root = new GameObject("TestWorld");

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(10f, 1f, 10f);

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var sun = new GameObject("Sun");
                sun.transform.SetParent(root.transform, false);
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                var light = sun.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;   // low-spec default until the real map exists
            }
            return root;
        }
    }
}
