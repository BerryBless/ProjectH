using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §44-§47, §123-§125): the confirmed pieces on screen. Each is a root with its collider at full
    // size (the aim ray and the camera stop at it, like the server's shots and collision do from the first tick) and a
    // body child drawn with one of 9 shared materials (3 materials x 3 damage stages) at its construction height.
    //  - Apply reads BuildStore.Changed (call it before ClearChanged): a piece added or changed is placed, one gone goes
    //    back to its type's pool. Nothing is done for pieces that did not change.
    //  - Tick redraws only the pieces still being built (their height and stage grow with the server tick).
    // Pools are per type (the collider kind differs) and bounded by MaxPooled; meshes are shared (PieceMeshes). Main
    // thread only. Dispose destroys the objects and the materials.
    public sealed class BuildPieceViews : System.IDisposable
    {
        public const int MaxPooled = 256;

        private static readonly Color[] MaterialColors =
        {
            new Color(0.62f, 0.45f, 0.27f),   // wood
            new Color(0.58f, 0.58f, 0.6f),    // stone
            new Color(0.42f, 0.48f, 0.56f),   // metal
        };

        private sealed class PieceView
        {
            public GameObject Root;
            public Transform Body;
            public MeshRenderer Renderer;
            public BoxCollider Box;   // walls and floors
            public BuildPieceType Type;
            public int Stage = -1;
            public float Height = -1f;
            public bool Building;
            public bool Posed;               // root pose and collider size are set for Shape
            public BuildPieceShape Shape;
        }

        private readonly PieceMeshes _meshes;
        private readonly GameObject _root;
        private readonly Material[] _materials = new Material[3 * BuildPieceLook.Stages];
        private readonly Stack<PieceView>[] _pools = new Stack<PieceView>[4];
        private readonly Dictionary<uint, PieceView> _active = new Dictionary<uint, PieceView>();
        private readonly List<uint> _building = new List<uint>();

        // 기능: 조각 루트 GameObject, 재료 3 x 손상 단계 3의 공유 Material, 종류별 Pool을 만든다.
        // 입력: meshes - 공유 조각 Mesh, source - 복제할 기본 Material.
        // 출력: 표시 중인 조각이 없는 BuildPieceViews.
        public BuildPieceViews(PieceMeshes meshes, Material source)
        {
            _meshes = meshes;
            _root = new GameObject("BuildPieces");
            for (int m = 0; m < BuildMaterials.Count; m++)
            {
                for (int s = 0; s < BuildPieceLook.Stages; s++)
                {
                    // Darker per stage, and the last one reddened: damage reads at a glance.
                    Color c = MaterialColors[m] * (1f - 0.25f * s);
                    if (s == 2) c = Color.Lerp(c, new Color(0.55f, 0.12f, 0.08f), 0.35f);
                    c.a = 1f;
                    _materials[m * BuildPieceLook.Stages + s] = new Material(source) { color = c };
                }
            }
            for (int i = 0; i < _pools.Length; i++) _pools[i] = new Stack<PieceView>();
        }

        public int Count => _active.Count;

        // 기능: BuildStore.Changed의 조각을 화면에 반영하고 건설 중인 조각을 다시 그린다. ClearChanged 전에 호출한다.
        // 입력: store - 확정 조각 저장소, catalog - 건설 Catalog(null 허용), serverTick - 기준 Server Tick.
        // 출력: 반환값 없음. 추가·변경된 조각은 배치되고 사라진 조각은 Pool로 돌아간다.
        public void Apply(BuildStore store, BuildCatalogData catalog, double serverTick)
        {
            if (_root == null) return;
            IReadOnlyList<uint> changed = store.Changed;
            for (int i = 0; i < changed.Count; i++)
            {
                uint id = changed[i];
                if (store.TryGet(id, out BuildPieceRecord piece)) Show(id, piece, catalog, serverTick);
                else Hide(id);
            }
            Tick(store, catalog, serverTick);
        }

        // 기능: 표시 중인 조각의 월드 위치를 얻는다.
        // 입력: id - 조각 ID.
        // 출력: 표시 중이면 true와 루트 위치, 아니면 false와 default.
        // The world position of a shown piece (for a destruction effect), false when it is not shown.
        public bool TryGetCenter(uint id, out Vector3 center)
        {
            if (_active.TryGetValue(id, out PieceView view) && view.Root != null)
            {
                center = view.Root.transform.position;
                return true;
            }
            center = default;
            return false;
        }

        // 기능: 표시 중인 모든 조각을 Pool로 돌려보낸다.
        // 입력: 없음.
        // 출력: 반환값 없음. 활성 조각과 건설 중 목록이 비워진다.
        public void Clear()
        {
            _building.Clear();
            foreach (KeyValuePair<uint, PieceView> kv in _active) Release(kv.Value);
            _active.Clear();
        }

        // 기능: 조각 GameObject와 생성한 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 루트 GameObject와 Material이 파괴되고 목록이 비워진다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            for (int i = 0; i < _materials.Length; i++)
            {
                if (_materials[i] != null) Object.Destroy(_materials[i]);
            }
            _active.Clear();
            _building.Clear();
        }

        // 기능: 건설 중인 조각만 높이와 손상 단계를 다시 그린다.
        // 입력: store - 확정 조각 저장소, catalog - 건설 Catalog, serverTick - 기준 Server Tick.
        // 출력: 반환값 없음. 건설이 끝났거나 사라진 조각은 건설 중 목록에서 빠진다.
        private void Tick(BuildStore store, BuildCatalogData catalog, double serverTick)
        {
            for (int i = _building.Count - 1; i >= 0; i--)
            {
                uint id = _building[i];
                if (!_active.TryGetValue(id, out PieceView view) || !store.TryGet(id, out BuildPieceRecord piece))
                {
                    RemoveBuildingAt(i);
                    continue;
                }
                Draw(view, piece, catalog, serverTick);
                if (!view.Building) RemoveBuildingAt(i);
            }
        }

        // 기능: 확정 조각 하나를 표시한다. 종류가 바뀌었으면 View를 바꾸고 모양이 바뀌었으면 다시 배치한다.
        // 입력: id - 조각 ID, piece - 조각 기록, catalog - 건설 Catalog, serverTick - 기준 Server Tick.
        // 출력: 반환값 없음. 조각 View가 그려지고, 건설 중이 되면 건설 중 목록에 추가된다.
        private void Show(uint id, in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            if (_active.TryGetValue(id, out PieceView view) && view.Type != piece.Shape.Type)
            {
                Release(view);
                _active.Remove(id);
                view = null;
            }
            if (view == null)
            {
                view = Take(piece.Shape.Type);
                _active.Add(id, view);
            }
            if (view.Posed && !view.Shape.Equals(piece.Shape)) view.Posed = false;   // a damage-only change leaves the colliders alone
            bool wasBuilding = view.Building;
            Draw(view, piece, catalog, serverTick);
            if (view.Building && !wasBuilding) _building.Add(id);
        }

        // 기능: View의 루트 자세·Collider 크기(처음이거나 모양이 바뀐 때), 건설 높이, 손상 단계 Material 중 바뀐 것만 갱신한다.
        // 입력: view - 그릴 View, piece - 조각 기록, catalog - 건설 Catalog(null이면 건설 완료로 본다), serverTick - 기준 Server Tick.
        // 출력: 반환값 없음. View의 Transform·Collider·Material과 Building 표시가 갱신된다.
        private void Draw(PieceView view, in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            int m = (int)piece.Material;
            int constructionTicks = catalog != null ? catalog.ConstructionTicks[m] : 0;
            float progress = BuildPieceLook.Progress(piece.CreatedTick, serverTick, constructionTicks);
            view.Building = progress < 1f;
            float height = BuildPieceLook.ConstructionScale(progress);
            if (!view.Posed)
            {
                view.Posed = true;
                view.Shape = piece.Shape;
                view.Height = -1f;
                _meshes.PlaceRoot(view.Root.transform, piece.Shape);
                if (view.Box != null) view.Box.size = BuildGrid.BoxOf(piece.Shape).Size.ToUnity();
            }
            if (height != view.Height)
            {
                view.Height = height;
                _meshes.PlaceBody(view.Body, piece.Shape, height);
            }
            int stage = BuildPieceLook.DamageStage(piece, catalog, serverTick);
            int index = m * BuildPieceLook.Stages + stage;
            if (index != view.Stage)
            {
                view.Stage = index;
                view.Renderer.sharedMaterial = _materials[index];
            }
        }

        // 기능: 표시 중인 조각을 숨기고 Pool로 돌려보낸다.
        // 입력: id - 조각 ID.
        // 출력: 반환값 없음. 표시 중이 아니면 아무것도 하지 않는다.
        private void Hide(uint id)
        {
            if (!_active.TryGetValue(id, out PieceView view)) return;
            _active.Remove(id);
            Release(view);
        }

        // 기능: 건설 중 목록의 항목을 마지막 항목과 바꿔 지운다.
        // 입력: i - 지울 Index.
        // 출력: 반환값 없음. 건설 중 목록이 하나 줄어든다.
        private void RemoveBuildingAt(int i)
        {
            int last = _building.Count - 1;
            _building[i] = _building[last];
            _building.RemoveAt(last);
        }

        // 기능: 종류별 Pool에서 View를 꺼내거나 없으면 새로 만든다(벽·바닥은 BoxCollider, 경사면·지붕은 convex MeshCollider).
        // 입력: type - 조각 종류.
        // 출력: 활성화되어 다시 배치·그리기가 필요한 상태의 PieceView.
        private PieceView Take(BuildPieceType type)
        {
            Stack<PieceView> pool = _pools[(int)type];
            while (pool.Count > 0)
            {
                PieceView pooled = pool.Pop();
                if (pooled.Root == null) continue;   // destroyed with the scene
                pooled.Root.SetActive(true);
                pooled.Stage = -1;
                pooled.Posed = false;
                pooled.Building = false;
                return pooled;
            }
            var root = new GameObject("Piece " + type);
            root.transform.SetParent(_root.transform, false);
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = _meshes.MeshOf(type);
            var renderer = body.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            BoxCollider box = null;
            if (type == BuildPieceType.Wall || type == BuildPieceType.Floor)
            {
                // The root sits at the box centre; the collider is the box's size (set when placed).
                box = root.AddComponent<BoxCollider>();
            }
            else
            {
                var collider = root.AddComponent<MeshCollider>();
                collider.sharedMesh = _meshes.MeshOf(type);
                collider.convex = true;
            }
            return new PieceView { Root = root, Body = body.transform, Renderer = renderer, Box = box, Type = type };
        }

        // 기능: View를 비활성화해 Pool에 넣고, Pool이 MaxPooled만큼 차 있으면 파괴한다.
        // 입력: view - 돌려보낼 View.
        // 출력: 반환값 없음. 이미 파괴된 View는 무시한다.
        private void Release(PieceView view)
        {
            if (view.Root == null) return;
            view.Building = false;
            Stack<PieceView> pool = _pools[(int)view.Type];
            if (pool.Count >= MaxPooled)
            {
                Object.Destroy(view.Root);
                return;
            }
            view.Root.SetActive(false);
            pool.Push(view);
        }
    }
}
