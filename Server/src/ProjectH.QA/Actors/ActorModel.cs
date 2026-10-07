using System.Text.Json;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 From(Vector3 v) => new(v.X, v.Y, v.Z);
    public Vector3 ToVector3() => new(X, Y, Z);
}

public enum ActorStatus
{
    Idle,          // created, never connected
    Connecting,    // socket open, not joined yet
    Joined,        // JoinMatchResponse Ok/Resumed received
    Disconnected,  // closed by us or by the server
}

// D9: one actor's state as of its latest pump tick. Immutable; the pump publishes a new one every tick (Volatile), so
// the runner reads a consistent picture without locks. Everything here is what a client knows (its BotView), never
// server state: assertions about the game read the server (`player.*`), these are `actor.*` and `network.*`.
public sealed record ActorState
{
    public string Alias { get; init; } = string.Empty;
    public string DevPlayerId { get; init; } = string.Empty;
    public ActorStatus Status { get; init; }
    public bool Connected { get; init; }
    public bool Joined { get; init; }
    public bool HasSnapshot { get; init; }
    public bool Disconnected { get; init; }
    public string DisconnectReason { get; init; } = string.Empty;
    public string DisconnectCode { get; init; } = string.Empty;
    // Each connect or reconnect opens a new connection; this counts them.
    public int Connections { get; init; }
    public ushort EntityId { get; init; }
    public bool Alive { get; init; }
    public Vec3 Position { get; init; }
    public float Yaw { get; init; }
    public string Mode { get; init; } = string.Empty;
    public int Health { get; init; }
    public int Shield { get; init; }
    public int Ammo { get; init; }
    // Rounds of the current weapon's ammo type the client holds besides the magazine (stress groups top it up).
    public int ReserveAmmo { get; init; }
    public int CurrentSlot { get; init; }
    public string Tool { get; init; } = string.Empty;
    public bool HasInventory { get; init; }
    public int WeaponId { get; init; }
    public string WeaponName { get; init; } = string.Empty;
    public bool WeaponAutomatic { get; init; }
    public int ReloadTicks { get; init; }
    public uint ServerTick { get; init; }
    public string MatchState { get; init; } = string.Empty;
    // Entity ids of the other players in the latest snapshot (at most the snapshot limit, 100).
    public IReadOnlyList<ushort> VisibleEntityIds { get; init; } = Array.Empty<ushort>();
    public int RttMs { get; init; }
    public long PacketsIn { get; init; }
    public long BytesIn { get; init; }
    public long InputsSent { get; init; }
    public int HitsLanded { get; init; }
    public int DamageTaken { get; init; }
    // Intent progress.
    public bool MoveActive { get; init; }
    public bool MoveArrived { get; init; }
    public bool MoveGaveUp { get; init; }
    public float MoveDistance { get; init; }
    public int ScriptSteps { get; init; }    // queued timed inputs (presses, holds) not yet sent
    public long PressesSent { get; init; }
    public string HeldButtons { get; init; } = string.Empty;
    public bool InputPaused { get; init; }
    // Raw (invalid) packets handed to the connection by SendRawCommand over this actor's life.
    public long RawPacketsSent { get; init; }
    // Build requests: sequences given to the latest BuildCommand (first..last), how many still wait to be sent, and the
    // latest results the server sent back (at most ActorState.MaxBuildResults, oldest dropped).
    public int BuildFirstSequence { get; init; }
    public int BuildLastSequence { get; init; }
    public int BuildsQueued { get; init; }
    public IReadOnlyList<BuildResultInfo> BuildResults { get; init; } = Array.Empty<BuildResultInfo>();
    public const int MaxBuildResults = 64;
    // Phase 13.5 buildEdit: the pieces this actor's edit commands named (at most MaxWatchedPieces, by id as text), as its
    // own client view holds them now (known = in the view; edit and rotation from Placed, Sync and Edited records).
    // actor.watchedPieces.<id>.edit reads the client's state, e.g. after a reconnect's sync.
    public IReadOnlyDictionary<string, WatchedPiece> WatchedPieces { get; init; } = new Dictionary<string, WatchedPiece>();
    public const int MaxWatchedPieces = 32;
    // QA-4 UnityClient actors: the player's latest GET /qa/status body (null for headless actors). Assertions read it as
    // actor.unity.<field> (screen, joined, statsOpen, debugVisible, fps...).
    public JsonElement? Unity { get; init; }
    // QA-5 playInputs: the latest PlayInputsCommand's id, whether it is still playing, inputs sent for it (ticks) and
    // whether it reached the end of the recording (false when stopped early: disconnect, clear, a new connection).
    public long PlaybackCommandId { get; init; }
    public bool PlaybackActive { get; init; }
    public int PlaybackSent { get; init; }
    public bool PlaybackCompleted { get; init; }
    // Stress (D38): the group behaviour driving this actor (move, combat, build...; null = scenario steps only) and the
    // build results it received over its life, by BuildResultCode (index = code).
    public string? Role { get; init; }
    public IReadOnlyList<long> BuildCodeCounts { get; init; } = Array.Empty<long>();
    // The newest command applied (ActorCommand.Id).
    public long LastCommandId { get; init; }
    // Last exception of this actor's pump work (the pump keeps running).
    public string? Error { get; init; }
}

