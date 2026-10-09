using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// A battle royale Match (no DevRespawn) driven tick by tick, for the Phase 5 rule tests. The countdown and the
// result screen are 1 s (30 ticks) so tests reach every state quickly. Every sent packet is recorded.
// Phase 12: airDrop false (the default here) starts matches on the drop points as in Phases 5-11, so the rule tests
// stay about their rules; the deployment tests pass true (ServerOptions.AirDrop, on in production).
internal sealed class RoyaleHarness
{
    public sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    public const int CountdownTicks = 30;
    public const int ResultTicks = 30;
    public static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // Phase 6 spec interpretation 8: the default drop points are the six lobby ring spots in the plaza, so the
    // Phase 5 rule tests still start close enough to shoot each other, inside the small test zone.
    public static readonly Vector3[] LobbyRingDrops = Enumerable.Range(1, 6).Select(id => Match.SpawnPosition((ushort)id)).ToArray();

    private readonly Dictionary<int, uint> _seq = new();

    // 기능: 배틀로얄 규칙 테스트용 Match를 만든다(카운트다운·결과 화면 1초, 기본 투입 지점은 로비 링).
    // 입력: loadout - 시작 장비(null이면 빈손), zonesJson - 존 설정, maxPlayers - 최대 인원, minPlayers - 시작 최소 인원, lootJson - Loot 설정, record - true면 보낸 패킷을 Packets에 복사해 기록, dropPoints - 투입 지점(null이면 LobbyRingDrops), matchSink - 경기 기록 수신자, reconnectGraceSeconds - 재접속 유예 초, graceExpired - 유예 만료 알림, airDrop - 수송기 투입 사용 여부, teamSize - 팀 인원, squad - 분대 카탈로그, map - 맵 카탈로그, deterministicSeeds - true면 seed + round로 굴림.
    // 출력: 참가자가 없는 Match와 빈 Packets 목록을 가진 하네스.
    // record false: sent packets are dropped instead of copied, for allocation tests.
    public RoyaleHarness(StartingLoadout? loadout = null, string zonesJson = TestGameData.ZonesJson, int maxPlayers = 6,
        int minPlayers = 2, string lootJson = TestGameData.LootJson, bool record = true,
        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null,
        int reconnectGraceSeconds = 10, Action<string>? graceExpired = null, bool airDrop = false, int teamSize = 1,
        ProjectH.Server.Game.Squad.SquadCatalog? squad = null, ProjectH.Server.Game.Map.MapCatalog? map = null,
        bool deterministicSeeds = true)
    {
        SendPacket send = record ? (peer, data, method) => Packets.Add(new Sent(peer, data.ToArray(), method)) : static (_, _, _) => { };
        // Review fix C1: the rule tests roll with the configured seeds (seed + round), as they did before the match secret.
        Match = new Match(new ServerOptions
            {
                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds, AirDrop = airDrop, TeamSize = teamSize,
                DeterministicSeeds = deterministicSeeds,
            },
            TestGameData.Create(lootJson: lootJson, zonesJson: zonesJson, squad: squad, map: map), send, loadout, dropPoints: dropPoints ?? LobbyRingDrops,
            matchSink: matchSink, graceExpired: graceExpired);
    }

    public Match Match { get; }
    public List<Sent> Packets { get; } = new();

    // 기능: Peer를 "p<peer>" 이름으로 Match에 참가시킨다. 참가가 거부되면 테스트를 실패시킨다.
    // 입력: peer - 참가할 Peer ID.
    // 출력: 참가한 PlayerEntity.
    public PlayerEntity Join(int peer)
    {
        Assert.Equal(JoinResult.Ok, Match.TryJoin(peer, "p" + peer));
        Match.TryGetPlayer(peer, out var player);
        return player;
    }

    // 기능: 경기가 진행 중(Playing 또는 FinalPhase)이 될 때까지 Tick을 돌린다. 카운트다운 2회분 안에 못 가면 테스트를 실패시킨다.
    // 입력: 없음.
    // 출력: 반환값 없음. Match.Flow.InMatch가 true가 된다.
    // Ticks until the match is running (Playing or FinalPhase).
    public void RunToMatch() => TickUntil(() => Match.Flow.InMatch, 2 * CountdownTicks + 5);

    // 기능: 조건이 참이 될 때까지 최대 maxTicks번 Tick을 돌린다. 끝까지 조건이 거짓이면 테스트를 실패시킨다.
    // 입력: condition - 멈출 조건, maxTicks - 최대 Tick 수.
    // 출력: 반환값 없음. Match가 조건을 만족하는 상태까지 진행된다.
    public void TickUntil(Func<bool> condition, int maxTicks)
    {
        for (int i = 0; i < maxTicks && !condition(); i++) Match.Tick();
        Assert.True(condition(), $"condition not reached within {maxTicks} ticks (state {Match.Flow.State})");
    }

