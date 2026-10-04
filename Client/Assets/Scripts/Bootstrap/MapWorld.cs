using ProjectH.Client.Game;
using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Bootstrap
{
    // Phase 6 map (D1-D7): the terrain mesh with its MeshCollider, one cube per Shared GameMap box, ground outside the
    // walls and a light. The colliders are for camera collision and the aim ray only; movement collision is
    // MovementSimulation with the same GameMap, so there is a single source for the map. Three shared materials
    // (terrain, structures, cover), no shadows: low-spec first. Built once at startup.
    public static class MapWorld
    {
        // A box rising at least this far above the terrain under it is a building wall, roof or outer wall; lower
        // boxes are cover (display only).
        private const float StructureHeight = 2.5f;

        // 기능: Shared GameMap으로 지형 Mesh·MeshCollider, 맵 상자 Cube, 바깥 바닥, 조명(Scene에 없을 때)을 만든다.
        // 입력: 없음.
        // 출력: 만든 MapWorld 루트 GameObject와 out으로 공유 Material 3개(지형, 구조물, 엄폐물)·지형 Mesh. 셋 모두 호출자가 파괴해야 한다.
        // The caller destroys the returned root, every material and the terrain mesh (GameClient.OnDestroy).
        public static GameObject Build(out Material[] materials, out Mesh terrainMesh)
        {
            var root = new GameObject("MapWorld");

            // Ground beyond the walls, 400 x 400 m, a little under the terrain's flat edge so the two never z-fight.
            var outside = GameObject.CreatePrimitive(PrimitiveType.Plane);
            outside.name = "OutsideGround";
            outside.transform.SetParent(root.transform, false);
            outside.transform.localPosition = new Vector3(0f, -0.05f, 0f);
            outside.transform.localScale = new Vector3(40f, 1f, 40f);

            // Copies of URP's Lit material (LitMaterial: a primitive's default material is magenta in a build).
            Renderer outsideRenderer = outside.GetComponent<Renderer>();
            Material source = LitMaterial.Source(outsideRenderer.sharedMaterial);
            var terrainMaterial = new Material(source) { color = new Color(0.42f, 0.55f, 0.36f) };
            var structureMaterial = new Material(source) { color = new Color(0.55f, 0.56f, 0.6f) };
            var coverMaterial = new Material(source) { color = new Color(0.62f, 0.5f, 0.36f) };
            materials = new[] { terrainMaterial, structureMaterial, coverMaterial };
            outsideRenderer.sharedMaterial = terrainMaterial;

            terrainMesh = new Mesh { name = "Terrain" };
            terrainMesh.vertices = TerrainMesh.Vertices(GameMap.Terrain);
            terrainMesh.triangles = TerrainMesh.Triangles(GameMap.Terrain);
            terrainMesh.RecalculateNormals();
            terrainMesh.RecalculateBounds();
            var terrain = new GameObject("Terrain");
            terrain.transform.SetParent(root.transform, false);
            terrain.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
            var terrainRenderer = terrain.AddComponent<MeshRenderer>();
            terrainRenderer.sharedMaterial = terrainMaterial;
            terrainRenderer.shadowCastingMode = ShadowCastingMode.Off;
            terrain.AddComponent<MeshCollider>().sharedMesh = terrainMesh;

            foreach (Box box in GameMap.Boxes)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "MapBox";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = box.Center.ToUnity();
                cube.transform.localScale = box.Size.ToUnity();
                var renderer = cube.GetComponent<Renderer>();
                renderer.sharedMaterial = IsStructure(box) ? structureMaterial : coverMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var sun = new GameObject("Sun");
                sun.transform.SetParent(root.transform, false);
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                var light = sun.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;   // low-spec default
            }
            return root;
        }

        // 기능: 맵 상자가 건물 벽·지붕·외벽 같은 구조물인지 판정한다.
        // 입력: box - 판정할 GameMap 상자.
        // 출력: 상자 윗면이 그 아래 지형보다 StructureHeight 이상 높으면 true(구조물), 아니면 false(엄폐물).
        private static bool IsStructure(Box box) =>
            box.Max.Y - GameMap.Terrain.Height(box.Center.X, box.Center.Z) >= StructureHeight;
    }
}
