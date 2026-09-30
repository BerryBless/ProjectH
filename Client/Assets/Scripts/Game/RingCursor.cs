namespace ProjectH.Client.Game
{
    // Slot index for a fixed-size ring pool (D14): hands out 0..Capacity-1 in order and then reuses
    // the oldest slot. The pool arrays are allocated once with Capacity entries and never grow.
    public sealed class RingCursor
    {
        private int _next;

        public RingCursor(int capacity)
        {
            if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }

        public int Next()
        {
            int slot = _next;
            _next = slot + 1 == Capacity ? 0 : slot + 1;
            return slot;
        }
    }
}
