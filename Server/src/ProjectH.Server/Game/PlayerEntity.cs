using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// One joined player. Owned by Match on the game loop thread.
public sealed class PlayerEntity
{
    public PlayerEntity(ushort entityId, int peerId, string devPlayerId, int inputCapacity)
    {
        EntityId = entityId;
        PeerId = peerId;
        DevPlayerId = devPlayerId;
        Inputs = new PlayerInputBuffer(inputCapacity);
    }

    public ushort EntityId { get; }
    public int PeerId { get; }
    public string DevPlayerId { get; }
    public PlayerInputBuffer Inputs { get; }

    // Fields, not properties, so MovementSimulation can step State by ref without copies.
    public MoveState State;
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

    // Loadout slot (0 = Slot1, 1 = Slot2). Ammo and NextFireTick are per slot, so switching weapons
    // neither refills a magazine nor skips the other weapon's fire interval.
    public int WeaponSlot;
    public readonly int[] Ammo = new int[WeaponCatalog.SlotCount];
    public readonly uint[] NextFireTick = new uint[WeaponCatalog.SlotCount];
    public bool Reloading;
    public uint ReloadEndTick;
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
    public bool FireHeld;

    // Feet position at the end of each recent tick, for rewinding this player as a target (D6).
    public readonly PositionHistory History = new();
}
