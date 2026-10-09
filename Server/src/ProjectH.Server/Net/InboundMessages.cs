using LiteNetLib;
using ProjectH.Server.Game.Build;
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
    // 기능: 연결 사건(연결·Join 요청·끊김)을 Game Loop로 넘길 메시지로 만든다.
    // 입력: kind - 사건 종류, peerId - 연결 id, peer - 연결(같은 id 재사용 구분용), devPlayerId - 연결 요청의 이름(없으면 null).
    // 출력: 메시지.
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

// Phase 13 D8: one parsed build request. Phase 13.5 D4: a placement or an edit (BuildQueueItem).
public readonly struct BuildMessage
{
    // 기능: 파싱된 건설 요청(배치 또는 편집)을 Game Loop로 넘길 메시지로 만든다.
    // 입력: peerId - 연결 id, peer - 연결(같은 id 재사용 구분용), request - 요청.
    // 출력: 메시지.
    public BuildMessage(int peerId, NetPeer peer, in BuildQueueItem request)
    {
        PeerId = peerId;
        Peer = peer;
        Request = request;
    }

    public int PeerId { get; }
    public NetPeer Peer { get; }
    public BuildQueueItem Request { get; }
}

// Phase 15 D7: one parsed map marker request (a ping or a waypoint change).
public readonly struct MarkerMessage
{
    // 기능: 파싱된 MapMarker를 Game Loop로 넘길 메시지로 만든다.
    // 입력: peerId - 연결 id, peer - 연결(같은 id 재사용 구분용), marker - 요청.
    // 출력: 메시지.
    public MarkerMessage(int peerId, NetPeer peer, in MapMarker marker)
    {
        PeerId = peerId;
        Peer = peer;
        Marker = marker;
    }

    public int PeerId { get; }
    public NetPeer Peer { get; }
    public MapMarker Marker { get; }
}

public readonly struct InputMessage
{
    // 기능: 파싱된 플레이어 입력을 Game Loop로 넘길 메시지로 만든다.
    // 입력: peerId - 연결 id, peer - 연결(같은 id 재사용 구분용), packet - 입력 패킷.
    // 출력: 메시지.
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
