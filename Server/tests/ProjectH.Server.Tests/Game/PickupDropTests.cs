using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D8, D9, D12 through Match.Tick. The world starts empty (no loot points) unless a test says otherwise,
// so every item in it was put there by the test.
public class PickupDropTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    // 기능: CombatLoadout과 빈 월드(Loot 지점 없음)의 테스트 Match를 만든다.
    // 입력: 없음.
    // 출력: _match가 준비된 테스트 인스턴스.
    public PickupDropTests()
    {
        _match = NewMatch(TestGameData.CombatLoadout);
    }

    // 기능: DevRespawn Match를 만든다. 보낸 패킷은 _sent에 기록한다.
    // 입력: loadout - 시작 장비, lootPoints - Loot 지점(null이면 없음), data - 게임 데이터(null이면 테스트 데이터).
    // 출력: 새 Match.
    private Match NewMatch(StartingLoadout loadout, LootPoint[]? lootPoints = null, GameData? data = null) =>
        new(new ServerOptions { MaxPlayers = 3, DevRespawn = true }, data ?? TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), loadout,
            lootPoints ?? Array.Empty<LootPoint>());

    // 기능: Peer를 "p<peer>" 이름으로 참가시키고 feet 위치·yaw로 둔다(Lag Compensation 기록 포함). 참가가 거부되면 테스트를 실패시킨다.
    // 입력: peer - 참가할 Peer ID, feet - 발 위치, yaw - 바라보는 방향.
    // 출력: 참가해 배치된 PlayerEntity.
    private PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.State.Yaw = yaw;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    // 기능: 플레이어별 다음 Seq를 붙여 버튼 입력 하나를 Match에 넣는다(AimPitch 10도, Tick은 돌리지 않는다).
    // 입력: player - 입력을 보낼 플레이어, buttons - 누를 버튼, yaw - 바라보는 방향(NaN이면 현재 방향 유지).
    // 출력: 반환값 없음. Match 입력 큐에 명령이 들어간다.
    private void Send(PlayerEntity player, InputButtons buttons, float yaw = float.NaN)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        _seq[player.PeerId] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        // Yaw NaN keeps the player's facing (MovementSimulation ignores a non-finite yaw).
        packet.Set(0, new InputCommand { Seq = seq, Buttons = buttons, Yaw = yaw, AimPitch = 10f });
        _match.EnqueueInput(player.PeerId, packet);
    }

    // 기능: 버튼 입력을 넣고 한 Tick 돌린다(방향은 유지).
    // 입력: player - 입력을 보낼 플레이어, buttons - 누를 버튼.
    // 출력: 반환값 없음. 입력이 처리된 뒤 Match가 한 Tick 진행된다.
    private void Press(PlayerEntity player, InputButtons buttons)
    {
        Send(player, buttons);
        _match.Tick();
    }

    // 기능: 월드에 아이템 하나를 떨어뜨린다(Loot 지점 없음).
    // 입력: kind - 아이템 종류, defId - 정의 ID, amount - 수량, position - 위치, rarity - 희귀도.
    // 출력: 생성된 아이템 ID.
    private ushort Put(ItemKind kind, byte defId, ushort amount, Vector3 position, byte rarity = 0) =>
        _match.SpawnItem(new LootRoll(kind, defId, rarity, amount), position, -1);

    // 기능: 아이템 ID로 월드 아이템 인덱스를 찾는다.
    // 입력: itemId - 아이템 ID.
    // 출력: 월드 아이템 인덱스, 없으면 -1.
    private int IndexOf(ushort itemId) => _match.WorldItems.IndexOf(itemId);

    // 기능: 월드 아이템 전부의 데이터를 인덱스 순으로 모은다.
    // 입력: 없음.
    // 출력: WorldItemData 목록.
    private List<WorldItemData> WorldList() =>
        Enumerable.Range(0, _match.WorldItems.Count).Select(i => _match.WorldItems[i].Data).ToList();

    // 기능: 송신 기록의 PacketId를 건너뛴 본문 위치의 PacketReader를 만든다.
    // 입력: s - 송신 기록.
    // 출력: 본문 첫 바이트를 가리키는 PacketReader.
    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    // 기능: 특정 Peer에게 보낸 PickupResult를 순서대로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: peer - 받은 Peer ID.
    // 출력: 읽은 PickupResult 목록.
    private List<PickupResult> ResultsTo(int peer) => _sent.Where(s => s.PeerId == peer && s.Id == PacketId.PickupResult)
        .Select(s => { var r = Reader(s); Assert.True(PickupResult.TryRead(ref r, out var v)); return v; }).ToList();

    // 기능: 특정 Peer에게 마지막으로 보낸 InventoryState를 읽는다. 없거나 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: peer - 받은 Peer ID.
    // 출력: 읽은 InventoryState.
    private InventoryState LastInventoryTo(int peer)
    {
        var r = Reader(_sent.Last(s => s.PeerId == peer && s.Id == PacketId.InventoryState));
        Assert.True(InventoryState.TryRead(ref r, out var state));
        return state;
    }

    // 기능: 특정 Peer에게 보낸 ItemSpawned의 아이템 데이터를 순서대로 읽는다. 읽기에 실패하면 테스트를 실패시킨다.
    // 입력: peer - 받은 Peer ID.
    // 출력: 읽은 WorldItemData 목록.
    private List<WorldItemData> SpawnedTo(int peer) => _sent.Where(s => s.PeerId == peer && s.Id == PacketId.ItemSpawned)
        .Select(s => { var r = Reader(s); Assert.True(ItemSpawnedPacket.TryRead(ref r, out var v)); return v; }).ToList();

    [Fact]
    public void Pickup_WithNothingInRange_ReportsIt_AndChangesNothing()
    {
        var a = Join(1, Vector3.Zero);
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, new Vector3(5f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        var result = Assert.Single(ResultsTo(1));
        Assert.Equal(PickupResultCode.NothingInRange, result.Result);
        Assert.Equal(0, result.ItemId);
        Assert.Equal(1, _match.WorldItems.Count);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.InventoryState);
    }

    // Review Focus / spec §6: 2.0 m is in range, just beyond is not. The client cannot ask for a far item:
    // it sends no item id at all (D8).
    [Theory]
    [InlineData(2.0f, true)]
    [InlineData(2.05f, false)]
    public void Pickup_RangeBoundary(float distance, bool expectedTaken)
    {
        var a = Join(1, new Vector3(-3f, 0f, 0f));
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, new Vector3(-3f + distance, 0f, 0f));

        Press(a, InputButtons.Interact);

        Assert.Equal(expectedTaken, IndexOf(id) < 0);
        Assert.Equal(expectedTaken ? 30 : 0, a.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(expectedTaken ? PickupResultCode.Ok : PickupResultCode.NothingInRange, ResultsTo(1).Single().Result);
    }

    // Review Focus: two players press E for the same item in the same tick. The first processed takes it;
    // the second finds nothing, and the item is removed exactly once.
    [Fact]
    public void TwoPlayers_SameTick_SameItem_OnlyTheFirstGetsIt()
    {
        var a = Join(1, new Vector3(-1f, 0f, 0f));
        var b = Join(2, new Vector3(1f, 0f, 0f));
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, Vector3.Zero);
        _sent.Clear();

        Send(a, InputButtons.Interact);
        Send(b, InputButtons.Interact);
        _match.Tick();

        Assert.Equal(30, a.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(0, b.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Single().Result);
        Assert.Equal(PickupResultCode.NothingInRange, ResultsTo(2).Single().Result);
        foreach (int peer in new[] { 1, 2 })
        {
            var removed = Assert.Single(_sent, s => s.PeerId == peer && s.Id == PacketId.ItemRemoved);
            var r = Reader(removed);
            Assert.True(ItemRemoved.TryRead(ref r, out var rm));
            Assert.Equal(id, rm.ItemId);
        }
    }

    [Fact]
    public void Weapon_GoesIntoTheFirstEmptySlot_CurrentSlotStays()
    {
        var a = Join(1, Vector3.Zero);   // slots: Auto, Semi, empty
        ushort id = Put(ItemKind.Weapon, TestWeapons.LightId, 7, new Vector3(1f, 0f, 0f), rarity: 3);
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(-1, IndexOf(id));
        Assert.Equal(TestWeapons.LightId, a.Inventory.Slots[2].Weapon!.Id);
        Assert.Equal(3, a.Inventory.Slots[2].Rarity);
        Assert.Equal(7, a.Inventory.Slots[2].MagAmmo);
        Assert.Equal(0, a.Inventory.CurrentSlot);
        var state = LastInventoryTo(1);
        Assert.Equal(TestWeapons.LightId, state.Slot2.WeaponId);
        Assert.Equal(7, state.Slot2.MagAmmo);
    }

    // Empty-handed (the current slot is empty), the picked weapon also goes into the hand.
    [Fact]
    public void Weapon_PickedUpEmptyHanded_IsTakenInHand()
    {
        _match = NewMatch(new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) } });
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot3);
        Assert.Equal(2, a.Inventory.CurrentSlot);
        Put(ItemKind.Weapon, TestWeapons.SemiId, 2, new Vector3(1f, 0f, 0f));

        Press(a, InputButtons.Interact);

        Assert.Equal(TestWeapons.SemiId, a.Inventory.Slots[1].Weapon!.Id);   // first empty slot
        Assert.Equal(1, a.Inventory.CurrentSlot);                             // and in hand
    }

    // D9: all slots full -> swap with the current slot; the old weapon lies in front of the player (like a
    // G-drop, not on the loot point), with its magazine and rarity.
    [Fact]
    public void Weapon_AllSlotsFull_SwapsWithTheCurrentSlot_OldWeaponDropsInItsPlace()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 4), new LoadoutWeapon(TestWeapons.LightId, 0) },
            HeavyAmmo = 10,
        });
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot2);
        a.Inventory.Slots[1].MagAmmo = 1;
        a.Reloading = true;
        a.ReloadEndTick = _match.ServerTick + 50;
        var spot = new Vector3(0f, 0f, 1.5f);
        Put(ItemKind.Weapon, TestWeapons.AutoId, 4, spot, rarity: 2);
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(TestWeapons.AutoId, a.Inventory.Slots[1].Weapon!.Id);
        Assert.Equal(2, a.Inventory.Slots[1].Rarity);
        Assert.Equal(4, a.Inventory.Slots[1].MagAmmo);
        Assert.False(a.Reloading);   // the reload belonged to the weapon that left the hand
        var dropped = Assert.Single(WorldList());
        Assert.Equal(ItemKind.Weapon, dropped.Kind);
        Assert.Equal(TestWeapons.SemiId, dropped.DefId);
        Assert.Equal(4, dropped.Rarity);
        Assert.Equal(1, dropped.Amount);
        // Not on the loot point (a respawned point item would overlap it): placed like a G-drop.
        Assert.NotEqual(spot, dropped.Position);
        Assert.Equal(ItemRules.DropPosition(a.State.Position, ItemRules.Offset(a.State.Yaw, ItemRules.DropDistance), GameMap.Boxes, GameMap.Terrain), dropped.Position);
        Assert.True(_match.WorldItems[0].IsDropped);
    }

    // D9: ammo is taken up to the type's max; the rest stays on the ground with the smaller amount.
    [Fact]
    public void Ammo_PartialPickup_LeavesTheRest()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.SetAmmo(AmmoType.Heavy, TestGameData.HeavyMax - 4);
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Heavy, 10, new Vector3(1f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(TestGameData.HeavyMax, a.Inventory.GetAmmo(AmmoType.Heavy));
        Assert.Equal(6, _match.WorldItems[IndexOf(id)].Data.Amount);
        var update = Assert.Single(SpawnedTo(1));   // the amount change is an upsert
        Assert.Equal(id, update.ItemId);
        Assert.Equal(6, update.Amount);
        Assert.Equal(TestGameData.HeavyMax, LastInventoryTo(1).HeavyAmmo);
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Single().Result);
    }

    [Fact]
    public void Ammo_AtMax_IsFull_AndTheItemStays()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.SetAmmo(AmmoType.Heavy, TestGameData.HeavyMax);
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Heavy, 10, new Vector3(1f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        var result = ResultsTo(1).Single();
        Assert.Equal(PickupResultCode.Full, result.Result);
        Assert.Equal(id, result.ItemId);
        Assert.Equal(10, _match.WorldItems[IndexOf(id)].Data.Amount);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.ItemRemoved || s.Id == PacketId.InventoryState);
    }

    // D9: stack limits Medkit 3, Shield Cell 6.
    [Fact]
    public void Consumables_StopAtTheStackLimit()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.Medkits = TestGameData.MedkitMaxStack - 1;
        ushort medkits = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 2, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.Interact);
        Assert.Equal(TestGameData.MedkitMaxStack, a.Inventory.Medkits);
        Assert.Equal(1, _match.WorldItems[IndexOf(medkits)].Data.Amount);

        _match.RemoveItemAt(IndexOf(medkits));
        ushort cells = Put(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 9, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.None);   // final review A5: released between presses
        PastItemInterval();            // review fix C6
        Press(a, InputButtons.Interact);
        Assert.Equal(TestGameData.ShieldCellMaxStack, a.Inventory.ShieldCells);
        Assert.Equal(3, _match.WorldItems[IndexOf(cells)].Data.Amount);
        Assert.Equal(TestGameData.ShieldCellMaxStack, LastInventoryTo(1).ShieldCells);
    }

    // D12: G drops the current weapon 1 m in front, magazine included.
    [Fact]
    public void Drop_PutsTheCurrentWeapon1mInFront_WithItsMagazine()
    {
        var a = Join(1, new Vector3(2f, 0f, 3f), yaw: 90f);   // facing +X
        a.Inventory.Slots[0].MagAmmo = 4;
        a.Reloading = true;
        a.ReloadEndTick = _match.ServerTick + 50;
        _sent.Clear();

        Press(a, InputButtons.Drop);

        Assert.True(a.Inventory.Slots[0].IsEmpty);
        Assert.False(a.Reloading);
        var item = Assert.Single(SpawnedTo(1));
        Assert.Equal(ItemKind.Weapon, item.Kind);
        Assert.Equal(TestWeapons.AutoId, item.DefId);
        Assert.Equal(4, item.Amount);
        Assert.Equal(3f, item.Position.X, 3);
        Assert.Equal(0f, item.Position.Y);
        Assert.Equal(3f, item.Position.Z, 3);
        Assert.True(LastInventoryTo(1).Slot0.IsEmpty);
    }

    [Fact]
    public void Drop_EmptyHanded_DoesNothing()
    {
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot3);
        _sent.Clear();
        Press(a, InputButtons.Drop);
        Assert.Equal(0, _match.WorldItems.Count);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.InventoryState);
    }

    // Phase 13 final review A5: G held over several inputs drops once, even when a weapon is back in hand meanwhile.
    [Fact]
    public void HeldG_DropsOnce()
    {
        var a = Join(1, Vector3.Zero);
        HeldWeapon weapon = a.Inventory.Slots[0];
        Assert.False(weapon.IsEmpty);
        Press(a, InputButtons.Drop);
        Assert.Equal(1, _match.WorldItems.Count);
        a.Inventory.Slots[0] = weapon;   // in hand again (as a pickup would)
        PastItemInterval();              // review fix C6: only the held button keeps the next two from dropping
        Press(a, InputButtons.Drop);
        Press(a, InputButtons.Drop);
        Assert.Equal(1, _match.WorldItems.Count);
        Assert.False(a.Inventory.Slots[0].IsEmpty);
        Press(a, InputButtons.None);
        Press(a, InputButtons.Drop);
        Assert.Equal(2, _match.WorldItems.Count);
    }

    // Final review B10: a weapon dropped on a ramp lies on the ramp's surface ahead, not on the terrain under it.
    [Fact]
    public void Drop_OnARamp_LandsOnItsSurface()
    {
        SandboxHarness.AddPiece(_match, new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, 0));   // rises +Z, 0.6 per metre from z 0
        var a = Join(1, new Vector3(2.5f, 0.6f * (2f + MoveSettings.HalfWidth), 2f), yaw: 0f);
        Press(a, InputButtons.Drop);
        var item = Assert.Single(WorldList());
        Assert.Equal(3f, item.Position.Z, 3);
        Assert.Equal(0.6f * 3f, item.Position.Y, 3);
    }

    // Placement fallback: 1 m in front is behind (or inside) a wall, so the weapon lands at the feet. The
    // field wall (26, 1.5, 0) is 0.5 m thick (x 25.75-26.25): 1 m from x 25.3 is already past it.
    [Theory]
    [InlineData(25.3f)]
    [InlineData(25.0f)]
    public void Drop_IntoOrThroughAWall_LandsAtTheFeet(float x)
    {
        var a = Join(1, new Vector3(x, 0f, 0f), yaw: 90f);
        Press(a, InputButtons.Drop);
        var item = Assert.Single(WorldList());
        Assert.Equal(new Vector3(x, 0f, 0f), item.Position);
    }

    // Placement: a weapon dropped from a box edge lands on the floor, where it can be picked up.
    [Fact]
    public void Drop_OverABoxEdge_LandsOnTheFloor()
    {
        // Low crate (0, 0.5, 22), 2 x 1 x 2: top at y 1, north edge at z 23.
        var a = Join(1, new Vector3(0f, 1f, 22.8f), yaw: 0f);
        Press(a, InputButtons.Drop);
        var item = Assert.Single(WorldList());
        Assert.Equal(0f, item.Position.Y);
        Assert.Equal(23.8f, item.Position.Z, 3);
    }

    // 기능: 점이 맵 상자 중 하나의 안쪽(바닥면 포함, 윗면 제외, 옆면 제외)에 있는지 본다.
    // 입력: p - 검사할 점.
    // 출력: 어느 상자 안에든 있으면 true.
    // Inside = strictly within the footprint and at or above the bottom, below the top. The bottom face
    // counts: an item "on the floor" under a floor-standing box (y 0 = Min.Y) is inside it. The top face
    // does not: that is standing on the box.
    private static bool StrictlyInsideAnyBox(Vector3 p)
    {
        foreach (Box b in GameMap.Boxes)
        {
            if (p.X > b.Min.X && p.X < b.Max.X && p.Y >= b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z)
                return true;
        }
        return false;
    }

    // Placement: in mid-air next to a box, the knee-height ray passes over the box top, and that top is
    // above the feet so it is not ground either. The point 1 m ahead would lie inside the low crate
    // (0, 0.5, 22), x -1..1, y 0..1, z 21..23; the item lands at the feet instead, on the floor.
    [Fact]
    public void Drop_InMidAirNextToABox_DoesNotLandInsideIt()
    {
        var feet = new Vector3(0f, 0.8f, 20.6f);
        Vector3 p = ItemRules.DropPosition(feet, ItemRules.Offset(0f, ItemRules.DropDistance), GameMap.Boxes, GameMap.Terrain);
        Assert.False(StrictlyInsideAnyBox(p));
        Assert.Equal(new Vector3(0f, 0f, 20.6f), p);
    }

    // The same through a death in mid-air: no point of the circle lies inside a box.
    [Fact]
    public void DeathDrop_InMidAirNextToABox_PutsNothingInsideIt()
    {
        var a = Join(1, new Vector3(0f, 0f, 14f));
        var b = Join(2, new Vector3(0f, 0.8f, 20.6f));   // joins after a: still in mid-air when a fires
        b.Health = 1;
        b.Shield = 0;
        _sent.Clear();

        ShootAt(a, b);

        Assert.False(b.Alive);
        var drops = SpawnedTo(1);
        Assert.Equal(4, drops.Count);   // Auto, Semi, Medium, Heavy
        Assert.All(drops, d => Assert.False(StrictlyInsideAnyBox(d.Position), $"drop at {d.Position} is inside a box"));
    }

    // Review Focus (Phase 6 D11): a death on the north hill's slope scatters every item onto the terrain, none in the
    // air or under the ground.
    [Fact]
    public void DeathDrop_OnASlope_EveryItemLiesOnTheTerrain()
    {
        var a = Join(1, new Vector3(0f, GameMap.Terrain.Height(0f, 30f), 30f));
        var b = Join(2, new Vector3(0f, GameMap.Terrain.Height(0f, 36f), 36f));
        b.Health = 1;
        b.Shield = 0;
        _sent.Clear();

        ShootAt(a, b);

        Assert.False(b.Alive);
        var drops = SpawnedTo(1);
        Assert.Equal(4, drops.Count);   // Auto, Semi, Medium, Heavy
        Assert.All(drops, d => Assert.Equal(GameMap.Terrain.Height(d.Position.X, d.Position.Z), d.Position.Y));
        Assert.Contains(drops, d => d.Position.Y != b.State.Position.Y);   // really on the slope, not flat at the feet
    }

    // Keep the per-slot fire interval: fire the semi, drop it, pick it up into another slot, and the next
    // shot still waits for the interval.
    [Fact]
    public void DropAndPickUpAgain_DoesNotSkipTheFireInterval()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.Slots[0] = default;                     // slot 0 free (review fix C6: set up, not a G that would start the item interval)
        Press(a, InputButtons.Slot2);
        Send(a, InputButtons.Fire);
        _match.Tick();
        uint firedAt = _match.ServerTick - 1;               // the tick's "now"
        Assert.Equal(TestWeapons.SemiMagazine - 1, a.Inventory.Slots[1].MagAmmo);
        Press(a, InputButtons.Drop);                        // semi out of slot 1
        PastItemInterval();                                 // review fix C6 (still inside the semi's 15-tick interval)
        Press(a, InputButtons.Interact);                    // back in, into slot 0 (the first empty one)
        Assert.Equal(TestWeapons.SemiId, a.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(0, a.Inventory.CurrentSlot);           // empty-handed: taken in hand

        int shots = 0;
        while (_match.ServerTick <= firedAt + TestWeapons.SemiInterval + 2)
        {
            uint now = _match.ServerTick;
            Send(a, (now % 2 == 0) ? InputButtons.Fire : InputButtons.None);
            int before = a.Inventory.Slots[0].MagAmmo;
            _match.Tick();
            if (a.Inventory.Slots[0].MagAmmo < before)
            {
                shots++;
                Assert.True(now >= firedAt + TestWeapons.SemiInterval, $"fired at {now}, interval ends at {firedAt + TestWeapons.SemiInterval}");
            }
        }
        Assert.Equal(1, shots);   // and it does fire once the interval is over
    }

    // 기능: Fire를 누르고 떼기를 번갈아 보내며 slot의 탄창이 줄어든 첫 Tick(그 Tick의 now)을 찾는다.
    // 입력: player - 쏘는 플레이어, slot - 지켜볼 칸, maxTicks - 최대 Tick 수.
    // 출력: 처음 쏜 Tick의 now. 그 안에 쏘지 않으면 테스트가 실패한다.
    private uint FirstShotTick(PlayerEntity player, int slot, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            uint now = _match.ServerTick;
            Send(player, now % 2 == 0 ? InputButtons.Fire : InputButtons.None);
            int before = player.Inventory.Slots[slot].MagAmmo;
            _match.Tick();
            if (player.Inventory.Slots[slot].MagAmmo < before) return now;
        }
        Assert.Fail($"no shot within {maxTicks} ticks");
        return 0;
    }

    // Review fix C2 (SEC-8): a weapon picked up into the hand (empty-handed, or swapped with the one in hand) waits its equip
    // ticks before it fires, like a slot switch, so a pickup cannot skip the wait.
    [Fact]
    public void AWeaponPickedUpIntoTheHand_WaitsItsEquipTicks()
    {
        _match = NewMatch(new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) } },
            data: TestGameData.Create(equipSeconds: 0.4f));   // 12 ticks
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot2);                                   // empty-handed
        Put(ItemKind.Weapon, TestWeapons.SemiId, 2, new Vector3(1f, 0f, 0f));
        uint pickedAt = _match.ServerTick;
        Press(a, InputButtons.Interact);
        Assert.Equal(1, a.Inventory.CurrentSlot);
        Assert.Equal(pickedAt + 12, a.SwitchReadyTick);
        Assert.True(FirstShotTick(a, 1, 30) >= pickedAt + 12);

        // A swap with all three slots full brings the ground weapon into the hand too.
        Press(a, InputButtons.Slot3);
        Put(ItemKind.Weapon, TestWeapons.LightId, 10, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.Interact);                                // into slot 2 (the hand), not a swap yet
        Put(ItemKind.Weapon, TestWeapons.AutoId, 6, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.None);                                    // E released, so the next E is a new press
        for (int i = 0; i < 10; i++) _match.Tick();                     // past the item action interval (review fix C6)
        uint swappedAt = _match.ServerTick;
        Press(a, InputButtons.Interact);                                // all full: swaps with slot 2
        Assert.Equal(TestWeapons.AutoId, a.Inventory.Slots[2].Weapon!.Id);
        Assert.Equal(swappedAt + 12, a.SwitchReadyTick);
    }

    // Review fix C6 (SEC-20): G and an E pickup share one item action interval (0.25 s = 8 ticks at 30 Hz), counted from every
    // attempt, done or not, so a client alternating them every tick makes at most four Reliable item changes a second.
    [Fact]
    public void AlternatingDropAndPickup_IsLimitedToFourPerSecond()
    {
        _match = NewMatch(new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) } });
        var a = Join(1, Vector3.Zero);
        Assert.Equal(8u, ItemRules.ActionIntervalTicks(30));
        // A client that presses, every tick, whatever would change its hand: G while holding, E while empty-handed (a button
        // already held is released for one tick first, so every press is a new one).
        bool held = true;
        int changes = 0;
        InputButtons last = InputButtons.None;
        for (int i = 0; i < 30; i++)
        {
            InputButtons want = held ? InputButtons.Drop : InputButtons.Interact;
            last = want == last ? InputButtons.None : want;
            Press(a, last);
            bool now = !a.Inventory.Slots[0].IsEmpty;
            if (now != held) changes++;
            held = now;
        }
        Assert.InRange(changes, 3, 4);
        Assert.Equal(1, Enumerable.Range(0, 3).Count(s => !a.Inventory.Slots[s].IsEmpty) + _match.WorldItems.Count);   // conserved
    }

    // 기능: 아이템 행동 간격(리뷰 수정 C6)이 지나도록 입력 없이 Tick을 돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void PastItemInterval()
    {
        for (uint i = 0; i < ItemRules.ActionIntervalTicks(30); i++) _match.Tick();
    }

    // ---- Review fix C4 (SEC-9): an item is picked up only in line of sight from the eye ----

    // 기능: 지금 경기의 원점 근처 칸 경계에 나무 벽을 세운다.
    // 입력: 없음.
    // 출력: 벽의 경계 상자(벽은 X로 길고 Z로 얇다).
    private Box WallNearTheOrigin()
    {
        int cell = (int)MathF.Floor(GameMap.HalfSize / BuildGrid.CellSize);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, cell, 0, cell, 0, out BuildPieceShape wall));
        Assert.Equal(ProjectH.Shared.Protocol.BuildResultCode.Ok, _match.PlacePiece(wall, BuildMaterialType.Wood, out _));
        Box bounds = BuildGrid.BoundsOf(wall);
        Assert.True(bounds.Max.X - bounds.Min.X > 3f && bounds.Max.Z - bounds.Min.Z < 0.5f, "a wall along X");
        return bounds;
    }

    // 기능: 지면 위 점(지형 높이)을 만든다.
    // 입력: x·z - 수평 위치.
    // 출력: 그 자리의 지면 점.
    private static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    [Fact]
    public void PickingUp_ThroughABuildWall_IsNothingInRange()
    {
        Box wall = WallNearTheOrigin();
        float x = (wall.Min.X + wall.Max.X) * 0.5f;
        var a = Join(1, Ground(x, wall.Min.Z - 0.6f));
        ushort id = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 1, Ground(x, wall.Max.Z + 0.6f));
        Press(a, InputButtons.Interact);
        Assert.Equal(PickupResultCode.NothingInRange, ResultsTo(1).Last().Result);
        Assert.True(IndexOf(id) >= 0);
        Assert.Equal(0, a.Inventory.Medkits);

        // The same item on the player's side of the wall is taken (past the item action interval).
        Press(a, InputButtons.None);
        for (int i = 0; i < 10; i++) _match.Tick();
        ushort near = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 1, Ground(x + 0.5f, wall.Min.Z - 1.2f));
        Press(a, InputButtons.Interact);
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Last().Result);
        Assert.True(IndexOf(near) < 0);
    }

    // The south door of the map (GameMap.Doors[0], centre x -54, z 50.25). The player faces away from it, so E is a pickup.
    [Fact]
    public void PickingUp_ThroughAClosedDoor_IsNothingInRange_AndOkWhenOpen()
    {
        Box door = GameMap.Doors[0];
        float x = (door.Min.X + door.Max.X) * 0.5f;
        var a = Join(1, Ground(x, door.Min.Z - 0.6f), yaw: 180f);
        ushort id = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 1, Ground(x, door.Max.Z + 0.6f));
        Assert.False(_match.Doors.IsOpen(0));
        Press(a, InputButtons.Interact);
        Assert.False(_match.Doors.IsOpen(0));   // not a door press
        Assert.Equal(PickupResultCode.NothingInRange, ResultsTo(1).Last().Result);
        Assert.True(IndexOf(id) >= 0);

        _match.Doors.Set(0, true);
        Press(a, InputButtons.None);
        for (int i = 0; i < 10; i++) _match.Tick();
        Press(a, InputButtons.Interact);
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Last().Result);
        Assert.True(IndexOf(id) < 0);
    }

    // A map box (a building wall) between the eye and the item blocks the pickup the same way.
    [Fact]
    public void PickingUp_ThroughAWall_IsNothingInRange()
    {
        Assert.True(FindThinMapWall(out Vector3 near, out Vector3 far), "a thin map wall with open ground on both sides");
        var a = Join(1, near);
        ushort id = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 1, far);
        Press(a, InputButtons.Interact);
        Assert.Equal(PickupResultCode.NothingInRange, ResultsTo(1).Last().Result);
        Assert.True(IndexOf(id) >= 0);
    }

    // 기능: 두께 0.8 m 이하이고 눈보다 높은 맵 상자 하나와 그 양쪽 0.5 m의 빈 지면 두 점을 찾는다(X 방향으로 얇은 벽).
    // 입력: near·far - 찾은 두 점.
    // 출력: 찾았으면 true.
    private static bool FindThinMapWall(out Vector3 near, out Vector3 far)
    {
        ReadOnlySpan<Box> boxes = GameMap.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box b = boxes[i];
            float thickness = b.Max.X - b.Min.X;
            if (thickness > 0.8f || thickness < 0.05f || b.Max.Z - b.Min.Z < 2f) continue;
            float z = (b.Min.Z + b.Max.Z) * 0.5f;
            near = Ground(b.Min.X - 0.5f, z);
            far = Ground(b.Max.X + 0.5f, z);
            if (b.Min.Y > near.Y + 0.2f || b.Max.Y < near.Y + 2.5f || MathF.Abs(far.Y - near.Y) > 0.2f) continue;
            if (Blocked(boxes, near) || Blocked(boxes, far)) continue;
            return true;
        }
        near = far = default;
        return false;
    }

    // 기능: 발 위치의 몸(반폭 0.4 m, 높이 1.8 m)이 맵 상자와 겹치는지 본다.
    // 입력: boxes - 맵 상자, feet - 발 위치.
    // 출력: 겹치면 true.
    private static bool Blocked(ReadOnlySpan<Box> boxes, Vector3 feet)
    {
        for (int i = 0; i < boxes.Length; i++)
        {
            Box b = boxes[i];
            if (feet.X > b.Min.X - 0.4f && feet.X < b.Max.X + 0.4f && feet.Z > b.Min.Z - 0.4f && feet.Z < b.Max.Z + 0.4f
                && feet.Y + 1.8f > b.Min.Y && feet.Y < b.Max.Y) return true;
        }
        return false;
    }

    // Resources lying in reach are picked up on touch, but not through a wall.
    [Fact]
    public void MaterialAutoPickup_RespectsLineOfSight()
    {
        Box wall = WallNearTheOrigin();
        float x = (wall.Min.X + wall.Max.X) * 0.5f;
        var a = Join(1, Ground(x, wall.Min.Z - 0.4f));
        int before = a.Inventory.Resource(BuildMaterialType.Wood);
        ushort behind = Put(ItemKind.Material, (byte)(BuildMaterialType.Wood + 1), 10, Ground(x, wall.Max.Z + 0.4f));
        ushort front = Put(ItemKind.Material, (byte)(BuildMaterialType.Wood + 1), 10, Ground(x + 0.6f, wall.Min.Z - 0.9f));
        for (int i = 0; i < 2 * ItemRules.MaterialPickupEveryTicks; i++) _match.Tick();
        Assert.True(IndexOf(behind) >= 0);
        Assert.True(IndexOf(front) < 0);
        Assert.Equal(before + 10, a.Inventory.Resource(BuildMaterialType.Wood));
    }

    // Review C round 1: a player whose resource is full takes nothing, so the sight test is not run for it (the same outcome, the
    // order only saves the trace); the item stays whether or not a wall is in the way.
    [Fact]
    public void MaterialAutoPickup_AFullPlayer_SkipsTheItem_BehindAWallOrNot()
    {
        Box wall = WallNearTheOrigin();
        float x = (wall.Min.X + wall.Max.X) * 0.5f;
        var a = Join(1, Ground(x, wall.Min.Z - 0.4f));
        a.Inventory.SetResource(BuildMaterialType.Wood, _match.Building.MaxResource);
        ushort behind = Put(ItemKind.Material, (byte)(BuildMaterialType.Wood + 1), 10, Ground(x, wall.Max.Z + 0.4f));
        ushort front = Put(ItemKind.Material, (byte)(BuildMaterialType.Wood + 1), 10, Ground(x + 0.6f, wall.Min.Z - 0.9f));
        for (int i = 0; i < 2 * ItemRules.MaterialPickupEveryTicks; i++) _match.Tick();
        Assert.True(IndexOf(behind) >= 0);
        Assert.True(IndexOf(front) >= 0);
        Assert.Equal(_match.Building.MaxResource, a.Inventory.Resource(BuildMaterialType.Wood));
    }

    // The missed-input repeat copies the last input's buttons. It must never repeat a drop or a pickup.
    [Fact]
    public void MissedInputTicks_DoNotRepeatDropOrPickup()
    {
        var a = Join(1, Vector3.Zero);
        // Nearer than the weapon G drops 1 m in front, so each pickup takes ammo.
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 10, new Vector3(0f, 0f, 0.5f));
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 10, new Vector3(0f, 0f, -0.6f));
        _sent.Clear();

        // Review fix C6: G and E share the item interval, so each goes in its own real input, one interval apart.
        Send(a, InputButtons.Drop);
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks
        Send(a, InputButtons.Interact);
        for (int i = 0; i < 12; i++) _match.Tick();

        Assert.Single(SpawnedTo(1));                   // one weapon dropped
        Assert.False(a.Inventory.Slots[1].IsEmpty);    // the semi stayed
        Assert.Single(ResultsTo(1));                   // one pickup
        Assert.Equal(10, a.Inventory.GetAmmo(AmmoType.Light));
    }

    // D12: death drops everything on a circle around the body, after PlayerDied, amounts and magazines kept.
    [Fact]
    public void Death_DropsEverything_OnACircle_AndEmptiesTheInventory()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 5;
        b.Inventory.Medkits = 2;
        b.Health = 1;
        b.Shield = 0;
        _sent.Clear();

        TestAim.YawPitch(a.State.Position, b.State.Position + Chest, out float yaw, out float pitch);
        _seq.TryGetValue(1, out uint seq);
        _seq[1] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.EnqueueInput(1, packet);
        _match.Tick();

        Assert.False(b.Alive);
        var drops = SpawnedTo(1);
        Assert.Equal(5, drops.Count);   // Auto, Semi, Medium, Heavy, Medkit
        Assert.Equal(drops, SpawnedTo(2));
        Assert.Contains(drops, d => d.Kind == ItemKind.Weapon && d.DefId == TestWeapons.AutoId && d.Amount == 5);
        Assert.Contains(drops, d => d.Kind == ItemKind.Weapon && d.DefId == TestWeapons.SemiId && d.Amount == TestWeapons.SemiMagazine);
        Assert.Contains(drops, d => d.Kind == ItemKind.Ammo && d.DefId == (byte)AmmoType.Medium && d.Amount == TestGameData.LoadoutMediumAmmo);
        Assert.Contains(drops, d => d.Kind == ItemKind.Ammo && d.DefId == (byte)AmmoType.Heavy && d.Amount == TestGameData.LoadoutHeavyAmmo);
        Assert.Contains(drops, d => d.Kind == ItemKind.Consumable && d.DefId == (byte)ConsumableType.Medkit && d.Amount == 2);
        foreach (var d in drops)
        {
            Vector3 fromBody = d.Position - b.State.Position;
            Assert.Equal(1f, MathF.Sqrt(fromBody.X * fromBody.X + fromBody.Z * fromBody.Z), 3);
            Assert.Equal(0f, d.Position.Y);
        }
        Assert.Equal(5, drops.Select(d => d.Position).Distinct().Count());

        var toB = _sent.Where(s => s.PeerId == 2 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(toB.IndexOf(PacketId.PlayerDied) < toB.IndexOf(PacketId.ItemSpawned));
        Assert.All(b.Inventory.Slots, held => Assert.True(held.IsEmpty));
        var empty = LastInventoryTo(2);
        Assert.True(empty.Slot0.IsEmpty && empty.Slot1.IsEmpty);
        Assert.Equal(0, empty.MediumAmmo);
        Assert.Equal(0, empty.Medkits);
    }

    // Review Focus: G, swap-drop, death drop and pickups move items around but never make or lose any.
    [Fact]
    public void Items_AreConserved_ThroughDropSwapDeathAndPickup()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 1), new LoadoutWeapon(TestWeapons.LightId, 2) },
            LightAmmo = 50, MediumAmmo = 40, HeavyAmmo = 20, Medkits = 1, ShieldCells = 2,
        });
        var a = Join(1, Vector3.Zero, yaw: 90f);
        var b = Join(2, new Vector3(0f, 0f, 6f));
        Put(ItemKind.Weapon, TestWeapons.AutoId, 3, new Vector3(0f, 0f, 1f), rarity: 4);
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 60, new Vector3(0f, 0f, -1f));

        // 기능: 두 플레이어의 인벤토리와 월드에 있는 무기 수·탄약(탄창 포함)·소모품을 종류별로 합산한다.
        // 입력: 없음.
        // 출력: (무기 수, Light, Medium, Heavy, Medkit, Shield Cell) 합계.
        (int weapons, int light, int medium, int heavy, int medkits, int cells) Count()
        {
            int w = 0, l = 0, m = 0, h = 0, mk = 0, c = 0;
            // 기능: 무기 하나를 세고 그 탄창을 무기 종류에 맞는 탄약 합계에 더한다.
            // 입력: id - 무기 ID, mag - 탄창 탄약 수.
            // 출력: 반환값 없음. 바깥 합계 w와 l·m·h 중 하나가 늘어난다.
            void Weapon(byte id, int mag)
            {
                w++;
                if (id == TestWeapons.LightId) l += mag; else if (id == TestWeapons.AutoId) m += mag; else h += mag;
            }
            foreach (var p in new[] { a, b })
            {
                foreach (HeldWeapon held in p.Inventory.Slots) if (!held.IsEmpty) Weapon(held.Weapon!.Id, held.MagAmmo);
                l += p.Inventory.GetAmmo(AmmoType.Light);
                m += p.Inventory.GetAmmo(AmmoType.Medium);
                h += p.Inventory.GetAmmo(AmmoType.Heavy);
                mk += p.Inventory.Medkits;
                c += p.Inventory.ShieldCells;
            }
            foreach (var d in WorldList())
            {
                if (d.Kind == ItemKind.Weapon) Weapon(d.DefId, d.Amount);
                else if (d.Kind == ItemKind.Ammo)
                {
                    if (d.DefId == (byte)AmmoType.Light) l += d.Amount; else if (d.DefId == (byte)AmmoType.Medium) m += d.Amount; else h += d.Amount;
                }
                else if (d.DefId == (byte)ConsumableType.Medkit) mk += d.Amount;
                else c += d.Amount;
            }
            return (w, l, m, h, mk, c);
        }

        var start = Count();
        Press(a, InputButtons.Interact);                       // full: swaps the Auto in hand for the ground Auto
        Assert.Equal(start, Count());
        Press(a, InputButtons.Drop);                           // G
        Assert.Equal(start, Count());
        Press(a, InputButtons.Interact);                       // takes the nearest thing back
        Assert.Equal(start, Count());
        Press(a, InputButtons.None);   // final review A5: released between presses
        Press(a, InputButtons.Interact);
        Assert.Equal(start, Count());

        // Death: everything a carried goes to the ground; b walks over and picks up what it can.
        a.Health = 1;
        a.Shield = 0;
        b.Inventory.Slots[0].MagAmmo = 6;
        TestAim.YawPitch(b.State.Position, a.State.Position + Chest, out float yaw, out float pitch);
        _seq.TryGetValue(2, out uint seq);
        _seq[2] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.EnqueueInput(2, packet);
        _match.Tick();
        Assert.False(a.Alive);
        var afterShot = Count();
        Assert.Equal(start.medium - 1, afterShot.medium);      // b's Auto fired one Medium round, nothing else changed
        Assert.Equal((start.weapons, start.light, start.heavy, start.medkits, start.cells),
                     (afterShot.weapons, afterShot.light, afterShot.heavy, afterShot.medkits, afterShot.cells));

        b.State.Position = a.State.Position;
        for (int i = 0; i < 12; i++)
        {
            Press(b, InputButtons.Interact);
            Press(b, InputButtons.None);   // final review A5: released between presses
        }
        Assert.Equal(afterShot, Count());
    }

    // Review Focus / D13: many deaths never push the world past 256 items, and the loot at the spawn points
    // is never evicted to make room for drops.
    [Fact]
    public void RepeatedDeaths_KeepTheWorldAtTheCap_AndSpawnPointItemsSurvive()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0), new LoadoutWeapon(TestWeapons.LightId, 0) },
            LightAmmo = TestGameData.LightMax, MediumAmmo = TestGameData.MediumMax, HeavyAmmo = TestGameData.HeavyMax,
            Medkits = TestGameData.MedkitMaxStack, ShieldCells = TestGameData.ShieldCellMaxStack,
        }, LootPoints.All.ToArray());
        var spawnItems = Enumerable.Range(0, _match.WorldItems.Count).Select(i => _match.WorldItems[i].Data.ItemId).ToList();
        Assert.Equal(LootPoints.All.Length, spawnItems.Count);
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var b = Join(2, new Vector3(0f, 0f, 4f));

        for (int death = 0; death < 40; death++)   // 8 items per death: far past the cap
        {
            b.Health = 1;
            b.Shield = 0;
            a.Inventory.Slots[0].MagAmmo = TestWeapons.AutoMagazine;
            a.Inventory.Slots[0].NextFireTick = 0;
            TestAim.YawPitch(a.State.Position, b.State.Position + Chest, out float yaw, out float pitch);
            _seq.TryGetValue(1, out uint seq);
            _seq[1] = ++seq;
            var packet = new PlayerInputPacket { Count = 1 };
            packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.EnqueueInput(1, packet);
            _match.Tick();
            Assert.False(b.Alive, $"death {death}");
            Assert.True(_match.WorldItems.Count <= WorldItems.Capacity);
            for (int i = 0; i < 300 && !b.Alive; i++) _match.Tick();
            Assert.True(b.Alive, $"respawn after death {death}");
        }

        Assert.Equal(WorldItems.Capacity, _match.WorldItems.Count);
        foreach (ushort id in spawnItems) Assert.True(_match.WorldItems.IndexOf(id) >= 0, $"spawn item {id} was evicted");
    }

    // Server hot path: a tick with a pickup (and its events) allocates nothing.
    [Fact]
    public void PickupTick_AllocatesNothing()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            TestGameData.CombatLoadout, Array.Empty<LootPoint>());
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out var a);
        var packet = new PlayerInputPacket { Count = 1 };
        long allocated = 0;
        for (uint i = 1; i <= 4; i++)   // 1-3 warm up; 4 is measured
        {
            packet.Set(0, new InputCommand { Seq = 2 * i - 1 });   // final review A5: released between presses
            _match.EnqueueInput(1, packet);
            _match.Tick();
            PastItemInterval();                                     // review fix C6
            _match.SpawnItem(new LootRoll(ItemKind.Ammo, (byte)AmmoType.Light, 0, 1), a.State.Position, -1);
            packet.Set(0, new InputCommand { Seq = 2 * i, Buttons = InputButtons.Interact });
            _match.EnqueueInput(1, packet);
            long start = GC.GetAllocatedBytesForCurrentThread();
            _match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal((int)i, a.Inventory.GetAmmo(AmmoType.Light));
        }
        Assert.Equal(0, allocated);
    }

    // Server hot path: a weapon-swap tick and a G-drop tick allocate nothing either.
    [Fact]
    public void SwapAndDropTicks_AllocateNothing()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            new StartingLoadout
            {
                Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0), new LoadoutWeapon(TestWeapons.LightId, 0) },
            }, Array.Empty<LootPoint>());
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out var a);
        var packet = new PlayerInputPacket { Count = 1 };
        long swapAllocated = 0, dropAllocated = 0;
        uint seq = 0;
        for (int round = 1; round <= 4; round++)   // 1-3 warm up; 4 is measured
        {
            _match.SpawnItem(new LootRoll(ItemKind.Weapon, TestWeapons.AutoId, 0, 5), a.State.Position, -1);   // nearest, so the pickup picks it
            packet.Set(0, new InputCommand { Seq = ++seq, Buttons = InputButtons.Interact });
            _match.EnqueueInput(1, packet);
            long start = GC.GetAllocatedBytesForCurrentThread();
            _match.Tick();
            swapAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(5, a.Inventory.Current.MagAmmo);
            PastItemInterval();   // review fix C6

            packet.Set(0, new InputCommand { Seq = ++seq, Buttons = InputButtons.Drop });
            _match.EnqueueInput(1, packet);
            start = GC.GetAllocatedBytesForCurrentThread();
            _match.Tick();
            dropAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(a.Inventory.Current.IsEmpty);
            // Put the slot back so the next round swaps again (outside the measured ticks).
            a.Inventory.Slots[a.Inventory.CurrentSlot] = new HeldWeapon { Weapon = a.Inventory.Slots[(a.Inventory.CurrentSlot + 1) % 3].Weapon, MagAmmo = 1 };
            PastItemInterval();
        }
        Assert.Equal(0, swapAllocated);
        Assert.Equal(0, dropAllocated);
    }

    // 기능: 사수가 대상의 가슴을 겨냥해 발사하는 입력(ViewTick = 현재 Tick)을 넣고 한 Tick 돌린다.
    // 입력: shooter - 사수, target - 대상.
    // 출력: 반환값 없음. 발사 입력이 처리된 뒤 Match가 한 Tick 진행된다.
    private void ShootAt(PlayerEntity shooter, PlayerEntity target)
    {
        TestAim.YawPitch(shooter.State.Position, target.State.Position + Chest, out float yaw, out float pitch);
        _seq.TryGetValue(shooter.PeerId, out uint seq);
        _seq[shooter.PeerId] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.EnqueueInput(shooter.PeerId, packet);
        _match.Tick();
    }

    // 기능: 월드 아이템 수가 count가 될 때까지 멀리(20, 0, 20)에 스폰 지점 아이템을 넣는다. 생성에 실패하면 테스트를 실패시킨다.
    // 입력: count - 채울 아이템 수.
    // 출력: 반환값 없음. 월드에 퇴거되지 않는 아이템이 count개 있게 된다.
    // SpawnItem returns 0 only when every record is a spawn-point item, which production cannot reach (at
    // most Capacity / 2 loot points). The tests make that state by hand with spawn-point items far away.
    private void FillWorldWithSpawnPointItems(int count = WorldItems.Capacity)
    {
        while (_match.WorldItems.Count < count)
            Assert.NotEqual(0, PutAtSpawnPoint(ItemKind.Ammo, (byte)AmmoType.Light, 1, new Vector3(20f, 0f, 20f)));
    }

    // 기능: 스폰 지점 0 소속(퇴거되지 않는) 아이템 하나를 월드에 넣는다.
    // 입력: kind - 아이템 종류, defId - 정의 ID, amount - 수량, position - 위치, rarity - 희귀도.
    // 출력: 생성된 아이템 ID, 자리가 없으면 0.
    // Spawn point 0: never evicted (D13).
    private ushort PutAtSpawnPoint(ItemKind kind, byte defId, ushort amount, Vector3 position, byte rarity = 0) =>
        _match.SpawnItem(new LootRoll(kind, defId, rarity, amount), position, 0);

    // Conservation: an item leaves the inventory only once it is in the world. G into a world that cannot
    // take the weapon keeps it in hand, and nothing is sent.
    [Fact]
    public void Drop_WhenTheWorldCannotTakeIt_KeepsTheWeaponInHand()
    {
        var a = Join(1, Vector3.Zero);
        FillWorldWithSpawnPointItems();
        a.Inventory.Slots[0].MagAmmo = 4;
        _sent.Clear();

        Press(a, InputButtons.Drop);

        Assert.Equal(TestWeapons.AutoId, a.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(4, a.Inventory.Slots[0].MagAmmo);
        Assert.Equal(WorldItems.Capacity, _match.WorldItems.Count);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.ItemRemoved || s.Id == PacketId.InventoryState);
    }

    // Conservation: the death drop empties only what it placed. What the world refused stays with the body.
    [Fact]
    public void DeathDrop_WhenTheWorldCannotTakeTheItems_KeepsThemInTheInventory()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        FillWorldWithSpawnPointItems();
        b.Health = 1;
        b.Shield = 0;
        _sent.Clear();

        ShootAt(a, b);

        Assert.False(b.Alive);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.ItemRemoved);
        Assert.Equal(TestWeapons.AutoId, b.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(TestWeapons.SemiId, b.Inventory.Slots[1].Weapon!.Id);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, b.Inventory.GetAmmo(AmmoType.Medium));
        Assert.Equal(TestGameData.LoadoutHeavyAmmo, b.Inventory.GetAmmo(AmmoType.Heavy));
    }

    // The swap takes the ground weapon out first, so its record is free for the old weapon even when every
    // other record is a spawn-point item.
    [Fact]
    public void Swap_WithAFullWorld_PutsTheOldWeaponInTheFreedRecord()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0), new LoadoutWeapon(TestWeapons.LightId, 0) },
        }, new[] { new LootPoint(new Vector3(20f, 0f, 20f), LootPoints.FloorTable) });   // spawn point 0 must exist: taking its item arms the refill timer
        var a = Join(1, Vector3.Zero);
        FillWorldWithSpawnPointItems(WorldItems.Capacity - 1);
        ushort id = PutAtSpawnPoint(ItemKind.Weapon, TestWeapons.AutoId, 3, new Vector3(0f, 0f, 1f), rarity: 4);

        Press(a, InputButtons.Interact);

        Assert.Equal(-1, IndexOf(id));
        Assert.Equal(4, a.Inventory.Slots[0].Rarity);
        Assert.Equal(3, a.Inventory.Slots[0].MagAmmo);
        Assert.Equal(WorldItems.Capacity, _match.WorldItems.Count);
        var old = Assert.Single(WorldList(), d => d.Kind == ItemKind.Weapon && _match.WorldItems[_match.WorldItems.IndexOf(d.ItemId)].IsDropped);
        Assert.Equal(TestWeapons.AutoId, old.DefId);
        Assert.Equal(0, old.Rarity);
        Assert.Equal(TestWeapons.AutoMagazine, old.Amount);
        Assert.True(_match.WorldItems[_match.WorldItems.IndexOf(old.ItemId)].IsDropped);
    }

    // A reload never outlives the reserve it would take from: the death drop takes the reserve, and death
    // cancels the reload. Past the reload's end and through the respawn wait, nothing is made or lost.
    [Fact]
    public void Death_WhileReloading_DropsTheReserve_AndTheReloadMovesNothing()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 1;
        Press(b, InputButtons.Reload);
        Assert.True(b.Reloading);
        b.Health = 1;
        b.Shield = 0;

        ShootAt(a, b);

        Assert.False(b.Alive);
        Assert.False(b.Reloading);
        var world = WorldList();
        Assert.Contains(world, d => d.Kind == ItemKind.Weapon && d.DefId == TestWeapons.AutoId && d.Amount == 1);
        Assert.Contains(world, d => d.Kind == ItemKind.Ammo && d.DefId == (byte)AmmoType.Medium && d.Amount == TestGameData.LoadoutMediumAmmo);
        for (int i = 0; i < TestWeapons.AutoReload + 2; i++) _match.Tick();
        Assert.False(b.Alive);
        Assert.Equal(world, WorldList());
        Assert.Equal(0, b.Inventory.GetAmmo(AmmoType.Medium));
    }
}
