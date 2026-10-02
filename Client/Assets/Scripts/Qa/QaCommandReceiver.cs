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
using UnityEngine;

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
    // Bounds: at most MaxInFlight connections are being handled at once (more get 503 at once); the queue holds at most
    // MaxQueued requests (more get 503); a request without an answer after TimeoutMs gets 504, one whose body has not
    // arrived by then gets 408 and its connection is closed; pending screenshots are at most MaxQueued (more get 503).
    // Status codes: 200 ok, 400 bad JSON / bad name / unknown command, 403 not from loopback, 404 unknown path,
    // 405 wrong method (OPTIONS included), 408 body not received within 5 s, 409 UI command does not apply to the current screen, 413 body over 16 KB,
    // 415 POST without Content-Type application/json, 500 capture or write failed, 503 full or shutting down,
    // 504 not answered within 5 s.
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
        private int _inFlight;   // Interlocked: connections between accept and response close
        private GameClient _client;
        private UiRoot _ui;
        private string _shotDir;
        private float _smoothedDelta = 1f / 60f;

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
            Debug.Log($"[QA] Command receiver listening on port {options.Port}; screenshots to {_shotDir}");
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

        private static async Task RespondAndCloseAsync(HttpListenerContext context, int status, string json)
        {
            HttpListenerResponse response = context.Response;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                response.StatusCode = status;
                response.ContentType = "application/json; charset=utf-8";
                // One request per connection: the socket closes with the response, so no idle connection outlives it.
                response.KeepAlive = false;
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
        }

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

        private string StatusJson()
        {
            ClientState state = _client.State;
            bool joined = state == ClientState.Joined;
            _json.Clear();
            QaResponses.AppendStatus(_json, _client.QaDevPlayerId, state == ClientState.Connected || joined, joined,
                ScreenName(_ui.QaScreen), _ui.QaStatsOpen, _ui.QaDebugVisible, joined && _client.QaAlive,
                joined ? _client.QaHealth : 0, 1.0 / _smoothedDelta, Time.frameCount);
            return _json.ToString();
        }

        private void ProcessUi(Request request)
        {
            QaJsonResult result = QaJsonReader.TryGetString(request.Body, "command", out string text);
            QaUiCommand command = result == QaJsonResult.Ok ? QaHttp.ParseUiCommand(text) : QaUiCommand.None;
            if (command == QaUiCommand.None)
            {
                Answer(request, 400, ErrorJson("expected {\"command\": openMenu|closeMenu|openStats|closeStats|toggleDebug}"));
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
        }
    }
}
#endif
