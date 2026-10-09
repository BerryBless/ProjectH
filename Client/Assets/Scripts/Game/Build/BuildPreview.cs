using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §37, §52-§54): the see-through pieces of build mode. One ghost per piece type for the
    // candidate, tinted by BuildController's judgement (valid, invalid, not affordable), and BuildController.MaxPending
    // ghosts for the placements waiting for their BuildResult (white). Ghosts have no collider: they never stop the
    // camera, the aim ray or the player (prediction collides with confirmed pieces only, D3). Built once; the pending
    // ghosts are redrawn only when the pending set changes. Sprites/Default for the transparency (always in a build, like
    // ZoneView's wall). Dispose destroys the objects and the materials.
    public sealed class BuildPreview : System.IDisposable
    {
        private static readonly Color ValidColor = new Color(0.3f, 0.7f, 1f, 0.35f);
        private static readonly Color InvalidColor = new Color(1f, 0.25f, 0.2f, 0.35f);
        private static readonly Color NoResourceColor = new Color(1f, 0.65f, 0.15f, 0.35f);
        private static readonly Color PendingColor = new Color(1f, 1f, 1f, 0.3f);

        private static bool _warnedNoSprite;
        private readonly PieceMeshes _meshes;
        private readonly GameObject _root;
        private readonly Transform[] _ghostRoots = new Transform[4];
        private readonly Transform[] _ghostBodies = new Transform[4];
        private readonly MeshRenderer[] _ghostRenderers = new MeshRenderer[4];
        private readonly Transform[] _pendingRoots = new Transform[BuildController.MaxPending];
        private readonly Transform[] _pendingBodies = new Transform[BuildController.MaxPending];
        private readonly MeshFilter[] _pendingFilters = new MeshFilter[BuildController.MaxPending];
        private readonly Material[] _stateMaterials = new Material[3];
        private readonly Material _pendingMaterial;
        private int _shownType = -1;
        private int _pendingVersion = -1;

        // 기능: 종류별 후보 유령 4개와 대기 배치 유령 MaxPending개, 반투명 Material 4개를 만든다(모두 숨긴 채).
        // 입력: meshes - 공유 Mesh(이 객체보다 오래 산다), fallback - Sprites/Default가 없을 때 쓸 불투명 Material.
        // 출력: 숨겨진 미리보기(Dispose가 Object와 Material을 파괴한다).
        public BuildPreview(PieceMeshes meshes, Material fallback)
        {
            _meshes = meshes;
            _root = new GameObject("BuildPreview");
            Shader sprite = Shader.Find("Sprites/Default");
            _stateMaterials[(int)BuildPreviewState.Valid] = Make(sprite, fallback, ValidColor);
            _stateMaterials[(int)BuildPreviewState.Invalid] = Make(sprite, fallback, InvalidColor);
            _stateMaterials[(int)BuildPreviewState.NoResource] = Make(sprite, fallback, NoResourceColor);
            _pendingMaterial = Make(sprite, fallback, PendingColor);
            for (int t = 0; t < 4; t++)
            {
                Transform body = CreateGhost("Ghost " + (BuildPieceType)t, meshes.MeshOf((BuildPieceType)t), _stateMaterials[0], out _ghostRoots[t], out MeshFilter _);
                _ghostBodies[t] = body;
                _ghostRenderers[t] = body.GetComponent<MeshRenderer>();
            }
            for (int i = 0; i < _pendingRoots.Length; i++)
                _pendingBodies[i] = CreateGhost("Pending", meshes.Box, _pendingMaterial, out _pendingRoots[i], out _pendingFilters[i]);
        }

        // 기능: 후보 유령을 이번 프레임의 후보 자리·판정 색으로 놓고, 대기 배치가 바뀐 프레임에만 대기 유령을 다시 놓는다.
        // 입력: build - 배치 컨트롤러(후보, 판정, 대기 배치).
        // 출력: 반환값 없음. 후보가 없으면 후보 유령을 숨긴다. 할당 없음.
        public void Update(BuildController build)
        {
            if (_root == null) return;
            int type = build.HasCandidate ? (int)build.Candidate.Type : -1;
            if (type != _shownType)
            {
                if (_shownType >= 0) _ghostRoots[_shownType].gameObject.SetActive(false);
                if (type >= 0) _ghostRoots[type].gameObject.SetActive(true);
                _shownType = type;
            }
            if (type >= 0)
            {
                _meshes.Place(_ghostRoots[type], _ghostBodies[type], build.Candidate, 1f);
                Material material = _stateMaterials[(int)build.CandidateState];
                if (_ghostRenderers[type].sharedMaterial != material) _ghostRenderers[type].sharedMaterial = material;
            }
            if (build.PendingVersion == _pendingVersion) return;
            _pendingVersion = build.PendingVersion;
            for (int i = 0; i < _pendingRoots.Length; i++)
            {
                bool show = i < build.PendingCount;
                if (show)
                {
                    BuildPieceShape shape = build.PendingAt(i).Shape;
                    _pendingFilters[i].sharedMesh = _meshes.MeshOf(shape.Type);
                    _meshes.Place(_pendingRoots[i], _pendingBodies[i], shape, 1f);
                }
                if (_pendingRoots[i].gameObject.activeSelf != show) _pendingRoots[i].gameObject.SetActive(show);
            }
        }

        // 기능: 후보·대기 유령을 모두 숨긴다(건설 모드를 나가거나 경기 상태를 비울 때).
        // 입력: 없음.
        // 출력: 반환값 없음. 다음 Update가 대기 유령을 다시 놓는다.
        public void HideAll()
        {
            if (_root == null) return;
            if (_shownType >= 0) _ghostRoots[_shownType].gameObject.SetActive(false);
            _shownType = -1;
            for (int i = 0; i < _pendingRoots.Length; i++) _pendingRoots[i].gameObject.SetActive(false);
            _pendingVersion = -1;
        }

        // 기능: 유령 Object와 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            for (int i = 0; i < _stateMaterials.Length; i++)
            {
                if (_stateMaterials[i] != null) Object.Destroy(_stateMaterials[i]);
            }
            if (_pendingMaterial != null) Object.Destroy(_pendingMaterial);
        }

        // 기능: 루트와 몸체(그림자 없는 MeshRenderer) 두 단으로 된 유령 하나를 꺼진 채 만든다(Collider 없음).
        // 입력: name - 루트 이름, mesh - 몸체 Mesh, material - 몸체 Material, root - 만든 루트 Transform, filter - 몸체 MeshFilter.
        // 출력: 몸체 Transform.
        private Transform CreateGhost(string name, Mesh mesh, Material material, out Transform root, out MeshFilter filter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var body = new GameObject("Body");
            body.transform.SetParent(go.transform, false);
            filter = body.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            root = go.transform;
            return body.transform;
        }

        // 기능: 반투명 Material을 만든다(Sprites/Default가 빌드에 없으면 경고 한 번 뒤 불투명 대체 Material).
        // 입력: sprite - Sprites/Default Shader(null 가능), fallback - 대체 원본, color - 색.
        // 출력: 새 Material(Dispose에서 파괴한다).
        private static Material Make(Shader sprite, Material fallback, Color color)
        {
            if (sprite == null && !_warnedNoSprite)
            {
                _warnedNoSprite = true;
                Debug.LogWarning("BuildPreview: Sprites/Default not found (not in the build); ghosts are drawn opaque.");
            }
            if (sprite != null) return new Material(sprite) { color = color };
            return new Material(fallback) { color = new Color(color.r, color.g, color.b, 1f) };
        }
    }
}