// One timed input: Buttons held for Ticks pump ticks. FirePress = one tick of Fire then the current weapon's fire
// interval released, so a semi-automatic weapon sees a fresh trigger edge and the next press is not lost to cooldown.
public readonly record struct InputStep(InputButtons Buttons, int Ticks, bool FirePress = false);

// Changes to an actor's intent. They only set what the pump does from its next tick; progress is read back from
// ActorState (D9: commands in, state out).
// Id: increasing over the process. An actor applies its commands in order, so State.LastCommandId >= Id means this
// command and everything sent before it are in effect; handlers wait for that before judging state, since the pump
// publishes state only once per tick (a state read right after SendAsync may still be the previous step's).
public abstract record ActorCommand
{
    private static long s_next;

    public long Id { get; } = Interlocked.Increment(ref s_next);
}
public sealed record ConnectCommand(string Host, int Port, bool Reconnect) : ActorCommand;
public sealed record DisconnectCommand(bool Graceful) : ActorCommand;
public sealed record MoveToCommand(Vec3? Target, float Tolerance, bool Sprint) : ActorCommand;
// Local move input (strafe X, forward Y in -1..1) for DurationTicks (0 = until changed). (0, 0) stops.
public sealed record MoveVectorCommand(float X, float Y, int DurationTicks) : ActorCommand;
public sealed record LookCommand(float Yaw, float Pitch) : ActorCommand;
public sealed record AimAtPointCommand(Vec3 Point) : ActorCommand;
// Aim at another actor's entity as this actor's latest snapshot shows it; Fallback is read only while the entity is
// not in the snapshot.
public sealed record AimAtActorCommand(ushort EntityId, IQaActor Fallback) : ActorCommand;
public sealed record ClearAimCommand : ActorCommand;
public sealed record ScriptCommand(IReadOnlyList<InputStep> Steps) : ActorCommand;
public sealed record HoldCommand(InputButtons Buttons, bool Down) : ActorCommand;
public sealed record StopFireCommand : ActorCommand;
// Stop / restart sending input while staying connected (request §75: the server's InputTimeout closes the client).
public sealed record PauseInputCommand(bool Paused) : ActorCommand;
// Real build requests over the game protocol, in order: build mode (Q) first, then per piece one tick aiming at it and
// one tick sending the input with that aim followed by the request (the server places with the last input's aim).
public sealed record BuildCommand(IReadOnlyList<BuildPlan> Pieces) : ActorCommand;
public readonly record struct BuildPlan(BuildPieceType Piece, BuildMaterialType Material, byte X, byte Y, byte Z, byte Rotation, Vec3 AimAt);
public readonly record struct BuildResultInfo(int Sequence, string Code, uint PieceId);
// Phase 13.5 D13: real edit requests over the game protocol. Paced (Burst false): per edit AimTicksPerBuild ticks aiming
// at the piece, then the request after the input that carries the aim (no tool needed, §29). Burst: every request sent
// at once in the pump tick that applies the command (a client ignoring the interval: RateLimited when the server queue is
// full). ReuseSequence: sent with the last sequence again (duplicates, which the server drops without an answer).
public sealed record BuildEditCommand(IReadOnlyList<EditPlan> Edits, bool Burst, bool ReuseSequence) : ActorCommand
{
    public const int MaxEdits = 16;
}
// One edit. The target: PieceId, or (PieceId 0) the piece in Slot's slot as this actor's view knows it (none known: id 0,
// which the server answers NotFound). State: RawState when >= 0 (sent as is, for refusals), else Edit with Rotation
// (-1 = the piece's current rotation in the view, else 0).
public readonly record struct EditPlan(uint PieceId, BuildPieceShape? Slot, int Edit, int Rotation, int RawState);
public readonly record struct WatchedPiece(bool Known, int Edit, int Rotation);
// QA-3 (D14): raw packets sent as they are over the live connection (invalid-packet tests). At most MaxRawPackets.
public sealed record SendRawCommand(IReadOnlyList<byte[]> Packets) : ActorCommand
{
    public const int MaxRawPackets = 2500;
}
// Drops the queued timed inputs (presses, holds) and the one in progress; held buttons stay. Also stops a playback.
public sealed record ClearInputQueueCommand : ActorCommand;
// QA-5 D34: replay recorded inputs, one per pump tick while joined and sending (Seq and ViewTick are the connection's
// own). Speed 0.25-4: the recording cursor advances Speed entries per tick; entries skipped at Speed > 1 have their
// buttons merged into the sent one so a one-tick press is not lost. Replaces the actor's own intent while it plays.
public sealed record PlayInputsCommand(IReadOnlyList<RecordedInput> Inputs, double Speed) : ActorCommand
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4;
}
// Stop moving, aiming, holding, pressing: the actor stands still (still sending inputs). Also ends a group behaviour.
public sealed record ResetIntentCommand : ActorCommand;
// Stress (D38): the behaviour that sets this actor's intent every tick from now on (null = none). The brain object is
// handed over: after this command only the pump thread touches it.
public sealed record SetBrainCommand(ActorBrain? Brain) : ActorCommand;

public interface IQaActor
{
    string Alias { get; }
    string DevPlayerId { get; }
    ActorState State { get; }

    // Queues the command; it takes effect on the actor's next tick.
    ValueTask SendAsync(ActorCommand command, CancellationToken token);
}
