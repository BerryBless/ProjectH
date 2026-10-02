using System.Text.Json;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Actions an actor performs through the game protocol (request §18-21, §24: real inputs, the server validates them).
// Each one queues an intent change and then waits on the actor's published state.
public static class ActorActions
{
    public const int MaxPresses = 500;
    public const int MaxHoldMs = 60_000;
    public const float DefaultMoveTolerance = 0.75f;
    public const int ReadyWaitMs = 3000;      // weapon/target readiness inside fire and aim
    private const int AssumedHz = 30;         // the server's SimHz default; only converts ms to input ticks
    private const int SlotSettleTicks = 2;

    public static void Register(ActionRegistry r)
    {
        r.Add(Actor("connect", ConnectAsync));
        r.Add(Actor("disconnect", DisconnectAsync, optional: new[] { "graceful" }));
        r.Add(Actor("reconnect", ReconnectAsync, timeoutMs: 20_000));
        r.Add(Actor("moveTo", MoveToAsync, required: new[] { "position" }, optional: new[] { "tolerance", "sprint" },
            positions: new[] { "position" }, timeoutMs: 30_000));
        r.Add(Actor("moveVector", MoveVectorAsync, required: new[] { "x|y" }, optional: new[] { "x", "y", "milliseconds" }, check: CheckMoveVector,
            defaultTimeout: s => (s.Params.TryGetValue("milliseconds", out JsonElement ms) && Comparison.TryNumber(ms, out double v) ? (int)Math.Min(v, MaxHoldMs) : 0) + ActionSpec.StandardTimeoutMs));
        r.Add(Actor("look", LookAsync, required: new[] { "yaw" }, optional: new[] { "pitch" }));
        r.Add(Actor("aim", AimAsync, required: new[] { "target|at|clear" }, optional: new[] { "target", "at", "clear" }, positions: new[] { "at" },
            check: CheckTargetActor));
        r.Add(Actor("fire", FireAsync, optional: new[] { "count", "holdMilliseconds", "slot", "target" }, check: CheckFire,
            defaultTimeout: s => (s.Params.TryGetValue("holdMilliseconds", out JsonElement h) && Comparison.TryNumber(h, out double v) ? (int)Math.Min(v, MaxHoldMs) : 0) + 15_000));
        r.Add(Actor("stopFire", StopFireAsync));
        r.Add(Actor("press", PressAsync, required: new[] { "button" }, optional: new[] { "count", "hold" }, check: CheckButton));
        r.Add(Actor("release", ReleaseAsync, required: new[] { "button" }, check: CheckButton));
        r.Add(Actor("switchWeapon", SwitchWeaponAsync, required: new[] { "slot" }, check: CheckSlot));
        r.Add(Actor("jump", (ctx, t) => PressButtonsAsync(ctx, InputButtons.Jump, 1, t)));
        r.Add(Actor("sprint", (ctx, t) => HoldAsync(ctx, InputButtons.Sprint, t), optional: new[] { "held" }));
        r.Add(Actor("crouch", (ctx, t) => HoldAsync(ctx, InputButtons.Crouch, t), optional: new[] { "held" }));
        r.Add(Actor("pauseInput", (ctx, t) => PauseInputAsync(ctx, true, t)));
        r.Add(Actor("resumeInput", (ctx, t) => PauseInputAsync(ctx, false, t)));
        r.Add(Actor("build", BuildAsync, required: new[] { "piece", "cellX|position" },
            optional: new[] { "material", "level", "cellZ", "rotation", "count", "dx", "dz", "expect" }, positions: new[] { "position" },
            check: CheckBuild));
    }

    private static DelegateAction Actor(string name, Func<StepContext, CancellationToken, Task<StepOutcome>> run,
        string[]? required = null, string[]? optional = null, string[]? positions = null, int? timeoutMs = null,
        Func<StepDefinition, int>? defaultTimeout = null, Func<StepDefinition, IEnumerable<string>>? check = null) =>
        new(new ActionSpec
        {
            Name = name,
            Actor = ActorUse.Required,
            Required = required ?? Array.Empty<string>(),
            Optional = optional ?? Array.Empty<string>(),
            PositionParams = positions ?? Array.Empty<string>(),
            DefaultTimeout = defaultTimeout ?? (timeoutMs != null ? _ => timeoutMs.Value : null),
            Check = check,
        }, run);

