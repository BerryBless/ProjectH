using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// A handler from a spec and a function: most actions are a few lines, a class each would only add files.
public sealed class DelegateAction : IScenarioActionHandler
{
    private readonly Func<StepContext, CancellationToken, Task<StepOutcome>> _run;

    public DelegateAction(ActionSpec spec, Func<StepContext, CancellationToken, Task<StepOutcome>> run)
    {
        Spec = spec;
        _run = run;
    }

    public ActionSpec Spec { get; }

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken token) => _run(context, token);
}

// Waiting, assertions, events and actor groups.
public static class FlowActions
{
    public const int MaxWaitMs = 600_000;
    public const int MaxSpawn = ActorManager.MaxActors;

    public static void Register(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "wait",
            Required = new[] { "milliseconds|seconds" },
            DefaultTimeout = s => WaitMs(s) + 1000,
            Check = CheckWait,
        }, WaitAsync));

        string[] operatorParams = Comparison.Operators.Append("tolerance").Append("pieceId").Append("at").Append("radius").Append("windowSeconds").ToArray();
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "assert",
            Actor = ActorUse.Optional,
            Required = new[] { "path" },
            Optional = operatorParams,
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, "path", requireOperator: true),
        }, AssertAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "waitFor",
            Actor = ActorUse.Optional,
            Required = new[] { "condition|path" },
            Optional = operatorParams,
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, s.Has("condition") ? "condition" : "path", requireOperator: true),
        }, WaitForAsync));
        // Reads a value into a variable (saveAs), e.g. the health before a shot.
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "save",
            Actor = ActorUse.Optional,
            Required = new[] { "path" },
            Optional = new[] { "pieceId", "at", "radius", "windowSeconds" },
            PositionParams = new[] { "at" },
            Check = s => CheckAssertion(s, "path", requireOperator: false).Concat(s.SaveAs == null ? new[] { "'save' needs 'saveAs'." } : Array.Empty<string>()),
        }, SaveAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "waitForEvent",
            Actor = ActorUse.Optional,
            Required = new[] { "event" },
            Optional = new[] { "where" },
        }, WaitForEventAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "spawnActors",
            Required = new[] { "count", "prefix" },
            Optional = new[] { "type" },
            Check = CheckSpawn,
            CreatesActors = SpawnedAliases,
        }, SpawnActorsAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "connectAll",
            Optional = new[] { "prefix" },
            DefaultTimeout = _ => 30_000,
        }, ConnectAllAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "disconnectAll",
            Optional = new[] { "prefix", "graceful" },
        }, DisconnectAllAsync));
    }

    private static int WaitMs(StepDefinition s)
    {
        if (s.Params.TryGetValue("milliseconds", out JsonElement ms) && Comparison.TryNumber(ms, out double m)) return (int)Math.Clamp(m, 0, MaxWaitMs);
        if (s.Params.TryGetValue("seconds", out JsonElement sec) && Comparison.TryNumber(sec, out double v)) return (int)Math.Clamp(v * 1000, 0, MaxWaitMs);
        // A ${variable} duration is known only at run time: the longest wait bounds it (the handler waits the real value).
        if (s.Params.ContainsKey("milliseconds") || s.Params.ContainsKey("seconds")) return MaxWaitMs;
        return ActionSpec.StandardTimeoutMs;
    }

    private static IEnumerable<string> CheckWait(StepDefinition s)
    {
        foreach (string name in new[] { "milliseconds", "seconds" })
        {
            if (!s.Params.TryGetValue(name, out JsonElement v) || Variables.HasReference(v)) continue;
            if (!Comparison.TryNumber(v, out double d) || d < 0) yield return $"'{name}' must be a non-negative number.";
            else if ((name == "seconds" ? d * 1000 : d) > MaxWaitMs) yield return $"'{name}' is longer than {MaxWaitMs / 1000} s.";
        }
    }

    private static IEnumerable<string> CheckAssertion(StepDefinition s, string pathParam, bool requireOperator)
    {
        if (s.Params.TryGetValue(pathParam, out JsonElement p))
        {
            if (p.ValueKind != JsonValueKind.String) yield return $"'{pathParam}' must be a string.";
            else
            {
                string path = p.GetString()!;
                string? error = AssertionEngine.CheckPath(path);
                if (error != null) yield return error;
                else if (AssertionEngine.NeedsActor(path) && s.Actor == null) yield return $"'{path}' needs 'actor'.";
            }
        }
        string[] ops = Comparison.Operators.Where(s.Has).ToArray();
        if (requireOperator && ops.Length == 0) yield return $"No operator: give one of {string.Join(", ", Comparison.Operators)}.";
        if (ops.Length > 1) yield return $"Only one operator per step (got {string.Join(", ", ops)}).";
        if (s.Has("tolerance") && !s.Has("approximately")) yield return "'tolerance' only applies to 'approximately'.";
        if (s.Params.TryGetValue("between", out JsonElement b) && !Variables.HasReference(b)
            && (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() != 2 || !Comparison.TryNumber(b[0], out _) || !Comparison.TryNumber(b[1], out _)))
            yield return "'between' needs [low, high] numbers.";
    }

    private static IEnumerable<string> CheckSpawn(StepDefinition s)
    {
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c)
            && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MaxSpawn || n != Math.Floor(n)))
            yield return $"'count' must be an integer 1-{MaxSpawn}.";
        foreach (string alias in SpawnedAliases(s).TakeLast(1))
        {
            string? error = ActorManager.CheckAlias(alias);
            if (error != null) yield return error;
        }
    }

    // prefix-001 .. prefix-NNN, like the bots' names.
    public static IEnumerable<string> SpawnedAliases(StepDefinition s)
    {
        if (!s.Params.TryGetValue("prefix", out JsonElement p) || p.ValueKind != JsonValueKind.String) yield break;
        if (!s.Params.TryGetValue("count", out JsonElement c) || !Comparison.TryNumber(c, out double n)) yield break;
        int count = (int)Math.Clamp(n, 0, MaxSpawn);
        for (int i = 1; i <= count; i++) yield return SpawnAlias(p.GetString()!, i);
    }

    public static string SpawnAlias(string prefix, int i) => $"{prefix}-{i.ToString("000", CultureInfo.InvariantCulture)}";

    private static async Task<StepOutcome> WaitAsync(StepContext ctx, CancellationToken token)
    {
        double ms = ctx.Double("milliseconds") ?? (ctx.Double("seconds") ?? 0) * 1000;
        if (ms < 0 || ms > MaxWaitMs) throw new QaStepException($"Wait must be 0-{MaxWaitMs} ms.");
        await Task.Delay(TimeSpan.FromMilliseconds(ms), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    private static (string Op, JsonElement Expected) Operator(StepContext ctx)
    {
        foreach (string op in Comparison.Operators)
        {
            JsonElement? v = ctx.Param(op);
            if (v != null) return (op, v.Value);
        }
        throw new QaStepException("No operator.");
    }

    private static string PathOf(StepContext ctx) => ctx.String("path") ?? ctx.String("condition") ?? throw new QaStepException("'path' is required.");

    private static async Task<StepOutcome> AssertAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        (string op, JsonElement expected) = Operator(ctx);
        JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
        (bool ok, string expectedText) = Comparison.Evaluate(op, expected, ctx.Param("tolerance"), actual, AssertionEngine.EnumHint(path));
        return ok
            ? StepOutcome.Pass($"{path} = {Comparison.Text(actual)}", actual)
            : StepOutcome.Fail($"{path}: expected {expectedText}, actual {Comparison.Text(actual)}", expectedText, Comparison.Text(actual));
    }

    private static async Task<StepOutcome> SaveAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
        if (actual == null) return StepOutcome.Fail($"{path} has no value to save.", "a value", "(missing)");
        return StepOutcome.Pass($"{path} = {Comparison.Text(actual)}", actual);
    }

    // Polls the same engine as assert at the run's poll interval until it holds or the step's limit passes; the
    // failure shows the last value seen. A QA API error while polling is remembered and polling goes on (the server
    // may be restarting); it is the actual value if nothing else came.
    private static async Task<StepOutcome> WaitForAsync(StepContext ctx, CancellationToken token)
    {
        string path = PathOf(ctx);
        (string op, JsonElement expected) = Operator(ctx);
        JsonElement? tolerance = ctx.Param("tolerance");
        Type? hint = AssertionEngine.EnumHint(path);
        string expectedText = string.Empty;
        string lastActual = "(not read)";
        while (true)
        {
            try
            {
                JsonElement? actual = await AssertionEngine.ResolveAsync(path, ctx, token).ConfigureAwait(false);
                (bool ok, expectedText) = Comparison.Evaluate(op, expected, tolerance, actual, hint);
                lastActual = Comparison.Text(actual);
                if (ok) return StepOutcome.Pass($"{path} = {lastActual} after {ctx.ElapsedMs} ms", actual);
            }
            catch (QaApiException e)
            {
                lastActual = $"(QA API error: {e.Message})";
            }
            if (ctx.TimedOut)
                return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms waiting for {path} {expectedText}; last {lastActual}", expectedText, lastActual);
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
    }

    private static async Task<StepOutcome> WaitForEventAsync(StepContext ctx, CancellationToken token)
    {
        EventCursor events = ctx.Run.Events ?? throw new QaStepException("No event source in this run.");
        string type = ctx.RequireString("event");
        string? player = ctx.Step.Actor != null ? ctx.Actor().DevPlayerId : null;
        JsonElement? where = ctx.Param("where");
        if (where != null && where.Value.ValueKind != JsonValueKind.Object) throw new QaStepException("'where' must be an object of event data fields.");
        bool Match(QaEvent e)
        {
            if (!string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (player != null && !string.Equals(e.Player, player, StringComparison.Ordinal)) return false;
            if (where == null) return true;
            JsonElement? data = JsonPath.Child(e.Raw, "data");
            foreach (JsonProperty p in where.Value.EnumerateObject())
            {
                if (!Comparison.AreEqual(JsonPath.Get(data, p.Name), p.Value)) return false;
            }
            return true;
        }
        while (true)
        {
            await events.FetchAsync(token).ConfigureAwait(false);
            QaEvent? found = events.TakeFirst(Match);
            if (found != null) return StepOutcome.Pass($"{found.Type} seq {found.Seq} tick {found.Tick}", found.Raw);
            if (ctx.TimedOut)
            {
                string expected = type + (player != null ? $" for {player}" : string.Empty);
                return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms waiting for event {expected}", expected, "no such event");
            }
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
    }

    private static async Task<StepOutcome> SpawnActorsAsync(StepContext ctx, CancellationToken token)
    {
        int count = ctx.Int("count", 1, MaxSpawn)!.Value;
        string prefix = ctx.RequireString("prefix");
        string type = ctx.String("type") ?? ActorSpec.HeadlessClient;
        for (int i = 1; i <= count; i++) await ctx.Run.Actors.CreateAsync(SpawnAlias(prefix, i), type, token).ConfigureAwait(false);
        return StepOutcome.Pass($"{count} actors {SpawnAlias(prefix, 1)}..{SpawnAlias(prefix, count)}");
    }

    // Cheap group connect: all at once, then one wait for all to join.
    private static async Task<StepOutcome> ConnectAllAsync(StepContext ctx, CancellationToken token)
    {
        string? prefix = ctx.String("prefix");
        IQaActor[] actors = ctx.Run.Actors.All.Where(a => prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (IQaActor a in actors)
        {
            (string host, int port) = await ctx.Run.ConnectTargetAsync(a.Alias).ConfigureAwait(false);
            await a.SendAsync(new ConnectCommand(host, port, false), token).ConfigureAwait(false);
        }
        bool joined = await ctx.WaitUntilAsync(() => actors.All(a => a.State.Joined && a.State.HasSnapshot), token).ConfigureAwait(false);
        int count = actors.Count(a => a.State.Joined);
        if (joined) return StepOutcome.Pass($"{count} joined");
        string failed = string.Join(", ", actors.Where(a => !a.State.Joined).Take(5).Select(a => $"{a.Alias}: {Describe(a.State)}"));
        return StepOutcome.Fail($"{count}/{actors.Length} joined within {ctx.TimeoutMs} ms ({failed})", $"{actors.Length} joined", $"{count} joined");
    }

    private static async Task<StepOutcome> DisconnectAllAsync(StepContext ctx, CancellationToken token)
    {
        string? prefix = ctx.String("prefix");
        bool graceful = ctx.Bool("graceful") ?? true;
        IQaActor[] actors = ctx.Run.Actors.All.Where(a => prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (IQaActor a in actors) await a.SendAsync(new DisconnectCommand(graceful), token).ConfigureAwait(false);
        await ctx.WaitUntilAsync(() => actors.All(a => a.State.Status is ActorStatus.Disconnected or ActorStatus.Idle), token).ConfigureAwait(false);
        return StepOutcome.Pass($"{actors.Length} disconnected");
    }

    public static string Describe(ActorState s) => s.Status switch
    {
        ActorStatus.Disconnected => $"disconnected ({s.DisconnectReason})",
        ActorStatus.Joined when !s.HasSnapshot => "joined, no snapshot yet",
        _ => s.Status.ToString().ToLowerInvariant(),
    };
}
