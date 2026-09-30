using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Bootstrap
{
    // Throwaway test map: 100 x 100 m ground, a light and one cube per Shared TestArena box (D5, D10).
    // The cubes keep their BoxColliders for camera collision and fire rays only; movement collision is
    // MovementSimulation with the same TestArena boxes, so there is a single source for the map.
    // Replaced by a real map in Phase 6.
    public static class TestWorld
    {
        // boxMaterial is shared by every cube; the caller destroys it together with the returned root.
        public static GameObject Build(out Material boxMaterial)
        {
            var root = new GameObject("TestWorld");

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(10f, 1f, 10f);

            // Copy of the primitive's default material, so the shader is guaranteed to be in the build.
            boxMaterial = new Material(ground.GetComponent<Renderer>().sharedMaterial) { color = new Color(0.55f, 0.56f, 0.6f) };
            foreach (Box box in TestArena.Boxes)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "ArenaBox";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = box.Center.ToUnity();
                cube.transform.localScale = box.Size.ToUnity();
                cube.GetComponent<Renderer>().sharedMaterial = boxMaterial;
            }

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
