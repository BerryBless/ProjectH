using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 16 D8: the supply drops, a fixed pool of SupplyDropsPacket.MaxSupplyDrops (4) views made once, one per entry of
    // the received list. A blue box; while it falls a parachute (a cone) above it and its height from SupplyDropFall at the
    // render tick, the same formula the server uses (no physics, nothing sent per tick); landed, a thin light pillar rises
    // from it; open, the box turns grey and the pillar goes. Apply runs only when LootState.DropVersion changes; Tick moves
    // only the falling ones every frame. Built-in cube and cylinder meshes (sharedMesh), one cone mesh made here, four shared
    // materials. No colliders (the server has no supply drop collider either). Dispose destroys the objects, the cone mesh
    // and the materials.
    public sealed class SupplyDropViews : System.IDisposable
    {
        private const float BoxSize = 1.2f;
        private const float ChuteRadius = 1.8f;
        private const float ChuteHeight = 1.2f;
        private const float ChuteLift = 3.2f;     // cone base above the box bottom
        private const float PillarWidth = 0.35f;
        private const float PillarHeight = 40f;
        private const int ConeSegments = 12;

        private readonly GameObject _root;
        private readonly Transform[] _views = new Transform[SupplyDropsPacket.MaxSupplyDrops];
        private readonly Renderer[] _boxes = new Renderer[SupplyDropsPacket.MaxSupplyDrops];
        private readonly GameObject[] _chutes = new GameObject[SupplyDropsPacket.MaxSupplyDrops];
        private readonly GameObject[] _pillars = new GameObject[SupplyDropsPacket.MaxSupplyDrops];
        // What each pool view shows: the list entry it draws (copied on Apply) and whether it is drawn as falling.
        private readonly SupplyDropInfo[] _drops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
        private readonly bool[] _falling = new bool[SupplyDropsPacket.MaxSupplyDrops];
        private readonly Mesh _cone;
        private readonly Material _blue;
        private readonly Material _grey;
        private readonly Material _chute;
        private readonly Material _pillar;
        private int _count;
        private int _shownVersion = -1;

        // 기능: Supply Drop 뷰 4개(상자·낙하산·빛기둥)와 원뿔 Mesh, 공유 Material 4개를 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 숨겨진 풀(Dispose가 해제한다).
        public SupplyDropViews(Material source)
        {
            _root = new GameObject("SupplyDrops");
            _blue = new Material(source) { color = new Color(0.15f, 0.4f, 0.95f) };
            _grey = new Material(source) { color = new Color(0.45f, 0.45f, 0.48f) };
            _chute = new Material(source) { color = new Color(0.95f, 0.95f, 0.98f) };
            _pillar = new Material(source) { color = new Color(0.55f, 0.85f, 1f) };
            _cone = BuildCone(ConeSegments);
            Mesh cube = BuiltinMesh(PrimitiveType.Cube);
            Mesh cylinder = BuiltinMesh(PrimitiveType.Cylinder);
            for (int i = 0; i < _views.Length; i++)
            {
                var root = new GameObject("SupplyDrop " + i).transform;
                root.SetParent(_root.transform, false);
                _boxes[i] = Part("Box", root, cube, new Vector3(0f, BoxSize * 0.5f, 0f), new Vector3(BoxSize, BoxSize, BoxSize), _blue);
                _chutes[i] = Part("Parachute", root, _cone, new Vector3(0f, ChuteLift, 0f), new Vector3(ChuteRadius, ChuteHeight, ChuteRadius), _chute).gameObject;
                // The cylinder mesh is 2 m tall around its middle.
                _pillars[i] = Part("Pillar", root, cylinder, new Vector3(0f, BoxSize + PillarHeight * 0.5f, 0f),
                    new Vector3(PillarWidth, PillarHeight * 0.5f, PillarWidth), _pillar).gameObject;
                root.gameObject.SetActive(false);
                _views[i] = root;
            }
        }

        // 기능: 받은 Supply Drop 목록을 뷰에 반영한다(DropVersion이 바뀌었을 때만): 수평 위치, 색, 낙하산·빛기둥, 남는 뷰 숨김. 할당 없음.
        // 입력: loot - 최신 Loot 상태, tick - 그리는 서버 Tick(낙하 높이).
        // 출력: 반환값 없음.
        public void Apply(LootState loot, double tick)
        {
            if (_root == null || loot.DropVersion == _shownVersion) return;
            _shownVersion = loot.DropVersion;
            _count = System.Math.Min(loot.DropCount, _views.Length);
            for (int i = 0; i < _count; i++)
            {
                SupplyDropInfo d = loot.Drop(i);
                _drops[i] = d;
                bool opened = d.State == SupplyDropState.Opened;
                _boxes[i].sharedMaterial = opened ? _grey : _blue;
                _falling[i] = d.State == SupplyDropState.Falling;
                SetActive(_chutes[i], _falling[i]);
                SetActive(_pillars[i], d.State == SupplyDropState.Landed);
                _views[i].localPosition = new Vector3(d.X, LootState.HeightOf(d, tick), d.Z);
                SetActive(_views[i].gameObject, true);
            }
            for (int i = _count; i < _views.Length; i++) SetActive(_views[i].gameObject, false);
        }

        // 기능: 낙하 중인 Supply Drop의 높이만 매 프레임 맞춘다(착지 Tick이 지나면 착지 높이에 멈추고 낙하산을 접는다). 할당 없음.
        // 입력: tick - 그리는 서버 Tick(원격 플레이어·수송기와 같은 렌더 Tick).
        // 출력: 반환값 없음.
        public void Tick(double tick)
        {
            for (int i = 0; i < _count; i++)
            {
                if (!_falling[i]) continue;
                SupplyDropInfo d = _drops[i];
                Vector3 p = _views[i].localPosition;
                p.y = LootState.HeightOf(d, tick);
                _views[i].localPosition = p;
                // Down before the server's Landed arrives: fold the chute, no pillar until that packet says Landed.
                if (tick >= d.LandTick)
                {
                    _falling[i] = false;
                    SetActive(_chutes[i], false);
                }
            }
        }

        // 기능: 모든 뷰를 숨긴다(끊김). 다음 Apply가 다시 그리도록 버전을 잊는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            if (_root == null) return;
            for (int i = 0; i < _views.Length; i++)
            {
                SetActive(_views[i].gameObject, false);
                _falling[i] = false;
            }
            _count = 0;
            _shownVersion = -1;
        }

        // 기능: 뷰, 원뿔 Mesh, Material을 파괴한다. 내장 큐브·원기둥 Mesh는 에셋이라 파괴하지 않는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_cone != null) Object.Destroy(_cone);
            if (_blue != null) Object.Destroy(_blue);
            if (_grey != null) Object.Destroy(_grey);
            if (_chute != null) Object.Destroy(_chute);
            if (_pillar != null) Object.Destroy(_pillar);
        }

        // 기능: 바뀔 때만 SetActive를 부른다.
        // 입력: go - 대상, active - 켤지.
        // 출력: 반환값 없음.
        private static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active) go.SetActive(active);
        }

        // 기능: 충돌체 없는 조각 하나를 만든다(MeshFilter + MeshRenderer, 그림자 없음).
        // 입력: name - 이름, parent - 부모, mesh - 공유 Mesh, position - 부모 기준 위치, scale - 크기, material - 공유 Material.
        // 출력: 만든 조각의 Renderer.
        private static Renderer Part(string name, Transform parent, Mesh mesh, Vector3 position, Vector3 scale, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return renderer;
        }

        // 기능: 내장 Primitive Mesh를 얻는다(임시 Object는 바로 파괴한다).
        // 입력: type - Primitive 종류.
        // 출력: 내장 Mesh(에셋: 파괴하지 않는다).
        private static Mesh BuiltinMesh(PrimitiveType type)
        {
            var probe = GameObject.CreatePrimitive(type);
            Mesh mesh = probe.GetComponent<MeshFilter>().sharedMesh;
            // Immediate: a deferred destroy would leave its collider in the world for this frame's rays.
            Object.DestroyImmediate(probe);
            return mesh;
        }

        // 기능: 바닥 반지름 1, 높이 1인 원뿔 Mesh를 만든다(바닥 중심이 원점, 꼭짓점이 +Y; 옆면은 면마다 정점을 따로 둬 각진 음영).
        // 입력: segments - 둘레 조각 수(3 이상).
        // 출력: 새 Mesh(호출자가 Dispose에서 파괴한다).
        private static Mesh BuildCone(int segments)
        {
            // Side: three vertices per triangle (flat shading). Base: centre + ring, facing down.
            var vertices = new Vector3[segments * 3 + segments + 1];
            var triangles = new int[segments * 3 * 2];
            int v = 0, t = 0;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;
                vertices[v] = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                vertices[v + 1] = Vector3.up;
                vertices[v + 2] = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                // Clockwise seen from outside (Unity's front face).
                triangles[t++] = v;
                triangles[t++] = v + 1;
                triangles[t++] = v + 2;
                v += 3;
            }
            int centre = v;
            vertices[v++] = Vector3.zero;
            int ring = v;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vertices[v++] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            for (int i = 0; i < segments; i++)
            {
                triangles[t++] = centre;
                triangles[t++] = ring + i;
                triangles[t++] = ring + (i + 1) % segments;
            }
            var mesh = new Mesh { name = "SupplyDropCone" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
