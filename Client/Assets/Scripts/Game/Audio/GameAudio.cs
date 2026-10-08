using System;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 (Docs/Audio.md): the client's sound system. GameClient owns one (created in Awake, disposed in OnDestroy) and
    // turns game events into requests: Play3D / Play2D put them into the mixer's fixed queue, the footsteps are computed in
    // TickFootsteps, and Tick (once per frame, after every event of the frame) mixes and starts the chosen voices. The clip
    // bank, the voice pool and the change trackers live and die with this object. Main thread only.
    public sealed class GameAudio : IDisposable
    {
        // The QA names of the kinds (index = SoundKind), built once.
        public static readonly string[] KindNames = BuildKindNames();

        private readonly AudioClipBank _bank;
        private readonly AudioVoicePool _pool;
        private readonly AudioMixerModel _mixer = new AudioMixerModel();
        private readonly FootstepModel _footsteps = new FootstepModel();
        private readonly CollapseCollector _collapse = new CollapseCollector();
        private readonly RemotePose[] _poses = new RemotePose[FootstepModel.Capacity];
        private NVector3 _listener;
        private bool _disposed;

        // Change trackers GameClient feeds (D9 baseline rule). Reset with the match state.
        public DoorSoundTracker Doors { get; } = new DoorSoundTracker();
        public ContainerSoundTracker Containers { get; } = new ContainerSoundTracker();
        public SupplyDropSoundTracker SupplyDrops { get; } = new SupplyDropSoundTracker();
        public ZoneShrinkWatch ZoneShrink { get; } = new ZoneShrinkWatch();
        public ZoneDamageTicker ZoneDamage { get; } = new ZoneDamageTicker();
        public RisingEdge Reload { get; } = new RisingEdge();
        public FootstepModel Footsteps => _footsteps;
        public AudioMixerModel Mixer => _mixer;

        // 기능: 클립을 합성하고 목소리 풀을 만든다(시작 때 한 번).
        // 입력: 없음.
        // 출력: 소리를 낼 준비가 된 시스템. Dispose가 클립과 GameObject를 파괴한다.
        public GameAudio()
        {
            _bank = new AudioClipBank();
            _pool = new AudioVoicePool();
            for (int k = 0; k < (int)SoundKind.Count; k++) _mixer.SetDuration((SoundKind)k, _bank.LongestSeconds((SoundKind)k));
        }

        // 기능: 3D 소리 하나를 요청한다.
        // 입력: kind - 종류, position - 위치, source - 소스(같은 소스·종류의 중복 판단, 0 = 없음).
        // 출력: 반환값 없음. 큐가 가득 차면 버리고 센다.
        public void Play3D(SoundKind kind, Vector3 position, uint source)
        {
            if (_disposed) return;
            _mixer.Enqueue(new AudioEvent { Kind = kind, Spatial = true, Position = new NVector3(position.x, position.y, position.z), Source = source });
        }

        // 기능: 2D 소리 하나를 요청한다(내 몸·UI·자기장).
        // 입력: kind - 종류, source - 소스(0 = 없음), volumeScale - 표 음량에 곱할 값(1 = 그대로).
        // 출력: 반환값 없음. 큐가 가득 차면 버리고 센다.
        public void Play2D(SoundKind kind, uint source = 0, float volumeScale = 1f)
        {
            if (_disposed) return;
            _mixer.Enqueue(new AudioEvent { Kind = kind, Spatial = false, Source = source, VolumeScale = volumeScale });
        }

        // 기능: 무너진 조각 하나를 붕괴 소리 후보로 더한다(D6: 한 프레임에 여러 개여도 가장 가까운 한 번).
        // 입력: position - 조각 중심.
        // 출력: 반환값 없음. 다음 Tick이 가장 가까운 하나를 요청한다.
        public void AddCollapse(Vector3 position) => _collapse.Add(new NVector3(position.x, position.y, position.z), _listener);

        // 기능: 이번 프레임의 발소리를 계산한다(D5): 내 발(예측, 2D·작게)과 듣는 위치 30 m 안의 원격 플레이어(3D). 발밑 재질은 소리를 낼 때만 찾는다.
        //   Phase 19 D15: 차량에 앉은 사람(나는 예측기의 Seated, 원격은 렌더 Tick의 Seated)은 발소리가 없다(FootstepModel.Audible).
        // 입력: remotes - 원격 플레이어, renderTick - 원격을 그리는 Tick, myId - 내 Entity id, predictor - 내 예측(null 가능),
        //   store - 확정 조각, listener - 듣는 위치, now - 현재 시각, deltaTime - 프레임 시간.
        // 출력: 반환값 없음. 발소리 요청이 큐에 들어간다. 할당 없음.
        public void TickFootsteps(RemotePlayers remotes, double renderTick, ushort myId, LocalPlayerPredictor predictor, BuildStore store,
            Vector3 listener, float now, float deltaTime)
        {
            if (_disposed) return;
            _footsteps.BeginFrame();
            if (predictor != null)
            {
                Vector3 feet = predictor.RenderPosition;
                FootstepGait gait = _footsteps.Sample(myId, ToNumerics(feet), predictor.Mode, predictor.Sprinting,
                    FootstepModel.Audible(!predictor.IsDead, predictor.Seated), now, deltaTime, predictor.HorizontalSpeed);
                if (gait != FootstepGait.None && SurfaceProbe.TryFind(ToNumerics(feet), store, out SurfaceMaterial surface))
                    Play2D(AudioCatalog.FootstepFor(gait, surface), myId, AudioCatalog.OwnFootstepVolumeScale);
            }
            int count = remotes.CollectPoses(renderTick, _poses);
            float rangeSq = AudioCatalog.FootstepComputeDistance * AudioCatalog.FootstepComputeDistance;
            for (int i = 0; i < count; i++)
            {
                RemotePose pose = _poses[i];
                if ((pose.Feet - listener).sqrMagnitude > rangeSq) continue;   // not sampled: its slot is freed (EndFrame)
                FootstepGait gait = _footsteps.Sample(pose.EntityId, ToNumerics(pose.Feet), pose.Mode, pose.Sprinting,
                    FootstepModel.Audible(pose.Alive, pose.Seated), now, deltaTime);
                if (gait == FootstepGait.None || !SurfaceProbe.TryFind(ToNumerics(pose.Feet), store, out SurfaceMaterial surface)) continue;
                Play3D(AudioCatalog.FootstepFor(gait, surface), pose.Feet, pose.EntityId);
            }
            _footsteps.EndFrame();
        }

        // 기능: 이번 프레임의 요청을 섞고 고른 목소리를 시작한다(LateUpdate 끝에서 한 번, 모든 이벤트 뒤).
        // 입력: listener - 듣는 위치(AudioListener가 있는 카메라), now - 현재 시각.
        // 출력: 반환값 없음. 붕괴 후보가 있으면 가장 가까운 하나가 먼저 요청되고, 최대 MaxStartsPerFrame개 목소리가 시작된다. 할당 없음.
        public void Tick(Vector3 listener, float now)
        {
            if (_disposed) return;
            _listener = ToNumerics(listener);
            if (_collapse.TryTake(out NVector3 collapsed))
                _mixer.Enqueue(new AudioEvent { Kind = SoundKind.BuildCollapse, Spatial = true, Position = collapsed });
            _mixer.Mix(_listener, now);
            for (int i = 0; i < _mixer.StartCount; i++)
            {
                AudioStart start = _mixer.Start(i);
                _pool.Play(start, _bank.Get(start.Kind, start.Variant));
            }
        }

        // 기능: 경기 상태가 지워질 때(끊김) 대기 요청과 발소리·변화 추적 상태를 지운다. 재생 중인 소리는 끝까지 간다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void ResetMatch()
        {
            _mixer.ClearQueue();
            _footsteps.Clear();
            _collapse.TryTake(out _);
            Doors.Reset();
            Containers.Reset();
            SupplyDrops.Reset();
            ZoneShrink.Reset();
            ZoneDamage.Update(false, 0f);
            Reload.Reset();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 기능: /qa/status의 audio 필드를 모은다(D12).
        // 입력: 없음.
        // 출력: 종류별 재생 수(배열을 그대로 넘긴다, 복사 없음)와 재생 중·버림 카운터.
        public Qa.QaAudioStatus QaStatus => new Qa.QaAudioStatus
        {
            KindNames = KindNames,
            Plays = _mixer.Plays,
            Active = _mixer.ActiveCount,
            DroppedBudget = _mixer.DroppedBudget,
            DroppedDuplicate = _mixer.DroppedDuplicate,
            DroppedDistance = _mixer.DroppedDistance,
            QueueOverflow = _mixer.QueueOverflow,
        };
#endif

        // 기능: 클립과 목소리 GameObject를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 이후 요청은 무시된다. 두 번 불러도 안전하다.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pool.Dispose();
            _bank.Dispose();
        }

        // 기능: Unity 벡터를 System.Numerics 벡터로 바꾼다.
        // 입력: v - Unity 벡터.
        // 출력: 같은 값의 System.Numerics 벡터.
        private static NVector3 ToNumerics(Vector3 v) => new NVector3(v.x, v.y, v.z);

        // 기능: 종류 이름 표를 만든다(QA JSON 키, 시작 때 한 번).
        // 입력: 없음.
        // 출력: SoundKind.Count칸 문자열 배열.
        private static string[] BuildKindNames()
        {
            var names = new string[(int)SoundKind.Count];
            for (int k = 0; k < names.Length; k++) names[k] = ((SoundKind)k).ToString();
            return names;
        }
    }
}
