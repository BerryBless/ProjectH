using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 13.5 D10 (request §19): edit mode on screen: the target's tile grid (a wall's 3 x 3 on its face, a floor's,
    // roof's or ramp's 2 x 2 just above its top, BuildEdit.TileBox) and, for a roof or a ramp whose shape the selection
    // changes, a ghost of the preview shape. Tiles are tinted: chosen (blue, or red while the selection has no valid
    // state), under the crosshair (yellow), the rest faint. Nothing here collides (no colliders). The objects are made
    // once and only moved and re-tinted when BuildEditController.Version changes, so a frame with no change does nothing
    // and none allocates. Sprites/Default for the transparency, like BuildPreview. Dispose destroys the objects and the
    // materials.
    public sealed class BuildEditOverlay : System.IDisposable
    {
        private const int MaxTiles = BuildEdit.WallTiles;
        // Each tile is drawn this much smaller than its cell on the face (m), so the grid lines show between tiles.
        private const float TileGap = 0.06f;
        // A wall tile stands this much proud of the wall's faces (m), so it is drawn over the wall.
        private const float WallLift = 0.03f;
        private const float GridThickness = 0.04f;

        private static readonly Color IdleColor = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color HoverColor = new Color(1f, 0.9f, 0.3f, 0.4f);
        private static readonly Color ChosenColor = new Color(0.3f, 0.7f, 1f, 0.5f);
        private static readonly Color InvalidColor = new Color(1f, 0.25f, 0.2f, 0.5f);
        private static readonly Color GhostColor = new Color(0.3f, 0.7f, 1f, 0.3f);
        private static readonly Color GhostInvalidColor = new Color(1f, 0.25f, 0.2f, 0.3f);

        private static bool _warnedNoSprite;
        private readonly PieceMeshes _meshes;
        private readonly GameObject _root;
        private readonly Transform[] _tiles = new Transform[MaxTiles];
        private readonly MeshRenderer[] _tileRenderers = new MeshRenderer[MaxTiles];
        private readonly Transform _ghostRoot;
        private readonly Transform _ghostBody;
        private readonly MeshFilter _ghostFilter;
        private readonly MeshRenderer _ghostRenderer;
        private readonly Material _idle;
        private readonly Material _hover;
        private readonly Material _chosen;
        private readonly Material _invalid;
        private readonly Material _ghost;
        private readonly Material _ghostInvalid;
        private int _shownVersion = -1;
        private bool _shown;

        // 기능: 칸 9개와 미리보기 유령 하나, 반투명 Material 6개를 만든다(모두 숨긴 상태).
        // 입력: meshes - 공유 Mesh(칸은 단위 상자, 유령은 편집 Mesh), fallback - Sprites/Default가 없을 때 쓸 불투명 Material.
        // 출력: 숨겨진 오버레이.
        public BuildEditOverlay(PieceMeshes meshes, Material fallback)
        {
            _meshes = meshes;
            _root = new GameObject("BuildEditOverlay");
            Shader sprite = Shader.Find("Sprites/Default");
            _idle = Make(sprite, fallback, IdleColor);
            _hover = Make(sprite, fallback, HoverColor);
            _chosen = Make(sprite, fallback, ChosenColor);
            _invalid = Make(sprite, fallback, InvalidColor);
            _ghost = Make(sprite, fallback, GhostColor);
            _ghostInvalid = Make(sprite, fallback, GhostInvalidColor);
            for (int i = 0; i < MaxTiles; i++)
            {
                _tiles[i] = CreatePart("Tile " + i, meshes.Box, _idle, out MeshFilter _, out _tileRenderers[i]);
                _tiles[i].SetParent(_root.transform, false);
            }
            var ghost = new GameObject("Ghost");
            ghost.transform.SetParent(_root.transform, false);
            _ghostRoot = ghost.transform;
            _ghostBody = CreatePart("Body", meshes.Box, _ghost, out _ghostFilter, out _ghostRenderer);
            _ghostBody.SetParent(_ghostRoot, false);
            _ghostBody.gameObject.SetActive(true);
            ghost.SetActive(false);
        }

        // 기능: 편집 모드 상태를 화면에 맞춘다. 버전이 바뀐 프레임에만 칸과 유령을 옮기고 색을 바꾼다.
        // 입력: edit - 편집 컨트롤러.
        // 출력: 반환값 없음. 편집 중이 아니면 모두 숨긴다. 할당 없음.
        public void Update(BuildEditController edit)
        {
            if (_root == null) return;
            if (!edit.Active)
            {
                Hide();
                return;
            }
            if (_shown && edit.Version == _shownVersion) return;
            _shown = true;
            _shownVersion = edit.Version;
            BuildPieceShape target = edit.Target;
            int tiles = BuildEdit.TileCount(target.Type);
            Material chosen = edit.PreviewValid ? _chosen : _invalid;
            for (int i = 0; i < MaxTiles; i++)
            {
                bool show = i < tiles;
                if (_tiles[i].gameObject.activeSelf != show) _tiles[i].gameObject.SetActive(show);
                if (!show) continue;
                PlaceTile(_tiles[i], target, i);
                Material material = (edit.Selection & (1 << i)) != 0 ? chosen : i == edit.HoveredTile ? _hover : _idle;
                if (_tileRenderers[i].sharedMaterial != material) _tileRenderers[i].sharedMaterial = material;
            }
            // Walls and floors show their holes in the tiles; a roof's or a ramp's new shape needs the ghost.
            bool ghost = (target.Type == BuildPieceType.Roof || target.Type == BuildPieceType.Ramp) && !edit.Preview.Equals(target);
            if (_ghostRoot.gameObject.activeSelf != ghost) _ghostRoot.gameObject.SetActive(ghost);
            if (ghost)
            {
                BuildPieceShape preview = edit.Preview;
                Mesh mesh = _meshes.MeshOf(preview);
                if (_ghostFilter.sharedMesh != mesh) _ghostFilter.sharedMesh = mesh;
                _meshes.Place(_ghostRoot, _ghostBody, preview, 1f);
                Material material = edit.PreviewValid ? _ghost : _ghostInvalid;
                if (_ghostRenderer.sharedMaterial != material) _ghostRenderer.sharedMaterial = material;
            }
        }

        // 기능: 오버레이를 모두 숨긴다(편집 모드가 끝났거나 경기 상태를 비울 때).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Hide()
        {
            if (_root == null || !_shown) return;
            _shown = false;
            _shownVersion = -1;
            for (int i = 0; i < MaxTiles; i++) _tiles[i].gameObject.SetActive(false);
            _ghostRoot.gameObject.SetActive(false);
        }

        // 기능: 오버레이 Object와 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            DestroyMaterial(_idle);
            DestroyMaterial(_hover);
            DestroyMaterial(_chosen);
            DestroyMaterial(_invalid);
            DestroyMaterial(_ghost);
            DestroyMaterial(_ghostInvalid);
        }

        // 기능: 칸 하나를 놓는다. 벽 칸은 벽 면보다 조금 두껍고 면 방향으로 조금 작은 상자, 나머지는 조각 위의 얇은 판이다.
        // 입력: tile - 칸 Transform(단위 상자), shape - 대상 모양, index - 칸 번호.
        // 출력: 반환값 없음.
        private static void PlaceTile(Transform tile, in BuildPieceShape shape, int index)
        {
            Box box = BuildEdit.TileBox(shape, index);
            Vector3 min = box.Min.ToUnity();
            Vector3 max = box.Max.ToUnity();
            Vector3 center;
            Vector3 size;
            if (shape.Type == BuildPieceType.Wall)
            {
                center = (min + max) * 0.5f;
                size = max - min;
                size.y -= TileGap;
                if (shape.Rotation == 0)
                {
                    size.x -= TileGap;
                    size.z += WallLift * 2f;
                }
                else
                {
                    size.z -= TileGap;
                    size.x += WallLift * 2f;
                }
            }
            else
            {
                float y = BuildEditController.GridHeight(shape);
                center = new Vector3((min.x + max.x) * 0.5f, y, (min.z + max.z) * 0.5f);
                size = new Vector3(max.x - min.x - TileGap, GridThickness, max.z - min.z - TileGap);
            }
            tile.SetPositionAndRotation(center, Quaternion.identity);
            tile.localScale = size;
        }

        // 기능: 그림자 없는 MeshRenderer 하나를 가진 꺼진 Object를 만든다.
        // 입력: name - 이름, mesh - Mesh, material - Material, filter·renderer - 만든 Component.
        // 출력: 그 Object의 Transform(부모는 호출자가 정한다).
        private static Transform CreatePart(string name, Mesh mesh, Material material, out MeshFilter filter, out MeshRenderer renderer)
        {
            var go = new GameObject(name);
            filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        // 기능: 이 오버레이가 만든 Material 하나를 파괴한다.
        // 입력: material - Material(null 가능).
        // 출력: 반환값 없음.
        private static void DestroyMaterial(Material material)
        {
            if (material != null) Object.Destroy(material);
        }

        // 기능: 반투명 Material을 만든다(Sprites/Default가 빌드에 없으면 경고 한 번 뒤 불투명 대체 Material).
        // 입력: sprite - Sprites/Default Shader(null 가능), fallback - 대체 원본, color - 색.
        // 출력: 새 Material(Dispose에서 파괴한다).
        private static Material Make(Shader sprite, Material fallback, Color color)
        {
            if (sprite == null && !_warnedNoSprite)
            {
                _warnedNoSprite = true;
                Debug.LogWarning("BuildEditOverlay: Sprites/Default not found (not in the build); the edit grid is drawn opaque.");
            }
            if (sprite != null) return new Material(sprite) { color = color };
            return new Material(fallback) { color = new Color(color.r, color.g, color.b, 1f) };
        }
    }
}
