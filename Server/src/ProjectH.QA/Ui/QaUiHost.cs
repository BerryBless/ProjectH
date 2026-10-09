using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProjectH.QA;

public sealed class UiHostOptions
{
    public const int DefaultPort = 5180;

    public string RepoRoot { get; init; } = Directory.GetCurrentDirectory();
    public int Port { get; init; } = DefaultPort;   // 0 = any free port (tests)
    public string? AttachUrl { get; init; }
    public string? ServerDll { get; init; }
    // QA-4: the default Unity Development player and a fake receiver per port (tests).
    public string? UnityExe { get; init; }
    public Func<int, HttpMessageHandler?>? UnityHandlerFactory { get; init; }
    public string? ReportDir { get; init; }
    public int PollMs { get; init; } = 100;
    public bool WriteReports { get; init; } = true;
    // Where the static files are (default: wwwroot next to the tool's binaries).
    public string? WebRoot { get; init; }
    // Test seams, like QaRunOptions.
    public Func<Uri, IQaServerClient>? ServerClientFactory { get; init; }
    public Func<string, string, IQaActor>? ActorFactory { get; init; }

    public string ScenarioRoot => Path.GetFullPath(Path.Combine(RepoRoot, "QA", "Scenarios"));
    public string ReportRoot => Path.GetFullPath(ReportDir ?? Path.Combine(RepoRoot, "QA", "Reports"));
}

// D19: `ProjectH.QA ui`. Kestrel on 127.0.0.1 only (never ASPNETCORE_URLS: empty builder, the endpoint is set in code),
// static files from wwwroot and a small JSON API. The inner app has a passive lifetime: Ctrl+C belongs to Program, which
// stops the run (cleanup) before stopping this host.
// Security (localhost tool, no login):
//  - every request: the Host header must be 127.0.0.1/localhost/[::1] with our port (DNS rebinding cannot read
//    scenarios or reports through another name);
//  - every POST: Content-Type application/json, and Origin / Sec-Fetch-Site must be same-origin when the browser sends
//    them (another website cannot drive the tool); no CORS headers are ever sent;
//  - file access: scenarios only under QA/Scenarios (*.json, normalized path), reports only by run id pattern.
public sealed partial class QaUiHost : IAsyncDisposable
{
    public const int MaxBodyBytes = 4 * 1024 * 1024;   // a scenario is at most 1 MB; JSON escaping can grow it
    public const int MaxSseClients = 8;
    public const int SseIntervalMs = 250;
    public const int MaxLinesPerSecond = 200;   // D21
    public const int SseLinesPerInterval = MaxLinesPerSecond * SseIntervalMs / 1000;
    public const int MaxReportsListed = 50;
    private const long MaxReportJsonBytes = 20L * 1024 * 1024;

    private static readonly string[] StaticFiles = { "index.html", "app.js", "app.css" };

    private readonly UiHostOptions _options;
    private readonly ActionRegistry _registry;
    private readonly MarkerStore _markers;
    private WebApplication? _app;
    private int _sseClients;
    // Cancelled first thing in DisposeAsync: open SSE streams end at once instead of holding the host's shutdown.
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;

    // 기능: UI 호스트를 만든다(Hub·RunSession·ActionRegistry 준비, 아직 listen하지 않음).
    // 입력: options - UI 설정, markers - 마커 저장소.
    // 출력: StartCoreAsync 전의 QaUiHost.
    private QaUiHost(UiHostOptions options, MarkerStore markers)
    {
        _options = options;
        _registry = ActionRegistry.CreateDefault();
        _markers = markers;
        Hub = new UiHub();
        Session = new RunSession(options, Hub, _registry, markers);
    }

    public UiHub Hub { get; }
    public RunSession Session { get; }
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/";

