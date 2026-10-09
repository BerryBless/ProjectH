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

    // 기능: 칸 선택 입력 뒤 기다릴 Tick을 정한다(리뷰 수정 C2). 칸이 실제로 바뀌면 서버가 새로 든 무기의 교체 대기(EquipTicks) 동안 쏘지 않으므로
    //   그만큼 더 기다린다. 같은 칸(건설·채집 도구에서 무기로 돌아오기만 하는 경우 포함)과 빈 칸은 대기가 없다.
    // 입력: state - Actor의 지금 상태(칸마다의 EquipTicks는 그 Actor가 받은 WeaponCatalog 값, 모르면 0), want - 고를 칸.
    // 출력: 기다릴 Tick 수(SlotSettleTicks + 대기).
    internal static int SlotSettleTicksFor(ActorState state, int want)
    {
        if (want == state.CurrentSlot || want < 0 || want >= state.SlotEquipTicks.Count) return SlotSettleTicks;
        return SlotSettleTicks + Math.Max(0, state.SlotEquipTicks[want]);
    }

    // 기능: Actor 액션(connect·move·aim·fire·press·build·ping·playInputs 등)을 Registry에 등록한다.
    // 입력: r - 등록 대상 Registry.
    // 출력: 반환값 없음. Registry에 Actor 액션 Handler가 추가된다.
    public static void Register(ActionRegistry r)
    {
        // A Unity player needs time to start and join (QA-4); a headless client joins in well under a second.
        r.Add(Actor("connect", ConnectAsync,
            defaultTimeout: s => string.Equals(s.ActorType, ActorSpec.UnityClient, StringComparison.OrdinalIgnoreCase) ? UnityConnectTimeoutMs : ActionSpec.StandardTimeoutMs));
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
        // Phase 14 D7: holding E like the client: one Interact press, then InteractHeld in every input until released
        // ("held": false). A revive or reboot goes on only while it is held.
        r.Add(Actor("holdInteract", HoldInteractAsync, optional: new[] { "held" }));
        // Phase 15 D14: real MapMarker requests. ping: kind location (default), danger, enemy (target = an actor) or item
        // (itemId); count sends that many copies at once (the rate limit tests). waypoint: set at position, or clear.
        r.Add(Actor("ping", PingAsync, optional: new[] { "kind", "position", "target", "itemId", "count" }, positions: new[] { "position" },
            check: CheckPing));
        r.Add(Actor("waypoint", WaypointAsync, required: new[] { "position|clear" }, optional: new[] { "position", "clear" }, positions: new[] { "position" }));
        r.Add(Actor("pauseInput", (ctx, t) => PauseInputAsync(ctx, true, t)));
        r.Add(Actor("resumeInput", (ctx, t) => PauseInputAsync(ctx, false, t)));
        r.Add(Actor("build", BuildAsync, required: new[] { "piece", "cellX|position" },
            optional: new[] { "material", "level", "cellZ", "rotation", "count", "dx", "dz", "expect" }, positions: new[] { "position" },
            check: CheckBuild));
        // Phase 13.5 D13: editing a piece with real BuildEditRequest packets.
        r.Add(Actor("buildEdit", BuildEditAsync, required: new[] { "pieceId|cellX" },
            optional: new[] { "pieceId", "cellX", "level", "cellZ", "piece", "rotation", "edit", "preset", "editRotation", "rawState", "count", "burst",
                "alternate", "duplicate", "expect" },
            check: CheckBuildEdit));
        // QA-5 D34. The recording's length is not known before the run: give a long recording timeoutMilliseconds
        // (convert-recording writes it).
        r.Add(Actor("playInputs", PlayInputsAsync, required: new[] { "file" }, optional: new[] { "speed" }, check: CheckPlayInputs, timeoutMs: 60_000));
        // QA-5 D35: a group of actors walking (load scenarios): every actor whose alias starts with prefix gets the move
        // vector, each facing its own direction (spread evenly) so they spread over the map instead of stacking.
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "moveVectorAll",
            Optional = new[] { "prefix", "x", "y", "spread" },
            Check = CheckMoveVector,
        }, MoveVectorAllAsync));
    }

    // ---- QA-5: replaying a recording (D34) ----

    // 기능: playInputs 단계의 Literal 인자(file 경로·확장자, speed 범위)를 실행 전에 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckPlayInputs(StepDefinition s)
    {
        if (s.Params.TryGetValue("file", out JsonElement f) && !Variables.HasReference(f))
        {
            if (f.ValueKind != JsonValueKind.String || f.GetString()!.Length == 0) yield return "'file' must be the recording's path (relative to the scenario file).";
            else if (!f.GetString()!.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) yield return "'file' must be a .jsonl recording (convert-recording writes <name>.inputs.jsonl).";
        }
        if (s.Params.TryGetValue("speed", out JsonElement v) && !Variables.HasReference(v)
            && (!Comparison.TryNumber(v, out double speed) || speed < PlayInputsCommand.MinSpeed || speed > PlayInputsCommand.MaxSpeed))
            yield return $"'speed' must be {PlayInputsCommand.MinSpeed}-{PlayInputsCommand.MaxSpeed}.";
    }

    // 기능: 시나리오 파일 기준의 녹화 파일 경로를 절대 경로로 푼다(QA/ 밖이면 거부).
    // 입력: run - 실행 중인 Run(시나리오 경로·Repo 루트), file - 시나리오 기준 상대 경로.
    // 출력: 녹화 파일의 절대 경로. 시나리오 파일이 없거나 QA/ 밖이면 QaStepException.
    // The recording file next to the scenario, never outside <repo>/QA (a scenario must not read arbitrary files).
    internal static string ResolveRecording(RunContext run, string file)
    {
        if (run.ScenarioPath.Length == 0) throw new QaStepException("playInputs needs a saved scenario file: the recording path is relative to it.");
        string qaRoot = Path.GetFullPath(Path.Combine(run.RepoRoot.Length > 0 ? run.RepoRoot : Directory.GetCurrentDirectory(), "QA")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(run.ScenarioPath))!, file));
        if (!full.StartsWith(qaRoot, StringComparison.OrdinalIgnoreCase)) throw new QaStepException($"'file' {file} resolves outside QA/ ({full}); recordings live next to their scenario.");
        return full;
    }

    // 기능: 녹화 파일을 읽어 Actor가 Tick마다 한 입력씩 재생하게 하고 끝날 때까지 기다린다(QA-5 D34).
    // 입력: ctx - 단계 문맥(file, speed), token - 취소 토큰.
    // 출력: 재생이 끝나면 Pass(saveAs: inputs, sent, simHz, speed, seconds, ms), Timeout·이탈·Timeout 부족이면 Fail. 중단 시 입력 큐를 비운다.
    private static async Task<StepOutcome> PlayInputsAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        string file = ctx.RequireString("file");
        double speed = ctx.Double("speed") ?? 1.0;
        if (speed < PlayInputsCommand.MinSpeed || speed > PlayInputsCommand.MaxSpeed) throw new QaStepException($"'speed' must be {PlayInputsCommand.MinSpeed}-{PlayInputsCommand.MaxSpeed}.");
        string path = ResolveRecording(ctx.Run, file);
        InputRecording recording;
        try
        {
            recording = await Task.Run(() => InputRecording.Load(path), token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RecordingException or IOException or UnauthorizedAccessException)
        {
            throw new QaStepException($"Recording {file}: {e.Message}");
        }
        // The actor sends one entry per tick at its pump rate (the server's SimHz, about the recording's own).
        int ticks = HeadlessActor.PlaybackTicks(recording.Inputs.Length, speed);
        long needMs = (long)Math.Ceiling(ticks * 1000.0 / recording.SimHz);
        if (needMs + 1000 > ctx.TimeoutMs)
            return StepOutcome.Fail($"The recording plays for about {needMs} ms at speed {speed}, longer than the step timeout {ctx.TimeoutMs} ms. Set timeoutMilliseconds to at least {needMs + 5000}.",
                $"timeout >= {needMs + 1000} ms", $"{ctx.TimeoutMs} ms");
        if (RequireJoined(actor) is { } notJoined) return notJoined;

        var command = new PlayInputsCommand(recording.Inputs, speed);
        bool done = false;
        try
        {
            if (!await SendAppliedAsync(ctx, actor, command, token).ConfigureAwait(false)) return StepOutcome.Fail("The actor did not apply the playback command.");
            done = await ctx.WaitUntilAsync(() =>
            {
                ActorState s = actor.State;
                return s.PlaybackCommandId != command.Id || !s.PlaybackActive || !s.Joined;
            }, token).ConfigureAwait(false);
            ActorState state = actor.State;
            var value = new { inputs = recording.Inputs.Length, sent = state.PlaybackSent, simHz = recording.SimHz, speed, seconds = Math.Round(recording.Seconds, 3), ms = ctx.ElapsedMs };
            if (done && state.PlaybackCommandId == command.Id && state.PlaybackCompleted)
                return StepOutcome.Pass($"{recording.Inputs.Length} recorded inputs sent as {state.PlaybackSent} ticks in {ctx.ElapsedMs} ms", JsonPath.From(value));
            done = false;
            string actual = $"{state.PlaybackSent}/{ticks} sent, {FlowActions.Describe(state)}";
            if (!state.Joined) return StepOutcome.Fail($"{actor.Alias} left the game during the replay ({FlowActions.Describe(state)}).", $"{ticks} inputs sent", actual);
            return StepOutcome.Fail($"Replay not finished within {ctx.TimeoutMs} ms.", $"{ticks} inputs sent", actual);
        }
        finally
        {
            // Timeout, cancel or a lost connection: the rest of the recording must not leak into the next step.
            if (!done) await TrySendAsync(actor, new ClearInputQueueCommand()).ConfigureAwait(false);
        }
    }

    // 기능: 별칭이 prefix로 시작하는 모든 Headless Actor에 같은 이동 벡터를 보낸다(spread면 방향을 고르게 나눠 바라보게 한다).
    // 입력: ctx - 단계 문맥(prefix, x, y, spread), token - 취소 토큰.
    // 출력: 대상이 있으면 Pass, Headless Actor가 하나도 없으면 Fail.
    private static async Task<StepOutcome> MoveVectorAllAsync(StepContext ctx, CancellationToken token)
    {
        string? prefix = ctx.String("prefix");
        float x = (float)Math.Clamp(ctx.Double("x") ?? 0, -1, 1);
        float y = (float)Math.Clamp(ctx.Double("y") ?? 1, -1, 1);
        bool spread = ctx.Bool("spread") ?? true;
        IQaActor[] actors = ctx.Run.Actors.All.Where(a => a is not UnityActor && (prefix == null || a.Alias.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
        if (actors.Length == 0) return StepOutcome.Fail($"No headless actors{(prefix != null ? $" with prefix '{prefix}'" : string.Empty)}.");
        for (int i = 0; i < actors.Length; i++)
        {
            if (spread) await actors[i].SendAsync(new LookCommand(360f * i / actors.Length, 0f), token).ConfigureAwait(false);
            await actors[i].SendAsync(new MoveVectorCommand(x, y, 0), token).ConfigureAwait(false);
        }
        return StepOutcome.Pass($"{actors.Length} actors moving ({x}, {y}){(spread ? ", directions spread" : string.Empty)}");
    }

    // 기능: Actor가 필수인 액션 정의(ActionSpec + 실행 Delegate)를 만든다.
    // 입력: name - 액션 이름, run - 실행 함수, required - 필수 Parameter, optional - 선택 Parameter, positions - 위치 Parameter, timeoutMs - 고정 기본 Timeout, defaultTimeout - 단계별 기본 Timeout 계산, check - Literal 검사.
    // 출력: Actor = Required로 설정된 DelegateAction.
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

    // 기능: ",", "|", "+"로 이어진 버튼 이름 목록을 InputButtons Flag로 파싱한다(숫자·None은 거부).
    // 입력: text - 버튼 이름 목록, buttons - 파싱된 Flag(out).
    // 출력: 하나 이상 유효하면 true와 Flag, 아니면 false.
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

    // 기능: press·release 단계의 Literal 인자(button 이름, count 범위)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckButton(StepDefinition s)
    {
        if (s.Params.TryGetValue("button", out JsonElement b) && b.ValueKind == JsonValueKind.String && !Variables.HasReference(b)
            && !TryParseButtons(b.GetString()!, out _))
            yield return $"Unknown button '{b.GetString()}' (one of {string.Join(", ", Enum.GetNames<InputButtons>().Where(n => n != "None"))}).";
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c) && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MaxPresses))
            yield return $"'count' must be 1-{MaxPresses}.";
    }

    // 기능: moveVector·moveVectorAll 단계의 Literal 인자(milliseconds 정수 범위, x·y -1..1)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
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

    // 기능: slot 인자가 0부터 무기 칸 수 미만의 정수인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckSlot(StepDefinition s)
    {
        if (s.Params.TryGetValue("slot", out JsonElement v) && !Variables.HasReference(v)
            && (!Comparison.TryNumber(v, out double n) || n < 0 || n >= ItemConstantsSlots || n != Math.Floor(n)))
            yield return $"'slot' must be 0-{ItemConstantsSlots - 1} (0-based, like player.currentSlot).";
    }

    private static int ItemConstantsSlots => ProjectH.Shared.Protocol.ItemConstants.WeaponSlotCount;

    // 기능: fire 단계의 Literal 인자(count·holdMilliseconds 택일과 범위, slot, target)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
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

    // 기능: target 인자가 문자열(Actor 별칭)인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    // 'target' names another actor; the validator checks it exists (ScenarioValidator.ActorParams).
    private static IEnumerable<string> CheckTargetActor(StepDefinition s)
    {
        if (s.Params.TryGetValue("target", out JsonElement t) && t.ValueKind != JsonValueKind.String) yield return "'target' must be an actor id.";
    }

    // ---- connection ----

    public const int UnityConnectTimeoutMs = 90_000;

    // 기능: Actor를 서버에 새 연결로 접속시키고 Join·Snapshot 수신까지 기다린다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: Join되면 Pass(saveAs: Actor 상태), Unity Player를 띄울 수 없으면 Skip(나머지도 건너뜀), 연결 종료·Timeout이면 Fail.
    private static async Task<StepOutcome> ConnectAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        // No Unity player on this machine: skip the rest (like a missing Docker), not a game failure.
        if (actor is UnityActor { CannotStart: string why }) return StepOutcome.Skip(why, skipRest: true);
        int before = actor.State.Connections;   // read before sending: the command may apply at once
        (string host, int port) = await ctx.Run.ConnectTargetAsync(actor.Alias).ConfigureAwait(false);
        await actor.SendAsync(new ConnectCommand(host, port, false), token).ConfigureAwait(false);
        return await WaitJoinedAsync(ctx, actor, before, token).ConfigureAwait(false);
    }

    // 기능: connectionsBefore 이후의 새 연결에서 Join과 Snapshot 수신, 또는 연결 끊김까지 기다린다.
    // 입력: ctx - 단계 문맥, actor - 대상 Actor, connectionsBefore - 명령 전의 연결 횟수, token - 취소 토큰.
    // 출력: Join되면 Pass(entity id, saveAs: Actor 상태), 연결이 닫히거나 Timeout이면 Fail.
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

    // 기능: Actor의 연결을 끊고(graceful 기본, false면 즉시 중단) 연결이 닫힐 때까지 기다린다.
    // 입력: ctx - 단계 문맥(graceful), token - 취소 토큰.
    // 출력: 연결이 닫히면 Pass, 명령 미적용·Timeout이면 Fail.
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
    // 기능: 같은 DevPlayerId로 재접속한다. 연결 중이면 먼저 끊고, 서버가 옛 연결을 더 이상 연결됨으로 보지 않을 때까지 기다린 뒤 접속한다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 재Join되면 Pass, 옛 연결이 Timeout까지 남아 있거나 Join 실패면 Fail.
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

    // 기능: Actor가 게임에 들어와 Snapshot을 받은 상태인지 확인한다.
    // 입력: actor - 검사할 Actor.
    // 출력: 들어와 있으면 null, 아니면 그 이유를 담은 Fail.
    private static StepOutcome? RequireJoined(IQaActor actor)
    {
        ActorState s = actor.State;
        return s.Joined && s.HasSnapshot ? null : StepOutcome.Fail($"{actor.Alias} is not in the game ({FlowActions.Describe(s)}).", "joined", FlowActions.Describe(s));
    }

    // ---- movement and looking ----

    // 기능: Actor를 목표 위치까지 걷게 하고 도착·포기·이탈까지 기다린다. 끝나면 항상 이동 의도를 지운다.
    // 입력: ctx - 단계 문맥(position, tolerance, sprint), token - 취소 토큰.
    // 출력: tolerance 안에 도착하면 Pass(saveAs: 위치), 막힘·Timeout·이탈이면 Fail.
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

    // 기능: Actor에 이동 벡터를 보낸다. milliseconds가 있으면 그 시간만큼 Tick으로 환산해 보내고 단계가 그만큼 기다린다.
    // 입력: ctx - 단계 문맥(x, y, milliseconds), token - 취소 토큰.
    // 출력: Pass(지속 시간 설명).
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

    // 기능: Actor의 시선(yaw, pitch)을 설정한다.
    // 입력: ctx - 단계 문맥(yaw, pitch -90..90), token - 취소 토큰.
    // 출력: Pass. pitch가 범위 밖이면 QaStepException.
    private static async Task<StepOutcome> LookAsync(StepContext ctx, CancellationToken token)
    {
        float yaw = (float)ctx.Double("yaw")!.Value;
        float pitch = (float)(ctx.Double("pitch") ?? 0);
        if (pitch < -90f || pitch > 90f) throw new QaStepException("'pitch' must be -90..90 (positive looks down).");
        await ctx.Actor().SendAsync(new LookCommand(yaw, pitch), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    // 기능: 조준을 지우거나(clear), 다른 Actor(target) 또는 지점(at, y가 없으면 가슴 높이)에 조준한다.
    // 입력: ctx - 단계 문맥(target, at, clear), token - 취소 토큰.
    // 출력: Pass, 대상이 보이지 않거나 게임에 없으면 Fail. 세 인자가 모두 없으면 QaStepException.
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
    // 기능: 다른 Actor의 Entity를 조준시키고, 사수의 Snapshot에 그 Entity가 보일 때까지 짧게 기다린다.
    // 입력: ctx - 단계 문맥, shooter - 조준하는 Actor, targetAlias - 대상 Actor 별칭, token - 취소 토큰.
    // 출력: 조준 중이면 null, 사수·대상이 게임에 없거나 대상이 보이지 않으면 Fail.
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

    // 기능: 무기 칸을 고르고(필요 시 target 조준) count번 발사하거나 holdMilliseconds 동안 누른 뒤 HitConfirmed가 안정될 때까지 기다린다.
    // 입력: ctx - 단계 문맥(count, holdMilliseconds, slot, target), token - 취소 토큰.
    // 출력: 입력을 다 보내면 Pass(saveAs: presses, hits, ammoBefore, ammoAfter, weapon), 칸 선택·무기 없음·Timeout이면 Fail. 중단 시 입력 큐를 비운다.
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
            await SendAppliedAsync(ctx, actor, new ScriptCommand(new[] { new InputStep(SlotButton(want), 1), new InputStep(InputButtons.None, SlotSettleTicksFor(s, want)) }), token).ConfigureAwait(false);
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

    // 기능: Actor의 발사 입력을 멈춘다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: Pass.
    private static async Task<StepOutcome> StopFireAsync(StepContext ctx, CancellationToken token)
    {
        await ctx.Actor().SendAsync(new StopFireCommand(), token).ConfigureAwait(false);
        return StepOutcome.Pass();
    }

    // 기능: 버튼을 count번 Edge 입력하거나(hold true면) 계속 누른 상태로 둔다.
    // 입력: ctx - 단계 문맥(button, count, hold), token - 취소 토큰.
    // 출력: 입력을 보내면 Pass, Timeout이면 Fail.
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

    // 기능: 누르고 있던 버튼을 놓는다.
    // 입력: ctx - 단계 문맥(button), token - 취소 토큰.
    // 출력: Pass.
    private static async Task<StepOutcome> ReleaseAsync(StepContext ctx, CancellationToken token)
    {
        InputButtons buttons = ParseButtons(ctx);
        await ctx.Actor().SendAsync(new HoldCommand(buttons, false), token).ConfigureAwait(false);
        return StepOutcome.Pass($"released {buttons}");
    }

    // 기능: 무기 칸 키를 눌러 slot을 고르고 교체 대기가 끝날 때까지 기다린다.
    // 입력: ctx - 단계 문맥(slot), token - 취소 토큰.
    // 출력: 칸이 바뀌면 Pass, 아니면 Fail.
    private static async Task<StepOutcome> SwitchWeaponAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        int slot = ctx.Int("slot", 0, ItemConstantsSlots - 1)!.Value;
        bool done = await SendAppliedAsync(ctx, actor, new ScriptCommand(new[] { new InputStep(SlotButton(slot), 1), new InputStep(InputButtons.None, SlotSettleTicksFor(actor.State, slot)) }), token).ConfigureAwait(false)
            && await ctx.WaitUntilAsync(() => actor.State.CurrentSlot == slot && actor.State.ScriptSteps == 0, token).ConfigureAwait(false);
        return done ? StepOutcome.Pass($"slot {slot}") : StepOutcome.Fail($"Slot {slot} not selected.", $"slot {slot}", $"slot {actor.State.CurrentSlot}");
    }

    // 기능: 버튼을 count번 Edge 입력(1 Tick 누름, 1 Tick 뗌)하고 다 보내질 때까지 기다린다.
    // 입력: ctx - 단계 문맥, buttons - 누를 버튼 Flag, count - 반복 횟수, token - 취소 토큰.
    // 출력: 다 보내면 Pass, 미Join·Timeout이면 Fail. 중단 시 입력 큐를 비운다.
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

    // 기능: 버튼을 계속 누르거나(held 기본 true) 놓는다(sprint·crouch).
    // 입력: ctx - 단계 문맥(held), button - 대상 버튼, token - 취소 토큰.
    // 출력: Pass.
    private static async Task<StepOutcome> HoldAsync(StepContext ctx, InputButtons button, CancellationToken token)
    {
        bool held = ctx.Bool("held") ?? true;
        await ctx.Actor().SendAsync(new HoldCommand(button, held), token).ConfigureAwait(false);
        return StepOutcome.Pass(held ? $"holding {button}" : $"released {button}");
    }

    // 기능: Phase 14 holdInteract: E를 누르기 시작하거나(InteractHeld 유지 + Interact 한 번) 놓는다(held false).
    // 입력: ctx - 단계, token - 취소.
    // 출력: 보낸 내용을 담은 Pass.
    private static async Task<StepOutcome> HoldInteractAsync(StepContext ctx, CancellationToken token)
    {
        bool held = ctx.Bool("held") ?? true;
        await ctx.Actor().SendAsync(new HoldCommand(InputButtons.InteractHeld, held), token).ConfigureAwait(false);
        if (!held) return StepOutcome.Pass("released E (InteractHeld)");
        StepOutcome pressed = await PressButtonsAsync(ctx, InputButtons.Interact, 1, token).ConfigureAwait(false);
        return pressed.Passed ? StepOutcome.Pass("holding E (InteractHeld, one Interact press)") : pressed;
    }

    // ---- Phase 15: pings and waypoints ----

    private static readonly string[] PingKinds = { "location", "enemy", "item", "danger" };

    // 기능: ping Step의 Literal 인자를 실행 전에 검사한다(종류, 종류별 필수 인자, 횟수).
    // 입력: s - Step.
    // 출력: 오류 문장들.
    private static IEnumerable<string> CheckPing(StepDefinition s)
    {
        string kind = "location";
        if (s.Params.TryGetValue("kind", out JsonElement k) && !Variables.HasReference(k))
        {
            kind = k.ValueKind == JsonValueKind.String ? k.GetString()!.ToLowerInvariant() : string.Empty;
            if (Array.IndexOf(PingKinds, kind) < 0) yield return $"'kind' must be one of {string.Join(", ", PingKinds)}.";
        }
        if (kind == "enemy" && !s.Has("target")) yield return "An enemy ping needs 'target' (the actor pinged).";
        if (kind == "item" && !s.Has("itemId")) yield return "An item ping needs 'itemId'.";
        if (kind is "location" or "danger" && !s.Has("position")) yield return $"A {kind} ping needs 'position'.";
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c)
            && (!Comparison.TryNumber(c, out double n) || n < 1 || n > MapMarkerCommand.MaxMarkers || n != Math.Floor(n)))
            yield return $"'count' must be an integer 1-{MapMarkerCommand.MaxMarkers}.";
        foreach (string e in CheckTargetActor(s)) yield return e;
    }

    // 기능: Phase 15 ping: MapMarker 요청을 만들어 count번 한꺼번에 보낸다. 위치에 y가 없으면 지형 높이다. Enemy는 대상 배우의 Entity id와
    //   (position이 없으면) 이 배우가 아는 대상 위치를 보낸다. 서버의 판정은 player.teamPings·actor.pings로 확인한다.
    // 입력: ctx - 단계, token - 취소.
    // 출력: 보낸 내용을 담은 Pass, 연결이 없거나 대상이 없으면 Fail.
    private static async Task<StepOutcome> PingAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        string kindText = (ctx.String("kind") ?? "location").ToLowerInvariant();
        MapMarkerKind kind = kindText switch
        {
            "location" => MapMarkerKind.Location,
            "enemy" => MapMarkerKind.Enemy,
            "item" => MapMarkerKind.Item,
            "danger" => MapMarkerKind.Danger,
            _ => throw new QaStepException($"'kind' must be one of {string.Join(", ", PingKinds)}."),
        };
        int count = ctx.Int("count", 1, MapMarkerCommand.MaxMarkers) ?? 1;
        System.Numerics.Vector3 at = ctx.Position("position") is QaPosition p ? p.ToGround() : default;
        ushort targetId = 0;
        if (kind == MapMarkerKind.Enemy)
        {
            string alias = ctx.RequireString("target");
            IQaActor target = ctx.Run.Actors.Get(alias);
            targetId = target.State.EntityId;
            if (!target.State.Joined || targetId == 0)
                return StepOutcome.Fail($"Target {alias} is not in the game ({FlowActions.Describe(target.State)}).", "target joined", FlowActions.Describe(target.State));
            if (ctx.Position("position") == null) at = target.State.Position.ToVector3();
        }
        else if (kind == MapMarkerKind.Item)
        {
            int id = ctx.Int("itemId", 1, ushort.MaxValue) ?? throw new QaStepException("An item ping needs 'itemId'.");
            targetId = (ushort)id;
        }
        var marker = new MapMarker { Kind = kind, Position = at, TargetId = targetId };
        var markers = new MapMarker[count];
        Array.Fill(markers, marker);
        long sentBefore = actor.State.MapMarkersSent;
        if (!await SendAppliedAsync(ctx, actor, new MapMarkerCommand(markers), token).ConfigureAwait(false))
            return StepOutcome.Fail("The actor did not apply the ping command.");
        ActorState s = actor.State;
        if (s.MapMarkersSent - sentBefore < count) return StepOutcome.Fail($"Pings not sent: {s.Error}", $"{count} sent", $"{s.MapMarkersSent - sentBefore} sent");
        return StepOutcome.Pass($"{kind} ping x{count} at ({at.X:0.##}, {at.Y:0.##}, {at.Z:0.##}){(targetId != 0 ? $" target {targetId}" : string.Empty)}");
    }

    // 기능: Phase 15 waypoint: 이 배우의 Waypoint를 position에 두거나(y가 없으면 지형 높이, 지도 클릭과 같다) 지운다(clear).
    // 입력: ctx - 단계, token - 취소.
    // 출력: 보낸 내용을 담은 Pass, 연결이 없으면 Fail.
    private static async Task<StepOutcome> WaypointAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        bool clear = ctx.Bool("clear") ?? false;
        QaPosition? position = ctx.Position("position");
        if (!clear && position == null) throw new QaStepException("'position' or 'clear' is required.");
        var marker = clear
            ? new MapMarker { Kind = MapMarkerKind.WaypointClear }
            : new MapMarker { Kind = MapMarkerKind.WaypointSet, Position = position!.Value.ToGround() };
        long sentBefore = actor.State.MapMarkersSent;
        if (!await SendAppliedAsync(ctx, actor, new MapMarkerCommand(new[] { marker }), token).ConfigureAwait(false))
            return StepOutcome.Fail("The actor did not apply the waypoint command.");
        if (actor.State.MapMarkersSent == sentBefore) return StepOutcome.Fail($"Waypoint not sent: {actor.State.Error}");
        return StepOutcome.Pass(clear ? "waypoint cleared" : $"waypoint at {position}");
    }

    // 기능: 단계의 button 인자를 InputButtons로 파싱한다.
    // 입력: ctx - 단계 문맥(button).
    // 출력: 파싱된 버튼 Flag. 없거나 모르는 이름이면 QaStepException.
    private static InputButtons ParseButtons(StepContext ctx)
    {
        string text = ctx.RequireString("button");
        return TryParseButtons(text, out InputButtons b) ? b : throw new QaStepException($"Unknown button '{text}'.");
    }

    // 기능: 0부터 세는 무기 칸 번호를 해당 칸 선택 버튼으로 바꾼다.
    // 입력: slot - 칸 번호(0, 1, 그 외는 3번째 칸).
    // 출력: Slot1·Slot2·Slot3 중 하나.
    private static InputButtons SlotButton(int slot) => slot switch
    {
        0 => InputButtons.Slot1,
        1 => InputButtons.Slot2,
        _ => InputButtons.Slot3,
    };

    public const int HitSettleMs = 150;
    public const int HitSettleMaxMs = 500;

    // 기능: HitsLanded가 HitSettleMs 동안 변하지 않을 때까지(최대 HitSettleMaxMs) 기다린다.
    // 입력: actor - 대상 Actor, token - 취소 토큰.
    // 출력: 반환값 없음. 늦게 오는 HitConfirmed가 집계될 시간을 준다.
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

    // 기능: 연결은 유지한 채 Actor의 입력 전송을 멈추거나 다시 시작한다(request §75).
    // 입력: ctx - 단계 문맥, paused - true면 멈춤, false면 재개, token - 취소 토큰.
    // 출력: 명령이 적용되면 Pass, 아니면 Fail.
    private static async Task<StepOutcome> PauseInputAsync(StepContext ctx, bool paused, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        bool applied = await SendAppliedAsync(ctx, actor, new PauseInputCommand(paused), token).ConfigureAwait(false);
        return applied ? StepOutcome.Pass(paused ? "input paused (connection kept)" : "input resumed") : StepOutcome.Fail("The actor did not apply the command.");
    }

    // ---- building (real BuildRequest packets) ----

    public const int MaxBuildCount = 8;   // the server keeps 8 waiting requests per player (BuildRequestQueue.Capacity)

    // 기능: build 단계의 Literal 인자(piece·material 이름, 칸·position 택일, 칸·rotation·count 범위)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
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

    // ---- editing (real BuildEditRequest packets, Phase 13.5 D13) ----

    // Named edit states (BuildEdit tile numbering: wall tile = column + 3 x row, rows bottom to top; floor quadrant =
    // x half + 2 x z half; roof 0-6).
    private static readonly Dictionary<string, int> s_editPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reset"] = 0,
        ["window"] = 1 << 4,
        ["door"] = (1 << 1) | (1 << 4),
        ["halfWall"] = 0b111_000_000,
        ["tallOpening"] = (1 << 1) | (1 << 4) | (1 << 7),
        ["eyeRow"] = 0b000_111_000,
        ["floorHalf"] = 0b0011,
        ["floorQuarter"] = 0b0001,
        ["floorDiagonal"] = 0b1001,
        ["roofFlat"] = BuildEdit.RoofFlat,
        ["roofPassage"] = BuildEdit.RoofPassage,
    };

    // 기능: buildEdit 단계의 정적 검사(대상, 상태, 개수, 기대 코드).
    // 입력: s - 단계 정의.
    // 출력: 문제 설명들(없으면 빈 목록).
    private static IEnumerable<string> CheckBuildEdit(StepDefinition s)
    {
        if (s.Has("pieceId") && s.Has("cellX")) yield return "Give 'pieceId' or the cell (cellX, level, cellZ, piece), not both.";
        if (s.Has("cellX") && !(s.Has("level") && s.Has("cellZ") && s.Has("piece"))) yield return "'cellX' needs 'level', 'cellZ' and 'piece'.";
        int states = (s.Has("edit") ? 1 : 0) + (s.Has("preset") ? 1 : 0) + (s.Has("rawState") ? 1 : 0);
        if (states != 1) yield return "Give exactly one of 'edit', 'preset' or 'rawState'.";
        if (s.Params.TryGetValue("preset", out JsonElement p) && p.ValueKind == JsonValueKind.String && !Variables.HasReference(p)
            && !s_editPresets.ContainsKey(p.GetString()!))
            yield return $"Unknown preset '{p.GetString()}' ({string.Join(", ", s_editPresets.Keys)}).";
        if (s.Params.TryGetValue("piece", out JsonElement t) && t.ValueKind == JsonValueKind.String && !Variables.HasReference(t)
            && !TryEnum(t.GetString()!, out BuildPieceType _))
            yield return $"Unknown piece '{t.GetString()}' (wall, floor, ramp, roof).";
        if (s.Params.TryGetValue("expect", out JsonElement e) && e.ValueKind == JsonValueKind.String && !Variables.HasReference(e)
            && !e.GetString()!.Equals("any", StringComparison.OrdinalIgnoreCase) && !TryEnum(e.GetString()!, out BuildResultCode _))
            yield return $"Unknown result '{e.GetString()}' (a BuildResultCode or 'any').";
        foreach ((string name, int min, int max) in new[]
        {
            ("cellX", 0, BuildGrid.CellsX - 1), ("cellZ", 0, BuildGrid.CellsZ - 1), ("level", 0, BuildGrid.Levels - 1), ("rotation", 0, 3),
            ("edit", 0, BuildEdit.Mask), ("editRotation", 0, 3), ("rawState", 0, ushort.MaxValue), ("count", 1, BuildEditCommand.MaxEdits),
        })
        {
            if (!s.Params.TryGetValue(name, out JsonElement v) || Variables.HasReference(v)) continue;
            if (!Comparison.TryNumber(v, out double d) || d < min || d > max || d != Math.Floor(d)) yield return $"'{name}' must be an integer {min}-{max}.";
        }
    }

    // 기능: 조각을 플레이어처럼 편집한다(Phase 13.5 D13). 대상: pieceId(예: 직전 build의 ${wall.pieceId}) 또는 칸(cellX, level,
    //   cellZ, piece, rotation)으로 이 Actor의 Client가 아는 조각. 상태: preset/edit(+editRotation, 기본은 조각의 지금 회전) 또는
    //   rawState(그대로, 거절 시험용). count번 보낸다. burst는 간격 없이 한 번에(서버 큐가 차면 RateLimited), duplicate는 직전
    //   순번을 다시 써서 보낸다(서버가 답 없이 버린다: 결과를 기다리지 않는다). alternate는 짝수 번째를 Reset(Edit 0)으로 바꿔
    //   요청마다 상태가 실제로 바뀌게 한다(같은 상태 반복은 간격을 쓰지 않아 큐가 차지 않는다). 도구는 바꾸지 않는다(§29).
    // 입력: ctx - 단계 문맥, token - 취소.
    // 출력: 모든 요청이 결과를 받고 각 코드가 expect(기본 Ok, "any"는 모두)면 통과. saveAs: { sent, codes[], counts{code: n},
    //   pieceId }.
    private static async Task<StepOutcome> BuildEditAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        if (RequireJoined(actor) is { } notJoined) return notJoined;
        uint pieceId = 0;
        BuildPieceShape? slot = null;
        if (ctx.Double("pieceId") is double id)
        {
            if (id < 0 || id > uint.MaxValue || id != Math.Floor(id)) throw new QaStepException($"'pieceId' must be a piece id (got {id}).");
            pieceId = (uint)id;
        }
        else
        {
            string pieceText = ctx.RequireString("piece");
            if (!TryEnum(pieceText, out BuildPieceType piece)) throw new QaStepException($"Unknown piece '{pieceText}'.");
            int x = ctx.Int("cellX", 0, BuildGrid.CellsX - 1)!.Value;
            int y = ctx.Int("level", 0, BuildGrid.Levels - 1) ?? throw new QaStepException("'level' is required with 'cellX'.");
            int z = ctx.Int("cellZ", 0, BuildGrid.CellsZ - 1) ?? throw new QaStepException("'cellZ' is required with 'cellX'.");
            if (!BuildGrid.TryNormalize(piece, x, y, z, ctx.Int("rotation", 0, 3) ?? 0, out BuildPieceShape shape))
                throw new QaStepException($"Cell ({x}, {y}, {z}) is off the build grid.");
            slot = shape;
        }
        int raw = ctx.Int("rawState", 0, ushort.MaxValue) ?? -1;
        int edit = 0;
        if (raw < 0)
        {
            if (ctx.String("preset") is string preset)
            {
                if (!s_editPresets.TryGetValue(preset, out edit)) throw new QaStepException($"Unknown preset '{preset}'.");
            }
            else
            {
                edit = ctx.Int("edit", 0, BuildEdit.Mask) ?? throw new QaStepException("Give 'edit', 'preset' or 'rawState'.");
            }
        }
        int rotation = ctx.Int("editRotation", 0, 3) ?? -1;
        int count = ctx.Int("count", 1, BuildEditCommand.MaxEdits) ?? 1;
        bool burst = ctx.Bool("burst") ?? false;
        bool duplicate = ctx.Bool("duplicate") ?? false;
        string expect = ctx.String("expect") ?? nameof(BuildResultCode.Ok);

        bool alternate = ctx.Bool("alternate") ?? false;
        var plans = Enumerable.Range(0, count)
            .Select(i => alternate && i % 2 == 1 ? new EditPlan(pieceId, slot, 0, rotation, -1) : new EditPlan(pieceId, slot, edit, rotation, raw))
            .ToArray();
        bool done = false;
        try
        {
            var command = new BuildEditCommand(plans, burst || duplicate, duplicate);
            if (!await SendAppliedAsync(ctx, actor, command, token).ConfigureAwait(false))
                return StepOutcome.Fail("The actor did not apply the edit command.");
            if (actor.State.Error is string error && error.StartsWith("Edit command", StringComparison.Ordinal)) return StepOutcome.Fail(error);
            if (duplicate)
            {
                // The server drops a sequence it already processed without an answer: nothing to wait for.
                done = true;
                return StepOutcome.Pass($"{count} duplicate edit request(s) sent", JsonPath.From(new { sent = count, duplicate = true }));
            }
            ushort first = (ushort)actor.State.BuildFirstSequence;
            var expected = new HashSet<int>(Enumerable.Range(0, count).Select(i => (int)(ushort)(first + i)));
            BuildResultInfo[] Results() => actor.State.BuildResults.Where(r => expected.Contains(r.Sequence)).ToArray();
            done = await ctx.WaitUntilAsync(() => Results().Select(r => r.Sequence).Distinct().Count() == count || !actor.State.Joined, token).ConfigureAwait(false);
            BuildResultInfo[] results = Results();
            var value = new
            {
                sent = count,
                codes = results.Select(r => r.Code).ToArray(),
                counts = results.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.Count()),
                pieceId = results.Where(r => r.Code == nameof(BuildResultCode.Ok)).Select(r => (uint?)r.PieceId).FirstOrDefault(),
            };
            string actual = string.Join(", ", value.codes);
            if (!done || results.Length < count)
                return StepOutcome.Fail($"{results.Length}/{count} edit results within {ctx.TimeoutMs} ms ({actual}).", $"{count} results", $"{results.Length}: {actual}");
            bool ok = string.Equals(expect, "any", StringComparison.OrdinalIgnoreCase) || results.All(r => string.Equals(r.Code, expect, StringComparison.OrdinalIgnoreCase));
            return ok
                ? StepOutcome.Pass($"edit x{count}: {actual}", JsonPath.From(value))
                : StepOutcome.Fail($"Edit answered: {actual}", $"all {expect}", actual);
        }
        finally
        {
            if (!done) await TrySendAsync(actor, new ClearInputQueueCommand()).ConfigureAwait(false);
        }
    }

    // 기능: 이름 문자열을 enum 값으로 파싱한다(대소문자 무시, 숫자 문자열과 정의되지 않은 값은 거부).
    // 입력: text - enum 이름, value - 파싱된 값(out).
    // 출력: 정의된 이름이면 true와 값, 아니면 false.
    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(text, out _);

    // Places pieces the way a player does (BotBuilder): build mode, then per piece aim a tick and send the request after
    // the input that carries the aim. count/dx/dz: a row of pieces, cell (x + i*dx, z + i*dz) (turbo building: one
    // request every two ticks). Passes when every request got its BuildResult and each code is `expect` (default Ok,
    // "any" accepts all). saveAs: { sent, accepted, pieceId (first accepted), pieceIds[], codes[] }.
    // 기능: 플레이어처럼 건설 모드에서 조각을 count개 놓는 BuildRequest를 보내고 모든 BuildResult를 기다린다.
    // 입력: ctx - 단계 문맥(piece, material, cellX·level·cellZ 또는 position, rotation, count, dx, dz, expect), token - 취소 토큰.
    // 출력: 모든 결과가 오고 코드가 expect(기본 Ok, "any"는 모두)면 Pass(saveAs: sent, accepted, pieceId, pieceIds, codes), 아니면 Fail. 중단 시 입력 큐를 비운다.
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

    // 기능: 단계 안의 준비 대기. 조건이 성립할 때까지 최대 ReadyWaitMs(단계 Timeout 이내) 검사한다.
    // 입력: ctx - 단계 문맥, condition - 검사할 조건, token - 취소 토큰.
    // 출력: 조건이 성립하면 true, 짧은 한도나 단계 Timeout이 지나면 false.
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

    // 기능: 명령을 보내고 Actor가 그 명령을 적용한 상태를 발행할 때까지 단계 Timeout 안에서 기다린다.
    // 입력: ctx - 단계 문맥, actor - 대상 Actor, command - 보낼 명령, token - 취소 토큰.
    // 출력: 적용 상태가 발행되면 true, Timeout이면 false.
    // Sends and waits (bounded by the step) until the actor has applied it and published a state after it.
    private static async Task<bool> SendAppliedAsync(StepContext ctx, IQaActor actor, ActorCommand command, CancellationToken token)
    {
        await actor.SendAsync(command, token).ConfigureAwait(false);
        return await ctx.WaitUntilAsync(() => actor.State.LastCommandId >= command.Id, token).ConfigureAwait(false);
    }

    // 기능: 정리용 명령을 1초 한도로 보내고 실패(Pump 종료·가득 참)는 무시한다.
    // 입력: actor - 대상 Actor, command - 보낼 명령.
    // 출력: 반환값 없음. 보내지면 Actor 의도가 정리된다.
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
