using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 12 D12: the movement self-check. The server simulates every move itself from inputs only, so a move faster than
// its mode allows is a bug in the simulation, never a cheat: Match only counts it (movementAnomalies on the Health line).
// Each limit is the fastest horizontal speed of the mode plus the fastest vertical one, from MovementTuning; a tick may
// move at most that x dt x Slack.
public static class MovementLimits
{
    public const float Slack = 1.5f;

    // 기능: 이동 모드별 이론상 최대 속도(수평 최대 + 수직 최대)를 계산한다.
    // 입력: mode - 이동 모드.
    // 출력: 초당 최대 이동 거리(m/s). Transport처럼 시뮬레이션으로 이동하지 않는 모드는 0.
    public static float MaxSpeed(MovementMode mode)
    {
        switch (mode)
        {
            case MovementMode.Ground:
            case MovementMode.Crouch:
            case MovementMode.Slide:
                // A slide jump carries the slide's top speed. Ground-mode gravity has no terminal speed, but a fall long enough
                // to pass this bound is not reachable on this map; the bound holds through the Slack and the per-tick allowance.
                return MovementTuning.SlideMaxSpeed + MovementTuning.FreefallTerminalSpeed;
            case MovementMode.Vault:
                // The longest hurdle (reach, the deepest obstacle, the body and the landing gap) in the shortest vault, plus
                // the highest mantle in that time; a slope behind the obstacle adds at most MaxSlope of the distance.
                float reach = MovementTuning.VaultReach + MovementTuning.HurdleMaxDepth + 2f * MoveSettings.HalfWidth + MovementTuning.HurdleLandingGap;
                return (reach * (1f + MoveSettings.MaxSlope) + MovementTuning.MantleMaxHeight) / MovementTuning.HurdleSeconds;
            case MovementMode.Freefall:
                return MovementTuning.FreefallForwardSpeed + MovementTuning.FreefallTerminalSpeed;
            case MovementMode.Glide:
                return MovementTuning.GlideForwardSpeed + MovementTuning.GlideFallSpeed;
            default:
                return 0f;   // Transport: placed by DropTransport.Ride, never stepped
        }
    }
}
