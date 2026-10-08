using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Tests.Monitoring;

// A loopback HTTP server that answers the ingest path with whatever the test sets: a status code and a delay.
public sealed class FakeMonitoringServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _received;
    private volatile string? _lastToken;
    private volatile int _lastBodyBytes;

    public volatile int StatusCode = StatusCodes.Status202Accepted;
    public volatile int DelayMs;

    public Uri Endpoint { get; }
    public int Received => Volatile.Read(ref _received);
    public string? LastToken => _lastToken;
    public int LastBodyBytes => _lastBodyBytes;

    // 기능: 이미 시작한 앱과 그 주소를 묶는다(StartAsync만 부른다).
    // 입력: app - 실행 중인 WebApplication, endpoint - 앱이 듣는 기본 주소.
    // 출력: FakeMonitoringServer.
    private FakeMonitoringServer(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    // 기능: 127.0.0.1의 빈 포트에 가짜 서버를 띄운다. Ingest 경로는 받은 수·Token·본문 크기를 기록하고 DelayMs 뒤 StatusCode로 답한다.
    // 입력: 없음.
    // 출력: 실행 중인 FakeMonitoringServer(Endpoint = 기본 주소).
    public static async Task<FakeMonitoringServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new[] { "--Urls=http://127.0.0.1:0" });
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        FakeMonitoringServer? self = null;
        app.MapPost(MonitoringContract.IngestPath, async (HttpContext ctx) =>
        {
            FakeMonitoringServer s = self!;
            Interlocked.Increment(ref s._received);
            s._lastToken = ctx.Request.Headers.TryGetValue(MonitoringContract.TokenHeader, out var v) ? v.ToString() : null;
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            s._lastBodyBytes = (int)ms.Length;
            if (s.DelayMs > 0) await Task.Delay(s.DelayMs, ctx.RequestAborted);
            return Results.StatusCode(s.StatusCode);
        });
        await app.StartAsync();
        self = new FakeMonitoringServer(app, new Uri(app.Urls.First()));
        return self;
    }

    // 기능: 아무도 듣지 않는 127.0.0.1 포트의 주소를 만든다(Connection Refused 테스트).
    // 입력: 없음.
    // 출력: 방금 닫은 포트의 URI.
    public static Uri ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    // 기능: 가짜 서버를 멈추고 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 포트가 닫힌다.
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
