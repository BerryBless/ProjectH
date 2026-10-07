using NUnit.Framework;
using ProjectH.Client.Game.Map;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 15 D2: the map picture from the shared map data: size, black outside ring, dark footprints, row 0 = south.
    public class MapRasterTests
    {
        private const int Size = MapRaster.DefaultSize;

        // 기능: 지도 그림을 한 번 그린다.
        // 입력: 없음.
        // 출력: RGBA32 바이트 배열(Size × Size).
        private static byte[] Raster()
        {
            var rgba = new byte[Size * Size * 4];
            MapRaster.Rasterize(rgba, Size);
            return rgba;
        }

        // 기능: 월드 좌표가 들어가는 픽셀의 색이 주어진 색과 같은지 본다.
        // 입력: rgba - 그림, x·z - 월드 좌표, color - RGBA 4바이트.
        // 출력: 같으면 true.
        private static bool PixelIs(byte[] rgba, float x, float z, byte[] color)
        {
            int i = (int)((x + MapProjection.Extent) / MapProjection.Size * Size);
            int j = (int)((z + MapProjection.Extent) / MapProjection.Size * Size);
            int o = (j * Size + i) * 4;
            return rgba[o] == color[0] && rgba[o + 1] == color[1] && rgba[o + 2] == color[2] && rgba[o + 3] == color[3];
        }

        [Test]
        public void WrongBuffer_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => MapRaster.Rasterize(new byte[10], Size));
            Assert.Throws<System.ArgumentException>(() => MapRaster.Rasterize(null, Size));
        }

        [Test]
        public void EdgeRing_IsBlack_AndEveryPixelOpaque()
        {
            byte[] rgba = Raster();
            for (int k = 0; k < Size; k++)
            {
                foreach (int o in new[] { k * 4, ((Size - 1) * Size + k) * 4, (k * Size) * 4, (k * Size + Size - 1) * 4 })
                {
                    Assert.AreEqual(0, rgba[o]);
                    Assert.AreEqual(0, rgba[o + 1]);
                    Assert.AreEqual(0, rgba[o + 2]);
                }
            }
            for (int o = 3; o < rgba.Length; o += 4) Assert.AreEqual(255, rgba[o]);
        }

        [Test]
        public void BoxesDoorsAndHarvestables_AreDarkFootprints()
        {
            byte[] rgba = Raster();
            int checkedBoxes = 0;
            foreach (Box box in GameMap.Boxes)
            {
                System.Numerics.Vector3 c = box.Center;
                if (System.Math.Abs(c.X) > GameMap.HalfSize - 2f || System.Math.Abs(c.Z) > GameMap.HalfSize - 2f) continue;   // the outer walls
                Assert.IsTrue(PixelIs(rgba, c.X, c.Z, MapRaster.Footprint), "box at " + c);
                checkedBoxes++;
            }
            Assert.Greater(checkedBoxes, 0);
            foreach (Box door in GameMap.Doors) Assert.IsTrue(PixelIs(rgba, door.Center.X, door.Center.Z, MapRaster.Footprint));
            foreach (Harvestable h in GameMap.Harvestables) Assert.IsTrue(PixelIs(rgba, h.Bounds.Center.X, h.Bounds.Center.Z, MapRaster.Footprint));
        }

        [Test]
        public void OpenGround_IsTerrainGreen_NotAFootprint()
        {
            byte[] rgba = Raster();
            // The plaza centre is kept clear (lobby: nothing built there).
            Assert.IsFalse(PixelIs(rgba, 0f, 0f, MapRaster.Footprint));
            Assert.IsFalse(PixelIs(rgba, 0f, 0f, MapRaster.Outside));
            int o = ((Size / 2) * Size + Size / 2) * 4;
            Assert.Greater(rgba[o + 1], rgba[o + 2]);   // greener than blue
        }

        [Test]
        public void RowZero_IsSouth()
        {
            byte[] rgba = Raster();
            // A house roof in the north half (Rustvale or Gearworks, z > 0) lands in the upper rows; the mirrored south point does not.
            foreach (Box box in GameMap.Boxes)
            {
                System.Numerics.Vector3 c = box.Center;
                if (c.Z < 20f || System.Math.Abs(c.X) > GameMap.HalfSize - 2f || c.Z > GameMap.HalfSize - 2f) continue;
                Assert.IsTrue(PixelIs(rgba, c.X, c.Z, MapRaster.Footprint));
                int j = (int)((c.Z + MapProjection.Extent) / MapProjection.Size * Size);
                Assert.Greater(j, Size / 2);
                return;
            }
            Assert.Fail("no box in the north half");
        }
    }
}
