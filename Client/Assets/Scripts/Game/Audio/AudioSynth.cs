using System;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D1: the clips are synthesized at startup instead of shipped as assets. Each kind is a recipe of a filtered noise
    // burst and a decaying (swept) sine, optionally repeated a few times (clicks, rumbles); its variants nudge the numbers and
    // the noise seed. Deterministic: the same kind and variant always give the same samples (own xorshift, never
    // System.Random, whose sequence is not promised across runtimes). Pure (no UnityEngine): the EditMode tests render clips
    // without the Unity audio API; AudioClipBank wraps the result in AudioClip.Create.
    public static class AudioSynth
    {
        public const int SampleRate = 22050;
        public const float Peak = 0.9f;
        private const float FadeOutSeconds = 0.005f;

        // One kind's sound. Rates are per second; LowPass is the one-pole filter's coefficient (0..1, small = dull).
        private struct Recipe
        {
            public float Seconds;
            public float Attack;
            public float NoiseAmp;
            public float NoiseDecay;
            public float LowPass;
            public float ToneAmp;
            public float ToneStart;
            public float ToneEnd;
            public float ToneDecay;
            public int Hits;
            public float HitSpacing;
        }

        // 기능: 종류·변형의 클립 길이(샘플 수)를 낸다.
        // 입력: kind - 소리 종류, variant - 변형 번호(0부터, 표의 변형 수로 자른다).
        // 출력: 샘플 수(1 이상).
        public static int Length(SoundKind kind, int variant)
        {
            Recipe r = Varied(RecipeOf(kind), variant);
            return Math.Max(1, (int)(r.Seconds * SampleRate));
        }

        // 기능: 종류·변형의 클립을 합성한다(모노, SampleRate). 같은 입력이면 항상 같은 샘플이다.
        // 입력: kind - 소리 종류, variant - 변형 번호(0부터, 표의 변형 수로 자른다).
        // 출력: 새 float 배열(-Peak..Peak, 끝 5 ms는 0으로 줄어든다). 시작 때 종류·변형마다 한 번만 부른다(할당한다).
        public static float[] Render(SoundKind kind, int variant)
        {
            int variants = Math.Max(1, (int)AudioCatalog.Info(kind).Variants);
            variant = Math.Max(0, Math.Min(variant, variants - 1));
            Recipe r = Varied(RecipeOf(kind), variant);
            int length = Math.Max(1, (int)(r.Seconds * SampleRate));
            var samples = new float[length];
            uint state = Seed(kind, variant);
            float filtered = 0f;
            double phase = 0.0;
            int hits = Math.Max(1, r.Hits);
            float attack = Math.Max(1e-4f, r.Attack);
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)SampleRate;
                state = Next(state);
                float white = (state >> 8) * (1f / 16777216f) * 2f - 1f;
                filtered += r.LowPass * (white - filtered);
                float sweep = Math.Min(1f, t / r.Seconds);
                float frequency = r.ToneStart + (r.ToneEnd - r.ToneStart) * sweep;
                phase += 2.0 * Math.PI * frequency / SampleRate;
                float tone = (float)Math.Sin(phase);
                float sum = 0f;
                for (int k = 0; k < hits; k++)
                {
                    float local = t - k * r.HitSpacing;
                    if (local < 0f) break;
                    float rise = Math.Min(1f, local / attack);
                    float noise = r.NoiseAmp * filtered * (float)Math.Exp(-local * r.NoiseDecay);
                    float ring = r.ToneAmp * tone * (float)Math.Exp(-local * r.ToneDecay);
                    sum += rise * (noise + ring);
                }
                samples[i] = sum;
            }
            Finish(samples);
            return samples;
        }

        // 기능: 끝을 0으로 줄이고(클릭 방지) 최대 진폭을 Peak로 맞춘다.
        // 입력: samples - 합성한 샘플.
        // 출력: 반환값 없음. samples가 바뀐다. 모두 0이면 그대로 둔다.
        private static void Finish(float[] samples)
        {
            int fade = Math.Min(samples.Length, (int)(FadeOutSeconds * SampleRate));
            for (int i = 0; i < fade; i++) samples[samples.Length - 1 - i] *= i / (float)fade;
            float max = 0f;
            for (int i = 0; i < samples.Length; i++) max = Math.Max(max, Math.Abs(samples[i]));
            if (max <= 0f) return;
            float scale = Peak / max;
            for (int i = 0; i < samples.Length; i++) samples[i] *= scale;
        }

        // 기능: 변형마다 수치를 조금씩 바꾼다(반복감 줄이기, D1).
        // 입력: r - 기본 수치, variant - 변형 번호.
        // 출력: 바뀐 수치(변형 0은 기본 그대로).
        private static Recipe Varied(Recipe r, int variant)
        {
            if (variant <= 0) return r;
            float k = variant == 1 ? 1.07f : 0.93f;
            r.ToneStart *= k;
            r.ToneEnd *= k;
            r.NoiseDecay *= 2f - k;
            r.LowPass = Math.Min(1f, r.LowPass * k);
            return r;
        }

        // 기능: 종류·변형의 잡음 시드를 만든다(0이 되지 않는다).
        // 입력: kind - 소리 종류, variant - 변형 번호.
        // 출력: xorshift 시작 값.
        private static uint Seed(SoundKind kind, int variant) => (uint)(((int)kind + 1) * 7919 + (variant + 1) * 104729) | 1u;

        // 기능: xorshift32 한 걸음.
        // 입력: x - 지금 상태(0 아님).
        // 출력: 다음 상태.
        private static uint Next(uint x)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }

        // 기능: 종류의 기본 수치를 돌려준다(D1: 잡음 버스트·감쇠 사인·필터한 잡음의 조합. Phase 19: 차량 타기·내리기는 문 수치, 충돌은 금속 소리).
        // 입력: kind - 소리 종류.
        // 출력: 수치. 표에 없는 값이면 짧은 딸깍 소리.
        private static Recipe RecipeOf(SoundKind kind)
        {
            switch (kind)
            {
                case SoundKind.GunAR: return R(0.35f, 0.001f, 1f, 18f, 0.5f, 0.5f, 160f, 60f, 25f);
                case SoundKind.GunSMG: return R(0.25f, 0.001f, 1f, 26f, 0.6f, 0.4f, 220f, 90f, 35f);
                case SoundKind.GunShotgun: return R(0.5f, 0.001f, 1f, 10f, 0.35f, 0.6f, 110f, 45f, 14f);
                case SoundKind.GunSniper: return R(0.8f, 0.001f, 1f, 7f, 0.45f, 0.7f, 120f, 40f, 8f);
                case SoundKind.GunPistol: return R(0.25f, 0.001f, 0.9f, 24f, 0.55f, 0.45f, 260f, 110f, 30f);
                case SoundKind.RocketLaunch: return R(0.9f, 0.03f, 1f, 3.5f, 0.15f, 0.4f, 90f, 200f, 3f);
                case SoundKind.GrenadeThrow: return R(0.3f, 0.08f, 0.5f, 12f, 0.08f, 0.2f, 300f, 500f, 10f);
                case SoundKind.Explosion: return R(1.4f, 0.002f, 1f, 3f, 0.06f, 0.8f, 70f, 30f, 3f);

                case SoundKind.StepGroundWalk: return R(0.12f, 0.003f, 1f, 45f, 0.08f, 0.3f, 90f, 60f, 50f);
                case SoundKind.StepGroundSprint: return R(0.12f, 0.002f, 1f, 40f, 0.12f, 0.35f, 100f, 60f, 45f);
                case SoundKind.StepGroundCrouch: return R(0.1f, 0.006f, 1f, 55f, 0.06f, 0.2f, 80f, 60f, 60f);
                case SoundKind.StepStoneWalk: return R(0.12f, 0.002f, 1f, 60f, 0.35f, 0.2f, 400f, 300f, 70f);
                case SoundKind.StepStoneSprint: return R(0.12f, 0.001f, 1f, 55f, 0.45f, 0.25f, 420f, 300f, 65f);
                case SoundKind.StepStoneCrouch: return R(0.1f, 0.005f, 0.8f, 70f, 0.25f, 0.15f, 380f, 300f, 80f);
                case SoundKind.StepWoodWalk: return R(0.15f, 0.002f, 0.6f, 45f, 0.2f, 0.6f, 180f, 150f, 35f);
                case SoundKind.StepWoodSprint: return R(0.15f, 0.001f, 0.7f, 40f, 0.25f, 0.7f, 190f, 150f, 32f);
                case SoundKind.StepWoodCrouch: return R(0.12f, 0.005f, 0.5f, 55f, 0.15f, 0.4f, 170f, 150f, 45f);
                case SoundKind.StepMetalWalk: return R(0.25f, 0.001f, 0.6f, 50f, 0.5f, 0.5f, 900f, 880f, 18f);
                case SoundKind.StepMetalSprint: return R(0.25f, 0.001f, 0.7f, 45f, 0.55f, 0.6f, 920f, 880f, 16f);
                case SoundKind.StepMetalCrouch: return R(0.2f, 0.004f, 0.5f, 60f, 0.4f, 0.35f, 860f, 850f, 22f);
                case SoundKind.Slide: return R(0.45f, 0.05f, 0.8f, 4f, 0.1f, 0f, 0f, 0f, 1f);
                case SoundKind.DownedDrag: return R(0.5f, 0.1f, 0.7f, 3f, 0.05f, 0f, 0f, 0f, 1f);

                case SoundKind.BuildPlace: return R(0.25f, 0.002f, 0.5f, 30f, 0.25f, 0.7f, 220f, 180f, 20f, 2, 0.08f);
                case SoundKind.BuildEdit: return R(0.18f, 0.002f, 0.3f, 40f, 0.3f, 0.6f, 500f, 420f, 30f);
                case SoundKind.BuildDamage: return R(0.2f, 0.001f, 0.8f, 35f, 0.3f, 0.4f, 160f, 120f, 30f);
                case SoundKind.BuildDestroy: return R(0.6f, 0.001f, 1f, 7f, 0.2f, 0.5f, 120f, 50f, 8f, 3, 0.07f);
                case SoundKind.BuildCollapse: return R(1f, 0.002f, 1f, 4f, 0.1f, 0.6f, 80f, 35f, 4f, 4, 0.12f);
                case SoundKind.BuildRefused: return R(0.22f, 0.002f, 0.1f, 20f, 0.5f, 0.8f, 220f, 180f, 10f, 2, 0.1f);
                case SoundKind.HarvestSwing: return R(0.22f, 0.05f, 0.6f, 15f, 0.15f, 0f, 0f, 0f, 1f);
                case SoundKind.HarvestHit: return R(0.2f, 0.001f, 0.7f, 35f, 0.25f, 0.6f, 200f, 170f, 30f);
                case SoundKind.HarvestWeakPoint: return R(0.25f, 0.001f, 0.4f, 40f, 0.4f, 0.8f, 880f, 860f, 15f);
                case SoundKind.HarvestDestroyed: return R(0.6f, 0.002f, 1f, 6f, 0.15f, 0.5f, 140f, 60f, 6f);

                case SoundKind.HealthHit: return R(0.2f, 0.001f, 0.6f, 30f, 0.1f, 0.7f, 120f, 80f, 20f);
                case SoundKind.ShieldHit: return R(0.18f, 0.001f, 0.4f, 40f, 0.6f, 0.6f, 1200f, 1000f, 25f);
                case SoundKind.ShieldBreak: return R(0.5f, 0.001f, 0.8f, 9f, 0.7f, 0.7f, 1500f, 400f, 6f);
                case SoundKind.HitMarker: return R(0.07f, 0.001f, 0f, 1f, 0f, 1f, 1800f, 1800f, 60f);
                case SoundKind.KillConfirm: return R(0.3f, 0.001f, 0f, 1f, 0f, 1f, 1200f, 1800f, 10f, 2, 0.09f);
                case SoundKind.SelfDowned: return R(0.8f, 0.01f, 0.2f, 3f, 0.05f, 0.9f, 400f, 120f, 3f);
                case SoundKind.SelfDied: return R(1f, 0.01f, 0f, 1f, 0f, 0.9f, 300f, 60f, 2.5f);
                case SoundKind.Reload: return R(0.35f, 0.001f, 0.7f, 50f, 0.5f, 0.3f, 1500f, 1500f, 60f, 2, 0.2f);

                case SoundKind.Pickup: return R(0.15f, 0.002f, 0f, 1f, 0f, 0.9f, 700f, 1100f, 18f);
                case SoundKind.ContainerOpen: return R(0.4f, 0.01f, 0.5f, 10f, 0.15f, 0.5f, 260f, 330f, 8f);
                case SoundKind.SupplyDropLand: return R(0.9f, 0.002f, 1f, 5f, 0.07f, 0.8f, 60f, 35f, 5f);
                case SoundKind.Reboot: return R(0.7f, 0.02f, 0f, 1f, 0f, 0.8f, 400f, 900f, 3f);
                case SoundKind.DoorOpen: return R(0.45f, 0.02f, 0.3f, 6f, 0.1f, 0.5f, 180f, 260f, 5f);
                case SoundKind.DoorClose: return R(0.35f, 0.002f, 0.6f, 20f, 0.15f, 0.5f, 140f, 90f, 9f);

                case SoundKind.ZoneWarning: return R(0.9f, 0.01f, 0f, 1f, 0f, 0.9f, 660f, 660f, 2f, 2, 0.3f);
                case SoundKind.ZoneDamage: return R(0.25f, 0.005f, 0.4f, 15f, 0.3f, 0.6f, 90f, 70f, 12f);

                case SoundKind.UiClick: return R(0.05f, 0.0005f, 0.3f, 120f, 0.6f, 0.7f, 1400f, 1400f, 90f);
                case SoundKind.MapOpen: return R(0.18f, 0.02f, 0.5f, 14f, 0.4f, 0.2f, 600f, 900f, 15f);
                case SoundKind.MapClose: return R(0.15f, 0.02f, 0.5f, 14f, 0.4f, 0.2f, 900f, 600f, 15f);

                // Phase 19 D11: the door recipes (no vehicle assets yet), and a dull metal crunch for an impact.
                case SoundKind.VehicleEnter: return R(0.45f, 0.02f, 0.3f, 6f, 0.1f, 0.5f, 180f, 260f, 5f);
                case SoundKind.VehicleExit: return R(0.35f, 0.002f, 0.6f, 20f, 0.15f, 0.5f, 140f, 90f, 9f);
                case SoundKind.VehicleImpact: return R(0.5f, 0.001f, 1f, 9f, 0.25f, 0.6f, 150f, 60f, 10f, 2, 0.05f);
                default: return R(0.05f, 0.0005f, 0.3f, 120f, 0.6f, 0.7f, 1000f, 1000f, 90f);
            }
        }

        // 기능: 수치 묶음을 만든다(줄을 짧게 쓰기 위한 도우미).
        // 입력: seconds - 길이, attack - 올라가는 시간, noiseAmp·noiseDecay·lowPass - 잡음 크기·감쇠·필터, toneAmp·toneStart·toneEnd·toneDecay
        //   - 사인 크기·시작/끝 주파수·감쇠, hits·hitSpacing - 반복 횟수·간격(초).
        // 출력: 수치 묶음.
        private static Recipe R(float seconds, float attack, float noiseAmp, float noiseDecay, float lowPass, float toneAmp, float toneStart,
            float toneEnd, float toneDecay, int hits = 1, float hitSpacing = 0f) => new Recipe
        {
            Seconds = seconds,
            Attack = attack,
            NoiseAmp = noiseAmp,
            NoiseDecay = noiseDecay,
            LowPass = lowPass,
            ToneAmp = toneAmp,
            ToneStart = toneStart,
            ToneEnd = toneEnd,
            ToneDecay = toneDecay,
            Hits = hits,
            HitSpacing = hitSpacing,
        };
    }
}
