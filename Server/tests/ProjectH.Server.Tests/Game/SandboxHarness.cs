using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13: a dev-sandbox Match (DevRespawn, damage always allowed) driven tick by tick for the harvest and build rule
// tests. Every sent packet is recorded with its delivery method.
internal sealed class SandboxHarness
{
    public sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly Dictionary<int, uint> _seq = new();

    // 기능: 채집·건설 규칙 테스트용 샌드박스 Match(DevRespawn)를 만든다. 보낸 패킷은 Packets에 기록한다.
    // 입력: loadout - 시작 장비(null이면 CombatLoadout), options - 서버 옵션(null이면 8명·DevRespawn·고정 seed), data - 게임 데이터(null이면 테스트 데이터).
    // 출력: 참가자가 없는 Match와 빈 Packets 목록을 가진 하네스.
    public SandboxHarness(StartingLoadout? loadout = null, ServerOptions? options = null, GameData? data = null)
    {
        options ??= new ServerOptions { MaxPlayers = 8, DevRespawn = true, DeterministicSeeds = true };   // review fix C1
        Match = new Match(options, data ?? TestGameData.Create(), (peer, bytes, method) => Packets.Add(new Sent(peer, bytes.ToArray(), method)),
            loadout ?? TestGameData.CombatLoadout);
    }

    public Match Match { get; }
    public List<Sent> Packets { get; } = new();

    // 기능: 맵 지형 높이 위의 점을 만든다.
    // 입력: x - X 좌표, z - Z 좌표.
    // 출력: (x, 지형 높이, z) 위치.
    public static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    // 기능: Peer를 "p<peer>" 이름으로 참가시키고 feet 위치·yaw로 둔다. 참가가 거부되면 테스트를 실패시킨다.
    // 입력: peer - 참가할 Peer ID, feet - 발 위치, yaw - 바라보는 방향.
    // 출력: 참가해 배치된 PlayerEntity.
    public PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, Match.TryJoin(peer, "p" + peer));
        Match.TryGetPlayer(peer, out var player);
        Place(player, feet, yaw);
        return player;
    }

    // 기능: 플레이어의 위치·Yaw와 Lag Compensation 기록을 feet로 옮긴다.
    // 입력: player - 옮길 플레이어, feet - 발 위치, yaw - 바라보는 방향.
    // 출력: 반환값 없음. 플레이어 위치·Yaw·History가 바뀐다.
    public void Place(PlayerEntity player, Vector3 feet, float yaw = 0f)
    {
        player.State.Position = feet;
        player.State.Yaw = yaw;
        player.History.Reset(Match.ServerTick, feet);
    }

    // 기능: 플레이어별 다음 Seq를 붙여 입력 하나를 Match에 넣는다(Tick은 돌리지 않는다).
    // 입력: player - 입력을 보낼 플레이어, command - 보낼 입력 명령(Seq는 여기서 덮어쓴다).
    // 출력: 반환값 없음. Match 입력 큐에 명령이 들어간다.
    // One input for the next tick (Seq numbered per player).
    public void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        Match.EnqueueInput(player.PeerId, packet);
    }

    // 기능: 플레이어 눈에서 월드 지점을 겨냥한 채 버튼을 누른 입력을 넣고 한 Tick 돌린다.
    // 입력: player - 입력을 보낼 플레이어, buttons - 누를 버튼, aimAt - 겨냥할 월드 지점.
    // 출력: 반환값 없음. 입력이 처리된 뒤 Match가 한 Tick 진행된다.
    // An input with these buttons, aimed from the player's eye at a world point, then one tick.
    public void Act(PlayerEntity player, InputButtons buttons, Vector3 aimAt)
    {
        TestAim.YawPitch(player.State.Position, aimAt, out float yaw, out float pitch);
        Send(player, new InputCommand { Buttons = buttons, Yaw = player.State.Yaw, AimYaw = yaw, AimPitch = pitch });
        Match.Tick();
    }

    // 기능: 현재 Yaw를 유지한 채 버튼만 누른 입력을 넣고 한 Tick 돌린다.
    // 입력: player - 입력을 보낼 플레이어, buttons - 누를 버튼.
    // 출력: 반환값 없음. 입력이 처리된 뒤 Match가 한 Tick 진행된다.
    public void Press(PlayerEntity player, InputButtons buttons)
    {
        Send(player, new InputCommand { Buttons = buttons, Yaw = player.State.Yaw });
        Match.Tick();
    }

    // 기능: Match를 count번 Tick한다.
    // 입력: count - Tick 수.
    // 출력: 반환값 없음. Match 상태가 count Tick만큼 진행된다.
    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Match.Tick();
    }

    // 기능: 특정 Peer에게 보낸 특정 종류의 패킷을 보낸 순서대로 열거한다.
    // 입력: peer - 받은 Peer ID, id - 패킷 종류.
    // 출력: 조건에 맞는 송신 기록의 지연 열거(Packets를 다시 읽는다).
    public IEnumerable<Sent> To(int peer, PacketId id) => Packets.Where(p => p.PeerId == peer && p.Id == id);

    // 기능: 송신 기록의 PacketId를 건너뛴 본문 위치의 PacketReader를 만든다. PacketId를 못 읽으면 테스트를 실패시킨다.
    // 입력: sent - 송신 기록.
    // 출력: 본문 첫 바이트를 가리키는 PacketReader.
    public static PacketReader Body(Sent sent)
    {
        var reader = new PacketReader(sent.Data);
        Assert.True(reader.TryReadPacketId(out _));
        return reader;
    }

    // 기능: 기록된 송신 패킷을 모두 지운다.
    // 입력: 없음.
    // 출력: 반환값 없음. Packets가 비워진다.
    public void Clear() => Packets.Clear();

    // 기능: 이 하네스의 Match에 조각을 요청·이벤트 없이 바로 넣는다(지지 간선 포함).
    // 입력: shape - 조각 모양, material - 재질, createdTick - 생성 Tick, owner - 소유자 EntityId.
    // 출력: 추가된 조각 ID.
    // A piece put straight into the world (no request, no event), with its support edges, as a placement would.
    public uint AddPiece(BuildPieceShape shape, BuildMaterialType material = BuildMaterialType.Wood, uint createdTick = 0, ushort owner = 99) =>
        AddPiece(Match, shape, material, createdTick, owner);

    // 기능: 조각을 Build에 넣고 지형 접지 여부를 계산해 Support에 등록한다(배치가 하는 일과 같다). 추가에 실패하면 테스트를 실패시킨다.
    // 입력: match - 대상 Match, shape - 조각 모양, material - 재질, createdTick - 생성 Tick, owner - 소유자 EntityId.
    // 출력: 추가된 조각 ID.
    public static uint AddPiece(Match match, BuildPieceShape shape, BuildMaterialType material = BuildMaterialType.Wood, uint createdTick = 0,
        ushort owner = 99)
    {
        uint id = match.Build.Add(shape, material, owner, createdTick, BuildSupport.IsGrounded(shape, GameMap.Terrain, GameMap.Boxes));
        Assert.NotEqual(0u, id);
        match.Build.TryGetSlot(id, out int slot);
        match.Support.Add(slot, shape);
        return id;
    }
}
