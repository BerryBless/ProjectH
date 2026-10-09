using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// One joined player. Owned by Match on the game loop thread.
public sealed class PlayerEntity
{
    // Phase 10 D2: the PeerId of a player whose connection dropped and who waits for a reconnect. LiteNetLib reuses
    // peer ids, so a graced player must not keep its old one: the next connection with that id would get its packets.
    public const int NoPeer = -1;

    // 기능: 경기에 들어온 플레이어를 만든다.
    // 입력: entityId - 엔티티 id, peerId - 연결 id, devPlayerId - 이름, inputCapacity - 입력 버퍼 칸 수,
    //   maxSeqAhead - 입력 Seq 창(리뷰 수정 A4, ServerOptions.InputSeqWindow).
    // 출력: 빈 입력 버퍼를 가진 PlayerEntity.
    public PlayerEntity(ushort entityId, int peerId, string devPlayerId, int inputCapacity,
        int maxSeqAhead = ProjectH.Shared.Protocol.ProtocolLimits.MaxInputSeqAhead)
    {
        EntityId = entityId;
        PeerId = peerId;
        DevPlayerId = devPlayerId;
        Inputs = new PlayerInputBuffer(inputCapacity, maxSeqAhead);
    }

    public ushort EntityId { get; }
    // Changes only for the reconnect grace (D2): NoPeer while graced, the new connection's id after a resume.
    public int PeerId { get; internal set; }
    public string DevPlayerId { get; }
    public PlayerInputBuffer Inputs { get; }

    // Fields, not properties, so MovementSimulation can step State by ref without copies.
    public MoveState State;
    // Phase 12 D11: whether the last step sprinted (the snapshot flag).
    public bool Sprinting;
    public InputCommand LastInput;
    public uint LastProcessedSeq;
    // Consecutive ticks without a buffered input, reset when one is taken. Match stops counting at
    // SimHz / 2 (the repeat grace window), so it cannot overflow on a long-silent connection.
    public int MissedTicks;

    // Combat (Phase 3). Set by Match.ResetCombat at join and respawn; game loop thread only.
    public int Health;
    public int Shield;
    public bool Alive;
    public uint RespawnAtTick;

    // Phase 5 (D3, D9, D12): set when a match starts. A participant keeps its placement once eliminated
    // (0 = still in, or not a participant); kills count only during the match.
    public bool Participant;
    public byte Placement;
    // Review fix B4 (game loop thread only): the resume key of the connection this player joined or resumed with
    // (HMAC(K, "resume"), null = none: such a player can never be resumed), and the highest resume nonce taken with it. A
    // graced player is resumed only by a proof made with this key (or PrevResumeKey) and a larger nonce; Ok and Resumed set the new
    // connection's key.
    public byte[]? ResumeKey;
    public uint LastResumeNonce;
    // Review B round 1 (game loop thread only): the key the last Resumed request proved with, and the nonce it used. The client
    // takes ResumeKey only when JoinMatchResponse arrives, so until then this one (the client's) keeps resuming with a larger
    // nonce. Set at every Resumed (null at Ok), cleared when the connection's first input is accepted (Match.EnqueueInput).
    public byte[]? PrevResumeKey;
    public uint PrevResumeNonce;
    public int Kills;
    // Phase 9 (§37): damage this player dealt to others during the match (shield and health actually removed, no
    // overkill), and the tick it was eliminated (0 = still in). Reset when a match starts.
    public int DamageDealt;
    public uint EliminatedTick;

    // Phase 4 (D10): weapons, magazines, per-slot fire intervals, ammo reserves and consumables. Replaced
    // by the starting loadout at join and respawn.
    public readonly Inventory Inventory = new();
    // The reload of the current slot (a switch cancels it).
    public bool Reloading;
    public uint ReloadEndTick;
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
    public bool FireHeld;
    // Phase 13 D7: the tick the harvest tool can swing again (held Fire swings at the cooldown).
    public uint NextSwingTick;
    // Phase 17 D9: the tick the next grenade can be thrown at (one per throw interval). 0 at join and every new life.
    public uint NextGrenadeTick;
    // Review fix C2 (SEC-8): the weapon in hand cannot fire before this tick. Set by WeaponRules.Equip whenever what is in hand
    // changes (a slot switch, a pickup or swap into the hand, a drop); 0 at join and every new life (ResetState).
    public uint SwitchReadyTick;
    // Review fix C6 (SEC-20): the tick from which the next G or E pickup is taken (one per ItemRules.ActionIntervalTicks).
    public uint NextItemActionTick;
    // Review fix C7 (SEC-10, SEC-19): anti-cheat counters for the match record, game loop only, reset at every match start
    // (Match.ResetMatchCounters). Integers only, so recording allocates nothing. Shots fired (each trigger pull, projectiles
    // included), hitscan rays fired and rays that hit a player, the ticks the shots were rewound by (sum) and how many claims
    // were cut to the RTT allowance, the farthest player hit (cm), moves faster than the mode allows, and the largest aim
    // turn between two real inputs (tenths of a degree; a silent aim shows here, it is not judged).
    public int ShotsFired;
    public int PelletsFired;
    public int PelletsHit;
    public int RewindTicksSum;
    public int RewindClamped;
    public int MaxHitDistanceCm;
    public int MovementAnomalies;
    public int MaxAimTurnDeg10;
    // Review C round 1 (game loop only): the aim baseline MaxAimTurnDeg10 is measured from, kept apart from LastInput (whose
    // resets zero the aim). Set by every real input with a finite AimYaw; cleared at the join, a respawn, a resume, a seat
    // reset and an input gap (Match.ResetAimBaseline), so the first input after those only sets it again.
    public float LastAimYaw;
    public bool HasAimBaseline;
    // Phase 13 D8: build requests waiting for the game loop (BuildRequestQueue.Capacity at most), the newest sequence
    // processed (older or equal ones are dropped: a replay or a duplicate), and the tick it may place again.
    public readonly BuildRequestQueue BuildQueue = new();
    public ushort LastBuildSequence;
    public bool HasBuildSequence;
    public uint NextBuildTick;
    // Phase 13 D14: the interest cells this player's client keeps (the last BuildInterest), the ones still to sync, and
    // where the sync of the current one stands (its build column, the last piece id sent there). 0 cells = not yet told.
    public ulong InterestCells;
    public ulong SyncPending;
    public int SyncCell = -1;
    public int SyncColumn;
    public uint SyncAfterId;

