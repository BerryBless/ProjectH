using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 18 D4, D6, D7, D8: what the server puts on the wire for gameplay audio: the weapon id in ShotFired (one per trigger
// pull), the reason of a Destroyed record (damage 0, collapse 1), the shield flags of DamageTaken on every damage path, and
// WorldSound for a harvest swing to the other players within 30 m only. Dev sandbox (damage always allowed).
public class AudioPhase18Tests
{
    private const byte Ar = WeaponsPhase17Tests.Ar, Sniper = WeaponsPhase17Tests.Sniper, Shotgun = WeaponsPhase17Tests.Shotgun,
        Rocket = WeaponsPhase17Tests.Rocket;
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // 기능: 운영 무기 데이터와 주어진 장비로 개발 모드 경기를 만든다.
    // 입력: a·b·c - 칸 0·1·2의 무기 id.
    // 출력: SandboxHarness.
    private static SandboxHarness Armed(byte a, byte b, byte c) => new(WeaponsPhase17Tests.Loadout(a, b, c), data: WeaponsPhase17Tests.Data());

    // 기능: 한 peer가 받은 ShotFired를 모두 읽는다.
    // 입력: h - 경기, peer - 받는 연결.
    // 출력: ShotFired 목록(보낸 순서).
    private static List<ShotFired> Shots(SandboxHarness h, int peer) => h.To(peer, PacketId.ShotFired).Select(s =>
    {
        PacketReader r = SandboxHarness.Body(s);
        Assert.True(ShotFired.TryRead(ref r, out ShotFired shot));
        return shot;
    }).ToList();

    // 기능: 한 peer가 받은 마지막 DamageTaken을 읽는다.
    // 입력: h - 경기, peer - 받는 연결.
    // 출력: DamageTaken.
    private static DamageTaken LastDamage(SandboxHarness h, int peer)
    {
        PacketReader r = SandboxHarness.Body(h.To(peer, PacketId.DamageTaken).Last());
        Assert.True(DamageTaken.TryRead(ref r, out DamageTaken d));
        return d;
    }

    // ---- ShotFired weapon id (D4) ----

    [Theory]
    [InlineData(Shotgun)]
    [InlineData(Ar)]
    [InlineData(Sniper)]
    public void ShotFired_CarriesTheWeaponId_OnePerTriggerPull(byte weapon)
    {
        SandboxHarness h = Armed(weapon, Rocket, Rocket);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -20f));
        PlayerEntity other = h.Join(2, new Vector3(30f, 0f, -20f));
        h.Clear();
        h.Act(shooter, InputButtons.Fire, new Vector3(2.5f, 1.6f, 10f));
        foreach (int peer in new[] { shooter.PeerId, other.PeerId })
        {
            ShotFired shot = Assert.Single(Shots(h, peer));   // a shotgun's eight pellets share one
            Assert.Equal(weapon, shot.WeaponId);
            Assert.Equal(shooter.EntityId, shot.ShooterId);
        }
    }

    // ---- Destroyed reason (D6) ----

    private const int C = 16;

    // 기능: peer 1이 받은 BuildEvents의 Destroyed 기록을 이유와 함께 모은다.
    // 입력: h - 경기.
    // 출력: 조각 id → 이유.
    private static Dictionary<uint, BuildDestroyReason> DestroyedReasons(SandboxHarness h)
    {
        var result = new Dictionary<uint, BuildDestroyReason>();
        foreach (SandboxHarness.Sent sent in h.To(1, PacketId.BuildEvents))
        {
            PacketReader r = SandboxHarness.Body(sent);
            Assert.True(BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int edited, out int health, out int destroyed));
            for (int i = 0; i < placed; i++) Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out _));
            for (int i = 0; i < edited; i++) Assert.True(BuildEventsPacket.TryReadEdited(ref r, out _, out _));
            for (int i = 0; i < health; i++) Assert.True(BuildEventsPacket.TryReadHealth(ref r, out _, out _));
            for (int i = 0; i < destroyed; i++)
            {
                Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint id, out BuildDestroyReason reason));
                result.Add(id, reason);
            }
            Assert.Equal(0, r.Remaining);
        }
        return result;
    }

    [Fact]
    public void ADestroyedFoundation_IsDestroyed_WhatItHeld_IsCollapsed()
    {
        var h = new SandboxHarness();
        h.Join(1, new Vector3(-6f, 0f, -6f));
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0));
        uint floor = h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 1, C, 0));
        uint ramp = h.AddPiece(new BuildPieceShape(BuildPieceType.Ramp, C, 1, C, 0));
        h.Clear();
        h.Match.DestroyPiece(wall);
        h.Match.Tick();
        Dictionary<uint, BuildDestroyReason> reasons = DestroyedReasons(h);
        Assert.Equal(3, reasons.Count);
        Assert.Equal(BuildDestroyReason.Destroyed, reasons[wall]);
        Assert.Equal(BuildDestroyReason.Collapsed, reasons[floor]);
        Assert.Equal(BuildDestroyReason.Collapsed, reasons[ramp]);
    }

    [Fact]
    public void QaDamageBuild_ToZero_IsDestroyed_AndItsCollapseIsCollapsed()
    {
        var h = new SandboxHarness();
        h.Join(1, new Vector3(-6f, 0f, -6f));
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0));
        uint floor = h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 1, C, 0));
        h.Clear();
        Assert.True(h.Match.DamagePieceById(wall, 100000f, out bool destroyed));
        Assert.True(destroyed);
        h.Match.Tick();
        Dictionary<uint, BuildDestroyReason> reasons = DestroyedReasons(h);
        Assert.Equal(BuildDestroyReason.Destroyed, reasons[wall]);
        Assert.Equal(BuildDestroyReason.Collapsed, reasons[floor]);
    }

    [Fact]
    public void AShotThatBreaksAWall_IsDestroyed()
    {
        SandboxHarness h = Armed(Sniper, Ar, Shotgun);
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0));   // wood wall on z 0, x 0..5
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -10f));
        h.Clear();
        for (int i = 0; i < 10 && h.Match.Build.Contains(wall); i++)
        {
            h.Act(shooter, InputButtons.Fire, new Vector3(2.5f, 1.5f, 0f));
            h.Act(shooter, InputButtons.None, new Vector3(2.5f, 1.5f, 0f));
            h.Ticks(60);
        }
        Assert.False(h.Match.Build.Contains(wall));
        Assert.Equal(BuildDestroyReason.Destroyed, DestroyedReasons(h)[wall]);
    }

    [Fact]
    public void AnEditThatDropsAPiece_IsCollapsed()
    {
        var h = new SandboxHarness();
        PlayerEntity p = h.Join(1, new Vector3(2.5f, 0f, 2.5f));
        h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0), owner: p.EntityId);
        h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 1), owner: p.EntityId);    // west wall: holds rotation 1
        uint ramp = h.AddPiece(new BuildPieceShape(BuildPieceType.Ramp, C, 1, C, 0), owner: p.EntityId);
        uint floor = h.AddPiece(new BuildPieceShape(BuildPieceType.Floor, C, 2, C + 1, 0), owner: p.EntityId);   // on the ramp's high edge
        h.Clear();
        Assert.Equal(BuildResultCode.Ok, h.Match.EditPieceById(ramp, BuildEdit.PackState(0, 1)));
        h.Match.Tick();
        Assert.False(h.Match.Build.Contains(floor));
        Assert.Equal(BuildDestroyReason.Collapsed, Assert.Single(DestroyedReasons(h)).Value);
    }

    // ---- DamageTaken shield flags (D8) ----

    // A 90-damage sniper shot: no shield = health only; 100 shield = hit (10 left); 50 shield = hit and broken.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, DamageTaken.ShieldHitFlag)]
    [InlineData(50, DamageTaken.ShieldHitFlag | DamageTaken.ShieldBrokenFlag)]
    public void AShot_FlagsTheShield(int shield, byte expected)
    {
        SandboxHarness h = Armed(Sniper, Ar, Shotgun);
        PlayerEntity shooter = h.Join(1, SandboxHarness.Ground(2.5f, -40f));
        PlayerEntity target = h.Join(2, SandboxHarness.Ground(2.5f, -5f));
        target.Shield = shield;
        h.Act(shooter, InputButtons.Fire, target.State.Position + Chest);
        DamageTaken d = LastDamage(h, 2);
        Assert.Equal(90, d.Damage);
        Assert.Equal(expected, d.Flags);
    }

    [Fact]
    public void AShotgunBlast_BreakingTheShield_SendsOneDamageTaken_HitAndBroken()
    {
        SandboxHarness h = Armed(Shotgun, Ar, Rocket);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -10f));
        PlayerEntity target = h.Join(2, new Vector3(2.5f, 0f, -7f));
        target.Shield = 30;
        h.Act(shooter, InputButtons.Fire, target.State.Position + Chest);
        DamageTaken d = Assert.Single(h.To(2, PacketId.DamageTaken).Select(s =>
        {
            PacketReader r = SandboxHarness.Body(s);
            Assert.True(DamageTaken.TryRead(ref r, out DamageTaken v));
            return v;
        }));
        Assert.Equal(0, target.Shield);
        Assert.True(d.ShieldHit && d.ShieldBroken);
    }

    [Fact]
    public void ARocket_BreakingTheShield_IsHitAndBroken()
    {
        SandboxHarness h = Armed(Ar, Shotgun, Rocket);
        PlayerEntity shooter = h.Join(1, new Vector3(2.5f, 0f, -12f));
        PlayerEntity target = h.Join(2, new Vector3(2.5f, 0f, -2f));
        target.Shield = 10;
        h.Act(shooter, InputButtons.Fire | InputButtons.Slot3, target.State.Position + Chest);
        for (int i = 0; i < 20 && h.Match.Explosions == 0; i++) h.Match.Tick();
        Assert.Equal(1, h.Match.Explosions);
        DamageTaken d = LastDamage(h, 2);
        Assert.Equal(DamageTaken.ShieldHitFlag | DamageTaken.ShieldBrokenFlag, d.Flags);
        Assert.Equal(0, target.Shield);
    }

    [Fact]
    public void QaDamagePlayer_FlagsLikeAShot()
    {
        var h = new SandboxHarness();
        PlayerEntity target = h.Join(1, new Vector3(2.5f, 0f, -5f));
        target.Shield = 30;
        Assert.True(h.Match.DamagePlayer(target, 10, out _));
        Assert.Equal(DamageTaken.ShieldHitFlag, LastDamage(h, 1).Flags);
        Assert.True(h.Match.DamagePlayer(target, 25, out _));
        Assert.Equal(DamageTaken.ShieldHitFlag | DamageTaken.ShieldBrokenFlag, LastDamage(h, 1).Flags);
        Assert.True(h.Match.DamagePlayer(target, 5, out _));
        Assert.Equal(0, LastDamage(h, 1).Flags);
    }

    // ---- WorldSound (D7) ----

    // Tree 0 stands at (-36, 64) (HarvestTests): the swinger stands south of it, so the swing hits the trunk's south face.
    private const int Tree = 0;
    private static readonly Vector3 SouthOfTree = SandboxHarness.Ground(-36f, 62f);
    private static readonly Vector3 TrunkFace = new(-36f, 1.6f, GameMap.Harvestables[Tree].Bounds.Min.Z);

    // 기능: 한 peer가 받은 WorldSound를 모두 읽고 Unreliable로 왔는지 확인한다.
    // 입력: h - 경기, peer - 받는 연결.
    // 출력: WorldSound 목록.
    private static List<WorldSound> Sounds(SandboxHarness h, int peer) => h.To(peer, PacketId.WorldSound).Select(s =>
    {
        Assert.Equal(DeliveryMethod.Unreliable, s.Method);
        PacketReader r = SandboxHarness.Body(s);
        Assert.True(WorldSound.TryRead(ref r, out WorldSound sound));
        return sound;
    }).ToList();

    [Fact]
    public void AHarvestSwing_IsHeardWithin30m_ByOthersOnly_UntilTheTreeFalls()
    {
        var h = new SandboxHarness();
        PlayerEntity swinger = h.Join(1, SouthOfTree);
        h.Press(swinger, InputButtons.ToolHarvest);
        Vector3 face = TrunkFace;
        Vector3 nearFeet = SandboxHarness.Ground(face.X, face.Z - 29f);    // 29 m from the hit point (level ground)
        Vector3 farFeet = SandboxHarness.Ground(face.X, face.Z - 31f);     // 31 m
        Vector3 sideFeet = SandboxHarness.Ground(face.X + 20f, face.Z);    // 20 m east
        PlayerEntity near = h.Join(2, nearFeet);
        PlayerEntity far = h.Join(3, farFeet);
        PlayerEntity side = h.Join(4, sideFeet);
        Assert.InRange(Vector3.Distance(nearFeet, face), 28.5f, 29.9f);
        Assert.True(Vector3.Distance(farFeet, face) > 30.5f);
        h.Clear();

        int swings = 0;
        while (h.Match.Harvest.Health(Tree) > 0 && swings < 20)
        {
            h.Ticks(h.Match.Building.HarvestCooldownTicks);
            h.Act(swinger, InputButtons.Fire, face);
            h.Act(swinger, InputButtons.None, face);
            swings++;
        }
        Assert.Equal(0, h.Match.Harvest.Health(Tree));
        Assert.True(Vector3.Distance(near.State.Position, face) < 30f && Vector3.Distance(far.State.Position, face) > 30f &&
                    Vector3.Distance(side.State.Position, face) < 30f);   // nobody drifted across the line
        int hits = h.To(1, PacketId.HarvestHit).Count();
        Assert.True(hits >= 2);

        Assert.Empty(h.To(1, PacketId.WorldSound));   // the swinger hears its own HarvestHit instead
        Assert.Empty(h.To(3, PacketId.WorldSound));   // beyond 30 m
        foreach (int peer in new[] { 2, 4 })
        {
            List<WorldSound> sounds = Sounds(h, peer);
            Assert.Equal(hits, sounds.Count);         // one per swing that hit the tree
            Assert.All(sounds, s => Assert.Equal(swinger.EntityId, s.SourceId));
            Assert.All(sounds, s => Assert.True(Vector3.Distance(s.Position, face) < 0.6f, s.Position.ToString()));
            Assert.All(sounds.Take(sounds.Count - 1), s => Assert.Equal(WorldSoundKind.HarvestHit, s.Kind));
            Assert.Equal(WorldSoundKind.HarvestDestroyed, sounds[^1].Kind);
        }
    }

    [Fact]
    public void ASwingThatMisses_OrHitsAPiece_SendsNoWorldSound()
    {
        var h = new SandboxHarness();
        PlayerEntity swinger = h.Join(1, new Vector3(2.5f, 0f, -1.5f));
        h.Press(swinger, InputButtons.ToolHarvest);
        h.Join(2, new Vector3(2.5f, 0f, -5f));
        uint wall = h.AddPiece(new BuildPieceShape(BuildPieceType.Wall, C, 0, C, 0));   // wall on z 0 in front of the swinger
        h.Clear();
        h.Act(swinger, InputButtons.Fire, new Vector3(2.5f, 1.5f, 0f));   // the wall
        h.Act(swinger, InputButtons.None, new Vector3(2.5f, 1.5f, 0f));
        h.Ticks(h.Match.Building.HarvestCooldownTicks);
        h.Act(swinger, InputButtons.Fire, new Vector3(2.5f, 1.5f, -10f));   // the air behind
        // The first swing went to the wall (it took damage, or broke: a fresh piece is still weak).
        Assert.True(!h.Match.Build.Contains(wall) || (h.Match.Build.TryGetSlot(wall, out int slot) && h.Match.Build.At(slot).Damage > 0));
        Assert.DoesNotContain(h.Packets, s => s.Id == PacketId.WorldSound);
        Assert.Equal(0, h.Match.HarvestHits);
    }
}
