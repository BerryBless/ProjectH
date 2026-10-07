using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 16 D8: one view per Shared LootContainers entry (at most LootContainers.MaxCount = 64), made once and never
    // destroyed until Dispose. A chest is a brown box with a lid that carries a gold band; open, the lid swings back on its
    // rear hinge and every part turns dark. An ammo box is a small green box, dark once open. A container that did not
    // spawn this match is hidden. Views use the built-in cube mesh (sharedMesh) and six shared materials (sharedMaterial
    // only, no Renderer.material copies). No colliders: a container is not a collider on the server either (players walk
    // through, shots pass). Apply runs only when LootState.ContainerVersion changes and touches only containers whose
    // state changed. Dispose destroys the objects and the materials (not the built-in mesh).
    public sealed class ContainerViews : System.IDisposable
    {
        // Share of a chest's height that is the body; the rest is the lid.
        private const float BodyShare = 0.72f;
        private const float BandWidth = 0.14f;   // m, across the lid from front to back
        private const float OpenLidAngle = -110f;

        private enum Shown : byte { Hidden, Closed, Open }

        private readonly GameObject _root;
        private readonly GameObject[] _views = new GameObject[LootContainers.MaxCount];
        private readonly Renderer[][] _parts = new Renderer[LootContainers.MaxCount][];
        private readonly Transform[] _lids = new Transform[LootContainers.MaxCount];   // hinge pivot (chests only)
        private readonly Shown[] _shown = new Shown[LootContainers.MaxCount];
        private readonly Material _chest;
        private readonly Material _chestOpen;
        private readonly Material _band;
        private readonly Material _bandOpen;
        private readonly Material _ammo;
        private readonly Material _ammoOpen;
        private int _shownVersion = -1;

        // 기능: Container마다 뷰(Chest: 몸통·뚜껑·금색 띠, Ammo Box: 상자 하나)와 공유 Material 6개를 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 모든 Container가 숨겨진 객체(Dispose가 해제한다).
        public ContainerViews(Material source)
        {
            _root = new GameObject("LootContainers");
            _chest = new Material(source) { color = new Color(0.45f, 0.28f, 0.14f) };
            _chestOpen = new Material(source) { color = new Color(0.2f, 0.13f, 0.07f) };
            _band = new Material(source) { color = new Color(0.95f, 0.75f, 0.2f) };
            _bandOpen = new Material(source) { color = new Color(0.4f, 0.32f, 0.1f) };
            _ammo = new Material(source) { color = new Color(0.25f, 0.5f, 0.2f) };
            _ammoOpen = new Material(source) { color = new Color(0.11f, 0.2f, 0.09f) };
            Mesh cube = CubeMesh();

            var all = LootContainers.All;
            int count = System.Math.Min(all.Length, _views.Length);
            for (int i = 0; i < count; i++)
            {
                LootContainer c = all[i];
                Vector3 size = LootContainers.SizeOf(c.Kind).ToUnity();
                var root = new GameObject(c.Kind == LootContainerKind.Chest ? "Chest " + i : "AmmoBox " + i);
                root.transform.SetParent(_root.transform, false);
                root.transform.localPosition = c.Position.ToUnity();
                // Yaw 0 faces +Z like a character; the sizes are given at yaw 0, so the rotation alone turns the box.
                root.transform.localRotation = Quaternion.Euler(0f, c.Yaw, 0f);
                if (c.Kind == LootContainerKind.Chest)
                {
                    float bodyHeight = size.y * BodyShare;
                    float lidHeight = size.y - bodyHeight;
                    Renderer body = Part("Body", root.transform, cube, new Vector3(0f, bodyHeight * 0.5f, 0f), new Vector3(size.x, bodyHeight, size.z), _chest);
                    // The lid turns about its rear top edge (local -Z, the back of a chest facing +Z).
                    var hinge = new GameObject("Hinge").transform;
                    hinge.SetParent(root.transform, false);
                    hinge.localPosition = new Vector3(0f, bodyHeight, -size.z * 0.5f);
                    Renderer lid = Part("Lid", hinge, cube, new Vector3(0f, lidHeight * 0.5f, size.z * 0.5f), new Vector3(size.x, lidHeight, size.z), _chest);
                    Renderer band = Part("Band", hinge, cube, new Vector3(0f, lidHeight * 0.5f, size.z * 0.5f),
                        new Vector3(BandWidth, lidHeight + 0.02f, size.z + 0.02f), _band);
                    _parts[i] = new[] { body, lid, band };
                    _lids[i] = hinge;
                }
                else
                {
                    Renderer box = Part("Box", root.transform, cube, new Vector3(0f, size.y * 0.5f, 0f), size, _ammo);
                    _parts[i] = new[] { box };
                }
                root.SetActive(false);
                _views[i] = root;
            }
        }

        // 기능: 상태 모델의 생성·열림 마스크를 뷰에 반영한다(ContainerVersion이 바뀌었을 때만, 상태가 바뀐 Container만). 할당 없음.
        // 입력: loot - 최신 Loot 상태.
        // 출력: 반환값 없음. 생성 안 됨 = 숨김, 닫힘 = 원래 색·뚜껑 닫힘, 열림 = 어두운 색·뚜껑 젖힘.
        public void Apply(LootState loot)
        {
            if (_root == null || loot.ContainerVersion == _shownVersion) return;
            _shownVersion = loot.ContainerVersion;
            for (int i = 0; i < _views.Length; i++)
            {
                if (_views[i] == null) continue;
                ulong bit = 1UL << i;
                Shown want = (loot.SpawnedMask & bit) == 0 ? Shown.Hidden : (loot.OpenedMask & bit) != 0 ? Shown.Open : Shown.Closed;
                if (want == _shown[i]) continue;
                _shown[i] = want;
                if (want == Shown.Hidden)
                {
                    _views[i].SetActive(false);
                    continue;
                }
                bool open = want == Shown.Open;
                Renderer[] parts = _parts[i];
                if (_lids[i] != null)
                {
                    parts[0].sharedMaterial = open ? _chestOpen : _chest;
                    parts[1].sharedMaterial = open ? _chestOpen : _chest;
                    parts[2].sharedMaterial = open ? _bandOpen : _band;
                    _lids[i].localRotation = open ? Quaternion.Euler(OpenLidAngle, 0f, 0f) : Quaternion.identity;
                }
                else
                {
                    parts[0].sharedMaterial = open ? _ammoOpen : _ammo;
                }
                _views[i].SetActive(true);
            }
        }

        // 기능: 뷰(뿌리의 자식)와 Material을 파괴한다. 내장 큐브 Mesh는 에셋이라 파괴하지 않는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            Destroy(_chest);
            Destroy(_chestOpen);
            Destroy(_band);
            Destroy(_bandOpen);
            Destroy(_ammo);
            Destroy(_ammoOpen);
        }

        // 기능: 충돌체 없는 큐브 조각 하나를 만든다(MeshFilter + MeshRenderer, 그림자 없음).
        // 입력: name - 이름, parent - 부모, mesh - 공유 큐브 Mesh, position - 부모 기준 중심, scale - 크기, material - 공유 Material.
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

        // 기능: 내장 큐브 Mesh를 얻는다(임시 Primitive에서 꺼내고 그 Object는 바로 파괴한다).
        // 입력: 없음.
        // 출력: 내장 큐브 Mesh(에셋: 파괴하지 않는다).
        private static Mesh CubeMesh()
        {
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = probe.GetComponent<MeshFilter>().sharedMesh;
            // Immediate: a deferred destroy would leave its collider in the world for this frame's rays.
            Object.DestroyImmediate(probe);
            return mesh;
        }

        // 기능: null이 아니면 Material을 파괴한다.
        // 입력: material - 만든 Material.
        // 출력: 반환값 없음.
        private static void Destroy(Material material)
        {
            if (material != null) Object.Destroy(material);
        }
    }
}
