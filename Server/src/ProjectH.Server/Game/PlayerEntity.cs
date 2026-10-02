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

    // Feet position at the end of each recent tick, for rewinding this player as a target (D6).
    public readonly PositionHistory History = new();

    // Phase 10 D2: the tick the reconnect grace ends at (only meaningful while PeerId is NoPeer).
    public uint GraceEndTick;
    public bool IsGraced => PeerId == NoPeer;
}
