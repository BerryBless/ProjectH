using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Phase 14 D2, D8: the team as the client keeps it (TeamState) and the revive/reboot channels with their progress,
    // measured on the server tick only.
    public class SquadStateTests
    {
        // 기능: TeamId 2의 시험용 팀 상태를 만든다.
        // 입력: members - 팀원들(차례대로 칸에 들어간다).
        // 출력: 팀원 수만큼 채운 TeamState.
        private static TeamState Team(params TeamMember[] members)
        {
            var team = new TeamState { TeamId = 2, Count = (byte)members.Length };
            for (int i = 0; i < members.Length; i++) team.Set(i, members[i]);
            return team;
        }

        // 기능: 시험용 팀원 정보를 만든다.
        // 입력: id - Entity ID, state - 팀원 상태, health - 체력, flags - 카드 등 플래그.
        // 출력: TeamMember.
        private static TeamMember Member(ushort id, TeamMemberState state = TeamMemberState.Up, byte health = 100,
            TeamMemberFlags flags = TeamMemberFlags.None) =>
            new TeamMember { EntityId = id, State = state, Health = health, Flags = flags };

        // 기능: 시험용 소생·재투입 Channel 상태를 만든다.
        // 입력: actor - 행동하는 플레이어 ID, target - 대상(플레이어 또는 스테이션), end - 끝 Tick, active - 진행 중 여부, kind - Channel 종류.
        // 출력: ChannelState.
        private static ChannelState Channel(ushort actor, ushort target, uint end, bool active, ChannelKind kind = ChannelKind.Revive) =>
            new ChannelState { Kind = kind, ActorId = actor, Target = target, EndTick = end, Active = active };

        [Test]
        public void TeamState_ReadFromThePacket_IsApplied()
        {
            var buffer = new byte[64];
            var writer = new PacketWriter(buffer);
            TeamState.Write(ref writer, Team(Member(3), Member(7, TeamMemberState.Downed, 40), Member(9, TeamMemberState.Eliminated, 0, TeamMemberFlags.CardHeld)));
            var reader = new PacketReader(new System.ReadOnlySpan<byte>(buffer, 0, writer.Length));
            Assert.IsTrue(reader.TryReadPacketId(out PacketId id));
            Assert.AreEqual(PacketId.TeamState, id);
            Assert.IsTrue(TeamState.TryRead(ref reader, out TeamState read));

            var squad = new SquadState();
            squad.Apply(read);

            Assert.IsTrue(squad.HasTeam);
            Assert.AreEqual(2, squad.TeamId);
            Assert.AreEqual(3, squad.Count);
            Assert.AreEqual(7, squad.Member(1).EntityId);
            Assert.AreEqual(40, squad.Member(1).Health);
            Assert.AreEqual(TeamMemberFlags.CardHeld, squad.Member(2).Flags);
            Assert.AreEqual(1, squad.Version);
        }

        [Test]
        public void Membership_AndInPlay()
        {
            var squad = new SquadState();
            Assert.IsFalse(squad.Contains(3));   // no team yet
            squad.Apply(Team(Member(3), Member(7, TeamMemberState.Downed), Member(9, TeamMemberState.Eliminated), Member(11, TeamMemberState.Rebooting)));

            Assert.IsTrue(squad.Contains(3));
            Assert.IsTrue(squad.Contains(11));
            Assert.IsFalse(squad.Contains(4));
            Assert.IsFalse(squad.Contains(0));
            Assert.IsTrue(squad.IsInPlay(3));
            Assert.IsTrue(squad.IsInPlay(7));     // downed is still in play
            Assert.IsFalse(squad.IsInPlay(9));    // eliminated
            Assert.IsFalse(squad.IsInPlay(11));   // being rebooted, still out
        }

        [Test]
        public void Clear_ForgetsTheTeamAndTheChannels()
        {
            var squad = new SquadState();
            squad.Apply(Team(Member(3), Member(7)));
            squad.ApplyChannel(Channel(3, 7, 200, true), 50);
            squad.Clear();

            Assert.IsFalse(squad.HasTeam);
            Assert.AreEqual(0, squad.Count);
            Assert.AreEqual(0, squad.ChannelCount);
            Assert.IsFalse(squad.Contains(3));
            Assert.AreEqual(2, squad.Version);
        }

        [Test]
        public void Channels_StartReplaceAndStop_ByActor()
        {
            var squad = new SquadState();
            squad.ApplyChannel(Channel(3, 7, 200, true), 50);
            squad.ApplyChannel(Channel(5, 1, 300, true, ChannelKind.Reboot), 60);
            Assert.AreEqual(2, squad.ChannelCount);

            squad.ApplyChannel(Channel(3, 9, 400, true), 70);   // the same actor again: replaced
            Assert.AreEqual(2, squad.ChannelCount);
            Assert.IsTrue(squad.TryGetChannelOf(3, out ChannelView c));
            Assert.AreEqual(9, c.Target);
            Assert.AreEqual(70.0, c.StartTick);

            squad.ApplyChannel(Channel(3, 9, 400, false), 80);
            Assert.AreEqual(1, squad.ChannelCount);
            Assert.IsFalse(squad.TryGetChannelOf(3, out _));
            squad.ApplyChannel(Channel(8, 9, 400, false), 80);   // a stop for nobody: nothing
            Assert.AreEqual(1, squad.ChannelCount);
        }

        [Test]
        public void Channels_AreBounded()
        {
            var squad = new SquadState();
            for (ushort actor = 1; actor <= SquadState.MaxChannels + 3; actor++) squad.ApplyChannel(Channel(actor, 20, 500, true), 10);
            Assert.AreEqual(SquadState.MaxChannels, squad.ChannelCount);
        }

        [Test]
        public void ChannelOf_FindsTheActor_AndTheRevivedTarget_ButNotAStationIndex()
        {
            var squad = new SquadState();
            squad.ApplyChannel(Channel(3, 7, 200, true), 50);
            squad.ApplyChannel(Channel(5, 2, 300, true, ChannelKind.Reboot), 50);

            Assert.IsTrue(squad.TryGetChannelOf(7, out ChannelView revived));   // the downed teammate sees it too
            Assert.AreEqual(3, revived.ActorId);
            Assert.IsTrue(squad.TryGetChannelOf(5, out ChannelView reboot));
            Assert.AreEqual(ChannelKind.Reboot, reboot.Kind);
            Assert.IsFalse(squad.TryGetChannelOf(2, out _));   // entity 2 is not station 2
            Assert.IsFalse(squad.TryGetChannelOf(0, out _));
        }

        [Test]
        public void Channel_StartingAfterItsEnd_IsComplete()
        {
            var squad = new SquadState();
            squad.ApplyChannel(Channel(3, 7, 100, true), 150);   // a late resume
            Assert.IsTrue(squad.TryGetChannelOf(3, out ChannelView c));
            Assert.AreEqual(1f, SquadState.Progress(c.StartTick, c.EndTick, 150));
        }

        [Test]
        public void ExpireChannels_DropsOnlyLongFinishedOnes()
        {
            var squad = new SquadState();
            squad.ApplyChannel(Channel(3, 7, 100, true), 0);
            squad.ApplyChannel(Channel(4, 8, 300, true), 0);
            squad.ExpireChannels(100 + 30 * SquadState.ChannelGraceSeconds, 30);   // exactly at the grace: kept
            Assert.AreEqual(2, squad.ChannelCount);
            squad.ExpireChannels(100 + 30 * SquadState.ChannelGraceSeconds + 1, 30);
            Assert.AreEqual(1, squad.ChannelCount);
            Assert.IsTrue(squad.TryGetChannelOf(4, out _));
        }

        [Test]
        public void Progress_IsTheServerTickBetweenStartAndEnd()
        {
            Assert.AreEqual(0f, SquadState.Progress(100, 250, 90));
            Assert.AreEqual(0f, SquadState.Progress(100, 250, 100));
            Assert.AreEqual(0.5f, SquadState.Progress(100, 250, 175), 1e-6f);
            Assert.AreEqual(1f, SquadState.Progress(100, 250, 250));
            Assert.AreEqual(1f, SquadState.Progress(100, 250, 400));
            Assert.AreEqual(1f, SquadState.Progress(250, 250, 100));   // no length: done
        }
    }
}
