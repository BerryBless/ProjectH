using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Tests.Ingest;

namespace ProjectH.Monitoring.Tests;

// Request §66: the real app on a loopback port, a fake game server posting, the API read back.
public class MonitoringAppTests : IAsyncLifetime
{
    private readonly ManualTime _time = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    // 기능: Token "secret"을 요구하는 실제 앱을 빈 loopback 포트에 시작하고 그 주소의 HttpClient를 만든다.
    // 입력: 없음.
    // 출력: 반환값 없음. _app이 시작되고 _client가 준비된다.
    public async Task InitializeAsync()
    {
        _app = MonitoringApp.Create(["--Urls=http://127.0.0.1:0", "--MonitoringServer:IngestToken=secret", "--MonitoringServer:SweepIntervalSeconds=1"], _time);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    // 기능: HttpClient를 버리고 앱을 멈춘 뒤 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 포트와 OfflineSweeper가 정리된다.
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // 기능: 테스트 시계의 지금 관측된 유효한 Snapshot을 만든다.
    // 입력: id - 서버 이름.
    // 출력: ObservedAt = 지금, StartedAt = 1분 전인 Snapshot.
    private ServerMonitoringSnapshot Snapshot(string id = "dev-server-01") =>
        SnapshotValidatorTests.Valid(id, _time.GetUtcNow()) with { StartedAt = _time.GetUtcNow().AddMinutes(-1) };

    // 기능: 본문을 JSON으로 Ingest 경로에 POST한다.
    // 입력: body - 보낼 값, token - X-Monitoring-Token 헤더 값(null = 헤더 없음).
    // 출력: 서버 응답.
    private async Task<HttpResponseMessage> PostAsync(object body, string? token = "secret")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };
        if (token != null) request.Headers.Add(MonitoringContract.TokenHeader, token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ValidPost_IsAccepted_AndReadBackThroughEveryApi()
    {
        var s = Snapshot() with { Players = 5, TickP95Ms = 0.42 };
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(s)).StatusCode);

        using JsonDocument list = JsonDocument.Parse(await _client.GetStringAsync("/api/servers"));
        JsonElement first = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("dev-server-01", first.GetProperty("serverId").GetString());
        Assert.Equal("online", first.GetProperty("status").GetString());
        Assert.Equal(5, first.GetProperty("latest").GetProperty("players").GetInt32());
        Assert.Equal(_time.GetUtcNow(), first.GetProperty("lastReceivedAt").GetDateTimeOffset());

        using JsonDocument detail = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01"));
        Assert.Equal(0.42, detail.RootElement.GetProperty("latest").GetProperty("tickP95Ms").GetDouble());
        Assert.Equal(16.7, detail.RootElement.GetProperty("thresholds").GetProperty("tickP95WarningMs").GetDouble());

        using JsonDocument history = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=5"));
        Assert.Equal(5, history.RootElement.GetProperty("minutes").GetInt32());
        JsonElement sample = Assert.Single(history.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal(5, sample.GetProperty("snapshot").GetProperty("players").GetInt32());
        Assert.Equal(_time.GetUtcNow(), sample.GetProperty("receivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task UnknownServer_Is404_AndMinutesAreClamped()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/servers/nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/servers/nobody/metrics")).StatusCode);
        await PostAsync(Snapshot());
        using JsonDocument history = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=9999"));
        Assert.Equal(10, history.RootElement.GetProperty("minutes").GetInt32());
        // A non-number is not a 400 either: the default (5) is used.
        using JsonDocument text = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=abc"));
        Assert.Equal(5, text.RootElement.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task WrongOrMissingToken_Is401_AndStoresNothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Snapshot(), token: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Snapshot(), token: null)).StatusCode);
        Assert.Equal("[]", await _client.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task InvalidJson_InvalidValue_AndNaN_Are400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
        request.Headers.Add(MonitoringContract.TokenHeader, "secret");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Snapshot() with { Players = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Snapshot() with { ObservedAt = _time.GetUtcNow().AddHours(1) })).StatusCode);

        string nan = JsonSerializer.Serialize(Snapshot(), JsonSerializerOptions.Web).Replace("\"tickP95Ms\":0.4", "\"tickP95Ms\":NaN");
        Assert.Contains("NaN", nan);
        var nanRequest = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent(nan, Encoding.UTF8, "application/json") };
        nanRequest.Headers.Add(MonitoringContract.TokenHeader, "secret");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(nanRequest)).StatusCode);
        Assert.Equal("[]", await _client.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task OversizedBody_IsRefused()
    {
        string big = "{\"serverId\":\"dev-server-01\",\"version\":\"" + new string('x', MonitoringContract.MaxBodyBytes + 1024) + "\"}";
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent(big, Encoding.UTF8, "application/json") };
        request.Headers.Add(MonitoringContract.TokenHeader, "secret");
        // The server answers 413 and then Kestrel aborts the connection (it will not drain an over-limit body). On Windows
        // loopback the reset can arrive before the client has read the 413 (measured: about half the runs, with or
        // without Expect: 100-continue), so a reset is also a refusal here. Either way nothing may be stored.
        try
        {
            HttpResponseMessage response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            // connection reset by the server after the 413
        }
        Assert.Equal("[]", await _client.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task TwoServers_AreSeparate_AndOfflineIsDecidedByTime()
    {
        await PostAsync(Snapshot("a") with { Players = 1 });
        await PostAsync(Snapshot("b") with { Players = 2 });
        _time.Advance(TimeSpan.FromSeconds(10));
        await PostAsync(Snapshot("b") with { Players = 3 });
        _time.Advance(TimeSpan.FromSeconds(6));   // a: 16 s silent (> 15), b: 6 s
        using JsonDocument list = JsonDocument.Parse(await _client.GetStringAsync("/api/servers"));
        JsonElement[] servers = list.RootElement.EnumerateArray().ToArray();
        Assert.Equal("a", servers[0].GetProperty("serverId").GetString());
        Assert.Equal("offline", servers[0].GetProperty("status").GetString());
        Assert.Equal(1, servers[0].GetProperty("latest").GetProperty("players").GetInt32());
        Assert.Equal("online", servers[1].GetProperty("status").GetString());
        Assert.Equal(3, servers[1].GetProperty("latest").GetProperty("players").GetInt32());
    }

    [Fact]
    public async Task Health_Answers()
    {
        using JsonDocument health = JsonDocument.Parse(await _client.GetStringAsync("/health"));
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, health.RootElement.GetProperty("servers").GetInt32());
    }

    [Fact]
    public void InvalidOptions_StopTheAppAtCreate()
    {
        Assert.Throws<InvalidOperationException>(() => MonitoringApp.Create(["--Urls=http://127.0.0.1:0", "--MonitoringServer:HistoryMinutes=0"]));
    }
}
