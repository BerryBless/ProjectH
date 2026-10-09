using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 14 (spec "검증 계획", Server): teams, friendly fire, knock-downs and bleeding, squad wipes and team placements,
// revives, reboot cards and stations, through Match.Tick with real inputs.
public class SquadMatchTests
{
    // 기능: TeamSize 2, 4명(1·2 = 팀 1, 3·4 = 팀 2), 넓은 자기장으로 경기를 시작하고 광장에 세운다.
    // 입력: a1·a2·b1·b2 - 입장한 플레이어, sink - 경기 기록 Sink, loadout - 시작 장비(null = 전투 장비).
    // 출력: 경기가 진행 중인 RoyaleHarness.
    // Duo with four players: peers 1, 2 = team 1, peers 3, 4 = team 2 (join order).
    private static RoyaleHarness Duo(out PlayerEntity a1, out PlayerEntity a2, out PlayerEntity b1, out PlayerEntity b2,
        Action<MatchRecord>? sink = null, StartingLoadout? loadout = null)
    {
        var h = new RoyaleHarness(loadout ?? TestGameData.CombatLoadout, WideZone, teamSize: 2, matchSink: sink);
        a1 = h.Join(1);
        a2 = h.Join(2);
        b1 = h.Join(3);
        b2 = h.Join(4);
        h.RunToMatch();
        h.Place(a1, P(0f, 0f));
        h.Place(a2, P(3f, 0f));
        h.Place(b1, P(0f, 8f));
        h.Place(b2, P(-3f, 8f));
        return h;
    }

    // 기능: 맵 지형 높이 위의 점을 만든다.
    // 입력: x - X 좌표, z - Z 좌표.
    // 출력: (x, 지형 높이, z) 위치.
    private static Vector3 P(float x, float z) => SandboxHarness.Ground(x, z);

    // A circle over the whole map that waits 10 minutes: no zone damage (it would cancel revives and reboots far out).
    private const string WideZone = """
        {
          "initialCenter": [0, 0],
          "initialRadius": 200,
          "arenaHalfSize": 80,
          "phases": [ {"waitSeconds":600,"shrinkSeconds":10,"targetRadius":0,"damagePerSecond":1} ]
        }
        """;