    // 기능: 건설 관심 영역 동기화 상태를 처음으로 되돌린다(입장, 재접속, 라운드 초기화: Client가 아무것도 모르는 상태에서 창을 다시 받는다).
    // 입력: 없음.
    // 출력: 반환값 없음. InterestCells·SyncPending·SyncColumn·SyncAfterId가 0, SyncCell이 -1이 된다.
    // A join, a resume or a round reset: the client starts from nothing and the window is sent again.
    public void ResetInterest()
    {
        InterestCells = 0;
        SyncPending = 0;
        SyncCell = -1;
        SyncColumn = 0;
        SyncAfterId = 0;
    }

    // Feet position at the end of each recent tick, for rewinding this player as a target (D6).
    public readonly PositionHistory History = new();

    // Phase 14 D1: the join counter of the match object (1, 2, ...; only grows, never reused, unlike EntityId), the order
    // teams are made in and how a reboot card names its owner. Set once at the join.
    public uint JoinOrder;
    // Phase 14 D1: the team, 1.. during a match (fixed at its start); 0 = no team (lobby, a late spectator). Match.SameTeam
    // is the only gameplay test of it.
    public byte TeamId;
    // Phase 14 D5: who knocked this player down (null: the zone, a fall, QA, or not downed) and how; the kill of a bleed-out
    // or a squad wipe goes to DownedBy if it is still in the match. BleedCarry counts the bleed between whole health points.
    public PlayerEntity? DownedBy;
    public DeathCause DownedCause;
    public uint BleedCarry;
    // Review fix: knocked down in Freefall or Glide. The Downed body falls under ground gravity, so the landing that
    // follows does no fall damage (it would end almost every such knock-down). Used up by that landing.
    public bool DownedInAir;
    // Phase 14 D8, D10: the revive or reboot this player is doing (ChannelActive), its kind, the downed teammate or the
    // station index, and the tick it completes. RevivedBy: the reviver of this downed player (its bleed pauses).
    public bool ChannelActive;
    public ChannelKind Channel;
    public PlayerEntity? ReviveTarget;
    public int ChannelStation;
    public uint ChannelEndTick;
    public PlayerEntity? RevivedBy;

    // Phase 14 D4: knocked down (DBNO): alive in the Downed movement mode, which only the server enters and leaves.
    public bool IsDowned => Alive && State.Mode == MovementMode.Downed;
    // Phase 14 D6: alive and standing (an Up member keeps its team in and can revive).
    public bool IsUp => Alive && State.Mode != MovementMode.Downed;

    // Phase 15 D5: this player's one waypoint, which its team sees (Match.Map). Cleared at the match start, the match end,
    // the round reset and when the player leaves.
    public bool HasWaypoint;
    public System.Numerics.Vector3 Waypoint;

    // Phase 19 D5, D15: the vehicle this player sits in (null = on foot) and its seat. Set and cleared only by Match
    // (EnterVehicle, Unseat); every path that removes, resets, knocks down or eliminates the player unseats it first.
    // While seated the movement mode stays Ground, so every gate that must not treat a seated player as walking checks
    // InVehicle (Match.CanAct).
    public Vehicles.Vehicle? Vehicle;
    public int Seat;
    public bool InVehicle => Vehicle != null;
    // Phase 19 D10: this tick's input of a seated player (a real one or the missed-input repeat), read by the vehicle step.
    public InputCommand VehicleInput;
    // Phase 19 D3: the tick before which no vehicle runs this player over again (one run-over hit per cooldown).
    public uint RunOverReadyTick;
    // Phase 17–19 review (S8): set when the player leaves a seat (E or forced, Match.Unseat). While set, the Jump bit of every
    // input walked on foot is removed (Space brakes in the car: still held after the exit, it must not jump); the first real
    // input without Jump clears it. A server-made input (the missed-input repeat) never clears it. Cleared by a respawn, a
    // resume and entering a vehicle. The client's predictor applies the same rule from the input after the exit.
    public bool JumpLatchedFromVehicle;

    // Phase 10 D2: the tick the reconnect grace ends at (only meaningful while PeerId is NoPeer).
    public uint GraceEndTick;
    public bool IsGraced => PeerId == NoPeer;
}
