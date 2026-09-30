using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotEntityLimit_FitsInOneDatagram()
    {
        // D10: header 11 + self block 6, then 23 bytes per entity.
        Assert.Equal(17, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(23, SnapshotEntity.Size);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxSnapshotEntities * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ProtocolVersion_IsFour()
    {
        // Phase 4 changed the input command and the weapon catalog and added item packets; v3 clients
        // must be rejected at connect.
        Assert.Equal((ushort)4, ProtocolConstants.ProtocolVersion);
    }
}
