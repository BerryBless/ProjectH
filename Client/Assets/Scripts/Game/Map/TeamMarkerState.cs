using System;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D10: our team's pings and waypoints as the newest TeamMarkers said. Every packet replaces the whole state
    // (idempotent, order does not matter); no prediction (the server decides every marker). Fixed arrays of the packet's
    // limits, so it never grows. Cleared at a new round's countdown and with the match state (disconnect), like the
    // transport route. Pure (no UnityEngine).
    public sealed class TeamMarkerState
    {
        private readonly MarkerPing[] _pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        private readonly MarkerWaypoint[] _waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];

        public int PingCount { get; private set; }
        public int WaypointCount { get; private set; }
        // Counts changes (callers redraw only when it moved).
        public int Version { get; private set; }

        // 기능: index번째 Ping을 돌려준다.
        // 입력: index - 0..PingCount-1.
        // 출력: 그 Ping.
        public MarkerPing Ping(int index) => _pings[index];

        // 기능: index번째 Waypoint를 돌려준다.
        // 입력: index - 0..WaypointCount-1.
        // 출력: 그 Waypoint.
        public MarkerWaypoint Waypoint(int index) => _waypoints[index];

        // 기능: 받은 TeamMarkers 목록으로 상태를 통째로 바꾼다(검증은 TeamMarkersPacket.TryRead가 했다).
        // 입력: pings·pingCount - 팀의 활성 Ping, waypoints·waypointCount - 팀원 Waypoint(수는 배열 길이와 상한으로 자른다).
        // 출력: 반환값 없음. 상태가 바뀌고 Version이 오른다. 할당 없음.
        public void Apply(MarkerPing[] pings, int pingCount, MarkerWaypoint[] waypoints, int waypointCount)
        {
            int pc = Math.Max(0, Math.Min(Math.Min(pingCount, _pings.Length), pings != null ? pings.Length : 0));
            int wc = Math.Max(0, Math.Min(Math.Min(waypointCount, _waypoints.Length), waypoints != null ? waypoints.Length : 0));
            for (int i = 0; i < pc; i++) _pings[i] = pings[i];
            for (int i = pc; i < PingCount; i++) _pings[i] = default;
            for (int i = 0; i < wc; i++) _waypoints[i] = waypoints[i];
            for (int i = wc; i < WaypointCount; i++) _waypoints[i] = default;
            PingCount = pc;
            WaypointCount = wc;
            Version++;
        }

        // 기능: 모두 비운다(새 라운드 카운트다운, 끊김).
        // 입력: 없음.
        // 출력: 반환값 없음. 비울 것이 있었으면 Version이 오른다.
        public void Clear()
        {
            if (PingCount == 0 && WaypointCount == 0) return;
            Array.Clear(_pings, 0, _pings.Length);
            Array.Clear(_waypoints, 0, _waypoints.Length);
            PingCount = 0;
            WaypointCount = 0;
            Version++;
        }

        // 기능: Ping이 아직 살아 있는지 본다(서버가 끝 Tick에 지우고 새 목록을 보내지만, 그 전에 먼저 숨겨도 된다).
        // 입력: index - 0..PingCount-1, serverTick - 추정 서버 Tick(0 이하면 시계가 없으므로 살아 있다고 본다).
        // 출력: 끝 Tick 전이면 true.
        public bool IsLive(int index, double serverTick) => serverTick <= 0 || serverTick < _pings[index].EndTick;
    }
}
