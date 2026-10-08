using NUnit.Framework;
using ProjectH.Client.Game.Audio;
using Vector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // Phase 18 D2, D3: the mixer's rules: queue limit, distance drop, duplicate interval, priority and distance order, the
    // voice budget with replacement, the per-frame start cap, and the counters QA reads.
    public class AudioMixerModelTests
    {
        // 기능: x축 위치의 3D 요청을 만든다.
        // 입력: kind - 종류, x - 듣는 위치(원점)에서의 거리, source - 소스.
        // 출력: 요청.
        private static AudioEvent At(SoundKind kind, float x, uint source = 0) =>
            new AudioEvent { Kind = kind, Spatial = true, Position = new Vector3(x, 0f, 0f), Source = source };

        // 기능: 2D 요청을 만든다.
        // 입력: kind - 종류, source - 소스.
        // 출력: 요청.
        private static AudioEvent Flat(SoundKind kind, uint source = 0) => new AudioEvent { Kind = kind, Spatial = false, Source = source };

        [Test]
        public void Queue_IsBounded_OverflowIsCounted()
        {
            var mixer = new AudioMixerModel();
            for (int i = 0; i < AudioMixerModel.QueueCapacity; i++) Assert.IsTrue(mixer.Enqueue(Flat(SoundKind.UiClick, (uint)i)));
            Assert.IsFalse(mixer.Enqueue(Flat(SoundKind.UiClick, 999)));
            Assert.AreEqual(1, mixer.QueueOverflow);
            Assert.AreEqual(AudioMixerModel.QueueCapacity, mixer.Queued);
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(0, mixer.Queued);
        }

        [Test]
        public void BeyondTheKindsDistance_IsDropped_2DIsNever()
        {
            var mixer = new AudioMixerModel();
            mixer.Enqueue(At(SoundKind.GunAR, AudioCatalog.GunshotDistance - 1f, 1));
            mixer.Enqueue(At(SoundKind.GunAR, AudioCatalog.GunshotDistance + 1f, 2));
            mixer.Enqueue(At(SoundKind.StepGroundCrouch, AudioCatalog.CrouchDistance + 0.5f, 3));
            mixer.Enqueue(At(SoundKind.GunSniper, AudioCatalog.GunshotDistance + 10f, 4));   // the sniper carries 150 m
            mixer.Enqueue(Flat(SoundKind.HealthHit));
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(2, mixer.DroppedDistance);
            Assert.AreEqual(3, mixer.StartCount);
            Assert.AreEqual(1, mixer.Plays[(int)SoundKind.GunAR]);
            Assert.AreEqual(1, mixer.Plays[(int)SoundKind.GunSniper]);
            Assert.AreEqual(1, mixer.Plays[(int)SoundKind.HealthHit]);
        }

        [Test]
        public void SameKindAndSource_WithinItsInterval_IsADuplicate()
        {
            var mixer = new AudioMixerModel();
            mixer.Enqueue(At(SoundKind.BuildDamage, 5f, 7));
            mixer.Enqueue(At(SoundKind.BuildDamage, 5f, 7));   // same frame
            mixer.Enqueue(At(SoundKind.BuildDamage, 5f, 8));   // another piece
            mixer.Mix(Vector3.Zero, 10f);
            Assert.AreEqual(2, mixer.StartCount);
            Assert.AreEqual(1, mixer.DroppedDuplicate);

            mixer.Enqueue(At(SoundKind.BuildDamage, 5f, 7));
            mixer.Mix(Vector3.Zero, 10.1f);   // 0.1 s < 0.15 s
            Assert.AreEqual(0, mixer.StartCount);
            Assert.AreEqual(2, mixer.DroppedDuplicate);

            mixer.Enqueue(At(SoundKind.BuildDamage, 5f, 7));
            mixer.Mix(Vector3.Zero, 10.2f);   // 0.2 s after the last play
            Assert.AreEqual(1, mixer.StartCount);
        }

        [Test]
        public void SmgFireRate_IsNotADuplicate()
        {
            var mixer = new AudioMixerModel();
            int played = 0;
            for (int i = 0; i < 10; i++)
            {
                mixer.Enqueue(At(SoundKind.GunSMG, 10f, 3));
                mixer.Mix(Vector3.Zero, i * 0.0667f);
                played += mixer.StartCount;
            }
            Assert.AreEqual(10, played);
            Assert.AreEqual(0, mixer.DroppedDuplicate);
        }

        [Test]
        public void Starts_AreOrderedByPriorityThenDistance()
        {
            var mixer = new AudioMixerModel();
            mixer.Enqueue(Flat(SoundKind.UiClick));                 // 7
            mixer.Enqueue(At(SoundKind.StepGroundWalk, 10f, 1));    // 2, far
            mixer.Enqueue(At(SoundKind.StepGroundWalk, 2f, 2));     // 2, near
            mixer.Enqueue(At(SoundKind.GunAR, 50f, 3));             // 1
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(4, mixer.StartCount);
            Assert.AreEqual(SoundKind.GunAR, mixer.Start(0).Kind);
            Assert.AreEqual(2f, mixer.Start(1).Position.X);
            Assert.AreEqual(10f, mixer.Start(2).Position.X);
            Assert.AreEqual(SoundKind.UiClick, mixer.Start(3).Kind);
        }

        [Test]
        public void PerFrameStarts_AreCapped_TheRestCountAsBudgetDrops()
        {
            var mixer = new AudioMixerModel();
            for (int i = 0; i < 12; i++) mixer.Enqueue(At(SoundKind.GunAR, i, (uint)i));
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(AudioMixerModel.MaxStartsPerFrame, mixer.StartCount);
            Assert.AreEqual(12 - AudioMixerModel.MaxStartsPerFrame, mixer.DroppedBudget);
            // The nearest eight won.
            for (int i = 0; i < mixer.StartCount; i++) Assert.Less(mixer.Start(i).Position.X, 8f);
        }

        [Test]
        public void Budget_NeverExceeded_AndVoicesFreeWhenTheClipEnds()
        {
            var mixer = new AudioMixerModel();
            mixer.SetDuration(SoundKind.GunAR, 1f);
            uint source = 0;
            for (int frame = 0; frame < 5; frame++)
            {
                for (int i = 0; i < 20; i++) mixer.Enqueue(At(SoundKind.GunAR, 10f, source++));
                mixer.Mix(Vector3.Zero, frame * 0.01f);
                Assert.LessOrEqual(mixer.ActiveCount, AudioMixerModel.VoiceBudget);
            }
            Assert.AreEqual(AudioMixerModel.VoiceBudget, mixer.ActiveCount);
            Assert.Greater(mixer.DroppedBudget, 0);
            mixer.Mix(Vector3.Zero, 5f);   // every clip (1 s / at most 1.05 pitch) has ended
            Assert.AreEqual(0, mixer.ActiveCount);
        }

        [Test]
        public void FullBudget_ReplacesOnlyALowerPriorityOrFartherVoice()
        {
            var mixer = new AudioMixerModel();
            mixer.SetDuration(SoundKind.StepGroundWalk, 10f);
            mixer.SetDuration(SoundKind.GunAR, 10f);
            // Fill all 24 voices with footsteps (priority 2) 3 frames at a time.
            uint source = 0;
            for (int frame = 0; frame < 3; frame++)
            {
                for (int i = 0; i < AudioMixerModel.MaxStartsPerFrame; i++) mixer.Enqueue(At(SoundKind.StepGroundWalk, 5f + i, source++));
                mixer.Mix(Vector3.Zero, frame);
            }
            Assert.AreEqual(AudioMixerModel.VoiceBudget, mixer.ActiveCount);
            int dropped = mixer.DroppedBudget;

            // A UI click (7) cannot replace a footstep (2).
            mixer.Enqueue(Flat(SoundKind.UiClick));
            mixer.Mix(Vector3.Zero, 3f);
            Assert.AreEqual(0, mixer.StartCount);
            Assert.AreEqual(dropped + 1, mixer.DroppedBudget);

            // A gunshot (1) replaces the farthest footstep (x = 12).
            mixer.Enqueue(At(SoundKind.GunAR, 100f, 500));
            mixer.Mix(Vector3.Zero, 3.1f);
            Assert.AreEqual(1, mixer.StartCount);
            Assert.AreEqual(dropped + 2, mixer.DroppedBudget);   // the voice cut off

            // A footstep nearer than the farthest remaining footstep replaces it; a farther one does not.
            mixer.Enqueue(At(SoundKind.StepGroundWalk, 1f, 600));
            mixer.Mix(Vector3.Zero, 3.2f);
            Assert.AreEqual(1, mixer.StartCount);
            mixer.Enqueue(At(SoundKind.StepGroundWalk, 20f, 601));
            mixer.Mix(Vector3.Zero, 3.3f);
            Assert.AreEqual(0, mixer.StartCount);
            Assert.AreEqual(AudioMixerModel.VoiceBudget, mixer.ActiveCount);
        }

        [Test]
        public void FullQueueOfFarLowPriorityRequests_StillLetsAGunshotPlay()
        {
            var mixer = new AudioMixerModel();
            mixer.Mix(Vector3.Zero, 0f);   // a listener for the Enqueue checks
            for (int i = 0; i < AudioMixerModel.QueueCapacity; i++) Assert.IsTrue(mixer.Enqueue(At(SoundKind.BuildDamage, 30f + i * 0.1f, (uint)i)));
            Assert.IsTrue(mixer.Enqueue(At(SoundKind.GunAR, 50f, 999)));
            Assert.AreEqual(1, mixer.QueueOverflow);   // the farthest piece damage was replaced
            Assert.IsFalse(mixer.Enqueue(Flat(SoundKind.UiClick)));   // a less important request cannot replace any
            Assert.AreEqual(2, mixer.QueueOverflow);
            mixer.Mix(Vector3.Zero, 1f);
            Assert.AreEqual(SoundKind.GunAR, mixer.Start(0).Kind);
            Assert.AreEqual(1, mixer.Plays[(int)SoundKind.GunAR]);
        }

        [Test]
        public void Enqueue_DropsAFar3DRequestAtOnce_AfterTheFirstMix()
        {
            var mixer = new AudioMixerModel();
            Assert.IsTrue(mixer.Enqueue(At(SoundKind.BuildDestroy, 1000f, 1)));   // no listener yet: kept (Mix drops it)
            mixer.Mix(Vector3.Zero, 0f);
            Assert.AreEqual(1, mixer.DroppedDistance);
            for (int i = 0; i < 100; i++) Assert.IsFalse(mixer.Enqueue(At(SoundKind.BuildDestroy, AudioCatalog.BuildDistance + 5f, (uint)i)));
            Assert.AreEqual(101, mixer.DroppedDistance);
            Assert.AreEqual(0, mixer.Queued);
            Assert.AreEqual(0, mixer.QueueOverflow);
            Assert.IsTrue(mixer.Enqueue(Flat(SoundKind.HealthHit)));   // 2D is never too far
        }

        [Test]
        public void RecentTable_KeepsAFreshPairOverOlderOnes()
        {
            var mixer = new AudioMixerModel();
            // 64 old pairs.
            for (int i = 0; i < AudioMixerModel.RecentCapacity; i++) mixer.Enqueue(Flat(SoundKind.UiClick, (uint)(100 + i)));
            mixer.Mix(Vector3.Zero, 0f);
            // One fresh pair (BuildDamage, 0.15 s interval) 2 s later: the old ones no longer matter.
            mixer.Enqueue(Flat(SoundKind.BuildDamage, 7));
            mixer.Mix(Vector3.Zero, 2f);
            // 64 more new pairs in the same instant.
            for (int i = 0; i < AudioMixerModel.RecentCapacity; i++) mixer.Enqueue(Flat(SoundKind.HitMarker, (uint)(500 + i)));
            mixer.Mix(Vector3.Zero, 2.01f);
            int duplicates = mixer.DroppedDuplicate;
            mixer.Enqueue(Flat(SoundKind.BuildDamage, 7));   // still within its 0.15 s
            mixer.Mix(Vector3.Zero, 2.05f);
            Assert.AreEqual(duplicates + 1, mixer.DroppedDuplicate);
            Assert.AreEqual(0, mixer.StartCount);
        }

        [Test]
        public void IsBetter_LowerNumberWins_ThenNearer()
        {
            Assert.IsTrue(AudioMixerModel.IsBetter(1, 100f, 2, 1f));
            Assert.IsFalse(AudioMixerModel.IsBetter(2, 1f, 1, 100f));
            Assert.IsTrue(AudioMixerModel.IsBetter(3, 5f, 3, 6f));
            Assert.IsFalse(AudioMixerModel.IsBetter(3, 6f, 3, 6f));
        }

        [Test]
        public void Pitch_StaysWithinFivePercent_VariantWithinTheKinds()
        {
            var mixer = new AudioMixerModel();
            for (int frame = 0; frame < 50; frame++)
            {
                mixer.Enqueue(Flat(SoundKind.GunAR, (uint)frame));
                mixer.Mix(Vector3.Zero, frame);
                AudioStart start = mixer.Start(0);
                Assert.That(start.Pitch, Is.InRange(0.95f, 1.05f));
                Assert.That(start.Variant, Is.InRange(0, AudioCatalog.Info(SoundKind.GunAR).Variants - 1));
                Assert.IsFalse(start.Spatial);
            }
        }
    }
}
