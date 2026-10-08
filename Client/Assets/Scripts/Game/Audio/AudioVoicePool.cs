using System;
using UnityEngine;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D2: the fixed AudioSources the mixer plays on (AudioMixerModel.VoiceBudget, all created once). A start stops
    // whatever its voice was playing (the mixer decided to replace it) and plays the clip once: clip + Play, not PlayOneShot,
    // so a replaced voice really stops. 3D voices: spatialBlend 1, Linear rolloff to the kind's MaxDistance; 2D voices:
    // spatialBlend 0. Doppler off (the shoulder camera moves; the ±5 % pitch is the only pitch change). The root GameObject
    // and its sources are destroyed in Dispose. GameAudio owns it. Main thread only.
    public sealed class AudioVoicePool : IDisposable
    {
        private const float MinDistance = 1f;

        private readonly GameObject _root;
        private readonly AudioSource[] _sources = new AudioSource[AudioMixerModel.VoiceBudget];
        private readonly Transform[] _transforms = new Transform[AudioMixerModel.VoiceBudget];

        // 기능: 목소리 GameObject와 AudioSource를 예산 수만큼 만든다(한 번).
        // 입력: 없음.
        // 출력: 아무것도 재생하지 않는 풀. Dispose가 뿌리째 파괴한다.
        public AudioVoicePool()
        {
            _root = new GameObject("Audio Voices");
            for (int i = 0; i < _sources.Length; i++)
            {
                var go = new GameObject("Voice " + i);
                go.transform.SetParent(_root.transform, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = MinDistance;
                source.spatialBlend = 0f;
                _sources[i] = source;
                _transforms[i] = go.transform;
            }
        }

        // 기능: 믹서의 시작 명령 하나를 실행한다.
        // 입력: start - 시작 명령, clip - 그 종류·변형의 클립(null이면 목소리만 멈춘다).
        // 출력: 반환값 없음. 그 목소리의 이전 소리는 멈춘다. 할당 없음.
        public void Play(in AudioStart start, AudioClip clip)
        {
            if (start.Voice < 0 || start.Voice >= _sources.Length) return;
            AudioSource source = _sources[start.Voice];
            if (source == null) return;
            source.Stop();
            if (clip == null) return;
            source.clip = clip;
            source.pitch = start.Pitch;
            source.volume = start.Volume;
            source.spatialBlend = start.Spatial ? 1f : 0f;
            source.maxDistance = Mathf.Max(MinDistance + 0.01f, start.MaxDistance);
            if (start.Spatial) _transforms[start.Voice].position = new Vector3(start.Position.X, start.Position.Y, start.Position.Z);
            source.Play();
        }

        // 기능: 모든 목소리를 멈추고 GameObject를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 두 번 불러도 안전하다.
        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            Array.Clear(_sources, 0, _sources.Length);
            Array.Clear(_transforms, 0, _transforms.Length);
        }
    }
}
