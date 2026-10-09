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

        // 기능: 최신순으로 i번째 줄을 돌려준다.
        // 입력: i - 0이 가장 새 줄, Count - 1이 가장 오래된 줄.
        // 출력: 그 줄 문자열. i가 범위 밖이면 ArgumentOutOfRangeException.
        // i = 0 is the newest line, Count - 1 the oldest.
        public string Line(int i)
        {
            if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
            return _lines[(_oldest + _count - 1 - i) % Capacity];
        }

        // 기능: 줄 하나를 링의 끝에 넣는다. 가득 차 있으면 가장 오래된 줄을 먼저 버린다.
        // 입력: line - 넣을 줄, now - 현재 시각(초, 만료는 now + LineSeconds).
        // 출력: 반환값 없음. Count와 Version이 바뀐다.
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

        // 기능: 만료 시각이 지난 줄을 오래된 쪽부터 지운다(줄은 들어온 순서로 만료되므로 가장 오래된 줄만 본다).
        // 입력: now - 현재 시각(초).
        // 출력: 한 줄이라도 지웠으면 true(Version이 오른다), 아니면 false.
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

        // 기능: 모든 줄을 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 줄이 있었을 때만 Count가 0이 되고 Version이 오른다.
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
