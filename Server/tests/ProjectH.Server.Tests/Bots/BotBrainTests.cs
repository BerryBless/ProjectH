using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using static ProjectH.Server.Tests.Bots.BotTestView;

namespace ProjectH.Server.Tests.Bots;

// Phase 7 D4: one test per decision rule, on a hand-made BotView in the real map's plaza.
public class BotBrainTests
{
    [Fact]
    public void NotJoined_OrNoSnapshot_SendsNothing_AndDead_SendsAnEmptyInput()
    {
        var brain = new BotBrain(1);
        BotView view = Create();
        view.Joined = false;
        Assert.False(brain.Tick(view, 0f, out _));
        view = Create();
        view.HasSnapshot = false;
        Assert.False(brain.Tick(view, 0f, out _));
        // Phase 10 D4: dead (or spectating) the bot still sends, as the client does, so it is not closed by the input
        // timeout; the input moves and fires nothing.
        view = Create();
        view.Alive = false;
        Assert.True(brain.Tick(view, 0f, out InputCommand dead));
        Assert.Equal(0f, dead.MoveX);
        Assert.Equal(0f, dead.MoveY);
        Assert.Equal(InputButtons.None, dead.Buttons);
        Assert.Equal(BotGoal.None, brain.Goal);
    }

    [Theory]
    [InlineData(MatchFlowState.WaitingForPlayers)]
    [InlineData(MatchFlowState.Starting)]
    [InlineData(MatchFlowState.Finished)]
    public void OutsideAMatch_StandsStill_AndNeverFires(MatchFlowState state)
    {
        BotView view = Armed();
        view.HasMatchState = true;
        view.Match.State = state;
        AddOther(view, 2, new Vector3(0f, 0f, 6f));
        var brain = new BotBrain(1);
        foreach (InputCommand c in Run(brain, view, 0f, 30))
        {
            Assert.Equal(0f, c.MoveX);
            Assert.Equal(0f, c.MoveY);
            Assert.Equal(InputButtons.None, c.Buttons);
        }
        Assert.Equal(BotGoal.Idle, brain.Goal);
    }

