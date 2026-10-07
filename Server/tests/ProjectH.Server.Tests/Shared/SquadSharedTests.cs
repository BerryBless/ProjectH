using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 14 (spec "검증 계획", Shared): the Downed movement mode, the squad packets and the reboot station placement.
public class SquadSharedTests
{
    private const float Dt = 1f / 30f;
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    // 기능: 평지에서 같은 입력으로 steps번 이동한다.
    // 입력: s - 상태, input - 입력, steps - 횟수, world - 상자.
    // 출력: 반환값 없음. s가 바뀐다.
    private static void Run(ref MoveState s, InputCommand input, int steps, ReadOnlySpan<Box> world = default)
    {
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref s, input, Dt, world, HeightField.Flat);
    }

    // ---- Downed movement (D4) ----

    [Fact]
    public void Downed_CrawlsAtCrawlSpeed_AndNeverLeavesTheMode()
    {
        var s = new MoveState { Mode = MovementMode.Downed };
        Run(ref s, new InputCommand { MoveY = 1f }, 30);
        Assert.Equal(MovementMode.Downed, s.Mode);
        Assert.Equal(MovementTuning.CrawlSpeed, s.Position.Z, 1);
    }

    [Fact]
    public void Downed_IgnoresJumpSprintAndCrouch()
    {
        var s = new MoveState { Mode = MovementMode.Downed };
        var input = new InputCommand { MoveY = 1f, Buttons = InputButtons.Jump | InputButtons.Sprint | InputButtons.Crouch };
        MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat, out StepResult result);
        Assert.False(result.Sprinting);
        Run(ref s, input, 29);
        Assert.Equal(MovementMode.Downed, s.Mode);
        Assert.Equal(0f, s.Position.Y, 3);                                  // no jump
        Assert.Equal(MovementTuning.CrawlSpeed, s.Position.Z, 1);          // no sprint
    }

    [Fact]
    public void Downed_DoesNotVault_OverALowWall()
    {
        // A 0.8 m wall 1 m ahead: a standing character could vault it, a downed one stops at it.
        Box[] wall = { new(new Vector3(-2f, 0f, 1f), new Vector3(2f, 0.8f, 1.4f)) };
        var s = new MoveState { Mode = MovementMode.Downed };
        Run(ref s, new InputCommand { MoveY = 1f, Buttons = InputButtons.Jump }, 60, wall);
        Assert.Equal(MovementMode.Downed, s.Mode);
        Assert.True(s.Position.Z < 1f);
    }

    [Fact]
    public void Downed_FallsOffALedge_AndReportsTheLanding()
    {
        var s = new MoveState { Mode = MovementMode.Downed, Position = new Vector3(0f, 6f, 0f) };
        float landing = 0f;
        for (int i = 0; i < 90 && landing == 0f; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat, out StepResult result);
            landing = result.LandingSpeed;
        }
        Assert.True(landing > 0f);
        Assert.Equal(MovementMode.Downed, s.Mode);
    }

    [Fact]
    public void Downed_Box_Is0Point9High_AndPassesUnderALowSlab()
    {
        Assert.Equal(MovementTuning.DownedHeight, MovementSimulation.CollisionHeight(MovementMode.Downed));
        // A slab from 1.0 m: a crouched box (1.2 m) cannot pass under it, a downed one (0.9 m) crawls under.
        Box[] slab = { new(new Vector3(-2f, 1.0f, 1f), new Vector3(2f, 2f, 3f)) };
        var s = new MoveState { Mode = MovementMode.Downed };
        Run(ref s, new InputCommand { MoveY = 1f }, 90, slab);
        Assert.True(s.Position.Z > 3f);
    }

    [Fact]
    public void Downed_PredictionAndServerStep_GiveTheSameResult()
    {
        // The client predicts with the same Shared step; two runs from the same state and inputs end identically.
        var a = new MoveState { Mode = MovementMode.Downed, Yaw = 30f };
        var b = a;
        var input = new InputCommand { MoveX = 0.5f, MoveY = 1f, Yaw = 30f };
        Run(ref a, input, 45);
        Run(ref b, input, 45);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Mode, b.Mode);
    }

    // ---- Packets (D2, D5, D8, D10, D16) ----

    // 기능: 버퍼에 쓴 패킷의 id를 확인하고 본문 Reader를 돌려준다.
    // 입력: length - 길이, expected - 기대 id.
    // 출력: id 뒤의 PacketReader.
    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    [Fact]
    public void TeamState_RoundTrips_AndIsAtMost23Bytes()
    {
        var s = new TeamState { TeamId = 3, Count = 4 };
        s.Set(0, new TeamMember { EntityId = 1, State = TeamMemberState.Up, Health = 100 });
        s.Set(1, new TeamMember { EntityId = 9, State = TeamMemberState.Downed, Health = 40 });
        s.Set(2, new TeamMember { EntityId = 12, State = TeamMemberState.Eliminated, Flags = TeamMemberFlags.CardDropped });
        s.Set(3, new TeamMember { EntityId = 300, State = TeamMemberState.Rebooting, Flags = TeamMemberFlags.CardHeld });
        var writer = new PacketWriter(_buffer);
        TeamState.Write(ref writer, s);
        Assert.Equal(TeamState.MaxSize, writer.Length);
        Assert.Equal(23, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.TeamState);
        Assert.True(TeamState.TryRead(ref reader, out TeamState read));
        Assert.Equal(3, read.TeamId);
        Assert.Equal(4, read.Count);
        Assert.Equal(TeamMemberState.Downed, read.Get(1).State);
        Assert.Equal(40, read.Get(1).Health);
        Assert.Equal(TeamMemberFlags.CardDropped, read.Get(2).Flags);
        Assert.Equal(300, read.Get(3).EntityId);
        Assert.Equal(TeamMemberState.Rebooting, read.Get(3).State);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 1, 0, 0, 100, 0 })]          // team 0
    [InlineData(new byte[] { 1, 0 })]                           // no member
    [InlineData(new byte[] { 1, 5 })]                           // above the team size
    [InlineData(new byte[] { 1, 1, 0, 0, 0, 100, 0 })]          // entity 0
    [InlineData(new byte[] { 1, 1, 1, 0, 4, 100, 0 })]          // no such state
    [InlineData(new byte[] { 1, 1, 1, 0, 0, 100, 4 })]          // no such flag
    [InlineData(new byte[] { 1, 2, 1, 0, 0, 100, 0 })]          // short
    public void TeamState_RefusesWhatTheServerNeverSends(byte[] body)
    {
        var reader = new PacketReader(body);
        Assert.False(TeamState.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerDowned_RoundTrips()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDowned.Write(ref writer, new PlayerDowned { VictimId = 4, AttackerId = 7, Cause = DeathCause.Fall });
        Assert.Equal(PlayerDowned.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDowned);
        Assert.True(PlayerDowned.TryRead(ref reader, out PlayerDowned read));
        Assert.Equal(4, read.VictimId);
        Assert.Equal(7, read.AttackerId);
        Assert.Equal(DeathCause.Fall, read.Cause);
        _buffer[1] = 0;
        _buffer[2] = 0;   // victim 0
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDowned);
        Assert.False(PlayerDowned.TryRead(ref reader, out _));
    }

    [Fact]
    public void ChannelState_RoundTrips_AndChecksTheTarget()
    {
        var writer = new PacketWriter(_buffer);
        ChannelState.Write(ref writer, new ChannelState { Kind = ChannelKind.Reboot, ActorId = 2, Target = 3, EndTick = 123456, Active = true });
        Assert.Equal(ChannelState.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ChannelState);
        Assert.True(ChannelState.TryRead(ref reader, out ChannelState read));
        Assert.Equal(ChannelKind.Reboot, read.Kind);
        Assert.Equal(3, read.Target);
        Assert.Equal(123456u, read.EndTick);
        Assert.True(read.Active);

        writer = new PacketWriter(_buffer);
        ChannelState.Write(ref writer, new ChannelState { Kind = ChannelKind.Reboot, ActorId = 2, Target = RebootStations.Count });
        reader = ReaderAfterId(writer.Length, PacketId.ChannelState);
        Assert.False(ChannelState.TryRead(ref reader, out _));   // no such station

        writer = new PacketWriter(_buffer);
        ChannelState.Write(ref writer, new ChannelState { Kind = ChannelKind.Revive, ActorId = 2, Target = 0 });
        reader = ReaderAfterId(writer.Length, PacketId.ChannelState);
        Assert.False(ChannelState.TryRead(ref reader, out _));   // a revive names a player
    }

    [Fact]
    public void RebootStations_RoundTrips_AndRefusesUnknownStations()
    {
        var s = new RebootStationsState { CooldownMask = 0b0101 };
        s.SetEndTick(0, 900);
        s.SetEndTick(2, 1800);
        var writer = new PacketWriter(_buffer);
        RebootStationsState.Write(ref writer, s);
        Assert.Equal(RebootStationsState.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.RebootStations);
        Assert.True(RebootStationsState.TryRead(ref reader, out RebootStationsState read));
        Assert.True(read.IsCoolingDown(0));
        Assert.False(read.IsCoolingDown(1));
        Assert.Equal(1800u, read.GetEndTick(2));

        _buffer[1] = 1 << RebootStations.Count;
        reader = ReaderAfterId(writer.Length, PacketId.RebootStations);
        Assert.False(RebootStationsState.TryRead(ref reader, out _));
    }

    [Fact]
    public void RebootCardItem_IsValidOnlyWithAnOwner()
    {
        Assert.True(ReadsBack(new WorldItemData { ItemId = 1, Kind = ItemKind.RebootCard, Amount = 5 }));
        Assert.False(ReadsBack(new WorldItemData { ItemId = 1, Kind = ItemKind.RebootCard, Amount = 0 }));
        Assert.False(ReadsBack(new WorldItemData { ItemId = 1, Kind = ItemKind.RebootCard, DefId = 1, Amount = 5 }));
    }

    // 기능: 월드 아이템을 쓰고 다시 읽어 Client 리더가 받아들이는지 본다.
    // 입력: item - 아이템.
    // 출력: 읽혔으면 true.
    private bool ReadsBack(in WorldItemData item)
    {
        var writer = new PacketWriter(_buffer);
        WorldItemData.Write(ref writer, item);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        return WorldItemData.TryRead(ref reader, out _);
    }

}

