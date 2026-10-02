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
