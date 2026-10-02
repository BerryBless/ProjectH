using System.Linq;
using System.Numerics;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Harvest;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D5 (request §9, §25, §26, §112, §113): the tool in hand. Q build mode (again: back), F the harvest tool, 1-3 the
// weapons; Fire by tool; the tool in the snapshot.
public class ToolTests
{
    private readonly SandboxHarness _h = new();
    private static readonly Vector3 Spot = SandboxHarness.Ground(-40f, 0f);

    [Fact]
    public void Q_EntersBuildMode_AndQAgain_GoesBackToTheToolBefore()
    {
        PlayerEntity p = _h.Join(1, Spot);
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
        _h.Press(p, InputButtons.ToolBuild);
        Assert.Equal(ToolKind.Build, p.Inventory.Tool);
        _h.Press(p, InputButtons.ToolBuild);
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);

        _h.Press(p, InputButtons.ToolHarvest);
        Assert.Equal(ToolKind.Harvest, p.Inventory.Tool);
        _h.Press(p, InputButtons.ToolBuild);
        Assert.Equal(ToolKind.Build, p.Inventory.Tool);
        _h.Press(p, InputButtons.ToolBuild);
        Assert.Equal(ToolKind.Harvest, p.Inventory.Tool);
    }

    [Fact]
    public void AWeaponSlotKey_TakesTheWeaponsOut_FromAnyTool_EvenTheCurrentSlot()
    {
        PlayerEntity p = _h.Join(1, Spot);
        _h.Press(p, InputButtons.ToolBuild);
        _h.Press(p, InputButtons.Slot1);   // slot 0 is already current
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
        Assert.Equal(0, p.Inventory.CurrentSlot);
        _h.Press(p, InputButtons.ToolHarvest);
        _h.Press(p, InputButtons.Slot2);
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
        Assert.Equal(1, p.Inventory.CurrentSlot);
    }

    [Fact]
    public void KeysTogether_TheSlotWins_ThenF()
    {
        PlayerEntity p = _h.Join(1, Spot);
        _h.Press(p, InputButtons.ToolBuild | InputButtons.ToolHarvest);
        Assert.Equal(ToolKind.Harvest, p.Inventory.Tool);
        _h.Press(p, InputButtons.ToolBuild | InputButtons.ToolHarvest | InputButtons.Slot1);
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
    }

    [Fact]
    public void InBuildMode_FireShootsNothing()
    {
        PlayerEntity p = _h.Join(1, Spot);
        _h.Press(p, InputButtons.ToolBuild);
        int ammo = p.Inventory.Current.MagAmmo;
        _h.Act(p, InputButtons.Fire, Spot + new Vector3(0f, 1.6f, 10f));
        Assert.Equal(ammo, p.Inventory.Current.MagAmmo);
        Assert.DoesNotContain(_h.Packets, s => s.Id == PacketId.ShotFired);
    }

    [Fact]
    public void WithAToolOut_ReloadDoesNothing_AndSwitchingCancelsAReload()
    {
        PlayerEntity p = _h.Join(1, Spot);
        p.Inventory.Current.MagAmmo = 1;
        _h.Press(p, InputButtons.Reload);
        Assert.True(p.Reloading);
        _h.Press(p, InputButtons.ToolHarvest);
        Assert.False(p.Reloading);
        _h.Press(p, InputButtons.Reload);
        Assert.False(p.Reloading);
    }

    [Fact]
    public void TheTool_IsInTheSnapshot_ForTheOwnerAndForOthers()
    {
        PlayerEntity a = _h.Join(1, Spot);
        _h.Join(2, Spot + new Vector3(3f, 0f, 0f));
        _h.Press(a, InputButtons.ToolBuild);
        _h.Clear();
        _h.Ticks(2);   // a snapshot every second tick at the default rates
        SandboxHarness.Sent toA = _h.To(1, PacketId.WorldSnapshot).Last();
        PacketReader r = SandboxHarness.Body(toA);
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header));
        Assert.Equal(ToolKind.Build, header.Self.Tool);
        Assert.Equal(0, header.Self.WeaponSlot);

        SandboxHarness.Sent toB = _h.To(2, PacketId.WorldSnapshot).Last();
        r = SandboxHarness.Body(toB);
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out header));
        bool found = false;
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            if (e.EntityId != a.EntityId) continue;
            found = true;
            Assert.Equal(ToolKind.Build, e.Tool);
            Assert.True(e.IsAlive);
        }
        Assert.True(found);
    }

    [Fact]
    public void ARespawn_StartsWithTheWeaponsOut()
    {
        PlayerEntity p = _h.Join(1, Spot);
        _h.Press(p, InputButtons.ToolBuild);
        p.Inventory.Clear();   // what every respawn does (StartingLoadout.ApplyTo)
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
        Assert.Equal(ToolKind.Weapon, p.Inventory.PreviousTool);
    }

    [Fact]
    public void SelectTool_ReportsAChange_Only()
    {
        PlayerEntity p = _h.Join(1, Spot);
        Assert.False(HarvestRules.SelectTool(p, InputButtons.None));
        Assert.False(HarvestRules.SelectTool(p, InputButtons.Slot1));
        Assert.True(HarvestRules.SelectTool(p, InputButtons.ToolHarvest));
        Assert.False(HarvestRules.SelectTool(p, InputButtons.ToolHarvest));
    }

    // The client predicts the tool with its own copy of the rule (ToolState.Select): every sequence of four inputs drawn
    // from the tool keys (alone and together) must leave both copies on the same tool and the same tool before.
    [Fact]
    public void TheClientsCopy_SwitchesLikeTheServer()
    {
        InputButtons[] keys =
        {
            InputButtons.None, InputButtons.Slot1, InputButtons.Slot3, InputButtons.ToolHarvest, InputButtons.ToolBuild,
            InputButtons.ToolHarvest | InputButtons.ToolBuild, InputButtons.Slot2 | InputButtons.ToolBuild,
        };
        PlayerEntity p = _h.Join(1, Spot);
        int n = keys.Length;
        for (int combo = 0; combo < n * n * n * n; combo++)
        {
            p.Inventory.Clear();
            ToolKind current = ToolKind.Weapon;
            ToolKind previous = ToolKind.Weapon;
            for (int i = 0, c = combo; i < 4; i++, c /= n)
            {
                InputButtons buttons = keys[c % n];
                HarvestRules.SelectTool(p, buttons);
                current = ToolState.Select(current, ref previous, buttons);
                Assert.Equal(p.Inventory.Tool, current);
                Assert.Equal(p.Inventory.PreviousTool, previous);
            }
        }
    }
}
