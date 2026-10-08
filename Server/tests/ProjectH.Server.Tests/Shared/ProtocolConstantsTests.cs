using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotPacket_FitsInOneDatagram_AndTheMatchFitsInTheParts()
    {
        // Phase 8: header 13 + self block, then 13 bytes per entity; 100 players in 2 packets of at most 90. Phase 12 D11:
        // the self block grew from 6 to 14 bytes (header 27); the entity stays 13.
        Assert.Equal(27, WorldSnapshotHeader.Size);
        Assert.Equal(14, SnapshotSelf.Size);
        Assert.Equal(13, SnapshotEntity.Size);
        Assert.Equal(100, ProtocolConstants.MaxSnapshotEntities);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxEntitiesPerSnapshotPacket * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(WorldSnapshotHeader.Size + (ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1) * SnapshotEntity.Size > ProtocolConstants.MaxPacketSize);
        Assert.Equal(2, ProtocolConstants.MaxSnapshotParts);
        Assert.True(ProtocolConstants.MaxSnapshotParts * ProtocolConstants.MaxEntitiesPerSnapshotPacket >= ProtocolConstants.MaxSnapshotEntities);
    }

    [Fact]
    public void ProtocolVersion_IsNineteen()
    {
        // Review fixes A-D changed the connect request (cookie); v18 clients must be rejected.
        Assert.Equal((ushort)19, ProtocolConstants.ProtocolVersion);
    }
}
