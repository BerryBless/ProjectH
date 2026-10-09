using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// Request §138: an actor for runner tests. Commands take effect at once (no network); tests can make it refuse to join.
public sealed class MockActor : IQaActor
{
    private static int s_nextEntity = 1;
    private ActorState _state;

    // 기능: 별칭으로 Idle 상태의 가짜 Actor를 만든다.
    // 입력: alias - 시나리오 Actor 별칭.
    // 출력: DevPlayerId가 별칭에서 파생되고 Status가 Idle인 MockActor 객체.
    public MockActor(string alias)
    {
        Alias = alias;
        DevPlayerId = ActorManager.DevPlayerIdFor(alias);
        _state = new ActorState { Alias = alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle };
    }

    public string Alias { get; }
    public string DevPlayerId { get; }
    public ActorState State => Volatile.Read(ref _state);
    public List<ActorCommand> Commands { get; } = new();
    public bool NeverJoin { get; set; }
    // Scripts never finish (a stuck pump): tests check that an unfinished step clears its queued inputs.
    public bool StallScripts { get; set; }
    // Null: every build request is accepted; else every one gets this code.
    public string? BuildRefusal { get; set; }
    // QA-5: a playback that never completes (it is reported stopped, as after a lost connection).
    public bool NeverFinishPlayback { get; set; }
    // Called after a command is applied (tests react, e.g. the fake server takes damage on fire).
    public Action<MockActor, ActorCommand>? OnCommand { get; set; }

    // 기능: 명령을 기록하고 네트워크 없이 즉시 Actor 상태에 반영한 뒤 OnCommand를 호출한다.
    // 입력: command - 적용할 Actor 명령, token - 취소 토큰.
    // 출력: 반환값 없음. Commands에 명령이 쌓이고 State가 명령 종류에 맞게 바뀐다.
    public ValueTask SendAsync(ActorCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Commands) Commands.Add(command);
        ActorState s = State;
        s = command switch
        {
            ConnectCommand when NeverJoin => s with { Status = ActorStatus.Connecting, Connections = s.Connections + 1 },
            ConnectCommand => s with
            {
                Status = ActorStatus.Joined, Connected = true, Joined = true, HasSnapshot = true, Disconnected = false, Alive = true,
                Connections = s.Connections + 1, EntityId = s.EntityId != 0 ? s.EntityId : (ushort)Interlocked.Increment(ref s_nextEntity),
                HasInventory = true, WeaponId = 1, WeaponName = "Vesper AR", Tool = "Weapon", Health = 100,
            },
            DisconnectCommand d => s with { Status = ActorStatus.Disconnected, Connected = false, Joined = false, Disconnected = true, DisconnectReason = d.Graceful ? "graceful" : "aborted" },
            MoveToCommand { Target: Vec3 t } => s with { Position = t, MoveActive = true, MoveArrived = true },
            MoveToCommand => s with { MoveActive = false, MoveArrived = false },
            ScriptCommand sc when StallScripts => s with { ScriptSteps = s.ScriptSteps + sc.Steps.Count },
            ClearInputQueueCommand => s with { ScriptSteps = 0, BuildsQueued = 0 },
            PauseInputCommand pi => s with { InputPaused = pi.Paused },
            SendRawCommand raw => s with { RawPacketsSent = s.RawPacketsSent + raw.Packets.Count },
            BuildCommand b => s with
            {
                BuildFirstSequence = s.BuildLastSequence + 1,
                BuildLastSequence = s.BuildLastSequence + b.Pieces.Count,
                BuildResults = s.BuildResults.Concat(b.Pieces.Select((p, i) => new BuildResultInfo(s.BuildLastSequence + 1 + i,
                    BuildRefusal ?? "Ok", BuildRefusal == null ? (uint)(100 + s.BuildLastSequence + i) : 0u))).ToArray(),
            },
            // Phase 13.5: edits answer like builds (BuildRefusal or Ok with the target id); duplicates get no answer.
            BuildEditCommand { ReuseSequence: true } => s,
            BuildEditCommand e => s with
            {
                BuildFirstSequence = s.BuildLastSequence + 1,
                BuildLastSequence = s.BuildLastSequence + e.Edits.Count,
                BuildResults = s.BuildResults.Concat(e.Edits.Select((p, i) => new BuildResultInfo(s.BuildLastSequence + 1 + i,
                    BuildRefusal ?? "Ok", BuildRefusal == null ? p.PieceId : 0u))).ToArray(),
            },
            PlayInputsCommand play => s with { PlaybackCommandId = play.Id, PlaybackActive = false, PlaybackCompleted = !NeverFinishPlayback, PlaybackSent = NeverFinishPlayback ? 0 : HeadlessActor.PlaybackTicks(play.Inputs.Count, play.Speed) },
            ScriptCommand sc => s with { PressesSent = s.PressesSent + sc.Steps.Count(x => x.FirePress || x.Buttons != 0), ScriptSteps = 0 },
            AimAtActorCommand a => s with { VisibleEntityIds = s.VisibleEntityIds.Append(a.EntityId).ToArray() },
            _ => s,
        };
        s = s with { LastCommandId = command.Id };
        Volatile.Write(ref _state, s);
        OnCommand?.Invoke(this, command);
        return ValueTask.CompletedTask;
    }

    // 기능: 테스트가 Actor 상태를 직접 바꾼다.
    // 입력: change - 현재 상태를 받아 새 상태를 돌려주는 함수.
    // 출력: 반환값 없음. State가 change의 결과로 교체된다.
    public void Set(Func<ActorState, ActorState> change) => Volatile.Write(ref _state, change(State));
}

