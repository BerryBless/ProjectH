using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D15: every world item as a small spinning shape: weapon = cube in its rarity color, ammo = cylinder,
    // Medkit / Shield Cell = sphere. Holds the client's item list (WorldItemList) and one view per entry,
    // in the same index order. Views come from a pool that never exceeds WorldItemList.Capacity (256) and
    // is reused, never destroyed, until Dispose; all views share 8 materials and 3 built-in meshes
    // (sharedMaterial / sharedMesh only). No colliders: items never block the camera or the aim ray.
    public sealed class WorldItemViews : System.IDisposable
    {
        private const float Hover = 0.3f;
        private const float SpinDegreesPerSecond = 90f;

        private readonly WorldItemList _items = new WorldItemList();
        private readonly Transform[] _views = new Transform[WorldItemList.Capacity];   // parallel to _items
        private readonly Transform[] _free = new Transform[WorldItemList.Capacity];
        private readonly GameObject _root;
        private readonly Mesh _cube;
        private readonly Mesh _cylinder;
        private readonly Mesh _sphere;
        private readonly Material[] _rarityMaterials = new Material[ItemConstants.RarityCount];
        private readonly Material _ammoMaterial;
        private readonly Material _medkitMaterial;
        private readonly Material _shieldCellMaterial;
        private int _freeCount;
        private int _created;

        public WorldItemViews()
        {
            _root = new GameObject("WorldItems");
            Material template = null;
            _cube = BuiltinMesh(PrimitiveType.Cube, ref template);
            _cylinder = BuiltinMesh(PrimitiveType.Cylinder, ref template);
            _sphere = BuiltinMesh(PrimitiveType.Sphere, ref template);
            for (int i = 0; i < _rarityMaterials.Length; i++) _rarityMaterials[i] = new Material(template) { color = InventoryHud.RarityColors[i] };
            _ammoMaterial = new Material(template) { color = new Color(0.95f, 0.85f, 0.3f) };
            _medkitMaterial = new Material(template) { color = new Color(0.95f, 0.3f, 0.3f) };
            _shieldCellMaterial = new Material(template) { color = new Color(0.3f, 0.85f, 1f) };
        }

        public WorldItemList Items => _items;

        // WorldItems at join and ItemSpawned (a new item or an amount change).
        public void Upsert(in WorldItemData item)
        {
            if (!_items.Upsert(item, out int index, out bool added)) return;
            if (added)
            {
                Transform view = Rent();
                if (view == null) return;   // not reached: the list and the pool have the same capacity
                _views[index] = view;
                Dress(view, item);
            }
            _views[index].localPosition = item.Position.ToUnity() + new Vector3(0f, Hover, 0f);
        }

        public void Remove(ushort itemId)
        {
            if (!_items.Remove(itemId, out int removed, out int movedFrom)) return;
            Return(_views[removed]);
            _views[removed] = null;
            if (movedFrom >= 0)
            {
                _views[removed] = _views[movedFrom];
                _views[movedFrom] = null;
            }
        }

        // Disconnect: the next join sends the whole list again.
        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Return(_views[i]);
                _views[i] = null;
            }
            _items.Clear();
        }

        // Once per frame: one rotation for all (no per-item state, no allocation).
        public void Tick(float time)
        {
            Quaternion spin = Quaternion.Euler(0f, time * SpinDegreesPerSecond, 0f);
            for (int i = 0; i < _items.Count; i++) _views[i].localRotation = spin;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);   // every pooled view is its child
            foreach (Material m in _rarityMaterials) if (m != null) Object.Destroy(m);
            if (_ammoMaterial != null) Object.Destroy(_ammoMaterial);
            if (_medkitMaterial != null) Object.Destroy(_medkitMaterial);
            if (_shieldCellMaterial != null) Object.Destroy(_shieldCellMaterial);
        }

        private void Dress(Transform view, in WorldItemData item)
        {
            var filter = view.GetComponent<MeshFilter>();
            var renderer = view.GetComponent<MeshRenderer>();
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    filter.sharedMesh = _cube;
                    renderer.sharedMaterial = _rarityMaterials[item.Rarity < _rarityMaterials.Length ? item.Rarity : 0];
                    view.localScale = new Vector3(0.6f, 0.2f, 0.2f);
                    break;
                case ItemKind.Ammo:
                    filter.sharedMesh = _cylinder;
                    renderer.sharedMaterial = _ammoMaterial;
                    view.localScale = new Vector3(0.25f, 0.15f, 0.25f);
                    break;
                default:
                    filter.sharedMesh = _sphere;
                    renderer.sharedMaterial = item.DefId == (byte)ConsumableType.Medkit ? _medkitMaterial : _shieldCellMaterial;
                    view.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                    break;
            }
        }

        private Transform Rent()
        {
            Transform view;
            if (_freeCount > 0)
            {
                view = _free[--_freeCount];
            }
            else
            {
                if (_created == WorldItemList.Capacity) return null;
                var go = new GameObject("Item", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(_root.transform, false);
                view = go.transform;
                _created++;
            }
            view.gameObject.SetActive(true);
            return view;
        }

        private void Return(Transform view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _free[_freeCount++] = view;
        }

        // The primitive's mesh is a built-in asset that outlives the temporary object. The template is URP's Lit
        // material (LitMaterial: a primitive's default material is magenta in a build).
        private static Mesh BuiltinMesh(PrimitiveType type, ref Material template)
        {
            var go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            if (template == null) template = LitMaterial.Source(go.GetComponent<Renderer>().sharedMaterial);
            // Immediate: a deferred destroy would leave its collider in the world for the first frame.
            Object.DestroyImmediate(go);
            return mesh;
        }
    }
}
