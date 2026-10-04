namespace ProjectH.Client.Game
{
    // Slot index for a fixed-size ring pool (D14): hands out 0..Capacity-1 in order and then reuses
    // the oldest slot. The pool arrays are allocated once with Capacity entries and never grow.
    public sealed class RingCursor
    {
        private int _next;

        // 기능: 고정 크기 Ring Pool의 슬롯 커서를 만든다.
        // 입력: capacity - Pool 크기(1 이상, 아니면 ArgumentOutOfRangeException).
        // 출력: 다음 슬롯이 0인 RingCursor.
        public RingCursor(int capacity)
        {
            if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }

        // 기능: 다음에 쓸 슬롯을 내주고 커서를 한 칸 옮긴다.
        // 입력: 없음.
        // 출력: 0..Capacity-1 슬롯 인덱스(끝 다음은 0으로 돌아가 가장 오래된 슬롯을 재사용).
        public int Next()
        {
            int slot = _next;
            _next = slot + 1 == Capacity ? 0 : slot + 1;
            return slot;
        }
    }
}
