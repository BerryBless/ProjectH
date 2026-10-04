using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D15: every world item as a small spinning shape: weapon = cube in its rarity color, ammo = cylinder,
    // Medkit / Shield Cell = sphere, building material = brown cube. Holds the client's item list (WorldItemList)
    // and one view per entry, in the same index order. Views come from a pool that never exceeds
    // WorldItemList.Capacity (256) and is reused, never destroyed, until Dispose; all views share 9 materials
    // (5 rarity colors, ammo, medkit, shield cell, resource) and 3 built-in meshes
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
        private int _freeCount;
        private int _created;

        // 기능: 루트 오브젝트, 내장 Mesh 3개, 공유 Material(희귀도별 5개, 탄약, 메드킷, 실드셀, 자원)을 만든다.
        // 입력: 없음.
        // 출력: 아이템과 뷰가 없는 WorldItemViews.
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
        }

        // 기능: 클라이언트의 월드 아이템 목록을 돌려준다.
        // 입력: 없음.
        // 출력: 뷰와 같은 인덱스 순서의 WorldItemList.
        public WorldItemList Items => _items;

        // 기능: WorldItems·ItemSpawned의 아이템을 목록에 반영하고 뷰를 배치한다.
        // 입력: item - 서버가 보낸 아이템 데이터.
        // 출력: 반환값 없음. 새 아이템이면 Pool에서 뷰를 빌려 모양과 색을 입히고, 뷰 위치가 갱신된다. 목록이 가득 차면 무시한다.
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

        // 기능: ItemRemoved의 아이템 뷰를 Pool에 돌려주고 목록과 같은 방식으로 뷰 배열을 옮긴다.
        // 입력: itemId - 지울 아이템 ID.
        // 출력: 반환값 없음. 뷰가 비활성화되고 마지막 뷰가 빈 자리로 옮겨진다. 모르는 ID는 무시한다.
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

        // 기능: 모든 뷰를 Pool에 돌려주고 목록을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 뷰가 모두 비활성화되고 목록이 비워진다.
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

        // 기능: 매 프레임 모든 아이템 뷰를 같은 각도로 회전시킨다.
        // 입력: time - 회전 각도를 계산할 시간(초, 호출자는 Time.time).
        // 출력: 반환값 없음. 뷰의 localRotation이 갱신된다(할당 없음).
        // Once per frame: one rotation for all (no per-item state, no allocation).
        public void Tick(float time)
        {
            Quaternion spin = Quaternion.Euler(0f, time * SpinDegreesPerSecond, 0f);
            for (int i = 0; i < _items.Count; i++) _views[i].localRotation = spin;
        }

        // 기능: 루트(모든 Pool 뷰 포함)와 생성한 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 오브젝트와 Material이 해제된다. 내장 Mesh는 파괴하지 않는다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);   // every pooled view is its child
            foreach (Material m in _rarityMaterials) if (m != null) Object.Destroy(m);
            if (_ammoMaterial != null) Object.Destroy(_ammoMaterial);
            if (_medkitMaterial != null) Object.Destroy(_medkitMaterial);
            if (_shieldCellMaterial != null) Object.Destroy(_shieldCellMaterial);
            if (_resourceMaterial != null) Object.Destroy(_resourceMaterial);
        }

        // 기능: 아이템 종류에 맞게 뷰의 Mesh·Material·크기를 정한다.
        // 입력: view - 꾸밀 뷰, item - 표시할 아이템.
        // 출력: 반환값 없음. 무기는 희귀도 색 큐브, 탄약은 실린더, 자원은 갈색 큐브, 그 외는 구(메드킷 빨강, 실드셀 파랑)가 된다.
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
                default:
                    filter.sharedMesh = _sphere;
                    renderer.sharedMaterial = item.DefId == (byte)ConsumableType.Medkit ? _medkitMaterial : _shieldCellMaterial;
                    view.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                    break;
            }
        }

        // 기능: Pool에서 뷰를 꺼내거나 Capacity 안에서 새로 만든다.
        // 입력: 없음.
        // 출력: 활성화된 뷰, 이미 Capacity만큼 만들었고 남은 뷰가 없으면 null.
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

        // 기능: 뷰를 비활성화하고 Pool에 돌려준다.
        // 입력: view - 돌려줄 뷰(null이면 무시).
        // 출력: 반환값 없음. 뷰가 비활성화되고 Free 목록에 들어간다.
        private void Return(Transform view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _free[_freeCount++] = view;
        }

        // 기능: 임시 Primitive를 만들어 내장 Mesh를 얻고, 처음이면 Lit 템플릿 Material도 얻는다.
        // 입력: type - Primitive 종류, template - 아직 없으면 채울 템플릿 Material.
        // 출력: 내장 Mesh. 임시 오브젝트는 즉시 파괴된다.
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
