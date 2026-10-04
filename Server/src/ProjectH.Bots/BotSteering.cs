using System.Numerics;

namespace ProjectH.Bots;

// Phase 7 D5: straight-line steering with an unstick routine. Every CheckInterval while moving, a bot that made less
// than MinProgress jumps once; still stuck after that, it walks DetourSeconds at 90 degrees to one side; and when the
// goal has come no closer by GiveUpProgress for GiveUpSeconds, the caller should drop the goal.
public sealed class BotSteering
{
    public const float CheckInterval = 0.5f;
    public const float MinProgress = 0.3f;
    public const float DetourMinSeconds = 1f;
    public const float DetourMaxSeconds = 2f;
    public const float GiveUpSeconds = 8f;
    public const float GiveUpProgress = 0.5f;

    private Vector3 _lastCheckPosition;
    private float _nextCheck;
    private int _stuckChecks;
    private float _detourUntil;
    private float _detourYaw;
    private float _bestDistance;
    private float _bestTime;

    // Phase 12 D15: the last progress check found no progress (cleared again when a check starts a detour). The bot then sprints, so a closed door in the way is
    // shouldered open (D9) and a low obstacle is hurdled when it jumps (D8).
    public bool Stuck => _stuckChecks > 0;

    // 기능: 새 목표를 받아 끼임 판정 상태와 포기 타이머를 처음부터 다시 시작한다.
    // 입력: position - 봇의 현재 위치, goal - 새 목표 지점, now - 현재 시각(초).
    // 출력: 반환값 없음. 다음 점검 시각, 끼임 횟수, 우회 상태, 최단 거리 기록이 초기화된다.
    // A new goal: the unstick state and the give-up clock start over.
    public void Reset(Vector3 position, Vector3 goal, float now)
    {
        _lastCheckPosition = position;
        _nextCheck = now + CheckInterval;
        _stuckChecks = 0;
        _detourUntil = 0f;
        _bestDistance = BotAim.HorizontalDistance(position, goal);
        _bestTime = now;
    }

    // 기능: 이번 Tick에 걸어갈 방향을 정하고, CheckInterval마다 진행량을 보고 점프·옆 우회·포기를 판단한다.
    // 입력: position - 봇의 현재 위치, goal - 목표 지점, now - 현재 시각(초), rng - 우회 방향·시간을 뽑을 봇 고유 난수.
    // 출력: 반환값 없음. yaw - 이번 Tick 이동 방향(우회 중이면 우회 방향), jump - 처음 끼었을 때 true, giveUp - GiveUpSeconds 동안 목표에 GiveUpProgress만큼도 가까워지지 못했으면 true. 끼임·우회 상태가 갱신된다.
    // Heading to walk this tick, whether to jump, and whether the goal looks unreachable.
    public void Steer(Vector3 position, Vector3 goal, float now, Random rng, out float yaw, out bool jump, out bool giveUp)
    {
        jump = false;
        float distance = BotAim.HorizontalDistance(position, goal);
        if (distance < _bestDistance - GiveUpProgress)
        {
            _bestDistance = distance;
            _bestTime = now;
        }
        giveUp = now - _bestTime > GiveUpSeconds;

        if (now >= _nextCheck)
        {
            float moved = BotAim.HorizontalDistance(position, _lastCheckPosition);
            _stuckChecks = moved < MinProgress ? _stuckChecks + 1 : 0;
            _lastCheckPosition = position;
            _nextCheck = now + CheckInterval;
            if (_stuckChecks == 1)
            {
                jump = true;
            }
            else if (_stuckChecks >= 2 && now >= _detourUntil)
            {
                float side = rng.Next(2) == 0 ? -90f : 90f;
                _detourYaw = BotAim.YawTo(position, goal) + side;
                _detourUntil = now + DetourMinSeconds + (float)rng.NextDouble() * (DetourMaxSeconds - DetourMinSeconds);
                _stuckChecks = 0;
            }
        }

        yaw = now < _detourUntil ? _detourYaw : BotAim.YawTo(position, goal);
    }
}
