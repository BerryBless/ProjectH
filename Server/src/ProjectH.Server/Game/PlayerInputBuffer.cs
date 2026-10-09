using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Per-player input queue sorted by Seq. Owned by the game loop thread only, so no locking.
// Bounded: when full the oldest input is dropped. Because the game loop takes one input per tick,
// this also caps a client that sends faster than the tick rate (speed hack or clock drift).
public sealed class PlayerInputBuffer
{
    private readonly InputCommand[] _items;
    private readonly uint _maxSeqAhead;
    private int _count;

    // 기능: 입력 버퍼를 만든다.
    // 입력: capacity - 칸 수(1 이상), maxSeqAhead - 리뷰 수정 A4의 Seq 창(마지막으로 가져간 Seq보다 이만큼까지 앞선 입력만 받는다,
    //   ProtocolLimits.MaxInputSeqAhead 이상으로 올린다).
    // 출력: 빈 PlayerInputBuffer.
    public PlayerInputBuffer(int capacity, int maxSeqAhead = ProtocolLimits.MaxInputSeqAhead)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new InputCommand[capacity];
        _maxSeqAhead = (uint)Math.Max(ProtocolLimits.MaxInputSeqAhead, maxSeqAhead);
    }

    public int Count => _count;
    public uint LastTakenSeq { get; private set; }
    public long DroppedCount { get; private set; }
    // Review fix A4: inputs dropped for a Seq more than the window past LastTakenSeq (a total, like
    // DroppedCount; Reset keeps it).
    public long SeqAheadDrops { get; private set; }

    // 기능: 입력 하나를 Seq 순서 자리에 넣는다. 가득 차면 가장 오래된 것을 버린다.
    // 입력: command - 받은 입력.
    // 출력: 넣었으면 true. 이미 가져간 Seq 이하·중복·가득 찬 상태에서 가장 오래된 것, 리뷰 수정 A4(SEC-7): 가져간 입력이 있는데
    //   LastTakenSeq보다 Seq 창(생성자의 maxSeqAhead) 넘게 앞선 Seq(SeqAheadDrops를 센다)면 false.
    public bool Add(in InputCommand command)
    {
        if (command.Seq <= LastTakenSeq) return false;
        // A client numbers one input per tick, and its inputs lost in a network outage (up to the disconnect timeout) leave
        // a gap of that many Seqs; a Seq farther ahead is injected or corrupt, and taking it would make LastTakenSeq refuse
        // every real input after it. The subtraction cannot wrap: Seq > LastTakenSeq here. Before the first take (a new or
        // resumed connection) there is no reference yet.
        if (LastTakenSeq != 0 && command.Seq - LastTakenSeq > _maxSeqAhead)
        {
            SeqAheadDrops++;
            return false;
        }

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

    // 기능: 버퍼를 비우고 Seq 기준을 지운다(재접속한 연결이 Seq를 1부터 다시 센다).
    // 입력: 없음.
    // 출력: 반환값 없음. Count가 0, LastTakenSeq가 0이 된다. DroppedCount·SeqAheadDrops 누적은 유지된다.
    // Phase 10 D2: a resumed player's new connection numbers its inputs from 1 again, so what the old connection
    // sent and the Seq order it set are forgotten. DroppedCount is a total and stays.
    public void Reset()
    {
        _count = 0;
        LastTakenSeq = 0;
    }

    // 기능: Seq가 가장 작은 입력 하나를 꺼내고 LastTakenSeq를 그 Seq로 올린다.
    // 입력: command - 꺼낸 입력(없으면 default).
    // 출력: 입력이 있으면 true와 그 입력, 버퍼가 비었으면 false.
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
