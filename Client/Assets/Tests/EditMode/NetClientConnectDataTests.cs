using System.Security.Cryptography;
using NUnit.Framework;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    // Review fixes B1, B2, B3-1, B4: what NetClient puts in a connection request, read back with the server's reader, the
    // session key encryption on Unity's runtime (the Mono RSA assumption of B3-1), and the shipped public key file.
    public class NetClientConnectDataTests
    {
        // 기능: 접속 요청을 Shared Writer로 쓰고 Shared TryRead로 다시 읽는다(서버가 읽는 것과 같다).
        // 입력: request - 쓸 요청.
        // 출력: 읽은 요청. 읽기에 실패하면 테스트 실패.
        private static ConnectRequestData RoundTrip(in ConnectRequestData request)
        {
            var buffer = new byte[ProtocolConstants.MaxPacketSize];
            var writer = new PacketWriter(buffer);
            ConnectRequestData.Write(ref writer, request);
            Assert.IsFalse(writer.Overflowed);
            var reader = new PacketReader(new System.ReadOnlySpan<byte>(buffer, 0, writer.Length));
            Assert.IsTrue(ConnectRequestData.TryRead(ref reader, out ConnectRequestData read));
            return read;
        }

        // 기능: 시험용 세션 키 32 B를 만든다.
        // 입력: seed - 첫 바이트 값.
        // 출력: seed, seed+1, ... 로 채운 32 B.
        private static byte[] Key(byte seed)
        {
            var key = new byte[ProtocolLimits.SessionKeyBytes];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
            return key;
        }

        [Test]
        public void AFirstRequest_CarriesOnlyTheBlobAndTheName()
        {
            var blob = new byte[ProtocolLimits.RsaBlobBytes];
            blob[0] = 0xAB;
            var proof = new byte[ProtocolLimits.ResumeProofBytes];
            ConnectRequestData read = RoundTrip(NetClient.BuildConnectRequest("p1", blob, null, null, 0, Key(1), proof));

            Assert.AreEqual(ProtocolConstants.ProtocolVersion, read.ProtocolVersion);
            Assert.AreEqual(ConnectFlags.None, read.Flags);
            Assert.AreEqual(ProtocolLimits.RsaBlobBytes, read.SessionKeyBlob.Length);
            Assert.AreEqual(0xAB, read.SessionKeyBlob[0]);
            Assert.AreEqual("p1", read.DevPlayerId);
            Assert.IsNull(read.Cookie);
            Assert.IsNull(read.ResumeProof);
        }

        [Test]
        public void ARetryWithACookieAndAResumeKey_CarriesBoth_AndTheProofChecks()
        {
            var blob = new byte[ProtocolLimits.RsaBlobBytes];
            var cookie = new byte[ProtocolLimits.CookieBytes];
            for (int i = 0; i < cookie.Length; i++) cookie[i] = (byte)(i + 1);
            byte[] resumeKey = SessionAuth.DeriveResumeKey(Key(9));
            byte[] sessionKey = Key(40);
            var proof = new byte[ProtocolLimits.ResumeProofBytes];
            ConnectRequestData read = RoundTrip(NetClient.BuildConnectRequest("p1", blob, cookie, resumeKey, 3, sessionKey, proof));

            Assert.AreEqual(ConnectFlags.HasCookie | ConnectFlags.HasResume, read.Flags);
            CollectionAssert.AreEqual(cookie, read.Cookie);
            Assert.AreEqual(ProtocolLimits.RsaBlobBytes, read.SessionKeyBlob.Length);
            Assert.AreEqual(3u, read.ResumeNonce);
            Assert.AreEqual(ProtocolLimits.ResumeProofBytes, read.ResumeProof.Length);
            Assert.IsTrue(SessionAuth.VerifyResumeProof(resumeKey, 3, sessionKey, "p1", read.ResumeProof));
            Assert.IsFalse(SessionAuth.VerifyResumeProof(resumeKey, 3, Key(41), "p1", read.ResumeProof));
        }

        [Test]
        public void TheSessionKey_EncryptsTo256Bytes_AndDecryptsWithThePrivateKey()
        {
            // RSACryptoServiceProvider: the RSA type Unity's Mono gives, with a fresh 2048-bit key pair.
            using (var pair = new RSACryptoServiceProvider(2048))
            using (RSA publicOnly = RSA.Create())
            {
                publicOnly.FromXmlString(pair.ToXmlString(false));
                byte[] key = Key(77);
                byte[] blob = NetClient.EncryptSessionKey(publicOnly, key);
                Assert.AreEqual(ProtocolLimits.RsaBlobBytes, blob.Length);
                CollectionAssert.AreEqual(key, pair.Decrypt(blob, true));   // fOAEP true = OAEP-SHA1, what the server decrypts with
            }
        }

        [Test]
        public void TheShippedPublicKey_IsTheDevelopmentKey_AndEncrypts()
        {
            var asset = Resources.Load<TextAsset>("ServerPublicKey");
            Assert.IsNotNull(asset, "Resources/ServerPublicKey.txt is missing");
            string xml = asset.text.Trim();
            Resources.UnloadAsset(asset);
            // Development builds pin the development key; a release replaces the file (Docs/Client.md) and this test with it.
            Assert.AreEqual(DevServerPublicKey.Xml, xml);
            using (RSA rsa = RSA.Create())
            {
                rsa.FromXmlString(xml);
                Assert.AreEqual(ProtocolLimits.RsaBlobBytes, NetClient.EncryptSessionKey(rsa, Key(5)).Length);
            }
        }
    }
}
