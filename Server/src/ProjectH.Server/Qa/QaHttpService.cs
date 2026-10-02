using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Qa;

// QA-1 D4: the QA Control HTTP endpoint. A hosted service registered after GameServerService only in QA mode, so it
// starts once the UDP port is bound and, on shutdown, stops before the game loop (no request waits on a stopped loop).
//
// The Kestrel app is built from an empty builder: no configuration source, so ASPNETCORE_URLS, --urls, HTTP_PORTS and
// Kestrel:Endpoints cannot add an endpoint; the one listener is bound in code to 127.0.0.1 (every interface only with
// Qa:AllowRemote). Handlers never touch the match: each request becomes a work item (QaControl.SubmitAsync).
// Lifetime: this service owns the app; StopAsync stops it and DisposeAsync releases it (the host disposes this service).
public sealed class QaHttpService : IHostedService, IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A local tool API, never rendered as HTML: keep quotes and '+' readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        // No NaN may break a response: written as "NaN" instead of throwing.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly QaControl _qa;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private WebApplication? _app;

    public QaHttpService(QaControl qa, IHostApplicationLifetime lifetime, ILogger<QaHttpService> logger)
    {
        _qa = qa;
        _lifetime = lifetime;
        _logger = logger;
    }

    // The listener's bound addresses (tests).
    public IReadOnlyList<string> BoundAddresses { get; private set; } = Array.Empty<string>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        QaOptions options = _qa.Options;
        IPAddress address = options.AllowRemote ? IPAddress.Any : IPAddress.Loopback;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(QaHttpService).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = _qa.EnvironmentName,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = QaOptions.MaxBodyBytes;
            kestrel.Listen(address, options.Port);
        });
        builder.Services.AddRoutingCore();
        // The server's host owns Ctrl+C and SIGTERM; this inner app only follows StartAsync / StopAsync.
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();

        WebApplication app = builder.Build();
        Map(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        BoundAddresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
                         ?? Array.Empty<string>();
        int port = BoundAddresses.Select(a => new Uri(a.Replace("*", "localhost").Replace("+", "localhost")).Port).FirstOrDefault();
        _qa.QaPort = port;

        _logger.LogWarning("QA mode is ON: QA Control listens on {Addresses} (environment {Environment}). Never run this on a live server.",
            string.Join(", ", BoundAddresses), _qa.EnvironmentName);
        if (options.AllowRemote) _logger.LogWarning("Qa:AllowRemote is on: QA Control accepts connections from other machines");
        // D12: the QA tool waits for this line (after both listeners are bound) and reads the ports from it.
        Console.Out.WriteLine($"QA_READY gamePort={_qa.GamePort} qaPort={port}");
        Console.Out.Flush();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app != null) await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/qa/health", ctx => Run(ctx, t => QaResult.Data(QaQueries.Health(t))));
        app.MapGet("/qa/players", ctx => Run(ctx, t => QaResult.Data(QaQueries.Players(t.Match))));
        app.MapGet("/qa/players/{devPlayerId}", ctx =>
        {
            string id = (string)ctx.Request.RouteValues["devPlayerId"]!;
            return Run(ctx, t =>
            {
                Game.PlayerEntity? p = QaCommands.FindPlayer(t.Match, id);
                return p == null ? QaResult.Error(404, $"No player '{id}' in the match.") : QaResult.Data(QaQueries.Player(t.Match, p));
            });
        });
        app.MapGet("/qa/match", ctx => Run(ctx, t => QaResult.Data(QaQueries.Match(t.Match, t.Loop.Options.SimHz))));
        app.MapGet("/qa/build", ctx =>
        {
            if (!Query(ctx, "x", -1000, 1000, out double? x, out string? error) || !Query(ctx, "z", -1000, 1000, out double? z, out error) ||
                !Query(ctx, "radius", 0, 1000, out double? radius, out error) || !Query(ctx, "max", 1, QaQueries.MaxPieces, out double? max, out error))
                return Write(ctx, QaResult.Error(400, error!));
            if ((x.HasValue || z.HasValue || radius.HasValue) && !(x.HasValue && z.HasValue && radius.HasValue))
                return Write(ctx, QaResult.Error(400, "x, z and radius go together."));
            return Run(ctx, t => QaResult.Data(QaQueries.Build(t.Match, (float?)x, (float?)z, (float?)radius, (int)(max ?? QaQueries.MaxPieces))));
        });
        app.MapGet("/qa/metrics", ctx =>
        {
            if (!Query(ctx, "windowSeconds", 1, QaOptions.MetricsWindowSeconds, out double? window, out string? error))
                return Write(ctx, QaResult.Error(400, error!));
            return Run(ctx, t => QaResult.Data(Metrics(t, (int)(window ?? 10))));
        });
        app.MapGet("/qa/events", ctx =>
        {
            if (!Query(ctx, "after", 0, long.MaxValue, out double? after, out string? error) ||
                !Query(ctx, "max", 1, QaOptions.EventCapacity, out double? max, out error))
                return Write(ctx, QaResult.Error(400, error!));
            return Run(ctx, t => QaResult.Data(t.Qa.Events.Query((long)(after ?? 0), (int)(max ?? 200))));
        });
        app.MapPost("/qa/command", Command);
        app.MapPost("/qa/server/stop", async ctx =>
        {
            _logger.LogWarning("QA stop request: stopping the server");
            await Write(ctx, QaResult.Ok()).ConfigureAwait(false);
            // After the answer is written; the normal shutdown path (clients get ServerShutdown).
            _ = Task.Run(_lifetime.StopApplication);
        });
    }

    private static object Metrics(QaTick t, int windowSeconds)
    {
        GameLoop loop = t.Loop;
        Diagnostics.HealthCounters h = loop.Health;
        QaTickMetrics ticks = t.Qa.Metrics.Snapshot(windowSeconds, loop.Stats.PacketsInTotal, loop.Stats.PacketsOutTotal);
        return new
        {
            ok = true,
            serverTick = (long)t.Match.ServerTick,
            ticks,
            // Flat copies of the most used numbers (the QA tool's metric paths).
            tickP50Ms = ticks.TickP50Ms,
            tickP95Ms = ticks.TickP95Ms,
            tickP99Ms = ticks.TickP99Ms,
            tickMaxMs = ticks.TickMaxMs,
            qaCommandMs = ticks.QaCommandMs,
            pktInPerSec = ticks.PktInPerSec,
            pktOutPerSec = ticks.PktOutPerSec,
            cpuPercent = ticks.CpuPercent,
            workingSetMB = Environment.WorkingSet / 1048576.0,
            gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
            activeSessions = loop.PeerCount,
            players = t.Match.PlayerCount,
            health = new
            {
                connections = h.Connections, joins = h.Joins, resumes = h.Resumes, graceStarts = h.GraceStarts, graceExpiries = h.GraceExpiries,
                tickFailures = h.TickFailures, loopFailures = h.LoopFailures, matchResets = h.MatchResets, stalls = h.Stalls,
                movementAnomalies = h.MovementAnomalies, badPackets = BadPackets(h),
            },
        };
    }

    private static long BadPackets(Diagnostics.HealthCounters h)
    {
        long total = 0;
        for (int i = 0; i < (int)Diagnostics.BadPacketReason.Count; i++) total += h.BadPackets((Diagnostics.BadPacketReason)i);
        return total;
    }

    private async Task Command(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > QaOptions.MaxBodyBytes)
        {
            await Write(ctx, QaResult.Error(413, $"The body is over {QaOptions.MaxBodyBytes} bytes.")).ConfigureAwait(false);
            return;
        }
        string command;
        string? player;
        string? runId;
        JsonElement args;
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The body must be a JSON object.");
            command = String(root, "command", 64) ?? throw new JsonException("'command' is required.");
            player = String(root, "player", 128);
            runId = String(root, "runId", 128);
            args = root.TryGetProperty("args", out JsonElement a) && a.ValueKind != JsonValueKind.Null ? a.Clone() : default;
            if (args.ValueKind != JsonValueKind.Undefined && args.ValueKind != JsonValueKind.Object)
                throw new JsonException("'args' must be an object.");
        }
        catch (JsonException ex)
        {
            await Write(ctx, QaResult.Error(400, "Bad request: " + ex.Message)).ConfigureAwait(false);
            return;
        }
        catch (BadHttpRequestException ex)
        {
            await Write(ctx, QaResult.Error(ex.StatusCode, "Bad request: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        ILogger logger = _logger;
        QaResult result = await _qa.SubmitAsync(t => QaCommands.Execute(t, command, player, runId, args, logger), ctx.RequestAborted)
            .ConfigureAwait(false);
        // §69: every command is in the server log with its run, so a failed scenario can be followed there.
        _logger.LogInformation("QA command {Command} player={Player} run={RunId}: {Status}", command, player ?? "-", runId ?? "-", result.Status);
        await Write(ctx, result).ConfigureAwait(false);
    }

    private static string? String(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException($"'{name}' must be a string.");
        string text = value.GetString()!;
        if (text.Length > maxLength) throw new JsonException($"'{name}' is longer than {maxLength} characters.");
        return text;
    }

    private static bool Query(HttpContext ctx, string name, double min, double max, out double? value, out string? error)
    {
        value = null;
        error = null;
        string? text = ctx.Request.Query[name];
        if (string.IsNullOrEmpty(text)) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) ||
            number < min || number > max)
        {
            error = $"'{name}' must be a number in {min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }
        value = number;
        return true;
    }

    private async Task Run(HttpContext ctx, QaWork work)
    {
        QaResult result = await _qa.SubmitAsync(work, ctx.RequestAborted).ConfigureAwait(false);
        await Write(ctx, result).ConfigureAwait(false);
    }

    private static async Task Write(HttpContext ctx, QaResult result)
    {
        ctx.Response.StatusCode = result.Status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, result.Body, result.Body.GetType(), Json, ctx.RequestAborted).ConfigureAwait(false);
    }

    // An IHostLifetime that waits for nothing and hooks no signal.
    private sealed class PassiveLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
