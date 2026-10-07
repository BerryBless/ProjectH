using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game.Map
{
    // Phase 15: the map side of the client in one place, so GameClient only calls in: the shared textures, the minimap and
    // full map (MapHud), the world markers (WorldMarkers), our team's markers as the server sent them (TeamMarkerState), the
    // middle-button tap rule (PingTap) and the teammate buffer of this frame. Requests it decides on (a ping, a map-click
    // waypoint) are returned to GameClient, which sends them. Created in GameClient.Awake, disposed in its OnDestroy (before
    // UiFont.Release: the HUD texts use the font).
    public sealed class MapSystem : System.IDisposable
    {
        private readonly MapTextures _textures;
        private readonly MapHud _hud;
        private readonly WorldMarkers _world;
        private readonly PingTap _tap = new PingTap();
        private readonly MapMate[] _mates = new MapMate[SquadConstants.MaxTeamSize - 1];
        private int _mateCount;

        public TeamMarkerState Markers { get; } = new TeamMarkerState();
        public bool FullOpen => _hud.FullOpen;
        public bool PingPending => _tap.Pending;

        // 기능: 텍스처, 지도 HUD, 월드 표지를 만든다(모두 숨긴 채).
        // 입력: source - 표지 Material의 원본(LitMaterial로 고른 것).
        // 출력: 숨겨진 MapSystem(Dispose가 모두 해제한다).
        public MapSystem(Material source)
        {
            _textures = new MapTextures();
            _hud = new MapHud(_textures);
            _world = new WorldMarkers(source, _textures);
        }

        // 기능: 받은 TeamMarkers 목록으로 팀 표시를 통째로 바꾼다(D10).
        // 입력: pings·pingCount, waypoints·waypointCount - NetClient가 읽은 목록(호출 동안만 유효, 복사한다).
        // 출력: 반환값 없음.
        public void ApplyMarkers(MarkerPing[] pings, int pingCount, MarkerWaypoint[] waypoints, int waypointCount) =>
            Markers.Apply(pings, pingCount, waypoints, waypointCount);

        // 기능: 팀 표시와 기다리던 Ping을 비운다(새 라운드 카운트다운, 끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void ClearMarkers()
        {
            Markers.Clear();
            _tap.Cancel();
        }

        // 기능: 전체 지도의 수송기 경로 선을 정한다(경로가 올 때 한 번).
        // 입력: route - 받은 경로.
        // 출력: 반환값 없음.
        public void SetRoute(in DropRoute route) => _hud.SetRoute(route);

        // 기능: 경로 선을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void ClearRoute() => _hud.ClearRoute();

        // 기능: 전체 지도를 열거나 닫는다(UiFlow.MapOpen). 열 때 기다리던 Ping은 버린다(지도가 열리면 Ping할 수 없다).
        // 입력: open - 열지.
        // 출력: 반환값 없음.
        public void SetFullOpen(bool open)
        {
            if (open) _tap.Cancel();
            _hud.SetFullOpen(open);
        }

        // 기능: 이번 프레임의 팀원 목록을 비운다(AddMate 전에).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void BeginMates() => _mateCount = 0;

        // 기능: 이번 프레임에 그릴 팀원 하나를 더한다(고정 칸이 차면 무시).
        // 입력: feet - 그려지는 발, downed - 기절, name - 이름(캐시된 문자열 또는 null).
        // 출력: 반환값 없음.
        public void AddMate(Vector3 feet, bool downed, string name)
        {
            if (_mateCount >= _mates.Length) return;
            _mates[_mateCount++] = new MapMate { Feet = feet, Downed = downed, Name = name };
        }

        // 기능: 지도와 월드 표지를 그린다(매 프레임, 참가해 등장한 뒤).
        // 입력: f - 이번 프레임 값, camera - 화면 투영 카메라, from - 거리 기준 위치, loot - Supply Drop 목록(Phase 16 지도 아이콘).
        // 출력: 반환값 없음. 할당 없음.
        public void Draw(in MapFrame f, Camera camera, Vector3 from, LootState loot)
        {
            _hud.SetVisible(true);
            _hud.Draw(f, _mates, _mateCount, Markers, loot);
            _world.Draw(camera, from, Markers, f.ServerTick, f.MyId);
        }

        // 기능: 지도 아이콘과 월드 표지를 숨긴다(경기 상태를 비울 때). 미니맵도 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            _hud.HideMarkers();
            _hud.SetVisible(false);
            _world.HideAll();
        }

        // 기능: 가운데 버튼 Ping 한 프레임(D6): 기다리던 누름의 0.3초가 지났으면 맥락 Ping을, 그 안의 두 번째 누름이면 Danger를 낸다.
        // 입력: now - 지금 시각(초), allowed - Ping할 수 있음(살아 있음(기절 포함) + 경기 참가 + 지도·화면 닫힘 + 커서 잠김; 아니면
        //   기다리던 누름도 버린다), pressed - 이번 프레임 가운데 버튼 누름, enemyId - 누름 때 조준이 맞힌 적 Entity id(아니면 0),
        //   hit - 조준이 무언가를 맞혔는지, hitPoint - 맞은 점, items - 월드 아이템 목록, send - 결과.
        // 출력: 보낼 요청이 있으면 true와 그 요청(한 프레임에 최대 하나), 없으면 false.
        public bool UpdatePing(float now, bool allowed, bool pressed, ushort enemyId, bool hit, Vector3 hitPoint, WorldItemList items,
            out MapMarker send)
        {
            if (!allowed)
            {
                _tap.Cancel();
                send = default;
                return false;
            }
            bool ready = _tap.Update(now, out send);
            if (!pressed) return ready;
            MapMarker context = default;
            bool has = hit && PingContext.Choose(enemyId, new System.Numerics.Vector3(hitPoint.x, hitPoint.y, hitPoint.z), items, out context);
            // Update just cleared any pending press, so a press in the same frame only starts waiting (never a second send).
            if (_tap.Press(now, has, context, out MapMarker danger))
            {
                send = danger;
                return true;
            }
            return ready;
        }

        // 기능: 기다리던 Ping을 버린다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void CancelPing() => _tap.Cancel();

        // 기능: 열린 전체 지도의 클릭을 Waypoint 요청으로 바꾼다(D4): 왼쪽 = 그 지점에 내 Waypoint(높이는 지형 높이), 오른쪽 = 지우기.
        // 입력: pointer - 포인터 화면 좌표, left·right - 이번 프레임 왼쪽·오른쪽 클릭, myId - 내 Entity id(지울 Waypoint가 있는지 본다),
        //   send - 결과.
        // 출력: 보낼 요청이 있으면 true. 지도 밖 왼쪽 클릭, 내 Waypoint가 없을 때의 오른쪽 클릭은 false.
        public bool TryClick(Vector2 pointer, bool left, bool right, ushort myId, out MapMarker send)
        {
            send = default;
            if (!_hud.FullOpen) return false;
            if (left && _hud.ScreenToUv(pointer, out float u, out float v))
            {
                MapProjection.UvToWorld(u, v, out float x, out float z);
                send = new MapMarker
                {
                    Kind = MapMarkerKind.WaypointSet,
                    Position = new System.Numerics.Vector3(x, GameMap.Terrain.Height(x, z), z),
                    TargetId = 0,
                };
                return true;
            }
            if (!right || !HasWaypointOf(myId)) return false;
            send = new MapMarker { Kind = MapMarkerKind.WaypointClear, Position = default, TargetId = 0 };
            return true;
        }

        // ---- QA (D14): what the maps drew ----
        public float QaSelfU => _hud.SelfU;
        public float QaSelfV => _hud.SelfV;
        public float QaZoneRadiusU => _hud.ZoneRadiusU;
        public float QaZoneCenterU => _hud.ZoneCenterU;
        public float QaZoneCenterV => _hud.ZoneCenterV;
        public int QaTeammates => _hud.TeammatesDrawn;
        public int QaPings => _hud.PingsDrawn;
        public int QaWaypoints => _hud.WaypointsDrawn;
        public int QaSupplyDrops => _hud.SupplyDropsDrawn;

        // 기능: 월드 표지, 지도 HUD, 텍스처를 해제한다(텍스처를 쓰는 것부터).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            _world.Dispose();
            _hud.Dispose();
            _textures.Dispose();
        }

        // 기능: 이 플레이어의 Waypoint가 팀 표시에 있는지 본다.
        // 입력: ownerId - Entity id.
        // 출력: 있으면 true.
        private bool HasWaypointOf(ushort ownerId)
        {
            if (ownerId == 0) return false;
            for (int i = 0; i < Markers.WaypointCount; i++)
            {
                if (Markers.Waypoint(i).OwnerId == ownerId) return true;
            }
            return false;
        }
    }
}
