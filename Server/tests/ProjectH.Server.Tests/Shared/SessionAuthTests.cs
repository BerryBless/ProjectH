using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Review fix B3: the Shared datagram tail (counter + HMAC), the 64-counter replay window, the keyless tail rule and the
// resume proof (B4).
public class SessionAuthTests
{
    private static byte[] Key(byte seed)
    {
        var key = new byte[ProtocolLimits.SessionKeyBytes];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    // 기능: 길이 n 페이로드와 꼬리 자리를 가진 버퍼를 만든다.
    // 입력: n - 페이로드 길이, fill - 채울 값.
    // 출력: n + AuthTagBytes 크기 버퍼.
    private static byte[] Datagram(int n, byte fill = 7)
    {
        var data = new byte[n + ProtocolLimits.AuthTagBytes];
        for (int i = 0; i < n; i++) data[i] = (byte)(fill + i);
        return data;
    }

    // 기능: 클라이언트가 봉인한 데이터그램 하나를 만든다.
    // 입력: client - 클라이언트 키, n - 페이로드 길이.
    // 출력: 봉인된 바이트(길이 = n + 20).
    private static byte[] Sealed(SessionKeys client, int n = 30)
    {
        byte[] data = Datagram(n);
        int length = n;
        client.Seal(data, 0, ref length);
        Assert.Equal(n + ProtocolLimits.AuthTagBytes, length);
        return data;
    }

    [Fact]
    public void SealThenOpen_RoundTrips_AndStripsTheTag()
    {
        using var client = new SessionKeys(Key(1), isServer: false);
        using var server = new SessionKeys(Key(1), isServer: true);
        byte[] data = Sealed(client);
        int length = data.Length;
        Assert.True(server.TryOpen(data, ref length));
        Assert.Equal(30, length);
        Assert.Equal(Datagram(30).AsSpan(0, 30).ToArray(), data.AsSpan(0, 30).ToArray());

        // And the other way, with the other direction's key.
        byte[] back = Datagram(10);
        int backLength = 10;
        server.Seal(back, 0, ref backLength);
        Assert.True(client.TryOpen(back, ref backLength));
        Assert.Equal(10, backLength);
    }

    [Fact]
    public void ADirectionsOwnTag_DoesNotOpenOnTheSameSide()
    {
        using var client = new SessionKeys(Key(1), isServer: false);
        using var otherClient = new SessionKeys(Key(1), isServer: false);
        byte[] data = Sealed(client);
        int length = data.Length;
        Assert.False(otherClient.TryOpen(data, ref length));   // c2s sealed, c2s expected on receive: a reflected datagram fails
    }

    [Theory]
    [InlineData(0)]     // payload
    [InlineData(31)]    // counter
    [InlineData(40)]    // MAC
    public void ATamperedByte_FailsToOpen(int index)
    {
        using var client = new SessionKeys(Key(2), isServer: false);
        using var server = new SessionKeys(Key(2), isServer: true);
        byte[] data = Sealed(client);
        data[index] ^= 0x01;
        int length = data.Length;
        Assert.False(server.TryOpen(data, ref length));
        Assert.Equal(1, server.Rejected);
    }

    [Fact]
    public void AnotherSessionKey_FailsToOpen_AndAShortDatagramFails()
    {
        using var client = new SessionKeys(Key(3), isServer: false);
        using var server = new SessionKeys(Key(4), isServer: true);
        byte[] data = Sealed(client);
        int length = data.Length;
        Assert.False(server.TryOpen(data, ref length));
        int shortLength = ProtocolLimits.AuthTagBytes - 1;
        Assert.False(server.TryOpen(new byte[shortLength], ref shortLength));
    }

    [Fact]
    public void AReplayedCounter_IsRejected()
    {
        using var client = new SessionKeys(Key(5), isServer: false);
        using var server = new SessionKeys(Key(5), isServer: true);
        byte[] data = Sealed(client);
        byte[] copy = (byte[])data.Clone();
        int length = data.Length;
        Assert.True(server.TryOpen(data, ref length));
        length = copy.Length;
        Assert.False(server.TryOpen(copy, ref length));
    }

    [Fact]
    public void ACounterSixtyFourBehind_IsRejected_ButSixtyThreeBehindIsAccepted()
    {
        using var client = new SessionKeys(Key(6), isServer: false);
        using var server = new SessionKeys(Key(6), isServer: true);
        var sent = new byte[70][];
        for (int i = 0; i < sent.Length; i++) sent[i] = Sealed(client);   // counters 0..69
        int length = sent[69].Length;
        Assert.True(server.TryOpen(sent[69], ref length));                 // newest first
        length = sent[6].Length;
        Assert.True(server.TryOpen(sent[6], ref length));                  // 63 behind
        length = sent[5].Length;
        Assert.False(server.TryOpen(sent[5], ref length));                 // 64 behind
    }

    [Fact]
    public void AnOutOfOrderCounterInsideTheWindow_IsAccepted_Once()
    {
        using var client = new SessionKeys(Key(7), isServer: false);
        using var server = new SessionKeys(Key(7), isServer: true);
        byte[] a = Sealed(client), b = Sealed(client), c = Sealed(client);
        int length = c.Length;
        Assert.True(server.TryOpen(c, ref length));
        byte[] aCopy = (byte[])a.Clone();
        length = a.Length;
        Assert.True(server.TryOpen(a, ref length));
        length = b.Length;
        Assert.True(server.TryOpen(b, ref length));
        length = aCopy.Length;
        Assert.False(server.TryOpen(aCopy, ref length));
    }

    // A forged datagram (bad MAC) does not move the window: the real one with that counter still opens afterwards.
    [Fact]
    public void AForgedDatagram_DoesNotMoveTheWindow()
    {
        using var client = new SessionKeys(Key(8), isServer: false);
        using var server = new SessionKeys(Key(8), isServer: true);
        byte[] first = Sealed(client);
        byte[] second = Sealed(client);
        byte[] forged = (byte[])second.Clone();
        forged[0] ^= 0xFF;
        int length = forged.Length;
        Assert.False(server.TryOpen(forged, ref length));
        length = first.Length;
        Assert.True(server.TryOpen(first, ref length));
        length = second.Length;
        Assert.True(server.TryOpen(second, ref length));
    }

    // Several threads seal at once (LiteNetLib sends from more than one thread): every datagram opens once, no counter twice.
    [Fact]
    public void ConcurrentSeals_NeverRepeatACounter()
    {
        using var client = new SessionKeys(Key(9), isServer: false);
        var datagrams = new ConcurrentBag<byte[]>();
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < 2000; i++) datagrams.Add(Sealed(client, 12));
            });
            threads[t].Start();
        }
        foreach (var thread in threads) thread.Join();

        var counters = new System.Collections.Generic.HashSet<uint>();
        using var check = new SessionKeys(Key(9), isServer: true);
        foreach (byte[] d in datagrams)
        {
            Assert.True(counters.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12, 4))));
            using var fresh = new SessionKeys(Key(9), isServer: true);   // each opens on its own (MAC check, no window)
            int length = d.Length;
            Assert.True(fresh.TryOpen(d, ref length));
        }
        Assert.Equal(8000, counters.Count);
    }

    [Fact]
    public void SealAndOpen_AllocateNothing()
    {
        using var client = new SessionKeys(Key(10), isServer: false);
        using var server = new SessionKeys(Key(10), isServer: true);
        byte[] data = Datagram(100);
        int length = 100;
        client.Seal(data, 0, ref length);
        server.TryOpen(data, ref length);   // warm up both paths

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            length = 100;
            client.Seal(data, 0, ref length);
            server.TryOpen(data, ref length);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TheKeylessTail_IsZeros_AndIsStrippedUnchecked()
    {
        byte[] data = Datagram(16, fill: 0x55);
        for (int i = 16; i < data.Length; i++) data[i] = 0xEE;
        int length = 16;
        SessionAuth.WriteUnsignedTail(data, 0, ref length);
        Assert.Equal(16 + ProtocolLimits.AuthTagBytes, length);
        for (int i = 16; i < length; i++) Assert.Equal(0, data[i]);
        Assert.True(SessionAuth.TryStripUnverified(ref length));
        Assert.Equal(16, length);
        int tiny = ProtocolLimits.AuthTagBytes - 1;
        Assert.False(SessionAuth.TryStripUnverified(ref tiny));
    }

    [Fact]
    public void ResumeProof_VerifiesOnlyWithTheSameKeyNonceSessionAndName()
    {
        byte[] resumeKey = SessionAuth.DeriveResumeKey(Key(11));
        using (var keys = new SessionKeys(Key(11), isServer: true)) Assert.Equal(resumeKey, keys.ResumeKey);
        byte[] newSession = Key(12);
        var proof = new byte[ProtocolLimits.ResumeProofBytes];
        SessionAuth.ComputeResumeProof(resumeKey, 3, newSession, "alice", proof);

        Assert.True(SessionAuth.VerifyResumeProof(resumeKey, 3, newSession, "alice", proof));
        Assert.False(SessionAuth.VerifyResumeProof(SessionAuth.DeriveResumeKey(Key(13)), 3, newSession, "alice", proof));
        Assert.False(SessionAuth.VerifyResumeProof(resumeKey, 4, newSession, "alice", proof));
        Assert.False(SessionAuth.VerifyResumeProof(resumeKey, 3, Key(14), "alice", proof));   // a proof seen on the wire, another connection
        Assert.False(SessionAuth.VerifyResumeProof(resumeKey, 3, newSession, "alicf", proof));
        Assert.False(SessionAuth.VerifyResumeProof(Array.Empty<byte>(), 3, newSession, "alice", proof));
    }

    [Fact]
    public void AWrongSizeSessionKey_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SessionKeys(new byte[16], isServer: true));
    }

    // The Unity client ships the same development public key (Resources/ServerPublicKey.txt) as the bots' default.
    [Fact]
    public void TheClientsPublicKeyFile_IsTheDevPublicKey()
    {
        string? dir = AppContext.BaseDirectory;
        string? file = null;
        while (dir != null && file == null)
        {
            string candidate = System.IO.Path.Combine(dir, "Client", "Assets", "Resources", "ServerPublicKey.txt");
            if (System.IO.File.Exists(candidate)) file = candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        Assert.NotNull(file);
        Assert.Equal(DevServerPublicKey.Xml, System.IO.File.ReadAllText(file!).Trim());
    }

    // The public key the bots and the test client use by default is the public half of the committed development key.
    [Fact]
    public void TheDevPublicKey_IsThePublicHalfOfTheDevKeyFile()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "keys", "dev-server-key.xml");
        using var rsa = RSA.Create();
        rsa.FromXmlString(System.IO.File.ReadAllText(path));
        Assert.Equal(2048, rsa.KeySize);
        Assert.Equal(DevServerPublicKey.Xml, rsa.ToXmlString(false));

        using var pub = RSA.Create();
        pub.FromXmlString(DevServerPublicKey.Xml);
        byte[] blob = pub.Encrypt(Key(15), RSAEncryptionPadding.OaepSHA1);
        Assert.Equal(ProtocolLimits.RsaBlobBytes, blob.Length);
        Assert.Equal(Key(15), rsa.Decrypt(blob, RSAEncryptionPadding.OaepSHA1));
    }
}
