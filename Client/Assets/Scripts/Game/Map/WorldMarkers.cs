using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D11: the team markers in the world.
    //   - Pings: a fixed pool of MaxTeamPings thin beams (built-in cylinder mesh, one shared material per kind: Location
    //     yellow, Enemy red, Item sky blue, Danger orange), each with a distance text ("23 m", rebuilt only when the whole
    //     metre changes) over it on screen, or an arrow at the screen edge pointing to it while it is off screen.
    //   - Waypoints: a fixed pool of MaxWaypoints tall pillars (ours white, teammates' green).
    // No colliders (markers never block the camera or the aim ray), no shadows, sharedMaterial only. Everything is made
    // once here and destroyed in Dispose; per frame only transforms, the label and arrow positions (written when they move
    // more than half a pixel) and, when a pool slot starts showing another ping, its material and colour change.
    public sealed class WorldMarkers : System.IDisposable
    {
        private const float BeamHeight = 3f;
        private const float BeamWidth = 0.12f;
        private const float PillarHeight = 10f;
        private const float PillarWidth = 0.25f;
        private const float LabelLift = 3.4f;     // the label sits over the beam
        private const float EdgeMargin = 40f;     // screen px kept free at the edge for the arrow
        private const float ArrowSize = 26f;

        private readonly GameObject _root;
        private readonly GameObject _canvas;
        private readonly Material[] _kindMaterials = new Material[4];
        private readonly Material _myWaypointMaterial;
        private readonly Material _teamWaypointMaterial;
        private readonly Transform[] _beams = new Transform[MapMarkerConstants.MaxTeamPings];
        private readonly Renderer[] _beamRenderers = new Renderer[MapMarkerConstants.MaxTeamPings];
        private readonly byte[] _slotPingId = new byte[MapMarkerConstants.MaxTeamPings];
        private readonly ushort[] _slotOwner = new ushort[MapMarkerConstants.MaxTeamPings];
        private readonly MapMarkerKind[] _slotKind = new MapMarkerKind[MapMarkerConstants.MaxTeamPings];
        private readonly bool[] _slotUsed = new bool[MapMarkerConstants.MaxTeamPings];
        private readonly DistanceText[] _distances = new DistanceText[MapMarkerConstants.MaxTeamPings];
        private readonly Text[] _labels = new Text[MapMarkerConstants.MaxTeamPings];
        private readonly RawImage[] _arrows = new RawImage[MapMarkerConstants.MaxTeamPings];
        private readonly Vector2[] _labelAt = new Vector2[MapMarkerConstants.MaxTeamPings];
        private readonly Vector2[] _arrowAt = new Vector2[MapMarkerConstants.MaxTeamPings];
        private readonly float[] _arrowAngle = new float[MapMarkerConstants.MaxTeamPings];
        private readonly Transform[] _pillars = new Transform[MapMarkerConstants.MaxWaypoints];
        private readonly Renderer[] _pillarRenderers = new Renderer[MapMarkerConstants.MaxWaypoints];
        private readonly bool[] _pillarMine = new bool[MapMarkerConstants.MaxWaypoints];
        private int _beamsShown;
        private int _pillarsShown;

        // 기능: Ping 기둥 풀, Waypoint 기둥 풀, 화면 문구·화살표 Canvas, 공유 Material을 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것), textures - 화살표 텍스처(이 객체보다 오래 산다).
        // 출력: 숨겨진 WorldMarkers(Dispose가 모두 해제한다).
        public WorldMarkers(Material source, MapTextures textures)
        {
            _root = new GameObject("WorldMarkers");
            _kindMaterials[(int)MapMarkerKind.Location] = Tinted(source, MapColors.Location);
            _kindMaterials[(int)MapMarkerKind.Enemy] = Tinted(source, MapColors.Enemy);
            _kindMaterials[(int)MapMarkerKind.Item] = Tinted(source, MapColors.Item);
            _kindMaterials[(int)MapMarkerKind.Danger] = Tinted(source, MapColors.Danger);
            _myWaypointMaterial = Tinted(source, MapColors.MyWaypoint);
            _teamWaypointMaterial = Tinted(source, MapColors.TeamWaypoint);
            for (int i = 0; i < _beams.Length; i++)
            {
                _beams[i] = CreateColumn("Ping " + i, new Vector3(BeamWidth, BeamHeight * 0.5f, BeamWidth), _kindMaterials[0], out _beamRenderers[i]);
                _distances[i] = new DistanceText();
            }
            for (int i = 0; i < _pillars.Length; i++)
                _pillars[i] = CreateColumn("Waypoint " + i, new Vector3(PillarWidth, PillarHeight * 0.5f, PillarWidth), _teamWaypointMaterial, out _pillarRenderers[i]);

            // Under the minimap (96) and the crosshair (100): labels never cover the HUD corners' text.
            _canvas = UiFactory.CreateCanvas("PingLabels", 91, interactive: false);
            for (int i = 0; i < _labels.Length; i++)
            {
                Text label = UiFactory.CreateText("Distance" + i, _canvas.transform, string.Empty, 18, TextAnchor.MiddleCenter, Vector2.zero,
                    Vector2.zero, new Vector2(120f, 24f));
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                label.gameObject.SetActive(false);
                _labels[i] = label;
                RectTransform arrowRect = UiFactory.CreateRect("Arrow" + i, _canvas.transform, Vector2.zero, Vector2.zero, new Vector2(ArrowSize, ArrowSize));
                arrowRect.pivot = new Vector2(0.5f, 0.5f);
                var arrow = arrowRect.gameObject.AddComponent<RawImage>();
                arrow.texture = textures.Arrow;
                arrow.raycastTarget = false;
                arrowRect.gameObject.SetActive(false);
                _arrows[i] = arrow;
                _labelAt[i] = new Vector2(float.NaN, float.NaN);
                _arrowAt[i] = new Vector2(float.NaN, float.NaN);
                _arrowAngle[i] = float.NaN;
            }
        }

        // 기능: 팀 Ping과 Waypoint를 월드와 화면에 그린다(매 프레임). 할당 없음(거리 문구는 정수 m가 바뀔 때만 만든다).
        // 입력: camera - 화면 투영에 쓸 카메라(null이면 문구·화살표를 숨긴다), from - 거리를 재는 위치(내 발 또는 관전 대상),
        //   markers - 팀 표시, serverTick - 추정 서버 Tick(끝난 Ping은 숨긴다), myId - 내 Entity id(Waypoint 색).
        // 출력: 반환값 없음.
        public void Draw(Camera camera, Vector3 from, TeamMarkerState markers, double serverTick, ushort myId)
        {
            if (_root == null) return;
            int shown = 0;
            for (int i = 0; i < markers.PingCount && shown < _beams.Length; i++)
            {
                if (!markers.IsLive(i, serverTick)) continue;
                DrawPing(shown++, markers.Ping(i), camera, from);
            }
            HidePingsFrom(shown);

            int pillars = 0;
            for (int i = 0; i < markers.WaypointCount && pillars < _pillars.Length; i++)
            {
                MarkerWaypoint w = markers.Waypoint(i);
                Transform pillar = _pillars[pillars];
                if (!pillar.gameObject.activeSelf) pillar.gameObject.SetActive(true);
                pillar.position = new Vector3(w.Position.X, w.Position.Y + PillarHeight * 0.5f, w.Position.Z);
                bool mine = w.OwnerId == myId;
                if (mine != _pillarMine[pillars] || pillars >= _pillarsShown)
                {
                    _pillarMine[pillars] = mine;
                    _pillarRenderers[pillars].sharedMaterial = mine ? _myWaypointMaterial : _teamWaypointMaterial;
                }
                pillars++;
            }
            for (int i = pillars; i < _pillarsShown; i++) _pillars[i].gameObject.SetActive(false);
            _pillarsShown = pillars;
        }

        // 기능: 모든 기둥·문구·화살표를 숨긴다(경기 상태를 비울 때).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            if (_root == null) return;
            HidePingsFrom(0);
            for (int i = 0; i < _pillarsShown; i++) _pillars[i].gameObject.SetActive(false);
            _pillarsShown = 0;
        }

        // 기능: 뿌리(기둥)·Canvas(문구·화살표)·Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_canvas != null) Object.Destroy(_canvas);
            foreach (Material m in _kindMaterials)
            {
                if (m != null) Object.Destroy(m);
            }
            if (_myWaypointMaterial != null) Object.Destroy(_myWaypointMaterial);
            if (_teamWaypointMaterial != null) Object.Destroy(_teamWaypointMaterial);
        }

        // 기능: 풀 칸 하나에 Ping 하나를 그린다: 기둥, 그리고 화면 안이면 그 위의 거리 문구, 밖이면 가장자리 화살표와 그 옆 문구.
        // 입력: slot - 풀 칸, ping - 그릴 Ping, camera - 카메라(null 가능), from - 거리 기준 위치.
        // 출력: 반환값 없음. 칸에 다른 Ping이 오면 Material·색·거리 문구를 다시 정한다.
        private void DrawPing(int slot, in MarkerPing ping, Camera camera, Vector3 from)
        {
            Vector3 at = new Vector3(ping.Position.X, ping.Position.Y, ping.Position.Z);
            Transform beam = _beams[slot];
            if (!beam.gameObject.activeSelf) beam.gameObject.SetActive(true);
            if (slot >= _beamsShown) _beamsShown = slot + 1;
            beam.position = at + new Vector3(0f, BeamHeight * 0.5f, 0f);
            if (!_slotUsed[slot] || _slotPingId[slot] != ping.Id || _slotOwner[slot] != ping.OwnerId || _slotKind[slot] != ping.Kind)
            {
                _slotUsed[slot] = true;
                _slotPingId[slot] = ping.Id;
                _slotOwner[slot] = ping.OwnerId;
                _slotKind[slot] = ping.Kind;
                int kind = (int)ping.Kind;
                _beamRenderers[slot].sharedMaterial = _kindMaterials[kind >= 0 && kind < _kindMaterials.Length ? kind : 0];
                Color color = MapColors.Of(ping.Kind);
                _labels[slot].color = color;
                _arrows[slot].color = color;
                _distances[slot].Reset();
            }
            if (_distances[slot].Update(Vector3.Distance(from, at))) _labels[slot].text = _distances[slot].Text;

            if (camera == null)
            {
                SetActive(_labels[slot].gameObject, false);
                SetActive(_arrows[slot].gameObject, false);
                return;
            }
            Vector3 screen = camera.WorldToScreenPoint(at + new Vector3(0f, LabelLift, 0f));
            bool off = ScreenEdge.ToEdge(screen.x, screen.y, screen.z < 0f, Screen.width, Screen.height, EdgeMargin,
                out float ex, out float ey, out float angle);
            SetActive(_labels[slot].gameObject, true);
            SetActive(_arrows[slot].gameObject, off);
            Vector2 label;
            if (off)
            {
                // The arrow at the edge points outwards (its texture points up = 90 degrees); the text sits just inside it.
                float rad = angle * Mathf.Deg2Rad;
                label = new Vector2(ex - Mathf.Cos(rad) * 34f, ey - Mathf.Sin(rad) * 34f);
                Move(_arrows[slot].rectTransform, ref _arrowAt[slot], new Vector2(ex, ey));
                if (!(Mathf.Abs(Mathf.DeltaAngle(angle, _arrowAngle[slot])) <= 0.5f))
                {
                    _arrowAngle[slot] = angle;
                    _arrows[slot].rectTransform.localEulerAngles = new Vector3(0f, 0f, angle - 90f);
                }
            }
            else
            {
                label = new Vector2(ex, ey);
            }
            Move(_labels[slot].rectTransform, ref _labelAt[slot], label);
        }

        // 기능: slot번째부터의 Ping 칸을 숨긴다.
        // 입력: slot - 첫 숨길 칸.
        // 출력: 반환값 없음. 숨긴 칸은 다음에 쓸 때 Material과 문구를 다시 정한다.
        private void HidePingsFrom(int slot)
        {
            for (int i = slot; i < _beamsShown; i++)
            {
                _beams[i].gameObject.SetActive(false);
                SetActive(_labels[i].gameObject, false);
                SetActive(_arrows[i].gameObject, false);
                _slotUsed[i] = false;
            }
            if (slot < _beamsShown) _beamsShown = slot;
        }

        // 기능: 화면 UI 하나를 화면 좌표로 옮긴다(0.5 px 넘게 움직였을 때만). Screen Space Overlay라 position이 화면 px이다.
        // 입력: rect - 옮길 UI, last - 마지막으로 쓴 위치(바뀌면 갱신), to - 새 화면 좌표.
        // 출력: 반환값 없음.
        private static void Move(RectTransform rect, ref Vector2 last, Vector2 to)
        {
            if (Mathf.Abs(to.x - last.x) <= 0.5f && Mathf.Abs(to.y - last.y) <= 0.5f) return;
            last = to;
            rect.position = new Vector3(to.x, to.y, 0f);
        }

        // 기능: 바뀔 때만 SetActive를 부른다.
        // 입력: go - 대상, active - 켤지.
        // 출력: 반환값 없음.
        private static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active) go.SetActive(active);
        }

        // 기능: Collider 없는 원기둥 하나를 숨긴 채 만든다(내장 원기둥 Mesh는 공유된다).
        // 입력: name - 이름, scale - 크기(원기둥 기본 높이 2 m), material - 공유 Material, renderer - 결과 Renderer.
        // 출력: 원기둥의 Transform(뿌리의 자식).
        private Transform CreateColumn(string name, Vector3 scale, Material material, out Renderer renderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            // Immediate: a deferred destroy would leave its collider for this frame's camera and aim rays.
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;
            renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        // 기능: Lit Material을 복사해 색을 입힌다.
        // 입력: source - 원본, color - 색.
        // 출력: 새 Material(Dispose가 파괴한다).
        private static Material Tinted(Material source, Color color) => new Material(source) { color = color };
    }
}