// The QA Control API in memory. Thread-safe enough for tests: one runner flow at a time.
public sealed class FakeQaServer : IQaServerClient
{
    public Dictionary<string, Dictionary<string, object?>> Players { get; } = new();
    public Dictionary<string, object?> Match { get; } = new() { ["state"] = "Playing", ["players"] = 0, ["alive"] = 0 };
    public List<(string Command, string? Player, string Args)> Commands { get; } = new();
    public List<string> PlayerQueries { get; } = new();
    public List<object> Events { get; } = new();
    public bool Stopped { get; private set; }
    public Func<string, string?, JsonElement, CommandResponse>? CommandHandler { get; set; }

    // 기능: 가짜 서버에 접속·생존 상태의 플레이어 한 명을 등록한다.
    // 입력: devPlayerId - 플레이어 Dev ID, health - 초기 체력.
    // 출력: 등록된 플레이어 상태 사전(테스트가 값을 바꿀 수 있다).
    public Dictionary<string, object?> AddPlayer(string devPlayerId, int health = 100)
    {
        var p = new Dictionary<string, object?>
        {
            ["devPlayerId"] = devPlayerId, ["entityId"] = Players.Count + 1, ["connected"] = true, ["graced"] = false, ["alive"] = true,
            ["health"] = health, ["shield"] = 0, ["position"] = new Dictionary<string, object?> { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 0.0 },
            ["weapon"] = new Dictionary<string, object?> { ["name"] = "Vesper AR", ["slot"] = 0 },
        };
        Players[devPlayerId] = p;
        return p;
    }

    // 기능: 가짜 이벤트 목록에 순번·Tick이 붙은 이벤트를 하나 추가한다.
    // 입력: type - 이벤트 종류, player - 관련 플레이어(없으면 null), data - 이벤트 데이터(없으면 빈 객체).
    // 출력: 반환값 없음. Events에 이벤트가 하나 늘어난다.
    public void AddEvent(string type, string? player, object? data = null) =>
        Events.Add(new { seq = Events.Count + 1, tick = 100 + Events.Count, utc = "2026-10-02T00:00:00Z", type, player, data = data ?? new { } });