    // 기능: 대상의 몸 가운데(기절이면 0.45 m)를 겨눠 한 Tick 쏜다.
    // 입력: h - 경기, shooter - 사수, target - 대상.
    // 출력: 반환값 없음. 한 Tick이 진행된다.
    // Fires one tick at the target's body middle (a downed body is 0.9 m tall).
    private static void ShootAt(RoyaleHarness h, PlayerEntity shooter, PlayerEntity target)
    {
        float height = target.IsDowned ? 0.45f : 1.2f;
        TestAim.YawPitch(shooter.State.Position, target.State.Position + new Vector3(0f, height, 0f), out float yaw, out float pitch);
        h.Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = h.Match.ServerTick });
        h.Match.Tick();
    }

    // 기능: 조건이 맞을 때까지 쏜다.
    // 입력: h - 경기, shooter·target - 사수·대상, done - 끝 조건, maxTicks - 최대 Tick.
    // 출력: 반환값 없음. 조건이 맞지 않으면 실패.
    private static void ShootUntil(RoyaleHarness h, PlayerEntity shooter, PlayerEntity target, Func<bool> done, int maxTicks = 200)
    {
        for (int i = 0; i < maxTicks && !done(); i++) ShootAt(h, shooter, target);
        Assert.True(done(), "condition not reached by shooting");
    }

    // 기능: ticks 동안 InteractHeld(와 extra)를 보내며 Tick을 돌린다.
    // 입력: h - 경기, player - 누르는 사람, ticks - Tick 수, extra - 함께 누를 버튼.
    // 출력: 반환값 없음.
    private static void Hold(RoyaleHarness h, PlayerEntity player, int ticks, InputButtons extra = InputButtons.None)
    {
        for (int i = 0; i < ticks; i++)
        {
            h.Send(player, new InputCommand { Buttons = InputButtons.InteractHeld | extra });
            h.Match.Tick();
        }
    }

    // Body readers of recorded packets (a refused body fails the test).
    // 기능: 송신 기록을 PlayerDowned로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 PlayerDowned.
    private static PlayerDowned ReadPlayerDowned(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(PlayerDowned.TryRead(ref r, out PlayerDowned v)); return v; }
    // 기능: 송신 기록을 HitConfirmed로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 HitConfirmed.
    private static HitConfirmed ReadHitConfirmed(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(HitConfirmed.TryRead(ref r, out HitConfirmed v)); return v; }
    // 기능: 송신 기록을 MatchResult로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 MatchResult.
    private static MatchResult ReadMatchResult(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(MatchResult.TryRead(ref r, out MatchResult v)); return v; }
    // 기능: 송신 기록을 ChannelState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 ChannelState.
    private static ChannelState ReadChannelState(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(ChannelState.TryRead(ref r, out ChannelState v)); return v; }
    // 기능: ItemSpawned 송신 기록을 WorldItemData로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 WorldItemData.
    private static WorldItemData ReadWorldItemData(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(ItemSpawnedPacket.TryRead(ref r, out WorldItemData v)); return v; }
    // 기능: 송신 기록을 InventoryState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 InventoryState.
    private static InventoryState ReadInventoryState(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(InventoryState.TryRead(ref r, out InventoryState v)); return v; }
    // 기능: 송신 기록을 RebootStationsState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 RebootStationsState.
    private static RebootStationsState ReadRebootStationsState(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(RebootStationsState.TryRead(ref r, out RebootStationsState v)); return v; }
    // 기능: 송신 기록을 TeamState로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 TeamState.
    private static TeamState ReadTeamState(RoyaleHarness.Sent s) { var r = RoyaleHarness.Reader(s); Assert.True(TeamState.TryRead(ref r, out TeamState v)); return v; }

    // 기능: 특정 Peer에게 마지막으로 보낸 TeamState를 읽는다. 하나도 없으면 예외로 테스트가 실패한다.
    // 입력: h - 경기, peer - 받은 Peer ID.
    // 출력: 읽은 TeamState.
    private static TeamState LastTeamState(RoyaleHarness h, int peer) => ReadTeamState(h.SentTo(peer, PacketId.TeamState).Last());

    // ---- D1 teams ----

    [Fact]
    public void Teams_AreMadeInJoinOrder_TeamSizeEach()
    {
        var h = Duo(out var a1, out var a2, out var b1, out var b2);
        Assert.Equal(1, a1.TeamId);
        Assert.Equal(1, a2.TeamId);
        Assert.Equal(2, b1.TeamId);
        Assert.Equal(2, b2.TeamId);
        Assert.Equal(2, h.Match.Flow.Teams);
        Assert.True(a1.JoinOrder < a2.JoinOrder && a2.JoinOrder < b1.JoinOrder);
        Assert.True(Match.SameTeam(a1, a2));
        Assert.False(Match.SameTeam(a1, b1));
    }

    [Theory]
    [InlineData(2, 2, new byte[] { 1, 2 })]            // Duo with two players: 1:1
    [InlineData(4, 3, new byte[] { 1, 1, 2 })]         // Squad with three: 2:1
    [InlineData(1, 3, new byte[] { 1, 2, 3 })]         // Solo: all different
    [InlineData(2, 5, new byte[] { 1, 1, 2, 2, 3 })]   // Duo, five: the last team is short
    public void Teams_AreAlwaysTwoOrMore(int teamSize, int players, byte[] expected)
    {
        var h = new RoyaleHarness(teamSize: teamSize);
        var joined = new List<PlayerEntity>();
        for (int i = 1; i <= players; i++) joined.Add(h.Join(i));
        h.RunToMatch();
        Assert.Equal(expected, joined.Select(p => p.TeamId).ToArray());
        Assert.Equal(expected.Max(), h.Match.Flow.Teams);
    }

    [Fact]
    public void ALateSpectator_AndTheLobby_HaveNoTeam()
    {
        var h = new RoyaleHarness(teamSize: 2);
        PlayerEntity a = h.Join(1);
        Assert.Equal(0, a.TeamId);   // the lobby
        h.Join(2);
        h.RunToMatch();
        PlayerEntity late = h.Join(3);
        Assert.Equal(0, late.TeamId);
        Assert.False(Match.SameTeam(late, late));   // team 0 is nobody's team, not even its own
        Assert.Empty(h.SentTo(3, PacketId.TeamState));
    }

    [Fact]
    public void ServerOptions_TeamSize_Is1To4()
    {
        Assert.Null(new ServerOptions { TeamSize = 1 }.Validate());
        Assert.Null(new ServerOptions { TeamSize = 4 }.Validate());
        Assert.NotNull(new ServerOptions { TeamSize = 0 }.Validate());
        Assert.NotNull(new ServerOptions { TeamSize = 5 }.Validate());
    }

    // ---- D2 TeamState ----

    [Fact]
    public void TeamState_GoesToEachMember_OwnTeamOnly()
    {
        var h = Duo(out var a1, out var a2, out var b1, out _);
        h.Ticks(1);
        TeamState mine = LastTeamState(h, 1);
        Assert.Equal(1, mine.TeamId);
        Assert.Equal(2, mine.Count);
        Assert.Equal(a1.EntityId, mine.Get(0).EntityId);
        Assert.Equal(a2.EntityId, mine.Get(1).EntityId);
        Assert.Equal(TeamMemberState.Up, mine.Get(0).State);
        Assert.Equal(100, mine.Get(0).Health);
        TeamState theirs = LastTeamState(h, 3);
        Assert.Equal(2, theirs.TeamId);
        Assert.DoesNotContain(Enumerable.Range(0, theirs.Count), i => theirs.Get(i).EntityId == a1.EntityId);
        Assert.Equal(b1.EntityId, theirs.Get(0).EntityId);
    }

    [Fact]
    public void TeamState_IsSentOnlyWhenItChanges_HealthRoundedUpToTens()
    {
        var h = Duo(out var a1, out _, out _, out _);
        h.Ticks(2);
        int before = h.SentTo(1, PacketId.TeamState).Count;
        h.Ticks(5);
        Assert.Equal(before, h.SentTo(1, PacketId.TeamState).Count);   // nothing changed
        a1.Health = 95;   // rounds to 100: no change
        h.Ticks(1);
        Assert.Equal(before, h.SentTo(1, PacketId.TeamState).Count);
        a1.Health = 81;   // rounds to 90
        h.Ticks(1);
        Assert.Equal(before + 1, h.SentTo(1, PacketId.TeamState).Count);
        Assert.Equal(90, LastTeamState(h, 1).Get(0).Health);
        Assert.Equal(0, Match.RoundHealth(0));
        Assert.Equal(10, Match.RoundHealth(1));
    }

    // ---- D3 friendly fire ----

    [Fact]
    public void AShot_PassesThroughATeammate_AndHitsTheEnemyBehind()
    {
        var h = Duo(out var a1, out var a2, out var b1, out _);
        h.Place(a1, P(0f, 0f));
        h.Place(a2, P(0f, 3f));   // in the line of fire
        h.Place(b1, P(0f, 8f));
        ShootAt(h, a1, b1);
        Assert.Equal(100, a2.Health);
        Assert.Equal(TestGameData.LoadoutShield, a2.Shield);
        Assert.True(b1.Health + b1.Shield < 100 + TestGameData.LoadoutShield);
    }

    // ---- D4, D5 knock-down ----

    [Fact]
    public void AFatalHit_WithAStandingTeammate_KnocksDown()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        Assert.True(a1.Alive);
        Assert.Equal(MovementMode.Downed, a1.State.Mode);
        Assert.Equal(0, a1.Shield);
        Assert.True(a1.Health > 90);   // downedHealth 100, a tick or two of bleed at most
        Assert.Same(b1, a1.DownedBy);
        PlayerDowned downed = ReadPlayerDowned(h.SentTo(4, PacketId.PlayerDowned).Single());
        Assert.Equal(a1.EntityId, downed.VictimId);
        Assert.Equal(b1.EntityId, downed.AttackerId);
        HitConfirmed last = ReadHitConfirmed(h.SentTo(3, PacketId.HitConfirmed).Last());
        Assert.False(last.Killed);   // a knock-down is not a kill
        Assert.Empty(h.SentTo(1, PacketId.PlayerDied));
        Assert.Equal(4, h.Match.Flow.Alive);   // a downed player counts as alive
    }

    [Fact]
    public void Solo_HasNoKnockDown()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Place(a, P(0f, 0f));
        h.Place(b, P(0f, 8f));
        h.Place(c, P(8f, 8f));
        h.ShootUntilDead(b, a);
        Assert.Equal(0, h.Match.Downs);
        Assert.Equal(3, a.Placement);
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.PlayerDowned);
    }

    [Fact]
    public void Downed_BlocksEveryAction()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        int mag = a1.Inventory.Current.MagAmmo;
        int shots = h.Packets.Count(s => s.Id == PacketId.ShotFired);
        TestAim.YawPitch(a1.State.Position, b1.State.Position, out float yaw, out float pitch);
        for (int i = 0; i < 10; i++)
        {
            h.Send(a1, new InputCommand { Buttons = InputButtons.Fire | InputButtons.Reload | InputButtons.Drop | InputButtons.UseMedkit, AimYaw = yaw, AimPitch = pitch });
            h.Match.Tick();
        }
        Assert.Equal(mag, a1.Inventory.Current.MagAmmo);
        Assert.Equal(shots, h.Packets.Count(s => s.Id == PacketId.ShotFired));
        Assert.False(a1.Inventory.Current.IsEmpty);   // no drop
    }

    [Fact]
    public void Downed_Crawls()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        Vector3 from = a1.State.Position;
        for (int i = 0; i < 30; i++)
        {
            h.Send(a1, new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint | InputButtons.Jump });
            h.Match.Tick();
        }
        float moved = Vector3.Distance(from, a1.State.Position);
        Assert.InRange(moved, 1.2f, 1.6f);   // 1.5 m/s, no sprint
        Assert.True(a1.IsDowned);
    }

    [Fact]
    public void Bleeding_TakesTheDownedHealth_InBleedOutSeconds_AndTheKnockerGetsTheKill()
    {
        var h = Duo(out var a1, out _, out var b1, out _);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        int killsBefore = b1.Kills;
        uint bleed = h.Match.Squad.BleedOutTicks;
        h.Ticks((int)bleed / 2);
        Assert.InRange(a1.Health, 45, 55);
        h.Ticks((int)bleed);
        Assert.False(a1.Alive);
        Assert.Equal(killsBefore + 1, b1.Kills);
        Assert.Equal(1, h.Match.BleedOuts);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(1, PacketId.PlayerDied).Single());
        Assert.Equal(b1.EntityId, died.KillerId);
        Assert.True(died.Placement >= 1);   // the team survives: the teams left (leader rule: never 0 for a participant)
        Assert.Equal(2, died.Placement);
    }

    [Fact]
    public void Damage_ToADownedPlayer_LowersItsHealth_AndTheFinisherGetsTheKill()
    {
        var h = Duo(out var a1, out _, out var b1, out var b2);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        int health = a1.Health;
        ShootAt(h, b2, a1);
        Assert.True(a1.Health < health);
        Assert.Equal(0, a1.Shield);
        ShootUntil(h, b2, a1, () => !a1.Alive);
        Assert.Equal(1, b2.Kills);
        Assert.Equal(0, b1.Kills);
        HitConfirmed last = ReadHitConfirmed(h.SentTo(4, PacketId.HitConfirmed).Last());
        Assert.True(last.Killed);
    }

    [Fact]
    public void QaKillPlayer_Eliminates_WithoutAKnockDown_AndQaDamage_KnocksDown()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        Assert.True(h.Match.DamagePlayer(a1, 500, out bool killed));
        Assert.False(killed);
        Assert.True(a1.IsDowned);
        Assert.True(h.Match.KillPlayer(a2));   // the last standing member: the team is wiped, a1 too
        Assert.False(a1.Alive);
        Assert.False(a2.Alive);
        Assert.Equal(a1.Placement, a2.Placement);
        Assert.Equal(2, a1.Placement);
    }

    [Fact]
    public void ZoneAndFall_GoThroughTheSameFatalPath()
    {
        var h = Duo(out var a1, out _, out _, out _);
        a1.Health = 1;
        a1.Shield = 0;
        // A fall from high enough to hurt: the landing knocks it down (a teammate stands).
        a1.State.Position = a1.State.Position + new Vector3(0f, 30f, 0f);
        for (int i = 0; i < 120 && !a1.IsDowned; i++) h.Match.Tick();
        Assert.True(a1.IsDowned);
        Assert.Equal(DeathCause.Fall, a1.DownedCause);
        Assert.Null(a1.DownedBy);
    }

    [Theory]
    [InlineData(MovementMode.Glide)]
    [InlineData(MovementMode.Freefall)]
    public void KnockedDown_InTheAir_SurvivesTheLanding_StillDowned(MovementMode airMode)
    {
        var h = Duo(out var a1, out _, out _, out _);
        a1.State.Position = a1.State.Position + new Vector3(0f, 30f, 0f);
        a1.State.Mode = airMode;
        Assert.True(h.Match.DamagePlayer(a1, 500, out bool killed));
        Assert.False(killed);
        Assert.True(a1.IsDowned);
        for (int i = 0; i < 300 && a1.State.Position.Y > SandboxHarness.Ground(0f, 0f).Y + 0.5f; i++) h.Match.Tick();
        h.Ticks(5);
        Assert.True(a1.Alive, "a knock-down in the air must not end in a fall elimination");
        Assert.True(a1.IsDowned);
        Assert.InRange(a1.Health, 80, 100);   // only bleed, no fall damage
        Assert.False(a1.DownedInAir);         // used up by the landing
    }

    [Fact]
    public void Downed_CrawlingOffALedge_StillTakesFallDamage()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Assert.True(h.Match.DamagePlayer(a1, 500, out _));
        Assert.True(a1.IsDowned);
        // Downed on the ground, then moved up as if it had crawled off a ledge: the landing still hurts (D6).
        a1.State.Position = a1.State.Position + new Vector3(0f, 30f, 0f);
        for (int i = 0; i < 120 && a1.Alive; i++) h.Match.Tick();
        Assert.False(a1.Alive);
    }

    // ---- D6 squad wipe and placements ----

    [Fact]
    public void TheLastStandingMember_Falling_EliminatesTheDowned_SameTick_SamePlacement()
    {
        var records = new List<MatchRecord>();
        var h = Duo(out var a1, out var a2, out var b1, out var b2, records.Add);
        ShootUntil(h, b1, a1, () => a1.IsDowned);
        ShootUntil(h, b1, a2, () => !a2.IsUp);
        Assert.False(a2.Alive);   // no standing teammate: eliminated, not knocked down
        Assert.False(a1.Alive);   // and the downed teammate goes in the same tick
        Assert.Equal(a1.EliminatedTick, a2.EliminatedTick);
        Assert.Equal(2, a1.Placement);
        Assert.Equal(2, a2.Placement);
        Assert.Equal(2, b1.Kills);   // a2 and, as the knocker, a1
        Assert.True(h.Match.Flow.ShouldFinish || h.Match.Flow.State == MatchFlowState.Finished);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, b1.Placement);
        Assert.Equal(1, b2.Placement);

        MatchResult loser = ReadMatchResult(h.SentTo(1, PacketId.MatchResult).Single());
        Assert.Equal(2, loser.Participants);   // teams
        Assert.Equal(2, loser.Placement);
        MatchResult winner = ReadMatchResult(h.SentTo(4, PacketId.MatchResult).Single());
        Assert.Equal(1, winner.Placement);
        Assert.Equal(Math.Min(b1.EntityId, b2.EntityId), winner.WinnerId);
        Assert.Equal(Math.Min(b1.EntityId, b2.EntityId), h.Match.WinnerId);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(new byte[] { 2, 2, 1, 1 }, record.Players.Select(p => p.Placement).ToArray());
    }

    [Fact]
    public void AnEliminatedMemberOfTheWinningTeam_IsFirstToo()
    {
        var h = Duo(out var a1, out var a2, out var b1, out var b2);
        Assert.True(h.Match.KillPlayer(a1));   // a2 still stands: a1 is out, its team is in
        Assert.Equal(2, a1.Placement);         // provisional: the teams left
        Assert.True(h.Match.KillPlayer(b1));
        Assert.True(h.Match.KillPlayer(b2));
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, a1.Placement);
        Assert.Equal(1, a2.Placement);
        Assert.Equal(2, b1.Placement);
        Assert.Equal(a1.EntityId, h.Match.WinnerId);   // the smallest id of the winning team
    }

    [Fact]
    public void ADownedMember_KeepsTheTeamIn_TheMatchGoesOn()
    {
        var h = Duo(out var a1, out _, out var b1, out var b2);
        Assert.True(h.Match.KillPlayer(b1));
        Assert.True(h.Match.DownPlayer(a1));
        h.Ticks(2);
        Assert.True(h.Match.Flow.InMatch);
        Assert.Equal(2, h.Match.Flow.TeamsAlive);
        Assert.Equal(3, h.Match.Flow.Alive);   // players: a1 (downed), a2, b2
    }

    [Fact]
    public void TheLastStandingMember_Leaving_EliminatesTheDownedTeammate()
    {
        var records = new List<MatchRecord>();
        var h = Duo(out var a1, out var a2, out _, out _, records.Add);
        Assert.True(h.Match.DownPlayer(a1));
        h.Match.Leave(a2.PeerId);
        Assert.False(a1.Alive);
        Assert.Equal(2, a1.Placement);
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        MatchRecord record = Assert.Single(records);
        Assert.Equal(2, record.Players.Single(p => p.DevPlayerId == "p2").Placement);   // the leaver's record has the team's
    }

    [Fact]
    public void TheLastStandingMember_GraceEnding_EliminatesTheDownedTeammate()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        Assert.True(h.Match.DownPlayer(a1));
        Assert.True(h.Match.Disconnect(a2.PeerId, allowGrace: true));
        h.Ticks(5);
        Assert.True(a1.IsDowned);   // a graced player still stands for its team
        h.Ticks(30 * 10 + 2);       // the grace ends (10 s)
        Assert.False(a1.Alive);
    }

    // ---- D8 revive ----

    [Fact]
    public void Revive_HoldingE_ForReviveSeconds_StandsTheTargetUp()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Place(a2, a1.State.Position + new Vector3(1f, 0f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        Hold(h, a2, 5);
        Assert.True(a2.ChannelActive);
        Assert.Same(a2, a1.RevivedBy);
        int health = a1.Health;
        Hold(h, a2, 30);
        Assert.Equal(health, a1.Health);   // the bleed pauses during the revive
        ChannelState start = ReadChannelState(h.SentTo(1, PacketId.ChannelState).First());
        Assert.True(start.Active);
        Assert.Equal(ChannelKind.Revive, start.Kind);
        Assert.Equal(a2.EntityId, start.ActorId);
        Assert.Equal(a1.EntityId, start.Target);
        Assert.DoesNotContain(h.Packets, s => s.PeerId == 3 && s.Id == PacketId.ChannelState);   // the other team is not told

        Hold(h, a2, (int)h.Match.Squad.ReviveTicks);
        Assert.True(a1.IsUp);
        Assert.Equal(MovementMode.Ground, a1.State.Mode);
        Assert.Equal(h.Match.Squad.ReviveHealth, a1.Health);
        Assert.Equal(0, a1.Shield);
        Assert.False(a2.ChannelActive);
        Assert.Equal(1, h.Match.Revives);
        ChannelState end = ReadChannelState(h.SentTo(1, PacketId.ChannelState).Last());
        Assert.False(end.Active);
    }

    [Fact]
    public void Revive_NeedsATeammate_InRange_InSight()
    {
        var h = Duo(out var a1, out var a2, out _, out var b2);
        Assert.True(h.Match.DownPlayer(a1));
        h.Place(b2, a1.State.Position + new Vector3(-1f, 0f, 0f));
        Hold(h, b2, 3);
        Assert.False(b2.ChannelActive);   // an enemy cannot revive
        h.Place(a2, a1.State.Position + new Vector3(2.5f, 0f, 0f));
        Hold(h, a2, 3);
        Assert.False(a2.ChannelActive);   // out of range (2 m)

        // A house wall (0.5 m) between them, 1.6 m apart: no line of sight.
        Vector3 wallSide = SandboxHarness.Ground(-56f, -42.8f);
        Vector3 otherSide = SandboxHarness.Ground(-56f, -41.2f);
        Assert.Contains(GameMap.Boxes.ToArray(), b => b.Min.Z <= -42.1f && b.Max.Z >= -41.9f && b.Min.X <= -56f && b.Max.X >= -56f);
        h.Place(a1, wallSide);
        h.Place(a2, otherSide);
        Hold(h, a2, 3);
        Assert.False(a2.ChannelActive);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("distance")]
    [InlineData("damage")]
    [InlineData("fire")]
    [InlineData("slot")]
    [InlineData("targetOut")]
    public void Revive_IsCancelled(string by)
    {
        var h = Duo(out var a1, out var a2, out _, out var b2);
        h.Place(a2, a1.State.Position + new Vector3(1f, 0f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        Hold(h, a2, 10);
        Assert.True(a2.ChannelActive);
        switch (by)
        {
            case "release":
                h.Send(a2, new InputCommand());
                h.Match.Tick();
                break;
            case "distance":
                h.Place(a2, a1.State.Position + new Vector3(2.6f, 0f, 0f));   // past reviveRange + 0.5
                Hold(h, a2, 1);
                break;
            case "damage":
                h.Match.DamagePlayer(a2, 5, out _);
                break;
            case "fire":
                Hold(h, a2, 1, InputButtons.Fire);
                break;
            case "slot":
                Hold(h, a2, 1, InputButtons.Slot2);
                break;
            case "targetOut":
                h.Match.KillPlayer(a1);
                break;
        }
        Assert.False(a2.ChannelActive);
        Assert.Null(a1.RevivedBy);
        Assert.False(a1.IsUp);
        Assert.True(h.Match.ChannelsCancelled >= 1);
        ChannelState end = ReadChannelState(h.SentTo(2, PacketId.ChannelState).Last());
        Assert.False(end.Active);
    }

    [Fact]
    public void Revive_AReviverWhoseConnectionDrops_IsCancelled_ATargetWhoseConnectionDrops_GoesOn()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Place(a2, a1.State.Position + new Vector3(1f, 0f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        Hold(h, a2, 5);
        Assert.True(h.Match.Disconnect(a1.PeerId, allowGrace: true));   // the target drops: it goes on
        Hold(h, a2, 5);
        Assert.True(a2.ChannelActive);
        Assert.True(h.Match.Disconnect(a2.PeerId, allowGrace: true));   // the reviver drops: cancelled at once
        h.Match.Tick();
        Assert.False(a2.ChannelActive);
    }

    // 기능: (7.5, -7.5) 칸에 0단 경사면(+Z로 오르는)과 1단 바닥을 PlacePiece로 놓는다. 배치가 거부되면 테스트를 실패시킨다.
    // 입력: h - 경기, center - 놓은 경사면 칸의 중심(출력).
    // 출력: 반환값 없음. 경기 월드에 경사면과 바닥이 추가되고 center가 채워진다.
    // A ramp (rising towards +Z over the 5 m cell, 0 to 3 m) under a level-1 floor (its underside at 2.75 m). The downed
    // body settles on the ramp's highest point under it.
    private static void RampUnderFloor(RoyaleHarness h, out Vector3 center)
    {
        Vector3 spot = P(7.5f, -7.5f);
        int cx = BuildGrid.CellX(spot.X), cz = BuildGrid.CellZ(spot.Z);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Ramp, cx, 0, cz, 0, out BuildPieceShape ramp));
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(ramp, BuildMaterialType.Wood, out _));
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Floor, cx, 1, cz, 0, out BuildPieceShape floor));
        Assert.Equal(BuildResultCode.Ok, h.Match.PlacePiece(floor, BuildMaterialType.Wood, out _));
        center = BuildGrid.CenterOf(ramp);
    }

    // 기능: 경사면 위 높이 surface인 지점에 a1을 기절시켜 두고, a2가 1.5 m 아래쪽에서 E를 누르고 있게 한다.
    // 입력: h - 경기, a1·a2 - 같은 팀, center - 경사면 칸 중심, surface - 경사면 높이(0..3).
    // 출력: 반환값 없음.
    private static void DownOnRamp(RoyaleHarness h, PlayerEntity a1, PlayerEntity a2, Vector3 center, float surface)
    {
        float z = center.Z - BuildGrid.CellSize / 2f + surface / 3f * BuildGrid.CellSize;
        h.Place(a1, new Vector3(center.X, surface, z));
        Assert.True(h.Match.DownPlayer(a1));
        h.Ticks(2);   // settles on the ramp
        float lowerZ = z - 1.5f;
        float lowerY = (lowerZ - (center.Z - BuildGrid.CellSize / 2f)) / BuildGrid.CellSize * 3f + 0.25f;
        h.Place(a2, new Vector3(center.X, lowerY, lowerZ));
    }

    [Fact]
    public void Revive_UnderALowCeiling_StandsUpCrouched()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        RampUnderFloor(h, out Vector3 center);
        DownOnRamp(h, a1, a2, center, 1.1f);   // about 1.45 m up to the floor: no standing, a crouch fits
        for (int i = 0; i < 200 && !a1.IsUp; i++) Hold(h, a2, 1);
        Assert.True(a1.IsUp);
        Assert.Equal(MovementMode.Crouch, a1.State.Mode);   // on the completing tick
        float y = a1.State.Position.Y;
        h.Ticks(5);
        Assert.Equal(MovementMode.Crouch, a1.State.Mode);   // the Shared posture rule keeps it down (no room to stand)
        Assert.True(a1.State.Position.Y < 2f, "it must not be pushed through the floor");
        Assert.InRange(a1.State.Position.Y, y - 0.3f, y + 0.3f);
    }

    [Fact]
    public void Revive_WhereNotEvenACrouchFits_DoesNotStart()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        RampUnderFloor(h, out Vector3 center);
        DownOnRamp(h, a1, a2, center, 1.4f);   // about 1.14 m up to the floor: only the 0.9 m downed body fits
        Hold(h, a2, 10);
        Assert.False(a2.ChannelActive);
        Assert.True(a1.IsDowned);
    }

    [Fact]
    public void E_NearADownedTeammate_DoesNotPickUp()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Place(a2, a1.State.Position + new Vector3(1f, 0f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        ushort item = h.Match.SpawnItem(new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1), a2.State.Position, -1);
        Assert.NotEqual(0, item);
        h.Send(a2, new InputCommand { Buttons = InputButtons.Interact | InputButtons.InteractHeld });
        h.Match.Tick();
        Assert.True(h.Match.WorldItems.IndexOf(item) >= 0);   // still lying there
        Assert.True(a2.ChannelActive);
    }

    // ---- D9 reboot cards ----

    // 기능: 이 주인의 카드 월드 아이템을 찾는다.
    // 입력: m - 경기, owner - 카드 주인.
    // 출력: 아이템 index, 없으면 -1.
    private static int CardIndex(Match m, PlayerEntity owner)
    {
        for (int i = 0; i < m.WorldItems.Count; i++)
        {
            if (m.WorldItems[i].Data.Kind == ItemKind.RebootCard && m.WorldItems[i].CardOwner == owner.JoinOrder) return i;
        }
        return -1;
    }

    [Fact]
    public void AnEliminatedMember_WithTheTeamIn_DropsACard_OnlyItsTeamIsTold()
    {
        var h = Duo(out var a1, out _, out _, out _);
        h.Match.KillPlayer(a1);
        int index = CardIndex(h.Match, a1);
        Assert.True(index >= 0);
        WorldItemData card = h.Match.WorldItems[index].Data;
        Assert.Equal(a1.EntityId, card.Amount);
        Assert.Contains(h.SentTo(2, PacketId.ItemSpawned), s => ReadWorldItemData(s).ItemId == card.ItemId);
        Assert.DoesNotContain(h.SentTo(3, PacketId.ItemSpawned), s => ReadWorldItemData(s).ItemId == card.ItemId);
        Assert.Equal(1, h.Match.CardsDropped);
    }

    [Fact]
    public void ACard_IsPickedUpByTheTeam_NotByAnEnemy()
    {
        var h = Duo(out var a1, out var a2, out var b1, out _);
        h.Match.KillPlayer(a1);
        Vector3 cardAt = h.Match.WorldItems[CardIndex(h.Match, a1)].Data.Position;
        h.Place(b1, cardAt);
        h.Send(b1, new InputCommand { Buttons = InputButtons.Interact });
        h.Match.Tick();
        Assert.True(CardIndex(h.Match, a1) >= 0);
        Assert.Equal(0, b1.Inventory.CardCount);
        h.Place(a2, cardAt);
        h.Send(a2, new InputCommand { Buttons = InputButtons.Interact });
        h.Match.Tick();
        Assert.Equal(-1, CardIndex(h.Match, a1));
        Assert.Equal(1, a2.Inventory.CardCount);
        Assert.Equal(1, ReadInventoryState(h.SentTo(2, PacketId.InventoryState).Last()).RebootCards);
        Assert.Equal(TeamMemberFlags.CardHeld, LastTeamState(h, 2).Get(0).Flags);
    }

    [Fact]
    public void ACard_ExpiresAfterItsLifetime()
    {
        var h = Duo(out var a1, out _, out _, out _);
        h.Match.KillPlayer(a1);
        h.Ticks(1);
        Assert.Equal(TeamMemberFlags.CardDropped, LastTeamState(h, 2).Get(0).Flags);
        h.Ticks((int)h.Match.Squad.CardLifetimeTicks);
        Assert.Equal(-1, CardIndex(h.Match, a1));
        Assert.Equal(1, h.Match.CardsExpired);
        Assert.Equal(TeamMemberFlags.None, LastTeamState(h, 2).Get(0).Flags);
    }

    [Fact]
    public void ACardOwner_Leaving_TakesItsCard_FromTheWorldAndFromAHolder()
    {
        var h = Duo(out var a1, out var a2, out var b1, out var b2);
        h.Match.KillPlayer(a1);
        h.Match.Leave(a1.PeerId);
        Assert.Equal(-1, CardIndex(h.Match, a1));
        Assert.Equal(0, h.Match.WorldItems.CardCount);

        h.Match.KillPlayer(b1);
        Assert.True(h.Match.GiveCard(b2, b1));
        Assert.Equal(1, b2.Inventory.CardCount);
        h.Match.Leave(b1.PeerId);
        Assert.Equal(0, b2.Inventory.CardCount);
    }

    [Fact]
    public void AHolder_Eliminated_DropsTheCard_AndAWipe_RemovesEveryCard()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, teamSize: 4, maxPlayers: 6);
        var team = new List<PlayerEntity>();
        for (int i = 1; i <= 4; i++) team.Add(h.Join(i));
        PlayerEntity enemy1 = h.Join(5);
        PlayerEntity enemy2 = h.Join(6);
        h.RunToMatch();
        Assert.Equal(1, team[3].TeamId);
        h.Match.KillPlayer(team[0]);
        Assert.True(h.Match.GiveCard(team[1], team[0]));
        h.Match.KillPlayer(team[1]);   // team[2] and team[3] still stand: the held card is dropped with team[1]'s own
        Assert.True(CardIndex(h.Match, team[0]) >= 0);
        Assert.True(CardIndex(h.Match, team[1]) >= 0);
        Assert.Equal(2, h.Match.WorldItems.CardCount);
        h.Match.KillPlayer(team[2]);
        h.Match.KillPlayer(team[3]);   // the team is wiped: every card goes, none is dropped
        Assert.Equal(0, h.Match.WorldItems.CardCount);
        Assert.All(team, p => Assert.Equal(2, p.Placement));
        Assert.True(enemy1.Alive && enemy2.Alive);
    }

    [Fact]
    public void Cards_AreNeverEvicted_ByTheWorldItemCap()
    {
        var items = new WorldItems();
        Assert.True(items.TryAdd(ItemKind.RebootCard, 0, 0, 7, Vector3.Zero, -1, out _, out _, cardTeam: 1, cardOwner: 3, expireTick: 100));
        for (int i = 1; i < WorldItems.Capacity; i++) Assert.True(items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out _));
        Assert.True(items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out ushort evicted));
        Assert.NotEqual(0, evicted);
        Assert.Equal(1, items.CardCount);
        // Only the card's own team finds it.
        var lone = new WorldItems();
        lone.TryAdd(ItemKind.RebootCard, 0, 0, 7, Vector3.Zero, -1, out _, out _, cardTeam: 1, cardOwner: 3, expireTick: 100);
        Assert.Equal(-1, lone.FindNearest(Vector3.Zero, 1f, 1f));
        Assert.Equal(-1, lone.FindNearest(Vector3.Zero, 1f, 1f, cardTeam: 2));
        Assert.Equal(0, lone.FindNearest(Vector3.Zero, 1f, 1f, cardTeam: 1));
    }

    [Fact]
    public void CardsAndLootPoints_FitTheWorldItemStore()
    {
        // Cards are never evicted: at most one per participant (MaxPlayers <= MaxSnapshotEntities) beside the loot points'
        // items (never evicted either), and the rest of the store stays for drops.
        Assert.True(LootPoints.All.Length + ProtocolConstants.MaxSnapshotEntities < WorldItems.Capacity);
    }

    [Fact]
    public void ACardHolder_CarriesAtMostMaxCardsHeld()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, teamSize: 4, maxPlayers: 6);
        var team = new List<PlayerEntity>();
        for (int i = 1; i <= 4; i++) team.Add(h.Join(i));
        h.Join(5);
        h.RunToMatch();
        for (int i = 0; i < 3; i++)
        {
            h.Match.KillPlayer(team[i]);
            Assert.True(h.Match.GiveCard(team[3], team[i]));
        }
        Assert.Equal(3, team[3].Inventory.CardCount);
        Assert.Equal(SquadConstants.MaxCardsHeld, h.Match.Squad.MaxCardsHeld);
    }

    // ---- D10 reboot stations ----

    [Fact]
    public void Reboot_AtAStation_BringsTheCardOwnerBack_WithTheRebootLoadout()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Match.KillPlayer(a1);
        Assert.Equal(3, h.Match.Flow.Alive);
        Assert.True(h.Match.GiveCard(a2, a1));
        Vector3 station = RebootStations.All[1];
        h.Place(a2, station + new Vector3(2f, 0f, 0f));
        h.Place(a2, SandboxHarness.Ground(station.X + 2f, station.Z));
        Hold(h, a2, 3);
        Assert.True(a2.ChannelActive);
        Assert.Equal(ChannelKind.Reboot, a2.Channel);
        h.Ticks(0);
        Hold(h, a2, (int)h.Match.Squad.RebootTicks);
        Assert.True(a1.Alive);
        Assert.True(a1.IsUp);
        Assert.Equal(MovementMode.Ground, a1.State.Mode);
        Assert.True(BotLikeDistance(a1.State.Position, station) < 2f);
        Assert.Equal(100, a1.Health);
        Assert.Equal(h.Match.Squad.RebootLoadout.Shield, a1.Shield);
        Assert.Equal(TestWeapons.LightId, a1.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(30, a1.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(0, a1.Placement);
        Assert.Equal(4, h.Match.Flow.Alive);
        Assert.Equal(0, a2.Inventory.CardCount);
        Assert.Equal(1, h.Match.Reboots);
        Assert.True(h.Match.StationEndTick(1) > h.Match.ServerTick);
        h.Ticks(1);
        RebootStationsState stations = ReadRebootStationsState(h.SentTo(3, PacketId.RebootStations).Last());
        Assert.True(stations.IsCoolingDown(1));
        PlayerRespawned back = RoyaleHarness.ReadRespawned(h.SentTo(3, PacketId.PlayerRespawned).Last());
        Assert.Equal(a1.EntityId, back.EntityId);
        Assert.Equal(MovementMode.Ground, back.Mode);
    }

    // 기능: 두 점의 수평(XZ) 거리를 잰다(높이는 무시).
    // 입력: a - 첫 점, b - 둘째 점.
    // 출력: XZ 평면 거리.
    private static float BotLikeDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    [Fact]
    public void Reboot_IsRefused_WithoutACard_OutOfRange_OrCoolingDown()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Match.KillPlayer(a1);
        Vector3 station = RebootStations.All[1];
        h.Place(a2, SandboxHarness.Ground(station.X + 2f, station.Z));
        Hold(h, a2, 3);
        Assert.False(a2.ChannelActive);   // no card
        Assert.True(h.Match.GiveCard(a2, a1));
        h.Place(a2, SandboxHarness.Ground(station.X + 4f, station.Z));
        Hold(h, a2, 3);
        Assert.False(a2.ChannelActive);   // out of range (3 m)
        h.Match.SetStationCooldown(1, 10);
        h.Place(a2, SandboxHarness.Ground(station.X + 2f, station.Z));
        Hold(h, a2, 3);
        Assert.False(a2.ChannelActive);   // cooling down
        h.Match.SetStationCooldown(1, 0);
        Hold(h, a2, 3);
        Assert.True(a2.ChannelActive);
    }

    // ---- D13 reconnect ----

    [Fact]
    public void Resume_SendsTheTeam_TheStations_AndTheTeamsChannel()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Place(a2, a1.State.Position + new Vector3(1f, 0f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        Hold(h, a2, 3);
        Assert.True(h.Match.Disconnect(a1.PeerId, allowGrace: true));
        int bleedHealth = a1.Health;
        Hold(h, a2, 10);
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        Assert.True(a1.IsDowned);
        Assert.NotEmpty(h.SentTo(11, PacketId.TeamState));
        Assert.NotEmpty(h.SentTo(11, PacketId.RebootStations));
        ChannelState channel = ReadChannelState(h.SentTo(11, PacketId.ChannelState).Single());
        Assert.True(channel.Active);
        Assert.Equal(a2.EntityId, channel.ActorId);
        Assert.Equal(bleedHealth, a1.Health);   // no bleed while revived, graced or not
    }

    [Fact]
    public void ADownedPlayer_Graced_KeepsBleeding()
    {
        var h = Duo(out var a1, out _, out _, out _);
        Assert.True(h.Match.DownPlayer(a1));
        Assert.True(h.Match.Disconnect(a1.PeerId, allowGrace: true));
        int health = a1.Health;
        h.Ticks(90);
        Assert.True(a1.Health < health);
        Assert.True(a1.IsDowned);
    }

    [Fact]
    public void ARebootedPlayer_CanBeGraced_AndResume()
    {
        var h = Duo(out var a1, out var a2, out _, out _);
        h.Match.KillPlayer(a1);
        Assert.True(h.Match.GiveCard(a2, a1));
        Vector3 station = RebootStations.All[1];
        h.Place(a2, SandboxHarness.Ground(station.X + 2f, station.Z));
        Hold(h, a2, (int)h.Match.Squad.RebootTicks + 3);
        Assert.True(a1.Alive);
        Assert.True(h.Match.Disconnect(a1.PeerId, allowGrace: true));
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(12, "p1"));
        Assert.True(a1.Alive);
        Assert.Equal(1, LastTeamState(h, 12).TeamId);
    }

    // ---- review fixes: the result screen, dev-mode team ids ----

    [Fact]
    public void OnTheResultScreen_ADownedWinner_DoesNotBleed_NorDie()
    {
        var h = Duo(out var a1, out var a2, out var b1, out var b2);
        Assert.True(h.Match.DownPlayer(a1));
        Assert.True(h.Match.KillPlayer(b1));
        Assert.True(h.Match.KillPlayer(b2));
        h.Ticks(1);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, a1.Placement);
        int health = a1.Health;
        int died = h.SentTo(3, PacketId.PlayerDied).Count;
        h.Ticks(RoyaleHarness.ResultTicks - 2);   // the whole result screen but its last tick (then the round resets)
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(health, a1.Health);
        Assert.True(a1.IsDowned);
        Assert.Equal(1, a1.Placement);
        Assert.Equal(died, h.SentTo(3, PacketId.PlayerDied).Count);   // no PlayerDied { Placement 0 } after the result
        Assert.True(a2.IsUp);
    }

    [Fact]
    public void TheMatchEnding_CancelsAReboot_AndNoneCompletesOnTheResultScreen()
    {
        var h = Duo(out var a1, out var a2, out var b1, out var b2);
        h.Match.KillPlayer(a1);
        Assert.True(h.Match.GiveCard(a2, a1));
        Vector3 station = RebootStations.All[1];
        h.Place(a2, SandboxHarness.Ground(station.X + 2f, station.Z));
        Hold(h, a2, 5);
        Assert.True(a2.ChannelActive);
        h.Match.KillPlayer(b1);
        h.Match.KillPlayer(b2);
        Hold(h, a2, 1);   // the finish
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.False(a2.ChannelActive);
        Hold(h, a2, RoyaleHarness.ResultTicks - 3);
        Assert.False(a2.ChannelActive);
        Assert.False(a1.Alive);
        Assert.Equal(1, a1.Placement);   // the winning team's eliminated member keeps its 1
        Assert.Equal(1, a2.Inventory.CardCount);
        ChannelState end = ReadChannelState(h.SentTo(2, PacketId.ChannelState).Last());
        Assert.False(end.Active);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DevMode_TeamIds_NeverWrapOntoAPlayerHere(int teamSize)
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, TeamSize = teamSize });
        PlayerEntity stay = h.Join(1, P(0f, 0f));
        // Many short visits: the join counter passes 255 (the old formula wrapped there).
        for (int i = 0; i < 600; i++)
        {
            Assert.Equal(JoinResult.Ok, h.Match.TryJoin(100, "visitor"));
            h.Match.Leave(100);
        }
        Assert.True(h.Match.TryJoin(2, "p2") == JoinResult.Ok);
        h.Match.TryGetPlayer(2, out PlayerEntity late);
        Assert.True(late.JoinOrder > 255);
        if (teamSize == 1) Assert.False(Match.SameTeam(stay, late));   // Solo: always different teams
        else Assert.True(Match.SameTeam(stay, late));                  // Duo: fills the team with room
        h.Match.TryJoin(3, "p3");
        h.Match.TryGetPlayer(3, out PlayerEntity third);
        Assert.False(Match.SameTeam(stay, third) && teamSize == 2);    // that team is full now
        Assert.NotEqual(0, third.TeamId);
    }

    // ---- server-hotpath: a squad tick allocates nothing ----

    [Fact]
    public void SquadTicks_AllocateNothing()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, teamSize: 2, record: false);
        PlayerEntity a1 = h.Join(1), a2 = h.Join(2), b1 = h.Join(3);
        h.Join(4);
        h.RunToMatch();
        h.Place(a1, P(0f, 0f));
        h.Place(a2, P(1f, 0f));
        Assert.True(h.Match.DownPlayer(a1));
        h.Match.KillPlayer(b1);
        h.Ticks(60);   // warm up (JIT, first sends)
        long start = GC.GetAllocatedBytesForCurrentThread();
        h.Ticks(60);   // bleeding, a card lying, team states compared every tick
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
