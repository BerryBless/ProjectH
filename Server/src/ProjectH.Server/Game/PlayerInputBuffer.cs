using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Per-player input queue sorted by Seq. Owned by the game loop thread only, so no locking.
// Bounded: when full the oldest input is dropped. Because the game loop takes one input per tick,
// this also caps a client that sends faster than the tick rate (speed hack or clock drift).
public sealed class PlayerInputBuffer
{
    private readonly InputCommand[] _items;
    private int _count;

    public PlayerInputBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new InputCommand[capacity];
    }

    public int Count => _count;
    public uint LastTakenSeq { get; private set; }
    public long DroppedCount { get; private set; }

    public bool Add(in InputCommand command)
    {
        if (command.Seq <= LastTakenSeq) return false;

        int insertAt = _count;
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Seq == command.Seq) return false;
            if (_items[i].Seq > command.Seq)
            {
                insertAt = i;
                break;
            }
        }

        if (_count == _items.Length)
        {
            DroppedCount++;
            // The new input would be the oldest one kept: dropping it is the same as dropping the oldest.
            if (insertAt == 0) return false;
            Array.Copy(_items, 1, _items, 0, _count - 1);
            _count--;
            insertAt--;
        }

        Array.Copy(_items, insertAt, _items, insertAt + 1, _count - insertAt);
        _items[insertAt] = command;
        _count++;
        return true;
    }

    // Phase 10 D2: a resumed player's new connection numbers its inputs from 1 again, so what the old connection
    // sent and the Seq order it set are forgotten. DroppedCount is a total and stays.
    public void Reset()
    {
        _count = 0;
        LastTakenSeq = 0;
    }

    public bool TryTake(out InputCommand command)
    {
        if (_count == 0)
        {
            command = default;
            return false;
        }
        command = _items[0];
        Array.Copy(_items, 1, _items, 0, _count - 1);
        _count--;
        LastTakenSeq = command.Seq;
        return true;
    }
}
