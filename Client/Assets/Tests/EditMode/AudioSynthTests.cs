using System;
using NUnit.Framework;
using ProjectH.Client.Game.Audio;

namespace ProjectH.Client.Tests
{
    // Phase 18 D1: every clip is synthesized deterministically, has the length it says, is not silent and stays in range; the
    // catalog has a row for every kind.
    public class AudioSynthTests
    {
        [Test]
        public void EveryKindAndVariant_IsDeterministic_NotSilent_InRange()
        {
            for (int k = 0; k < (int)SoundKind.Count; k++)
            {
                var kind = (SoundKind)k;
                int variants = AudioCatalog.Info(kind).Variants;
                Assert.That(variants, Is.InRange(1, 3), kind.ToString());
                for (int v = 0; v < variants; v++)
                {
                    float[] a = AudioSynth.Render(kind, v);
                    float[] b = AudioSynth.Render(kind, v);
                    Assert.AreEqual(AudioSynth.Length(kind, v), a.Length, kind + " length");
                    CollectionAssert.AreEqual(a, b, kind + " deterministic");
                    double sum = 0;
                    float peak = 0f;
                    foreach (float s in a)
                    {
                        Assert.IsFalse(float.IsNaN(s), kind + " NaN");
                        sum += s * s;
                        peak = Math.Max(peak, Math.Abs(s));
                    }
                    Assert.Greater(Math.Sqrt(sum / a.Length), 0.01, kind + " silent");
                    Assert.LessOrEqual(peak, AudioSynth.Peak + 1e-4f, kind + " peak");
                    Assert.AreEqual(0f, a[a.Length - 1], 1e-3f, kind + " fades out");
                }
            }
        }

        [Test]
        public void Variants_Differ()
        {
            float[] a = AudioSynth.Render(SoundKind.GunAR, 0);
            float[] b = AudioSynth.Render(SoundKind.GunAR, 1);
            CollectionAssert.AreNotEqual(a, b);
        }

        [Test]
        public void Clips_AreShort()
        {
            for (int k = 0; k < (int)SoundKind.Count; k++)
            {
                float seconds = AudioSynth.Length((SoundKind)k, 0) / (float)AudioSynth.SampleRate;
                Assert.That(seconds, Is.InRange(0.03f, 1.5f), ((SoundKind)k).ToString());
            }
        }

        [Test]
        public void Catalog_FollowsTheRequestedPrioritiesAndDistances()
        {
            Assert.AreEqual(1, AudioCatalog.Info(SoundKind.GunAR).Priority);
            Assert.AreEqual(120f, AudioCatalog.Info(SoundKind.GunAR).MaxDistance);
            Assert.AreEqual(150f, AudioCatalog.Info(SoundKind.GunSniper).MaxDistance);
            Assert.AreEqual(150f, AudioCatalog.Info(SoundKind.RocketLaunch).MaxDistance);
            Assert.AreEqual(1, AudioCatalog.Info(SoundKind.Explosion).Priority);
            Assert.AreEqual(150f, AudioCatalog.Info(SoundKind.Explosion).MaxDistance);
            Assert.AreEqual(2, AudioCatalog.Info(SoundKind.StepGroundWalk).Priority);
            Assert.AreEqual(25f, AudioCatalog.Info(SoundKind.StepStoneWalk).MaxDistance);
            Assert.AreEqual(30f, AudioCatalog.Info(SoundKind.StepWoodSprint).MaxDistance);
            Assert.AreEqual(12f, AudioCatalog.Info(SoundKind.StepMetalCrouch).MaxDistance);
            Assert.AreEqual(3, AudioCatalog.Info(SoundKind.BuildPlace).Priority);
            Assert.AreEqual(40f, AudioCatalog.Info(SoundKind.BuildCollapse).MaxDistance);
            Assert.AreEqual(4, AudioCatalog.Info(SoundKind.ShieldBreak).Priority);
            Assert.AreEqual(5, AudioCatalog.Info(SoundKind.Pickup).Priority);
            Assert.AreEqual(15f, AudioCatalog.Info(SoundKind.ContainerOpen).MaxDistance);
            Assert.AreEqual(150f, AudioCatalog.Info(SoundKind.SupplyDropLand).MaxDistance);
            Assert.AreEqual(6, AudioCatalog.Info(SoundKind.ZoneWarning).Priority);
            Assert.AreEqual(7, AudioCatalog.Info(SoundKind.UiClick).Priority);
            Assert.AreEqual(0.15f, AudioCatalog.Info(SoundKind.BuildDamage).MinInterval);
            Assert.Less(AudioCatalog.Info(SoundKind.GunSMG).MinInterval, 0.0667f);
            for (int k = 0; k < (int)SoundKind.Count; k++)
                Assert.LessOrEqual(AudioCatalog.Info((SoundKind)k).MinInterval, AudioCatalog.LongestInterval);
        }
    }
}
