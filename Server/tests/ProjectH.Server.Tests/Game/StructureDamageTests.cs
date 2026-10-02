using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D10, D11 (request §176): shots and harvest swings against pieces, construction health, destruction. The wall
// stands on the south edge of plaza cell 16 (z 0, x 0..5, 3 m high); shooters stand south of it.
public class StructureDamageTests
{
    private static readonly BuildPieceShape WallShape = new(BuildPieceType.Wall, 16, 0, 16, 0);
    private static readonly Vector3 WallCentre = new(2.5f, 1.5f, 0f);

    private readonly SandboxHarness _h = new();

    // A wood wall that finished building (150 health) unless age says otherwise (the match runs past the longest
    // construction time first).
    private uint Wall(uint age = 1000, BuildMaterialType material = BuildMaterialType.Wood)
    {
        if (age > 0) _h.Ticks(160);
        uint created = _h.Match.ServerTick + 1 > age ? _h.Match.ServerTick + 1 - age : 0;
        return _h.Match.Build.Add(WallShape, material, 99, created, grounded: true);
    }

    private int Health(uint id)
    {
        Assert.True(_h.Match.Build.TryGetSlot(id, out int slot));
        return _h.Match.Build.Health(_h.Match.Build.At(slot), _h.Match.ServerTick);
    }

