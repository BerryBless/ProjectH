#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System;

namespace ProjectH.Client.UI
{
    // Phase 11 D10: the kill feed's newest lines in a fixed ring of Capacity. Each line shows for LineSeconds; a sixth
    // line pushes the oldest out. Pure (no UnityEngine): KillFeed redraws when Version changed. Times are the caller's
    // clock in seconds. Lines are built once per death (UiText.KillLine), never per frame.
    public sealed class KillFeedModel
    {
        public const int Capacity = 5;
        public const float LineSeconds = 6f;

        private readonly string[] _lines = new string[Capacity];
        private readonly float[] _expires = new float[Capacity];
        private int _oldest;
        private int _count;

        public int Count => _count;
        public int Version { get; private set; }

        // i = 0 is the newest line, Count - 1 the oldest.
        public string Line(int i)
        {
            if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
            return _lines[(_oldest + _count - 1 - i) % Capacity];
        }

        public void Add(string line, float now)
        {
            if (_count == Capacity)
            {
                _lines[_oldest] = null;
                _oldest = (_oldest + 1) % Capacity;
                _count--;
            }
            int slot = (_oldest + _count) % Capacity;
            _lines[slot] = line;
            _expires[slot] = now + LineSeconds;
            _count++;
            Version++;
        }

        // Lines expire in the order they came, so only the oldest ones are checked. True when a line went.
        public bool Expire(float now)
        {
            bool changed = false;
            while (_count > 0 && now >= _expires[_oldest])
            {
                _lines[_oldest] = null;
                _oldest = (_oldest + 1) % Capacity;
                _count--;
                changed = true;
            }
            if (changed) Version++;
            return changed;
        }

        public void Clear()
        {
            if (_count == 0) return;
            Array.Clear(_lines, 0, Capacity);
            _oldest = 0;
            _count = 0;
            Version++;
        }
    }
}
