using System.Collections.Generic;
using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Bots;

// Phase 12 D15 (spec §2 봇): the landing target, the jump tick, riding, steering the fall, and the stuck sprint.
public class BotDeployTests
{
    // 기능: 고도 90 m에서 Tick 1000~1300 동안 x -100에서 100까지 +X로 나는 수송기 경로를 만든다.
    // 입력: 없음.
    // 출력: DropRoute.
    // Along +X at 90 m: x -100..100 over ticks 1000..1300; the jump window is 1045..1255 (x -70..70).
    private static DropRoute AlongX() => new()
    {
        StartX = -100f, StartZ = 0f, EndX = 100f, EndZ = 0f, Altitude = 90f, StartTick = 1000, DurationTicks = 300,
    };

    // 기능: AlongX 경로의 수송기에 탄 채 Playing 상태인 BotView를 만든다.
    // 입력: serverTick - View의 현재 서버 Tick.
    // 출력: Transport 모드, 경로 보유, 참가자 2명 Match 상태의 BotView.
    private static BotView Aboard(uint serverTick = 1000)
    {
        BotView view = BotTestView.Create(new Vector3(-100f, 90f, 0f));
        view.MyMode = MovementMode.Transport;
        view.HasRoute = true;
        view.Route = AlongX();
        view.ServerTick = serverTick;
        view.HasMatchState = true;
        view.Match = new MatchState { State = MatchFlowState.Playing, Alive = 2, Participants = 2 };
        return view;
    }

    [Theory]
    [InlineData(0f, 1150u)]     // the centre: halfway
    [InlineData(40f, 1210u)]    // x 40 (its z does not matter)
    [InlineData(-95f, 1045u)]   // before the window opens: the window's first tick
    [InlineData(95f, 1255u)]    // after it closes: its last tick
    public void TheJumpTick_IsWhereTheTransportPassesClosest_InsideTheWindow(float x, uint tick)
    {
        Assert.Equal(tick, BotBrain.PlanJumpTick(AlongX(), new Vector3(x, 0f, 30f)));
    }

    [Fact]
    public void Aboard_TheBotSendsNoMove_AndJumpsFromItsTick_OnceEveryHalfSecond()
    {
        var brain = new BotBrain(3);
        BotView view = Aboard();
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(BotGoal.Deploy, brain.Goal);
        Assert.Equal(0f, command.MoveX);
        Assert.Equal(0f, command.MoveY);
        Assert.Equal(InputButtons.None, command.Buttons);
        uint jumpTick = brain.JumpTick;

        int jumps = 0;
        for (int i = 0; i < 30; i++)
        {
            view.ServerTick = jumpTick - 1 + (uint)i;
            brain.Tick(view, 1f + i / 30f, out command);
            if ((command.Buttons & InputButtons.Jump) != 0) jumps++;
            if (i == 0) Assert.Equal(InputButtons.Jump, command.Buttons);   // the tick before: it arrives in time
        }
        Assert.Equal(2, jumps);   // still aboard after half a second (as far as it knows): pressed again
    }

    [Fact]
    public void Falling_TheBotFacesItsTarget_AndFliesTowardsIt_ThenDropsStraight()
    {
        var brain = new BotBrain(5);
        BotView view = Aboard();
        brain.Tick(view, 0f, out _);
        Vector3 target = brain.LandingTarget;

        view.MyMode = MovementMode.Freefall;
        view.MyPosition = new Vector3(target.X - 30f, 60f, target.Z);
        brain.Tick(view, 1f, out InputCommand command);
        Assert.Equal(90f, command.Yaw, 2);   // the target is along +X
        Assert.Equal(1f, command.MoveY);
        Assert.Equal(InputButtons.None, command.Buttons);   // the glider is left to the server

        view.MyMode = MovementMode.Glide;
        view.MyPosition = new Vector3(target.X + 2f, 20f, target.Z + 2f);
        brain.Tick(view, 2f, out command);
        Assert.Equal(0f, command.MoveY);
        Assert.Equal(BotGoal.Deploy, brain.Goal);
    }

