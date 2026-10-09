using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 5 D1, D2, D4 inside Match: the flow runs at the start of every tick, and a battle royale server (no
// DevRespawn) has no loot until the match starts.
public class MatchFlowMatchTests
{
    // 기능: 패킷을 버리는 4인 Match를 만든다.
    // 입력: devRespawn - true면 개발 모드(흐름 전환 없음, Loot 즉시 채움).
    // 출력: 참가자 없는 Match.
    private static Match Create(bool devRespawn) =>
        new(new ServerOptions { MaxPlayers = 4, DevRespawn = devRespawn }, TestGameData.Create(), static (_, _, _) => { });

    [Fact]
    public void WithoutDevRespawn_TheWorldStartsEmpty()
    {
        Assert.Equal(0, Create(devRespawn: false).WorldItems.Count);
        Assert.Equal(LootPoints.All.Length, Create(devRespawn: true).WorldItems.Count);   // the Phase 4 sandbox fills every point
    }

    [Fact]
    public void TwoJoins_StartTheCountdown_AndTheMatchStarts10SecondsLater()
    {
        Match match = Create(devRespawn: false);
        match.TryJoin(1, "a");
        match.Tick();
        Assert.Equal(MatchFlowState.WaitingForPlayers, match.Flow.State);

        match.TryJoin(2, "b");
        match.Tick();   // tick 1 sees two players
        Assert.Equal(MatchFlowState.Starting, match.Flow.State);
        Assert.Equal(1u + 300u, match.Flow.StateEndTick);

        while (match.ServerTick < 301) match.Tick();
        Assert.Equal(MatchFlowState.Starting, match.Flow.State);
        match.Tick();
        Assert.Equal(MatchFlowState.Playing, match.Flow.State);
        Assert.Equal(2, match.Flow.Participants);
    }

    [Fact]
    public void LeaveDuringCountdown_GoesBackToWaiting()
    {
        Match match = Create(devRespawn: false);
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");
        match.Tick();
        Assert.Equal(MatchFlowState.Starting, match.Flow.State);

        match.Leave(2);
        match.Tick();
        Assert.Equal(MatchFlowState.WaitingForPlayers, match.Flow.State);
        for (int i = 0; i < 400; i++) match.Tick();
        Assert.Equal(MatchFlowState.WaitingForPlayers, match.Flow.State);
    }

    [Fact]
    public void DevRespawn_NeverLeavesWaiting()
    {
        Match match = Create(devRespawn: true);
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");
        for (int i = 0; i < 400; i++) match.Tick();
        Assert.Equal(MatchFlowState.WaitingForPlayers, match.Flow.State);
    }
}
