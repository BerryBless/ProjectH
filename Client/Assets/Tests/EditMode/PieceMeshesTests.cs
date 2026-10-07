using System.Collections.Generic;
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 13.5 D12: the edited-mesh cache key. Box meshes are built unrotated along the world axes, so a south wall
    // (along X) and a west wall (along Z) with the same Edit must not share a mesh.
    public class PieceMeshesTests
    {
        [Test]
        public void ASouthAndAWestWall_WithTheSameEdit_HaveDifferentKeys()
        {
            var south = new BuildPieceShape(BuildPieceType.Wall, 3, 0, 3, 0, 1 << 4);
            var west = new BuildPieceShape(BuildPieceType.Wall, 3, 0, 3, 1, 1 << 4);
            Assert.AreNotEqual(PieceMeshes.CacheKey(south), PieceMeshes.CacheKey(west));
            // Same rotation and Edit elsewhere on the map: the same mesh (it is built around the piece's pivot).
            Assert.AreEqual(PieceMeshes.CacheKey(south), PieceMeshes.CacheKey(new BuildPieceShape(BuildPieceType.Wall, 9, 2, 5, 0, 1 << 4)));
        }

        [Test]
        public void EveryPossibleKey_IsDistinct_AndFitsTheCacheLimit()
        {
            var keys = new HashSet<int>();
            for (int rotation = 0; rotation < 2; rotation++)
            {
                for (int edit = 1; edit < 1 << BuildEdit.WallTiles; edit++)
                    Assert.IsTrue(keys.Add(PieceMeshes.CacheKey(new BuildPieceShape(BuildPieceType.Wall, 0, 0, 0, rotation, edit))));
            }
            for (int edit = 1; edit < 16; edit++)
                Assert.IsTrue(keys.Add(PieceMeshes.CacheKey(new BuildPieceShape(BuildPieceType.Floor, 0, 0, 0, 0, edit))));
            for (int edit = 1; edit <= BuildEdit.RoofPassage; edit++)
                Assert.IsTrue(keys.Add(PieceMeshes.CacheKey(new BuildPieceShape(BuildPieceType.Roof, 0, 0, 0, 0, edit))));
            Assert.LessOrEqual(keys.Count, PieceMeshes.MaxCachedMeshes);
        }
    }
}
