using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Tests.Ingest;

// The ingest decisions without a socket: over loopback Kestrel may reset the connection before the client reads the 413
// (MonitoringAppTests.OversizedBody_IsRefused), and a truncated or broken body cannot be produced reliably through
// HttpClient, so the status codes are pinned here with a DefaultHttpContext.
public class IngestEndpointTests
{
    // A request body that fails the way Kestrel's body reader does (truncated body, broken chunked encoding, over the limit).
    private sealed class FailingBody(int statusCode) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // 기능: 읽을 때마다 Kestrel과 같은 BadHttpRequestException을 던진다.
        // 입력: buffer·offset·count - 무시한다.
        // 출력: 반환하지 않는다(항상 예외).
        public override int Read(byte[] buffer, int offset, int count) => throw new BadHttpRequestException("Unexpected end of request content.", statusCode);

        // 기능: 비동기 읽기도 같은 예외로 끝낸다(JsonSerializer.DeserializeAsync가 이 경로를 쓴다).
        // 입력: buffer - 무시한다, cancellationToken - 무시한다.
        // 출력: 반환하지 않는다(항상 예외).
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Unexpected end of request content.", statusCode);
    }

    // 기능: 테스트 시계·저장소·로그로 HandleAsync를 한 번 부른다(Token "secret" 설정).
    // 입력: context - 요청, time - 시계, store - 저장소, logger - IngestLog가 쓸 로그(null = 버림).
    // 출력: HandleAsync가 돌려준 HTTP 상태 코드.
    private static async Task<int> HandleAsync(HttpContext context, ManualTime time, MetricStore store, ILogger<IngestLog>? logger = null)
    {
        IResult result = await IngestEndpoint.HandleAsync(context, new MonitoringServerOptions { IngestToken = "secret" }, store, time,
            new IngestLog(logger ?? NullLogger<IngestLog>.Instance, time), NullLogger<MetricStore>.Instance, CancellationToken.None);
        return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;
    }

    // 기능: Token 헤더가 붙은 POST 요청 문맥을 만든다.
    // 입력: body - 요청 본문 스트림.
    // 출력: Body와 X-Monitoring-Token이 설정된 DefaultHttpContext.
    private static DefaultHttpContext Authorized(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[MonitoringContract.TokenHeader] = "secret";
        context.Request.Body = body;
        return context;
    }

    [Fact]
    public async Task DeclaredLengthOverTheLimit_Is413_BeforeTheTokenCheck_AndStoresNothing()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions());
        var context = new DefaultHttpContext();
        context.Request.ContentLength = MonitoringContract.MaxBodyBytes + 1;   // no token header either: size comes first (D8)

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, await HandleAsync(context, time, store));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]           // shorter than Content-Length, broken chunked encoding
    [InlineData(StatusCodes.Status413PayloadTooLarge)]      // over the limit while being read (no Content-Length)
    public async Task ABodyKestrelRefuses_IsAnsweredWithKestrelsStatus_AndStoresNothing(int statusCode)
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions());
        var logger = new IngestLogTests.ListLogger();

        Assert.Equal(statusCode, await HandleAsync(Authorized(new FailingBody(statusCode)), time, store, logger));
        Assert.Equal(0, store.Count);
        string line = Assert.Single(logger.Lines);
        Assert.Contains(statusCode == StatusCodes.Status413PayloadTooLarge ? "body too large" : "bad request body", line);
    }

    [Fact]
    public async Task AnInvalidServerId_IsNotWrittenToTheLog()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions());
        var logger = new IngestLogTests.ListLogger();
        string badId = "forged\r\nINFO fake line \u001b[31m" + new string('x', 200);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(SnapshotValidatorTests.Valid(badId, time.GetUtcNow()) with { StartedAt = time.GetUtcNow().AddMinutes(-1) },
            JsonSerializerOptions.Web);

        Assert.Equal(StatusCodes.Status400BadRequest, await HandleAsync(Authorized(new MemoryStream(json)), time, store, logger));
        Assert.Equal(0, store.Count);
        string line = Assert.Single(logger.Lines);
        Assert.Contains("serverId", line);
        Assert.Contains("from ?", line);
        Assert.DoesNotContain("forged", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public async Task AValidServerId_IsStillWrittenWhenAnotherValueIsRejected()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions());
        var logger = new IngestLogTests.ListLogger();
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            SnapshotValidatorTests.Valid("dev-server-01", time.GetUtcNow()) with { StartedAt = time.GetUtcNow().AddMinutes(-1), Players = -1 }, JsonSerializerOptions.Web));

        Assert.Equal(StatusCodes.Status400BadRequest, await HandleAsync(Authorized(new MemoryStream(json)), time, store, logger));
        Assert.Contains("from dev-server-01", Assert.Single(logger.Lines));
    }
}
