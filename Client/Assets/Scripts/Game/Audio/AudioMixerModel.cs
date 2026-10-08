using System;
using System.Numerics;

namespace ProjectH.Client.Game.Audio
{
    // One sound request (D2). Spatial = a 3D sound at Position; otherwise 2D (our own body, UI) and Position is ignored.
    // Source names who made it (an entity id, a piece id, a projectile id, a door or container number, 0 = none) for the
    // duplicate rule: a repeat of the same kind from the same source within the kind's MinInterval is dropped.
    public struct AudioEvent
    {
        public SoundKind Kind;
        public bool Spatial;
        public Vector3 Position;
        public uint Source;
        public float VolumeScale;   // 0 = 1 (the default value of the struct plays at the catalog volume)
    }

    // A voice to start this frame: the pool stops whatever Voice was playing and plays Kind's Variant there.
    public struct AudioStart
    {
        public int Voice;
        public SoundKind Kind;
        public int Variant;
        public float Pitch;
        public float Volume;
        public bool Spatial;
        public Vector3 Position;
        public float MaxDistance;
    }

    // Phase 18 D2: what to play, decided without Unity. Client handlers Enqueue events into a fixed queue (QueueCapacity). At
    // Enqueue a 3D request beyond its kind's MaxDistance from the last mixed listener is dropped at once (DroppedDistance), and
    // a full queue keeps the more important requests: the new one replaces the worst queued request (highest priority number,
    // then the farthest) only when it is strictly better; either way one request is lost and counted (QueueOverflow). So a big
    // building packet of far piece damage cannot push a gunshot out. Once per frame Mix:
    //  1. voices whose clip has ended are freed (the model keeps each voice's end time, from the clip length and pitch, so it
    //     never asks Unity);
    //  2. each queued event: a 3D one farther than its kind's MaxDistance is dropped (DroppedDistance); a repeat of the same
    //     kind from the same source within MinInterval is dropped (DroppedDuplicate);
    //  3. the rest are sorted by priority (1 first), then distance (nearer first);
    //  4. each takes a free voice, or else replaces the worst playing voice (the highest priority number, then the farthest)
    //     when it is strictly better (a lower number, or the same number and nearer); otherwise, or past MaxStartsPerFrame,
    //     it is dropped (DroppedBudget, which also counts the voices cut off).
    // Fixed arrays only: no allocation after construction. Main thread only. Counters only grow (QA reads them).
    public sealed class AudioMixerModel
    {
        public const int QueueCapacity = 64;
        public const int VoiceBudget = 24;
        public const int MaxStartsPerFrame = 8;
        public const float PitchJitter = 0.05f;
        // The (kind, source) plays remembered for the duplicate rule. When full, a new pair takes a slot whose duplicate window
        // (its time + its kind's MinInterval) is already over first, else the slot whose window ends soonest.
        public const int RecentCapacity = 64;
        private const float DefaultDuration = 0.5f;

        private readonly AudioEvent[] _queue = new AudioEvent[QueueCapacity];
        private int _queued;

        private readonly AudioEvent[] _candidates = new AudioEvent[QueueCapacity];
        private readonly float[] _candidateDistance = new float[QueueCapacity];
        private int _candidateCount;

        private readonly bool[] _voiceActive = new bool[VoiceBudget];
        private readonly float[] _voiceEnd = new float[VoiceBudget];
        private readonly byte[] _voicePriority = new byte[VoiceBudget];
        private readonly float[] _voiceDistance = new float[VoiceBudget];

        private readonly SoundKind[] _recentKind = new SoundKind[RecentCapacity];
        private readonly uint[] _recentSource = new uint[RecentCapacity];
        private readonly float[] _recentTime = new float[RecentCapacity];
        private int _recentCount;

        // The listener of the last Mix (the Enqueue distance check uses it; none before the first Mix).
        private Vector3 _listener;
        private bool _hasListener;

        private readonly AudioStart[] _starts = new AudioStart[MaxStartsPerFrame];
        private readonly float[] _durations = new float[(int)SoundKind.Count];
        private readonly int[] _plays = new int[(int)SoundKind.Count];
        private uint _random = 0x9E3779B9u;

        // 기능: 빈 큐·빈 목소리로 모델을 만든다. 클립 길이는 SetDuration 전까지 DefaultDuration이다.
        // 입력: 없음.
        // 출력: 아무것도 재생하지 않는 모델.
        public AudioMixerModel()
        {
            for (int i = 0; i < _durations.Length; i++) _durations[i] = DefaultDuration;
        }

