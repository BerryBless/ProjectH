using System;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 10 §2: seeded inputs full of hostile values (NaN, ±Infinity, huge numbers, random Seq and ViewTick, every
// button) through the real wire parser into Match for hundreds of ticks. Nothing may throw, and every position and
// health value must stay finite and in range.
public class InputFuzzTests
{
    private static readonly float[] Hostile =
    {
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e30f, -1e30f,
        float.Epsilon, -0f, 0f, 1f, -1f, 360f, -720f, 1e6f,
    };

    // 기능: 퍼징용 float 값 하나를 고른다(1/3은 -2..2의 정상 값, 나머지는 적대적인 값).
    // 입력: random - 시드 고정 난수.
    // 출력: 입력 필드에 넣을 float 값.
    private static float Value(Random random) =>
        random.Next(3) == 0 ? (float)(random.NextDouble() * 4 - 2) : Hostile[random.Next(Hostile.Length)];

    // Review fix D2: ViewTick is a uint; the hostile ones are "now" (uint.MaxValue), 0 (the oldest), huge and near-max ticks.
    private static readonly uint[] HostileTicks = { uint.MaxValue, 0u, 1u, 1_000_000_000u, uint.MaxValue - 1, int.MaxValue };

    // 기능: 퍼징용 ViewTick 하나를 고른다(정상 범위의 작은 값 또는 적대적인 값).
    // 입력: random - 시드 고정 난수.
    // 출력: ViewTick.
    private static uint TickValue(Random random) =>
        random.Next(3) == 0 ? (uint)random.Next(0, 10_000) : HostileTicks[random.Next(HostileTicks.Length)];

    // 기능: 적대적인 값으로 채운 입력 패킷을 만들어 실제 Wire 쓰기·읽기를 거친 결과를 돌려준다.
    // 입력: random - 시드 고정 난수, buffer - 직렬화 버퍼, nextSeq - 이 플레이어의 다음 순번(정상 입력마다 올라간다), endgame - true면 uint 상단 Seq도 섞는다.
    // 출력: TryRead로 다시 읽은 PlayerInputPacket. 쓰기·읽기에 실패하면 테스트가 실패한다.
    // Through PlayerInputPacket.Write and TryRead, as NetworkListener sees it (unknown buttons are masked there).
    // Seq: mostly the next one (so most inputs are taken and acted on), sometimes old or repeated ones; near the end
    // also the top of the uint range ("negative" Seq), after which this player's normal inputs are refused.
    private static PlayerInputPacket RandomPacket(Random random, byte[] buffer, ref uint nextSeq, bool endgame)
    {
        var packet = new PlayerInputPacket { Count = (byte)random.Next(1, ProtocolConstants.MaxInputsPerPacket + 1) };
        for (int i = 0; i < packet.Count; i++)
        {
            packet.Set(i, new InputCommand
            {
                Seq = endgame && random.Next(20) == 0 ? uint.MaxValue - (uint)random.Next(3)
                    : random.Next(5) == 0 ? nextSeq - (uint)Math.Min(nextSeq, (uint)random.Next(10))
                    : nextSeq++,
                MoveX = Value(random),
                MoveY = Value(random),
                Yaw = Value(random),
                Buttons = (InputButtons)random.Next(ushort.MaxValue + 1),
                AimYaw = Value(random),
                AimPitch = Value(random),
                ViewTick = TickValue(random),
            });
        }
        var writer = new PacketWriter(buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = new PacketReader(writer.WrittenSpan);
        Assert.True(reader.TryReadPacketId(out _));
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket read));
        return read;
    }

    // 기능: 경기에 있는 모든 플레이어의 위치·속도·Yaw가 유한하고 체력·보호막이 0..100인지 확인한다.
    // 입력: match - 검사할 경기, peers - 검사할 연결 id 상한(1..peers, 없는 peer는 건너뛴다).
    // 출력: 반환값 없음. 하나라도 벗어나면 테스트가 실패한다.
    private static void AssertSane(Match match, int peers)
    {
        for (int peer = 1; peer <= peers; peer++)
        {
            if (!match.TryGetPlayer(peer, out PlayerEntity p)) continue;
            Assert.True(float.IsFinite(p.State.Position.X) && float.IsFinite(p.State.Position.Y) && float.IsFinite(p.State.Position.Z),
                $"position {p.State.Position} at tick {match.ServerTick}");
            Assert.True(float.IsFinite(p.State.VelocityY) && float.IsFinite(p.State.Yaw), $"velocity/yaw at tick {match.ServerTick}");
            Assert.InRange(p.Health, 0, 100);
            Assert.InRange(p.Shield, 0, 100);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void HostileInputs_NeverBreakTheMatch(bool devRespawn, int seed)
    {
        const int players = 4;
        var random = new Random(seed);
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var match = new Match(new ServerOptions
            {
                MaxPlayers = players, MinPlayers = 2, StartCountdownSeconds = 1, ResultSeconds = 1, DevRespawn = devRespawn,
            },
            TestGameData.Create(zonesJson: TestGameData.ShortZonesJson), static (_, _, _) => { }, TestGameData.CombatLoadout,
            dropPoints: RoyaleHarness.LobbyRingDrops);
        for (int peer = 1; peer <= players; peer++) Assert.Equal(JoinResult.Ok, match.TryJoin(peer, "f" + peer));
        var nextSeq = new uint[players + 1];
        Array.Fill(nextSeq, 1u);

        uint mostTaken = 0;
        bool reachedPlaying = false;
        for (int tick = 0; tick < 900; tick++)
        {
            for (int peer = 1; peer <= players; peer++)
            {
                if (random.Next(4) != 0) match.EnqueueInput(peer, RandomPacket(random, buffer, ref nextSeq[peer], tick > 800));
            }
            match.Tick();
            reachedPlaying |= match.Flow.State == MatchFlowState.Playing;
            AssertSane(match, players);
            for (int peer = 1; peer <= players; peer++)
            {
                if (match.TryGetPlayer(peer, out PlayerEntity p) && p.LastProcessedSeq < uint.MaxValue - 3)
                    mostTaken = Math.Max(mostTaken, p.LastProcessedSeq);
            }
        }
        Assert.True(mostTaken > 300, $"the hostile inputs were hardly taken ({mostTaken})");
        // The battle royale run must really have played (damage, zone, deaths); the sandbox has no flow states.
        if (!devRespawn) Assert.True(reachedPlaying, "the match never reached Playing");
    }

    // Review fix A4 (SEC-7): Match says whether a packet gave the player any input, so the game loop refreshes the input
    // timeout only then; inputs past the Seq window are counted in InputSeqDrops. A peer with no player (not joined, or
    // already out) is not judged here: true, as before.
    [Fact]
    public void EnqueueInput_SaysWhetherAnyInputWasTaken()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { });
        Assert.Equal(JoinResult.Ok, match.TryJoin(1, "a"));
        Assert.True(match.EnqueueInput(1, new PlayerInputPacket { Count = 1, Input0 = new InputCommand { Seq = 1 } }));
        match.Tick();
        Assert.False(match.EnqueueInput(1, new PlayerInputPacket { Count = 1, Input0 = new InputCommand { Seq = 1 } }));   // already taken
        Assert.False(match.EnqueueInput(1, new PlayerInputPacket { Count = 1, Input0 = new InputCommand { Seq = 0xFFFFFFF0 } }));
        Assert.Equal(1, match.InputSeqDrops);
        Assert.True(match.EnqueueInput(1, new PlayerInputPacket
        {
            Count = 2, Input0 = new InputCommand { Seq = 0xFFFFFFF1 }, Input1 = new InputCommand { Seq = 2 },
        }));
        Assert.Equal(2, match.InputSeqDrops);
        Assert.True(match.EnqueueInput(99, new PlayerInputPacket { Count = 1, Input0 = new InputCommand { Seq = 1 } }));
    }
}
