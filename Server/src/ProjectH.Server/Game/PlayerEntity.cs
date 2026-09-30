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
}
