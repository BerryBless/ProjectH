using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    // Review fix D4 (STB-2): a piece synced before our own spawn gets its view. GameClient (a MonoBehaviour) calls
    // ApplyPieceChanges from UpdateBuildPresentationWithoutPredictor while it has no predictor; this drives the same call with
    // no catalog yet and no clock (tick 0). Builds real views (Unity objects), cleaned up with UnityObjects.Destroy.
    public class BuildPieceViewsTests
    {
        [Test]
        public void Apply_RunsWithoutAPredictor()
        {
            Shader shader = Shader.Find("Unlit/Color");
            Assert.IsNotNull(shader, "a built-in shader for the source material");
            var source = new Material(shader);
            var meshes = new PieceMeshes();
            var views = new BuildPieceViews(meshes, source);
            try
            {
                var store = new BuildStore();
                store.ApplyInterest(ulong.MaxValue);
                store.ApplyPiece(new BuildPieceRecord
                {
                    Id = 1,
                    Shape = new BuildPieceShape(BuildPieceType.Wall, 3, 0, 3, 0),
                    Material = BuildMaterialType.Wood,
                    CreatedTick = 10,
                }, 1);
                Assert.AreEqual(1, store.Changed.Count);

                GameClient.ApplyPieceChanges(views, store, null, 0);
                Assert.AreEqual(1, views.Count);
                Assert.AreEqual(0, store.Changed.Count);
            }
            finally
            {
                views.Dispose();
                meshes.Dispose();
                UnityObjects.Destroy(source);
            }
        }
    }
}
