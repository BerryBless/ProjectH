using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using static ProjectH.Server.Tests.Bots.BotTestView;

namespace ProjectH.Server.Tests.Bots;

// Phase 14 D15: bots never target a teammate, still target a knocked-down enemy, and crawl to a teammate when downed.
public class BotSquadTests
{
    // 기능: BotView에 팀 1의 TeamState를 넣는다.
    // 입력: view - 봇 View, members - 구성원 Entity id.
    // 출력: 반환값 없음. view.Team과 HasTeam이 설정된다.
    private static void SetTeam(BotView view, params ushort[] members)
    {
        var team = new TeamState { TeamId = 1, Count = (byte)members.Length };
        for (int i = 0; i < members.Length; i++) team.Set(i, new TeamMember { EntityId = members[i], Health = 100 });
        view.Team = team;
        view.HasTeam = true;
    }

    [Fact]
    public void ATeammate_IsNeverTargeted_AnEnemyIs()
    {
        BotView view = Armed();
        SetTeam(view, 1, 2);
        AddOther(view, 2, new Vector3(0f, 0f, 6f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 3);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
        Assert.True(view.IsTeammate(2));
        Assert.False(view.IsTeammate(1));   // itself
        AddOther(view, 3, new Vector3(6f, 0f, 0f));
        Run(brain, view, 1f, 30);
        Assert.Equal(BotGoal.Fight, brain.Goal);
        Assert.Equal(3, brain.Target);
    }

    [Fact]
    public void AKnockedDownEnemy_StaysATarget_AimedLow()
    {
        BotView view = Armed();
        view.Others[view.OtherCount++] = new SnapshotEntity
        {
            EntityId = 3, Position = new Vector3(0f, 0f, 6f), Flags = SnapshotEntity.MakeFlags(true, MovementMode.Downed, false, false),
        };
        var brain = new BotBrain(1);
        InputCommand[] commands = Run(brain, view, 0f, 5);
        Assert.Equal(BotGoal.Fight, brain.Goal);
        Assert.True(commands[^1].AimPitch > 0f);   // looking down at the low body
        Assert.Equal(BotAim.DownedChestHeight, BotAim.Chest(Vector3.Zero, MovementMode.Downed).Y);
    }

    [Fact]
    public void ADownedBot_CrawlsTowardsItsNearestTeammate_AndNeverFires()
    {
        BotView view = Armed();
        view.MyMode = MovementMode.Downed;
        SetTeam(view, 1, 2);
        AddOther(view, 2, new Vector3(6f, 0f, 0f));   // teammate, east
        AddOther(view, 3, new Vector3(0f, 0f, 5f));   // enemy, close
        var brain = new BotBrain(1);
        InputCommand[] commands = Run(brain, view, 0f, 10);
        Assert.Equal(BotGoal.Crawl, brain.Goal);
        InputCommand last = commands[^1];
        Assert.Equal(1f, last.MoveY);
        Assert.Equal(InputButtons.None, last.Buttons & (InputButtons.Fire | InputButtons.Jump | InputButtons.Sprint));
        Assert.True(AngleBetween(last.Yaw, 90f) < 30f, $"yaw {last.Yaw}");
    }

    [Fact]
    public void ADownedBot_NextToItsTeammate_StopsCrawling()
    {
        BotView view = Armed();
        view.MyMode = MovementMode.Downed;
        SetTeam(view, 1, 2);
        AddOther(view, 2, new Vector3(1f, 0f, 0f));
        var brain = new BotBrain(1);
        InputCommand[] commands = Run(brain, view, 0f, 5);
        Assert.Equal(0f, commands[^1].MoveY);
    }
}
