using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D3, D5 (spec §2): every transition of the client's screen flow, and the cursor and input outputs. The flow is
// the Unity client's own file, compiled here through a source link (it has no UnityEngine dependency).
public class UiFlowTests
{
    private const MatchFlowState Playing = MatchFlowState.Playing;

    private static UiFlow InGame()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        return flow;
    }

    [Fact]
    public void Title_Connecting_InGame()
    {
        var flow = new UiFlow();
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Title, flow.Screen);   // nothing happens until the player connects

        flow.ConnectRequested();
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void Escape_OpensAndClosesTheMenu_AndContinueCloses()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();
        Assert.Equal(UiScreen.Menu, flow.Screen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
        flow.EscapePressed();
        flow.ContinuePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void Stats_OpensOverTheMenu_AndEscapeClosesItFirst()
    {
        UiFlow flow = InGame();
        flow.OpenStats();
        Assert.False(flow.StatsOpen);   // not from the game itself
        flow.EscapePressed();
        flow.OpenStats();
        Assert.True(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);

        flow.EscapePressed();
        Assert.False(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);
        flow.OpenStats();
        flow.CloseStats();
        Assert.False(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);
    }

    [Theory]
    [InlineData(UiScreen.InGame)]
    [InlineData(UiScreen.Menu)]
    [InlineData(UiScreen.Result)]
    public void ALostConnection_ShowsDisconnected_FromAnyGameScreen(UiScreen from)
    {
        UiFlow flow = InGame();
        if (from == UiScreen.Menu) flow.EscapePressed();
        if (from == UiScreen.Result) flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(from, flow.Screen);

        flow.Update(UiConnection.Offline, true, 1, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.True(flow.Reconnecting);
        Assert.False(flow.StatsOpen);
    }

    [Fact]
    public void AnAutomaticReconnect_GoesBackIntoTheGame()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        flow.Update(UiConnection.Connecting, true, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.True(flow.Reconnecting);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        Assert.False(flow.Reconnecting);
    }

    [Fact]
    public void ADisconnectThatIsNotRetried_OffersRetryAndTitle()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.False(flow.Reconnecting);

        flow.ConnectRequested();   // "retry"
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        flow.LeaveRequested();     // "to title"
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    // A refused connect (version, full server), an unreachable server and a full match (the server answers MatchFull,
    // then closes) all end the connecting screen the same way; UiText says which.
    [Fact]
    public void AFailedConnect_OrAFullMatch_ShowsDisconnected()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.Update(UiConnection.Offline, false, 0, false, Playing);   // Connect failed at once (bad address)
        Assert.Equal(UiScreen.Disconnected, flow.Screen);

        flow.ConnectRequested();
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);   // connected, the join refused (MatchFull)
        flow.Update(UiConnection.Offline, false, 0, false, Playing);      // the server closes it
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
    }

    [Fact]
    public void TheResult_OpensOnce_AndClosesWithTheNextRound()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Closing);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.WaitingForPlayers);
        Assert.Equal(UiScreen.InGame, flow.Screen);

        // The same result does not open it again; the next one does, and Starting closes it too.
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Starting);
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    // A resume during Finished: the server sends JoinMatchResponse and MatchResult in the same tick, so the flow sees the
    // join and the new result together. It goes straight to the result instead of losing it.
    [Fact]
    public void AnAutomaticReconnect_DuringFinished_GoesToTheResult()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        flow.Update(UiConnection.Connecting, true, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
    }

    [Fact]
    public void AConnectThatJoinsWithAResult_GoesToTheResult()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
    }

    // Only a count above the last one is a new result: a count that goes down (a new client object) opens nothing.
    [Fact]
    public void AResultCountThatGoesDown_OpensNothing()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.ContinuePressed();
        flow.Update(UiConnection.Joined, false, 1, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        flow.Update(UiConnection.Joined, false, 2, true, Playing);   // above 1: new
        Assert.Equal(UiScreen.Result, flow.Screen);
    }

    // "To title" is not offered while reconnecting, but a leave (UiRoot also stops the reconnect) must still win over
    // whatever the reconnect does afterwards.
    [Fact]
    public void LeaveWhileReconnecting_GoesToTheTitle_AndStaysThere()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        Assert.True(flow.Reconnecting);
        flow.LeaveRequested();
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    [Fact]
    public void Escape_DoesNothing_WhileConnecting_OrDisconnected()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        int version = flow.Version;
        flow.EscapePressed();
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        Assert.Equal(version, flow.Version);

        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        version = flow.Version;
        flow.EscapePressed();
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.Equal(version, flow.Version);
    }

    [Fact]
    public void TheResult_ClosesByContinueOrEscape_AndOpensOverTheMenu()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();   // menu open when the match ends
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.OpenStats();
        Assert.True(flow.StatsOpen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.ContinuePressed();   // "keep spectating"
        Assert.Equal(UiScreen.InGame, flow.Screen);

        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Finished);
        flow.EscapePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void DisconnectFromTheMenu_GoesToTheTitle_AndStaysThere()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();
        flow.LeaveRequested();
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);    // the disconnect is not processed yet
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    [Fact]
    public void CancelWhileConnecting_GoesToTheTitle()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.LeaveRequested();
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    // D5: only the bare game screen lets a click lock the cursor and lets input through.
    [Theory]
    [InlineData(UiScreen.Title, false)]
    [InlineData(UiScreen.Connecting, false)]
    [InlineData(UiScreen.InGame, true)]
    [InlineData(UiScreen.Menu, false)]
    [InlineData(UiScreen.Disconnected, false)]
    [InlineData(UiScreen.Result, false)]
    public void CursorAndInput_FollowTheScreen(UiScreen screen, bool game)
    {
        var flow = new UiFlow();
        switch (screen)
        {
            case UiScreen.Connecting: flow.ConnectRequested(); break;
            case UiScreen.InGame: flow = InGame(); break;
            case UiScreen.Menu: flow = InGame(); flow.EscapePressed(); break;
            case UiScreen.Disconnected: flow = InGame(); flow.Update(UiConnection.Offline, false, 0, false, Playing); break;
            case UiScreen.Result: flow = InGame(); flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished); break;
        }
        Assert.Equal(screen, flow.Screen);
        Assert.Equal(game, flow.AllowCursorLock);
        Assert.Equal(!game, flow.BlocksGameInput);
    }

    [Fact]
    public void Version_ChangesOnlyWhenSomethingShownChanges()
    {
        UiFlow flow = InGame();
        int version = flow.Version;
        for (int i = 0; i < 10; i++) flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(version, flow.Version);
        flow.EscapePressed();
        Assert.NotEqual(version, flow.Version);
        version = flow.Version;
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        int afterDrop = flow.Version;
        Assert.NotEqual(version, afterDrop);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);   // the reconnect gave up
        Assert.NotEqual(afterDrop, flow.Version);
    }

    [Fact]
    public void StatsWait_WaitsFiveSeconds_AndReusesARecentRequest()
    {
        Assert.Equal(StatsWaitState.NoAnswer, StatsWait.Of(10f, -1f, -1f));     // nothing could be sent
        Assert.Equal(StatsWaitState.Waiting, StatsWait.Of(10f, 8f, -1f));
        Assert.Equal(StatsWaitState.NoAnswer, StatsWait.Of(13f, 8f, -1f));
        Assert.Equal(StatsWaitState.Answered, StatsWait.Of(9f, 8f, 8.2f));
        Assert.Equal(StatsWaitState.Waiting, StatsWait.Of(20f, 19f, 8.2f));     // an older answer does not count

        Assert.True(StatsWait.MaySend(0f, -1f));
        Assert.False(StatsWait.MaySend(10f, 8f));
        Assert.True(StatsWait.MaySend(10.5f, 8f));
    }
}
