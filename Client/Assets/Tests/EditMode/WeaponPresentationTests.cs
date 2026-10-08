using NUnit.Framework;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Game;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    // Phase 17 D3, D8: the recoil kick, the tracer spread cone and the explosion lines of the kill feed.
    public class WeaponPresentationTests
    {
        [Test]
        public void Recoil_KicksAtOnce_AndReturnsOverAFewFrames()
        {
            var recoil = new RecoilKick();
            recoil.Kick(2f);
            Assert.AreEqual(2f, recoil.Offset, 1e-5f);

            recoil.Step(1f / 60f);
            float afterOneFrame = recoil.Offset;
            Assert.Less(afterOneFrame, 2f);
            Assert.Greater(afterOneFrame, 1f);   // not gone in one frame

            for (int i = 0; i < 30; i++) recoil.Step(1f / 60f);   // half a second
            Assert.AreEqual(0f, recoil.Offset);                  // snapped to exactly zero
        }

        [Test]
        public void Recoil_IsCapped_AndIgnoresBadInput()
        {
            var recoil = new RecoilKick();
            for (int i = 0; i < 100; i++) recoil.Kick(1f);
            Assert.AreEqual(RecoilKick.MaxOffsetDegrees, recoil.Offset, 1e-5f);
            recoil.Reset();
            recoil.Kick(-1f);
            recoil.Kick(float.NaN);
            recoil.Kick(float.PositiveInfinity);
            Assert.AreEqual(0f, recoil.Offset);
            recoil.Kick(1f);
            recoil.Step(0f);
            recoil.Step(-1f);
            recoil.Step(float.NaN);
            Assert.AreEqual(1f, recoil.Offset, 1e-5f);
        }

        [Test]
        public void SpreadCone_StaysInsideTheHalfAngle()
        {
            Vector3 forward = new Vector3(0.3f, -0.2f, 0.93f).normalized;
            var random = new System.Random(1);
            for (int i = 0; i < 500; i++)
            {
                Vector3 d = SpreadCone.Sample(forward, 6f, (float)random.NextDouble(), (float)random.NextDouble());
                Assert.AreEqual(1f, d.magnitude, 1e-4f);
                float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, forward), -1f, 1f)) * Mathf.Rad2Deg;
                Assert.LessOrEqual(angle, 6f + 1e-3f);
            }
            // The edge of the cone (u = 1) is at the half angle.
            Vector3 edge = SpreadCone.Sample(forward, 6f, 1f, 0.25f);
            Assert.AreEqual(6f, Mathf.Acos(Vector3.Dot(edge, forward)) * Mathf.Rad2Deg, 0.01f);
        }

        [Test]
        public void SpreadCone_ZeroSpread_IsTheCentre_StraightUpWorks()
        {
            Vector3 forward = new Vector3(0f, 0f, 1f);
            Assert.AreEqual(forward, SpreadCone.Sample(forward, 0f, 0.7f, 0.3f));
            Vector3 up = SpreadCone.Sample(Vector3.up, 5f, 1f, 0.5f);   // the basis must not degenerate along +Y
            Assert.AreEqual(5f, Mathf.Acos(Mathf.Clamp(Vector3.Dot(up, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg, 0.01f);
        }

        [Test]
        public void KillFeed_ExplosionLines()
        {
            Assert.AreEqual("폭발", UiText.CauseName(DeathCause.Explosion));
            Assert.AreEqual("alice ▸ bob (폭발)", UiText.KillLine("alice", "bob", DeathCause.Explosion));
            Assert.AreEqual("폭발 ▸ bob", UiText.KillLine(null, "bob", DeathCause.Explosion));
            Assert.AreEqual("alice ▸ bob 기절 (폭발)", UiText.DownedLine("alice", "bob", DeathCause.Explosion));
            Assert.AreEqual("폭발 ▸ bob 기절", UiText.DownedLine(null, "bob", DeathCause.Explosion));
            Assert.AreEqual("탈락 원인: 폭발", UiText.KilledBy(true, true, DeathCause.Explosion, null));
            // Unchanged: a shot (Cause Zone with a killer), the zone and a fall.
            Assert.AreEqual("alice ▸ bob", UiText.KillLine("alice", "bob", DeathCause.Zone));
            Assert.AreEqual("자기장 ▸ bob", UiText.KillLine(null, "bob", DeathCause.Zone));
            Assert.AreEqual("낙하 ▸ bob 기절", UiText.DownedLine(null, "bob", DeathCause.Fall));
        }
    }
}
