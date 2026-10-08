using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Monitoring;

// Monitoring D5: every IntervalSeconds, takes the latest snapshot out of the slot and posts it with a short timeout. A
// failure drops that snapshot (the next interval has a newer one) and is logged only when the state changes: one
// Warning when the connection is lost, one Information when it is back. Shutdown cancels the post in flight and does
// not wait to send a last snapshot. Registered after GameServerService, so it stops before the game loop. It never
// touches the game loop; only the slot is shared.
// Lifetime: created by the host (MonitoringSetup) only when Monitoring:Enabled; owns one HttpClient, disposed with the
// service. One post at a time (the loop awaits it), each with its own linked CancellationTokenSource, disposed after it.
// No lock: _lost and _failuresSinceLost are written by the sender task only; Sent/Failed/Lost are read by tests.
// Review fix 1 (request §7): only the status line of the answer is used; its body is never read (ResponseHeadersRead), so a
// wrong endpoint that answers with a large or endless body costs no buffer here. Disposing the unread response lets
// SocketsHttpHandler drain at most its MaxResponseDrainSize (1 MB default, 2 s, through a small fixed buffer, nothing
// kept) to reuse the connection, else it closes it.
public sealed class MonitoringSender : BackgroundService
{
    private readonly MonitoringSlot _slot;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _ingest;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private long _sent;
    private long _failed;
    private bool _lost;                // sender task only (tests read it through Lost)
    private long _failuresSinceLost;   // sender task only

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public bool Lost => Volatile.Read(ref _lost);

    // 기능: 전송기를 만든다. HttpClient 하나(SocketsHttpHandler, 연결 수명 5분)를 소유한다(Dispose가 닫는다).
    // 입력: options - 검증이 끝난 설정, slot - Game Loop가 채우는 슬롯, logger - 로그, interval·timeout - 테스트용(null = 설정값).
    // 출력: Start 전의 MonitoringSender.
    public MonitoringSender(MonitoringOptions options, MonitoringSlot slot, ILogger logger, TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        _slot = slot;
        _logger = logger;
        _ingest = options.IngestUri;
        _interval = interval ?? TimeSpan.FromSeconds(options.IntervalSeconds);
        _timeout = timeout ?? TimeSpan.FromSeconds(options.TimeoutSeconds);
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,   // the per-request token below is the timeout
        };
        if (options.Token.Length > 0) _http.DefaultRequestHeaders.Add(MonitoringContract.TokenHeader, options.Token);
    }

    // 기능: 주기마다 슬롯에서 Snapshot을 꺼내 보낸다. 비어 있으면 보내지 않는다(Game Loop가 멈추면 아무것도 가지 않는다).
    // 입력: stoppingToken - 호스트 종료.
    // 출력: 호스트가 멈출 때 끝나는 Task. 예외로 끝나지 않는다.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ServerMonitoringSnapshot? snapshot = _slot.Take();
                if (snapshot == null) continue;
                try
                {
                    await SendAsync(snapshot, stoppingToken);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // SendAsync catches every post failure; only its logger can get here. An exception leaving ExecuteAsync
                    // would stop the whole host (BackgroundServiceExceptionBehavior.StopHost): monitoring must never.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    // 기능: Snapshot 하나를 JSON으로 POST하고 응답 헤더의 상태 코드만으로 성공(2xx)·실패를 정한다(응답 본문은 읽지 않는다). 어떤 실패도
    //       예외로 나가지 않고 상태 변화 때만 로그한다(끊김 Warning 1회, 복구 Information 1회).
    // 입력: snapshot - 보낼 값, stoppingToken - 호스트 종료(진행 중 요청을 취소한다).
    // 출력: 끝나면 완료되는 Task. 성공이면 Sent, 실패면 Failed가 1 늘어난다. 종료로 취소되면 둘 다 그대로다.
    private async Task SendAsync(ServerMonitoringSnapshot snapshot, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(_timeout);
        string? failure = null;
        try
        {
            // JsonContent serializes with JsonSerializerOptions.Web, as PostAsJsonAsync did. ResponseHeadersRead: the call
            // returns at the headers and the body is never read (the default, ResponseContentRead, would buffer all of it).
            using var request = new HttpRequestMessage(HttpMethod.Post, _ingest) { Content = JsonContent.Create(snapshot) };
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) failure = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;   // shutdown: neither sent nor failed
        }
        catch (OperationCanceledException)
        {
            failure = $"timeout after {_timeout.TotalSeconds:0.#} s";
        }
        catch (Exception ex)
        {
            // HttpRequestException (refused, DNS), JSON, anything: monitoring must never take the service down.
            failure = ex.Message;
        }

        if (failure == null)
        {
            Interlocked.Increment(ref _sent);
            if (_lost)
            {
                Volatile.Write(ref _lost, false);
                _logger.LogInformation("Monitoring connection restored after {Failures} failed posts", _failuresSinceLost);
            }
            return;
        }
        Interlocked.Increment(ref _failed);
        if (!_lost)
        {
            Volatile.Write(ref _lost, true);
            _failuresSinceLost = 0;
            _logger.LogWarning("Monitoring connection lost ({Reason}); posting to {Endpoint} is retried every {Interval:0.#} s without further logs",
                failure, _ingest, _interval.TotalSeconds);
        }
        _failuresSinceLost++;
        _logger.LogDebug("Monitoring post failed ({Reason})", failure);
    }

    // 기능: HttpClient를 닫고 BackgroundService를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 이후 전송은 없다.
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