    // ---- validation of literal values ----

    public static bool TryParseButtons(string text, out InputButtons buttons)
    {
        buttons = InputButtons.None;
        foreach (string part in text.Split(new[] { ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out _) || !Enum.TryParse(part, ignoreCase: true, out InputButtons b) || b == InputButtons.None) return false;
            buttons |= b;
        }
        return buttons != InputButtons.None;
    }

    private static IEnumerable<string> CheckButton(StepDefinition s)
    {
        if (s.Params.TryGetValue("button", out JsonElement b) && b.ValueKind == JsonValueKind.String && !Variables.HasReference(b)
            && !TryParseButtons(b.GetString()!, out _))
            yield return $"Unknown button '{b.GetString()}' (one of {string.Join(", ", Enum.GetNames<InputButtons>().Where(n => n != "None"))}).";
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c) && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MaxPresses))
            yield return $"'count' must be 1-{MaxPresses}.";
    }

    private static IEnumerable<string> CheckMoveVector(StepDefinition s)
    {
        if (s.Params.TryGetValue("milliseconds", out JsonElement v) && !Variables.HasReference(v)
            && (!Comparison.TryNumber(v, out double ms) || ms < 1 || ms > MaxHoldMs || ms != Math.Floor(ms)))
            yield return $"'milliseconds' must be an integer 1-{MaxHoldMs}.";
        foreach (string axis in new[] { "x", "y" })
        {
            if (s.Params.TryGetValue(axis, out JsonElement a) && !Variables.HasReference(a) && (!Comparison.TryNumber(a, out double d) || d < -1 || d > 1))
                yield return $"'{axis}' must be -1..1.";
        }
    }

    private static IEnumerable<string> CheckSlot(StepDefinition s)
    {
        if (s.Params.TryGetValue("slot", out JsonElement v) && !Variables.HasReference(v)
            && (!Comparison.TryNumber(v, out double n) || n < 0 || n >= ItemConstantsSlots || n != Math.Floor(n)))
            yield return $"'slot' must be 0-{ItemConstantsSlots - 1} (0-based, like player.currentSlot).";
    }

    private static int ItemConstantsSlots => ProjectH.Shared.Protocol.ItemConstants.WeaponSlotCount;

    private static IEnumerable<string> CheckFire(StepDefinition s)
    {
        if (s.Has("count") && s.Has("holdMilliseconds")) yield return "Give 'count' or 'holdMilliseconds', not both.";
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c) && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MaxPresses || n != Math.Floor(n)))
            yield return $"'count' must be an integer 1-{MaxPresses}.";
        if (s.Params.TryGetValue("holdMilliseconds", out JsonElement h) && !Variables.HasReference(h) && (!Comparison.TryNumber(h, out double ms) || ms <= 0 || ms > MaxHoldMs))
            yield return $"'holdMilliseconds' must be 1-{MaxHoldMs}.";
        foreach (string e in CheckSlot(s)) yield return e;
        foreach (string e in CheckTargetActor(s)) yield return e;
    }

    // 'target' names another actor; the validator checks it exists (ScenarioValidator.ActorParams).
    private static IEnumerable<string> CheckTargetActor(StepDefinition s)
    {
        if (s.Params.TryGetValue("target", out JsonElement t) && t.ValueKind != JsonValueKind.String) yield return "'target' must be an actor id.";
    }

    // ---- connection ----

    private static async Task<StepOutcome> ConnectAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        int before = actor.State.Connections;   // read before sending: the command may apply at once
        (string host, int port) = await ctx.Run.ConnectTargetAsync(actor.Alias).ConfigureAwait(false);
        await actor.SendAsync(new ConnectCommand(host, port, false), token).ConfigureAwait(false);
        return await WaitJoinedAsync(ctx, actor, before, token).ConfigureAwait(false);
    }

    // Joined with a snapshot on a connection newer than `connectionsBefore`; fails at once when that connection drops.
    private static async Task<StepOutcome> WaitJoinedAsync(StepContext ctx, IQaActor actor, int connectionsBefore, CancellationToken token)
    {
        bool done = await ctx.WaitUntilAsync(() =>
        {
            ActorState s = actor.State;
            return s.Connections > connectionsBefore && (s.Disconnected || (s.Joined && s.HasSnapshot));
        }, token).ConfigureAwait(false);
        ActorState state = actor.State;
        if (done && state.Joined) return StepOutcome.Pass($"entity {state.EntityId}", JsonPath.From(state));
        string actual = FlowActions.Describe(state);
        return StepOutcome.Fail(done ? $"Connection closed: {state.DisconnectReason}" : $"Not joined within {ctx.TimeoutMs} ms ({actual})", "joined", actual);
    }

    private static async Task<StepOutcome> DisconnectAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        bool graceful = ctx.Bool("graceful") ?? true;
        bool done = await SendAppliedAsync(ctx, actor, new DisconnectCommand(graceful), token).ConfigureAwait(false)
            && await ctx.WaitUntilAsync(() => actor.State.Status is ActorStatus.Disconnected or ActorStatus.Idle, token).ConfigureAwait(false);
        return done ? StepOutcome.Pass(graceful ? "graceful" : "aborted") : StepOutcome.Fail("Actor did not close its connection.");
    }

    // Same DevPlayerId on a new connection (like BotRunner.Reconnect). First waits until the server no longer counts
    // the old connection as connected: a connect while the old peer still lives would join as a new player instead of
    // resuming the graced one (Match.TryJoin finds graced players only). After an abrupt disconnect that is the
    // server's DisconnectTimeoutMs (5 s by default).
    private static async Task<StepOutcome> ReconnectAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (actor.State.Status is ActorStatus.Joined or ActorStatus.Connecting)
        {
            await actor.SendAsync(new DisconnectCommand(true), token).ConfigureAwait(false);
            await ctx.WaitUntilAsync(() => actor.State.Status == ActorStatus.Disconnected, token).ConfigureAwait(false);
        }
        string serverView = "(not read)";
        while (true)
        {
            JsonElement? player = await ctx.Run.Server.GetPlayerAsync(actor.DevPlayerId, token).ConfigureAwait(false);
            if (player == null || JsonPath.Child(player.Value, "connected") is not { ValueKind: JsonValueKind.True }) break;
            serverView = "server still shows the old connection";
            if (ctx.TimedOut) return StepOutcome.Fail($"Timeout after {ctx.TimeoutMs} ms: {serverView}", "old connection gone", serverView);
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
        int before = actor.State.Connections;
        (string host, int port) = await ctx.Run.ConnectTargetAsync(actor.Alias).ConfigureAwait(false);
        await actor.SendAsync(new ConnectCommand(host, port, true), token).ConfigureAwait(false);
        return await WaitJoinedAsync(ctx, actor, before, token).ConfigureAwait(false);
    }

    private static StepOutcome? RequireJoined(IQaActor actor)
    {
        ActorState s = actor.State;
        return s.Joined && s.HasSnapshot ? null : StepOutcome.Fail($"{actor.Alias} is not in the game ({FlowActions.Describe(s)}).", "joined", FlowActions.Describe(s));
    }

    // ---- movement and looking ----

    private static async Task<StepOutcome> MoveToAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        QaPosition target = ctx.Position("position")!.Value;
        float tolerance = (float)(ctx.Double("tolerance") ?? DefaultMoveTolerance);
        if (tolerance <= 0f) throw new QaStepException("'tolerance' must be positive.");
        var goal = Vec3.From(target.ToGround());
        try
        {
            // Judge only states published after this command (not the previous moveTo's arrival or give-up).
            bool done = await SendAppliedAsync(ctx, actor, new MoveToCommand(goal, tolerance, ctx.Bool("sprint") ?? false), token).ConfigureAwait(false)
                && await ctx.WaitUntilAsync(() =>
            {
                ActorState s = actor.State;
                return !s.Joined || (s.MoveActive && (s.MoveArrived || s.MoveGaveUp));
            }, token).ConfigureAwait(false);
            ActorState state = actor.State;
            string actual = $"{state.Position} ({state.MoveDistance:0.##} m away)";
            if (!state.Joined) return StepOutcome.Fail($"{actor.Alias} left the game while moving ({FlowActions.Describe(state)}).", target.ToString(), actual);
            if (done && state.MoveArrived) return StepOutcome.Pass($"at {state.Position} in {ctx.ElapsedMs} ms", JsonPath.From(state.Position));
            string why = state.MoveGaveUp ? "stuck (no progress)" : $"timeout after {ctx.TimeoutMs} ms";
            return StepOutcome.Fail($"Did not reach {target} within {tolerance} m: {why}", $"within {tolerance} m of {target}", actual);
        }
        finally
        {
            // Stop walking whatever happened; CancellationToken.None so a cancelled step still clears its intent.
            await TrySendAsync(actor, new MoveToCommand(null, tolerance, false)).ConfigureAwait(false);
        }
    }

    private static async Task<StepOutcome> MoveVectorAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        float x = (float)Math.Clamp(ctx.Double("x") ?? 0, -1, 1);
        float y = (float)Math.Clamp(ctx.Double("y") ?? 0, -1, 1);
        int? ms = ctx.Int("milliseconds", 1, MaxHoldMs);
        int ticks = ms == null ? 0 : Math.Max(1, ms.Value * AssumedHz / 1000);
        await actor.SendAsync(new MoveVectorCommand(x, y, ticks), token).ConfigureAwait(false);
        if (ms != null) await Task.Delay(ms.Value, token).ConfigureAwait(false);
        return StepOutcome.Pass(ms == null ? "until changed" : $"{ms} ms");
    }

    private static async Task<StepOutcome> LookAsync(StepContext ctx, CancellationToken token)
    {
        float yaw = (float)ctx.Double("yaw")!.Value;
        float pitch = (float)(ctx.Double("pitch") ?? 0);
        if (pitch < -90f || pitch > 90f) throw new QaStepException("'pitch' must be -90..90 (positive looks down).");
        await ctx.Actor().SendAsync(new LookCommand(yaw, pitch), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    private static async Task<StepOutcome> AimAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (ctx.Bool("clear") == true)
        {
            await actor.SendAsync(new ClearAimCommand(), token).ConfigureAwait(false);
            return StepOutcome.Pass("cleared");
        }
        if (ctx.String("target") is string target) return await AimAtActorAsync(ctx, actor, target, token).ConfigureAwait(false) ?? StepOutcome.Pass($"at {target}");
        QaPosition at = ctx.Position("at") ?? throw new QaStepException("'target', 'at' or 'clear' is required.");
        // No y: aim at chest height above the ground there.
        var point = at.Y != null ? at.ToGround() : at.ToGround() + new System.Numerics.Vector3(0f, BotAim.ChestHeight, 0f);
        await actor.SendAsync(new AimAtPointCommand(Vec3.From(point)), token).ConfigureAwait(false);
        return StepOutcome.Pass($"at {at}");
    }

    // Aims at another actor's entity as the shooter's snapshots show it. Waits (bounded) until the shooter actually
    // sees that entity, so a fire right after does not shoot at a stale guess. Null = aiming.
    private static async Task<StepOutcome?> AimAtActorAsync(StepContext ctx, IQaActor shooter, string targetAlias, CancellationToken token)
    {
        if (RequireJoined(shooter) is { } notJoined) return notJoined;
        IQaActor target = ctx.Run.Actors.Get(targetAlias);
        ushort entity = target.State.EntityId;
        if (!target.State.Joined || entity == 0) return StepOutcome.Fail($"Target {targetAlias} is not in the game ({FlowActions.Describe(target.State)}).", "target joined", FlowActions.Describe(target.State));
        await shooter.SendAsync(new AimAtActorCommand(entity, target), token).ConfigureAwait(false);
        bool seen = await WaitShortAsync(ctx, () => shooter.State.VisibleEntityIds.Contains(entity), token).ConfigureAwait(false);
        return seen ? null : StepOutcome.Fail($"{shooter.Alias} does not see {targetAlias} (entity {entity}) in its snapshots.", "target in snapshot", "not visible");
    }

    // ---- buttons ----

    private static async Task<StepOutcome> FireAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        if (ctx.String("target") is string target && await AimAtActorAsync(ctx, actor, target, token).ConfigureAwait(false) is { } aimFailed) return aimFailed;

        int? slot = ctx.Int("slot", 0, ItemConstantsSlots - 1);
        ActorState s = actor.State;
        // Select the slot (a slot key also brings the weapon back from the harvest or build tool, HarvestRules.SelectTool).
        if (slot != null || !string.Equals(s.Tool, nameof(ProjectH.Shared.Protocol.ToolKind.Weapon), StringComparison.Ordinal))
        {
            int want = slot ?? s.CurrentSlot;
            await SendAppliedAsync(ctx, actor, new ScriptCommand(new[] { new InputStep(SlotButton(want), 1), new InputStep(InputButtons.None, SlotSettleTicks) }), token).ConfigureAwait(false);
            bool selected = await WaitShortAsync(ctx, () =>
            {
                ActorState a = actor.State;
                return a.CurrentSlot == want && a.Tool == "Weapon" && a.ScriptSteps == 0;
            }, token).ConfigureAwait(false);
            if (!selected) return StepOutcome.Fail($"Slot {want} weapon not selected.", $"slot {want}, Weapon tool", $"slot {actor.State.CurrentSlot}, {actor.State.Tool}");
        }
        // The inventory and catalog come over UDP after an HTTP giveWeapon: wait (bounded) until the client has them.
        bool armed = await WaitShortAsync(ctx, () => actor.State.HasInventory && actor.State.WeaponId != 0, token).ConfigureAwait(false);
        if (!armed) return StepOutcome.Fail($"{actor.Alias} has no weapon in slot {actor.State.CurrentSlot}.", "a weapon", "empty slot");

        ActorState before = actor.State;
        int? count = ctx.Int("count", 1, MaxPresses);
        int? holdMs = ctx.Int("holdMilliseconds", 1, MaxHoldMs);
        InputStep[] steps = holdMs != null
            ? new[] { new InputStep(InputButtons.Fire, Math.Max(1, holdMs.Value * AssumedHz / 1000)) }
            : Enumerable.Repeat(new InputStep(InputButtons.Fire, 1, FirePress: true), count ?? 1).ToArray();
        bool done = false;
        try
        {
            done = await SendAppliedAsync(ctx, actor, new ScriptCommand(steps), token).ConfigureAwait(false);
            long expectedPresses = before.PressesSent + steps.Length;
            done = done && await ctx.WaitUntilAsync(() => actor.State.PressesSent >= expectedPresses && actor.State.ScriptSteps == 0, token).ConfigureAwait(false);
        }
        finally
        {
            // A step that ends early (timeout, cancel) must not leave presses running into the next step.
            if (!done) await TrySendAsync(actor, new ClearInputQueueCommand()).ConfigureAwait(false);
        }
        // HitConfirmed for the last shots arrives a round trip later: wait until the count is still for HitSettleMs
        // (at most HitSettleMaxMs) so `hits` is not read early.
        if (done) await SettleHitsAsync(actor, token).ConfigureAwait(false);
        ActorState after = actor.State;
        var result = new { presses = after.PressesSent - before.PressesSent, hits = after.HitsLanded - before.HitsLanded, ammoBefore = before.Ammo, ammoAfter = after.Ammo, weapon = before.WeaponName };
        if (!done) return StepOutcome.Fail($"Presses not sent within {ctx.TimeoutMs} ms ({result.presses}/{steps.Length}).", $"{steps.Length} presses", $"{result.presses}");
        return StepOutcome.Pass($"{before.WeaponName}: {result.presses} press(es), hits confirmed {result.hits}, ammo {result.ammoBefore}->{result.ammoAfter}", JsonPath.From(result));
    }

    private static async Task<StepOutcome> StopFireAsync(StepContext ctx, CancellationToken token)
    {
        await ctx.Actor().SendAsync(new StopFireCommand(), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    private static async Task<StepOutcome> PressAsync(StepContext ctx, CancellationToken token)
    {
        InputButtons buttons = ParseButtons(ctx);
        if (ctx.Bool("hold") == true)
        {
            await ctx.Actor().SendAsync(new HoldCommand(buttons, true), token).ConfigureAwait(false);
            return StepOutcome.Pass($"holding {buttons}");
        }
        return await PressButtonsAsync(ctx, buttons, ctx.Int("count", 1, MaxPresses) ?? 1, token).ConfigureAwait(false);
    }

    private static async Task<StepOutcome> ReleaseAsync(StepContext ctx, CancellationToken token)
    {
        InputButtons buttons = ParseButtons(ctx);
        await ctx.Actor().SendAsync(new HoldCommand(buttons, false), token).ConfigureAwait(false);
        return StepOutcome.Pass($"released {buttons}");
    }

    private static async Task<StepOutcome> SwitchWeaponAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        int slot = ctx.Int("slot", 0, ItemConstantsSlots - 1)!.Value;
        bool done = await SendAppliedAsync(ctx, actor, new ScriptCommand(new[] { new InputStep(SlotButton(slot), 1), new InputStep(InputButtons.None, SlotSettleTicks) }), token).ConfigureAwait(false)
            && await ctx.WaitUntilAsync(() => actor.State.CurrentSlot == slot && actor.State.ScriptSteps == 0, token).ConfigureAwait(false);
        return done ? StepOutcome.Pass($"slot {slot}") : StepOutcome.Fail($"Slot {slot} not selected.", $"slot {slot}", $"slot {actor.State.CurrentSlot}");
    }

    // Edge presses: each held one tick, released one tick, so the server sees a new press every time.
    private static async Task<StepOutcome> PressButtonsAsync(StepContext ctx, InputButtons buttons, int count, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        var steps = new List<InputStep>(count * 2);
        for (int i = 0; i < count; i++)
        {
            steps.Add(new InputStep(buttons, 1));
            steps.Add(new InputStep(InputButtons.None, 1));
        }
        long expected = actor.State.PressesSent + count;
        bool done = false;
        try
        {
            done = await SendAppliedAsync(ctx, actor, new ScriptCommand(steps), token).ConfigureAwait(false)
                && await ctx.WaitUntilAsync(() => actor.State.PressesSent >= expected && actor.State.ScriptSteps == 0, token).ConfigureAwait(false);
        }
        finally
        {
            if (!done) await TrySendAsync(actor, new ClearInputQueueCommand()).ConfigureAwait(false);
        }
        return done ? StepOutcome.Pass($"{buttons} x{count}") : StepOutcome.Fail($"Presses not sent within {ctx.TimeoutMs} ms.");
    }

    private static async Task<StepOutcome> HoldAsync(StepContext ctx, InputButtons button, CancellationToken token)
    {
        bool held = ctx.Bool("held") ?? true;
        await ctx.Actor().SendAsync(new HoldCommand(button, held), token).ConfigureAwait(false);
        return StepOutcome.Pass(held ? $"holding {button}" : $"released {button}");
    }

    private static InputButtons ParseButtons(StepContext ctx)
    {
        string text = ctx.RequireString("button");
        return TryParseButtons(text, out InputButtons b) ? b : throw new QaStepException($"Unknown button '{text}'.");
    }

    private static InputButtons SlotButton(int slot) => slot switch
    {
        0 => InputButtons.Slot1,
        1 => InputButtons.Slot2,
        _ => InputButtons.Slot3,
    };

    public const int HitSettleMs = 150;
    public const int HitSettleMaxMs = 500;

    private static async Task SettleHitsAsync(IQaActor actor, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int hits = actor.State.HitsLanded;
        long stableSince = 0;
        while (clock.ElapsedMilliseconds < HitSettleMaxMs)
        {
            await Task.Delay(StepContext.ActorPollMs, token).ConfigureAwait(false);
            int now = actor.State.HitsLanded;
            if (now != hits)
            {
                hits = now;
                stableSince = clock.ElapsedMilliseconds;
            }
            else if (clock.ElapsedMilliseconds - stableSince >= HitSettleMs)
            {
                return;
            }
        }
    }

    // ---- input pause (request §75) ----

    private static async Task<StepOutcome> PauseInputAsync(StepContext ctx, bool paused, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        bool applied = await SendAppliedAsync(ctx, actor, new PauseInputCommand(paused), token).ConfigureAwait(false);
        return applied ? StepOutcome.Pass(paused ? "input paused (connection kept)" : "input resumed") : StepOutcome.Fail("The actor did not apply the command.");
    }

    // ---- building (real BuildRequest packets) ----

    public const int MaxBuildCount = 8;   // the server keeps 8 waiting requests per player (BuildRequestQueue.Capacity)

    private static IEnumerable<string> CheckBuild(StepDefinition s)
    {
        if (s.Params.TryGetValue("piece", out JsonElement p) && p.ValueKind == JsonValueKind.String && !Variables.HasReference(p)
            && !TryEnum(p.GetString()!, out BuildPieceType _))
            yield return $"Unknown piece '{p.GetString()}' (wall, floor, ramp, roof).";
        if (s.Params.TryGetValue("material", out JsonElement m) && m.ValueKind == JsonValueKind.String && !Variables.HasReference(m)
            && !TryEnum(m.GetString()!, out BuildMaterialType _))
            yield return $"Unknown material '{m.GetString()}' (wood, stone, metal).";
        if (s.Has("cellX") && s.Has("position")) yield return "Give the cell (cellX, level, cellZ) or 'position', not both.";
        if (s.Has("cellX") && !(s.Has("level") && s.Has("cellZ"))) yield return "'cellX' needs 'level' and 'cellZ'.";
        foreach ((string name, int max) in new[] { ("cellX", BuildGrid.CellsX - 1), ("cellZ", BuildGrid.CellsZ - 1), ("level", BuildGrid.Levels - 1), ("rotation", 3), ("count", MaxBuildCount) })
        {
            if (!s.Params.TryGetValue(name, out JsonElement v) || Variables.HasReference(v)) continue;
            int min = name == "count" ? 1 : 0;
            if (!Comparison.TryNumber(v, out double d) || d < min || d > max || d != Math.Floor(d)) yield return $"'{name}' must be an integer {min}-{max}.";
        }
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(text, out _);

    // Places pieces the way a player does (BotBuilder): build mode, then per piece aim a tick and send the request after
    // the input that carries the aim. count/dx/dz: a row of pieces, cell (x + i*dx, z + i*dz) (turbo building: one
    // request every two ticks). Passes when every request got its BuildResult and each code is `expect` (default Ok,
    // "any" accepts all). saveAs: { sent, accepted, pieceId (first accepted), pieceIds[], codes[] }.
    private static async Task<StepOutcome> BuildAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        string pieceText = ctx.RequireString("piece");
        if (!TryEnum(pieceText, out BuildPieceType piece)) throw new QaStepException($"Unknown piece '{pieceText}'.");
        string materialText = ctx.String("material") ?? "wood";
        if (!TryEnum(materialText, out BuildMaterialType material)) throw new QaStepException($"Unknown material '{materialText}'.");
        int x, y, z;
        if (ctx.Position("position") is QaPosition at)
        {
            System.Numerics.Vector3 g = at.ToGround();
            x = BuildGrid.CellX(g.X);
            y = Math.Clamp(BuildGrid.Level(g.Y + 0.1f), 0, BuildGrid.Levels - 1);
            z = BuildGrid.CellZ(g.Z);
        }
        else
        {
            x = ctx.Int("cellX", 0, BuildGrid.CellsX - 1)!.Value;
            y = ctx.Int("level", 0, BuildGrid.Levels - 1) ?? throw new QaStepException("'level' is required with 'cellX'.");
            z = ctx.Int("cellZ", 0, BuildGrid.CellsZ - 1) ?? throw new QaStepException("'cellZ' is required with 'cellX'.");
        }
        int rotation = ctx.Int("rotation", 0, 3) ?? 0;
        int count = ctx.Int("count", 1, MaxBuildCount) ?? 1;
        int dx = ctx.Int("dx", -BuildGrid.CellsX, BuildGrid.CellsX) ?? 0;
        int dz = ctx.Int("dz", -BuildGrid.CellsZ, BuildGrid.CellsZ) ?? 0;
        string expect = ctx.String("expect") ?? nameof(BuildResultCode.Ok);

        var plans = new List<BuildPlan>(count);
        for (int i = 0; i < count; i++)
        {
            if (!BuildGrid.TryNormalize(piece, x + i * dx, y, z + i * dz, rotation, out BuildPieceShape shape))
                throw new QaStepException($"Piece {i + 1}: cell ({x + i * dx}, {y}, {z + i * dz}) rotation {rotation} is off the build grid.");
            plans.Add(new BuildPlan(piece, material, shape.X, shape.Y, shape.Z, shape.Rotation, Vec3.From(BuildGrid.CenterOf(shape))));
        }

        bool done = false;
        try
        {
            var command = new BuildCommand(plans);
            if (!await SendAppliedAsync(ctx, actor, command, token).ConfigureAwait(false))
                return StepOutcome.Fail("The actor did not apply the build command.");
            ushort first = (ushort)actor.State.BuildFirstSequence;
            var expected = new HashSet<int>(Enumerable.Range(0, count).Select(i => (int)(ushort)(first + i)));
            BuildResultInfo[] Results() => actor.State.BuildResults.Where(r => expected.Contains(r.Sequence)).ToArray();
            done = await ctx.WaitUntilAsync(() => Results().Select(r => r.Sequence).Distinct().Count() == count || !actor.State.Joined, token).ConfigureAwait(false);
            BuildResultInfo[] results = Results();
            var value = new
            {
                sent = count - actor.State.BuildsQueued,
                accepted = results.Count(r => r.Code == nameof(BuildResultCode.Ok)),
                pieceId = results.Where(r => r.Code == nameof(BuildResultCode.Ok)).Select(r => (uint?)r.PieceId).FirstOrDefault(),
                pieceIds = results.Where(r => r.Code == nameof(BuildResultCode.Ok)).Select(r => r.PieceId).ToArray(),
                codes = results.Select(r => r.Code).ToArray(),
            };
            string actual = string.Join(", ", value.codes);
            if (!done || results.Length < count)
                return StepOutcome.Fail($"{results.Length}/{count} build results within {ctx.TimeoutMs} ms ({actual}).", $"{count} results", $"{results.Length}: {actual}");
            bool ok = string.Equals(expect, "any", StringComparison.OrdinalIgnoreCase) || results.All(r => string.Equals(r.Code, expect, StringComparison.OrdinalIgnoreCase));
            return ok
                ? StepOutcome.Pass($"{piece} x{count}: {actual}", JsonPath.From(value))
                : StepOutcome.Fail($"Build refused: {actual}", $"all {expect}", actual);
        }
        finally
        {
            if (!done) await TrySendAsync(actor, new ClearInputQueueCommand()).ConfigureAwait(false);
        }
    }

    // A readiness wait inside a step: at most ReadyWaitMs, and never past the step's own limit.
    private static async Task<bool> WaitShortAsync(StepContext ctx, Func<bool> condition, CancellationToken token)
    {
        long until = ctx.ElapsedMs + ReadyWaitMs;
        while (true)
        {
            if (condition()) return true;
            if (ctx.ElapsedMs >= until || ctx.TimedOut) return false;
            await Task.Delay(StepContext.ActorPollMs, token).ConfigureAwait(false);
        }
    }

    // Sends and waits (bounded by the step) until the actor has applied it and published a state after it.
    private static async Task<bool> SendAppliedAsync(StepContext ctx, IQaActor actor, ActorCommand command, CancellationToken token)
    {
        await actor.SendAsync(command, token).ConfigureAwait(false);
        return await ctx.WaitUntilAsync(() => actor.State.LastCommandId >= command.Id, token).ConfigureAwait(false);
    }

    private static async Task TrySendAsync(IQaActor actor, ActorCommand command)
    {
        try
        {
            using var cts = new CancellationTokenSource(1000);
            await actor.SendAsync(command, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump is gone or full: the run is ending anyway, and cleanup closes the actor.
        }
    }
}
