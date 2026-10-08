using System;
using NUnit.Framework;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Review fix A3: how NetClient reads a refused connection's data. 16 bytes are the server's cookie (retry once with it at
    // once), 1 byte is a RejectReason as before, anything else is a reject without a known reason.
    public class NetClientCookieTests
    {
        [Test]
        public void SixteenBytes_AreACookie()
        {
            Assert.AreEqual(16, ProtocolLimits.CookieBytes);
            Assert.AreEqual(RejectKind.Cookie, NetClient.ClassifyReject(new byte[16]));
        }

        [Test]
        public void OneByte_IsARejectReason()
        {
            Assert.AreEqual(RejectKind.Reason, NetClient.ClassifyReject(new[] { (byte)RejectReason.VersionMismatch }));
            Assert.AreEqual(RejectKind.Reason, NetClient.ClassifyReject(new[] { (byte)RejectReason.ServerFull }));
        }

        [Test]
        public void NoData_IsNone()
        {
            Assert.AreEqual(RejectKind.None, NetClient.ClassifyReject(ReadOnlySpan<byte>.Empty));
        }

        [Test]
        public void OtherLengths_AreNone()
        {
            // Neither a cookie nor a reason byte: not retried and no reason read from it.
            Assert.AreEqual(RejectKind.None, NetClient.ClassifyReject(new byte[2]));
            Assert.AreEqual(RejectKind.None, NetClient.ClassifyReject(new byte[15]));
            Assert.AreEqual(RejectKind.None, NetClient.ClassifyReject(new byte[17]));
        }
    }
}
