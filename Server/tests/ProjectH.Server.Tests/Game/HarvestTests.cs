using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Harvest;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D5-D7 (request §173): the harvest tool, hits, resources, the cap, destruction and the weak point, in the dev
// sandbox (damage always allowed). Tree 0 stands at (-36, 64): trunk x -35.5..-36.5, z 63.5..64.5, 4 m high, 150 health;
// the tests stand south of it at z 62, facing north, so the trunk's south face is 1.5 m from the eye.
public class HarvestTests
{
    private const int Tree = 0;
    private static readonly Box Trunk = GameMap.Harvestables[Tree].Bounds;
    private static readonly Vector3 SouthOfTree = SandboxHarness.Ground(-36f, 62f);
    private static readonly Vector3 TrunkFace = new(-36f, 1.6f, Trunk.Min.Z);

    private readonly SandboxHarness _h = new();

    private PlayerEntity WithTool(int peer, Vector3 feet)
    {
        PlayerEntity p = _h.Join(peer, feet);
        _h.Press(p, InputButtons.ToolHarvest);
        Assert.Equal(ToolKind.Harvest, p.Inventory.Tool);
        return p;
    }

    private HarvestHit LastHit(int peer)
    {
        SandboxHarness.Sent sent = _h.To(peer, PacketId.HarvestHit).Last();
        PacketReader r = SandboxHarness.Body(sent);
        Assert.True(HarvestHit.TryRead(ref r, out HarvestHit hit));
        return hit;
    }

    private void Swing(PlayerEntity p, Vector3 at)
    {
        // Past the cooldown, then one press.
        _h.Ticks(_h.Match.Building.HarvestCooldownTicks);
        _h.Act(p, InputButtons.Fire, at);
        _h.Act(p, InputButtons.None, at);
    }

    [Fact]
    public void TheTreeAsCatalogued()
    {
        Assert.Equal(HarvestKind.Tree, GameMap.Harvestables[Tree].Kind);
        Assert.Equal(new Vector3(-36f, 2f, 64f), Trunk.Center);
        Assert.Equal(150, _h.Match.Harvest.Health(Tree));
    }

