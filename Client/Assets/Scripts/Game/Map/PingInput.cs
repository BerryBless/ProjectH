using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Map
{
    // Phase 15 D6: the middle-button tap rule. A press waits DangerWindow seconds: a second press inside it sends one Danger
    // ping at the first press's point, otherwise the first press's context ping goes out when the window ends. So a
    // double press is one request, never two (the server's per-second cap stays for real pings). Pure (no UnityEngine).
    // The caller cancels a waiting ping when pinging stops being allowed (death, the map opening, a disconnect, a round reset).
    public sealed class PingTap
    {
        public const float DangerWindow = 0.3f;

        private bool _pending;
        private float _pressAt;
        private MapMarker _marker;

        public bool Pending => _pending;

        // 기능: 가운데 버튼 누름 하나를 처리한다.
        // 입력: now - 지금 시각(초), hasMarker - 이번 누름의 맥락 Ping이 있는지(조준이 아무것도 맞히지 않았으면 false),
        //   marker - 이번 누름의 맥락 Ping, send - 결과.
        // 출력: 기다리던 첫 누름의 DangerWindow 안이면 true와 그 지점의 Danger Ping(대상 0). 아니면 false이고, 맥락이 있으면 이번 누름이
        //   기다리기 시작한다(맥락이 없으면 아무것도 기다리지 않는다).
        public bool Press(float now, bool hasMarker, in MapMarker marker, out MapMarker send)
        {
            if (_pending && now - _pressAt <= DangerWindow)
            {
                _pending = false;
                send = new MapMarker { Kind = MapMarkerKind.Danger, Position = _marker.Position, TargetId = 0 };
                return true;
            }
            send = default;
            _pending = hasMarker;
            if (!hasMarker) return false;
            _pressAt = now;
            _marker = marker;
            return false;
        }

        // 기능: 기다리던 첫 누름의 DangerWindow가 지났는지 본다(매 프레임, 그 프레임의 누름보다 먼저 부른다).
        // 입력: now - 지금 시각(초), send - 결과.
        // 출력: 창이 지났으면 true와 첫 누름의 맥락 Ping(한 번만), 아니면 false.
        public bool Update(float now, out MapMarker send)
        {
            if (_pending && now - _pressAt > DangerWindow)
            {
                _pending = false;
                send = _marker;
                return true;
            }
            send = default;
            return false;
        }

        // 기능: 기다리던 누름을 버린다(Ping을 보낼 수 없게 됐을 때).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Cancel() => _pending = false;
    }

    // Phase 15 D6: which ping a press means, from what the aim ray hit. Pure (no UnityEngine).
    public static class PingContext
    {
        // An item on the ground has no collider, so the item nearest to the hit point within this distance is meant.
        public const float ItemRadius = 2f;

        // 기능: 조준 Raycast 결과로 맥락 Ping을 고른다: 적 Collider → Enemy(대상 Entity id), 맞은 점 ItemRadius 안의 가장 가까운 월드 아이템 →
        //   Item(아이템 id, 위치는 아이템 위치), 그 밖 → Location(맞은 점).
        // 입력: enemyId - 맞은 Collider가 원격 플레이어면 그 Entity id, 아니면 0(팀원 Collider는 꺼져 있어 맞지 않는다),
        //   hitPoint - 맞은 점(월드), items - Client의 월드 아이템 목록, marker - 결과.
        // 출력: 맞은 점이 맵 안(|x|, |z| ≤ HalfSize)이면 true와 Ping, 맵 밖이면 false(서버가 버린다). id 0인 아이템은 고르지 않는다
        //   (서버 Reader가 Item 대상 0을 잘못된 패킷으로 센다).
        public static bool Choose(ushort enemyId, Vector3 hitPoint, WorldItemList items, out MapMarker marker)
        {
            marker = default;
            if (!InMap(hitPoint)) return false;
            if (enemyId != 0)
            {
                marker = new MapMarker { Kind = MapMarkerKind.Enemy, Position = hitPoint, TargetId = enemyId };
                return true;
            }
            int best = -1;
            float bestSq = ItemRadius * ItemRadius;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    WorldItemData item = items[i];
                    if (item.ItemId == 0) continue;
                    float sq = Vector3.DistanceSquared(item.Position, hitPoint);
                    if (sq > bestSq) continue;
                    bestSq = sq;
                    best = i;
                }
            }
            if (best >= 0 && InMap(items[best].Position))
            {
                WorldItemData chosen = items[best];
                marker = new MapMarker { Kind = MapMarkerKind.Item, Position = chosen.Position, TargetId = chosen.ItemId };
                return true;
            }
            marker = new MapMarker { Kind = MapMarkerKind.Location, Position = hitPoint, TargetId = 0 };
            return true;
        }

        // 기능: 지금 Ping할 수 있는지 정한다(D6). 기절해도 된다: 행동 검사(canAct·LocalPlayerPredictor.ActionsAllowed, 기절이면 false)를
        //   지나지 않는 별도 조건이다.
        // 입력: dead - 탈락(기절은 dead가 아니다), hasMatch - 경기 상태를 받았는지(개발 모드는 false), inPlay - 경기 안의 우리 팀 구성원(Up
        //   또는 기절)인지, uiBlocked - 화면·전체 지도가 열렸거나 커서가 풀림, spectating - 관전 중.
        // 출력: 살아 있고(기절 포함), 경기 중이면 참가자이고(개발 모드는 누구나), 화면·지도가 닫혀 있고, 관전 중이 아니면 true.
        public static bool CanPing(bool dead, bool hasMatch, bool inPlay, bool uiBlocked, bool spectating) =>
            !dead && (!hasMatch || inPlay) && !uiBlocked && !spectating;

        // 기능: 수평 좌표가 맵 안(양 끝 포함)인지 본다. NaN이면 false.
        // 입력: p - 월드 위치.
        // 출력: 안이면 true.
        public static bool InMap(Vector3 p) =>
            p.X >= -GameMap.HalfSize && p.X <= GameMap.HalfSize && p.Z >= -GameMap.HalfSize && p.Z <= GameMap.HalfSize;
    }

    // Phase 15 D11: a ping's on-screen distance text ("23 m"), rebuilt only when the whole metre changes. Pure.
    public sealed class DistanceText
    {
        private int _meters = -1;

        public string Text { get; private set; } = string.Empty;

        // 기능: 거리를 받아 정수 m가 바뀌었을 때만 문구를 새로 만든다.
        // 입력: meters - 거리(m, 음수·NaN은 0으로 본다).
        // 출력: 문구가 바뀌었으면 true(Text가 새 문자열), 같으면 false(할당 없음).
        public bool Update(float meters)
        {
            int m = meters > 0f ? (meters >= 99999f ? 99999 : (int)meters) : 0;
            if (m == _meters) return false;
            _meters = m;
            Text = ProjectH.Client.UI.UiText.Meters(m);
            return true;
        }

        // 기능: 다음 Update가 반드시 문구를 만들게 한다(표지 칸이 다른 Ping으로 바뀔 때).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset() => _meters = -1;
    }
}
