using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectH.QA;

// The Unity Development player's QA receiver (Docs/QA.md "Unity Client", D26-D28): GET /qa/status, POST /qa/screenshot,
// POST /qa/ui, POST /qa/input (real Input System input, §87). 127.0.0.1 only. One per UnityActor; disposed with it. Per-request timeout: the player answers within 5 s
// (its own queue timeout), a screenshot after the PNG is on disk.
public sealed class UnityQaClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http;

    // 기능: 플레이어 QA 수신기용 HttpClient를 만든다(전체 Timeout은 무한, 요청마다 RequestTimeout).
    // 입력: port - 수신기 포트, handler - 테스트용 가짜 endpoint(null이면 실제 HTTP).
    // 출력: http://127.0.0.1:port/를 기본 주소로 하는 UnityQaClient.
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

    // 기능: GET /qa/status로 플레이어 상태를 읽는다.
    // 입력: token - 취소 토큰.
    // 출력: 플레이어의 답(상태 JSON). 전송 실패·시간 초과면 QaApiException.
    public Task<UnityAnswer> StatusAsync(CancellationToken token) => SendAsync(HttpMethod.Get, "qa/status", null, token);

    // 기능: POST /qa/screenshot으로 스크린샷을 찍게 한다.
    // 입력: name - 파일 이름, token - 취소 토큰.
    // 출력: 플레이어의 답(PNG가 디스크에 쓰인 뒤). 전송 실패·시간 초과면 QaApiException.
    public Task<UnityAnswer> ScreenshotAsync(string name, CancellationToken token) => SendAsync(HttpMethod.Post, "qa/screenshot", new { name }, token);

    // 기능: POST /qa/ui로 UI 명령을 보낸다.
    // 입력: command - UI 명령, token - 취소 토큰.
    // 출력: 플레이어의 답(적용할 수 없는 명령은 409 상태로 온다). 전송 실패·시간 초과면 QaApiException.
    public Task<UnityAnswer> UiAsync(string command, CancellationToken token) => SendAsync(HttpMethod.Post, "qa/ui", new { command }, token);

    // 기능: Player에 Gameplay 입력 하나(키, 마우스 버튼, 시점 이동)를 보낸다. Player가 Input System의 가상 장치로 넣는다.
    // 입력: body - POST /qa/input의 평평한 JSON 본문(key|button|lookX·lookY와 holdMs·action·ms), token - 실행 취소.
    // 출력: Player의 답. 200이면 입력이 Input System에 들어갔고(hold는 그 뒤에도 이어진다), 400·409·503은 상태 코드로 온다.
    public Task<UnityAnswer> InputAsync(object body, CancellationToken token) => SendAsync(HttpMethod.Post, "qa/input", body, token);

    // 기능: Player에 눌린 입력 전부를 떼라고 보낸다(POST /qa/input {"releaseAll":true}, join 전에도 받는다).
    // 입력: token - 실행 취소(호출자가 짧은 시간 제한을 건다).
    // 출력: Player의 답. 200이면 hold·look이 모두 끝났다.
    public Task<UnityAnswer> ReleaseAllAsync(CancellationToken token) => SendAsync(HttpMethod.Post, "qa/input", new { releaseAll = true }, token);

    // 기능: HttpClient(와 테스트 handler)를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음.
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
