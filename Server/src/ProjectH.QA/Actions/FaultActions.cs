using System.Text.Json;
using ProjectH.QA.Faults;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// QA-3 fault injection (D13, D14, request §70-78, §127-129): network faults through the actor's proxy, server process
// stop/start/kill, DB container stop/start, invalid packets. Faults the run sets are always undone in cleanup
// (QaOrchestrator: proxies closed, stopped DB containers started again, the server stopped).
public static class FaultActions
{
    public const string DefaultContainer = DockerDbController.DefaultContainerName;
    public const int MaxInvalidPerKind = 500;

    public static readonly string[] InvalidKinds = { "unknownId", "truncated", "oversized", "garbage", "inputFlood" };
    private static readonly string[] s_directions = { "both", "toServer", "toClient" };

    // 기능: 장애 주입 액션(네트워크 장애·차단·연결 끊김, 잘못된 패킷, 서버 프로세스, DB 컨테이너)을 Registry에 등록한다.
    // 입력: r - 등록 대상 Registry.
    // 출력: 반환값 없음. Registry에 장애 액션 Handler가 추가된다.
    public static void Register(ActionRegistry r)
    {
        // ---- network (actor must have a proxy) ----
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "networkFault",
            Actor = ActorUse.Required,
            NeedsProxy = true,
            Optional = new[] { "latencyMs", "jitterMs", "lossPercent", "duplicatePercent", "direction" },
            Check = CheckNetworkFault,
        }, NetworkFaultAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "clearNetworkFault", Actor = ActorUse.Required, NeedsProxy = true }, ClearNetworkFaultAsync));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "blockNetwork", Actor = ActorUse.Required, NeedsProxy = true, Optional = new[] { "direction" }, Check = CheckDirection,
        }, (ctx, t) => BlockAsync(ctx, true)));
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "unblockNetwork", Actor = ActorUse.Required, NeedsProxy = true, Optional = new[] { "direction" }, Check = CheckDirection,
        }, (ctx, t) => BlockAsync(ctx, false)));
        r.Add(new DelegateAction(new ActionSpec { Name = "dropConnection", Actor = ActorUse.Required }, DropConnectionAsync));

        // ---- invalid packets (D14) ----
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "sendInvalidPackets",
            Actor = ActorUse.Required,
            Required = new[] { "kinds" },
            Optional = new[] { "count" },
            Check = CheckInvalid,
        }, SendInvalidAsync));

        // ---- server process (launch mode only) ----
        r.Add(new DelegateAction(new ActionSpec { Name = "stopServer", LaunchOnly = true, DefaultTimeout = _ => 30_000 }, StopServerAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "killServer", LaunchOnly = true, DefaultTimeout = _ => 15_000 }, KillServerAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "startServer", LaunchOnly = true, DefaultTimeout = _ => 30_000 }, StartServerAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "restartServer", LaunchOnly = true, DefaultTimeout = _ => 60_000 }, RestartServerAsync));

        // ---- database container (Docker; skipped when unavailable) ----
        r.Add(new DelegateAction(new ActionSpec { Name = "stopDb", Optional = new[] { "container" }, Check = CheckContainer, DefaultTimeout = _ => 45_000 }, StopDbAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "startDb", Optional = new[] { "container" }, Check = CheckContainer, DefaultTimeout = _ => 90_000 }, StartDbAsync));
        r.Add(new DelegateAction(new ActionSpec { Name = "waitDbHealthy", Optional = new[] { "container" }, Check = CheckContainer, DefaultTimeout = _ => 60_000 }, WaitDbHealthyAsync));
    }

    // ---- validation of literal values ----

    // 기능: networkFault 단계의 Literal 인자(direction, latencyMs·jitterMs 정수 범위, loss·duplicate 0-100)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckNetworkFault(StepDefinition s)
    {
        foreach (string e in CheckDirection(s)) yield return e;
        foreach (string name in new[] { "latencyMs", "jitterMs" })
        {
            if (s.Params.TryGetValue(name, out JsonElement v) && !Variables.HasReference(v)
                && (!Comparison.TryNumber(v, out double d) || d < 0 || d > NetworkFaultSettings.MaxDelayMs || d != Math.Floor(d)))
                yield return $"'{name}' must be an integer 0-{NetworkFaultSettings.MaxDelayMs}.";
        }
        foreach (string name in new[] { "lossPercent", "duplicatePercent" })
        {
            if (s.Params.TryGetValue(name, out JsonElement v) && !Variables.HasReference(v) && (!Comparison.TryNumber(v, out double d) || d < 0 || d > 100))
                yield return $"'{name}' must be 0-100.";
        }
    }

    // 기능: direction 인자가 both·toServer·toClient 중 하나인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckDirection(StepDefinition s)
    {
        if (s.Params.TryGetValue("direction", out JsonElement v) && !Variables.HasReference(v)
            && (v.ValueKind != JsonValueKind.String || !TryDirection(v.GetString(), out _)))
            yield return $"'direction' must be one of {string.Join(", ", s_directions)}.";
    }

    // 기능: container 인자가 문자열(컨테이너 이름)인지 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckContainer(StepDefinition s)
    {
        if (s.Params.TryGetValue("container", out JsonElement v) && !Variables.HasReference(v) && v.ValueKind != JsonValueKind.String)
            yield return "'container' must be a container name.";
    }

    // 기능: sendInvalidPackets 단계의 Literal 인자(kinds 배열과 종류 이름, count 범위, 종류 x count 총량)를 검사한다.
    // 입력: s - 단계 정의.
    // 출력: 오류 문장들(없으면 빈 목록).
    private static IEnumerable<string> CheckInvalid(StepDefinition s)
    {
        if (s.Params.TryGetValue("kinds", out JsonElement k) && !Variables.HasReference(k))
        {
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() == 0) yield return $"'kinds' must be a non-empty array of {string.Join(", ", InvalidKinds)}.";
            else
            {
                foreach (JsonElement e in k.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String || !InvalidKinds.Contains(e.GetString(), StringComparer.OrdinalIgnoreCase))
                        yield return $"Unknown packet kind {e.GetRawText()} (one of {string.Join(", ", InvalidKinds)}).";
                }
            }
        }
        double perKind = 1;
        if (s.Params.TryGetValue("count", out JsonElement c) && !Variables.HasReference(c)
            && (!Comparison.TryNumber(c, out perKind) || perKind < 1 || perKind > MaxInvalidPerKind || perKind != Math.Floor(perKind)))
            yield return $"'count' must be an integer 1-{MaxInvalidPerKind}.";
        else if (s.Params.TryGetValue("kinds", out JsonElement lk) && lk.ValueKind == JsonValueKind.Array && !Variables.HasReference(lk)
            && (!s.Params.TryGetValue("count", out JsonElement lc) || !Variables.HasReference(lc))
            && lk.GetArrayLength() * perKind > SendRawCommand.MaxRawPackets)
            yield return $"{lk.GetArrayLength()} kinds x {perKind} = {lk.GetArrayLength() * perKind} packets: more than {SendRawCommand.MaxRawPackets} in one step.";
    }

    // 기능: 방향 이름(both, toServer, toClient)을 FaultDirection으로 파싱한다(대소문자 무시).
    // 입력: text - 방향 이름, direction - 파싱된 방향(out, 실패 시 Both).
    // 출력: 아는 이름이면 true와 방향, 아니면 false.
    internal static bool TryDirection(string? text, out FaultDirection direction)
    {
        direction = FaultDirection.Both;
        if (string.Equals(text, "both", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "toServer", StringComparison.OrdinalIgnoreCase)) { direction = FaultDirection.ToServer; return true; }
        if (string.Equals(text, "toClient", StringComparison.OrdinalIgnoreCase)) { direction = FaultDirection.ToClient; return true; }
        return false;
    }

    // 기능: 단계의 direction 인자를 읽는다(없으면 Both).
    // 입력: ctx - 단계 문맥.
    // 출력: FaultDirection. 모르는 이름이면 QaStepException.
    private static FaultDirection Direction(StepContext ctx)
    {
        string? text = ctx.String("direction");
        if (text == null) return FaultDirection.Both;
        return TryDirection(text, out FaultDirection d) ? d : throw new QaStepException($"'direction' must be one of {string.Join(", ", s_directions)}.");
    }

    // ---- network ----

    // 기능: Actor Proxy의 해당 방향에 지연·Jitter·손실·중복을 설정한다(안 준 값은 0, 차단 상태는 유지).
    // 입력: ctx - 단계 문맥(latencyMs, jitterMs, lossPercent, duplicatePercent, direction), token - 취소 토큰(사용 안 함).
    // 출력: Pass(saveAs: Proxy 장애 설명). 값이 범위 밖이면 QaStepException.
    // Sets the direction's latency, jitter, loss and duplication (unset values are 0); a block stays as it was.
    private static Task<StepOutcome> NetworkFaultAsync(StepContext ctx, CancellationToken token)
    {
        string alias = ctx.ActorAlias;
        FaultDirection direction = Direction(ctx);
        var settings = new NetworkFaultSettings
        {
            LatencyMs = ctx.Int("latencyMs", 0, NetworkFaultSettings.MaxDelayMs) ?? 0,
            JitterMs = ctx.Int("jitterMs", 0, NetworkFaultSettings.MaxDelayMs) ?? 0,
            PacketLossPercent = ctx.Double("lossPercent") ?? 0,
            DuplicatePercent = ctx.Double("duplicatePercent") ?? 0,
        };
        try
        {
            if (direction != FaultDirection.ToClient)
                ctx.Run.Network.SetFaults(alias, settings with { Blocked = ctx.Run.Network.GetFaults(alias, FaultDirection.ToServer).Blocked }, FaultDirection.ToServer);
            if (direction != FaultDirection.ToServer)
                ctx.Run.Network.SetFaults(alias, settings with { Blocked = ctx.Run.Network.GetFaults(alias, FaultDirection.ToClient).Blocked }, FaultDirection.ToClient);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new QaStepException(e.Message);
        }
        string text = $"{direction}: latency {settings.LatencyMs} ms, jitter {settings.JitterMs} ms, loss {settings.PacketLossPercent}%, duplicate {settings.DuplicatePercent}%";
        ctx.Run.Log($"network fault {alias} {text}");
        return Task.FromResult(StepOutcome.Pass(text, JsonPath.From(ctx.Run.Network.Describe(alias))));
    }

    // 기능: Actor Proxy의 모든 네트워크 장애를 지운다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰(사용 안 함).
    // 출력: Pass(saveAs: Proxy 장애 설명).
    private static Task<StepOutcome> ClearNetworkFaultAsync(StepContext ctx, CancellationToken token)
    {
        ctx.Run.Network.Clear(ctx.ActorAlias);
        return Task.FromResult(StepOutcome.Pass("cleared", JsonPath.From(ctx.Run.Network.Describe(ctx.ActorAlias))));
    }

    // 기능: Actor Proxy의 해당 방향을 차단하거나 푼다.
    // 입력: ctx - 단계 문맥(direction), blocked - true면 차단, false면 해제.
    // 출력: Pass(saveAs: Proxy 장애 설명).
    private static Task<StepOutcome> BlockAsync(StepContext ctx, bool blocked)
    {
        FaultDirection direction = Direction(ctx);
        ctx.Run.Network.SetBlocked(ctx.ActorAlias, blocked, direction);
        return Task.FromResult(StepOutcome.Pass($"{(blocked ? "blocked" : "unblocked")} {direction}", JsonPath.From(ctx.Run.Network.Describe(ctx.ActorAlias))));
    }

    // Request §74: the network goes away without a word (LiteNetLib stopped, no disconnect packet). The server notices
    // only through its DisconnectTimeoutMs and graces the player like any network loss. Works with or without a proxy.
    // 기능: Disconnect 패킷 없이 Actor의 연결을 끊고 연결이 닫힐 때까지 기다린다(request §74).
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 연결이 닫히면 Pass, Timeout이면 Fail.
    private static async Task<StepOutcome> DropConnectionAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        var command = new DisconnectCommand(Graceful: false);
        await actor.SendAsync(command, token).ConfigureAwait(false);
        bool done = await ctx.WaitUntilAsync(() => actor.State.LastCommandId >= command.Id
            && actor.State.Status is ActorStatus.Disconnected or ActorStatus.Idle, token).ConfigureAwait(false);
        return done ? StepOutcome.Pass("connection dropped without notice") : StepOutcome.Fail("Actor did not drop its connection.", "disconnected", FlowActions.Describe(actor.State));
    }

    // ---- invalid packets ----

    // 기능: 종류마다 count개의 잘못된 패킷을 Run·단계 Seed로 만들어 Actor 연결로 보낸다(D14).
    // 입력: ctx - 단계 문맥(kinds, count), token - 취소 토큰.
    // 출력: 모두 보내지면 Pass(saveAs: sent, total, handedToConnection), 미Join·미적용·일부만 전송이면 Fail. 총량 초과면 QaStepException.
    private static async Task<StepOutcome> SendInvalidAsync(StepContext ctx, CancellationToken token)
    {
        IQaActor actor = ctx.Actor();
        ActorState before = actor.State;
        if (!before.Joined) return StepOutcome.Fail($"{actor.Alias} is not in the game ({FlowActions.Describe(before)}).", "joined", FlowActions.Describe(before));
        int count = ctx.Int("count", 1, MaxInvalidPerKind) ?? 1;
        JsonElement kinds = ctx.Param("kinds") ?? throw new QaStepException("'kinds' is required.");
        if (kinds.ValueKind != JsonValueKind.Array) throw new QaStepException("'kinds' must be an array.");
        // Bound before anything is allocated (kinds and count may come from variables).
        if ((long)kinds.GetArrayLength() * count > SendRawCommand.MaxRawPackets)
            throw new QaStepException($"{kinds.GetArrayLength()} kinds x {count} = {(long)kinds.GetArrayLength() * count} packets: more than {SendRawCommand.MaxRawPackets} in one step.");
        // Seeded per run and step (request §14): the same scenario sends the same bytes.
        var rng = new Random(unchecked(ctx.Run.Seed * 397 + ctx.Step.Index));
        var packets = new List<byte[]>();
        var sent = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement k in kinds.EnumerateArray())
        {
            string kind = InvalidKinds.FirstOrDefault(x => string.Equals(x, k.GetString(), StringComparison.OrdinalIgnoreCase))
                ?? throw new QaStepException($"Unknown packet kind {k.GetRawText()} (one of {string.Join(", ", InvalidKinds)}).");
            for (int i = 0; i < count; i++) packets.Add(InvalidPacket(kind, rng));
            sent[kind] = sent.GetValueOrDefault(kind) + count;
        }
        long rawBefore = before.RawPacketsSent;
        var command = new SendRawCommand(packets);
        await actor.SendAsync(command, token).ConfigureAwait(false);
        bool applied = await ctx.WaitUntilAsync(() => actor.State.LastCommandId >= command.Id, token).ConfigureAwait(false);
        ActorState after = actor.State;
        long handed = after.RawPacketsSent - rawBefore;
        var value = JsonPath.From(new { sent, total = packets.Count, handedToConnection = handed });
        if (!applied) return StepOutcome.Fail("The actor did not take the packets.", $"{packets.Count} packets", "not applied");
        if (handed != packets.Count)
            return StepOutcome.Fail($"Only {handed} of {packets.Count} packets were sent ({after.Error ?? FlowActions.Describe(after)}).", $"{packets.Count}", $"{handed}");
        return StepOutcome.Pass($"{packets.Count} invalid packets ({string.Join(", ", sent.Select(p => $"{p.Key} {p.Value}"))})", value);
    }

    // What the server sees for each kind (Net/NetworkListener): unknownId → UnknownId; truncated, oversized → Malformed
    // (a PlayerInput whose count or length is wrong); garbage → UnknownId or WrongDirection (the first byte is never a
    // client-to-server id); inputFlood → valid-format inputs (Seq 0, so never applied) above the per-peer rate →
    // InputRate for the ones over the limit. Every bad one counts toward the BadPacketDisconnectThreshold kick.
    // 기능: 종류 이름에 맞는 잘못된 패킷 하나를 만든다(리뷰 수정 A1: oversized는 조각 2개 안의 1,600 B).
    // 입력: kind - InvalidKinds 중 하나, rng - 난수.
    // 출력: 보낼 바이트.
    internal static byte[] InvalidPacket(string kind, Random rng)
    {
        switch (kind)
        {
            case "unknownId":
            {
                var b = new byte[1 + rng.Next(0, 9)];
                rng.NextBytes(b);
                b[0] = 0xFF;
                return b;
            }
            case "truncated":
                return new[] { (byte)PacketId.PlayerInput, (byte)3 };
            case "oversized":
            {
                // Larger than any valid packet (MaxPacketSize) and with an impossible input count. Review fix A1: the server
                // refuses it by size before parsing (Malformed). At most 2 fragments at the bots' user MTU (ProtocolLimits.UserMtu,
                // review fix B3), their MaxFragmentsCount (ProtocolLimits.MaxFragments): a larger one could not be sent at all.
                var b = new byte[ProtocolConstants.MaxPacketSize + 400];
                rng.NextBytes(b);
                b[0] = (byte)PacketId.PlayerInput;
                b[1] = 0xFF;
                return b;
            }
            case "garbage":
            {
                var b = new byte[1 + rng.Next(0, 64)];
                rng.NextBytes(b);
                while (IsClientPacket(b[0])) b[0] = (byte)rng.Next(0, 256);
                return b;
            }
            case "inputFlood":
            {
                var buffer = new byte[ProtocolConstants.MaxPacketSize];
                var writer = new PacketWriter(buffer);
                var packet = new PlayerInputPacket { Count = 1 };
                packet.Set(0, new InputCommand { Seq = 0, ViewTick = uint.MaxValue });   // review fix D2: "now"
                PlayerInputPacket.Write(ref writer, packet);
                return writer.WrittenSpan.ToArray();
            }
            default:
                throw new QaStepException($"Unknown packet kind '{kind}'.");
        }
    }

    // 기능: 첫 바이트가 Client가 보낼 수 있는 패킷 id인지 본다(garbage가 그 id로 시작하면 다시 고른다). Phase 15: MapMarker는 버킷이
    //   조용히 버릴 수 있어 잘못된 패킷 수가 흔들리므로 넣었다(BuildEditRequest도 같은 이유로 함께).
    // 입력: id - 첫 바이트.
    // 출력: Client→Server 패킷 id면 true.
    private static bool IsClientPacket(byte id) => id is (byte)PacketId.JoinMatchRequest or (byte)PacketId.PlayerInput
        or (byte)PacketId.StatsRequest or (byte)PacketId.BuildRequest or (byte)PacketId.BuildEditRequest or (byte)PacketId.MapMarker;

    // ---- server process ----

    // 기능: 도구가 띄운 서버의 제어 객체를 가져온다.
    // 입력: ctx - 단계 문맥.
    // 출력: IServerControl. 기존 서버에 붙은 Run이면 QaStepException.
    private static IServerControl Server(StepContext ctx) => ctx.Run.ServerControl
        ?? throw new QaStepException($"'{ctx.Step.Action}' needs a server the tool launched; this run attaches to a running server.");

    // 기능: 띄운 서버에 정상 종료를 요청하고 종료까지 기다린다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 종료되면 Pass(saveAs: 종료 정보), 실행 중이 아니거나 유예 안에 안 끝나면 Fail.
    private static async Task<StepOutcome> StopServerAsync(StepContext ctx, CancellationToken token)
    {
        IServerControl server = Server(ctx);
        if (!server.Running) return StepOutcome.Fail("The server is not running.", "running", "stopped");
        ServerExitInfo exit = await server.StopAsync(token).ConfigureAwait(false);
        ctx.Run.Log($"server stop: {exit.Message}, exit {exit.ExitMs} ms, code {exit.ExitCode}");
        var value = JsonPath.From(exit);
        return exit.Exited
            ? StepOutcome.Pass($"exited in {exit.ExitMs} ms (code {exit.ExitCode})", value)
            : StepOutcome.Fail($"The server did not exit after the stop request: {exit.Message}", $"exit within {ServerProcessManager.StopGrace.TotalSeconds:0} s", exit.Message);
    }

    // 기능: 띄운 서버 프로세스를 강제 종료한다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 종료되면 Pass(saveAs: 종료 정보), 실행 중이 아니거나 Kill 실패면 Fail.
    private static async Task<StepOutcome> KillServerAsync(StepContext ctx, CancellationToken token)
    {
        IServerControl server = Server(ctx);
        if (!server.Running) return StepOutcome.Fail("The server is not running.", "running", "stopped");
        ServerExitInfo exit = await server.KillAsync(token).ConfigureAwait(false);
        ctx.Run.Log($"server kill: {exit.Message}");
        return exit.Exited
            ? StepOutcome.Pass($"killed ({exit.ExitMs} ms)", JsonPath.From(exit))
            : StepOutcome.Fail($"Kill failed: {exit.Message}", "exited", exit.Message);
    }

    // 기능: 멈춘 서버를 다시 띄운다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 시작되면 Pass(saveAs: 시작 정보), 이미 실행 중이면 Fail. 시작 실패면 QaStepException.
    private static async Task<StepOutcome> StartServerAsync(StepContext ctx, CancellationToken token)
    {
        IServerControl server = Server(ctx);
        if (server.Running) return StepOutcome.Fail("The server is already running.", "stopped", "running");
        ServerStartInfo info = await StartAsync(server, token).ConfigureAwait(false);
        return StepOutcome.Pass($"started: game port {info.GamePort}, pid {info.Pid} ({info.StartMs} ms)", JsonPath.From(info));
    }

    // 기능: 서버가 실행 중이면 정상 종료한 뒤 다시 띄운다.
    // 입력: ctx - 단계 문맥, token - 취소 토큰.
    // 출력: 재시작되면 Pass(saveAs: exit, start), 종료가 안 되면 Fail. 시작 실패면 QaStepException.
    private static async Task<StepOutcome> RestartServerAsync(StepContext ctx, CancellationToken token)
    {
        IServerControl server = Server(ctx);
        ServerExitInfo? exit = null;
        if (server.Running)
        {
            exit = await server.StopAsync(token).ConfigureAwait(false);
            if (!exit.Exited) return StepOutcome.Fail($"The server did not exit: {exit.Message}", "exit", exit.Message);
        }
        ServerStartInfo info = await StartAsync(server, token).ConfigureAwait(false);
        return StepOutcome.Pass($"restarted: exit {exit?.ExitMs ?? 0} ms, game port {info.GamePort}", JsonPath.From(new { exit, start = info }));
    }

    // 기능: 서버를 띄우고 도구 예외를 단계 예외로 바꾼다.
    // 입력: server - 서버 제어 객체, token - 취소 토큰.
    // 출력: 시작 정보. 시작 실패면 QaStepException.
    // A server that will not start again fails the step (exit 1) with the tool's reason; the run's cleanup still runs.
    private static async Task<ServerStartInfo> StartAsync(IServerControl server, CancellationToken token)
    {
        try
        {
            return await server.StartAsync(token).ConfigureAwait(false);
        }
        catch (QaToolException e)
        {
            throw new QaStepException($"The server did not start again: {e.Message}");
        }
    }

    // ---- database ----

    // 기능: 단계의 container(기본 DefaultContainer)에 대한 Docker DB 제어 객체를 가져온다.
    // 입력: ctx - 단계 문맥(container), token - 취소 토큰.
    // 출력: Docker가 있으면 (제어 객체, null), 없으면 (null, 나머지 단계까지 건너뛰는 Skip).
    private static async Task<(DockerDbController? Db, StepOutcome? Skip)> DbAsync(StepContext ctx, CancellationToken token)
    {
        string container = ctx.String("container") ?? DefaultContainer;
        (DockerDbController? db, string? unavailable) = await ctx.Run.Db.GetAsync(container, token).ConfigureAwait(false);
        // Request §76: no Docker = skip. The rest of the scenario depends on the DB fault, so it is skipped too.
        return db != null ? (db, null) : (null, StepOutcome.Skip(unavailable!, skipRest: true));
    }

    // 기능: DB 컨테이너를 멈춘다(Run이 멈춘 것은 정리 때 다시 시작된다).
    // 입력: ctx - 단계 문맥(container), token - 취소 토큰.
    // 출력: 멈추면 Pass(saveAs: container, stoppedByRun), Docker가 없으면 Skip, docker stop 실패면 Fail.
    private static async Task<StepOutcome> StopDbAsync(StepContext ctx, CancellationToken token)
    {
        (DockerDbController? db, StepOutcome? skip) = await DbAsync(ctx, token).ConfigureAwait(false);
        if (db == null) return skip!;
        DockerCommandResult r = await db.StopAsync(token).ConfigureAwait(false);
        if (!r.Ok) return StepOutcome.Fail($"docker stop {db.ContainerName} failed: {r}", "stopped", r.ToString());
        string text = db.StoppedByThisController ? "stopped (cleanup starts it again)" : "was not running";
        ctx.Run.Log($"db {db.ContainerName}: {text}");
        return StepOutcome.Pass(text, JsonPath.From(new { container = db.ContainerName, stoppedByRun = db.StoppedByThisController }));
    }

    // 기능: DB 컨테이너를 시작하고 Healthy가 될 때까지 기다린다.
    // 입력: ctx - 단계 문맥(container), token - 취소 토큰.
    // 출력: Healthy면 Pass, Docker가 없으면 Skip, docker start 실패나 Healthy Timeout이면 Fail.
    private static async Task<StepOutcome> StartDbAsync(StepContext ctx, CancellationToken token)
    {
        (DockerDbController? db, StepOutcome? skip) = await DbAsync(ctx, token).ConfigureAwait(false);
        if (db == null) return skip!;
        DockerCommandResult r = await db.StartAsync(token).ConfigureAwait(false);
        if (!r.Ok) return StepOutcome.Fail($"docker start {db.ContainerName} failed: {r}", "started", r.ToString());
        return await HealthyAsync(ctx, db, token).ConfigureAwait(false);
    }

    // The DB must already be up. A container that exists but is stopped is skipped like a missing one: the tool does
    // not start a DB the user stopped (cleanup only restarts what the run itself stopped).
    // 기능: 이미 실행 중인 DB 컨테이너가 Healthy가 될 때까지 기다린다(멈춰 있으면 시작하지 않고 건너뛴다).
    // 입력: ctx - 단계 문맥(container), token - 취소 토큰.
    // 출력: Healthy면 Pass, Docker가 없거나 컨테이너가 멈춰 있으면 Skip, inspect 실패나 Timeout이면 Fail.
    private static async Task<StepOutcome> WaitDbHealthyAsync(StepContext ctx, CancellationToken token)
    {
        (DockerDbController? db, StepOutcome? skip) = await DbAsync(ctx, token).ConfigureAwait(false);
        if (db == null) return skip!;
        (DockerCommandResult state, bool running) = await db.IsRunningAsync(token).ConfigureAwait(false);
        if (!state.Ok) return StepOutcome.Fail($"docker inspect {db.ContainerName} failed: {state}", "running", state.ToString());
        if (!running)
            return StepOutcome.Skip($"The container '{db.ContainerName}' exists but is not running (start it with: docker compose up -d mysql)", skipRest: true);
        return await HealthyAsync(ctx, db, token).ConfigureAwait(false);
    }

    // 기능: 단계의 남은 시간(최소 1초) 안에 컨테이너 Health 검사가 healthy가 되기를 기다린다.
    // 입력: ctx - 단계 문맥, db - DB 컨테이너 제어 객체, token - 취소 토큰.
    // 출력: healthy면 Pass(saveAs: container, healthy, status), 아니면 Fail.
    private static async Task<StepOutcome> HealthyAsync(StepContext ctx, DockerDbController db, CancellationToken token)
    {
        TimeSpan left = TimeSpan.FromMilliseconds(Math.Max(1000, ctx.TimeoutMs - ctx.ElapsedMs));
        DockerHealthResult h = await db.WaitHealthyAsync(left, token).ConfigureAwait(false);
        var value = JsonPath.From(new { container = db.ContainerName, healthy = h.Healthy, status = h.LastStatus });
        return h.Healthy ? StepOutcome.Pass($"{db.ContainerName} {h.LastStatus}", value)
            : StepOutcome.Fail($"{db.ContainerName} not healthy within {left.TotalSeconds:0} s", "healthy", h.LastStatus);
    }
}
