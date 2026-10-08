using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using LiteNetLib.Layers;
using ProjectH.Shared.Protocol;

namespace ProjectH.Bots;

// Review fix B3 (D0-5: the bots and the test client follow the Unity client's rule): the client side of the datagram
// authentication, a thin PacketLayerBase over Shared SessionKeys (wire rule in SessionAuth). A client talks to one server,
// so it holds one set of keys, not a table.
//   - Use(keys) before every Connect (the keys of that request's session key). From then on every datagram is sealed.
//   - Received: a datagram that opens marks the connection verified. Before the first one, a datagram that does not open is
//     stripped unverified (the server's cookie or reject answer comes with a zero tail, the server having no keys yet).
//     After it, one that does not open is dropped and counted (AuthDrops).
// Threads: Use runs on the caller's thread before Connect; the callbacks run on LiteNetLib's threads (the receive thread
// for inbound; in manual mode everything is the caller's thread). _keys and _verified are volatile. Keys replaced by Use
// are not disposed (a send may still be using them); the finalizers free them.
public sealed class AuthPacketLayer : PacketLayerBase
{
    private volatile SessionKeys? _keys;
    private volatile bool _verified;
    private long _authDrops;

    // 기능: Client 인증 계층을 만든다(꼬리 20 B).
    // 입력: 없음.
    // 출력: 키가 없는 계층(모든 송신은 0 꼬리, 수신은 검증 없이 벗긴다).
    public AuthPacketLayer() : base(ProtocolLimits.AuthTagBytes)
    {
    }

    // Datagrams dropped after the first verified one because their tail failed.
    public long AuthDrops => Interlocked.Read(ref _authDrops);
    public bool Verified => _verified;
    public SessionKeys? Keys => _keys;

    // 기능: 다음 연결 요청의 세션 키를 쓴다(Connect 직전). 검증 상태를 처음으로 되돌린다.
    // 입력: keys - 이 요청의 세션 키에서 만든 Client 쪽 키.
    // 출력: 반환값 없음.
    public void Use(SessionKeys keys)
    {
        _verified = false;
        _keys = keys;
    }

    // 기능: 받은 데이터그램의 꼬리를 검증하고 벗긴다(첫 검증 성공 전에는 실패해도 검증 없이 벗긴다).
    // 입력: endPoint - 보낸 곳(서버 하나), data·length - 데이터그램.
    // 출력: 반환값 없음. 통과면 length가 꼬리만큼 줄고, 버리면 length = 0.
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        SessionKeys? keys = _keys;
        if (keys != null && keys.TryOpen(data, ref length))
        {
            _verified = true;
            return;
        }
        if (keys != null && _verified)
        {
            Interlocked.Increment(ref _authDrops);
            length = 0;
            return;
        }
        if (!SessionAuth.TryStripUnverified(ref length)) length = 0;
    }

    // 기능: 보낼 데이터그램에 꼬리를 붙인다(키가 있으면 봉인, 없으면 0 꼬리).
    // 입력: endPoint - 받는 곳, data·offset·length - 보낼 부분.
    // 출력: 반환값 없음. length가 AuthTagBytes 는다.
    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (data.Length - offset - length < ProtocolLimits.AuthTagBytes)
        {
            var bigger = new byte[offset + length + ProtocolLimits.AuthTagBytes];
            Buffer.BlockCopy(data, 0, bigger, 0, offset + length);
            data = bigger;
        }
        SessionKeys? keys = _keys;
        if (keys != null) keys.Seal(data, offset, ref length);
        else SessionAuth.WriteUnsignedTail(data, offset, ref length);
    }
}

// Review fix B4: what a bot (or the test client) keeps across its connections to resume its character: the resume key of
// the connection that last joined or resumed, and the nonce of the last resume attempt made with it. One per bot, owned by
// whoever reconnects it (BotRunner, the QA actor, a test).
public sealed class ResumeTicket
{
    public byte[]? ResumeKey { get; private set; }
    public uint Nonce { get; private set; }

    // 기능: Join이 Ok·Resumed로 끝난 연결의 Resume 키를 보관한다(nonce는 0부터).
    // 입력: resumeKey - 그 연결의 SessionKeys.ResumeKey.
    // 출력: 반환값 없음.
    public void Joined(byte[] resumeKey)
    {
        ResumeKey = resumeKey;
        Nonce = 0;
    }

    // 기능: 다음 연결 요청의 Resume 증명을 만든다(보관한 키가 있을 때만, nonce를 1 올린다).
    // 입력: sessionKey - 새 연결의 세션 키, devPlayerId - 이름, proof - 받을 16 B.
    // 출력: 만들었으면 true와 nonce, 키가 없으면 false.
    public bool TryMakeProof(ReadOnlySpan<byte> sessionKey, string devPlayerId, Span<byte> proof, out uint nonce)
    {
        nonce = 0;
        if (ResumeKey == null) return false;
        nonce = ++Nonce;
        SessionAuth.ComputeResumeProof(ResumeKey, nonce, sessionKey, devPlayerId, proof);
        return true;
    }
}

// Review fix B2: the client half of the connect request's key exchange, shared by the bots and the test client.
public static class SessionKeyExchange
{
    // 기능: 새 32 B 세션 키를 만들고 서버 공개키(XML)로 RSA-OAEP-SHA1 암호화한다.
    // 입력: serverPublicKeyXml - 서버 공개키(RSA.ToXmlString(false)).
    // 출력: (세션 키 32 B, blob RsaBlobBytes B).
    public static (byte[] SessionKey, byte[] Blob) Create(string serverPublicKeyXml)
    {
        byte[] sessionKey = RandomNumberGenerator.GetBytes(ProtocolLimits.SessionKeyBytes);
        using var rsa = RSA.Create();
        rsa.FromXmlString(serverPublicKeyXml);
        return (sessionKey, rsa.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1));
    }
}