        // Play counts per kind (index = SoundKind), the array itself: QA reads it without a copy. Never shrinks.
        public int[] Plays => _plays;
        public int Queued => _queued;
        public int ActiveCount { get; private set; }
        public int DroppedBudget { get; private set; }
        public int DroppedDuplicate { get; private set; }
        public int DroppedDistance { get; private set; }
        public int QueueOverflow { get; private set; }
        // This frame's starts (valid until the next Mix).
        public int StartCount { get; private set; }

        // 기능: 이번 프레임에 시작할 목소리 하나를 돌려준다.
        // 입력: index - 0..StartCount-1.
        // 출력: 시작 명령.
        public AudioStart Start(int index) => _starts[index];

        // 기능: 종류의 클립 길이를 정한다(가장 긴 변형, 시작 때 클립 bank가 한 번 알려 준다).
        // 입력: kind - 소리 종류, seconds - 길이(초, 0 이하면 무시).
        // 출력: 반환값 없음. 그 종류 목소리의 끝 시각 계산이 바뀐다.
        public void SetDuration(SoundKind kind, float seconds)
        {
            if ((int)kind < _durations.Length && seconds > 0f) _durations[(int)kind] = seconds;
        }

        // 기능: 소리 요청 하나를 큐에 넣는다. 지난 Mix의 듣는 위치에서 종류의 거리보다 먼 3D 요청은 바로 버린다. 큐가 가득 차면 큐에서
        //   가장 덜 중요한 요청(우선순위 숫자가 크고, 같으면 먼 것)보다 새 요청이 더 중요할 때만 그것을 바꾼다.
        // 입력: e - 요청(종류가 Count 이상이면 버린다).
        // 출력: 큐에 들어갔으면 true. 거리 밖이면 false(DroppedDistance가 는다). 가득 찬 큐에서는 바꿔 넣었든 못 넣었든 하나를 잃으므로
        //   QueueOverflow가 늘고, 바꿔 넣었으면 true, 못 넣었으면 false(무한 증가 방지). 할당 없음.
        public bool Enqueue(in AudioEvent e)
        {
            if ((int)e.Kind >= (int)SoundKind.Count) return false;
            SoundInfo info = AudioCatalog.Info(e.Kind);
            float distance = QueuedDistance(e);
            if (e.Spatial && _hasListener && !(distance <= info.MaxDistance))
            {
                DroppedDistance++;
                return false;
            }
            if (_queued < QueueCapacity)
            {
                _queue[_queued++] = e;
                return true;
            }
            QueueOverflow++;
            int worst = 0;
            float worstDistance = QueuedDistance(_queue[0]);
            for (int i = 1; i < _queued; i++)
            {
                float d = QueuedDistance(_queue[i]);
                if (!IsBetter(AudioCatalog.Info(_queue[worst].Kind).Priority, worstDistance, AudioCatalog.Info(_queue[i].Kind).Priority, d)) continue;
                worst = i;
                worstDistance = d;
            }
            if (!IsBetter(info.Priority, distance, AudioCatalog.Info(_queue[worst].Kind).Priority, worstDistance)) return false;
            _queue[worst] = e;
            return true;
        }

        // 기능: 큐 비교에 쓸 요청의 거리(지난 Mix의 듣는 위치 기준)를 낸다.
        // 입력: e - 요청.
        // 출력: 3D이고 듣는 위치를 알면 거리, 2D이거나 Mix 전이면 0.
        private float QueuedDistance(in AudioEvent e) => e.Spatial && _hasListener ? Vector3.Distance(_listener, e.Position) : 0f;

        // 기능: 큐를 비우고 이번 프레임에 시작할 목소리를 정한다(위 1–4 규칙).
        // 입력: listener - 듣는 위치(카메라), now - 현재 시각(초, 계속 늘어나는 값).
        // 출력: 반환값 없음. StartCount·Start(i)·카운터·ActiveCount가 갱신되고 큐가 빈다. 듣는 위치는 다음 Enqueue의 거리 판단에 쓰인다. 할당 없음.
        public void Mix(Vector3 listener, float now)
        {
            _listener = listener;
            _hasListener = true;
            StartCount = 0;
            ExpireVoices(now);
            CollectCandidates(listener, now);
            SortCandidates();
            for (int c = 0; c < _candidateCount; c++)
            {
                if (StartCount >= MaxStartsPerFrame)
                {
                    DroppedBudget += _candidateCount - c;
                    break;
                }
                AudioEvent e = _candidates[c];
                SoundInfo info = AudioCatalog.Info(e.Kind);
                float distance = _candidateDistance[c];
                int voice = FindFreeVoice();
                if (voice < 0)
                {
                    voice = FindWorstVoice();
                    if (!IsBetter(info.Priority, distance, _voicePriority[voice], _voiceDistance[voice]))
                    {
                        DroppedBudget++;
                        continue;
                    }
                    DroppedBudget++;   // the voice cut off for this one
                }
                StartVoice(voice, e, info, distance, now);
            }
            _queued = 0;
            _candidateCount = 0;
            ActiveCount = CountActive();
        }