    [Fact]
    public void TheDevSandbox_WithoutMatchState_CountsAsInAMatch()
    {
        BotView view = Armed();
        AddOther(view, 2, new Vector3(0f, 0f, 6f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 3);
        Assert.Equal(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void OutsideTheNextCircle_WalksToItsCentre_AheadOfAFight()
    {
        BotView view = Armed();
        view.Zone = new ZoneState { Phase = 1, ToX = 40f, ToZ = 0f, ToRadius = 10f, FromRadius = 115f };
        AddOther(view, 2, new Vector3(0f, 0f, 6f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.Equal(BotGoal.Zone, brain.Goal);
        Assert.True(AngleBetween(c.Yaw, 90f) < 1f, $"yaw {c.Yaw}");
        Assert.Equal(1f, c.MoveY);
        Assert.True((c.Buttons & InputButtons.Sprint) != 0);
        Assert.Equal(0, (int)(c.Buttons & InputButtons.Fire));
    }

    [Fact]
    public void InsideTheNextCircle_TheZoneIsNoGoal()
    {
        BotView view = Create(new Vector3(38f, 0f, 2f));
        view.Zone = new ZoneState { Phase = 1, ToX = 40f, ToZ = 0f, ToRadius = 10f, FromRadius = 115f };
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        Assert.NotEqual(BotGoal.Zone, brain.Goal);
    }

    [Fact]
    public void AVisibleEnemyInRange_IsAimedAtAndShot()
    {
        BotView view = Armed();
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.Equal(BotGoal.Fight, brain.Goal);
        Assert.Equal((ushort)2, brain.Target);
        Assert.True((c.Buttons & InputButtons.Fire) != 0);
        BotAim.Solve(BotAim.Eye(Vector3.Zero), BotAim.Chest(new Vector3(0f, 0f, 10f)), out float yaw, out float pitch);
        float max = BotAim.MaxError(10f);
        Assert.True(AngleBetween(c.AimYaw, yaw) <= max + 1e-3f, $"aim yaw {c.AimYaw} vs {yaw}");
        Assert.True(MathF.Abs(c.AimPitch - pitch) <= max + 1e-3f, $"aim pitch {c.AimPitch} vs {pitch}");
        Assert.Equal(view.ServerTick, (uint)c.ViewTick);
    }

    [Fact]
    public void AnEnemyBehindAWall_IsNotTargeted()
    {
        // Field wall (26, 1.5, 0): x 25.75..26.25, z -2.5..2.5, 3 m high, between the two.
        BotView view = Armed(new Vector3(24f, 0f, 0f));
        AddOther(view, 2, new Vector3(28f, 0f, 0f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void AnEnemyBehindAHill_IsNotTargeted()
    {
        // North hill (4 m at (0, 46)) between them, 55 m apart (inside the 60 m engage range).
        BotView view = Armed(new Vector3(-30f, 0f, 46f));
        AddOther(view, 2, new Vector3(25f, 0f, 46f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void AnEnemyBeyondTheEngageRange_IsNotTargeted()
    {
        BotView view = Armed();
        AddOther(view, 2, new Vector3(0f, 0f, -(BotBrain.EngageRange + 1f)));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void AnEmptyMagazine_WithReserve_Reloads_InsteadOfFiring()
    {
        BotView view = Armed();
        view.Self.Ammo = 0;
        view.Inventory.MediumAmmo = 30;
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.True((c.Buttons & InputButtons.Reload) != 0);
        Assert.Equal(0, (int)(c.Buttons & InputButtons.Fire));
    }

    [Fact]
    public void NoRoundsInTheCurrentWeapon_SwitchesToALoadedSlot()
    {
        BotView view = Armed();
        view.Self.Ammo = 0;   // slot 0 empty, no Medium reserve
        view.Inventory.Slot1 = new InventorySlotState { WeaponId = SemiId, MagAmmo = 5 };
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.Equal(BotGoal.Fight, brain.Goal);
        Assert.True((c.Buttons & InputButtons.Slot2) != 0);
        Assert.Equal(0, (int)(c.Buttons & InputButtons.Fire));
    }

    [Fact]
    public void NoRoundsAnywhere_DoesNotFight()
    {
        BotView view = Armed();
        view.Self.Ammo = 0;
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void ASemiAutomatic_IsPressedEveryOtherTick()
    {
        BotView view = Create();
        view.Inventory.Slot0 = new InventorySlotState { WeaponId = SemiId, MagAmmo = 5 };
        view.Self.Ammo = 5;
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        InputCommand[] commands = Run(brain, view, 0f, 8);
        int presses = commands.Count(c => (c.Buttons & InputButtons.Fire) != 0);
        Assert.Equal(4, presses);
        for (int i = 1; i < commands.Length; i++)
            Assert.False((commands[i].Buttons & commands[i - 1].Buttons & InputButtons.Fire) != 0, $"fire held on ticks {i - 1} and {i}");
    }

    [Fact]
    public void FiringWithoutAHit_IgnoresTheTarget_ForAWhile()
    {
        BotView view = Armed();
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, BotBrain.FireTicksWithoutHit + 1);
        Run(brain, view, (BotBrain.FireTicksWithoutHit + 1) / 30f, 4);
        Assert.NotEqual(BotGoal.Fight, brain.Goal);
        Run(brain, view, 2f + BotBrain.IgnoreTargetSeconds, 2);
        Assert.Equal(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void AHit_KeepsTheTarget()
    {
        BotView view = Armed();
        AddOther(view, 2, new Vector3(0f, 0f, 10f));
        var brain = new BotBrain(1);
        for (int i = 0; i < BotBrain.FireTicksWithoutHit * 2; i++)
        {
            if (i % 20 == 0) view.HitsLanded++;
            brain.Tick(view, i / 30f, out _);
        }
        Assert.Equal(BotGoal.Fight, brain.Goal);
    }

    [Fact]
    public void LowHealth_WithAMedkit_AndNoEnemyClose_Heals()
    {
        BotView view = Create();
        view.Self.Health = 40;
        view.Inventory.Medkits = 1;
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.Equal(BotGoal.Heal, brain.Goal);
        Assert.True((c.Buttons & InputButtons.UseMedkit) != 0);
    }

    [Fact]
    public void LowShield_WithACell_Heals_WithAShieldCell()
    {
        BotView view = Create();
        view.Self.Shield = 0;
        view.Inventory.ShieldCells = 2;
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.True((c.Buttons & InputButtons.UseShieldCell) != 0);
    }

    [Fact]
    public void AnEnemyClose_PostponesHealing()
    {
        BotView view = Create();   // unarmed, so it cannot fight either
        view.Self.Health = 40;
        view.Inventory.Medkits = 1;
        AddOther(view, 2, new Vector3(0f, 0f, BotBrain.HealSafeDistance - 1f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.NotEqual(BotGoal.Heal, brain.Goal);
        Assert.Equal(0, (int)(c.Buttons & InputButtons.UseMedkit));
    }

    [Fact]
    public void AUsefulItem_IsWalkedTo_ThenPickedUp()
    {
        BotView view = Create();
        AddItem(view, 7, ItemKind.Weapon, AutoId, new Vector3(6f, 0f, 0f));
        var brain = new BotBrain(1);
        InputCommand c = Run(brain, view, 0f, 1)[0];
        Assert.Equal(BotGoal.Loot, brain.Goal);
        Assert.Equal((ushort)7, brain.GoalItem);
        Assert.True(AngleBetween(c.Yaw, 90f) < 1f);
        Assert.Equal(1f, c.MoveY);

        view.MyPosition = new Vector3(5.2f, 0f, 0f);   // within reach
        c = Run(brain, view, 1f, 1)[0];
        Assert.True((c.Buttons & InputButtons.Interact) != 0);
        Assert.Equal(0f, c.MoveY);
    }

    [Fact]
    public void AnItemTheServerKeepsRefusing_IsBlacklisted()
    {
        BotView view = Create(new Vector3(5.2f, 0f, 0f));
        AddItem(view, 7, ItemKind.Weapon, AutoId, new Vector3(6f, 0f, 0f));
        var brain = new BotBrain(1);
        int presses = 0;
        for (int i = 0; i < 90; i++)
        {
            brain.Tick(view, i / 30f, out InputCommand c);
            if ((c.Buttons & InputButtons.Interact) != 0) presses++;
        }
        Assert.Equal(BotBrain.MaxInteractTries, presses);
        Assert.NotEqual(BotGoal.Loot, brain.Goal);
    }

    [Fact]
    public void ATakenItem_EndsTheLootGoal()
    {
        BotView view = Create();
        AddItem(view, 7, ItemKind.Weapon, AutoId, new Vector3(6f, 0f, 0f));
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 1);
        view.Items.Remove(7);
        Run(brain, view, 0.01f, 1);
        Assert.NotEqual(BotGoal.Loot, brain.Goal);
    }

    [Fact]
    public void IsUseful_FollowsTheInventory()
    {
        BotView view = Armed();   // slot 0 = automatic (Medium)
        Assert.True(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Weapon, DefId = SemiId }));          // two slots free
        Assert.True(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Ammo, DefId = (byte)AmmoType.Medium }));
        Assert.False(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Ammo, DefId = (byte)AmmoType.Heavy }));  // no Heavy weapon
        view.Inventory.MediumAmmo = 150;
        Assert.False(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Ammo, DefId = (byte)AmmoType.Medium })); // full
        view.Inventory.Medkits = 3;
        Assert.False(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Consumable, DefId = (byte)ConsumableType.Medkit }));
        Assert.True(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Consumable, DefId = (byte)ConsumableType.ShieldCell }));
        view.Inventory.Slot1 = new InventorySlotState { WeaponId = SemiId };
        view.Inventory.Slot2 = new InventorySlotState { WeaponId = SemiId };
        Assert.False(BotBrain.IsUseful(view, new WorldItemData { Kind = ItemKind.Weapon, DefId = AutoId }));            // no slot free
    }

    [Fact]
    public void WithNothingToDo_WandersInsideTheNextCircle()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            BotView view = Create();
            view.Zone = new ZoneState { Phase = 1, ToX = 0f, ToZ = 0f, ToRadius = 50f, FromRadius = 115f };
            var brain = new BotBrain(seed);
            Run(brain, view, 0f, 1);
            Assert.Equal(BotGoal.Wander, brain.Goal);
            Assert.True(BotAim.HorizontalDistance(brain.GoalPoint, Vector3.Zero) <= 50f * 0.8f + 1e-3f, $"seed {seed}: {brain.GoalPoint}");
            Assert.Equal(GameMap.Terrain.Height(brain.GoalPoint.X, brain.GoalPoint.Z), brain.GoalPoint.Y);
        }
    }

    [Fact]
    public void ATick_AllocatesNothing()
    {
        BotView view = Armed();
        for (ushort id = 2; id < 12; id++) AddOther(view, id, new Vector3(id * 3f, 0f, 30f));
        for (ushort id = 1; id <= 50; id++) AddItem(view, id, ItemKind.Ammo, (byte)AmmoType.Medium, new Vector3(-id, 0f, -20f));
        view.Zone = new ZoneState { Phase = 1, ToX = 0f, ToZ = 0f, ToRadius = 70f, FromRadius = 115f };
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 60);
        Assert.Equal(BotGoal.Fight, brain.Goal);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) brain.Tick(view, 2f + i / 30f, out _);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void ALootOrWanderTick_AllocatesNothing()
    {
        BotView view = Create();   // unarmed: every weapon is useful, so far items keep the Loot goal active
        for (ushort id = 1; id <= 50; id++) AddItem(view, id, ItemKind.Weapon, AutoId, new Vector3(-id, 0f, -20f));
        view.Zone = new ZoneState { Phase = 1, ToX = 0f, ToZ = 0f, ToRadius = 70f, FromRadius = 115f };
        var brain = new BotBrain(1);
        Run(brain, view, 0f, 60);
        Assert.Equal(BotGoal.Loot, brain.Goal);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) brain.Tick(view, 2f + i / 30f, out _);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());

        for (ushort id = 1; id <= 50; id++) view.Items.Remove(id);
        Run(brain, view, 20f, 30);
        Assert.Equal(BotGoal.Wander, brain.Goal);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) brain.Tick(view, 22f + i / 30f, out _);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    // Phase 8 D8: the packets of one tick add up; the first packet of a new tick starts the list over.
    [Fact]
    public void ASnapshotInTwoParts_AddsUp_AndANewTickStartsOver()
    {
        BotView view = Create();
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 10, Part = 0, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 2, Flags = SnapshotEntity.AliveFlag });
        view.ApplyEntity(new SnapshotEntity { EntityId = 1, Position = new Vector3(3f, 0f, 4f), Flags = SnapshotEntity.AliveFlag });   // ourselves
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 10, Part = 1, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 3, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(2, view.OtherCount);
        Assert.Equal(new Vector3(3f, 0f, 4f), view.MyPosition);

        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 12, Part = 0, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 4, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(1, view.OtherCount);
        Assert.Equal((ushort)4, view.Others[0].EntityId);

        // A lost first part: the second part of a new tick still starts that tick's list.
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 14, Part = 1, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 5, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(1, view.OtherCount);
        Assert.Equal((ushort)5, view.Others[0].EntityId);
    }
}
