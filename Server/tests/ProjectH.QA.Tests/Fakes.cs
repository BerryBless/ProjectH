using System.Text.Json;
using ProjectH.QA;

namespace ProjectH.QA.Tests;

// Request §138: an actor for runner tests. Commands take effect at once (no network); tests can make it refuse to join.
public sealed class MockActor : IQaActor
{
    private static int s_nextEntity = 1;
    private ActorState _state;

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

    public void AddEvent(string type, string? player, object? data = null) =>
        Events.Add(new { seq = Events.Count + 1, tick = 100 + Events.Count, utc = "2026-10-02T00:00:00Z", type, player, data = data ?? new { } });

    public Task<JsonElement> GetHealthAsync(CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, qaMode = true, gamePort = 7777, qaPort = 7780, version = "test-1", activeSessions = Players.Count }));

    public Task<CommandResponse> CommandAsync(string command, string? player, JsonElement args, string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Commands) Commands.Add((command, player, args.GetRawText()));
        if (CommandHandler != null) return Task.FromResult(CommandHandler(command, player, args));
        return Task.FromResult(new CommandResponse(true, null, JsonSerializer.SerializeToElement(new { done = command }), 200));
    }

    public Task<JsonElement?> GetPlayerAsync(string devPlayerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (PlayerQueries) PlayerQueries.Add(devPlayerId);
        return Task.FromResult(Players.TryGetValue(devPlayerId, out var p) ? JsonSerializer.SerializeToElement(p) : (JsonElement?)null);
    }

    public Task<JsonElement> GetPlayersAsync(CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(Players.Values));

    public Task<JsonElement> GetMatchAsync(CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(Match));

    public Task<JsonElement> GetBuildAsync(float? x, float? z, float? radius, int? max, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { count = 1, pieces = new[] { new { id = 42, health = 100 } } }));

    public Task<JsonElement> GetLootAsync(float? x, float? z, float? radius, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { containersSpawned = 1, items = new { count = 3, weapons = 1 } }));

    // QA-5: settable so baseline tests can make a run worse than the previous one.
    public object Metrics { get; set; } = new { tickP50Ms = 0.4, tickP95Ms = 0.9, tickP99Ms = 1.5, tickMaxMs = 3.0, workingSetMB = 80.5 };

    public Task<JsonElement> GetMetricsAsync(int? windowSeconds, CancellationToken token) =>
        Task.FromResult(JsonSerializer.SerializeToElement(Metrics));

    public Task<JsonElement> GetEventsAsync(long after, int max, CancellationToken token)
    {
        var list = Events.Skip((int)after).Take(max).ToList();
        return Task.FromResult(JsonSerializer.SerializeToElement(new { next = after + list.Count, oldest = 1, dropped = 0, events = list }));
    }

    public Task StopServerAsync(CancellationToken token)
    {
        Stopped = true;
        return Task.CompletedTask;
    }
}
