using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D15, D17 (request §157-§160, §175): a dead player's resources as world items, picked up on touch up to the
// cap; E never takes them; the load-test switch that makes building free.
public class MaterialItemTests
{
    private readonly SandboxHarness _h = new();
    private static readonly Vector3 Spot = new(2f, 0f, -6f);

    // 기능: 자원만 가진 피해자(Peer 1)를 Spot에 두고 사수(Peer 2)가 한 발로 죽여 자원 아이템이 떨어지게 한다. 피해자는 부활하지 않게 둔다.
    // 입력: wood - 피해자의 나무 수, metal - 피해자의 금속 수.
    // 출력: 죽은 상태의 피해자 PlayerEntity.
    private PlayerEntity KillWithResources(int wood, int metal)
    {
        PlayerEntity victim = _h.Join(1, Spot);
        PlayerEntity shooter = _h.Join(2, Spot + new Vector3(0f, 0f, -5f));
        victim.Inventory.Clear();   // nothing but resources to drop
        victim.Inventory.SetResource(BuildMaterialType.Wood, wood);
        victim.Inventory.SetResource(BuildMaterialType.Metal, metal);
        victim.Health = 1;
        victim.Shield = 0;
        victim.RespawnAtTick = uint.MaxValue;
        _h.Act(shooter, InputButtons.Fire, Spot + new Vector3(0f, 1.2f, 0f));
        Assert.False(victim.Alive);
        victim.RespawnAtTick = uint.MaxValue;   // stays dead: the sandbox would respawn it
        return victim;
    }

    // 기능: 월드 아이템 중 Material 종류만 모은다.
    // 입력: 없음.
    // 출력: Material 아이템 데이터 배열.
    private WorldItemData[] Materials() =>
        Enumerable.Range(0, _h.Match.WorldItems.Count).Select(i => _h.Match.WorldItems[i].Data).Where(d => d.Kind == ItemKind.Material).ToArray();

    [Fact]
    public void ADeadPlayer_DropsItsResources_OneItemPerMaterial()
    {
        PlayerEntity victim = KillWithResources(wood: 40, metal: 5);
        WorldItemData[] dropped = Materials();
        Assert.Equal(2, dropped.Length);
        Assert.Contains(dropped, d => d.DefId == 1 + (int)BuildMaterialType.Wood && d.Amount == 40);
        Assert.Contains(dropped, d => d.DefId == 1 + (int)BuildMaterialType.Metal && d.Amount == 5);
        Assert.Equal(0, victim.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(0, victim.Inventory.Resource(BuildMaterialType.Metal));
        Assert.Contains(_h.To(2, PacketId.ItemSpawned), s =>
        {
            PacketReader r = SandboxHarness.Body(s);
            return ItemSpawnedPacket.TryRead(ref r, out WorldItemData item) && item.Kind == ItemKind.Material;
        });
    }

    [Fact]
    public void WalkingOverResources_PicksThemUp_UpToTheCap()
    {
        KillWithResources(wood: 40, metal: 5);
        PlayerEntity looter = _h.Join(3, Spot + new Vector3(8f, 0f, 0f));
        looter.Inventory.SetResource(BuildMaterialType.Wood, _h.Match.Building.MaxResource - 15);
        _h.Place(looter, Spot);
        _h.Ticks(ItemRules.MaterialPickupEveryTicks);
        Assert.Equal(_h.Match.Building.MaxResource, looter.Inventory.Resource(BuildMaterialType.Wood));
        Assert.Equal(5, looter.Inventory.Resource(BuildMaterialType.Metal));
        WorldItemData left = Assert.Single(Materials());
        Assert.Equal(25, left.Amount);   // 40 - 15 stays on the ground
    }

    [Fact]
    public void E_NeverPicksUpResources()
    {
        KillWithResources(wood: 40, metal: 0);
        PlayerEntity looter = _h.Join(3, Spot + new Vector3(8f, 0f, 0f));
        looter.Inventory.SetResource(BuildMaterialType.Wood, _h.Match.Building.MaxResource);   // full: the touch takes nothing
        _h.Place(looter, Spot);
        Assert.Equal(-1, _h.Match.WorldItems.FindNearest(Spot, ItemRules.PickupRange, ItemRules.PickupHeight));
        _h.Press(looter, InputButtons.Interact);
        PacketReader r = SandboxHarness.Body(_h.To(3, PacketId.PickupResult).Last());
        Assert.True(PickupResult.TryRead(ref r, out PickupResult result));
        Assert.Equal(PickupResultCode.NothingInRange, result.Result);
        Assert.Single(Materials());
    }

    [Fact]
    public void AddStack_PutsMaterialInTheResources_AndIgnoresUnknownKinds()
    {
        var inventory = new Inventory();
        ItemRules.AddStack(inventory, ItemKind.Material, 1 + (byte)BuildMaterialType.Stone, 12);
        Assert.Equal(12, inventory.Resource(BuildMaterialType.Stone));
        Assert.Equal(0, inventory.ShieldCells);
        ItemRules.AddStack(inventory, (ItemKind)9, 2, 5);
        Assert.Equal(0, inventory.ShieldCells);
        Assert.Equal(0, inventory.Medkits);
        Assert.Equal(0, ItemRules.Room(inventory, TestGameData.Items(), ItemKind.Material, 1));
    }

    [Fact]
    public void WithInfiniteResources_BuildingCostsNothing()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 4, DevRespawn = true, BuildInfiniteResources = true });
        PlayerEntity p = h.Join(1, new Vector3(2.5f, 0f, -3f));
        h.Press(p, InputButtons.ToolBuild);
        Vector3 at = BuildGrid.CenterOf(new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0));
        h.Act(p, InputButtons.None, at);
        h.Match.EnqueueBuild(1, new BuildRequest { Sequence = 1, Piece = (byte)BuildPieceType.Wall, X = 16, Y = 0, Z = 16 });
        h.Act(p, InputButtons.None, at);
        Assert.Equal(1, h.Match.BuildPieces);
        Assert.Equal(0, p.Inventory.Resource(BuildMaterialType.Wood));
    }
}
