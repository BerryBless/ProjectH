using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // D14: the safe zone on the ground. Two LineRenderer circles (the current circle, and the next target in another
    // color) and one see-through wall. The wall is a unit open tube mesh built once (no caps, so it does not cover
    // the arena, and drawn from both sides so it is visible from inside); only its transform scale changes. Every
    // object, mesh and material is created in the constructor and destroyed in Dispose; nothing is allocated per
    // frame. The circle comes from ZoneMath with the server's ZoneState (same formula as the server's judgement).
    public sealed class ZoneView : System.IDisposable
    {
        private const int Segments = 128;
        private const float WallHeight = 40f;   // D12: tall enough to stand out above the Lookout plateau (6 m)
        private const float LineLift = 0.05f;   // above the floor, so the line does not flicker into it
        private const float LineWidth = 0.15f;
        private const float MinWallRadius = 0.01f;

        private static readonly Color CurrentColor = new Color(1f, 1f, 1f);
        private static readonly Color TargetColor = new Color(0.3f, 0.75f, 1f);
        private static readonly Color WallColor = new Color(0.3f, 0.55f, 1f, 0.25f);

        private readonly GameObject _root;
        private readonly LineRenderer _current;
        private readonly LineRenderer _target;
        private readonly Transform _wall;
        private readonly GameObject _wallObject;
        private readonly Mesh _tube;
        private readonly Material _currentMaterial;
        private readonly Material _targetMaterial;
        private readonly Material _wallMaterial;
        private readonly Vector3[] _points = new Vector3[Segments];
        private readonly float[] _cos = new float[Segments];
        private readonly float[] _sin = new float[Segments];

        private ZoneState _zone;
        private bool _visible;
        private float _drawnX = float.NaN;
        private float _drawnZ;
        private float _drawnRadius;

        // 기능: 현재·목표 원 LineRenderer 두 개와 반투명 벽(Tube Mesh), Material을 만든다.
        // 입력: 없음.
        // 출력: 숨겨진 ZoneView.
        public ZoneView()
        {
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * Mathf.PI * 2f / Segments;
                _cos[i] = Mathf.Cos(angle);
                _sin[i] = Mathf.Sin(angle);
            }

            _root = new GameObject("ZoneView");

            // The opaque line materials are copies of URP's Lit material (LitMaterial: a primitive's default material
            // is magenta in a build). Never renderer.material (clones per object).
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material source = LitMaterial.Source(probe.GetComponent<Renderer>().sharedMaterial);
            _currentMaterial = new Material(source) { color = CurrentColor };
            _targetMaterial = new Material(source) { color = TargetColor };
            // The wall uses the built-in Sprites/Default: always included in player builds, alpha blended and
            // Cull Off (seen from inside) with no keyword variant. URP Lit's transparent mode needs the
            // _SURFACE_TYPE_TRANSPARENT shader_feature variant, which is stripped from builds when no asset uses it,
            // so a Lit wall would render opaque in a Standalone build. The tint and alpha come from the color.
            Shader sprite = Shader.Find("Sprites/Default");
            if (sprite != null)
            {
                _wallMaterial = new Material(sprite) { color = WallColor };
            }
            else
            {
                Debug.LogWarning("ZoneView: Sprites/Default not found; the zone wall falls back to URP Lit (may be opaque in a build).");
                _wallMaterial = new Material(source) { color = WallColor };
                MakeTransparentTwoSided(_wallMaterial);
            }
            Object.Destroy(probe);

            _current = CreateLine("Current", _currentMaterial);
            _target = CreateLine("Target", _targetMaterial);

            _tube = BuildTube();
            _wallObject = new GameObject("Wall");
            _wall = _wallObject.transform;
            _wall.SetParent(_root.transform, false);
            _wallObject.AddComponent<MeshFilter>().sharedMesh = _tube;
            var wallRenderer = _wallObject.AddComponent<MeshRenderer>();
            wallRenderer.sharedMaterial = _wallMaterial;
            wallRenderer.shadowCastingMode = ShadowCastingMode.Off;
            wallRenderer.receiveShadows = false;

            _root.SetActive(false);
        }

        // 기능: 새 ZoneState를 적용하고 목표 원을 그린다.
        // 입력: zone - 서버가 보낸 구역 상태.
        // 출력: 반환값 없음. Phase가 0보다 크면 표시되고 다음 Tick에서 현재 원이 다시 그려진다. Phase 0이면 숨겨진다.
        // A new ZoneState (phase change, join). Phase 0 = no zone: nothing is drawn.
        public void SetZone(in ZoneState zone)
        {
            if (_root == null) return;
            _zone = zone;
            _visible = zone.Phase > 0;
            _root.SetActive(_visible);
            _drawnX = float.NaN;   // redraw the current circle on the next Tick
            if (!_visible) return;
            DrawCircle(_target, zone.ToX, zone.ToZ, zone.ToRadius);
        }

        // 기능: 구역 상태를 지우고 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. 저장된 ZoneState가 지워지고 루트가 비활성화된다.
        public void Clear()
        {
            if (_root == null) return;
            _zone = default;
            _visible = false;
            _root.SetActive(false);
        }

        // 기능: 매 프레임 서버 Tick 기준 현재 원을 계산하고, 바뀐 경우에만 선과 벽을 갱신한다.
        // 입력: serverTick - 현재 서버 Tick 추정값(소수, 보간 Render Tick + 보간 지연).
        // 출력: 반환값 없음. 원이 움직였을 때 현재 원 선과 벽 위치·크기·표시가 갱신된다.
        // Once per frame with the estimated server tick. The circle points and the wall scale change only while the
        // circle moves (during a shrink).
        public void Tick(double serverTick)
        {
            if (!_visible || _root == null) return;
            ZoneMath.Sample(_zone, serverTick, out float x, out float z, out float radius);
            if (x == _drawnX && z == _drawnZ && radius == _drawnRadius) return;
            _drawnX = x;
            _drawnZ = z;
            _drawnRadius = radius;
            DrawCircle(_current, x, z, radius);
            bool wall = radius > MinWallRadius;
            if (_wallObject.activeSelf != wall) _wallObject.SetActive(wall);
            if (wall)
            {
                _wall.localPosition = new Vector3(x, 0f, z);
                _wall.localScale = new Vector3(radius, WallHeight, radius);
            }
        }

        // 기능: 루트, Tube Mesh, Material 3개를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 생성한 오브젝트·Mesh·Material이 해제된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_tube != null) Object.Destroy(_tube);
            if (_currentMaterial != null) Object.Destroy(_currentMaterial);
            if (_targetMaterial != null) Object.Destroy(_targetMaterial);
            if (_wallMaterial != null) Object.Destroy(_wallMaterial);
        }

        // 기능: 원을 지형 높이 위에 Segments개 점으로 그린다.
        // 입력: line - 그릴 LineRenderer, x - 중심 X, z - 중심 Z, radius - 반지름.
        // 출력: 반환값 없음. 선의 점이 바뀌고, 반지름이 MinWallRadius 이하면 선이 숨겨진다.
        private void DrawCircle(LineRenderer line, float x, float z, float radius)
        {
            // Phase 6 spec interpretation 10: on the terrain, so the line is not buried in a hill.
            for (int i = 0; i < Segments; i++)
            {
                float px = x + _cos[i] * radius;
                float pz = z + _sin[i] * radius;
                _points[i] = new Vector3(px, GameMap.Terrain.Height(px, pz) + LineLift, pz);
            }
            line.SetPositions(_points);
            // A radius-0 circle collapses to a point; with a 0.15 m width it would show as a stray dot, so the
            // line is hidden under the same limit as the wall.
            line.enabled = radius > MinWallRadius;
        }

        // 기능: 루트 아래에 원 그리기용 LineRenderer를 만든다.
        // 입력: name - GameObject 이름, material - 공유 Material.
        // 출력: Loop·World Space로 설정된 LineRenderer.
        private LineRenderer CreateLine(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = Segments;
            line.startWidth = LineWidth;
            line.endWidth = LineWidth;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        // 기능: 반지름 1, 높이 1, 양끝이 열린 원통 Mesh를 만든다.
        // 입력: 없음.
        // 출력: 새 Mesh(Dispose에서 파괴).
        // Radius 1, height 1, open at both ends.
        private static Mesh BuildTube()
        {
            var vertices = new Vector3[(Segments + 1) * 2];
            var triangles = new int[Segments * 6];
            for (int i = 0; i <= Segments; i++)
            {
                float angle = i * Mathf.PI * 2f / Segments;
                float cx = Mathf.Cos(angle);
                float cz = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(cx, 0f, cz);
                vertices[i * 2 + 1] = new Vector3(cx, 1f, cz);
            }
            for (int i = 0; i < Segments; i++)
            {
                int v = i * 2;
                int t = i * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2;
                triangles[t + 4] = v + 1;
                triangles[t + 5] = v + 3;
            }
            var mesh = new Mesh { name = "ZoneTube", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // 기능: Sprites/Default가 없을 때 URP Lit Material을 반투명 양면으로 설정한다.
        // 입력: material - 설정할 Material.
        // 출력: 반환값 없음. Material의 Surface·Blend·ZWrite·Cull 설정과 Render Queue가 바뀐다.
        // Fallback only (Sprites/Default missing). URP Lit surface options set from code: alpha blended, no depth
        // write, both faces. Works in the Editor; in a build the transparent variant may be stripped.
        private static void MakeTransparentTwoSided(Material material)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
        }
    }
}
