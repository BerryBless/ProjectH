using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D15: every world item as a small spinning shape: weapon = cube in its rarity color, ammo = cylinder,
    // Medkit / Shield Cell = sphere. Holds the client's item list (WorldItemList) and one view per entry,
    // in the same index order. Views come from a pool that never exceeds WorldItemList.Capacity (256) and
    // is reused, never destroyed, until Dispose; all views share the materials made here and 3 built-in meshes
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
        private readonly Material _resourceMaterial;
        private readonly Material _cardMaterial;   // Phase 14 D9: a teammate's reboot card (only its team is told of it)
        private readonly Material _grenadeMaterial;   // Phase 17 D9
        private int _freeCount;
        private int _created;

        // 기능: 공유 Mesh 3개와 Material(희귀도 5, 탄, 구급상자, 실드 셀, 자원, Phase 14 재투입 카드, Phase 17 수류탄)을 만든다.
        // 입력: 없음.
        // 출력: 빈 아이템 목록과 풀을 가진 객체(Dispose가 해제한다).
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
            _resourceMaterial = new Material(template) { color = new Color(0.55f, 0.4f, 0.25f) };   // Phase 13 D15
            _cardMaterial = new Material(template) { color = new Color(0.3f, 0.95f, 0.6f) };
            _grenadeMaterial = new Material(template) { color = new Color(0.35f, 0.45f, 0.2f) };
        }

        public WorldItemList Items => _items;

        // 기능: 아이템을 목록에 넣거나 갱신하고(입장 시 WorldItems, ItemSpawned) 새 아이템이면 풀에서 뷰를 빌려 입힌 뒤 위치를 맞춘다.
        // 입력: item - 월드 아이템.
        // 출력: 반환값 없음. 목록이 가득 차 못 넣으면 아무것도 하지 않는다.
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

        // 기능: 아이템을 목록에서 지우고 뷰를 풀에 돌려준다(ItemRemoved). 목록이 마지막 아이템을 구멍으로 옮기면 뷰 배열도 따라 옮긴다.
        // 입력: itemId - 지울 아이템 id.
        // 출력: 반환값 없음. 모르는 id면 아무것도 하지 않는다.
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

        // 기능: 모든 아이템을 지우고 뷰를 풀에 돌려준다(끊김: 다음 입장이 목록 전체를 다시 보낸다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Return(_views[i]);
                _views[i] = null;
            }
            _items.Clear();
        }

        // 기능: 매 프레임 모든 아이템 뷰를 같은 각도로 돌린다(아이템별 상태 없음, 할당 없음).
        // 입력: time - 현재 시각(초).
        // 출력: 반환값 없음.
        public void Tick(float time)
        {
            Quaternion spin = Quaternion.Euler(0f, time * SpinDegreesPerSecond, 0f);
            for (int i = 0; i < _items.Count; i++) _views[i].localRotation = spin;
        }

        // 기능: 풀의 뷰(뿌리의 자식)와 만든 Material(Phase 17 수류탄 포함)을 모두 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);   // every pooled view is its child
            foreach (Material m in _rarityMaterials) if (m != null) Object.Destroy(m);
            if (_ammoMaterial != null) Object.Destroy(_ammoMaterial);
            if (_medkitMaterial != null) Object.Destroy(_medkitMaterial);
            if (_shieldCellMaterial != null) Object.Destroy(_shieldCellMaterial);
            if (_resourceMaterial != null) Object.Destroy(_resourceMaterial);
            if (_cardMaterial != null) Object.Destroy(_cardMaterial);
            if (_grenadeMaterial != null) Object.Destroy(_grenadeMaterial);
        }

        // 기능: 아이템 종류에 맞는 공유 Mesh·Material·크기를 뷰에 입힌다(Phase 14: 재투입 카드는 납작한 초록 판, Phase 17: 수류탄은 올리브색 구).
        // 입력: view - 풀에서 빌린 뷰, item - 월드 아이템.
        // 출력: 반환값 없음.
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
                case ItemKind.Material:
                    filter.sharedMesh = _cube;
                    renderer.sharedMaterial = _resourceMaterial;
                    view.localScale = new Vector3(0.4f, 0.25f, 0.4f);
                    break;
                case ItemKind.RebootCard:
                    filter.sharedMesh = _cube;
                    renderer.sharedMaterial = _cardMaterial;
                    view.localScale = new Vector3(0.45f, 0.06f, 0.3f);
                    break;
                default:
                    filter.sharedMesh = _sphere;
                    renderer.sharedMaterial = item.DefId == (byte)ConsumableType.Medkit ? _medkitMaterial
                        : item.DefId == (byte)ConsumableType.Grenade ? _grenadeMaterial : _shieldCellMaterial;
                    view.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                    break;
            }
        }

        // 기능: 풀에서 뷰 하나를 빌린다(빈 뷰가 없으면 Capacity까지 새로 만든다).
        // 입력: 없음.
        // 출력: 켜진 뷰의 Transform. 만든 수가 Capacity에 닿았으면 null.
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

        // 기능: 뷰를 끄고 풀에 돌려준다.
        // 입력: view - 돌려줄 뷰(null이면 아무것도 하지 않는다).
        // 출력: 반환값 없음.
        private void Return(Transform view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _free[_freeCount++] = view;
        }

        // 기능: 내장 Primitive Mesh를 얻고, 아직 없으면 Material 원본도 그 Primitive에서 고른다(임시 Object는 바로 파괴한다).
        // 입력: type - Primitive 종류, template - Material 원본(null이면 LitMaterial.Source로 채운다).
        // 출력: 내장 Mesh(에셋: 파괴하지 않는다).
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
