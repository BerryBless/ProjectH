// QA-4 D26-D28: a localhost HTTP endpoint the QA tool uses to read the client's state, take screenshots and open or
// close UI. The whole file exists only in the Editor and Development Builds; Release builds contain none of it.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectH.Client.Game;
using ProjectH.Client.Net;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ProjectH.Client.Qa
{
    // Thread model (D27):
    //   accept thread  - one background thread blocked in HttpListener.GetContext. It hands every connection to
    //                    HandleAsync and goes back to accepting. It never touches a Unity API.
    //   HandleAsync    - runs on thread-pool threads (no SynchronizationContext there). Checks the route and the
    //                    Content-Type, reads the body (at most 16 KB), queues a Request and awaits its answer, then
    //                    writes the response and closes it. One 5 s deadline per request, started before the body is
    //                    read, covers the read, the queue and the answer. Never touches a Unity API either.
    //   main thread    - Update takes the queued requests and answers them (status, UI) or starts a screenshot
    //                    coroutine that answers after the PNG is on disk. The answer is a TaskCompletionSource created
    //                    with RunContinuationsAsynchronously, so completing it never runs the network write on the main
    //                    thread.
    // Bounds: at most MaxInFlight requests are being handled at once (more get 503 at once); the queue holds at most
    // MaxQueued requests (more get 503); a request without an answer after TimeoutMs gets 504, one whose body has not
    // arrived by then gets 408 and its connection is closed; pending screenshots are at most MaxQueued (more get 503).
    // Status codes: 200 ok, 400 bad JSON / bad name / unknown command / bad input body, 403 not from loopback,
    // 404 unknown path, 405 wrong method (OPTIONS included), 408 body not received within 5 s, 409 UI command does not
    // apply to the current screen or gameplay input while not joined, 413 body over 16 KB, 415 POST without
    // Content-Type application/json, 500 capture or write failed, 503 full, shutting down, all MaxHolds input slots in
    // use or no QA input devices, 504 not answered within 5 s.
    // Gameplay input (POST /qa/input): two virtual Input System devices (QaKeyboard, QaMouse) added in Awake and removed
    // in Shutdown. InputReader's bindings (<Keyboard>/q, <Mouse>/delta, ...) match any keyboard and mouse, so the game
    // reads them through its normal actions. Main thread only: requests change one kept KeyboardState/MouseState and
    // queue it (InputSystem.QueueStateEvent); the Input System applies it at its next update (the next frame). Held keys,
    // buttons and spread mouse looks take one of MaxHolds fixed slots (one per key or button, one per look) and are
    // released by Update when due; Shutdown removes the devices, which releases everything. While the receiver runs,
    // InputSystem.settings.backgroundBehavior is IgnoreFocus (a QA player is often not focused; in the Editor also
    // editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView) and
    // GameClient.QaAssumeCursorLocked is set; Shutdown restores both.
    // Connections stay open after a response (HTTP keep-alive) except after 408 and 413, whose body may be partly
    // unread. Closing from this side reset the connection on Windows now and then (Mono's HttpListener: about 1 in 12
    // requests failed with "forcibly closed by the remote host" before the caller read the answer). Mono closes an
    // idle connection after 90 s itself, and the QA tool closes its own when the actor ends.
    // Lock: _queueLock is the only lock. It guards _queue alone, is held only for one Enqueue/Dequeue/Count, and no
    // other lock is taken inside it, so it cannot deadlock.
    // Lifetime: added by GameBootstrap next to GameClient and UiRoot (only with -qaPort); Shutdown runs from
    // OnApplicationQuit and OnDestroy (whichever comes first): it stops accepting, answers every queued and pending
    // request with 503, waits up to 100 ms for those responses to be written, closes the listener and joins the accept
    // thread.
    public sealed class QaCommandReceiver : MonoBehaviour
    {
        private const int MaxInFlight = 32;
        private const int MaxQueued = 32;
        private const int MaxBodyBytes = 16 * 1024;
        private const int MaxDrainBytes = 1024 * 1024;
        private const int TimeoutMs = 5000;
        private const int JoinTimeoutMs = 1000;
        private const int CloseGraceMs = 100;
        private const int ReadTimedOut = -1;

        private readonly struct Reply
        {
            public Reply(int status, string json)
            {
                Status = status;
                Json = json;
            }

            public int Status { get; }
            public string Json { get; }
        }

        private sealed class Request
        {
            public Request(QaRoute route, string body)
            {
                Route = route;
                Body = body;
            }

            public QaRoute Route { get; }
            public string Body { get; }
            public readonly TaskCompletionSource<Reply> Done =
                new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // _queueLock guards _queue only (see the class comment).
        private readonly object _queueLock = new object();
        private readonly Queue<Request> _queue = new Queue<Request>(MaxQueued);
        // Main thread only.
        private readonly List<Request> _pendingShots = new List<Request>(MaxQueued);
        private readonly StringBuilder _json = new StringBuilder(256);
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();

        private HttpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stopping;
        private int _inFlight;   // Interlocked: requests between accept and response close
        private GameClient _client;
        private UiRoot _ui;
        private string _shotDir;
        private float _smoothedDelta = 1f / 60f;

        // ---- gameplay input (main thread only) ----
        private const int MaxHolds = 16;

        // QaInput.KeyNames in the same order ("1".."5" are the top-row digits, not the numpad).
        private static readonly Key[] InputKeys =
        {
            Key.W, Key.A, Key.S, Key.D, Key.Space, Key.LeftShift, Key.LeftCtrl, Key.C, Key.Q, Key.F, Key.Z, Key.X, Key.V,
            Key.B, Key.T, Key.R, Key.E, Key.G, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Escape, Key.F1, Key.H,
            Key.M,   // Phase 15: the full map
        };

        private enum HoldKind : byte
        {
            Free,
            Key,
            Button,
            Look,
        }

        // One active held key/button (released at ReleaseAt, but never before MinFrame so the down is seen for at least
        // one frame) or one look being spread from Start to End (unscaled seconds).
        private struct Hold
        {
            public HoldKind Kind;
            public int Code;            // key index (QaInput.KeyNames) or 0 = left, 1 = right, 2 = middle
            public double ReleaseAt;
            public int MinFrame;
            public double Start;
            public double End;
            public double TotalX;
            public double TotalY;
            public double SentX;
            public double SentY;
        }

        private readonly Hold[] _holds = new Hold[MaxHolds];
        private int _holdCount;
        private Keyboard _qaKeyboard;
        private Mouse _qaMouse;
        private KeyboardState _keyState;
        private MouseState _mouseState;   // delta is always zero here; a look's delta goes only into the queued copy
        private bool _keysDirty;
        private bool _mouseDirty;
        private bool _inputReady;
        private bool _backgroundSet;
        private InputSettings.BackgroundBehavior _previousBackground;
#if UNITY_EDITOR
        private bool _editorBehaviorSet;
        private InputSettings.EditorInputBehaviorInPlayMode _previousEditorBehavior;
#endif

        // 기능: 수신기를 준비한다. -qaPort가 있으면 Listener와 Accept 스레드를 시작하고, 그 다음 QA 입력 장치를 붙인다.
        // 입력: 없음(실행 인자·환경 변수를 QaLaunchOptions로 읽는다).
        // 출력: 반환값 없음. 시작하지 못하면 컴포넌트가 꺼진다(enabled = false). 입력 장치를 붙이지 못하면 /qa/input만 503이다.
        private void Awake()
        {
            _client = GetComponent<GameClient>();
            _ui = GetComponent<UiRoot>();
            QaLaunchOptions options = QaLaunchOptions.FromEnvironment();
            // Resolved here: persistentDataPath may only be read on the main thread.
            try
            {
                _shotDir = Path.GetFullPath(options.ShotDir ?? Path.Combine(Application.persistentDataPath, "qa-shots"));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QA] Invalid -qaShotDir '{options.ShotDir}': {e.Message}; using persistentDataPath/qa-shots");
                _shotDir = Path.Combine(Application.persistentDataPath, "qa-shots");
            }
            if (options.Port <= 0 || _client == null || _ui == null)
            {
                enabled = false;
                return;
            }
            _listener = StartListener(options.Port);
            if (_listener == null)
            {
                enabled = false;
                return;
            }
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "QaCommandReceiver" };
            _acceptThread.Start();
            // After the listener started: Shutdown (guarded by _listener) is then sure to undo it.
            StartInput();
            Debug.Log($"[QA] Command receiver listening on port {options.Port}; screenshots to {_shotDir}");
        }

        // 기능: QA 입력 장치(QaKeyboard, QaMouse)를 붙이고, 포커스 없이도 입력이 가게 하고(backgroundBehavior, Editor에서는
        //       editorInputBehaviorInPlayMode도), GameClient에 커서 잠금 가정을 켠다.
        // 입력: 없음.
        // 출력: 반환값 없음. 성공하면 _inputReady가 true. 실패하면 경고 한 줄을 남기고 붙인 장치를 떼며 /qa/input은 503으로 답한다.
        private void StartInput()
        {
            if (InputKeys.Length != QaInput.KeyNames.Length)
            {
                Debug.LogError("[QA] Input key table does not match QaInput.KeyNames; /qa/input is off");
                return;
            }
            try
            {
                _qaKeyboard = InputSystem.AddDevice<Keyboard>("QaKeyboard");
                _qaMouse = InputSystem.AddDevice<Mouse>("QaMouse");
                _keyState = default;
                _mouseState = default;
                _previousBackground = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                _backgroundSet = true;
#if UNITY_EDITOR
                // In Play Mode the Editor routes keyboard and mouse input away from the game while the Game View is not
                // focused (the default); a QA run usually has another window in front.
                _previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                _editorBehaviorSet = true;
#endif
                _client.QaAssumeCursorLocked = true;
                _inputReady = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QA] Gameplay input disabled: cannot add the QA input devices: {e.Message}");
                StopInput();
            }
        }

        // 기능: QA 입력을 모두 끝낸다. 칸을 비우고, 장치를 떼고(눌린 키·버튼이 함께 풀린다), 포커스 설정(Editor 설정 포함)과
        //       커서 가정을 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. _inputReady가 false가 되고 InputSystem 설정이 수신기 시작 전 값으로 돌아간다. 여러 번 불러도 된다.
        private void StopInput()
        {
            _inputReady = false;
            Array.Clear(_holds, 0, _holds.Length);
            _holdCount = 0;
            _keysDirty = false;
            _mouseDirty = false;
            // Each step on its own: at quit the Input System may already be partly torn down, and one failure must not
            // leave the others undone.
            if (_qaKeyboard != null)
            {
                try { InputSystem.RemoveDevice(_qaKeyboard); }
                catch (Exception e) { Debug.LogWarning($"[QA] Removing QaKeyboard failed: {e.Message}"); }
                _qaKeyboard = null;
            }
            if (_qaMouse != null)
            {
                try { InputSystem.RemoveDevice(_qaMouse); }
                catch (Exception e) { Debug.LogWarning($"[QA] Removing QaMouse failed: {e.Message}"); }
                _qaMouse = null;
            }
            if (_backgroundSet)
            {
                try { InputSystem.settings.backgroundBehavior = _previousBackground; }
                catch (Exception e) { Debug.LogWarning($"[QA] Restoring backgroundBehavior failed: {e.Message}"); }
                _backgroundSet = false;
            }
#if UNITY_EDITOR
            if (_editorBehaviorSet)
            {
                try { InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorBehavior; }
                catch (Exception e) { Debug.LogWarning($"[QA] Restoring editorInputBehaviorInPlayMode failed: {e.Message}"); }
                _editorBehaviorSet = false;
            }
#endif
            // GameClient may already be destroyed at quit (Unity's == null covers that).
            if (_client != null) _client.QaAssumeCursorLocked = false;
        }

        // D26: 127.0.0.1 first; "localhost" only when that cannot start. A port in use (another Editor clone, another
        // client) logs one warning and leaves the receiver off.
        private static HttpListener StartListener(int port)
        {
            string[] prefixes = { $"http://127.0.0.1:{port}/", $"http://localhost:{port}/" };
            Exception last = null;
            for (int i = 0; i < prefixes.Length; i++)
            {
                var listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add(prefixes[i]);
                    listener.Start();
                    return listener;
                }
                catch (Exception e)
                {
                    last = e;
                    try { listener.Close(); } catch (Exception) { /* already failed; nothing to release */ }
                }
            }
            Debug.LogWarning($"[QA] Command receiver disabled: cannot listen on port {port}: {last?.Message}");
            return null;
        }

        private void AcceptLoop()
        {
            HttpListener listener = _listener;
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception e)
                {
                    // Close() during shutdown ends GetContext with an exception: the normal way out. Anything else ends
                    // the loop too (logged once) instead of spinning on a broken listener.
                    if (!_stopping) Debug.LogWarning($"[QA] Command receiver stopped accepting: {e.Message}");
                    return;
                }
                if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
                {
                    Interlocked.Decrement(ref _inFlight);
                    _ = RespondAndCloseAsync(context, 503, ErrorJson("too many requests"));
                    continue;
                }
                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            int status = 500;
            string json = null;
            bool bodyTimedOut = false;
            // One deadline per request, started before the body is read: reading the body, waiting in the queue and the
            // main thread's answer together get TimeoutMs. A sender that stalls mid-body therefore cannot hold an
            // in-flight slot longer than that.
            using (var deadlineSource = new CancellationTokenSource())
            {
                Task deadline = Task.Delay(TimeoutMs, deadlineSource.Token);
                try
                {
                    HttpListenerRequest http = context.Request;
                    QaRoute route = QaHttp.Resolve(http.HttpMethod, http.Url.AbsolutePath);
                    if (http.RemoteEndPoint == null || !IPAddress.IsLoopback(http.RemoteEndPoint.Address))
                    {
                        status = 403;
                        json = ErrorJson("loopback only");
                    }
                    else if (route == QaRoute.NotFound)
                    {
                        status = 404;
                        json = ErrorJson("not found");
                    }
                    else if (route == QaRoute.MethodNotAllowed)
                    {
                        status = 405;
                        json = ErrorJson("method not allowed");
                    }
                    else if (route != QaRoute.Status && !QaHttp.IsJsonContentType(http.ContentType))
                    {
                        // CSRF guard (see IsJsonContentType). The body is drained so the caller gets the 415.
                        status = 415;
                        json = ErrorJson("Content-Type must be application/json");
                        if (http.HasEntityBody)
                            bodyTimedOut = !await DrainAsync(http.InputStream, new byte[4096], deadline).ConfigureAwait(false);
                    }
                    else
                    {
                        string body = null;
                        if (route != QaRoute.Status)
                        {
                            BodyResult result = await ReadBodyAsync(http, deadline).ConfigureAwait(false);
                            if (result.TimedOut)
                            {
                                bodyTimedOut = true;
                            }
                            else if (result.Body == null)
                            {
                                status = 413;
                                json = ErrorJson("body over 16 KB");
                            }
                            body = result.Body;
                        }
                        if (json == null && !bodyTimedOut)
                        {
                            Reply reply = await SubmitAsync(new Request(route, body), deadline).ConfigureAwait(false);
                            status = reply.Status;
                            json = reply.Json;
                        }
                    }
                }
                catch (Exception e)
                {
                    status = 500;
                    json = ErrorJson(e.Message);
                }
                finally
                {
                    deadlineSource.Cancel();   // releases the timer at once
                    if (bodyTimedOut)
                    {
                        // The body never arrived: answer 408 and close the connection (Connection: close). Mono's
                        // Response.Abort() is not used because it writes a "200 OK" header while closing.
                        status = 408;
                        json = ErrorJson("request body not received within 5 s");
                    }
                    await RespondAndCloseAsync(context, status, json ?? ErrorJson("no answer")).ConfigureAwait(false);
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        private readonly struct BodyResult
        {
            public BodyResult(string body, bool timedOut)
            {
                Body = body;
                TimedOut = timedOut;
            }

            public string Body { get; }       // null = over MaxBodyBytes (or timed out)
            public bool TimedOut { get; }
        }

        // The body as text, null when it is larger than MaxBodyBytes (by Content-Length or by what actually arrives),
        // or TimedOut when the deadline passed while reading.
        private static async Task<BodyResult> ReadBodyAsync(HttpListenerRequest http, Task deadline)
        {
            if (!http.HasEntityBody) return new BodyResult(string.Empty, false);
            Stream input = http.InputStream;
            var buffer = new byte[MaxBodyBytes + 1];
            if (http.ContentLength64 > MaxBodyBytes)
                return new BodyResult(null, !await DrainAsync(input, buffer, deadline).ConfigureAwait(false));
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await ReadAsync(input, buffer, total, buffer.Length - total, deadline).ConfigureAwait(false);
                if (read == ReadTimedOut) return new BodyResult(null, true);
                if (read <= 0) break;
                total += read;
            }
            if (total > MaxBodyBytes)
                return new BodyResult(null, !await DrainAsync(input, buffer, deadline).ConfigureAwait(false));
            return new BodyResult(Encoding.UTF8.GetString(buffer, 0, total), false);
        }

        // Closing a socket with unread request bytes resets the connection on Windows and the caller never sees the
        // 413/415. Reads and discards at most MaxDrainBytes; a larger body is left to the reset. False = the deadline
        // passed first.
        private static async Task<bool> DrainAsync(Stream input, byte[] buffer, Task deadline)
        {
            long drained = 0;
            while (drained < MaxDrainBytes)
            {
                int read = await ReadAsync(input, buffer, 0, buffer.Length, deadline).ConfigureAwait(false);
                if (read == ReadTimedOut) return false;
                if (read <= 0) return true;
                drained += read;
            }
            return true;
        }

        // The listener's stream does not honour cancellation tokens, so the read races the deadline instead. A read
        // that loses keeps running until the response closes the connection; its fault is observed so it is not
        // reported as an unobserved task exception. The buffer belongs to that request only, so a late write is harmless.
        private static async Task<int> ReadAsync(Stream input, byte[] buffer, int offset, int count, Task deadline)
        {
            Task<int> read = input.ReadAsync(buffer, offset, count);
            if (await Task.WhenAny(read, deadline).ConfigureAwait(false) == read) return await read.ConfigureAwait(false);
            _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return ReadTimedOut;
        }

        private async Task<Reply> SubmitAsync(Request request, Task deadline)
        {
            if (_stopping) return new Reply(503, ErrorJson("shutting down"));
            bool queued;
            lock (_queueLock)
            {
                queued = _queue.Count < MaxQueued;
                if (queued) _queue.Enqueue(request);
            }
            if (!queued) return new Reply(503, ErrorJson("queue full"));

            Task first = await Task.WhenAny(request.Done.Task, deadline).ConfigureAwait(false);
            if (first == request.Done.Task) return request.Done.Task.Result;
            // Completing it tells the main thread to skip it; if the main thread answered in the meantime, the
            // TrySetResult fails and its answer is used.
            request.Done.TrySetResult(new Reply(504, ErrorJson("not answered within 5 s")));
            return request.Done.Task.Result;
        }

        // 기능: 요청 하나에 JSON 응답을 쓰고 응답을 닫는다.
        // 입력: context - 응답할 요청, status - HTTP 상태 코드, json - 응답 본문.
        // 출력: 반환값 없음. 응답이 전송되고, 408·413이면 연결도 닫힌다(그 밖에는 다음 요청을 위해 유지된다).
        private static async Task RespondAndCloseAsync(HttpListenerContext context, int status, string json)
        {
            HttpListenerResponse response = context.Response;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                response.StatusCode = status;
                response.ContentType = "application/json; charset=utf-8";
                // Keep-alive (see the class comment): closing from this side made Windows reset the connection at random.
                // 408 and 413 still close: the rest of their body may not have been read.
                response.KeepAlive = status != 408 && status != 413;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The caller went away or the listener is closing: nothing more to tell it.
            }
            finally
            {
                try { response.Close(); } catch (Exception) { /* already closed by the listener */ }
            }
        }

        private static string ErrorJson(string error)
        {
            var sb = new StringBuilder(64);
            QaResponses.AppendError(sb, error);
            return sb.ToString();
        }

        // ---- main thread ----

        // 기능: 메인 스레드에서 쌓인 요청에 답하고, QA 입력의 누름 해제와 시점 이동 나누기를 이번 프레임만큼 진행한다.
        // 입력: 없음(Unity가 매 프레임 부른다).
        // 출력: 반환값 없음. 큐의 요청이 답해지고, 바뀐 QA 키보드·마우스 상태가 Input System에 들어간다.
        // Steady state (no request, no hold) allocates nothing: the queue check and TickInput's early returns only.
        private void Update()
        {
            _smoothedDelta = Mathf.Lerp(_smoothedDelta, Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);
            // At most MaxQueued per frame: the queue never holds more, so this drains what was there at frame start.
            for (int n = 0; n < MaxQueued; n++)
            {
                Request request;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) break;
                    request = _queue.Dequeue();
                }
                if (request.Done.Task.IsCompleted) continue;   // timed out while queued
                Process(request);
            }
            if (!_inputReady) return;
            try
            {
                TickInput();
            }
            catch (Exception e)
            {
                // Outside Process's try/catch: a failing QueueStateEvent (a device removed by someone else) would throw
                // again every frame. Turn QA input off once instead (/qa/input then answers 503).
                Debug.LogWarning($"[QA] Gameplay input stopped: {e.Message}");
                StopInput();
            }
        }

        // 기능: 요청 하나를 경로에 맞게 처리하고 답한다(스크린샷은 Coroutine이 나중에 답한다).
        // 입력: request - 큐에서 꺼낸 요청.
        // 출력: 반환값 없음. 요청의 답이 정해지고, 예외가 나면 500으로 답한다.
        private void Process(Request request)
        {
            try
            {
                switch (request.Route)
                {
                    case QaRoute.Status:
                        Answer(request, 200, StatusJson());
                        break;
                    case QaRoute.Ui:
                        ProcessUi(request);
                        break;
                    case QaRoute.Screenshot:
                        ProcessScreenshot(request);
                        break;
                    case QaRoute.Input:
                        ProcessInput(request);
                        break;
                    default:
                        Answer(request, 404, ErrorJson("not found"));
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QA] Request failed: {e.Message}");
                Answer(request, 500, ErrorJson(e.Message));
            }
        }

        // 기능: GET /qa/status 응답을 만든다.
        // 입력: 없음(GameClient·UiRoot의 QA 읽기 전용 값을 읽는다).
        // 출력: 상태 JSON 문자열(접속·화면·생존·체력·fps·frame·도구·건설 미리보기·커서 잠금, Phase 15 지도 필드).
        private string StatusJson()
        {
            ClientState state = _client.State;
            bool joined = state == ClientState.Joined;
            _json.Clear();
            QaResponses.AppendStatus(_json, _client.QaDevPlayerId, state == ClientState.Connected || joined, joined,
                ScreenName(_ui.QaScreen), _ui.QaStatsOpen, _ui.QaDebugVisible, joined && _client.QaAlive,
                joined ? _client.QaHealth : 0, 1.0 / _smoothedDelta, Time.frameCount, ToolName(_client.QaTool),
                PreviewName(_client.QaPreview), _client.QaCursorLocked, _client.QaMap);
            return _json.ToString();
        }

        // 기능: 도구를 상태 JSON의 이름으로 바꾼다.
        // 입력: tool - GameClient.QaTool(null = Join 전).
        // 출력: "Weapon", "Harvest", "Build" 또는 "none".
        private static string ToolName(ToolKind? tool)
        {
            if (!tool.HasValue) return "none";
            switch (tool.Value)
            {
                case ToolKind.Weapon: return "Weapon";
                case ToolKind.Harvest: return "Harvest";
                case ToolKind.Build: return "Build";
                default: return "none";
            }
        }

        // 기능: 건설 미리보기 판정을 상태 JSON의 이름으로 바꾼다.
        // 입력: preview - GameClient.QaPreview(null = 건설 모드가 아니거나 후보 없음).
        // 출력: "Valid", "Invalid", "NoResource" 또는 "none".
        private static string PreviewName(BuildPreviewState? preview)
        {
            if (!preview.HasValue) return "none";
            switch (preview.Value)
            {
                case BuildPreviewState.Valid: return "Valid";
                case BuildPreviewState.Invalid: return "Invalid";
                case BuildPreviewState.NoResource: return "NoResource";
                default: return "none";
            }
        }

        // 기능: POST /qa/input 하나를 검사하고 QA 키보드·마우스에 적용한다.
        // 입력: request - Body가 {key|button|lookX,lookY, holdMs?, action?, ms?}인 요청.
        // 출력: 반환값 없음. 200(적용, 키·버튼 상태는 이미 Input System 큐에 들어감), 400(잘못된 Body), 409(Join 전),
        //       503(입력 장치 없음 또는 칸 16개가 모두 사용 중)으로 답한다.
        private void ProcessInput(Request request)
        {
            if (!QaInput.TryParse(request.Body, out QaInputRequest input, out string error))
            {
                Answer(request, 400, ErrorJson(error));
                return;
            }
            if (input.Kind == QaInputKind.ReleaseAll)
            {
                // Allowed in any state (also before a join): nothing held is the goal, so it always succeeds.
                if (_inputReady) ReleaseAll();
                _json.Clear();
                QaResponses.AppendInput(_json, input);
                Answer(request, 200, _json.ToString());
                return;
            }
            if (!_inputReady)
            {
                Answer(request, 503, ErrorJson("QA input devices are not available"));
                return;
            }
            if (_client.State != ClientState.Joined)
            {
                Answer(request, 409, ErrorJson("not joined"));
                return;
            }
            if (!ApplyInput(input))
            {
                Answer(request, 503, ErrorJson("too many active holds (16)"));
                return;
            }
            FlushInputState(Vector2.zero, false);
            _json.Clear();
            QaResponses.AppendInput(_json, input);
            Answer(request, 200, _json.ToString());
        }

        // 기능: 파싱한 입력 하나를 칸과 장치 상태에 반영한다. 같은 키·버튼은 한 칸을 같이 쓰고, 새 누름이 해제 시각을 바꾼다.
        // 입력: input - QaInput.TryParse가 만든 입력.
        // 출력: 반영했으면 true, 새 칸이 필요한데 MaxHolds칸이 모두 차 있으면 false(아무것도 바꾸지 않는다).
        private bool ApplyInput(in QaInputRequest input)
        {
            double now = Time.unscaledTimeAsDouble;
            int frame = Time.frameCount;
            if (input.Kind == QaInputKind.Look)
            {
                int free = FindSlot(HoldKind.Free, 0);
                if (free < 0) return false;
                double seconds = input.Ms / 1000.0;
                _holds[free] = new Hold
                {
                    Kind = HoldKind.Look,
                    Start = now,
                    End = now + seconds,
                    TotalX = input.LookX,
                    TotalY = input.LookY,
                };
                _holdCount++;
                return true;
            }

            HoldKind kind = input.Kind == QaInputKind.Key ? HoldKind.Key : HoldKind.Button;
            int slot = FindSlot(kind, input.Code);
            if (input.Action == QaInputAction.Up)
            {
                // Idempotent: an up for something not held still writes "up" and answers 200.
                if (slot >= 0) FreeSlot(slot);
                SetDown(kind, input.Code, false);
                return true;
            }
            if (slot < 0)
            {
                slot = FindSlot(HoldKind.Free, 0);
                if (slot < 0) return false;
                _holdCount++;
            }
            double holdSeconds = input.Action == QaInputAction.Hold ? input.HoldMs / 1000.0
                : input.Action == QaInputAction.Down ? QaInput.MaxHoldMs / 1000.0
                : 0.0;
            // A second press or hold of a key already held replaces its release time, so the earlier one cannot let go
            // of a key the later one still expects down.
            _holds[slot] = new Hold { Kind = kind, Code = input.Code, ReleaseAt = now + holdSeconds, MinFrame = frame + 1 };
            SetDown(kind, input.Code, true);
            return true;
        }

        // 기능: 눌린 QA 키·버튼을 모두 떼고 진행 중인 시점 이동을 멈춘다.
        // 입력: 없음.
        // 출력: 반환값 없음. 칸이 모두 비고, 모든 키·버튼이 떼어진 상태가 바로 Input System 큐에 들어간다.
        private void ReleaseAll()
        {
            Array.Clear(_holds, 0, _holds.Length);
            _holdCount = 0;
            // "up" can leave a key down only through a slot, but clearing the whole state also covers anything else.
            _keyState = default;
            _mouseState = default;
            _keysDirty = true;
            _mouseDirty = true;
            FlushInputState(Vector2.zero, false);
        }

        // 기능: 종류와 코드가 같은 칸을 찾는다(Free면 빈 칸).
        // 입력: kind - 칸 종류, code - 키 번호나 버튼 번호(Free·Look이면 무시).
        // 출력: 칸 번호, 없으면 -1.
        private int FindSlot(HoldKind kind, int code)
        {
            for (int i = 0; i < MaxHolds; i++)
            {
                if (_holds[i].Kind != kind) continue;
                if (kind == HoldKind.Free || kind == HoldKind.Look || _holds[i].Code == code) return i;
            }
            return -1;
        }

        // 기능: 칸 하나를 비운다.
        // 입력: slot - 비울 칸 번호.
        // 출력: 반환값 없음. 칸이 Free가 되고 사용 중 칸 수가 줄어든다.
        private void FreeSlot(int slot)
        {
            _holds[slot] = default;
            _holdCount--;
        }

        // 기능: 보관 중인 QA 키보드·마우스 상태에서 키나 버튼 하나를 누르거나 뗀다(Input System에는 FlushInputState가 넣는다).
        // 입력: kind - Key 또는 Button, code - 키 번호(InputKeys)나 버튼 번호(0 왼쪽, 1 오른쪽, 2 가운데: Phase 15 Ping), down - 누름 여부.
        // 출력: 반환값 없음. _keyState나 _mouseState가 바뀌고 dirty 표시가 켜진다.
        private void SetDown(HoldKind kind, int code, bool down)
        {
            if (kind == HoldKind.Key)
            {
                _keyState.Set(InputKeys[code], down);
                _keysDirty = true;
                return;
            }
            _mouseState = _mouseState.WithButton(code == 0 ? MouseButton.Left : code == 1 ? MouseButton.Right : MouseButton.Middle, down);
            _mouseDirty = true;
        }

        // 기능: 이번 프레임의 QA 입력을 진행한다. Joined가 아니면 모두 떼고(ReleaseAll), 때가 된 누름을 떼고, 시점 이동을 시간에 맞게 나눠 보낸다.
        // 입력: 없음(Time.unscaledTimeAsDouble, Time.frameCount를 읽는다).
        // 출력: 반환값 없음. 바뀐 상태와 이번 프레임의 마우스 delta가 Input System 큐에 들어간다. 칸이 없으면 바로 끝난다.
        private void TickInput()
        {
            if (_holdCount == 0)
            {
                if (_keysDirty || _mouseDirty) FlushInputState(Vector2.zero, false);
                return;
            }
            // Leaving the Joined state (disconnect, back to the title) lets go of everything at once, so no hold survives
            // into the next session. Every down key or button has a slot, so checking only while slots are used suffices.
            if (_client.State != ClientState.Joined)
            {
                ReleaseAll();
                return;
            }
            double now = Time.unscaledTimeAsDouble;
            int frame = Time.frameCount;
            double dx = 0;
            double dy = 0;
            bool look = false;
            for (int i = 0; i < MaxHolds; i++)
            {
                ref Hold hold = ref _holds[i];
                switch (hold.Kind)
                {
                    case HoldKind.Key:
                    case HoldKind.Button:
                        if (frame >= hold.MinFrame && now >= hold.ReleaseAt)
                        {
                            SetDown(hold.Kind, hold.Code, false);
                            FreeSlot(i);
                        }
                        break;
                    case HoldKind.Look:
                        // Spread by time: the part due by now minus what was sent, so the total is exact at any frame
                        // rate (the last frame carries the remainder). ms 0 sends everything in this frame.
                        double fraction = hold.End <= hold.Start ? 1.0 : Math.Min(1.0, (now - hold.Start) / (hold.End - hold.Start));
                        double targetX = hold.TotalX * fraction;
                        double targetY = hold.TotalY * fraction;
                        dx += targetX - hold.SentX;
                        dy += targetY - hold.SentY;
                        hold.SentX = targetX;
                        hold.SentY = targetY;
                        look = true;
                        if (fraction >= 1.0) FreeSlot(i);
                        break;
                }
            }
            if (_keysDirty || _mouseDirty || look) FlushInputState(new Vector2((float)dx, (float)dy), look);
        }

        // 기능: 바뀐 QA 키보드·마우스 상태를 Input System 큐에 넣는다. Input System은 다음 입력 Update(다음 프레임)에 반영한다.
        // 입력: delta - 이번 프레임에 더할 마우스 이동(픽셀), look - delta를 보내야 하는지.
        // 출력: 반환값 없음. dirty 표시가 꺼진다. 마우스 delta는 Input System이 Update마다 0으로 되돌리므로 따로 0을 보내지 않는다.
        private void FlushInputState(Vector2 delta, bool look)
        {
            if (_keysDirty)
            {
                InputSystem.QueueStateEvent(_qaKeyboard, _keyState);
                _keysDirty = false;
            }
            if (_mouseDirty || look)
            {
                MouseState state = _mouseState;
                state.delta = delta;
                InputSystem.QueueStateEvent(_qaMouse, state);
                _mouseDirty = false;
            }
        }

        // 기능: POST /qa/ui 하나를 처리한다(Phase 15: openMap·closeMap 포함).
        // 입력: request - Body가 {"command": ...}인 요청.
        // 출력: 반환값 없음. 200(적용), 409(지금 화면에 맞지 않음), 400(모르는 명령)으로 답한다.
        private void ProcessUi(Request request)
        {
            QaJsonResult result = QaJsonReader.TryGetString(request.Body, "command", out string text);
            QaUiCommand command = result == QaJsonResult.Ok ? QaHttp.ParseUiCommand(text) : QaUiCommand.None;
            if (command == QaUiCommand.None)
            {
                Answer(request, 400, ErrorJson("expected {\"command\": openMenu|closeMenu|openStats|closeStats|toggleDebug|openMap|closeMap}"));
                return;
            }
            bool applied = _ui.QaApply(command);
            _json.Clear();
            QaResponses.AppendUi(_json, applied, command, ScreenName(_ui.QaScreen), _ui.QaStatsOpen, _ui.QaDebugVisible);
            Answer(request, applied ? 200 : 409, _json.ToString());
        }

        private void ProcessScreenshot(Request request)
        {
            QaJsonResult result = QaJsonReader.TryGetString(request.Body, "name", out string name);
            if (result != QaJsonResult.Ok || !QaHttp.IsValidShotName(name))
            {
                Answer(request, 400, ErrorJson("expected {\"name\": [A-Za-z0-9_-]{1,64}, not a Windows device name}"));
                return;
            }
            if (_pendingShots.Count >= MaxQueued)
            {
                Answer(request, 503, ErrorJson("too many pending screenshots"));
                return;
            }
            _pendingShots.Add(request);
            StartCoroutine(Capture(request, Path.Combine(_shotDir, name + ".png")));
        }

        // Captures after this frame finished rendering (UI included) and answers only after the PNG is written.
        // A window that never renders (minimized without a swap chain, -nographics) leaves the request to the 5 s
        // timeout; the coroutine then finds it completed and skips the capture.
        private IEnumerator Capture(Request request, string path)
        {
            yield return _endOfFrame;
            _pendingShots.Remove(request);
            if (request.Done.Task.IsCompleted) yield break;
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                byte[] png = texture.EncodeToPNG();
                Directory.CreateDirectory(_shotDir);
                File.WriteAllBytes(path, png);
                _json.Clear();
                QaResponses.AppendShot(_json, path);
                Answer(request, 200, _json.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QA] Screenshot failed: {e.Message}");
                Answer(request, 500, ErrorJson("screenshot failed: " + e.Message));
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }

        private static void Answer(Request request, int status, string json)
        {
            request.Done.TrySetResult(new Reply(status, json));
        }

        private static string ScreenName(UiScreen screen)
        {
            switch (screen)
            {
                case UiScreen.Title: return "Title";
                case UiScreen.Connecting: return "Connecting";
                case UiScreen.InGame: return "InGame";
                case UiScreen.Menu: return "Menu";
                case UiScreen.Disconnected: return "Disconnected";
                case UiScreen.Result: return "Result";
                default: return "Unknown";
            }
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy() => Shutdown();

        // 기능: 수신기를 한 번만 멈춘다. 남은 요청에 503으로 답하고, Listener를 닫고 Accept 스레드를 Join하고, QA 입력을 끝낸다.
        // 입력: 없음.
        // 출력: 반환값 없음. 스레드·Listener가 정리되고, QA 장치가 제거되며 Input System 설정과 커서 가정이 원래대로 돌아간다.
        //       _listener가 null이면(시작하지 않았거나 이미 멈춤) 아무것도 하지 않는다.
        private void Shutdown()
        {
            if (_listener == null) return;
            _stopping = true;
            // Answer everything first, then give the handlers a moment to write their 503 before the listener closes.
            while (true)
            {
                Request request;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) break;
                    request = _queue.Dequeue();
                }
                Answer(request, 503, ErrorJson("shutting down"));
            }
            for (int i = 0; i < _pendingShots.Count; i++) Answer(_pendingShots[i], 503, ErrorJson("shutting down"));
            _pendingShots.Clear();
            StopAllCoroutines();
            // The handlers write those 503s on the thread pool and Close() would cut them off, so wait until nothing is
            // in flight, at most CloseGraceMs (a short block of the main thread, only at quit). A handler still reading a
            // body is not waited for longer; Close() drops it.
            var grace = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref _inFlight) > 0 && grace.ElapsedMilliseconds < CloseGraceMs) Thread.Sleep(5);
            try
            {
                _listener.Close();   // ends GetContext on the accept thread
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QA] Command receiver close failed: {e.Message}");
            }
            if (_acceptThread != null && !_acceptThread.Join(JoinTimeoutMs))
                Debug.LogWarning("[QA] Command receiver accept thread did not stop within 1 s");
            _acceptThread = null;
            _listener = null;
            StopInput();
        }
    }
}
#endif