        // 기능: 대기 중인 요청을 모두 버린다(끊김, 경기 상태 정리). 재생 중인 목소리와 카운터는 그대로다.
        // 입력: 없음.
        // 출력: 반환값 없음. 큐가 빈다.
        public void ClearQueue() => _queued = 0;

        // 기능: 두 소리 중 새 소리가 더 중요한지 본다(교체 판단).
        // 입력: priority·distance - 새 소리, otherPriority·otherDistance - 재생 중인 소리.
        // 출력: 새 소리의 우선순위 숫자가 더 작거나, 같고 더 가까우면 true.
        public static bool IsBetter(byte priority, float distance, byte otherPriority, float otherDistance) =>
            priority < otherPriority || (priority == otherPriority && distance < otherDistance);

        // 기능: 끝난 목소리를 비운다.
        // 입력: now - 현재 시각.
        // 출력: 반환값 없음.
        private void ExpireVoices(float now)
        {
            for (int v = 0; v < VoiceBudget; v++)
            {
                if (_voiceActive[v] && now >= _voiceEnd[v]) _voiceActive[v] = false;
            }
        }

        // 기능: 큐의 요청을 거리·중복 규칙으로 걸러 후보 배열에 옮긴다. 통과한 요청은 바로 최근 재생 표에 오른다(같은 프레임의 중복도 막는다).
        // 입력: listener - 듣는 위치, now - 현재 시각.
        // 출력: 반환값 없음. 후보와 버림 카운터가 바뀐다.
        private void CollectCandidates(Vector3 listener, float now)
        {
            _candidateCount = 0;
            for (int i = 0; i < _queued; i++)
            {
                AudioEvent e = _queue[i];
                SoundInfo info = AudioCatalog.Info(e.Kind);
                float distance = 0f;
                if (e.Spatial)
                {
                    distance = Vector3.Distance(listener, e.Position);
                    if (!(distance <= info.MaxDistance))
                    {
                        DroppedDistance++;
                        continue;
                    }
                }
                if (IsDuplicate(e.Kind, e.Source, now, info.MinInterval))
                {
                    DroppedDuplicate++;
                    continue;
                }
                Remember(e.Kind, e.Source, now);
                _candidates[_candidateCount] = e;
                _candidateDistance[_candidateCount] = distance;
                _candidateCount++;
            }
        }

        // 기능: 후보를 우선순위(작은 숫자 먼저), 같으면 거리(가까운 것 먼저)로 정렬한다. 64개 이하라 삽입 정렬(할당 없음, 같은 값은 들어온 순서 유지).
        // 입력: 없음.
        // 출력: 반환값 없음. 후보 배열이 정렬된다.
        private void SortCandidates()
        {
            for (int i = 1; i < _candidateCount; i++)
            {
                AudioEvent e = _candidates[i];
                float d = _candidateDistance[i];
                byte p = AudioCatalog.Info(e.Kind).Priority;
                int j = i - 1;
                while (j >= 0 && IsBetter(p, d, AudioCatalog.Info(_candidates[j].Kind).Priority, _candidateDistance[j]))
                {
                    _candidates[j + 1] = _candidates[j];
                    _candidateDistance[j + 1] = _candidateDistance[j];
                    j--;
                }
                _candidates[j + 1] = e;
                _candidateDistance[j + 1] = d;
            }
        }

        // 기능: 같은 종류·같은 소스의 최근 재생이 최소 간격 안인지 본다.
        // 입력: kind - 종류, source - 소스, now - 현재 시각, interval - 최소 간격.
        // 출력: 간격 안이면 true.
        private bool IsDuplicate(SoundKind kind, uint source, float now, float interval)
        {
            for (int i = 0; i < _recentCount; i++)
            {
                if (_recentKind[i] == kind && _recentSource[i] == source && now - _recentTime[i] < interval) return true;
            }
            return false;
        }

