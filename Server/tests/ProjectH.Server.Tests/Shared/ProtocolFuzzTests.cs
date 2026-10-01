using System;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 10 §2: seeded random bytes through every parser the server runs on client data and every parser the client
// runs on server data. A parser may refuse the bytes; it must never throw. Same seed = same bytes, so a failure
// reproduces.
public class ProtocolFuzzTests
{
    private const int Buffers = 100_000;

    [Fact]
    public void RandomBytes_NeverThrow_InAnyParser()
    {
        var random = new Random(10);
        var buffer = new byte[ProtocolConstants.MaxPacketSize + 100];
        int serverParsed = 0;
        int clientParsed = 0;
        for (int n = 0; n < Buffers; n++)
        {
            // Mostly short (where the length checks matter), every 100th up to a full datagram and beyond.
            int length = n % 100 == 0 ? random.Next(buffer.Length + 1) : random.Next(65);
            random.NextBytes(buffer.AsSpan(0, length));
            // Half of them start with a valid packet id, so the body parsers also see plausible headers.
            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.MatchResult + 1);
            ReadOnlySpan<byte> data = buffer.AsSpan(0, length);

            serverParsed += ServerSide(data);
            clientParsed += ClientSide(data);
            clientParsed += ClientSide(length > 0 ? data.Slice(1) : data);   // the body after the id byte
        }
        // Body parsers only (reading the id byte alone does not count): random bytes must also reach the success
        // paths on both sides, not only the early refusals.
        Assert.True(serverParsed > 0, "no server-side body ever parsed");
        Assert.True(clientParsed > 0, "no client-side body ever parsed");
    }

    // What NetworkListener runs on client bytes. Counts the bodies that parsed.
    private static int ServerSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (ConnectRequestData.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (r.TryReadPacketId(out PacketId id) && id == PacketId.PlayerInput && PlayerInputPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerInputPacket.TryRead(ref r, out _)) ok++;
        DisconnectCodes.Read(data);   // must not throw; not counted (any first byte 1-5 "parses")
        return ok;
    }

    // What NetClient (and the bots) run on server bytes, body only (the id byte already read).
    private static int ClientSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (JoinMatchResponse.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerSpawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header))
        {
            ok++;
            for (int i = 0; i < header.Count && SnapshotEntity.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (SnapshotSelf.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WeaponCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ShotFired.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (HitConfirmed.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (DamageTaken.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDied.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerRespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldItemsPacket.TryReadHeader(ref r, out int count))
        {
            ok++;
            for (int i = 0; i < count && WorldItemData.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (ItemSpawnedPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemRemoved.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (InventoryState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PickupResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ZoneState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchResult.TryRead(ref r, out _)) ok++;
        return ok;
    }
}
