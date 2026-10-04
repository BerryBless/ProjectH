using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectH.QA;

// The Unity Development player's QA receiver (Docs/QA.md "Unity Client", D26-D28): GET /qa/status, POST /qa/screenshot,
// POST /qa/ui. 127.0.0.1 only. One per UnityActor; disposed with it. Per-request timeout: the player answers within 5 s
// (its own queue timeout), a screenshot after the PNG is on disk.
public sealed class UnityQaClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http;

    // handler: tests pass a fake endpoint; null = real HTTP.
    public UnityQaClient(int port, HttpMessageHandler? handler = null)
    {
        Port = port;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    public int Port { get; }

    public Task<UnityAnswer> StatusAsync(CancellationToken token) => SendAsync(HttpMethod.Get, "qa/status", null, token);

    public Task<UnityAnswer> ScreenshotAsync(string name, CancellationToken token) => SendAsync(HttpMethod.Post, "qa/screenshot", new { name }, token);

    public Task<UnityAnswer> UiAsync(string command, CancellationToken token) => SendAsync(HttpMethod.Post, "qa/ui", new { command }, token);

    public void Dispose() => _http.Dispose();

    // 기능: Unity Player의 QA 수신기에 요청 하나를 보내고 응답을 읽는다.
    // 입력: method·path - 보낼 HTTP 요청, body - JSON 본문(없으면 null), token - 실행 취소.
    // 출력: 상태 코드와 JSON을 담은 UnityAnswer. 전송 실패나 시간 초과는 QaApiException(안쪽 예외 메시지 포함).
    // Never throws for an HTTP status: the answer carries it (409 for a UI command that does not apply is a step
    // failure, not an error).
    private async Task<UnityAnswer> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            JsonElement? json = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    using JsonDocument doc = JsonDocument.Parse(text);
                    json = doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                json = null;
            }
            string? error = json != null && JsonPath.Child(json.Value, "error") is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
            return new UnityAnswer((int)response.StatusCode, response.StatusCode == HttpStatusCode.OK, json, error);
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !token.IsCancellationRequested))
        {
            throw new QaApiException($"Unity QA {method} /{path} on port {Port}: {(e is OperationCanceledException ? "no answer" : e.Message + (e.InnerException != null ? " (" + e.InnerException.Message + ")" : ""))}", 0, e);
        }
    }
}

public sealed record UnityAnswer(int StatusCode, bool Ok, JsonElement? Json, string? Error);
