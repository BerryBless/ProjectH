using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 13 D6: one cube per Shared GameMap harvestable (a tree trunk, a rock, a wreck, a crate), hidden once the server
    // says it is destroyed (HarvestStates) and shown again at a round reset. Its collider (default layer) stops the camera
    // and the aim ray like the server's shots stop at it. Built once; Apply changes them only when the mask changes.
    // Four shared materials, one per kind. Dispose destroys the objects and the materials.
    public sealed class HarvestableViews : System.IDisposable
    {
        private readonly GameObject[] _views;
        private readonly Material[] _materials = new Material[4];
        private ulong _shownMask;

        // 기능: GameMap의 채집 대상마다 Collider가 있는 큐브를 Root 아래에 만들고 종류별 공유 Material 네 개를 입힌다.
        // 입력: 없음.
        // 출력: 모든 채집 대상이 서 있는 상태로 생성된 HarvestableViews.
        public HarvestableViews()
        {
            var root = new GameObject("Harvestables");
            Root = root;
            int count = GameMap.Harvestables.Length;
            _views = new GameObject[count];
            Material source = null;
            for (int i = 0; i < count; i++)
            {
                Harvestable h = GameMap.Harvestables[i];
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Harvestable " + i;
                cube.transform.SetParent(root.transform, false);
                cube.transform.position = h.Bounds.Center.ToUnity();
                cube.transform.localScale = h.Bounds.Size.ToUnity();
                var renderer = cube.GetComponent<Renderer>();
                if (source == null) source = LitMaterial.Source(renderer.sharedMaterial);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                _views[i] = cube;
            }
            _materials[(int)HarvestKind.Tree] = new Material(source) { color = new Color(0.36f, 0.25f, 0.14f) };
            _materials[(int)HarvestKind.Rock] = new Material(source) { color = new Color(0.5f, 0.5f, 0.52f) };
            _materials[(int)HarvestKind.Wreck] = new Material(source) { color = new Color(0.42f, 0.28f, 0.22f) };
            _materials[(int)HarvestKind.Crate] = new Material(source) { color = new Color(0.66f, 0.5f, 0.3f) };
            for (int i = 0; i < count; i++) _views[i].GetComponent<Renderer>().sharedMaterial = _materials[(int)GameMap.Harvestables[i].Kind];
        }

        public GameObject Root { get; }

        // 기능: 서버가 보낸 파괴 마스크가 바뀌었을 때만 파괴된 채집 대상은 숨기고 나머지는 보인다.
        // 입력: destroyedMask - bit i가 1이면 채집 대상 i가 파괴됨(HarvestStates).
        // 출력: 반환값 없음. 채집 대상 GameObject의 활성 상태가 바뀐다.
        // HarvestStates: bit i = harvestable i is destroyed.
        public void Apply(ulong destroyedMask)
        {
            if (destroyedMask == _shownMask) return;
            _shownMask = destroyedMask;
            for (int i = 0; i < _views.Length; i++)
            {
                bool standing = (destroyedMask & (1UL << i)) == 0;
                if (_views[i] != null && _views[i].activeSelf != standing) _views[i].SetActive(standing);
            }
        }

        // 기능: 채집 대상 Root와 동적으로 만든 Material 네 개를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 오브젝트와 Material이 파괴된다.
        public void Dispose()
        {
            if (Root != null) Object.Destroy(Root);
            for (int i = 0; i < _materials.Length; i++)
            {
                if (_materials[i] != null) Object.Destroy(_materials[i]);
            }
        }
    }
}
