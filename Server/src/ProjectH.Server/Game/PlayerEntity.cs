using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// One joined player. Owned by Match on the game loop thread.
public sealed class PlayerEntity
{
    // Phase 10 D2: the PeerId of a player whose connection dropped and who waits for a reconnect. LiteNetLib reuses
    // peer ids, so a graced player must not keep its old one: the next connection with that id would get its packets.
    public const int NoPeer = -1;

    // 기능: 접속한 플레이어 엔티티를 만든다.
    // 입력: entityId - Match 안의 엔티티 ID, peerId - 연결 Peer ID, devPlayerId - 개발용 플레이어 ID, inputCapacity - 입력 Buffer 최대 크기.
    // 출력: 빈 입력 Buffer를 가진 PlayerEntity. 전투·위치 상태는 Match가 이후에 설정한다.
    public PlayerEntity(ushort entityId, int peerId, string devPlayerId, int inputCapacity)
    {
        EntityId = entityId;
        PeerId = peerId;
        DevPlayerId = devPlayerId;
        Inputs = new PlayerInputBuffer(inputCapacity);
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

    // 기능: 건설 관심 영역 동기화 상태를 처음으로 되돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 관심 셀, 동기화 대기 셀, 진행 위치가 초기화되어 관심 영역 전체를 다시 보낸다.
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

    // Phase 10 D2: the tick the reconnect grace ends at (only meaningful while PeerId is NoPeer).
    public uint GraceEndTick;
    public bool IsGraced => PeerId == NoPeer;
}
