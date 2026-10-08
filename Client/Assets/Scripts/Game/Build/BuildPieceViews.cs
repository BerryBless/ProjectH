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
    // Phase 13.5 D10, D12: the body draws the piece's actual (edited, or predicted) shape (PieceMeshes.MeshOf). Box shapes
    // (walls, floors, flat roofs, roof passages) get one BoxCollider per PartsOf box on the root, so the aim ray goes
    // through an open window exactly where the server's shot does (a convex MeshCollider would fill it); slopes keep a
    // convex MeshCollider. Every root is listed (root -> view) so the aim ray's collider tells which piece it hit
    // (TryGetPieceId, edit mode's target).
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
            public MeshFilter Filter;
            // Phase 13.5 D12: one per PartsOf box (made on first need, the unused ones disabled); ramps never have any.
            public readonly BoxCollider[] Parts = new BoxCollider[BuildGrid.MaxPartsPerPiece];
            public MeshCollider Slope;   // ramps and roofs (disabled while a roof is flat or a passage)
            public BuildPieceType Type;
            public uint Id;              // the piece shown, 0 while pooled
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
        // Phase 13.5 D10: every view's root (active or pooled) -> its view. An entry goes when its root is destroyed.
        private readonly Dictionary<GameObject, PieceView> _byRoot = new Dictionary<GameObject, PieceView>();
        private readonly Box[] _parts = new Box[BuildGrid.MaxPartsPerPiece];

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

        // 기능: 조준 Raycast가 맞힌 Collider가 어느 조각의 것인지 낸다(Phase 13.5 D10: 편집 대상).
        // 입력: collider - 맞힌 Collider(null 가능), id - 결과.
        // 출력: 보이는 조각의 Collider면 true와 그 id, 아니면 false.
        public bool TryGetPieceId(Collider collider, out uint id)
        {
            id = 0;
            if (collider == null || !_byRoot.TryGetValue(collider.gameObject, out PieceView view)) return false;
            if (view.Id == 0 || view.Root == null || !view.Root.activeSelf) return false;
            id = view.Id;
            return true;
        }

        public void Clear()
        {
            _building.Clear();
            foreach (KeyValuePair<uint, PieceView> kv in _active) Release(kv.Value);
            _active.Clear();
        }

        // 기능: 조각 Object 전체와 Material을 파괴하고 표를 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 활성·짓는 중·루트 표가 빈다.
        public void Dispose()
        {
            UnityObjects.Destroy(_root);
            for (int i = 0; i < _materials.Length; i++) UnityObjects.Destroy(_materials[i]);
            _active.Clear();
            _building.Clear();
            _byRoot.Clear();
        }

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

        // 기능: 바뀐 조각 하나를 보이거나 갱신한다. 종류가 바뀌면 다른 풀의 뷰로 바꾸고, 모양이 바뀌면 다시 놓는다(피해만 바뀌면 Collider는 그대로).
        // 입력: id - 조각 id, piece - 보이는 기록(편집 예측 포함), catalog - 건설 수치, serverTick - 추정 서버 Tick.
        // 출력: 반환값 없음. 뷰의 Id가 이 조각이 되고, 짓는 중이면 짓는 중 목록에 오른다.
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
            view.Id = id;
            if (view.Posed && !view.Shape.Equals(piece.Shape)) view.Posed = false;   // a damage-only change leaves the colliders alone
            bool wasBuilding = view.Building;
            Draw(view, piece, catalog, serverTick);
            if (view.Building && !wasBuilding) _building.Add(id);
        }

        // 기능: 조각 하나를 서버 Tick 기준의 건설 높이·피해 단계로 그린다. 모양이 새로 정해지면 자세·Mesh·Collider도 맞춘다.
        // 입력: view - 조각 뷰, piece - 보이는 기록(편집 예측 포함), catalog - 건설 수치(null 가능), serverTick - 추정 서버 Tick.
        // 출력: 반환값 없음. view의 Building·Height·Stage가 갱신된다.
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
                Pose(view, piece.Shape);
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

        // 기능: 조각 루트의 자세와 그릴 Mesh, Collider를 모양에 맞춘다(Phase 13.5 D12). 모양이 바뀔 때만 부른다.
        //   상자 모양은 PartsOf 상자마다 BoxCollider(루트 기준 중심·크기), 경사면은 볼록 MeshCollider 하나.
        // 입력: view - 조각 뷰, shape - 보이는 모양.
        // 출력: 반환값 없음. 쓰지 않는 BoxCollider는 꺼진다. 처음 필요한 BoxCollider만 새로 붙인다.
        private void Pose(PieceView view, in BuildPieceShape shape)
        {
            Transform root = view.Root.transform;
            _meshes.PlaceRoot(root, shape);
            Mesh mesh = _meshes.MeshOf(shape);
            if (view.Filter.sharedMesh != mesh) view.Filter.sharedMesh = mesh;
            int count = BuildGrid.PartsOf(shape, _parts, out bool slope);
            if (view.Slope != null)
            {
                if (slope && view.Slope.sharedMesh != mesh) view.Slope.sharedMesh = mesh;
                if (view.Slope.enabled != slope) view.Slope.enabled = slope;
            }
            // Box shapes have an unrotated root (PlaceRoot turns only ramps and one-way roofs), so world offsets are local.
            Vector3 pivot = root.position;
            for (int i = 0; i < view.Parts.Length; i++)
            {
                BoxCollider part = view.Parts[i];
                if (i >= count)
                {
                    if (part != null && part.enabled) part.enabled = false;
                    continue;
                }
                if (part == null)
                {
                    part = view.Root.AddComponent<BoxCollider>();
                    view.Parts[i] = part;
                }
                part.center = _parts[i].Center.ToUnity() - pivot;
                part.size = _parts[i].Size.ToUnity();
                if (!part.enabled) part.enabled = true;
            }
        }

        private void Hide(uint id)
        {
            if (!_active.TryGetValue(id, out PieceView view)) return;
            _active.Remove(id);
            Release(view);
        }

        private void RemoveBuildingAt(int i)
        {
            int last = _building.Count - 1;
            _building[i] = _building[last];
            _building.RemoveAt(last);
        }

        // 기능: 종류의 풀에서 뷰를 꺼내거나 새로 만든다. 새 뷰는 루트 표에 오르고, 경사로·지붕에는 볼록 MeshCollider를 붙인다.
        // 입력: type - 조각 종류.
        // 출력: 켜진 뷰(Posed false: 다음 Draw가 모양에 맞춰 Mesh·Collider를 정한다).
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
            var filter = body.AddComponent<MeshFilter>();
            filter.sharedMesh = _meshes.MeshOf(type);
            var renderer = body.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            MeshCollider slope = null;
            if (type == BuildPieceType.Ramp || type == BuildPieceType.Roof)
            {
                slope = root.AddComponent<MeshCollider>();
                slope.sharedMesh = _meshes.MeshOf(type);
                slope.convex = true;
            }
            // Box colliders (walls, floors, flat roofs) are added by Pose when the shape is known.
            var view = new PieceView { Root = root, Body = body.transform, Renderer = renderer, Filter = filter, Slope = slope, Type = type };
            _byRoot.Add(root, view);
            return view;
        }

        // 기능: 뷰를 풀로 돌려보낸다. 풀이 MaxPooled면 파괴하고 루트 표에서도 지운다.
        // 입력: view - 돌려보낼 뷰.
        // 출력: 반환값 없음. 뷰의 Id가 0이 된다.
        private void Release(PieceView view)
        {
            if (view.Root == null) return;
            view.Building = false;
            view.Id = 0;
            Stack<PieceView> pool = _pools[(int)view.Type];
            if (pool.Count >= MaxPooled)
            {
                _byRoot.Remove(view.Root);
                UnityObjects.Destroy(view.Root);
                return;
            }
            view.Root.SetActive(false);
            pool.Push(view);
        }
    }
}