    // 기능: Match를 count번 Tick한다.
    // 입력: count - Tick 수.
    // 출력: 반환값 없음. Match 상태가 count Tick만큼 진행된다.
    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Match.Tick();
    }

    // 기능: 플레이어와 그 Lag Compensation 기록을 feet 위치로 옮긴다.
    // 입력: player - 옮길 플레이어, feet - 발 위치.
    // 출력: 반환값 없음. 플레이어 위치와 History가 feet로 바뀐다.
    // Moves a player (and its lag compensation history) to feet.
    public void Place(PlayerEntity player, Vector3 feet)
    {
        player.State.Position = feet;
        player.History.Reset(Match.ServerTick, feet);
    }

    // 기능: 플레이어의 다음 Seq를 붙여 입력 하나를 Match에 넣는다(Seq는 마지막 처리 Seq 다음부터 이어진다).
    // 입력: player - 입력을 보낼 플레이어, command - 보낼 입력 명령(Seq는 여기서 덮어쓴다).
    // 출력: 반환값 없음. Match 입력 큐에 명령이 들어간다.
    public void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        // Continue after the client's last seq (a respawn or a match start keeps it).
        seq = Math.Max(seq, player.LastProcessedSeq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        Match.EnqueueInput(player.PeerId, packet);
    }

    // 기능: 사수가 대상의 가슴(현재 위치)을 겨냥해 발사하는 입력을 넣고 한 Tick 돌린다.
    // 입력: shooter - 사수, target - 대상.
    // 출력: 반환값 없음. 발사 입력이 처리된 뒤 Match가 한 Tick 진행된다.
    // One tick with the shooter firing at the target's chest (as rendered now).
    public void ShootOnce(PlayerEntity shooter, PlayerEntity target)
    {
        TestAim.YawPitch(shooter.State.Position, target.State.Position + Chest, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = Match.ServerTick });
        Match.Tick();
    }

    // 기능: 대상이 죽을 때까지 매 Tick 발사한다. maxTicks 안에 죽지 않으면 테스트를 실패시킨다.
    // 입력: shooter - 사수, target - 대상, maxTicks - 최대 발사 Tick 수.
    // 출력: 반환값 없음. 대상이 죽은 상태가 된다.
    // Fires until the target is dead (the combat loadout's automatic weapon: 30 damage every 3 ticks).
    public void ShootUntilDead(PlayerEntity shooter, PlayerEntity target, int maxTicks = 120)
    {
        for (int i = 0; i < maxTicks && target.Alive; i++) ShootOnce(shooter, target);
        Assert.False(target.Alive, "target still alive");
    }

    // 기능: 특정 Peer에게 보낸 특정 종류의 패킷을 보낸 순서대로 모은다.
    // 입력: peer - 받은 Peer ID, id - 패킷 종류.
    // 출력: 조건에 맞는 송신 기록 목록(없으면 빈 목록).
    public List<Sent> SentTo(int peer, PacketId id) => Packets.Where(s => s.PeerId == peer && s.Id == id).ToList();

    // 기능: 송신 기록의 PacketId를 건너뛴 본문 위치의 PacketReader를 만든다.
    // 입력: s - 송신 기록.
    // 출력: 본문 첫 바이트를 가리키는 PacketReader.
    public static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    // 기능: 송신 기록을 PlayerDied로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 PlayerDied.
    public static PlayerDied ReadDied(Sent s) { var r = Reader(s); Assert.True(PlayerDied.TryRead(ref r, out var v)); return v; }
    // 기능: 송신 기록을 PlayerRespawned로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 PlayerRespawned.
    public static PlayerRespawned ReadRespawned(Sent s) { var r = Reader(s); Assert.True(PlayerRespawned.TryRead(ref r, out var v)); return v; }
    // 기능: 송신 기록을 ShotFired로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 ShotFired.
    public static ShotFired ReadShot(Sent s) { var r = Reader(s); Assert.True(ShotFired.TryRead(ref r, out var v)); return v; }
    // 기능: 송신 기록을 ItemRemoved로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: s - 송신 기록.
    // 출력: 읽은 ItemRemoved.
    public static ItemRemoved ReadRemoved(Sent s) { var r = Reader(s); Assert.True(ItemRemoved.TryRead(ref r, out var v)); return v; }
}
