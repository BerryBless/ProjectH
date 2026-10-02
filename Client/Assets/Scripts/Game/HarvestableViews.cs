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
