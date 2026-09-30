using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotEntityLimit_FitsInOneDatagram()
    {
        const int headerBytes = 11;
        const int entityBytes = 22;
        Assert.True(headerBytes + ProtocolConstants.MaxSnapshotEntities * entityBytes <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ProtocolVersion_IsTwo()
    {
        // Phase 1 changed movement results (box collision); v1 clients must be rejected at connect.
        Assert.Equal((ushort)2, ProtocolConstants.ProtocolVersion);
    }
}
