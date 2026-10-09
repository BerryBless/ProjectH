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

    // Phase 12 D15: the last progress check found no progress. The bot then sprints, so a closed door in the way is
    // shouldered open (D9) and a low obstacle is hurdled when it jumps (D8).
    public bool Stuck => _stuckChecks > 0;

    // 기능: 새 목표로 조종을 시작한다(끼임 해제 상태와 포기 시계를 처음으로 되돌린다).
    // 입력: position - 봇의 지금 위치, goal - 새 목표점, now - 지금 시각(초).
    // 출력: 반환값 없음. 진행 검사 기준 위치·시각과 최단 거리가 다시 잡힌다.
    public void Reset(Vector3 position, Vector3 goal, float now)
    {
        _lastCheckPosition = position;
        _nextCheck = now + CheckInterval;
        _stuckChecks = 0;
        _detourUntil = 0f;
        _bestDistance = BotAim.HorizontalDistance(position, goal);
        _bestTime = now;
    }

    // 기능: 이번 Tick에 걸을 방향을 정한다. CheckInterval마다 진행을 재서 처음 막히면 점프, 다시 막히면 옆으로 우회하고, GiveUpSeconds 동안 가까워지지 않으면 포기를 알린다.
    // 입력: position - 봇의 지금 위치, goal - 목표점, now - 지금 시각(초), rng - 우회 방향 난수, yaw·jump·giveUp - 결과를 받을 곳.
    // 출력: 반환값 없음. yaw는 걸을 방향(우회 중이면 우회 방향), jump는 이번 Tick에 점프할지, giveUp은 목표가 닿지 않는 것 같으면 true.
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