    // 기능: 고정된 서버 상태(qaMode, 포트, 버전)와 현재 플레이어 수를 health 응답으로 돌려준다.
    // 입력: token - 취소 토큰(쓰지 않음).
    // 출력: ok=true인 health JSON.
    public Task<JsonElement> GetHealthAsync(CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, qaMode = true, gamePort = 7777, qaPort = 7780, version = "test-1", activeSessions = Players.Count }));

    // 기능: QA 명령을 기록하고 CommandHandler가 있으면 그 응답을, 없으면 성공 응답을 돌려준다.
    // 입력: command - 명령 이름, player - 대상 플레이어, args - 명령 인자, runId - 실행 ID(쓰지 않음), token - 취소 토큰.
    // 출력: CommandHandler의 응답 또는 done=command를 담은 200 성공 응답.
    public Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Commands) Commands.Add((command, player, args.GetRawText()));
        if (CommandHandler != null) return Task.FromResult(CommandHandler(command, player, args));
        return Task.FromResult(new CommandResponse(true, null, JsonSerializer.SerializeToElement(new { done = command }), 200));
    }

    // 기능: 조회를 기록하고 등록된 플레이어 상태를 JSON으로 돌려준다.
    // 입력: devPlayerId - 조회할 플레이어 Dev ID, token - 취소 토큰.
    // 출력: 플레이어 상태 JSON. 등록되지 않았으면 null.
    public Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (PlayerQueries) PlayerQueries.Add(devPlayerId);
        return Task.FromResult(Players.TryGetValue(devPlayerId, out var p) ? JsonSerializer.SerializeToElement(p) : (JsonElement?)null);
    }

    // 기능: 등록된 모든 플레이어 상태를 배열 JSON으로 돌려준다.
    // 입력: token - 취소 토큰(쓰지 않음).
    // 출력: 플레이어 상태 배열 JSON.
    public Task<JsonElement> GetPlayersAsync(CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(Players.Values));

    // 기능: 테스트가 채운 Match 사전을 JSON으로 돌려준다.
    // 입력: token - 취소 토큰(쓰지 않음).
    // 출력: Match 상태 JSON.
    public Task<JsonElement> GetMatchAsync(CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(Match));

    // 기능: 조건과 무관하게 조각 하나(id 42, health 100)가 있는 건설 상태를 돌려준다.
    // 입력: x, z, radius, max - 조회 범위(쓰지 않음), token - 취소 토큰(쓰지 않음).
    // 출력: count=1과 조각 배열을 담은 JSON.
    public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { count = 1, pieces = new[] { new { id = 42, health = 100 } } }));

    // 기능: 조건과 무관하게 고정된 Loot 상태(상자 1, 아이템 3, 무기 1)를 돌려준다.
    // 입력: x, z, radius - 조회 범위(쓰지 않음), token - 취소 토큰(쓰지 않음).
    // 출력: containersSpawned와 items 집계를 담은 JSON.
    public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { containersSpawned = 1, items = new { count = 3, weapons = 1 } }));

    // 기능: 터진 수류탄 하나가 있는 고정된 투사체 상태를 돌려준다.
    // 입력: token - 취소 토큰(쓰지 않음).
    // 출력: 빈 projectiles 배열과 explosionsTotal=1, launched=1을 담은 JSON.
    // Phase 17: one exploded grenade.
    public Task<JsonElement> GetProjectilesAsync(CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { projectiles = Array.Empty<object>(), explosionsTotal = 1, launched = 1 }));

    // 기능: Phase 19: GET /qa/vehicles 대신 차량 하나(id 3)를 서버처럼 id 키로 돌려준다.
    // 입력: token - 취소(쓰지 않음).
    // 출력: count와 "3" 키를 가진 JSON.
    public Task<JsonElement> GetVehiclesAsync(CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["count"] = 1,
            ["3"] = new { id = 3, state = "Active", health = 400, speed = 0f },
        }));

    // QA-5: settable so baseline tests can make a run worse than the previous one.
    public object Metrics { get; set; } = new { tickP50Ms = 0.4, tickP95Ms = 0.9, tickP99Ms = 1.5, tickMaxMs = 3.0, workingSetMB = 80.5 };

    // 기능: 테스트가 설정한 Metrics 객체를 그대로 JSON으로 돌려준다.
    // 입력: windowSeconds - 집계 창(쓰지 않음), token - 취소 토큰(쓰지 않음).
    // 출력: Metrics를 직렬화한 JSON.
    public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(Metrics));

    // 기능: 이벤트 목록에서 after 이후 최대 max개를 잘라 서버 응답 형식으로 돌려준다.
    // 입력: after - 건너뛸 이벤트 수(마지막으로 받은 순번), max - 최대 개수, token - 취소 토큰(쓰지 않음).
    // 출력: next 순번, oldest=1, dropped=0, events 배열을 담은 JSON.
    public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token)
    {
        var list = Events.Skip((int)after).Take(max).ToList();
        return Task.FromResult(JsonSerializer.SerializeToElement(new { next = after + list.Count, oldest = 1, dropped = 0, events = list }));
    }

    // 기능: 서버 정지 요청을 받았다고 표시한다.
    // 입력: token - 취소 토큰(쓰지 않음).
    // 출력: 반환값 없음. Stopped가 true가 된다.
    public Task StopServerAsync(CancellationToken token)
    {
        Stopped = true;
        return Task.CompletedTask;
    }
}