    private List<(uint Id, ushort Damage)> HealthRecords(int peer)
    {
        var list = new List<(uint, ushort)>();
        foreach (SandboxHarness.Sent sent in _h.To(peer, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int health, out _));
            for (int i = 0; i < placed; i++) BuildPieceRecord.TryReadPlaced(ref r, out _);
            for (int i = 0; i < health; i++)
            {
                Assert.True(BuildEventsPacket.TryReadHealth(ref r, out uint id, out ushort damage));
                list.Add((id, damage));
            }
        }
        return list;
    }

    private List<uint> DestroyedRecords(int peer)
    {
        var list = new List<uint>();
        foreach (SandboxHarness.Sent sent in _h.To(peer, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int health, out int destroyed));
            for (int i = 0; i < placed; i++) BuildPieceRecord.TryReadPlaced(ref r, out _);
            for (int i = 0; i < health; i++) BuildEventsPacket.TryReadHealth(ref r, out _, out _);
            for (int i = 0; i < destroyed; i++)
            {
                Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint id));
                list.Add(id);
            }
        }
        return list;
    }

    private void Shoot(PlayerEntity shooter, Vector3 at)
    {
        _h.Ticks(TestWeapons.AutoInterval);
        _h.Act(shooter, InputButtons.Fire, at);
        _h.Act(shooter, InputButtons.None, at);
    }

    [Fact]
    public void AShot_DamagesTheWall_AndStopsThere()
    {
        uint wall = Wall();
        PlayerEntity shooter = _h.Join(1, new Vector3(2.5f, 0f, -3f));
        PlayerEntity behind = _h.Join(2, new Vector3(2.5f, 0f, 3f));
        _h.Act(shooter, InputButtons.Fire, behind.State.Position + new Vector3(0f, 1.2f, 0f));
        Assert.Equal(150 - TestWeapons.AutoDamage, Health(wall));
        Assert.Equal(100 + TestGameData.LoadoutShield, behind.Health + behind.Shield);
        Assert.Empty(_h.To(1, PacketId.HitConfirmed));
        PacketReader r = SandboxHarness.Body(_h.Packets.Last(s => s.Id == PacketId.ShotFired));
        Assert.True(ShotFired.TryRead(ref r, out ShotFired shot));
        Assert.Equal(-BuildGrid.WallThickness * 0.5f, shot.End.Z, 3);
        foreach (int peer in new[] { 1, 2 }) Assert.Equal((wall, (ushort)TestWeapons.AutoDamage), Assert.Single(HealthRecords(peer)));
    }

    [Fact]
    public void TwoShotsInOneTick_SendOneHealthRecord()
    {
        uint wall = Wall();
        PlayerEntity a = _h.Join(1, new Vector3(1.5f, 0f, -3f));
        PlayerEntity b = _h.Join(2, new Vector3(3.5f, 0f, -3f));
        TestAim.YawPitch(a.State.Position, WallCentre, out float ya, out float pa);
        TestAim.YawPitch(b.State.Position, WallCentre, out float yb, out float pb);
        _h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = ya, AimPitch = pa });
        _h.Send(b, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yb, AimPitch = pb });
        _h.Match.Tick();
        Assert.Equal(150 - 2 * TestWeapons.AutoDamage, Health(wall));
        Assert.Equal((wall, (ushort)(2 * TestWeapons.AutoDamage)), Assert.Single(HealthRecords(1)));
    }

    [Fact]
    public void ShotsToZero_DestroyTheWall_AndThenPassThrough()
    {
        uint wall = Wall();
        PlayerEntity shooter = _h.Join(1, new Vector3(2.5f, 0f, -3f));
        PlayerEntity behind = _h.Join(2, new Vector3(2.5f, 0f, 3f));
        Vector3 chest = behind.State.Position + new Vector3(0f, 1.2f, 0f);
        for (int i = 0; i < 5; i++) Shoot(shooter, chest);
        Assert.False(_h.Match.Build.Contains(wall));
        Assert.Equal(0, _h.Match.BuildPieces);
        Assert.Equal(1, _h.Match.PiecesDestroyed);
        Assert.Equal(wall, Assert.Single(DestroyedRecords(2)));
        Assert.Equal(100 + TestGameData.LoadoutShield, behind.Health + behind.Shield);
        Shoot(shooter, chest);   // the wall is gone
        Assert.Equal(100 + TestGameData.LoadoutShield - TestWeapons.AutoDamage, behind.Health + behind.Shield);
        Assert.Single(_h.To(1, PacketId.HitConfirmed));
    }

    [Fact]
    public void UnderConstruction_HealthGrowsFromTheInitial_AndItCanBeHit()
    {
        BuildingCatalog c = _h.Match.Building;
        BuildMaterialConfig wood = c.Material(BuildMaterialType.Wood);
        var piece = new BuildPiece { Material = BuildMaterialType.Wood, CreatedTick = 1000 };
        Assert.Equal(wood.InitialHealth, _h.Match.Build.Health(piece, 1000));
        Assert.Equal(45, wood.InitialHealth);
        Assert.Equal(96, _h.Match.Build.Health(piece, 1000 + 22));   // 45 + 105 x 22 / 45
        Assert.Equal(150, _h.Match.Build.Health(piece, 1000u + wood.ConstructionTicks));
        Assert.Equal(150, _h.Match.Build.Health(piece, 99999));
        Assert.Equal(45, _h.Match.Build.Health(piece, 10));            // a tick before it: initial
        piece.Damage = 30;
        Assert.Equal(66, _h.Match.Build.Health(piece, 1000 + 22));

        // A fresh wall (45 health) falls to two shots.
        uint wall = Wall(age: 0);
        PlayerEntity shooter = _h.Join(1, new Vector3(2.5f, 0f, -3f));
        _h.Act(shooter, InputButtons.Fire, WallCentre);
        Assert.True(_h.Match.Build.Contains(wall));
        Shoot(shooter, WallCentre);
        Assert.False(_h.Match.Build.Contains(wall));
    }

    [Fact]
    public void Materials_Differ_InHealthAndBuildTime()
    {
        BuildingCatalog c = _h.Match.Building;
        Assert.True(c.Material(BuildMaterialType.Wood).MaxHealth < c.Material(BuildMaterialType.Stone).MaxHealth);
        Assert.True(c.Material(BuildMaterialType.Stone).MaxHealth < c.Material(BuildMaterialType.Metal).MaxHealth);
        Assert.True(c.Material(BuildMaterialType.Wood).ConstructionTicks < c.Material(BuildMaterialType.Stone).ConstructionTicks);
        Assert.True(c.Material(BuildMaterialType.Stone).ConstructionTicks < c.Material(BuildMaterialType.Metal).ConstructionTicks);
        uint metal = Wall(material: BuildMaterialType.Metal);
        Assert.Equal(360, Health(metal));
    }

    [Fact]
    public void TheHarvestTool_DamagesAPiece_AndGivesNothing()
    {
        uint wall = Wall();
        PlayerEntity p = _h.Join(1, new Vector3(2.5f, 0f, -1.2f));
        _h.Press(p, InputButtons.ToolHarvest);
        _h.Act(p, InputButtons.Fire, WallCentre);
        Assert.Equal(150 - _h.Match.Building.HarvestStructureDamage, Health(wall));
        Assert.Equal(0, p.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Empty(_h.To(1, PacketId.HarvestHit));
        for (int i = 0; i < 2; i++)
        {
            _h.Ticks(_h.Match.Building.HarvestCooldownTicks);
            _h.Act(p, InputButtons.Fire, WallCentre);
        }
        Assert.False(_h.Match.Build.Contains(wall));
    }

    [Fact]
    public void TheStructureMultiplier_ScalesShotDamage()
    {
        string json = BuildingCatalog.DefaultJson.Replace(
            "\"constructionSeconds\": 3.0, \"structureDamageMultiplier\": 1.0", "\"constructionSeconds\": 3.0, \"structureDamageMultiplier\": 0.5");
        Assert.True(BuildingCatalog.TryParse(json, 30, out BuildingCatalog? catalog, out _));
        var items = TestGameData.Items();
        var h = new SandboxHarness(data: new GameData(TestWeapons.Create(), items, TestGameData.Loot(items), TestGameData.Zones(), catalog));
        uint stone = h.Match.Build.Add(WallShape, BuildMaterialType.Stone, 99, 0, grounded: true);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -3f));
        h.Ticks(200);
        h.Act(shooter, InputButtons.Fire, WallCentre);
        h.Match.Build.TryGetSlot(stone, out int slot);
        Assert.Equal(TestWeapons.AutoDamage / 2, h.Match.Build.At(slot).Damage);
    }

    [Fact]
    public void BeforeTheMatch_ShotsDoNotDamagePieces()
    {
        var h = new RoyaleHarness(loadout: TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        uint wall = h.Match.Build.Add(WallShape, BuildMaterialType.Wood, a.EntityId, 0, grounded: true);
        h.Place(a, new Vector3(2.5f, 0f, -3f));
        TestAim.YawPitch(a.State.Position, WallCentre, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch });
        h.Match.EnqueueInput(1, packet);
        h.Ticks(1);
        h.Match.Build.TryGetSlot(wall, out int slot);
        Assert.Equal(0, h.Match.Build.At(slot).Damage);
    }

    // ---- The trace itself ----

    private static BuildWorld World(params BuildPieceShape[] shapes)
    {
        var world = new BuildWorld(BuildingCatalog.Default(30));
        foreach (BuildPieceShape s in shapes) world.Add(s, BuildMaterialType.Wood, 1, 0, true);
        return world;
    }

    [Fact]
    public void TheTrace_MeetsRampsRoofsAndWallsWhereTheyAre()
    {
        BuildWorld ramp = World(new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, 0));
        Assert.True(PieceTrace.Trace(new Vector3(2.5f, 10f, 2.5f), -Vector3.UnitY, 50f, ramp, out _, out _, out float t));
        Assert.Equal(10f - 1.5f, t, 3);
        // Under the high end the slab is overhead; going south at 0.5 m the ray meets its underside at z = 1.25.
        Assert.True(PieceTrace.Trace(new Vector3(2.5f, 0.5f, 8f), -Vector3.UnitZ, 50f, ramp, out _, out _, out t));
        Assert.Equal(8f - 1.25f, t, 3);

        BuildWorld roof = World(new BuildPieceShape(BuildPieceType.Roof, 16, 0, 16, 0));
        Assert.True(PieceTrace.Trace(new Vector3(2.5f, 10f, 2.5f), -Vector3.UnitY, 50f, roof, out _, out _, out t));
        Assert.Equal(10f - 4.5f, t, 3);
        Assert.True(PieceTrace.Trace(new Vector3(-2f, 3.1f, 2.5f), Vector3.UnitX, 50f, roof, out _, out _, out t));
        Assert.Equal(2.5f - (4.5f - 3.1f) / 0.6f + 2f, t, 3);
        Assert.False(PieceTrace.Trace(new Vector3(-2f, 1.5f, 2.5f), Vector3.UnitX, 50f, roof, out _, out _, out _));   // under the ceiling

        // A wall stored with the next cell, met from the cell before it.
        BuildWorld wall = World(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0));
        Assert.True(PieceTrace.Trace(new Vector3(2.5f, 1.5f, -3f), Vector3.UnitZ, 50f, wall, out _, out _, out t));
        Assert.Equal(3f - 0.125f, t, 4);
        Assert.False(PieceTrace.Trace(new Vector3(2.5f, 1.5f, -3f), Vector3.UnitZ, 2f, wall, out _, out _, out _));   // out of range
    }

    // The cell walk finds the same first piece as testing every piece.
    [Fact]
    public void TheTrace_FindsTheSameFirstPiece_AsTestingEveryPiece()
    {
        var rng = new Random(1311);
        var shapes = new List<BuildPieceShape>();
        var keys = new HashSet<uint>();
        while (shapes.Count < 300)
        {
            var type = (BuildPieceType)rng.Next(4);
            if (!BuildGrid.TryNormalize(type, 10 + rng.Next(12), rng.Next(4), 10 + rng.Next(12), rng.Next(4), out BuildPieceShape s)) continue;
            if (keys.Add(BuildGrid.SlotKey(s))) shapes.Add(s);
        }
        BuildWorld world = World(shapes.ToArray());
        for (int n = 0; n < 2000; n++)
        {
            var origin = new Vector3(-35f + (float)rng.NextDouble() * 70f, (float)rng.NextDouble() * 14f, -35f + (float)rng.NextDouble() * 70f);
            var direction = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 1.4f - 0.7f, (float)rng.NextDouble() * 2f - 1f));
            bool hit = PieceTrace.Trace(origin, direction, 60f, world, out uint id, out _, out float t);
            float best = 60f;
            uint bestId = 0;
            for (int i = 0; i < shapes.Count; i++)
            {
                if (PieceTrace.Hit(shapes[i], origin, direction, best, out float ti) && ti < best)
                {
                    best = ti;
                    bestId = (uint)(i + 1);
                }
            }
            Assert.Equal(bestId != 0, hit);
            if (hit) Assert.Equal(best, t, 4);
        }
    }

    [Fact]
    public void TheTrace_AllocatesNothing()
    {
        BuildWorld world = World(new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, 0), new BuildPieceShape(BuildPieceType.Wall, 17, 0, 16, 1));
        PieceTrace.Trace(new Vector3(-10f, 1f, 2f), Vector3.UnitX, 50f, world, out _, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) PieceTrace.Trace(new Vector3(-10f, 1f, 2f + i * 0.001f), Vector3.UnitX, 50f, world, out _, out _, out _);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
