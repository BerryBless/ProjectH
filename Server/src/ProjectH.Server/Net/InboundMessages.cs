using LiteNetLib;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

public enum ControlKind : byte
{
    Connected,
    JoinRequested,
    Disconnected,
}

// Messages are readonly structs so the network -> game loop handoff does not allocate.
// Peer is carried because LiteNetLib reuses peer ids: the game loop compares references
// to ignore messages from a previous connection that had the same id.
public readonly struct ControlMessage
{
    // 기능: Network Thread가 Game Loop에 넘길 연결 제어 메시지를 만든다.
    // 입력: kind - 메시지 종류, peerId - 연결 Id, peer - 보낸 연결(재사용된 Id 구분용), devPlayerId - 개발용 Player Id(없으면 null).
    // 출력: 받은 값을 그대로 담은 ControlMessage.
    public ControlMessage(ControlKind kind, int peerId, NetPeer peer, string? devPlayerId)
    {
        Kind = kind;
        PeerId = peerId;
        Peer = peer;
        DevPlayerId = devPlayerId;
    }

    public ControlKind Kind { get; }
    public int PeerId { get; }
    public NetPeer Peer { get; }
    public string? DevPlayerId { get; }
}

// Phase 13 D8: one parsed build request.
public readonly struct BuildMessage
{
    // 기능: Network Thread가 Game Loop에 넘길 건설 요청 메시지를 만든다.
    // 입력: peerId - 연결 Id, peer - 보낸 연결(재사용된 Id 구분용), request - 파싱된 건설 요청.
    // 출력: 받은 값을 그대로 담은 BuildMessage.
    public BuildMessage(int peerId, NetPeer peer, in BuildRequest request)
    {
        PeerId = peerId;
        Peer = peer;
        Request = request;
    }

    public int PeerId { get; }
    public NetPeer Peer { get; }
    public BuildRequest Request { get; }
}

public readonly struct InputMessage
{
    // 기능: Network Thread가 Game Loop에 넘길 입력 메시지를 만든다.
    // 입력: peerId - 연결 Id, peer - 보낸 연결(재사용된 Id 구분용), packet - 파싱된 입력 Packet.
    // 출력: 받은 값을 그대로 담은 InputMessage.
    public InputMessage(int peerId, NetPeer peer, in PlayerInputPacket packet)
    {
        PeerId = peerId;
        Peer = peer;
        Packet = packet;
    }

    public int PeerId { get; }
    public NetPeer Peer { get; }
    public PlayerInputPacket Packet { get; }
}
