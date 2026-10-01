using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D3, D10 (spec §2 낙하 피해): the rule, and falls in a running match: a safe height, a hurting one, a big one
// and a fatal one (a death without a killer, cause Fall), the shield untouched, a glide landing free, and no damage
// before the match.
public class FallDamageTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(13f, 0)]
    [InlineData(21.5f, 50)]
    [InlineData(30f, 100)]
    [InlineData(80f, 100)]
    [InlineData(float.NaN, 0)]
    [InlineData(-20f, 0)]
    public void TheRule_IsLinear_FromThirteenToThirtyMetresPerSecond(float speed, int damage)
    {
        Assert.Equal(damage, CombatRules.FallDamage(speed));
    }

    // The landing speed of a fall from this height onto the flat plaza (the same Step the match runs).
    private static float LandingSpeed(float height)
    {
        var s = new MoveState { Position = new Vector3(0f, height, -3f) };
        for (int i = 0; i < 300; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand(), 1f / 30f, GameMap.Boxes, GameMap.Terrain, out StepResult r);
            if (r.LandingSpeed > 0f) return r.LandingSpeed;
        }
        throw new InvalidOperationException("never landed");
    }

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b) InMatch()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        return (h, a, b);
    }

    private static void DropFrom(RoyaleHarness h, PlayerEntity p, float height)
    {
        h.Place(p, new Vector3(0f, height, -3f));
        h.TickUntil(() => !p.Alive || (p.State.VelocityY == 0f && MovementSimulation.IsGrounded(p.State, GameMap.Boxes, GameMap.Terrain)), 400);
    }

    [Theory]
    [InlineData(3.25f)]   // a roof: no damage (D3)
    [InlineData(4f)]
    public void ASafeHeight_DoesNothing(float height)
    {
        var (h, a, _) = InMatch();
        DropFrom(h, a, height);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
        Assert.Empty(h.SentTo(1, PacketId.DamageTaken));
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(25f)]
    public void AHigherFall_TakesHealthOnly_AndTellsTheVictim(float height)
    {
        var (h, a, _) = InMatch();
        int shield = a.Shield;
        DropFrom(h, a, height);
        int expected = CombatRules.FallDamage(LandingSpeed(height));
        Assert.True(expected > 0);
        Assert.Equal(CombatRules.MaxHealth - expected, a.Health);
        Assert.Equal(shield, a.Shield);
        DamageTaken damage = h.SentTo(1, PacketId.DamageTaken).Select(s =>
        {
            var r = RoyaleHarness.Reader(s);
            Assert.True(DamageTaken.TryRead(ref r, out var d));
            return d;
        }).Single();
        Assert.Equal(0, damage.AttackerId);
        Assert.Equal(expected, damage.Damage);
        Assert.Equal(Vector3.Zero, damage.FromDirection);
    }

    [Fact]
    public void AFatalFall_IsADeathWithoutKiller_CauseFall_WithAPlacement()
    {
        var (h, a, b) = InMatch();
        DropFrom(h, a, 50f);
        Assert.False(a.Alive);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(2, PacketId.PlayerDied).Single());
        Assert.Equal(a.EntityId, died.VictimId);
        Assert.Equal(0, died.KillerId);
        Assert.Equal(DeathCause.Fall, died.Cause);
        Assert.Equal(2, died.Placement);
        Assert.Equal(0, b.Kills);
    }

    [Fact]
    public void AGlideLanding_DoesNoDamage()
    {
        var (h, a, _) = InMatch();
        h.Place(a, new Vector3(0f, 20f, -3f));
        a.State.Mode = MovementMode.Glide;
        h.TickUntil(() => a.State.Mode == MovementMode.Ground, 200);
        h.Ticks(2);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void BeforeTheMatch_AFallDoesNoDamage()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        DropFrom(h, a, 25f);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void AShotKill_StillCarriesCauseZero()
    {
        var (h, a, b) = InMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(1, PacketId.PlayerDied).Single());
        Assert.Equal(a.EntityId, died.KillerId);
        Assert.Equal(DeathCause.Zone, died.Cause);
    }
}
