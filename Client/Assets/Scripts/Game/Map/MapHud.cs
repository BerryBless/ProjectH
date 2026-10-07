using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game.Map
{
    // A teammate as the map draws it (filled by GameClient every frame into MapSystem's fixed buffer).
    public struct MapMate
    {
        public Vector3 Feet;
        public bool Downed;
        public string Name;   // cached in GameClient's name table (no allocation per frame)
    }

    // One frame of what the map shows besides the team markers (GameClient fills it in LateUpdate).
    public struct MapFrame
    {
        public Vector3 Center;        // the minimap window centre: whoever the camera follows (ourselves, or the watched player)
        public bool SelfVisible;      // alive and not spectating
        public Vector3 Self;          // our drawn feet
        public float Yaw;             // camera yaw (degrees, 0 = north, clockwise)
        public bool HasZone;
        public float ZoneX, ZoneZ, ZoneRadius;   // the zone now (ZoneMath.Sample)
        public bool HasNext;
        public float NextX, NextZ, NextRadius;   // the circle it shrinks to, while that is known
        public byte StationCooling;   // bit i: RebootStations.All[i] is cooling down
        public ushort MyId;
        public double ServerTick;     // estimated server tick (pings past their end tick are hidden)
    }

    // Phase 15 D3, D4, D12: the minimap (top right, 200 px, north up, 60 m around the followed player, clipped by RectMask2D)
    // and the full map (centre, 900 px, the whole map, opened with M). Both are code-made uGUI on their own canvases with no
    // GraphicRaycaster (map clicks are read from the pointer, MapSystem). Every icon is in a fixed pool made here (teammates
    // 3, pings 8, waypoints 4, POIs 5, stations 4, two zone rings, the player, the route line); per frame only positions
    // (written when they move more than half a pixel), angles, ring sizes and colours change, and the full map is skipped
    // while it is closed. Texts are set once (POI names) or when the string reference changes (teammate names). Dispose
    // destroys both canvases; the textures belong to MapTextures.
    public sealed class MapHud : System.IDisposable
    {
        public const float MiniSize = 200f;
        public const float MiniMargin = 20f;
        public const float WindowMeters = 60f;
        public const float FullSize = 900f;

        private readonly GameObject _miniCanvas;
        private readonly GameObject _fullCanvas;
        private readonly RawImage _miniMap;
        private readonly RectTransform _fullMapRect;
        private readonly MapLayer _mini;
        private readonly MapLayer _full;
        private readonly MapIcon _route;
        private Rect _uvRect = new Rect(float.NaN, 0f, 0f, 0f);
        private bool _miniVisible;
        private bool _fullOpen;
        private bool _hasRoute;

        // QA (D14): what the maps drew in the last Draw. -1 = not drawn.
        public float SelfU { get; private set; } = -1f;
        public float SelfV { get; private set; } = -1f;
        public float ZoneRadiusU { get; private set; } = -1f;
        public float ZoneCenterU { get; private set; } = -1f;
        public float ZoneCenterV { get; private set; } = -1f;
        public int TeammatesDrawn => _miniVisible ? _mini.MatesDrawn : 0;
        public int PingsDrawn => _miniVisible ? _mini.PingsDrawn : 0;
        public int WaypointsDrawn => _miniVisible ? _mini.WaypointsDrawn : 0;

        // 기능: 미니맵과 전체 지도 Canvas, 모든 아이콘 풀을 만든다(모두 숨긴 채).
        // 입력: textures - 지도 그림과 아이콘 텍스처(이 객체보다 오래 산다).
        // 출력: 숨겨진 MapHud(Dispose가 Canvas를 파괴한다).
        public MapHud(MapTextures textures)
        {
            // Minimap: above the squad HUD (95), under the crosshair (100).
            _miniCanvas = UiFactory.CreateCanvas("Minimap", 96, interactive: false);
            Vector2 topRight = new Vector2(1f, 1f);
            RectTransform frame = UiFactory.CreateRect("Frame", _miniCanvas.transform, topRight, new Vector2(-MiniMargin + 2f, -MiniMargin + 2f),
                new Vector2(MiniSize + 4f, MiniSize + 4f));
            var frameImage = frame.gameObject.AddComponent<Image>();
            frameImage.color = new Color(0f, 0f, 0f, 0.6f);
            frameImage.raycastTarget = false;
            RectTransform view = UiFactory.CreateRect("View", _miniCanvas.transform, topRight, new Vector2(-MiniMargin, -MiniMargin),
                new Vector2(MiniSize, MiniSize));
            view.gameObject.AddComponent<RectMask2D>();
            _miniMap = CreateMapImage(view, textures.Map);
            _mini = new MapLayer(view, MiniSize, textures, mini: true);
            _miniCanvas.SetActive(false);

            // Full map: over every HUD and the crosshair, under the screens (110: Esc menu, result).
            _fullCanvas = UiFactory.CreateCanvas("FullMap", 105, interactive: false);
            RectTransform dim = UiFactory.CreateScreen("Dim", _fullCanvas.transform, dim: true);
            dim.GetComponent<Image>().raycastTarget = false;
            RectTransform fullFrame = UiFactory.CreatePanel("Frame", _fullCanvas.transform, new Vector2(FullSize + 8f, FullSize + 8f));
            fullFrame.anchoredPosition = new Vector2(0f, 10f);
            _fullMapRect = UiFactory.CreateRect("Map", _fullCanvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(FullSize, FullSize));
            _fullMapRect.gameObject.AddComponent<RectMask2D>();
            RawImage fullImage = CreateMapImage(_fullMapRect, textures.Map);
            fullImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            // The route line under every icon (created first: drawing order).
            _route = new MapIcon("Route", _fullMapRect, null, 3f, MapColors.Route);
            _full = new MapLayer(_fullMapRect, FullSize, textures, mini: false);
            UiFactory.CreateText("Hint", _fullCanvas.transform, UiText.MapHint, 20, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f),
                new Vector2(0f, -FullSize * 0.5f - 18f), new Vector2(FullSize, 28f));
            _fullCanvas.SetActive(false);
        }

        public bool FullOpen => _fullOpen;

        // 기능: 미니맵을 보이거나 숨긴다(바뀔 때만). 숨기면 QA 값도 "그리지 않음"이 된다.
        // 입력: visible - 보일지.
        // 출력: 반환값 없음.
        public void SetVisible(bool visible)
        {
            if (_miniCanvas == null || visible == _miniVisible) return;
            _miniVisible = visible;
            _miniCanvas.SetActive(visible);
            if (visible) return;
            SelfU = SelfV = -1f;
        }

        // 기능: 전체 지도를 열거나 닫는다(UiFlow.MapOpen을 따른다).
        // 입력: open - 열지.
        // 출력: 반환값 없음. 닫으면 다음에 열 때까지 전체 지도 갱신을 건너뛴다.
        public void SetFullOpen(bool open)
        {
            if (_fullCanvas == null || open == _fullOpen) return;
            _fullOpen = open;
            _fullCanvas.SetActive(open);
            if (open) return;
            ZoneRadiusU = ZoneCenterU = ZoneCenterV = -1f;
        }

        // 기능: 전체 지도의 수송기 경로 선을 경로가 바뀔 때 한 번 계산한다(D12).
        // 입력: route - 받은 경로.
        // 출력: 반환값 없음. 선의 위치·길이·각도가 정해진다.
        public void SetRoute(in DropRoute route)
        {
            if (_fullCanvas == null) return;
            MapProjection.WorldToUv(route.StartX, route.StartZ, out float u0, out float v0);
            MapProjection.WorldToUv(route.EndX, route.EndZ, out float u1, out float v1);
            float x0 = u0 * FullSize, y0 = v0 * FullSize, x1 = u1 * FullSize, y1 = v1 * FullSize;
            float dx = x1 - x0, dy = y1 - y0;
            _route.SetSize(Mathf.Sqrt(dx * dx + dy * dy), 3f);
            _route.SetAngle(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
            _route.Place((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
            _hasRoute = true;
        }

        // 기능: 경로 선을 숨긴다(대기실, 끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void ClearRoute()
        {
            if (_fullCanvas == null) return;
            _hasRoute = false;
            _route.Show(false);
        }

        // 기능: 한 프레임을 그린다: 미니맵 창(uvRect)과 아이콘, 열려 있으면 전체 지도 아이콘. 할당 없음.
        // 입력: f - 이번 프레임 값, mates·mateCount - 그릴 팀원, markers - 팀 Ping·Waypoint.
        // 출력: 반환값 없음. QA 값(SelfU/V, 자기장, 그린 수)이 갱신된다.
        public void Draw(in MapFrame f, MapMate[] mates, int mateCount, TeamMarkerState markers)
        {
            if (_miniCanvas == null || !_miniVisible) return;
            MapProjection.Window(f.Center.x, f.Center.z, WindowMeters, out float minU, out float minV, out float sizeUv);
            // Half a minimap pixel of movement before the uvRect (and the RawImage's mesh) is rewritten.
            float halfPixel = sizeUv * 0.5f / MiniSize;
            if (!(Mathf.Abs(minU - _uvRect.x) <= halfPixel && Mathf.Abs(minV - _uvRect.y) <= halfPixel && Mathf.Abs(sizeUv - _uvRect.width) <= halfPixel))
            {
                _uvRect = new Rect(minU, minV, sizeUv, sizeUv);
                _miniMap.uvRect = _uvRect;
            }
            SelfU = minU + sizeUv * 0.5f;
            SelfV = minV + sizeUv * 0.5f;
            _mini.Draw(f, minU, minV, sizeUv, mates, mateCount, markers);

            if (!_fullOpen) return;
            _full.Draw(f, 0f, 0f, 1f, mates, mateCount, markers);
            _route.Show(_hasRoute);
            if (f.HasZone)
            {
                MapProjection.WorldToUv(f.ZoneX, f.ZoneZ, out float zu, out float zv);
                ZoneCenterU = zu;
                ZoneCenterV = zv;
                ZoneRadiusU = MapProjection.MetersToUv(f.ZoneRadius);
            }
            else
            {
                ZoneRadiusU = ZoneCenterU = ZoneCenterV = -1f;
            }
        }

        // 기능: 아이콘을 모두 숨긴다(경기 상태를 비울 때). 지도 그림과 POI는 남는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideMarkers()
        {
            if (_miniCanvas == null) return;
            _mini.HideDynamic();
            _full.HideDynamic();
        }

        // 기능: 화면 좌표가 전체 지도의 어느 지도 정규 좌표인지 구한다(지도 클릭 Waypoint).
        // 입력: screen - 포인터 화면 좌표(px, 왼쪽 아래 0), u·v - 결과.
        // 출력: 전체 지도가 열려 있고 그 점이 지도 사각형 안이면 true와 u, v. 아니면 false.
        public bool ScreenToUv(Vector2 screen, out float u, out float v)
        {
            u = v = 0f;
            if (_fullCanvas == null || !_fullOpen) return false;
            // Screen Space Overlay: no camera.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_fullMapRect, screen, null, out Vector2 local)) return false;
            Rect r = _fullMapRect.rect;
            MapProjection.RectToUv(local.x - r.xMin, local.y - r.yMin, r.width, r.height, out u, out v);
            return MapProjection.InsideUv(u, v);
        }

        // 기능: 두 Canvas를 파괴한다(아이콘은 자식이라 함께 사라진다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_miniCanvas != null) Object.Destroy(_miniCanvas);
            if (_fullCanvas != null) Object.Destroy(_fullCanvas);
        }

        // 기능: 부모를 가득 채우는 지도 그림 RawImage를 만든다.
        // 입력: parent - 지도 사각형, texture - 지도 그림.
        // 출력: 만든 RawImage.
        private static RawImage CreateMapImage(RectTransform parent, Texture texture)
        {
            var go = new GameObject("Picture", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = go.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }
    }

    // The icons of one map (minimap or full map) over a square rectangle of Size px, and how a world point lands on it.
    internal sealed class MapLayer
    {
        private const float SelfIcon = 20f;
        private const float MateIcon = 12f;
        private const float PingIcon = 16f;
        private const float WaypointIcon = 12f;
        private const float PoiIcon = 7f;
        private const float StationIcon = 10f;
        private const int Mates = SquadConstants.MaxTeamSize - 1;

        private readonly bool _mini;
        private readonly float _size;
        private readonly MapIcon _zoneNow;
        private readonly MapIcon _zoneNext;
        private readonly MapIcon[] _pois = new MapIcon[5];
        private readonly MapLabel[] _poiNames;
        private readonly MapIcon[] _stations = new MapIcon[RebootStations.Count];
        private readonly MapIcon[] _waypoints = new MapIcon[MapMarkerConstants.MaxWaypoints];
        private readonly MapIcon[] _pings = new MapIcon[MapMarkerConstants.MaxTeamPings];
        private readonly MapIcon[] _mates = new MapIcon[Mates];
        private readonly MapLabel[] _mateNames;
        private readonly MapIcon _self;
        private float _minU;
        private float _minV;
        private float _sizeUv = 1f;

        public int MatesDrawn { get; private set; }
        public int PingsDrawn { get; private set; }
        public int WaypointsDrawn { get; private set; }

        // 기능: 한 지도의 아이콘 풀을 만든다(그리기 순서: 고리, POI, 스테이션, Waypoint, Ping, 팀원, 나).
        // 입력: parent - 지도 사각형, size - 한 변(px), textures - 아이콘 텍스처, mini - 미니맵이면 true(전체 지도는 POI·팀원 이름 포함).
        // 출력: 숨겨진 아이콘을 가진 층. 전체 지도의 POI·스테이션은 위치가 변하지 않으므로 여기서 한 번 둔다.
        public MapLayer(RectTransform parent, float size, MapTextures textures, bool mini)
        {
            _mini = mini;
            _size = size;
            _zoneNext = new MapIcon("ZoneNext", parent, textures.Ring, 0f, MapColors.ZoneNext);
            _zoneNow = new MapIcon("ZoneNow", parent, textures.Ring, 0f, MapColors.ZoneNow);
            int poiCount = System.Math.Min(_pois.Length, MapPois.All.Length);
            if (!mini) _poiNames = new MapLabel[_pois.Length];
            for (int i = 0; i < poiCount; i++)
            {
                _pois[i] = new MapIcon("Poi" + i, parent, textures.Dot, mini ? PoiIcon : PoiIcon * 1.5f, MapColors.Poi);
                if (!mini) _poiNames[i] = new MapLabel("PoiName" + i, parent, 20, MapColors.Poi, new Vector2(200f, 26f));
            }
            for (int i = 0; i < _stations.Length; i++)
            {
                _stations[i] = new MapIcon("Station" + i, parent, null, mini ? StationIcon : StationIcon * 1.4f, MapColors.Station);
                _stations[i].SetAngle(45f);
            }
            for (int i = 0; i < _waypoints.Length; i++)
            {
                _waypoints[i] = new MapIcon("Waypoint" + i, parent, null, mini ? WaypointIcon : WaypointIcon * 1.4f, MapColors.TeamWaypoint);
                _waypoints[i].SetAngle(45f);
            }
            for (int i = 0; i < _pings.Length; i++) _pings[i] = new MapIcon("Ping" + i, parent, textures.Dot, mini ? PingIcon : PingIcon * 1.3f, MapColors.Location);
            if (!mini) _mateNames = new MapLabel[Mates];
            for (int i = 0; i < Mates; i++)
            {
                _mates[i] = new MapIcon("Mate" + i, parent, textures.Dot, mini ? MateIcon : MateIcon * 1.4f, MapColors.Teammate);
                if (!mini) _mateNames[i] = new MapLabel("MateName" + i, parent, 18, MapColors.Teammate, new Vector2(240f, 24f));
            }
            _self = new MapIcon("Self", parent, textures.Arrow, mini ? SelfIcon : SelfIcon * 1.3f, MapColors.Self);

            if (mini) return;
            // The full map never moves: the POIs (with names) and the stations sit where they are once.
            for (int i = 0; i < poiCount; i++)
            {
                MapPoi poi = MapPois.All[i];
                ToPx(poi.X, poi.Z, out float px, out float py);
                _pois[i].Place(px, py);
                _poiNames[i].Set(poi.Name, px, py + 18f);
            }
            for (int i = 0; i < _stations.Length; i++)
            {
                System.Numerics.Vector3 s = RebootStations.All[i];
                ToPx(s.X, s.Z, out float px, out float py);
                _stations[i].Place(px, py);
            }
        }

        // 기능: 이 지도에 한 프레임을 그린다. 미니맵은 창 밖 팀원·Ping·Waypoint를 테두리에 붙이고, 창 밖 POI·스테이션은 숨긴다.
        // 입력: f - 프레임 값, minU·minV·sizeUv - 보이는 창(전체 지도는 0, 0, 1), mates·mateCount - 팀원, markers - 팀 표시.
        // 출력: 반환값 없음. 그린 팀원·Ping·Waypoint 수가 갱신된다. 할당 없음.
        public void Draw(in MapFrame f, float minU, float minV, float sizeUv, MapMate[] mates, int mateCount, TeamMarkerState markers)
        {
            _minU = minU;
            _minV = minV;
            _sizeUv = sizeUv;

            DrawRing(_zoneNow, f.HasZone, f.ZoneX, f.ZoneZ, f.ZoneRadius);
            DrawRing(_zoneNext, f.HasNext, f.NextX, f.NextZ, f.NextRadius);

            for (int i = 0; i < _stations.Length; i++)
            {
                if (_mini)
                {
                    System.Numerics.Vector3 s = RebootStations.All[i];
                    if (ToPx(s.X, s.Z, out float px, out float py)) _stations[i].Place(px, py);
                    else _stations[i].Show(false);
                }
                _stations[i].SetColor((f.StationCooling & (1 << i)) != 0 ? MapColors.StationCooling : MapColors.Station);
            }
            if (_mini)
            {
                for (int i = 0; i < _pois.Length; i++)
                {
                    if (_pois[i] == null) continue;
                    MapPoi poi = MapPois.All[i];
                    if (ToPx(poi.X, poi.Z, out float px, out float py)) _pois[i].Place(px, py);
                    else _pois[i].Show(false);
                }
            }

            int shown = 0;
            for (int i = 0; i < markers.WaypointCount && shown < _waypoints.Length; i++)
            {
                MarkerWaypoint w = markers.Waypoint(i);
                MapIcon icon = _waypoints[shown++];
                ToEdgePx(w.Position.X, w.Position.Z, WaypointIcon, out float px, out float py);
                icon.Place(px, py);
                icon.SetColor(w.OwnerId == f.MyId ? MapColors.MyWaypoint : MapColors.TeamWaypoint);
            }
            WaypointsDrawn = shown;
            for (int i = shown; i < _waypoints.Length; i++) _waypoints[i].Show(false);

            shown = 0;
            for (int i = 0; i < markers.PingCount && shown < _pings.Length; i++)
            {
                if (!markers.IsLive(i, f.ServerTick)) continue;
                MarkerPing p = markers.Ping(i);
                MapIcon icon = _pings[shown++];
                ToEdgePx(p.Position.X, p.Position.Z, PingIcon, out float px, out float py);
                icon.Place(px, py);
                icon.SetColor(MapColors.Of(p.Kind));
            }
            PingsDrawn = shown;
            for (int i = shown; i < _pings.Length; i++) _pings[i].Show(false);

            shown = 0;
            for (int i = 0; i < mateCount && shown < _mates.Length; i++)
            {
                MapIcon icon = _mates[shown];
                ToEdgePx(mates[i].Feet.x, mates[i].Feet.z, MateIcon, out float px, out float py);
                icon.Place(px, py);
                icon.SetColor(mates[i].Downed ? MapColors.Downed : MapColors.Teammate);
                if (_mateNames != null) _mateNames[shown].Set(mates[i].Name, px, py + 18f);
                shown++;
            }
            MatesDrawn = shown;
            for (int i = shown; i < _mates.Length; i++)
            {
                _mates[i].Show(false);
                if (_mateNames != null) _mateNames[i].Hide();
            }

            if (f.SelfVisible)
            {
                ToEdgePx(f.Self.x, f.Self.z, SelfIcon, out float px, out float py);
                _self.Place(px, py);
                _self.SetAngle(-f.Yaw);
            }
            else
            {
                _self.Show(false);
            }
        }

        // 기능: 매 프레임 바뀌는 아이콘(고리, Ping, Waypoint, 팀원, 나)을 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. 그린 수가 0이 된다.
        public void HideDynamic()
        {
            _zoneNow.Show(false);
            _zoneNext.Show(false);
            foreach (MapIcon icon in _waypoints) icon.Show(false);
            foreach (MapIcon icon in _pings) icon.Show(false);
            for (int i = 0; i < _mates.Length; i++)
            {
                _mates[i].Show(false);
                if (_mateNames != null) _mateNames[i].Hide();
            }
            _self.Show(false);
            MatesDrawn = PingsDrawn = WaypointsDrawn = 0;
        }

        // 기능: 자기장 고리 하나를 원 텍스처의 크기만 바꿔 그린다(D12).
        // 입력: ring - 고리 아이콘, has - 그릴지, x·z - 중심(월드), radius - 반지름(m).
        // 출력: 반환값 없음. 반지름이 0 이하이거나 has가 false면 숨긴다.
        private void DrawRing(MapIcon ring, bool has, float x, float z, float radius)
        {
            if (!has || !(radius > 0f))
            {
                ring.Show(false);
                return;
            }
            MapProjection.WorldToUv(x, z, out float u, out float v);
            MapProjection.UvToWindow(u, v, _minU, _minV, _sizeUv, out float wx, out float wy);
            float radiusPx = MapProjection.MetersToUv(radius) / _sizeUv * _size;
            ring.SetSize(MapTextures.RingDiameter(radiusPx));
            ring.Place(wx * _size, wy * _size);
        }

        // 기능: 월드 점의 픽셀 위치를 구한다(창 밖이면 보이지 않음).
        // 입력: x, z - 월드 좌표, px·py - 결과(왼쪽 아래 기준 px).
        // 출력: 창 안이면 true.
        private bool ToPx(float x, float z, out float px, out float py)
        {
            MapProjection.WorldToUv(x, z, out float u, out float v);
            MapProjection.UvToWindow(u, v, _minU, _minV, _sizeUv, out float wx, out float wy);
            px = wx * _size;
            py = wy * _size;
            return wx >= 0f && wx <= 1f && wy >= 0f && wy <= 1f;
        }

        // 기능: 월드 점의 픽셀 위치를 구하고, 창 밖이면 테두리(아이콘 반 크기 안쪽)에 붙인다(D3).
        // 입력: x, z - 월드 좌표, icon - 아이콘 한 변(px), px·py - 결과.
        // 출력: 반환값 없음.
        private void ToEdgePx(float x, float z, float icon, out float px, out float py)
        {
            MapProjection.WorldToUv(x, z, out float u, out float v);
            MapProjection.UvToWindow(u, v, _minU, _minV, _sizeUv, out float wx, out float wy);
            MapProjection.ClampToEdge(ref wx, ref wy, icon * 0.5f / _size);
            px = wx * _size;
            py = wy * _size;
        }
    }
}