    [GeneratedRegex(@"^qa-\d{8}-\d{6}-[0-9a-f]{4}$")]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,80}\.png$")]
    private static partial Regex ShotFile();

    // 기능: QA/Markers.json을 읽고 호스트를 만들어 Kestrel을 띄운다.
    // 입력: options - UI 설정, token - 취소 토큰.
    // 출력: listen 중인 QaUiHost(Port 결정됨). 바인딩에 실패하면 QaToolException.
    public static async Task<QaUiHost> StartAsync(UiHostOptions options, CancellationToken token)
    {
        MarkerStore markers = MarkerStore.LoadFile(Path.Combine(options.RepoRoot, "QA", "Markers.json"));
        var host = new QaUiHost(options, markers);
        await host.StartCoreAsync(token).ConfigureAwait(false);
        return host;
    }

    // 기능: 127.0.0.1의 options.Port에 Kestrel을 띄우고 Guard와 엔드포인트를 등록한 뒤 실제 포트를 읽는다.
    // 입력: token - 취소 토큰.
    // 출력: 반환값 없음. _app과 Port가 설정된다. 주소를 얻지 못하면 QaToolException.
    private async Task StartCoreAsync(CancellationToken token)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(QaUiHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
            kestrel.Listen(IPAddress.Loopback, _options.Port);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();
        WebApplication app = builder.Build();
        app.Use(Guard);
        Map(app);
        await app.StartAsync(token).ConfigureAwait(false);
        _app = app;
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                         ?? throw new QaToolException("The UI did not bind.");
        Port = new Uri(address).Port;
    }

    // 기능: SSE 스트림을 끊고 활성 run을 멈춰 정리를 기다린 뒤 웹 호스트를 멈춘다(두 번째 호출은 무시).
    // 입력: 없음.
    // 출력: 반환값 없음. 호스트가 종료되고 _app이 null이 된다.
    // Stops the active run first (bounded wait for its cleanup), then the web host.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stopping.Cancel();
        await Session.StopAndWaitAsync().ConfigureAwait(false);
        if (_app != null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
        _stopping.Dispose();
    }

    // ---- security ----

    // 기능: 모든 요청에 프레임 금지 헤더를 붙이고 Host를 검사하며, POST면 Origin·Sec-Fetch-Site·Content-Type까지 검사한 뒤 다음 미들웨어로 넘긴다.
    // 입력: ctx - HTTP 컨텍스트, next - 다음 미들웨어.
    // 출력: 반환값 없음. 거부되면 403/415 JSON 오류가 응답된다.
    private async Task Guard(HttpContext ctx, Func<Task> next)
    {
        // No page of this tool (reports included) may be framed by another site (click-jacking).
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        if (!HostAllowed(ctx.Request.Host))
        {
            await Error(ctx, 403, "Host not allowed (the UI answers only on 127.0.0.1/localhost).").ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            string? origin = ctx.Request.Headers.Origin;
            string? site = ctx.Request.Headers["Sec-Fetch-Site"];
            if (origin != null && !OriginAllowed(origin))
            {
                await Error(ctx, 403, "Cross-origin request refused.").ConfigureAwait(false);
                return;
            }
            if (site != null && site is not ("same-origin" or "none"))
            {
                await Error(ctx, 403, "Cross-site request refused.").ConfigureAwait(false);
                return;
            }
            if (ctx.Request.ContentType == null || !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await Error(ctx, 415, "POST bodies must be application/json.").ConfigureAwait(false);
                return;
            }
        }
        await next().ConfigureAwait(false);
    }

    // 기능: 요청 Host 헤더가 이 도구의 loopback 주소인지 본다.
    // 입력: host - 요청 Host 헤더.
    // 출력: 127.0.0.1/localhost/[::1]이고 포트가 같으면 true.
    private bool HostAllowed(HostString host) =>
        host.HasValue && host.Port == Port && host.Host is "127.0.0.1" or "localhost" or "[::1]";

    // 기능: Origin 헤더가 같은 출처인지 본다.
    // 입력: origin - 요청 Origin 헤더.
    // 출력: 세 loopback 이름 중 하나에 이 포트가 붙은 출처면 true.
    private bool OriginAllowed(string origin) =>
        origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}" || origin == $"http://[::1]:{Port}";

    // 기능: 상대 경로를 QA/Scenarios 아래의 .json 절대 경로로 정규화한다.
    // 입력: relative - 요청이 준 상대 경로.
    // 출력: 루트 아래의 절대 경로. 조건에 안 맞으면(비었거나 260자 초과, 절대 경로, ':' 포함, .json 아님, 루트 밖) null.
    // D25: a path under QA/Scenarios ending in .json, or null.
    public string? ScenarioFile(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 260 || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        string root = _options.ScenarioRoot;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---- endpoints ----

    // 기능: 정적 파일·시나리오·run 제어·inspect·보고서 엔드포인트를 등록한다.
    // 입력: app - 라우트를 등록할 앱.
    // 출력: 반환값 없음. 라우트가 등록된다.
    private void Map(WebApplication app)
    {
        app.MapGet("/", ctx => Static(ctx, "index.html"));
        app.MapGet("/{file}", ctx => Static(ctx, (string)ctx.Request.RouteValues["file"]!));

        app.MapGet("/api/tree", ctx => Json(ctx, Tree()));
        app.MapGet("/api/actions", ctx => Json(ctx, _registry.Specs.Select(s => new
        {
            name = s.Name, actor = s.Actor.ToString(), required = s.Required, optional = s.Optional,
            positions = s.PositionParams, serverCommand = s.ServerCommand, arrangeOnly = s.ArrangeOnly,
        })));
        app.MapGet("/api/markers", ctx => Json(ctx, _markers.Markers.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        app.MapGet("/api/scenario", async ctx =>
        {
            string? file = ScenarioFile(ctx.Request.Query["path"]);
            if (file == null) { await Error(ctx, 400, "Path must be a .json file under QA/Scenarios.").ConfigureAwait(false); return; }
            if (!File.Exists(file)) { await Error(ctx, 404, "No such scenario.").ConfigureAwait(false); return; }
            if (new FileInfo(file).Length > ScenarioLoader.MaxFileBytes) { await Error(ctx, 413, "Scenario file too large.").ConfigureAwait(false); return; }
            await Json(ctx, new { path = (string)ctx.Request.Query["path"]!, text = await File.ReadAllTextAsync(file, ctx.RequestAborted).ConfigureAwait(false) }).ConfigureAwait(false);
        });
        app.MapPost("/api/validate", async ctx =>
        {
            JsonElement body = await Body(ctx).ConfigureAwait(false);
            await Json(ctx, Validate(Str(body, "text") ?? string.Empty, out _)).ConfigureAwait(false);
        });
        app.MapPost("/api/save", Save);

        app.MapGet("/api/run", ctx => Json(ctx, Session.State()));
        app.MapPost("/api/run", StartRun);
        app.MapPost("/api/run/retry-scenario", async ctx =>
        {
            UiRunRequest? last = Session.LastRequest;
            if (last == null) { await Error(ctx, 409, "Nothing ran yet.").ConfigureAwait(false); return; }
            // Same scenario text and seed, run from the start (D23); a batch (QA-5) runs again as the same batch.
            await StartRunCore(ctx, new UiRunRequest
            {
                Path = last.Path, Text = last.Text, Seed = last.Seed, Debug = last.Debug, Breakpoints = last.Breakpoints,
                Repeat = last.Mode == "run" ? last.Repeat : 1, SeedSweep = last.Mode == "run" ? last.SeedSweep : null, StopOnFail = last.StopOnFail,
            }).ConfigureAwait(false);
        });
        app.MapPost("/api/run/pause", ctx => Command(ctx, Session.Pause));
        app.MapPost("/api/run/resume", ctx => Command(ctx, Session.Resume));
        app.MapPost("/api/run/step", ctx => Command(ctx, Session.StepOnce));
        app.MapPost("/api/run/stop", ctx => Command(ctx, Session.Stop));
        app.MapPost("/api/run/manual", async ctx =>
        {
            JsonElement body = await Body(ctx).ConfigureAwait(false);
            if (JsonPath.Child(body, "passed") is not { ValueKind: JsonValueKind.True or JsonValueKind.False } passed)
            {
                await Error(ctx, 400, "'passed' must be true or false.").ConfigureAwait(false);
                return;
            }
            string? note = Str(body, "note");
            if (note != null && note.Length > 1000) note = note[..1000];
            if (!Session.AnswerManual(passed.GetBoolean(), note)) { await Error(ctx, 409, "No manual check is waiting.").ConfigureAwait(false); return; }
            await Json(ctx, new { ok = true }).ConfigureAwait(false);
        });
        app.MapPost("/api/run/retry", async ctx =>
        {
            if (!Session.Retry()) { await Error(ctx, 409, "No failed step is held.").ConfigureAwait(false); return; }
            await Json(ctx, new { ok = true, warning = "The step runs again in the same game: the state may have changed since it failed." }).ConfigureAwait(false);
        });
        app.MapPost("/api/run/breakpoints", async ctx =>
        {
            JsonElement body = await Body(ctx).ConfigureAwait(false);
            Session.SetBreakpoints(Ints(body, "indices"));
            await Json(ctx, new { ok = true }).ConfigureAwait(false);
        });
        app.MapGet("/api/stream", Stream);

        app.MapGet("/api/inspect/actors", async ctx =>
        {
            IReadOnlyList<UiActorRow>? rows = Session.Actors();
            if (rows == null) { await Error(ctx, 409, "No live run.").ConfigureAwait(false); return; }
            await Json(ctx, rows).ConfigureAwait(false);
        });
        app.MapGet("/api/inspect/actor", InspectActor);
        app.MapGet("/api/inspect/match", ctx => Inspect(ctx, async t => (object?)await Session.MatchAsync(t).ConfigureAwait(false)));
        app.MapGet("/api/inspect/server", ctx => Inspect(ctx, Session.ServerAsync));

        app.MapGet("/api/reports", ctx => Json(ctx, Reports()));
        app.MapGet("/reports/{runId}/report.html", async ctx =>
        {
            string runId = (string)ctx.Request.RouteValues["runId"]!;
            string file = Path.Combine(_options.ReportRoot, runId, "report.html");
            if (!RunIdPattern().IsMatch(runId) || !File.Exists(file)) { await Error(ctx, 404, "No such report.").ConfigureAwait(false); return; }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(file, ctx.RequestAborted).ConfigureAwait(false);
        });
        // QA-4: the report's screenshot thumbnails (relative links) when it is opened through the UI. Same guards:
        // run id pattern, a plain PNG file name, the reports folder only.
        app.MapGet("/reports/{runId}/screenshots/{file}", async ctx =>
        {
            string runId = (string)ctx.Request.RouteValues["runId"]!;
            string name = (string)ctx.Request.RouteValues["file"]!;
            string path = Path.Combine(_options.ReportRoot, runId, "screenshots", name);
            if (!RunIdPattern().IsMatch(runId) || !ShotFile().IsMatch(name) || !File.Exists(path)) { await Error(ctx, 404, "No such screenshot.").ConfigureAwait(false); return; }
            ctx.Response.ContentType = "image/png";
            await ctx.Response.SendFileAsync(path, ctx.RequestAborted).ConfigureAwait(false);
        });
    }

    // 기능: wwwroot의 허용된 파일(index.html, app.js, app.css)만 Content-Type·no-store 헤더로 보낸다.
    // 입력: ctx - HTTP 컨텍스트, file - 파일 이름.
    // 출력: 반환값 없음. 파일 또는 404 JSON이 응답된다.
    private async Task Static(HttpContext ctx, string file)
    {
        if (Array.IndexOf(StaticFiles, file) < 0) { await Error(ctx, 404, "Not found.").ConfigureAwait(false); return; }
        string path = Path.Combine(_options.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), file);
        if (!File.Exists(path)) { await Error(ctx, 404, $"{file} is missing from wwwroot.").ConfigureAwait(false); return; }
        ctx.Response.ContentType = file.EndsWith(".html") ? "text/html; charset=utf-8" : file.EndsWith(".js") ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await ctx.Response.SendFileAsync(path, ctx.RequestAborted).ConfigureAwait(false);
    }

    // 기능: QA/Scenarios의 분류별 시나리오 목록(경로·이름·태그·malformed)과 QA/Suites의 suite 목록을 만든다.
    // 입력: 없음.
    // 출력: {categories, suites} 익명 객체.
    private object Tree()
    {
        string root = _options.ScenarioRoot;
        var categories = new List<object>();
        if (Directory.Exists(root))
        {
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(500)
                    .Select(f =>
                    {
                        ScenarioLoadResult load = ScenarioLoader.LoadFile(f);
                        return new
                        {
                            path = Path.GetRelativePath(root, f).Replace('\\', '/'),
                            name = load.Scenario?.Name ?? Path.GetFileNameWithoutExtension(f),
                            tags = load.Scenario?.Tags ?? Array.Empty<string>(),
                            malformed = load.Malformed,
                        };
                    }).ToArray();
                categories.Add(new { name = Path.GetFileName(dir), files });
            }
        }
        var suites = new List<object>();
        string suiteDir = Path.Combine(_options.RepoRoot, "QA", "Suites");
        if (Directory.Exists(suiteDir))
        {
            foreach (string f in Directory.GetFiles(suiteDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(100))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string[] scenarios;
                try
                {
                    scenarios = ScenarioCatalog.Resolve(_options.RepoRoot, ScenarioCatalog.SuitePrefix + name)
                        .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToArray();
                }
                catch (QaToolException)
                {
                    scenarios = Array.Empty<string>();
                }
                suites.Add(new { name, scenarios });
            }
        }
        return new { categories, suites };
    }

    // 기능: 시나리오 텍스트를 파싱·검증해 오류·경고·단계 목록을 만든다.
    // 입력: text - 시나리오 JSON 텍스트.
    // 출력: {ok, errors, warnings, steps} 익명 객체와, 오류가 없을 때의 scenario(오류가 있으면 null).
    private object Validate(string text, out ScenarioDefinition? scenario)
    {
        ScenarioLoadResult load = ScenarioLoader.Parse(text);
        var issues = new List<ValidationIssue>();
        foreach (string e in load.Errors) issues.Add(new ValidationIssue(true, "file", e));
        if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, _registry, _markers));
        scenario = issues.Any(i => i.IsError) ? null : load.Scenario;
        return new
        {
            ok = scenario != null,
            errors = issues.Where(i => i.IsError).Select(i => i.ToString()).ToArray(),
            warnings = issues.Where(i => !i.IsError).Select(i => i.ToString()).ToArray(),
            steps = load.Scenario?.Steps.Select(s => new { index = s.Index, id = s.Id, title = ScenarioRunner.Title(s), action = s.Action }).ToArray(),
        };
    }

    // 기능: POST /api/save: 경로·크기를 검사하고 검증을 통과한 시나리오만 QA/Scenarios 아래에 저장한다.
    // 입력: ctx - HTTP 컨텍스트(본문 path·text).
    // 출력: 반환값 없음. 검증 결과 JSON(저장됨) 또는 400/413/422 응답이 쓰인다.
    // D25: only under QA/Scenarios, normalized, and only when it validates (warnings allowed).
    private async Task Save(HttpContext ctx)
    {
        JsonElement body = await Body(ctx).ConfigureAwait(false);
        string? relative = Str(body, "path");
        string? file = ScenarioFile(relative);
        if (file == null) { await Error(ctx, 400, "Path must be a .json file under QA/Scenarios (no '..', no absolute path).").ConfigureAwait(false); return; }
        string text = Str(body, "text") ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > ScenarioLoader.MaxFileBytes) { await Error(ctx, 413, "Scenario too large.").ConfigureAwait(false); return; }
        object result = Validate(text, out ScenarioDefinition? scenario);
        if (scenario == null)
        {
            ctx.Response.StatusCode = 422;
            await Json(ctx, result).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), ctx.RequestAborted).ConfigureAwait(false);
        Hub.Log("QA", $"saved {relative}");
        await Json(ctx, result).ConfigureAwait(false);
    }

    // 기능: POST /api/run 본문을 UiRunRequest로 읽어 StartRunCore에 넘긴다.
    // 입력: ctx - HTTP 컨텍스트.
    // 출력: 반환값 없음. 응답은 StartRunCore가 쓴다.
    private async Task StartRun(HttpContext ctx)
    {
        JsonElement body = await Body(ctx).ConfigureAwait(false);
        var request = new UiRunRequest
        {
            Path = Str(body, "path"),
            Text = Str(body, "text"),
            Mode = Str(body, "mode") ?? "run",
            StepIndex = JsonPath.Child(body, "stepIndex") is { ValueKind: JsonValueKind.Number } i && i.TryGetInt32(out int idx) ? idx : 0,
            Seed = JsonPath.Child(body, "seed") is { ValueKind: JsonValueKind.Number } s && s.TryGetInt32(out int seed) ? seed : null,
            Debug = JsonPath.Child(body, "debug") is { ValueKind: JsonValueKind.True },
            Breakpoints = Ints(body, "breakpoints"),
            // QA-5: range checks are RunSession.Start's (one place for the CLI's and the UI's rules).
            Repeat = JsonPath.Child(body, "repeat") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt32(out int repeat) ? repeat : 1,
            SeedSweep = Str(body, "seedSweep"),
            StopOnFail = JsonPath.Child(body, "stopOnFail") is { ValueKind: JsonValueKind.True },
        };
        await StartRunCore(ctx, request).ConfigureAwait(false);
    }

    // 기능: 모드·경로·텍스트를 검사하고 시나리오를 파싱·검증한 뒤 RunSession.Start로 run을 시작한다.
    // 입력: ctx - HTTP 컨텍스트, request - 실행 요청.
    // 출력: 반환값 없음. 성공이면 run 상태 JSON, 실패면 400/413/422/409 응답이 쓰인다.
    private async Task StartRunCore(HttpContext ctx, UiRunRequest request)
    {
        if (request.Mode is not ("run" or "from" or "until" or "single")) { await Error(ctx, 400, "mode must be run, from, until or single.").ConfigureAwait(false); return; }
        string? file = request.Path != null ? ScenarioFile(request.Path) : null;
        if (request.Path != null && file == null) { await Error(ctx, 400, "Path must be a .json file under QA/Scenarios.").ConfigureAwait(false); return; }
        string? saved = file != null && File.Exists(file) ? await File.ReadAllTextAsync(file, ctx.RequestAborted).ConfigureAwait(false) : null;
        string? text = request.Text ?? saved;
        if (text == null) { await Error(ctx, 400, "Give 'text' or the 'path' of a saved scenario.").ConfigureAwait(false); return; }
        if (Encoding.UTF8.GetByteCount(text) > ScenarioLoader.MaxFileBytes) { await Error(ctx, 413, "Scenario too large.").ConfigureAwait(false); return; }

        ScenarioLoadResult load = ScenarioLoader.Parse(text, file ?? string.Empty);
        var issues = new List<ValidationIssue>();
        foreach (string e in load.Errors) issues.Add(new ValidationIssue(true, "file", e));
        if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, _registry, _markers));
        if (load.Scenario == null || issues.Any(i => i.IsError))
        {
            ctx.Response.StatusCode = 422;
            await Json(ctx, new { ok = false, errors = issues.Where(i => i.IsError).Select(i => i.ToString()).ToArray() }).ConfigureAwait(false);
            return;
        }
        bool unsaved = saved == null || !string.Equals(saved, text, StringComparison.Ordinal);
        request.Text = text;
        string? error = Session.Start(request, load.Scenario, issues.Where(i => !i.IsError).ToArray(), unsaved);
        if (error != null) { await Error(ctx, 409, error).ConfigureAwait(false); return; }
        await Json(ctx, Session.State()).ConfigureAwait(false);
    }

    // 기능: 세션 제어 동작을 실행하고 ok를 응답한다.
    // 입력: ctx - HTTP 컨텍스트, action - 실행할 세션 동작.
    // 출력: 반환값 없음. {ok:true}가 응답된다.
    private static async Task Command(HttpContext ctx, Action action)
    {
        action();
        await Json(ctx, new { ok = true }).ConfigureAwait(false);
    }

    // 기능: GET /api/inspect/actor?alias=로 액터의 게시 상태와 서버 플레이어 상태를 조회한다.
    // 입력: ctx - HTTP 컨텍스트.
    // 출력: 반환값 없음. Inspect가 응답을 쓴다.
    private Task InspectActor(HttpContext ctx)
    {
        string alias = (string?)ctx.Request.Query["alias"] ?? string.Empty;
        return Inspect(ctx, t => Session.ActorAsync(alias, t));
    }

    // 기능: 조회를 RequestTimeout 안에 실행해 JSON으로 응답한다.
    // 입력: ctx - HTTP 컨텍스트, query - 토큰을 받아 결과를 내는 조회.
    // 출력: 반환값 없음. 결과 JSON, 결과가 null이면 409, 서버 오류·시간 초과면 503이 응답된다.
    private static async Task Inspect(HttpContext ctx, Func<CancellationToken, Task<object?>> query)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(QaServerClient.RequestTimeout);
        object? result;
        try
        {
            result = await query(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is QaApiException or ObjectDisposedException or HttpRequestException
                                  || (e is OperationCanceledException && !ctx.RequestAborted.IsCancellationRequested))
        {
            await Error(ctx, 503, $"Server state unavailable: {e.Message}").ConfigureAwait(false);
            return;
        }
        if (result == null) { await Error(ctx, 409, "No live run (or no such actor).").ConfigureAwait(false); return; }
        await Json(ctx, result).ConfigureAwait(false);
    }

    // 기능: 보고서 폴더의 최신 MaxReportsListed개 run(시나리오·상태·소요 시간·URL)을 읽는다.
    // 입력: 없음.
    // 출력: 보고서 요약 객체 배열(읽을 수 없는 report.json은 status "unreadable", 폴더가 없으면 빈 배열).
    // The newest MaxReportsListed run folders (run ids sort by time), with a few fields from report.json.
    private object Reports()
    {
        string root = _options.ReportRoot;
        if (!Directory.Exists(root)) return Array.Empty<object>();
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => n != null && RunIdPattern().IsMatch(n))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Take(MaxReportsListed)
            .Select(runId =>
            {
                string json = Path.Combine(root, runId!, "report.json");
                string? scenario = null, status = null;
                long? duration = null;
                try
                {
                    var info = new FileInfo(json);
                    if (info.Exists && info.Length <= MaxReportJsonBytes)
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));   // text: report.json has a UTF-8 BOM
                        scenario = JsonPath.Child(doc.RootElement, "scenario")?.GetString();
                        status = JsonPath.Child(doc.RootElement, "status")?.ToString();
                        duration = JsonPath.Child(doc.RootElement, "durationMs") is { ValueKind: JsonValueKind.Number } d ? d.GetInt64() : null;
                    }
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
                {
                    status = "unreadable";
                }
                return (object)new { runId, scenario, status, durationMs = duration, url = $"/reports/{runId}/report.html" };
            }).ToArray();
    }

    // 기능: SSE 스트림: 현재 상태와 최근 로그를 보낸 뒤 SseIntervalMs마다 새 이벤트와 제한된 로그 줄을 보내고, 유휴가 길면 keep-alive 주석을 쓴다.
    // 입력: ctx - HTTP 컨텍스트.
    // 출력: 반환값 없음. 페이지가 닫히거나 호스트가 멈출 때까지 응답이 이어진다. MaxSseClients를 넘으면 429.
    // D20-D21: one SSE stream per page. Every SseIntervalMs: all new run events, and at most SseLinesPerInterval log lines
    // (200/s); a backlog beyond that is skipped and its size sent as `skipped`. At most MaxSseClients at once.
    private async Task Stream(HttpContext ctx)
    {
        if (Interlocked.Increment(ref _sseClients) > MaxSseClients)
        {
            Interlocked.Decrement(ref _sseClients);
            await Error(ctx, 429, "Too many open streams.").ConfigureAwait(false);
            return;
        }
        try
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _stopping.Token);
            CancellationToken token = linked.Token;
            // Start with the current state and the recent log, then follow.
            long eventCursor = Hub.LatestEventSeq;
            await Send(ctx, "state", JsonSerializer.Serialize(Session.State(), QaJson.Compact), token).ConfigureAwait(false);
            IReadOnlyList<UiLogLine> tail = Hub.TailLogs(200);
            long logCursor = tail.Count > 0 ? tail[^1].Seq : Hub.LatestLogSeq;
            if (tail.Count > 0) await Send(ctx, "log", JsonSerializer.Serialize(new { lines = tail, skipped = 0 }, QaJson.Compact), token).ConfigureAwait(false);
            int idle = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(SseIntervalMs, token).ConfigureAwait(false);
                bool sent = false;
                (IReadOnlyList<UiEvent> events, long nextEvent, bool lost) = Hub.ReadEvents(eventCursor);
                eventCursor = nextEvent;
                if (lost) await Send(ctx, "state", JsonSerializer.Serialize(Session.State(), QaJson.Compact), token).ConfigureAwait(false);
                foreach (UiEvent e in events)
                {
                    await Send(ctx, e.Type, e.Json, token).ConfigureAwait(false);
                    sent = true;
                }
                UiLogBatch batch = Hub.ReadLogs(logCursor, SseLinesPerInterval);
                logCursor = batch.Next;
                if (batch.Lines.Count > 0 || batch.Skipped > 0)
                {
                    await Send(ctx, "log", JsonSerializer.Serialize(new { lines = batch.Lines, skipped = batch.Skipped }, QaJson.Compact), token).ConfigureAwait(false);
                    sent = true;
                }
                // A comment every ~15 s keeps proxies and the browser from closing an idle stream.
                idle = sent ? 0 : idle + 1;
                if (idle >= 60)
                {
                    idle = 0;
                    await ctx.Response.WriteAsync(": keep-alive\n\n", token).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page went away.
        }
        catch (IOException)
        {
            // The connection broke.
        }
        finally
        {
            Interlocked.Decrement(ref _sseClients);
        }
    }

    // 기능: SSE 이벤트 한 건을 쓰고 flush한다.
    // 입력: ctx - HTTP 컨텍스트, type - 이벤트 이름, json - data 본문, token - 취소 토큰.
    // 출력: 반환값 없음. 응답에 이벤트가 쓰인다.
    private static async Task Send(HttpContext ctx, string type, string json, CancellationToken token)
    {
        await ctx.Response.WriteAsync($"event: {type}\ndata: {json}\n\n", token).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    // 기능: 요청 본문을 JSON 객체로 읽는다.
    // 입력: ctx - HTTP 컨텍스트.
    // 출력: 본문 JSON 객체. 객체가 아니거나 파싱에 실패하면 빈 객체.
    private static async Task<JsonElement> Body(HttpContext ctx)
    {
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : JsonPath.From(new { });
        }
        catch (JsonException)
        {
            return JsonPath.From(new { });
        }
    }

    // 기능: JSON 객체에서 문자열 필드를 읽는다.
    // 입력: body - JSON 객체, name - 필드 이름.
    // 출력: 문자열 값. 없거나 문자열이 아니면 null.
    private static string? Str(JsonElement body, string name) =>
        JsonPath.Child(body, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    // 기능: JSON 객체에서 정수 배열 필드를 읽는다.
    // 입력: body - JSON 객체, name - 필드 이름.
    // 출력: 정수 배열(int 범위 값만, MaxSteps개까지). 배열이 아니면 빈 배열.
    private static int[] Ints(JsonElement body, string name) =>
        JsonPath.Child(body, name) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32()).Take(ScenarioLoader.MaxSteps).ToArray()
            : Array.Empty<int>();

    // 기능: 값을 JSON으로 직렬화해 응답한다.
    // 입력: ctx - HTTP 컨텍스트, value - 직렬화할 값.
    // 출력: 반환값 없음. 응답 본문이 쓰인다.
    private static Task Json(HttpContext ctx, object value)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value, QaJson.Compact), ctx.RequestAborted);
    }

    // 기능: 상태 코드와 {ok:false, error}를 응답한다.
    // 입력: ctx - HTTP 컨텍스트, status - HTTP 상태 코드, message - 오류 설명.
    // 출력: 반환값 없음. 응답 본문이 쓰인다.
    private static Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return Json(ctx, new { ok = false, error = message });
    }

    // The UI host follows Program (StartAsync/StopAsync); it never installs its own Ctrl+C handling.
    private sealed class PassiveLifetime : IHostLifetime
    {
        // 기능: 아무것도 기다리지 않는다(수명은 Program이 관리).
        // 입력: cancellationToken - 사용하지 않음.
        // 출력: 완료된 Task.
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // 기능: 아무것도 하지 않는다(수명은 Program이 관리).
        // 입력: cancellationToken - 사용하지 않음.
        // 출력: 완료된 Task.
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
