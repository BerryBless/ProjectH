using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 6 spec interpretation 11: the POI holding the point (edge included) whose centre is nearest; -1 if none.
    public class PoiLookupTests
    {
        [Test]
        public void ThePlaza_IsCrossroads_UpToItsEdge()
        {
            Assert.AreEqual(0, PoiLookup.Find(0f, 0f));
            Assert.AreEqual(0, PoiLookup.Find(GameMap.PlazaRadius, 0f));
            Assert.AreEqual(-1, PoiLookup.Find(GameMap.PlazaRadius + 0.01f, 0f));
        }

        [Test]
        public void EveryPoiCentre_FindsThatPoi()
        {
            for (int i = 0; i < MapPois.All.Length; i++)
                Assert.AreEqual(i, PoiLookup.Find(MapPois.All[i].X, MapPois.All[i].Z), MapPois.All[i].Name);
        }

        [Test]
        public void OpenGround_IsNoPoi()
        {
            Assert.AreEqual(-1, PoiLookup.Find(70f, -10f));
            Assert.AreEqual(-1, PoiLookup.Find(float.NaN, 0f));
        }

        [Test]
        public void Overlapping_TheNearestCentreWins_TiesGoToTheFirst()
        {
            var pois = new[] { new MapPoi("A", 0f, 0f, 10f), new MapPoi("B", 8f, 0f, 10f) };
            Assert.AreEqual(1, PoiLookup.Find(pois, 5f, 0f));
            Assert.AreEqual(0, PoiLookup.Find(pois, 3f, 0f));
            Assert.AreEqual(0, PoiLookup.Find(pois, 4f, 0f));
        }
    }
}
