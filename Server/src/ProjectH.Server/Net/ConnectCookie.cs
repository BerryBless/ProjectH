using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Review fix A3 (SEC-4): the connect cookie. A first connection request (no cookie) is answered with a RejectForce that
// carries HMAC-SHA256(secret, address, port, 30 s window number)[0..16]; only a request that repeats a cookie of the
// current or the previous window gets a token, a slot or (package B) an RSA decrypt. A forged source address never sees
// its cookie, so it cannot spend any of them. Nothing is stored per request (stateless).
// Lifetime: one per NetworkListener, made at startup with a random secret (never written, never sent). Threads: the
// secret is read-only and HMACSHA256.HashData is a static one-shot, so any thread may call it (the listener calls it on
// LiteNetLib's receive thread only). No allocation per call.
public sealed class ConnectCookie
{
    public const int WindowMs = 30_000;
    public const int SecretBytes = 32;
    // Address (16 B, IPv4 in the first 4) + port 2 + window number 8.
    private const int MessageBytes = 16 + 2 + 8;

    private readonly byte[] _secret;

    // 기능: 쿠키를 만들고 검사할 비밀을 받는다.
    // 입력: secret - 32 B 비밀(서버는 시작 때 RandomNumberGenerator로 만든다). 복사해 둔다.
    // 출력: 사용 준비가 된 ConnectCookie. 32 B가 아니면 ArgumentException.
    public ConnectCookie(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException($"The cookie secret must be {SecretBytes} bytes.", nameof(secret));
        _secret = secret.ToArray();
    }

    // 기능: 지금 시간 창의 쿠키를 만든다(RejectForce 데이터로 보낸다).
    // 입력: endPoint - 요청한 주소와 포트, nowMs - 단조 증가 ms 시계(Environment.TickCount64), cookie - 받을 곳(16 B 이상).
    // 출력: 반환값 없음. cookie 앞 ProtocolLimits.CookieBytes에 쿠키가 들어간다.
    public void Make(IPEndPoint endPoint, long nowMs, Span<byte> cookie) => MakeForWindow(endPoint, nowMs / WindowMs, cookie);

    // 기능: 받은 쿠키가 이 주소·포트의 지금 또는 바로 전 시간 창 쿠키인지 본다(창 경계에서 받은 쿠키도 통과). 상수 시간 비교.
    // 입력: endPoint - 요청한 주소와 포트, nowMs - 단조 증가 ms 시계, cookie - 요청에 든 쿠키.
    // 출력: 맞으면 true, 길이가 틀리거나 다르면 false.
    public bool Verify(IPEndPoint endPoint, long nowMs, ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length != ProtocolLimits.CookieBytes) return false;
        Span<byte> expected = stackalloc byte[ProtocolLimits.CookieBytes];
        long window = nowMs / WindowMs;
        MakeForWindow(endPoint, window, expected);
        if (CryptographicOperations.FixedTimeEquals(expected, cookie)) return true;
        MakeForWindow(endPoint, window - 1, expected);
        return CryptographicOperations.FixedTimeEquals(expected, cookie);
    }

    // 기능: 주어진 창 번호의 쿠키를 만든다.
    // 입력: endPoint - 주소와 포트, window - 시간 창 번호, cookie - 받을 곳(16 B 이상).
    // 출력: 반환값 없음. cookie 앞 16 B가 채워진다.
    private void MakeForWindow(IPEndPoint endPoint, long window, Span<byte> cookie)
    {
        Span<byte> message = stackalloc byte[MessageBytes];
        message.Clear();
        endPoint.Address.TryWriteBytes(message.Slice(0, 16), out _);
        BinaryPrimitives.WriteUInt16LittleEndian(message.Slice(16), (ushort)endPoint.Port);
        BinaryPrimitives.WriteInt64LittleEndian(message.Slice(18), window);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_secret, message, mac);
        mac.Slice(0, ProtocolLimits.CookieBytes).CopyTo(cookie);
    }
}
