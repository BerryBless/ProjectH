using System;
using NUnit.Framework;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // Review fix B3, B4: the Shared datagram rule run by Unity's runtime (Mono HMACSHA256.TryComputeHash, FixedTimeEquals):
    // a sealed datagram opens on the other side, a changed byte or a replay does not, and the resume proof checks.
    public class SessionAuthTests
    {
        private const int Tail = ProtocolLimits.AuthTagBytes;

        // 기능: 시험용 세션 키 32 B를 만든다.
        // 입력: seed - 첫 바이트 값(키마다 다르게).
        // 출력: seed, seed+1, ... 로 채운 32 B.
        private static byte[] Key(byte seed)
        {
            var key = new byte[ProtocolLimits.SessionKeyBytes];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
            return key;
        }

        // 기능: 본문 뒤에 꼬리 자리를 둔 데이터그램 버퍼를 만든다.
        // 입력: body - 본문.
        // 출력: 본문이 0부터 들어 있고 뒤에 Tail 바이트 여유가 있는 배열.
        private static byte[] Datagram(byte[] body)
        {
            var data = new byte[body.Length + Tail];
            Buffer.BlockCopy(body, 0, data, 0, body.Length);
            return data;
        }

        [Test]
        public void ASealedDatagram_OpensOnTheOtherSide_BothDirections()
        {
            using (var client = new SessionKeys(Key(1), isServer: false))
            using (var server = new SessionKeys(Key(1), isServer: true))
            {
                byte[] body = { 10, 20, 30, 40, 50 };
                byte[] up = Datagram(body);
                int length = body.Length;
                client.Seal(up, 0, ref length);
                Assert.AreEqual(body.Length + Tail, length);
                Assert.IsTrue(server.TryOpen(up, ref length));
                Assert.AreEqual(body.Length, length);
                CollectionAssert.AreEqual(body, new ArraySegment<byte>(up, 0, length));

                byte[] down = Datagram(body);
                length = body.Length;
                server.Seal(down, 0, ref length);
                Assert.IsTrue(client.TryOpen(down, ref length));
                Assert.AreEqual(body.Length, length);
            }
        }

        [Test]
        public void AChangedByte_OrAnotherKey_DoesNotOpen()
        {
            using (var client = new SessionKeys(Key(1), isServer: false))
            using (var server = new SessionKeys(Key(1), isServer: true))
            using (var stranger = new SessionKeys(Key(99), isServer: true))
            {
                byte[] body = { 1, 2, 3, 4 };
                byte[] data = Datagram(body);
                int length = body.Length;
                client.Seal(data, 0, ref length);
                byte[] copy = (byte[])data.Clone();
                int copyLength = length;

                data[1] ^= 0x40;
                Assert.IsFalse(server.TryOpen(data, ref length));
                Assert.AreEqual(body.Length + Tail, length);   // a refused datagram keeps its length (the caller drops it)
                Assert.AreEqual(1, server.Rejected);

                Assert.IsFalse(stranger.TryOpen(copy, ref copyLength));
                Assert.IsTrue(server.TryOpen(copy, ref copyLength));   // the untouched copy still opens with the right key
            }
        }

        [Test]
        public void AReplayedDatagram_IsRefused_ButOutOfOrderOnesInTheWindowOpen()
        {
            using (var client = new SessionKeys(Key(7), isServer: false))
            using (var server = new SessionKeys(Key(7), isServer: true))
            {
                byte[] body = { 5, 6, 7 };
                byte[][] sent = new byte[3][];
                for (int i = 0; i < sent.Length; i++)
                {
                    sent[i] = Datagram(body);
                    int sealedLength = body.Length;
                    client.Seal(sent[i], 0, ref sealedLength);
                }
                int sealedSize = body.Length + Tail;

                int length = sealedSize;
                Assert.IsTrue(server.TryOpen((byte[])sent[2].Clone(), ref length));
                length = sealedSize;
                Assert.IsTrue(server.TryOpen((byte[])sent[0].Clone(), ref length));   // older, inside the 64 window
                length = sealedSize;
                Assert.IsFalse(server.TryOpen((byte[])sent[2].Clone(), ref length));  // the same counter again
                length = sealedSize;
                Assert.IsTrue(server.TryOpen((byte[])sent[1].Clone(), ref length));
            }
        }

        [Test]
        public void TheUnsignedTail_IsZeroes_AndStripsWithoutAKey()
        {
            byte[] data = Datagram(new byte[] { 9, 9 });
            for (int i = 2; i < data.Length; i++) data[i] = 0xFF;
            int length = 2;
            SessionAuth.WriteUnsignedTail(data, 0, ref length);
            Assert.AreEqual(2 + Tail, length);
            for (int i = 2; i < data.Length; i++) Assert.AreEqual(0, data[i]);
            Assert.IsTrue(SessionAuth.TryStripUnverified(ref length));
            Assert.AreEqual(2, length);
            int shortLength = Tail - 1;
            Assert.IsFalse(SessionAuth.TryStripUnverified(ref shortLength));
        }

        [Test]
        public void TheResumeProof_ChecksOnlyWithTheSameKeyNonceSessionKeyAndName()
        {
            byte[] resumeKey;
            using (var first = new SessionKeys(Key(3), isServer: false)) resumeKey = first.ResumeKey;
            CollectionAssert.AreEqual(SessionAuth.DeriveResumeKey(Key(3)), resumeKey);
            byte[] newKey = Key(50);
            var proof = new byte[ProtocolLimits.ResumeProofBytes];
            SessionAuth.ComputeResumeProof(resumeKey, 1, newKey, "p1", proof);

            Assert.IsTrue(SessionAuth.VerifyResumeProof(resumeKey, 1, newKey, "p1", proof));
            Assert.IsFalse(SessionAuth.VerifyResumeProof(resumeKey, 2, newKey, "p1", proof));
            Assert.IsFalse(SessionAuth.VerifyResumeProof(resumeKey, 1, Key(51), "p1", proof));
            Assert.IsFalse(SessionAuth.VerifyResumeProof(resumeKey, 1, newKey, "p2", proof));
            Assert.IsFalse(SessionAuth.VerifyResumeProof(SessionAuth.DeriveResumeKey(Key(4)), 1, newKey, "p1", proof));
        }
    }
}