    [Fact]
    public void TheLandingTargets_AreOnTheMap_AndSpreadOverPoisAndItems()
    {
        var targets = new HashSet<Vector3>();
        for (int seed = 0; seed < 20; seed++)
        {
            var brain = new BotBrain(seed);
            BotView view = Aboard();
            BotTestView.AddItem(view, 7, ItemKind.Ammo, (byte)AmmoType.Light, new Vector3(30f, 0f, -20f));
            BotTestView.AddItem(view, 8, ItemKind.Ammo, (byte)AmmoType.Heavy, new Vector3(-12f, 0f, 61f));
            brain.Tick(view, 0f, out _);
            Vector3 t = brain.LandingTarget;
            Assert.True(MathF.Abs(t.X) < GameMap.HalfSize && MathF.Abs(t.Z) < GameMap.HalfSize);
            targets.Add(t);
        }
        Assert.True(targets.Count >= 4, $"only {targets.Count} different targets");
    }

    [Fact]
    public void OnceLanded_TheBotPlaysAsBefore()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard();
        brain.Tick(view, 0f, out _);
        view.MyMode = MovementMode.Ground;
        view.MyPosition = new Vector3(0f, 0f, 0f);
        brain.Tick(view, 1f, out _);
        Assert.NotEqual(BotGoal.Deploy, brain.Goal);
    }

    [Fact]
    public void AboardWithoutARoute_TheBotWaits()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard();
        view.HasRoute = false;
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(InputButtons.None, command.Buttons);
        view.HasRoute = true;
        brain.Tick(view, 0.1f, out _);
        Assert.NotEqual(0u, brain.JumpTick);
    }

    [Fact]
    public void AStuckBot_Sprints()
    {
        var steering = new BotSteering();
        var rng = new Random(1);
        var goal = new Vector3(5f, 0f, 0f);   // within SprintDistance: a walk
        steering.Reset(Vector3.Zero, goal, 0f);
        Assert.False(steering.Stuck);
        steering.Steer(Vector3.Zero, goal, BotSteering.CheckInterval + 0.01f, rng, out _, out bool jump, out _);
        Assert.True(jump);
        Assert.True(steering.Stuck);
        steering.Steer(new Vector3(1f, 0f, 0f), goal, 2f * BotSteering.CheckInterval + 0.02f, rng, out _, out _, out _);
        Assert.False(steering.Stuck);

        // Through the brain: a goal 8 m away (the zone's centre) is walked to; stuck there, the input holds Sprint.
        var brain = new BotBrain(1);
        BotView view = BotTestView.Create();
        view.Zone = new ZoneState { Phase = 1, ToX = 8f, ToZ = 0f, ToRadius = 0f, FromRadius = 115f };
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(BotGoal.Zone, brain.Goal);
        Assert.Equal(InputButtons.None, command.Buttons & InputButtons.Sprint);
        brain.Tick(view, BotSteering.CheckInterval + 0.01f, out command);
        Assert.Equal(InputButtons.Sprint, command.Buttons & InputButtons.Sprint);
    }

    // Final review B2: a new round's countdown forgets the last route, a route that already ended is never planned from,
    // and a death ends the deployment, so the next life plans from the next route.
    [Fact]
    public void ANewRoundsCountdown_ForgetsTheRoute()
    {
        BotView view = Aboard();
        view.ApplyMatch(new MatchState { State = MatchFlowState.Finished });
        Assert.True(view.HasRoute);
        view.ApplyMatch(new MatchState { State = MatchFlowState.Starting });
        Assert.False(view.HasRoute);
        view.HasRoute = true;
        view.ApplyMatch(new MatchState { State = MatchFlowState.WaitingForPlayers });
        Assert.False(view.HasRoute);
    }

    [Fact]
    public void Aboard_ARouteThatEnded_IsNotPlannedFrom()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard(serverTick: AlongX().EndTick + 50);
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(BotGoal.Deploy, brain.Goal);
        Assert.Equal(InputButtons.None, command.Buttons);
        Assert.Equal(0u, brain.JumpTick);
    }

    [Fact]
    public void ADeath_EndsTheDeployment_TheNextLifePlansFromTheNextRoute()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard();
        brain.Tick(view, 0f, out _);
        Assert.InRange(brain.JumpTick, 1000u, 1300u);

        view.Alive = false;
        brain.Tick(view, 1f, out _);
        view.Alive = true;
        DropRoute next = AlongX();
        next.StartTick = 5000;
        view.Route = next;
        view.ServerTick = 5000;
        brain.Tick(view, 2f, out _);
        Assert.InRange(brain.JumpTick, 5000u, 5300u);
    }
}
