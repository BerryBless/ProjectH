namespace ProjectH.Client.Game
{
    // Slot index for a fixed-size ring pool (D14): hands out 0..Capacity-1 in order and then reuses
    // the oldest slot. The pool arrays are allocated once with Capacity entries and never grow.
    public sealed class RingCursor
    {
        private int _next;

        // 기능: 고정 크기 링 풀의 칸 번호 발급기를 만든다.
        // 입력: capacity - 칸 수(0 이하면 ArgumentOutOfRangeException).
        // 출력: 다음 칸이 0인 RingCursor.
        public RingCursor(int capacity)
        {
            if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }

        // 기능: 다음 칸 번호를 내준다. Capacity에 닿으면 0으로 돌아가 가장 오래된 칸을 재사용한다.
        // 입력: 없음.
        // 출력: 이번에 쓸 칸 번호(0..Capacity-1).
        public int Next()
        {
            int slot = _next;
            _next = slot + 1 == Capacity ? 0 : slot + 1;
            return slot;
        }
    }
}
