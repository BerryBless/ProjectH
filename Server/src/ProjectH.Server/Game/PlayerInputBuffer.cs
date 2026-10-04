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

    // 기능: 고정 크기 입력 Buffer를 만든다.
    // 입력: capacity - 보관할 최대 입력 수(1 이상, 아니면 ArgumentOutOfRangeException).
    // 출력: 비어 있는 PlayerInputBuffer.
    public PlayerInputBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new InputCommand[capacity];
    }

    public int Count => _count;
    public uint LastTakenSeq { get; private set; }
    public long DroppedCount { get; private set; }

    // 기능: Client 입력을 Seq 순서 위치에 넣는다. 가득 차면 가장 오래된 입력을 버린다.
    // 입력: command - Client가 보낸 입력.
    // 출력: 보관되면 true. 이미 처리한 Seq 이하, 중복 Seq, 가득 찬 상태에서 가장 오래된 입력이 되면 false.
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

    // 기능: 재접속한 플레이어를 위해 보관 입력과 Seq 기준을 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 보관 입력과 LastTakenSeq가 0이 되고 DroppedCount는 유지된다.
    // Phase 10 D2: a resumed player's new connection numbers its inputs from 1 again, so what the old connection
    // sent and the Seq order it set are forgotten. DroppedCount is a total and stays.
    public void Reset()
    {
        _count = 0;
        LastTakenSeq = 0;
    }

    // 기능: Seq가 가장 작은 입력 하나를 꺼낸다.
    // 입력: command - 꺼낸 입력.
    // 출력: 입력이 있으면 true와 그 입력(LastTakenSeq 갱신), 비어 있으면 false.
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
