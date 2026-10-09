using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 13 D10: the drawn piece follows the server's construction and health rule (BuildWorld.Progress / Health).
    public class BuildPieceLookTests
    {
        // 기능: 나무 재질(index 0)의 최대 체력 150, 초기 체력 30, 건설 60 Tick으로 채운 시험용 건설 카탈로그를 만든다.
        // 입력: 없음.
        // 출력: 시험용 BuildCatalogData.
        private static BuildCatalogData Catalog()
        {
            var c = new BuildCatalogData();
            c.MaxHealth[0] = 150;
            c.InitialHealth[0] = 30;
            c.ConstructionTicks[0] = 60;
            return c;
        }

        // 기능: Tick 100에 지은 나무 벽 조각 레코드를 만든다.
        // 입력: damage - 누적 피해량.
        // 출력: Id 1인 BuildPieceRecord.
        private static BuildPieceRecord Piece(ushort damage = 0) => new BuildPieceRecord
        {
            Id = 1,
            Shape = new BuildPieceShape(BuildPieceType.Wall, 1, 0, 1, 0),
            Material = BuildMaterialType.Wood,
            CreatedTick = 100,
            Damage = damage,
        };

        [Test]
        public void Progress_GoesFromZeroToOne_OverTheConstructionTicks()
        {
            Assert.AreEqual(0f, BuildPieceLook.Progress(100, 90, 60));
            Assert.AreEqual(0.5f, BuildPieceLook.Progress(100, 130, 60), 1e-5f);
            Assert.AreEqual(1f, BuildPieceLook.Progress(100, 160, 60));
            Assert.AreEqual(1f, BuildPieceLook.Progress(100, 100, 0));
        }

        [Test]
        public void Health_GrowsWhileBuilding_LessTheDamage()
        {
            Assert.AreEqual(30, BuildPieceLook.Health(Piece(), Catalog(), 100));
            Assert.AreEqual(90, BuildPieceLook.Health(Piece(), Catalog(), 130));
            Assert.AreEqual(150, BuildPieceLook.Health(Piece(), Catalog(), 1000));
            Assert.AreEqual(100, BuildPieceLook.Health(Piece(50), Catalog(), 1000));
        }

        [TestCase(150, 0)]
        [TestCase(101, 0)]
        [TestCase(100, 1)]
        [TestCase(51, 1)]
        [TestCase(50, 2)]
        [TestCase(1, 2)]
        public void Stage_SplitsTheHealthInThirds(int health, int stage)
        {
            Assert.AreEqual(stage, BuildPieceLook.Stage(health, 150));
        }

        [Test]
        public void DamageStage_UndamagedMidBuild_IsWhole_AndDamageShowsStages()
        {
            Assert.AreEqual(0, BuildPieceLook.DamageStage(Piece(), Catalog(), 100));
            Assert.AreEqual(0, BuildPieceLook.DamageStage(Piece(), Catalog(), 130));
            Assert.AreEqual(0, BuildPieceLook.DamageStage(Piece(), Catalog(), 1000));
            Assert.AreEqual(1, BuildPieceLook.DamageStage(Piece(50), Catalog(), 1000));
            Assert.AreEqual(2, BuildPieceLook.DamageStage(Piece(120), Catalog(), 1000));
            Assert.AreEqual(2, BuildPieceLook.DamageStage(Piece(20), Catalog(), 100));
        }

        [Test]
        public void ConstructionScale_StartsLow_AndEndsFull()
        {
            Assert.AreEqual(BuildPieceLook.MinConstructionScale, BuildPieceLook.ConstructionScale(0f), 1e-5f);
            Assert.AreEqual(1f, BuildPieceLook.ConstructionScale(1f), 1e-5f);
            Assert.AreEqual(1f, BuildPieceLook.ConstructionScale(3f), 1e-5f);
        }
    }
}
