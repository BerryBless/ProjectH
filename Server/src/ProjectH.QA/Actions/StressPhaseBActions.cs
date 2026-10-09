using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress phase B (request §18-49): the actions the remaining scenarios need beyond phase A's.
//  - spawnBuildPieces: many pieces through the existing QA command (spawnBuildPiece, one /qa/command per piece, at most
//    BulkParallel in flight), in a layout from BuildLayouts (field = piece count; hanging = one structure on a single
//    foundation). Arrange; no new server path (§24-26, §122).
//  - groupFireAt: members aim at a point and fire real shots (the destruction of a foundation while measure runs).
//  - groupConnect: members connect at a rate (join ramp) or all at once (join spike) as a background workload.
//  - groupInvalidPackets / groupBuildSpam (D38): abusive members on live connections, with a rejoin workload.
public static partial class StressActions
{
    public const int BulkParallel = 16;               // the server runs 16 QA commands per tick; its queue holds 64
    public const int MaxBulkPieces = 50_000;          // more than the grid holds (49,152) and the match budget (20,000)
    public const int MaxFirePresses = 500;
    public const float MaxAbuseRate = 200f;            // per member per second; RateBudget caps one tick at 20
    public const float MaxConnectRate = 100f;

    // 기능: Phase B Stress 액션(spawnBuildPieces, groupFireAt, groupConnect, groupInvalidPackets, groupBuildSpam)과 matchLoop을 Registry에 등록한다.
    // 입력: r - 등록 대상 Registry.
    // 출력: 반환값 없음. Registry에 Phase B Handler가 추가된다.
    private static void RegisterPhaseB(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "spawnBuildPieces",
            Required = new[] { "count" },
            Optional = new[] { "layout", "center", "side", "material", "parallel" },
            PositionParams = new[] { "center" },
            ServerCommand = true,
            // 16 per tick at 30 Hz is 480 pieces/s at best; leave room for a slower loop (100 /s) plus the step's own wait.
            DefaultTimeout = s => (int)Math.Min(int.MaxValue - 10_000, Seconds(s, "count", MaxBulkPieces) * 10 + 30_000),
            Check = s => CheckChoice(s, "layout", new[] { "field", "hanging" }).Concat(CheckCount(s)),
        }, SpawnBuildPiecesAsync));
        r.Add(Group("groupFireAt", GroupFireAtAsync, new[] { "presses", "burst", "slot" }, positions: new[] { "at" }, required: new[] { "at" }));
        r.Add(Group("groupConnect", GroupConnectAsync, new[] { "perSecond", "timeoutMs" }));
        r.Add(Group("groupInvalidPackets", GroupInvalidPacketsAsync, new[] { "ratePerSecond", "kinds", "rejoin" }, check: CheckInvalidKinds));
        r.Add(Group("groupBuildSpam", GroupBuildSpamAsync, new[] { "ratePerSecond", "material", "resources", "rejoin" }));
        RegisterMatchLoop(r);
    }

    // 기능: count Literal이 1-MaxBulkPieces의 정수인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckCount(StepDefinition s)
    {
        if (s.Params.TryGetValue("count", out JsonElement v) && !Variables.HasReference(v) && (!Comparison.TryNumber(v, out double d) || d < 1 || d > MaxBulkPieces || d != Math.Floor(d)))
            yield return $"'count' must be a whole number 1-{MaxBulkPieces}.";
    }

    private static readonly string[] AbuseKinds = { "unknownId", "truncated", "garbage", "oversized" };

    // 기능: kinds Literal이 AbuseKinds(unknownId, truncated, garbage, oversized)만 담은 비어 있지 않은 배열인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckInvalidKinds(StepDefinition s)
    {
        if (!s.Params.TryGetValue("kinds", out JsonElement k) || Variables.HasReference(k)) yield break;
        if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() == 0) yield return $"'kinds' must be a list of {string.Join(", ", AbuseKinds)}.";
        else foreach (JsonElement e in k.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || !AbuseKinds.Contains(e.GetString()!, StringComparer.OrdinalIgnoreCase))
                yield return $"Unknown kind {e.GetRawText()} ({string.Join(", ", AbuseKinds)}; inputFlood is the per-peer input rate, not a steady stream).";
        }
    }

    // ---- spawnBuildPieces ----

    // 기능: BuildLayouts의 배치(field 또는 hanging)대로 조각을 count개까지 spawnBuildPiece 명령으로 최대 parallel개씩 동시에 놓는다(거절된 조각은 재시도하지 않고 다음 Wave가 메운다). 끝나면 서버 조각 수를 읽는다.
    // 입력: ctx - 단계 문맥(count, layout, center, side, material, parallel), token - 취소 토큰.
    // 출력: Pass(saveAs: requested, placed, serverPieces, budgetFull, refused, errors, layout, seconds, perSecond, structure), 명령 오류·hanging 구조 불완전·단계 Timeout이면 Fail. hanging 자리가 없으면 Fail, material·layout이 틀리면 QaStepException.
    private static async Task<StepOutcome> SpawnBuildPiecesAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        int count = ctx.Int("count", 1, MaxBulkPieces) ?? throw new QaStepException("'count' is required.");
        string layout = (ctx.String("layout") ?? "field").ToLowerInvariant();
        Vector2 center = Center(ctx);
        string material = (ctx.String("material") ?? "wood").ToLowerInvariant();
        if (material is not ("wood" or "stone" or "metal")) throw new QaStepException("'material' must be wood, stone or metal.");
        int parallel = ctx.Int("parallel", 1, BulkParallel) ?? BulkParallel;
        IEnumerable<IReadOnlyList<PieceSpec>> waves;
        BuildLayouts.HangingStructure? hanging = null;
        if (layout == "hanging")
        {
            hanging = BuildLayouts.Hanging(center, count, ctx.Int("side", 3, 10));
            if (hanging == null) return StepOutcome.Fail($"No place near ({center.X}, {center.Y}) for a structure of {count} pieces hanging on one foundation (open, clear ground).");
            waves = hanging.Waves;
        }
        else if (layout == "field") waves = BuildLayouts.Field(center);
        else throw new QaStepException("'layout' must be field or hanging.");

        var clock = Stopwatch.StartNew();
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<string>();
        long placed = 0;
        bool budgetFull = false;
        uint foundationId = 0;
        string? hardError = null;
        using var gate = new SemaphoreSlim(parallel);
        foreach (IReadOnlyList<PieceSpec> wave in waves)
        {
            if (budgetFull || hardError != null || Interlocked.Read(ref placed) >= count) break;
            if (ctx.TimedOut) break;
            // Only as many of this wave as are still needed: a refused piece (occupied, unsupported) is not retried; the
            // next waves (or columns) make up for it in the field.
            int need = (int)(count - Interlocked.Read(ref placed));
            IEnumerable<PieceSpec> take = layout == "hanging" ? wave : wave.Take(need);
            Task[] tasks = take.Select(async p =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (budgetFull || Interlocked.Read(ref placed) >= count) return;
                    (bool ok, uint id, string? code, string? error) = await SpawnPieceAsync(run, p, material, token).ConfigureAwait(false);
                    if (ok)
                    {
                        Interlocked.Increment(ref placed);
                        if (hanging != null && p == hanging.Foundation) foundationId = id;
                        return;
                    }
                    lock (refused)
                    {
                        string key = code ?? "error";
                        refused[key] = refused.GetValueOrDefault(key) + 1;
                        if (errors.Count < 5) errors.Add($"{p.Piece} ({p.X},{p.Y},{p.Z} r{p.Rotation}): {error}");
                        if (code == "BudgetFull") budgetFull = true;
                        else if (code == null) hardError ??= error;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        double seconds = clock.Elapsed.TotalSeconds;
        int serverPieces = -1;
        try
        {
            JsonElement m = await MetricsAsync(run.Server, null, token).ConfigureAwait(false);
            if (JsonPath.Get(m, "buildPieces") is JsonElement b && Comparison.TryNumber(b, out double n)) serverPieces = (int)n;
        }
        catch (QaApiException)
        {
        }
        object? structure = hanging == null ? null : new
        {
            pieceId = (long)foundationId,
            foundation = new { x = hanging.Aim.X, y = hanging.Aim.Y, z = hanging.Aim.Z },
            shooter = new { x = hanging.Shooter.X, y = hanging.Shooter.Y, z = hanging.Shooter.Z },
            side = hanging.Side, firstLevel = hanging.FirstLevel,
        };
        var value = JsonPath.From(new
        {
            requested = count, placed = Interlocked.Read(ref placed), serverPieces, budgetFull, refused, errors, layout,
            seconds = Math.Round(seconds, 1), perSecond = seconds > 0 ? Math.Round(Interlocked.Read(ref placed) / seconds, 1) : 0, structure,
        });
        string text = $"{Interlocked.Read(ref placed)} of {count} pieces placed ({layout}) in {seconds:0.0} s, server has {serverPieces}"
                      + (refused.Count > 0 ? $"; refused {string.Join(", ", refused.Select(p => $"{p.Key} {p.Value}"))}" : string.Empty)
                      + (budgetFull ? " — the match budget is full" : string.Empty);
        if (hardError != null) return StepOutcome.Fail($"spawnBuildPiece failed: {hardError}", "pieces placed", text, value);
        if (hanging != null && Interlocked.Read(ref placed) != hanging.Count)
            return StepOutcome.Fail($"The hanging structure is incomplete: {text}; first errors: {string.Join("; ", errors)}", $"{hanging.Count} placed", $"{Interlocked.Read(ref placed)}", value);
        if (ctx.TimedOut) return StepOutcome.Fail($"Timed out: {text}", $"{count} pieces", $"{Interlocked.Read(ref placed)}", value);
        return StepOutcome.Pass(text, value);
    }

    // One spawnBuildPiece (503/504 retried twice). Returns the piece id, or the server's refusal code (BudgetFull,
    // Occupied, Unsupported from "The piece was refused: X.") or null for any other error.
    // 기능: spawnBuildPiece 명령 하나를 보낸다(503·504는 PollIntervalMs 간격으로 최대 MaxRetries 재시도).
    // 입력: run - Run, p - 조각 사양(종류, 칸, 회전), material - 재료 이름, token - 취소 토큰.
    // 출력: 성공이면 (true, 조각 id, null, null), 서버가 409로 거절하면 (false, 0, 거절 코드, 메시지), 그 외 오류·QA API 예외면 (false, 0, null, 메시지).
    private static async Task<(bool Ok, uint Id, string? Code, string? Error)> SpawnPieceAsync(RunContext run, PieceSpec p, string material, CancellationToken token)
    {
        JsonElement args = JsonPath.From(new
        {
            piece = p.Piece.ToString().ToLowerInvariant(), material, cellX = p.X, level = p.Y, cellZ = p.Z, rotation = p.Rotation,
        });
        for (int attempt = 0; ; attempt++)
        {
            CommandResponse response;
            try
            {
                response = await run.Server.CommandAsync("spawnBuildPiece", null, args, run.RunId, token).ConfigureAwait(false);
            }
            catch (QaApiException e)
            {
                return (false, 0, null, e.Message);
            }
            if (response.Ok)
            {
                uint id = response.Result is JsonElement r && JsonPath.Child(r, "pieceId") is JsonElement idJson && Comparison.TryNumber(idJson, out double d) ? (uint)d : 0;
                return (true, id, null, null);
            }
            if (response.StatusCode is 503 or 504 && attempt < MaxRetries)
            {
                await Task.Delay(run.PollIntervalMs, token).ConfigureAwait(false);
                continue;
            }
            string error = response.Error ?? $"HTTP {response.StatusCode}";
            const string prefix = "The piece was refused: ";
            if (response.StatusCode == 409 && error.StartsWith(prefix, StringComparison.Ordinal)) return (false, 0, error[prefix.Length..].TrimEnd('.'), error);
            return (false, 0, null, $"{error} (HTTP {response.StatusCode})");
        }
    }

    // ---- groupFireAt ----

    // 기능: 그룹 구성원에게 지점(y가 없으면 지형 높이 + 1.2 m)을 조준해 slot 무기로 presses번 쏘는 FireAt Brain을 준다.
    // 입력: ctx - 단계 문맥(group, at, presses, burst, slot), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, 아니면 Fail. at이 없으면 QaStepException.
    private static async Task<StepOutcome> GroupFireAtAsync(StepContext ctx, CancellationToken token)
    {
        ActorGroup group = GroupOf(ctx);
        QaPosition at = ctx.Position("at") ?? throw new QaStepException("'at' is required (a position, with y for a point above the ground).");
        Vector3 point = new(at.X, at.Y ?? GameMap.Terrain.Height(at.X, at.Z) + 1.2f, at.Z);
        int presses = ctx.Int("presses", 1, MaxFirePresses) ?? 10;
        int burst = ctx.Int("burst", 1, 30) ?? 3;
        int slot = ctx.Int("slot", 0, 2) ?? 0;
        bool applied = await SetBrainsAsync(ctx, group, (_, _) => new FireAtBrain(new FireAtPlan(point, presses, burst, slot)), token).ConfigureAwait(false);
        group.Role = "fireAt";
        return applied
            ? StepOutcome.Pass($"{group.Members.Count} firing {presses} presses at ({point.X:0.##}, {point.Y:0.##}, {point.Z:0.##})")
            : StepOutcome.Fail($"Not every member of '{group.Name}' took the fire-at behaviour.");
    }

    // ---- groupConnect (join ramp / spike) ----

    // §33-34: the members connect at perSecond (0 = all at once) in member order, as a background workload, so measure
    // can run during the ramp. Each one counts as joined (first snapshot) or failed (the connection closed, or no
    // snapshot within timeoutMs). The step returns at once; `waitFor server.activeSessions` or stopGroup waits.
    // 기능: 그룹의 이전 connect 작업을 멈추고 구성원을 perSecond 속도(0이면 한꺼번에)로 접속시키는 Background 작업을 시작한다(§33-34).
    // 입력: ctx - 단계 문맥(group, perSecond, timeoutMs), token - 취소 토큰.
    // 출력: 작업이 시작되면 바로 Pass. 속도가 범위 밖이거나 이미 연결 중인 구성원이 있으면 QaStepException.
    private static async Task<StepOutcome> GroupConnectAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        double rate = ctx.Double("perSecond") ?? 0;
        if (rate < 0 || rate > MaxConnectRate) throw new QaStepException($"'perSecond' must be 0 (all at once) to {MaxConnectRate}.");
        int timeoutMs = ctx.Int("timeoutMs", 1000, 120_000) ?? 15_000;
        IQaActor? busy = group.Members.FirstOrDefault(m => m.State.Status is ActorStatus.Joined or ActorStatus.Connecting);
        if (busy != null) throw new QaStepException($"{busy.Alias} is already connected or connecting: groupConnect is for members not in the game.");
        // Targets on the run flow (a proxied member gets its fresh proxy here; the workload never touches RunContext).
        var targets = new (string Host, int Port)[group.Members.Count];
        for (int i = 0; i < targets.Length; i++) targets[i] = await run.ConnectTargetAsync(group.Members[i].Alias).ConfigureAwait(false);
        await run.Groups.StopAsync(group.Name, GroupRegistry.StopTimeout, kind: "connect").ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        Task task = Task.Run(() => ConnectLoopAsync(group, targets, rate, timeoutMs, cts.Token));
        run.Groups.Start(new GroupWorkload(group.Name, "connect", cts, task));
        string pace = rate > 0 ? $"{rate}/s (about {group.Members.Count / rate:0.#} s)" : "all at once";
        return StepOutcome.Pass($"{group.Members.Count} connecting {pace}");
    }

    // 기능: 접속 작업 본체. 구성원을 순서대로 rate에 맞춰 접속시키고, 각각 Join(첫 Snapshot)과 실패(연결 닫힘 또는 timeoutMs 초과)를 통계에 센다.
    // 입력: group - 대상 그룹, targets - 구성원별 (host, port), rate - 초당 접속 수(0 이하면 한꺼번에), timeoutMs - 구성원별 Join 한도(ms), token - 중단 토큰.
    // 출력: 반환값 없음. 그룹 통계(Connects, ConnectMs, ConnectFailures)가 갱신되고, 중단 외의 예외로 끝나면 WorkloadFailed가 기록된다.
    internal static async Task ConnectLoopAsync(ActorGroup group, (string Host, int Port)[] targets, double rate, int timeoutMs, CancellationToken token)
    {
        GroupStats stats = group.Stats;
        int n = group.Members.Count;
        var sentAt = new long[n];
        var done = new bool[n];
        var before = group.Members.Select(m => m.State.Connections).ToArray();
        var clock = Stopwatch.StartNew();
        int sent = 0, finished = 0;
        try
        {
            while (finished < n)
            {
                // Send every member whose turn has come (rate 0: all in the first pass).
                while (sent < n && (rate <= 0 || clock.Elapsed.TotalSeconds >= sent / rate))
                {
                    await group.Members[sent].SendAsync(new ConnectCommand(targets[sent].Host, targets[sent].Port, false), token).ConfigureAwait(false);
                    sentAt[sent] = Math.Max(1, clock.ElapsedMilliseconds);
                    sent++;
                }
                for (int i = 0; i < sent; i++)
                {
                    if (done[i]) continue;
                    ActorState s = group.Members[i].State;
                    long ms = clock.ElapsedMilliseconds - sentAt[i];
                    if (s.Connections > before[i] && s.Joined && s.HasSnapshot)
                    {
                        Interlocked.Increment(ref stats.Connects);
                        Interlocked.Add(ref stats.ConnectMsTotal, ms);
                        stats.Max(ref stats.ConnectMsMax, ms);
                    }
                    else if ((s.Connections > before[i] && s.Disconnected) || ms > timeoutMs)
                    {
                        Interlocked.Increment(ref stats.ConnectFailures);
                        stats.Error($"{group.Members[i].Alias} did not join within {ms} ms ({FlowActions.Describe(s)})");
                    }
                    else continue;
                    done[i] = true;
                    finished++;
                }
                if (finished < n) await Task.Delay(StepContext.ActorPollMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            stats.WorkloadFailed($"connect stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    // ---- abuse groups (D38 groupInvalidPackets, groupBuildSpam) ----

    // 기능: 그룹 구성원에게 초당 ratePerSecond개의 잘못된 패킷(kinds)을 보내는 Brain을 주고, rejoin이면 Kick 뒤 재접속 작업을 시작한다(D38).
    // 입력: ctx - 단계 문맥(group, ratePerSecond, kinds, rejoin), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, 아니면 Fail. 속도가 범위 밖·모르는 종류·Proxy 구성원이면 QaStepException.
    private static async Task<StepOutcome> GroupInvalidPacketsAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        float rate = (float)(ctx.Double("ratePerSecond") ?? 5);
        if (!(rate > 0) || rate > MaxAbuseRate) throw new QaStepException($"'ratePerSecond' must be above 0 and at most {MaxAbuseRate}.");
        string[] kinds = new[] { "unknownId", "truncated", "garbage" };
        if (ctx.Param("kinds") is JsonElement k)
        {
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() == 0) throw new QaStepException("'kinds' must be a non-empty list.");
            kinds = k.EnumerateArray().Select(e => AbuseKinds.FirstOrDefault(x => string.Equals(x, QaJson.Text(e), StringComparison.OrdinalIgnoreCase))
                ?? throw new QaStepException($"Unknown kind {e.GetRawText()} ({string.Join(", ", AbuseKinds)}).")).ToArray();
        }
        int seed = run.Seed;
        bool applied = await SetBrainsAsync(ctx, group, (i, _) => new InvalidPacketBrain(new InvalidPacketPlan(kinds, rate, group.Indices[i], seed, group.Stats)), token).ConfigureAwait(false);
        group.Role = "invalidPackets";
        if (!applied) return StepOutcome.Fail($"Not every member of '{group.Name}' took the invalid-packet behaviour.");
        await StartRejoinAsync(ctx, group).ConfigureAwait(false);
        return StepOutcome.Pass($"{group.Members.Count} sending {rate}/s invalid packets each ({string.Join(", ", kinds)}){(ctx.Bool("rejoin") ?? true ? ", rejoining after a kick" : "")}");
    }

    // 기능: 그룹 구성원에게 자원을 Arrange한 뒤 초당 ratePerSecond개의 건설 요청을 보내는 Brain을 주고, rejoin이면 Kick 뒤 재접속 작업을 시작한다(D38).
    // 입력: ctx - 단계 문맥(group, ratePerSecond, material, resources, rejoin), token - 취소 토큰.
    // 출력: 모두 적용되면 Pass, giveResource 거절·Brain 미적용이면 Fail. 속도 범위 밖·모르는 재료·Proxy 구성원이면 QaStepException.
    private static async Task<StepOutcome> GroupBuildSpamAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        ActorGroup group = GroupOf(ctx);
        float rate = (float)(ctx.Double("ratePerSecond") ?? 15);
        if (!(rate > 0) || rate > MaxAbuseRate) throw new QaStepException($"'ratePerSecond' must be above 0 and at most {MaxAbuseRate}.");
        BuildMaterialType material = Material(ctx);
        int resources = ctx.Int("resources", 0, 500) ?? 500;
        if (resources > 0)
        {
            var commands = group.Members.Select(a => ("giveResource", (string?)a.DevPlayerId, (object)new { material = material.ToString().ToLowerInvariant(), amount = resources })).ToList();
            int failed = await CommandsAsync(run, group.Stats, commands, token).ConfigureAwait(false);
            if (failed > 0) return StepOutcome.Fail($"{failed} giveResource commands for '{group.Name}' were refused.");
        }
        int seed = run.Seed;
        bool applied = await SetBrainsAsync(ctx, group, (i, _) => new BuildSpamBrain(new BuildSpamPlan(rate, material, group.Indices[i], seed, group.Stats)), token).ConfigureAwait(false);
        group.Role = "buildSpam";
        if (!applied) return StepOutcome.Fail($"Not every member of '{group.Name}' took the build-spam behaviour.");
        await StartRejoinAsync(ctx, group).ConfigureAwait(false);
        return StepOutcome.Pass($"{group.Members.Count} spamming {rate} build requests/s each{(ctx.Bool("rejoin") ?? true ? ", rejoining after a kick" : "")}");
    }

    // A kicked abuser comes back (a real attacker reconnects): the rejoin workload, unless rejoin is false. A group's
    // previous rejoin workload ends first (one per group).
    // 기능: 그룹의 이전 rejoin 작업을 멈추고, rejoin이 false가 아니면 Kick된 구성원을 다시 접속시키는 Background 작업을 시작한다.
    // 입력: ctx - 단계 문맥(rejoin), group - 대상 그룹.
    // 출력: 반환값 없음. Run의 그룹 작업 목록에 rejoin 작업이 등록된다. Proxy 구성원이 있으면 QaStepException.
    private static async Task StartRejoinAsync(StepContext ctx, ActorGroup group)
    {
        RunContext run = ctx.Run;
        await run.Groups.StopAsync(group.Name, GroupRegistry.StopTimeout, kind: "rejoin").ConfigureAwait(false);
        if (!(ctx.Bool("rejoin") ?? true)) return;
        IQaActor? proxied = group.Members.FirstOrDefault(m => run.Network.IsProxied(m.Alias));
        if (proxied != null) throw new QaStepException($"rejoin does not reconnect proxied actors ({proxied.Alias}).");
        var cts = new CancellationTokenSource();
        string host = run.GameHost;
        int port = run.GamePort;
        Task task = Task.Run(() => RejoinLoopAsync(group, host, port, cts.Token));
        run.Groups.Start(new GroupWorkload(group.Name, "rejoin", cts, task));
    }

    public static readonly TimeSpan RejoinDelay = TimeSpan.FromSeconds(1);

    // Polls the members' published state every 250 ms: a member whose connection the server closed (Kicked or any other
    // close) is counted (kicks) and, RejoinDelay later, connected again as a new player (rejoins). At most one rejoin per
    // member per RejoinDelay; no server query.
    // 기능: 재접속 작업 본체. 250 ms마다 구성원 상태를 보고 서버가 닫은 연결을 세며(Kicked는 kicks), RejoinDelay 뒤 새 플레이어로 다시 접속시킨다.
    // 입력: group - 대상 그룹, host - 서버 호스트, port - 서버 포트, token - 중단 토큰.
    // 출력: 반환값 없음. 그룹 통계(Kicks, Rejoins)가 갱신되고, 중단 외의 예외로 끝나면 WorkloadFailed가 기록된다.
    internal static async Task RejoinLoopAsync(ActorGroup group, string host, int port, CancellationToken token)
    {
        GroupStats stats = group.Stats;
        int n = group.Members.Count;
        var seenAt = new long[n];
        var handled = new int[n];
        var clock = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                for (int i = 0; i < n; i++)
                {
                    ActorState s = group.Members[i].State;
                    if (!s.Disconnected || s.Connections == 0 || handled[i] == s.Connections) continue;
                    if (seenAt[i] == 0)
                    {
                        seenAt[i] = Math.Max(1, clock.ElapsedMilliseconds);
                        if (s.DisconnectCode == "Kicked") Interlocked.Increment(ref stats.Kicks);
                        continue;
                    }
                    if (clock.ElapsedMilliseconds - seenAt[i] < RejoinDelay.TotalMilliseconds) continue;
                    handled[i] = s.Connections;
                    seenAt[i] = 0;
                    await group.Members[i].SendAsync(new ConnectCommand(host, port, false), token).ConfigureAwait(false);
                    Interlocked.Increment(ref stats.Rejoins);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            stats.WorkloadFailed($"rejoin stopped: {e.GetType().Name}: {e.Message}");
        }
    }
}