// Phase 14 D10: the reboot station placement (RebootStations.cs names this class).
public class RebootStationsTests
{

    // 기능: 점에서 상자 바닥면까지의 지면 거리를 잰다.
    // 입력: p - 점, box - 상자.
    // 출력: 거리(안이면 0).
    private static float FootprintDistance(Vector3 p, in Box box)
    {
        float dx = MathF.Max(0f, MathF.Max(box.Min.X - p.X, p.X - box.Max.X));
        float dz = MathF.Max(0f, MathF.Max(box.Min.Z - p.Z, p.Z - box.Max.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    [Fact]
    public void Stations_StandOnFlatTerrain_InsideTheWalls_OutsideThePlaza()
    {
        Assert.Equal(RebootStations.Count, RebootStations.All.Length);
        foreach (Vector3 p in RebootStations.All)
        {
            Assert.Equal(GameMap.Terrain.Height(p.X, p.Z), p.Y, 3);
            Assert.True(MathF.Abs(p.X) < GameMap.HalfSize - 5f && MathF.Abs(p.Z) < GameMap.HalfSize - 5f);
            Assert.True(new Vector2(p.X, p.Z).Length() > GameMap.PlazaRadius + 5f);
            // Flat enough around it for the reboot spots (Match.RebootSpotRadius, 1.2 m away) to stand on.
            for (int a = 0; a < 8; a++)
            {
                float x = p.X + MathF.Cos(a * MathF.PI / 4f) * 1.2f;
                float z = p.Z + MathF.Sin(a * MathF.PI / 4f) * 1.2f;
                Assert.True(MathF.Abs(GameMap.Terrain.Height(x, z) - p.Y) < 0.6f, $"station {p} is on a slope");
            }
        }
    }

    [Fact]
    public void Stations_AreClearOfEveryBoxDoorAndHarvestable_AndAwayFromLoot()
    {
        foreach (Vector3 p in RebootStations.All)
        {
            foreach (Box box in GameMap.Boxes) Assert.True(FootprintDistance(p, box) >= RebootStations.ClearRadius, $"station {p} near box {box.Min}");
            foreach (Box door in GameMap.Doors) Assert.True(FootprintDistance(p, door) >= RebootStations.ClearRadius, $"station {p} near a door");
            foreach (Harvestable h in GameMap.Harvestables) Assert.True(FootprintDistance(p, h.Bounds) >= RebootStations.ClearRadius, $"station {p} near a harvestable");
            foreach (LootPoint loot in LootPoints.All)
                Assert.True(Vector3.Distance(p, loot.Position) >= RebootStations.ClearRadius, $"station {p} on a loot point");
        }
    }
}