    [Fact]
    public void AValidHit_DamagesTheTree_GivesWood_AndTellsTheSwinger()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Assert.Equal(125, _h.Match.Harvest.Health(Tree));
        Assert.Equal(6, p.Inventory.Resource(BuildMaterialType.Wood));
        HarvestHit hit = LastHit(1);
        Assert.Equal(Tree, hit.TargetId);
        Assert.Equal(125, hit.Health);
        Assert.Equal(6, hit.Gained);
        Assert.False(hit.WeakPointHit);
        Assert.False(hit.Destroyed);
        Assert.True(hit.HasWeakPoint);   // the first hit makes it, on the face that was hit
        Assert.Equal(Trunk.Min.Z, hit.WeakPoint.Z);
        Assert.Equal(1, _h.Match.HarvestHits);
        // ResourcesState went to the swinger at the end of that tick.
        PacketReader r = SandboxHarness.Body(_h.To(1, PacketId.ResourcesState).Last());
        Assert.True(ResourcesState.TryRead(ref r, out ResourcesState resources));
        Assert.Equal(6, resources.Wood);
    }

    [Fact]
    public void OutOfRange_IsNoHit()
    {
        PlayerEntity p = WithTool(1, SandboxHarness.Ground(-36f, 60.6f));   // the face is 2.9 m from the eye
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Assert.Equal(150, _h.Match.Harvest.Health(Tree));
        Assert.Empty(_h.To(1, PacketId.HarvestHit));
    }

    [Fact]
    public void TheWrongDirection_IsNoHit()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        _h.Act(p, InputButtons.Fire, SouthOfTree + new Vector3(0f, 1.6f, -2f));   // facing away
        Assert.Equal(150, _h.Match.Harvest.Health(Tree));
        Assert.Equal(0, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    [Fact]
    public void HeldFire_SwingsOncePerCooldown()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        int cooldown = _h.Match.Building.HarvestCooldownTicks;
        Assert.Equal(12, cooldown);   // 0.4 s at 30 Hz
        for (int i = 0; i < cooldown; i++) _h.Act(p, InputButtons.Fire, TrunkFace);
        Assert.Equal(125, _h.Match.Harvest.Health(Tree));
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Assert.True(_h.Match.Harvest.Health(Tree) < 125);
    }

    [Fact]
    public void TheResourceCap_LimitsTheGain()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        p.Inventory.SetResource(BuildMaterialType.Wood, 498);
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Assert.Equal(500, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(2, LastHit(1).Gained);
        Swing(p, TrunkFace);
        Assert.Equal(500, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(0, LastHit(1).Gained);
        Assert.True(_h.Match.Harvest.Health(Tree) < 125);   // the hit still did its damage
    }

    [Fact]
    public void ATreeHitToZero_IsDestroyed_LeavesTheWorld_AndEveryoneHears()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        _h.Join(2, SandboxHarness.Ground(-30f, 50f));
        // Aimed away from the weak point, every hit is a plain 25: six hits.
        int wood = 0;
        for (int i = 0; i < 6; i++)
        {
            Swing(p, i == 0 ? TrunkFace : FarFrom(_h.Match.Harvest.WeakPoint(Tree)));
            wood += LastHit(1).Gained;
        }
        Assert.True(_h.Match.Harvest.IsDestroyed(Tree));
        HarvestHit last = LastHit(1);
        Assert.True(last.Destroyed);
        Assert.Equal(0, last.Health);
        Assert.Equal(16, last.Gained);                    // 6 + the destroy bonus 10
        Assert.Equal(wood, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(1, _h.Match.EnvironmentDestroyed);
        foreach (int peer in new[] { 1, 2 })
        {
            PacketReader r = SandboxHarness.Body(_h.To(peer, PacketId.HarvestStates).Last());
            Assert.True(HarvestStatesPacket.TryRead(ref r, out ulong mask));
            Assert.Equal(1UL << Tree, mask);
        }

        // Gone from the collision world: walking north passes where the trunk stood.
        var world = new CollisionWorld();
        world.Gather(SouthOfTree, 0, _h.Match.Harvest.DestroyedMask, null);
        Assert.DoesNotContain(new ColliderId(ColliderKind.Harvestable, Tree), world.BoxIds.ToArray());
    }

    // A point on the trunk's south face at least 0.5 m from the weak point.
    private static Vector3 FarFrom(Vector3 weak)
    {
        var low = new Vector3(Trunk.Min.X + 0.1f, 0.3f, Trunk.Min.Z);
        var high = new Vector3(Trunk.Max.X - 0.1f, 2.4f, Trunk.Min.Z);
        return Vector3.Distance(low, weak) > Vector3.Distance(high, weak) ? low : high;
    }

    [Fact]
    public void ADestroyedObject_IsNotHitAgain()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        for (int i = 0; i < 8 && !_h.Match.Harvest.IsDestroyed(Tree); i++) Swing(p, FarFrom(_h.Match.Harvest.WeakPoint(Tree)));
        Assert.True(_h.Match.Harvest.IsDestroyed(Tree));
        int hits = _h.To(1, PacketId.HarvestHit).Count();
        int wood = p.Inventory.Resource(BuildMaterialType.Wood);
        Swing(p, TrunkFace);
        Assert.Equal(hits, _h.To(1, PacketId.HarvestHit).Count());
        Assert.Equal(wood, p.Inventory.Resource(BuildMaterialType.Wood));
    }

    [Fact]
    public void TheWeakPoint_DoublesDamageAndWood_AndMoves()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Vector3 weak = LastHit(1).WeakPoint;
        Assert.Equal(_h.Match.Harvest.WeakPoint(Tree), weak);
        Assert.InRange(weak.Y, 0.4f, 2f);
        Swing(p, weak);
        HarvestHit hit = LastHit(1);
        Assert.True(hit.WeakPointHit);
        Assert.Equal(125 - 50, hit.Health);
        Assert.Equal(12, hit.Gained);
        Assert.NotEqual(weak, hit.WeakPoint);
        Assert.Equal(Trunk.Min.Z, hit.WeakPoint.Z);   // still on the face first hit
    }

    [Fact]
    public void AMissedWeakPoint_IsAPlainHit_AndStays()
    {
        PlayerEntity p = WithTool(1, SouthOfTree);
        _h.Act(p, InputButtons.Fire, TrunkFace);
        Vector3 weak = LastHit(1).WeakPoint;
        Swing(p, FarFrom(weak));
        HarvestHit hit = LastHit(1);
        Assert.False(hit.WeakPointHit);
        Assert.Equal(100, hit.Health);
        Assert.Equal(6, hit.Gained);
        Assert.Equal(weak, hit.WeakPoint);
    }

    [Fact]
    public void TheWeakPoint_IsTheSameForTheSameHits()
    {
        var a = new HarvestWorld(_h.Match.Building);
        var b = new HarvestWorld(_h.Match.Building);
        for (int i = 0; i < 4; i++)
        {
            a.Hit(5, TrunkFace, Vector3.UnitZ);
            b.Hit(5, TrunkFace, Vector3.UnitZ);
            Assert.Equal(a.WeakPoint(5), b.WeakPoint(5));
        }
    }

    [Fact]
    public void WithoutTheHarvestTool_FireIsNoSwing()
    {
        PlayerEntity p = _h.Join(1, SouthOfTree);
        Assert.Equal(ToolKind.Weapon, p.Inventory.Tool);
        _h.Act(p, InputButtons.Fire, TrunkFace);   // a shot at the trunk instead
        Assert.Equal(150, _h.Match.Harvest.Health(Tree));
        Assert.Contains(_h.Packets, s => s.Id == PacketId.ShotFired);
    }

    [Fact]
    public void ARockGivesStone_AndAWreckMetal()
    {
        int rock = Array.FindIndex(GameMap.Harvestables.ToArray(), h => h.Kind == HarvestKind.Rock);
        int wreck = Array.FindIndex(GameMap.Harvestables.ToArray(), h => h.Kind == HarvestKind.Wreck);
        foreach ((int id, BuildMaterialType material, int perHit) in new[] { (rock, BuildMaterialType.Stone, 5), (wreck, BuildMaterialType.Metal, 4) })
        {
            Box b = GameMap.Harvestables[id].Bounds;
            PlayerEntity p = WithTool(10 + id, SandboxHarness.Ground(b.Center.X, b.Min.Z - 1f));
            _h.Act(p, InputButtons.Fire, new Vector3(b.Center.X, b.Min.Y + 0.6f, b.Min.Z));
            Assert.Equal(perHit, p.Inventory.Resource(material));
        }
    }

    [Fact]
    public void AShotStopsAtATree()
    {
        PlayerEntity shooter = _h.Join(1, SouthOfTree);
        PlayerEntity target = _h.Join(2, SandboxHarness.Ground(-36f, 67f));
        _h.Act(shooter, InputButtons.Fire, target.State.Position + new Vector3(0f, 1.2f, 0f));
        Assert.Empty(_h.To(1, PacketId.HitConfirmed));
        Assert.Equal(100, target.Health);
    }

    [Fact]
    public void AJoiningPlayer_GetsTheHarvestStates_AndItsResources()
    {
        _h.Join(1, SouthOfTree);
        Assert.Single(_h.To(1, PacketId.HarvestStates));
        Assert.Single(_h.To(1, PacketId.ResourcesState));
    }

    // ---- Round reset (a battle royale match) ----

    [Fact]
    public void ANewRound_StandsEveryHarvestableUp_AndStartsWithNoResources()
    {
        var h = new RoyaleHarness();
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        Match m = h.Match;
        a.Inventory.SetResource(BuildMaterialType.Wood, 40);
        m.Harvest.Hit(Tree, TrunkFace, Vector3.UnitZ);
        for (int i = 0; i < 10 && !m.Harvest.IsDestroyed(Tree); i++) m.Harvest.Hit(Tree, FarFrom(m.Harvest.WeakPoint(Tree)), Vector3.UnitZ);
        Assert.True(m.Harvest.IsDestroyed(Tree));
        h.Ticks(1);
        m.Leave(2);   // the other is out: the match finishes, then the round closes
        h.TickUntil(() => m.Flow.State == MatchFlowState.WaitingForPlayers || m.Flow.State == MatchFlowState.Starting, 200);
        Assert.Equal(0UL, m.Harvest.DestroyedMask);
        Assert.Equal(150, m.Harvest.Health(Tree));
        Assert.Equal(0, a.Inventory.Resource(BuildMaterialType.Wood));
        var last = h.Packets.Where(s => s.PeerId == 1 && s.Id == PacketId.HarvestStates).Last();
        var r = new PacketReader(last.Data);
        r.TryReadPacketId(out _);
        Assert.True(HarvestStatesPacket.TryRead(ref r, out ulong mask));
        Assert.Equal(0UL, mask);
    }
}
