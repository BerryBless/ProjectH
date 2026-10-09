using System;
using System.Net;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Review fix A3 (SEC-4): the connect cookie is HMAC(server secret, address, port, 30 s window). A cookie from the current
// or the previous window verifies; anything else does not.
public class ConnectCookieTests
{
    private static readonly byte[] Secret = new byte[32];
    private static readonly IPEndPoint Alice = new(IPAddress.Parse("10.0.0.1"), 50000);

    // 기능: 테스트 공용 비밀 키 32 B를 정해진 값으로 채운다.
    // 입력: 없음.
    // 출력: 반환값 없음. Secret이 채워진다.
    static ConnectCookieTests()
    {
        for (int i = 0; i < Secret.Length; i++) Secret[i] = (byte)(i * 7 + 1);
    }

    // 기능: 쿠키 하나를 새 배열로 만든다.
    // 입력: cookie - 만들 ConnectCookie, endPoint - 주소와 포트, nowMs - 시각.
    // 출력: 16 B 쿠키.
    private static byte[] Make(ConnectCookie cookie, IPEndPoint endPoint, long nowMs)
    {
        var bytes = new byte[ProtocolLimits.CookieBytes];
        cookie.Make(endPoint, nowMs, bytes);
        return bytes;
    }

    [Fact]
    public void ACookie_FromTheSameWindow_Verifies()
    {
        var cookie = new ConnectCookie(Secret);
        byte[] made = Make(cookie, Alice, 1000);
        Assert.True(cookie.Verify(Alice, 1000, made));
        Assert.True(cookie.Verify(Alice, ConnectCookie.WindowMs - 1, made));
    }

    [Fact]
    public void ACookie_FromThePreviousWindow_StillAccepts()
    {
        var cookie = new ConnectCookie(Secret);
        byte[] made = Make(cookie, Alice, ConnectCookie.WindowMs - 1);   // the last moment of a window
        Assert.True(cookie.Verify(Alice, ConnectCookie.WindowMs, made));   // the first moment of the next one
        Assert.True(cookie.Verify(Alice, ConnectCookie.WindowMs - 1 + 30000, made));
    }

    [Fact]
    public void ACookie_TwoWindowsOld_Fails()
    {
        var cookie = new ConnectCookie(Secret);
        byte[] made = Make(cookie, Alice, 1000);
        Assert.False(cookie.Verify(Alice, 1000 + 2 * ConnectCookie.WindowMs, made));
    }

    [Fact]
    public void ACookie_ForAnotherPortOrAddress_Fails()
    {
        var cookie = new ConnectCookie(Secret);
        byte[] made = Make(cookie, Alice, 1000);
        Assert.False(cookie.Verify(new IPEndPoint(Alice.Address, Alice.Port + 1), 1000, made));
        Assert.False(cookie.Verify(new IPEndPoint(IPAddress.Parse("10.0.0.2"), Alice.Port), 1000, made));
    }

    [Fact]
    public void ACookie_FromAnotherSecret_Fails_AndAWrongLengthFails()
    {
        var other = new byte[32];
        other[0] = 1;
        byte[] made = Make(new ConnectCookie(other), Alice, 1000);
        var cookie = new ConnectCookie(Secret);
        Assert.False(cookie.Verify(Alice, 1000, made));
        Assert.False(cookie.Verify(Alice, 1000, Make(cookie, Alice, 1000).AsSpan(0, 15)));
    }

    [Fact]
    public void TheSecret_MustBe32Bytes()
    {
        Assert.Throws<ArgumentException>(() => new ConnectCookie(new byte[16]));
    }
}
