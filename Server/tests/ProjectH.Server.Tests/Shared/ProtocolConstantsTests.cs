using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotPacket_FitsInOneDatagram_AndTheMatchFitsInTheParts()
    {
        // Phase 8: header 13 + self block 6, then 13 bytes per entity; 100 players in 2 packets of at most 90.
        Assert.Equal(19, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(13, SnapshotEntity.Size);
        Assert.Equal(100, ProtocolConstants.MaxSnapshotEntities);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxEntitiesPerSnapshotPacket * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(WorldSnapshotHeader.Size + (ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1) * SnapshotEntity.Size > ProtocolConstants.MaxPacketSize);
        Assert.Equal(2, ProtocolConstants.MaxSnapshotParts);
        Assert.True(ProtocolConstants.MaxSnapshotParts * ProtocolConstants.MaxEntitiesPerSnapshotPacket >= ProtocolConstants.MaxSnapshotEntities);
    }

    [Fact]
    public void ProtocolVersion_IsEight()
    {
        // Phase 10 added the disconnect codes and JoinResult.Resumed; v7 clients must be rejected at connect.
        Assert.Equal((ushort)8, ProtocolConstants.ProtocolVersion);
    }
}