        // 기능: 재생(후보 통과)을 최근 표에 적는다. 같은 쌍이 있으면 그 시각을 고친다. 없으면 빈 칸, 표가 가득 찼으면 중복 간격(시각 +
        //   그 종류의 MinInterval)이 이미 끝난 칸을 먼저, 그런 칸이 없으면 간격이 가장 먼저 끝나는 칸을 덮는다(아직 간격 안인 쌍이 덜 밀려나게).
        // 입력: kind - 종류, source - 소스, now - 현재 시각.
        // 출력: 반환값 없음.
        private void Remember(SoundKind kind, uint source, float now)
        {
            for (int i = 0; i < _recentCount; i++)
            {
                if (_recentKind[i] != kind || _recentSource[i] != source) continue;
                _recentTime[i] = now;
                return;
            }
            int slot;
            if (_recentCount < RecentCapacity) slot = _recentCount++;
            else
            {
                slot = 0;
                float slotEnds = float.MaxValue;
                for (int i = 0; i < RecentCapacity; i++)
                {
                    float ends = _recentTime[i] + AudioCatalog.Info(_recentKind[i]).MinInterval;
                    if (ends <= now)
                    {
                        slot = i;   // its duplicate window is over: it no longer matters
                        break;
                    }
                    if (ends >= slotEnds) continue;
                    slot = i;
                    slotEnds = ends;
                }
            }
            _recentKind[slot] = kind;
            _recentSource[slot] = source;
            _recentTime[slot] = now;
        }

        // 기능: 빈 목소리를 찾는다.
        // 입력: 없음.
        // 출력: 빈 목소리 번호, 없으면 -1.
        private int FindFreeVoice()
        {
            for (int v = 0; v < VoiceBudget; v++)
            {
                if (!_voiceActive[v]) return v;
            }
            return -1;
        }

        // 기능: 가장 덜 중요한 재생 중 목소리(우선순위 숫자가 가장 크고, 같으면 가장 먼)를 찾는다. 모두 재생 중일 때만 부른다.
        // 입력: 없음.
        // 출력: 목소리 번호.
        private int FindWorstVoice()
        {
            int worst = 0;
            for (int v = 1; v < VoiceBudget; v++)
            {
                if (IsBetter(_voicePriority[worst], _voiceDistance[worst], _voicePriority[v], _voiceDistance[v])) worst = v;
            }
            return worst;
        }

        // 기능: 목소리 하나를 시작 명령으로 적고 재생 중으로 표시한다(음높이 ±PitchJitter, 변형은 결정적 난수).
        // 입력: voice - 목소리 번호, e - 요청, info - 그 종류의 표 줄, distance - 듣는 위치까지 거리(2D는 0), now - 현재 시각.
        // 출력: 반환값 없음. StartCount와 종류별 재생 수가 는다.
        private void StartVoice(int voice, in AudioEvent e, in SoundInfo info, float distance, float now)
        {
            float pitch = 1f + (NextUnit() * 2f - 1f) * PitchJitter;
            int variants = Math.Max(1, (int)info.Variants);
            int variant = (int)(NextUnit() * variants);
            if (variant >= variants) variant = variants - 1;
            float scale = e.VolumeScale > 0f ? e.VolumeScale : 1f;
            _voiceActive[voice] = true;
            _voiceEnd[voice] = now + _durations[(int)e.Kind] / pitch;
            _voicePriority[voice] = info.Priority;
            _voiceDistance[voice] = distance;
            _starts[StartCount++] = new AudioStart
            {
                Voice = voice,
                Kind = e.Kind,
                Variant = variant,
                Pitch = pitch,
                Volume = info.Volume * scale * AudioCatalog.MasterVolume,
                Spatial = e.Spatial,
                Position = e.Position,
                MaxDistance = info.MaxDistance,
            };
            _plays[(int)e.Kind]++;
        }

        // 기능: 재생 중 목소리 수를 센다.
        // 입력: 없음.
        // 출력: 재생 중 목소리 수(0–VoiceBudget).
        private int CountActive()
        {
            int count = 0;
            for (int v = 0; v < VoiceBudget; v++)
            {
                if (_voiceActive[v]) count++;
            }
            return count;
        }

        // 기능: 0 이상 1 미만의 결정적 난수(xorshift32).
        // 입력: 없음.
        // 출력: 난수.
        private float NextUnit()
        {
            uint x = _random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _random = x;
            return (x >> 8) * (1f / 16777216f);
        }
    }
}
