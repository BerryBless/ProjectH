using System;
using UnityEngine;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D1: the clips of every sound kind (AudioCatalog's variants), synthesized once at construction (AudioSynth) and
    // destroyed in Dispose. GameAudio owns it. Main thread only.
    public sealed class AudioClipBank : IDisposable
    {
        private readonly AudioClip[][] _clips = new AudioClip[(int)SoundKind.Count][];
        private readonly float[] _longest = new float[(int)SoundKind.Count];

        // 기능: 모든 종류·변형의 클립을 합성해 AudioClip으로 만든다(시작 때 한 번, 할당한다).
        // 입력: 없음.
        // 출력: 클립이 준비된 bank. Dispose가 모두 파괴한다.
        public AudioClipBank()
        {
            for (int k = 0; k < _clips.Length; k++)
            {
                var kind = (SoundKind)k;
                int variants = Math.Max(1, (int)AudioCatalog.Info(kind).Variants);
                _clips[k] = new AudioClip[variants];
                for (int v = 0; v < variants; v++)
                {
                    float[] samples = AudioSynth.Render(kind, v);
                    AudioClip clip = AudioClip.Create(kind + "_" + v, samples.Length, 1, AudioSynth.SampleRate, false);
                    clip.SetData(samples, 0);
                    _clips[k][v] = clip;
                    _longest[k] = Mathf.Max(_longest[k], samples.Length / (float)AudioSynth.SampleRate);
                }
            }
        }

        // 기능: 종류·변형의 클립을 돌려준다.
        // 입력: kind - 소리 종류, variant - 변형 번호(범위 밖이면 0번).
        // 출력: 클립. Dispose 뒤나 범위 밖 종류면 null.
        public AudioClip Get(SoundKind kind, int variant)
        {
            if ((int)kind >= _clips.Length) return null;
            AudioClip[] list = _clips[(int)kind];
            if (list == null || list.Length == 0) return null;
            return list[variant >= 0 && variant < list.Length ? variant : 0];
        }

        // 기능: 종류의 가장 긴 변형 길이를 돌려준다(믹서가 목소리 끝 시각을 계산한다).
        // 입력: kind - 소리 종류.
        // 출력: 초.
        public float LongestSeconds(SoundKind kind) => (int)kind < _longest.Length ? _longest[(int)kind] : 0f;

        // 기능: 만든 클립을 모두 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 두 번 불러도 안전하다.
        public void Dispose()
        {
            for (int k = 0; k < _clips.Length; k++)
            {
                AudioClip[] list = _clips[k];
                if (list == null) continue;
                for (int v = 0; v < list.Length; v++)
                {
                    if (list[v] != null) UnityEngine.Object.Destroy(list[v]);
                }
                _clips[k] = null;
            }
        }
    }
}
