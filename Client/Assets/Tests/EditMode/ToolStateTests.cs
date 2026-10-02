using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 13 D5: the predicted tool switches like the server's HarvestRules.SelectTool and follows the snapshot.
    public class ToolStateTests
    {
        [Test]
        public void Q_EntersBuildMode_AndQAgain_GoesBackToTheToolBefore()
        {
            var tools = new ToolState();
            Assert.AreEqual(ToolKind.Harvest, tools.Step(1, InputButtons.ToolHarvest, true));
            Assert.AreEqual(ToolKind.Build, tools.Step(2, InputButtons.ToolBuild, true));
            Assert.AreEqual(ToolKind.Harvest, tools.Step(3, InputButtons.ToolBuild, true));
            Assert.AreEqual(ToolKind.Weapon, tools.Step(4, InputButtons.Slot2, true));
        }

        [Test]
        public void AnInputThatCannotAct_SwitchesNothing()
        {
            var tools = new ToolState();
            Assert.AreEqual(ToolKind.Weapon, tools.Step(1, InputButtons.ToolBuild, false));
            Assert.AreEqual(ToolKind.Weapon, tools.At(1));
        }

        [Test]
        public void KeysTogether_TheSlotWins_ThenF()
        {
            ToolKind previous = ToolKind.Weapon;
            Assert.AreEqual(ToolKind.Weapon, ToolState.Select(ToolKind.Build, ref previous, InputButtons.Slot1 | InputButtons.ToolHarvest));
            Assert.AreEqual(ToolKind.Harvest, ToolState.Select(ToolKind.Weapon, ref previous, InputButtons.ToolHarvest | InputButtons.ToolBuild));
        }

        [Test]
        public void AMatchingSnapshot_KeepsThePrediction()
        {
            var tools = new ToolState();
            tools.Step(1, InputButtons.ToolBuild, true);
            tools.Step(2, InputButtons.None, true);
            tools.ApplyServer(ToolKind.Build, 1);
            Assert.AreEqual(ToolKind.Build, tools.Current);
        }

        [Test]
        public void ADifferentSnapshot_ReplacesThePrediction_AndRunsTheNewerInputsAgain()
        {
            var tools = new ToolState();
            tools.Step(1, InputButtons.ToolBuild, true);    // the server did not take this one (say it could not act)
            tools.Step(2, InputButtons.ToolHarvest, true);
            tools.Step(3, InputButtons.ToolBuild, true);    // build, remembering harvest
            tools.ApplyServer(ToolKind.Weapon, 1);
            Assert.AreEqual(ToolKind.Build, tools.Current);
            Assert.AreEqual(ToolKind.Harvest, tools.Previous);
            tools.Step(4, InputButtons.ToolBuild, true);
            Assert.AreEqual(ToolKind.Harvest, tools.Current);
        }

        [Test]
        public void ASnapshotOutsideTheHistory_IsTakenAsIs()
        {
            var tools = new ToolState();
            tools.Step(1, InputButtons.None, true);
            tools.ApplyServer(ToolKind.Harvest, 500);
            Assert.AreEqual(ToolKind.Harvest, tools.Current);
            tools.Reset();
            Assert.AreEqual(ToolKind.Weapon, tools.Current);
            Assert.AreEqual(ToolKind.Weapon, tools.Previous);
        }
    }
}
