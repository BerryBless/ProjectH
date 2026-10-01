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
