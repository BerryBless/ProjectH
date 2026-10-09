using System;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Map;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 15: team pings and waypoints (Docs/Map.md, spec D5-D10). Same rules as the rest of Match: game loop thread only, no
// lock, no allocation per request or per tick (fixed arrays sized at construction).
public sealed partial class Match
{
    // D9: one active team ping and the order it was placed in (the oldest is replaced first).
    private struct TeamPing
    {
        public bool Active;
        public ulong Order;
        public MarkerPing Wire;
    }

    private readonly MapCatalog _map;
    // D9: every team's pings in one fixed array: team t owns slots [t * MaxTeamPings, t * MaxTeamPings + PingsPerTeam).
    // A slot is freed when its ping expires, when its owner leaves, and at the match start, the match end and the round
    // reset, so nothing grows; at most 256 x 8 entries.
    private readonly TeamPing[] _pings = new TeamPing[TeamSlots * MapMarkerConstants.MaxTeamPings];
    private int _activePings;
    private ulong _pingOrder;
    // D10: each team's serial number for its next ping (1-255, wrapping), and the teams whose markers changed this tick
    // (TeamMarkers at the end of the tick). Index = TeamId.
    private readonly byte[] _lastPingId = new byte[TeamSlots];
    private readonly bool[] _markersDirty = new bool[TeamSlots];
    private bool _anyMarkersDirty;
    // The TeamMarkers being written (one team at a time).
    private readonly MarkerPing[] _pingScratch = new MarkerPing[MapMarkerConstants.MaxTeamPings];
    private readonly MarkerWaypoint[] _waypointScratch = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];

    // Phase 15 counters since this match object was made (the Health line and the Meter).
    public long MarkerPings { get; private set; }
    public long EnemyPingsConfirmed { get; private set; }
    public long EnemyPingsDemoted { get; private set; }
    public long MarkersRefused { get; private set; }
    public long PingsReplaced { get; private set; }
    public long PingsExpired { get; private set; }
    public long WaypointChanges { get; private set; }
    public long MarkerPackets { get; private set; }

    // 기능: Health 줄과 Meter용 지도 표시 수치(GameLoop가 매 Tick 복사한다).
    // 입력: 없음.
    // 출력: MapCounts 값.
    public Diagnostics.MapCounts MapCounts() => new(MarkerPings, EnemyPingsConfirmed, EnemyPingsDemoted, MarkersRefused, PingsReplaced, PingsExpired,
        WaypointChanges, MarkerPackets);

    // Test seams.
    internal MapCatalog MapData => _map;
    internal int ActivePings => _activePings;

    // 기능: 지도 표시 요청 하나를 검증하고 반영한다(D7, D8). 바뀐 팀은 이 Tick 끝에 TeamMarkers를 받는다. 받아들이지 않는 요청은 답 없이 버린다.
    //   공통: 경기 중(또는 개발 모드), 팀이 있는 참가자(개발 모드는 참가자가 아니어도 되지만 팀은 있어야 한다), 살아 있음(기절 포함, ActionsAllowed는 보지 않는다 — 기절도
    //   Ping한다), 좌표가 맵 안. Waypoint 지우기는 탈락한 팀원도 할 수 있다. 높이는 [지형 - 1, 지형 + 건설 높이 + 2]로 자른다.
    //   Enemy는 다른 팀의 살아 있는 플레이어가 사거리·시선 안이면 그 순간 대상 발 위치, 아니면 보낸 좌표의 Location이 된다.
    //   Item은 아이템이 있고(다른 팀의 카드는 없는 것으로 본다) 사거리 안이면 아이템 위치, 아니면 버린다.
    // 입력: peerId - 보낸 연결, marker - 파싱된 요청(형식은 MapMarker.TryRead가 이미 확인했다).
    // 출력: 반환값 없음. Ping이 추가(오래된 것 교체)되거나 Waypoint가 바뀌고, 그 팀이 Dirty가 된다.
    public void HandleMarker(int peerId, in MapMarker marker)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        bool clear = marker.Kind == MapMarkerKind.WaypointClear;
        if (!_flow.DamageAllowed || player.TeamId == 0 || !(_flow.DevRespawn || player.Participant) || !(player.Alive || clear))
        {
            MarkersRefused++;
            return;
        }
        if (clear)
        {
            if (!player.HasWaypoint) return;
            player.HasWaypoint = false;
            WaypointChanges++;
            MarkDirty(player.TeamId);
            return;
        }
        Vector3 at = marker.Position;
        if (!InMap(at))
        {
            MarkersRefused++;
            return;
        }
        at.Y = ClampMarkerHeight(at);
        switch (marker.Kind)
        {
            case MapMarkerKind.WaypointSet:
                player.HasWaypoint = true;
                player.Waypoint = at;
                WaypointChanges++;
                MarkDirty(player.TeamId);
                break;
            case MapMarkerKind.Enemy:
                if (ConfirmEnemy(player, marker.TargetId, out Vector3 enemyAt))
                {
                    EnemyPingsConfirmed++;
                    AddPing(player, MapMarkerKind.Enemy, enemyAt, marker.TargetId, _map.EnemyPingTicks);
                }
                else
                {
                    // D8: the client's context is not trusted. The sent point (not the target's) becomes a Location ping.
                    EnemyPingsDemoted++;
                    AddPing(player, MapMarkerKind.Location, at, 0, _map.PingTicks);
                }
                break;
            case MapMarkerKind.Item:
                if (!FindPingItem(player, marker.TargetId, out Vector3 itemAt))
                {
                    MarkersRefused++;
                    return;
                }
                AddPing(player, MapMarkerKind.Item, itemAt, marker.TargetId, _map.PingTicks);
                break;
            default:   // Location, Danger
                AddPing(player, marker.Kind, at, 0, _map.PingTicks);
                break;
        }
    }

    // 기능: 수평 좌표가 맵 안(|x|, |z| ≤ HalfSize)인지 본다. TeamMarkers를 읽는 쪽도 이 범위 밖이면 패킷 전체를 거절하므로 저장하는 모든 위치가 지나야 한다.
    // 입력: position - 위치.
    // 출력: 안이면 true.
    private static bool InMap(Vector3 position) =>
        MathF.Abs(position.X) <= GameMap.HalfSize && MathF.Abs(position.Z) <= GameMap.HalfSize;

    // 기능: 보낸 높이를 그 점의 지형 높이 - 1 m부터 지형 높이 + 건설 최고 높이(Levels × LevelHeight) + 2 m까지로 자른다(D7: 탑·지붕 위 Ping).
    // 입력: position - 맵 안의 위치.
    // 출력: 자른 높이.
    private static float ClampMarkerHeight(Vector3 position)
    {
        float ground = GameMap.Terrain.Height(position.X, position.Z);
        return Math.Clamp(position.Y, ground - 1f, ground + BuildGrid.Levels * BuildGrid.LevelHeight + 2f);
    }

    // 기능: Enemy Ping을 확인한다(D8): 대상이 다른 팀의 살아 있는 플레이어이고, 보낸 사람의 눈에서 대상 몸 가운데까지 enemyPingRange 안이며,
    //   맵 상자·닫힌 문·지형에 막히지 않는다(건설 조각과 채집 대상은 보지 않는다, Phase 13.5 배치 시선과 같은 규칙). 대상 발이 맵 밖(수송기·낙하)이면 실패.
    // 입력: sender - 보낸 사람, targetId - 대상 Entity id, at - 결과 위치.
    // 출력: 확인되면 true와 대상의 지금 발 위치, 아니면 false.
    private bool ConfirmEnemy(PlayerEntity sender, ushort targetId, out Vector3 at)
    {
        at = default;
        PlayerEntity? target = null;
        foreach (var p in _players)
        {
            if (p.EntityId == targetId)
            {
                target = p;
                break;
            }
        }
        if (target == null || target == sender || !target.Alive || SameTeam(sender, target) || !InMap(target.State.Position)) return false;
        Vector3 eye = sender.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(sender.State.Mode), 0f);
        Vector3 center = target.State.Position + new Vector3(0f, MovementSimulation.CollisionHeight(target.State.Mode) * 0.5f, 0f);
        Vector3 delta = center - eye;
        float distance = delta.Length();
        if (distance > _map.EnemyPingRange) return false;
        if (distance > 1e-3f && HitScan.TraceWorld(eye, delta / distance, distance, _doors.World, GameMap.Terrain) < distance - 1e-3f) return false;
        at = target.State.Position;
        return true;
    }

    // 기능: Item Ping의 아이템을 찾는다(D8): 월드에 있고, 다른 팀의 재투입 카드가 아니며(그 팀만 보인다), 보낸 사람 발에서 itemPingRange 안.
    // 입력: sender - 보낸 사람, itemId - 아이템 id, at - 결과 위치.
    // 출력: 찾으면 true와 아이템 위치, 아니면 false.
    private bool FindPingItem(PlayerEntity sender, ushort itemId, out Vector3 at)
    {
        at = default;
        int index = _worldItems.IndexOf(itemId);
        if (index < 0) return false;
        ref readonly var item = ref _worldItems[index];
        if (item.Data.Kind == ItemKind.RebootCard && item.CardTeam != sender.TeamId) return false;
        if (Vector3.Distance(sender.State.Position, item.Data.Position) > _map.ItemPingRange || !InMap(item.Data.Position)) return false;
        at = item.Data.Position;
        return true;
    }

    // 기능: 팀 Ping 하나를 넣는다(D9). 보낸 사람의 활성 Ping이 상한이면 그 사람의 가장 오래된 것을, 아니면 빈 칸을, 팀 상한이면 팀의 가장 오래된 것을 바꾼다.
    // 입력: owner - 보낸 사람(팀 있음), kind - Ping 종류, at - 위치, targetId - Enemy·Item 대상 id(그 밖 0), lifetimeTicks - 수명.
    // 출력: 반환값 없음. 팀 배열이 바뀌고 팀이 Dirty가 된다.
    private void AddPing(PlayerEntity owner, MapMarkerKind kind, Vector3 at, ushort targetId, uint lifetimeTicks)
    {
        int team = owner.TeamId;
        int first = team * MapMarkerConstants.MaxTeamPings;
        int free = -1, teamOldest = -1, ownerOldest = -1, ownerCount = 0;
        for (int i = first; i < first + _map.PingsPerTeam; i++)
        {
            ref TeamPing p = ref _pings[i];
            if (!p.Active)
            {
                if (free < 0) free = i;
                continue;
            }
            if (teamOldest < 0 || p.Order < _pings[teamOldest].Order) teamOldest = i;
            if (p.Wire.OwnerId != owner.EntityId) continue;
            ownerCount++;
            if (ownerOldest < 0 || p.Order < _pings[ownerOldest].Order) ownerOldest = i;
        }
        int slot;
        if (ownerCount >= _map.PingsPerPlayer) slot = ownerOldest;
        else if (free >= 0) slot = free;
        else slot = teamOldest;
        if (_pings[slot].Active) PingsReplaced++;
        else _activePings++;

        byte id = (byte)(_lastPingId[team] == byte.MaxValue ? 1 : _lastPingId[team] + 1);
        _lastPingId[team] = id;
        _pings[slot] = new TeamPing
        {
            Active = true,
            Order = ++_pingOrder,
            Wire = new MarkerPing
            {
                Id = id, Kind = kind, OwnerId = owner.EntityId, Position = at, EndTick = ServerTick + lifetimeTicks, TargetId = targetId,
            },
        };
        MarkerPings++;
        MarkDirty(owner.TeamId);
    }

    // 기능: 팀의 표시가 바뀌었다고 적는다(이 Tick 끝에 TeamMarkers).
    // 입력: team - 팀 id(0은 무시).
    // 출력: 반환값 없음.
    private void MarkDirty(byte team)
    {
        if (team == 0) return;
        _markersDirty[team] = true;
        _anyMarkersDirty = true;
    }

    // 기능: 끝 Tick이 된 Ping을 지운다(D9). 활성 Ping이 없으면 바로 돌아간다(Tick 경로).
    // 입력: 없음(ServerTick과 비교한다).
    // 출력: 반환값 없음. 지운 Ping의 팀이 Dirty가 된다.
    private void ExpirePings()
    {
        if (_activePings == 0) return;
        int seen = 0;
        for (int i = 0; i < _pings.Length && seen < _activePings; i++)
        {
            ref TeamPing p = ref _pings[i];
            if (!p.Active) continue;
            if (ServerTick < p.Wire.EndTick)
            {
                seen++;
                continue;
            }
            p.Active = false;
            _activePings--;
            PingsExpired++;
            MarkDirty((byte)(i / MapMarkerConstants.MaxTeamPings));
        }
    }

    // 기능: 한 팀의 활성 Ping(오래된 순서 아님, 칸 순서)과 팀원 Waypoint를 주어진 배열에 모은다.
    // 입력: team - 팀 id, pings - MaxTeamPings 칸 이상, waypoints - MaxWaypoints 칸 이상, pingCount·waypointCount - 결과 수.
    // 출력: 반환값 없음. 배열 앞쪽이 채워진다.
    private void CollectTeamMarkers(byte team, Span<MarkerPing> pings, Span<MarkerWaypoint> waypoints, out int pingCount, out int waypointCount)
    {
        pingCount = 0;
        waypointCount = 0;
        int first = team * MapMarkerConstants.MaxTeamPings;
        for (int i = first; i < first + MapMarkerConstants.MaxTeamPings && pingCount < pings.Length; i++)
        {
            if (_pings[i].Active) pings[pingCount++] = _pings[i].Wire;
        }
        foreach (var p in _players)
        {
            if (p.TeamId != team || !p.HasWaypoint || waypointCount >= waypoints.Length) continue;
            waypoints[waypointCount++] = new MarkerWaypoint { OwnerId = p.EntityId, Position = p.Waypoint };
        }
    }

    // 기능: 한 팀의 TeamMarkers 패킷을 _sendBuffer에 쓴다.
    // 입력: team - 팀 id.
    // 출력: 쓴 패킷 바이트.
    private ReadOnlySpan<byte> WriteTeamMarkers(byte team)
    {
        CollectTeamMarkers(team, _pingScratch, _waypointScratch, out int pingCount, out int waypointCount);
        var writer = new PacketWriter(_sendBuffer);
        TeamMarkersPacket.Write(ref writer, new ReadOnlySpan<MarkerPing>(_pingScratch, 0, pingCount),
            new ReadOnlySpan<MarkerWaypoint>(_waypointScratch, 0, waypointCount));
        return writer.WrittenSpan;
    }

    // 기능: Tick 끝에 만료를 처리하고, 표시가 바뀐 팀마다 그 팀원에게만 TeamMarkers를 보낸다(D10).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void SendMarkerChanges()
    {
        ExpirePings();
        if (!_anyMarkersDirty) return;
        _anyMarkersDirty = false;
        for (int t = 1; t < TeamSlots; t++)
        {
            if (!_markersDirty[t]) continue;
            _markersDirty[t] = false;
            ReadOnlySpan<byte> packet = WriteTeamMarkers((byte)t);
            foreach (var p in _players)
            {
                if (p.TeamId != t) continue;
                _send(p.PeerId, packet, DeliveryMethod.ReliableOrdered);
                MarkerPackets++;
            }
        }
    }

    // 기능: 한 플레이어에게 자기 팀의 표시를 지금 보낸다(입장·Resume, D10). 팀이 없으면 보내지 않는다.
    // 입력: player - 받는 사람.
    // 출력: 반환값 없음.
    private void SendMarkersTo(PlayerEntity player)
    {
        if (player.TeamId == 0) return;
        _send(player.PeerId, WriteTeamMarkers(player.TeamId), DeliveryMethod.ReliableOrdered);
        MarkerPackets++;
    }

    // 기능: 떠나는 플레이어의 Ping과 Waypoint를 지운다(그 팀이 Dirty가 된다).
    // 입력: player - 떠나는 플레이어(아직 TeamId가 있다).
    // 출력: 반환값 없음.
    private void RemoveMarkersOf(PlayerEntity player)
    {
        byte team = player.TeamId;
        if (team == 0) return;
        bool changed = player.HasWaypoint;
        player.HasWaypoint = false;
        int first = team * MapMarkerConstants.MaxTeamPings;
        for (int i = first; i < first + MapMarkerConstants.MaxTeamPings; i++)
        {
            if (!_pings[i].Active || _pings[i].Wire.OwnerId != player.EntityId) continue;
            _pings[i].Active = false;
            _activePings--;
            changed = true;
        }
        if (changed) MarkDirty(team);
    }

    // 기능: 모든 Ping과 Waypoint를 지운다(경기 시작·경기 끝·라운드 리셋). 알림은 호출자가 정한다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearMarkers()
    {
        Array.Clear(_pings);
        _activePings = 0;
        foreach (var p in _players) p.HasWaypoint = false;
    }

    // 기능: 1.._maxTeamId 팀을 모두 Dirty로 만든다(경기 시작·끝: 그 Tick 끝에 빈 목록이 간다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void MarkAllTeamsDirty()
    {
        for (int t = 1; t <= _maxTeamId; t++) MarkDirty((byte)t);
    }

    // 기능: 경기 끝(D5, D9): 모든 표시를 지우고 Tick 끝에 각 팀에 빈 목록을 보낸다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearMarkersAtFinish()
    {
        ClearMarkers();
        MarkAllTeamsDirty();
    }

    // 기능: 라운드 리셋: 남은 표시가 있으면 팀이 지워지기 전에 팀이 있는 모두에게 빈 목록을 지금 보내고(Tick 끝에는 팀이 없다), 모두 지운다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearMarkersAtRoundReset()
    {
        bool any = _activePings > 0;
        foreach (var p in _players) any |= p.HasWaypoint;
        ClearMarkers();
        Array.Clear(_markersDirty);
        _anyMarkersDirty = false;
        if (!any) return;
        var writer = new PacketWriter(_sendBuffer);
        TeamMarkersPacket.Write(ref writer, ReadOnlySpan<MarkerPing>.Empty, ReadOnlySpan<MarkerWaypoint>.Empty);
        foreach (var p in _players)
        {
            if (p.TeamId == 0) continue;
            _send(p.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            MarkerPackets++;
        }
    }

    // 기능: QA 관찰용으로 한 팀의 활성 Ping과 Waypoint를 새 배열로 돌려준다(Game Loop 스레드의 QA 작업에서만, Tick 경로 아님).
    // 입력: team - 팀 id(0이면 빈 배열).
    // 출력: Ping 배열과 Waypoint 배열.
    internal (MarkerPing[] Pings, MarkerWaypoint[] Waypoints) TeamMarkersView(byte team)
    {
        if (team == 0) return (Array.Empty<MarkerPing>(), Array.Empty<MarkerWaypoint>());
        var pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        var waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        CollectTeamMarkers(team, pings, waypoints, out int pingCount, out int waypointCount);
        return (pings.AsSpan(0, pingCount).ToArray(), waypoints.AsSpan(0, waypointCount).ToArray());
    }
}
