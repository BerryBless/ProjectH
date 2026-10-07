using NUnit.Framework;
using ProjectH.Client.Game.Map;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 15 D1, D3, D11: the one map transform (bounds, round trip, north up), the minimap window with its edge clamp, and
    // the screen-edge arrow.
    public class MapProjectionTests
    {
        [Test]
        public void Corners_MapToUnitSquare_NorthIsUp()
        {
            MapProjection.WorldToUv(-GameMap.HalfSize, -GameMap.HalfSize, out float u, out float v);
            Assert.AreEqual((0f, 0f), (u, v));
            MapProjection.WorldToUv(GameMap.HalfSize, GameMap.HalfSize, out u, out v);
            Assert.AreEqual((1f, 1f), (u, v));
            MapProjection.WorldToUv(0f, 0f, out u, out v);
            Assert.AreEqual((0.5f, 0.5f), (u, v));
            // North (+Z) is up (+v), east (+X) is right (+u).
            MapProjection.WorldToUv(0f, 40f, out u, out v);
            Assert.AreEqual(0.5f, u, 1e-6f);
            Assert.AreEqual(0.75f, v, 1e-6f);
            MapProjection.WorldToUv(40f, 0f, out u, out v);
            Assert.AreEqual(0.75f, u, 1e-6f);
        }

        [Test]
        public void WorldUvRect_RoundTrip()
        {
            float[] xs = { -80f, -33.3f, 0f, 12.5f, 79.99f };
            foreach (float x in xs)
            {
                foreach (float z in xs)
                {
                    MapProjection.WorldToUv(x, z, out float u, out float v);
                    MapProjection.UvToRect(u, v, 900f, 900f, out float px, out float py);
                    MapProjection.RectToUv(px, py, 900f, 900f, out float u2, out float v2);
                    MapProjection.UvToWorld(u2, v2, out float x2, out float z2);
                    Assert.AreEqual(x, x2, 1e-3f);
                    Assert.AreEqual(z, z2, 1e-3f);
                }
            }
        }

        [Test]
        public void Rect_PixelsFromBottomLeft()
        {
            MapProjection.UvToRect(0.25f, 0.75f, 200f, 200f, out float px, out float py);
            Assert.AreEqual((50f, 150f), (px, py));
            MapProjection.RectToUv(10f, 10f, 0f, 0f, out float u, out float v);
            Assert.AreEqual((0f, 0f), (u, v));
        }

        [Test]
        public void InsideUv_Bounds()
        {
            Assert.IsTrue(MapProjection.InsideUv(0f, 1f));
            Assert.IsFalse(MapProjection.InsideUv(-0.001f, 0.5f));
            Assert.IsFalse(MapProjection.InsideUv(0.5f, 1.001f));
            Assert.IsFalse(MapProjection.InsideUv(float.NaN, 0.5f));
        }

        [Test]
        public void Window_IsSixtyMetresAroundTheCentre()
        {
            MapProjection.Window(10f, -20f, 60f, out float minU, out float minV, out float size);
            Assert.AreEqual(60f / 160f, size, 1e-6f);
            MapProjection.WorldToUv(10f - 30f, -20f - 30f, out float u, out float v);
            Assert.AreEqual(u, minU, 1e-6f);
            Assert.AreEqual(v, minV, 1e-6f);
            // The centre is the middle of the window; a point 15 m north of it is three quarters up.
            MapProjection.WorldToUv(10f, -5f, out u, out v);
            MapProjection.UvToWindow(u, v, minU, minV, size, out float wx, out float wy);
            Assert.AreEqual(0.5f, wx, 1e-5f);
            Assert.AreEqual(0.75f, wy, 1e-5f);
        }

        [Test]
        public void Window_AtTheMapEdge_GoesPastTheMap()
        {
            // Not clamped: we stay in the middle of the minimap and the black ring of the clamped texture fills the rest.
            MapProjection.Window(80f, 80f, 60f, out float minU, out float minV, out float size);
            Assert.Greater(minU + size, 1f);
            Assert.Greater(minV + size, 1f);
        }

        [Test]
        public void ClampToEdge_InsideIsUnchanged()
        {
            float x = 0.3f, y = 0.8f;
            Assert.IsFalse(MapProjection.ClampToEdge(ref x, ref y, 0.05f));
            Assert.AreEqual((0.3f, 0.8f), (x, y));
        }

        [Test]
        public void ClampToEdge_OutsideKeepsTheDirection()
        {
            float x = 2f, y = 0.5f;   // straight east
            Assert.IsTrue(MapProjection.ClampToEdge(ref x, ref y, 0.05f));
            Assert.AreEqual(0.95f, x, 1e-6f);
            Assert.AreEqual(0.5f, y, 1e-6f);

            x = 1.5f;   // north-east, twice as far east as north from the centre
            y = 1.0f;
            Assert.IsTrue(MapProjection.ClampToEdge(ref x, ref y, 0f));
            Assert.AreEqual(1f, x, 1e-6f);
            Assert.AreEqual(0.75f, y, 1e-6f);

            x = -3f;   // south-west corner direction
            y = -3f;
            MapProjection.ClampToEdge(ref x, ref y, 0.1f);
            Assert.AreEqual(0.1f, x, 1e-6f);
            Assert.AreEqual(0.1f, y, 1e-6f);
        }

        [Test]
        public void ClampToEdge_InsideTheInsetBandIsPulledIn()
        {
            float x = 0.98f, y = 0.5f;   // inside the window but under the icon's half size
            Assert.IsTrue(MapProjection.ClampToEdge(ref x, ref y, 0.05f));
            Assert.AreEqual(0.95f, x, 1e-6f);
        }

        [Test]
        public void ClampToEdge_BadValues()
        {
            float x = float.NaN, y = 0.5f;
            Assert.IsTrue(MapProjection.ClampToEdge(ref x, ref y, 0.05f));
            Assert.AreEqual((0.5f, 0.5f), (x, y));
            x = float.PositiveInfinity;
            y = 0.5f;
            MapProjection.ClampToEdge(ref x, ref y, 0.05f);
            Assert.AreEqual((0.95f, 0.5f), (x, y));
        }

        [Test]
        public void ScreenEdge_OnScreen_IsNotAnArrow()
        {
            Assert.IsFalse(ScreenEdge.ToEdge(500f, 400f, false, 1920f, 1080f, 40f, out float ex, out float ey, out _));
            Assert.AreEqual((500f, 400f), (ex, ey));
        }

        [Test]
        public void ScreenEdge_OffScreen_PointsThatWay()
        {
            Assert.IsTrue(ScreenEdge.ToEdge(5000f, 540f, false, 1920f, 1080f, 40f, out float ex, out float ey, out float angle));
            Assert.AreEqual(1880f, ex, 1e-3f);
            Assert.AreEqual(540f, ey, 1e-3f);
            Assert.AreEqual(0f, angle, 1e-3f);
            ScreenEdge.ToEdge(960f, -900f, false, 1920f, 1080f, 40f, out ex, out ey, out angle);
            Assert.AreEqual(960f, ex, 1e-3f);
            Assert.AreEqual(40f, ey, 1e-3f);
            Assert.AreEqual(-90f, angle, 1e-3f);
        }

        [Test]
        public void ScreenEdge_Behind_FlipsTheDirection()
        {
            // Behind the camera the projected point is mirrored: right of centre means the target is to the left.
            Assert.IsTrue(ScreenEdge.ToEdge(1200f, 540f, true, 1920f, 1080f, 40f, out float ex, out _, out float angle));
            Assert.AreEqual(40f, ex, 1e-3f);
            Assert.AreEqual(180f, System.Math.Abs(angle), 1e-3f);
            // Straight behind: down.
            ScreenEdge.ToEdge(960f, 540f, true, 1920f, 1080f, 40f, out _, out float ey, out angle);
            Assert.AreEqual(40f, ey, 1e-3f);
            Assert.AreEqual(-90f, angle, 1e-3f);
        }
    }
}
