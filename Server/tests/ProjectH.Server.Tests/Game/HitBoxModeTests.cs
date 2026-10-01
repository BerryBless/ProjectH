using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D13: the hit box follows the mode (1.2 m crouched or sliding), the lag-compensation history keeps the mode,
// and a crouched shooter's eye is at 1.0 m. In the dev sandbox with the combat loadout (slot 0: 30 damage).
public class HitBoxModeTests
{
    private readonly List<(int Peer, PacketId Id, byte[] Data)> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private readonly Match _match;

    public HitBoxModeTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 3, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0], data.ToArray())), TestGameData.CombatLoadout);
    }

    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    private void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        _match.EnqueueInput(player.PeerId, packet);
    }

    private int Hits(int peer) => _sent.Count(s => s.Peer == peer && s.Id == PacketId.HitConfirmed);

    // The target crouches (held) for a few ticks, then the shooter fires at a point height above the target's feet.
    private bool ShootAtCrouched(float height)
    {
        PlayerEntity shooter = Join(1, new Vector3(0f, 0f, -6f));
        PlayerEntity target = Join(2, new Vector3(0f, 0f, 0f));
        for (int i = 0; i < 3; i++)
        {
            Send(target, new InputCommand { Buttons = InputButtons.Crouch });
            _match.Tick();
        }
        Assert.Equal(MovementMode.Crouch, target.State.Mode);
        Send(target, new InputCommand { Buttons = InputButtons.Crouch });
        TestAim.YawPitch(shooter.State.Position, target.State.Position + new Vector3(0f, height, 0f), out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();
        return Hits(1) > 0;
    }

    [Fact]
    public void ACrouchedTarget_IsMissedAboveOnePointTwo()
    {
        Assert.False(ShootAtCrouched(1.5f));
    }

    [Fact]
    public void ACrouchedTarget_IsHitBelowOnePointTwo()
    {
        Assert.True(ShootAtCrouched(0.9f));
    }

    [Fact]
    public void ACrouchedShooter_ShootsFromOneMetre()
    {
        PlayerEntity shooter = Join(1, new Vector3(0f, 0f, -6f));
        Join(2, new Vector3(5f, 0f, 5f));
        for (int i = 0; i < 2; i++)
        {
            Send(shooter, new InputCommand { Buttons = InputButtons.Crouch });
            _match.Tick();
        }
        TestAim.YawPitch(shooter.State.Position, new Vector3(0f, 1f, 10f), CombatRules.CrouchEyeHeight, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Crouch | InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();
        var shot = _sent.Last(s => s.Id == PacketId.ShotFired);
        var r = new PacketReader(shot.Data);
        r.TryReadPacketId(out _);
        Assert.True(ShotFired.TryRead(ref r, out ShotFired fired));
        Assert.Equal(shooter.State.Position.Y + CombatRules.CrouchEyeHeight, fired.Start.Y, 4);
        Assert.Equal(CombatRules.CrouchEyeHeight, CombatRules.EyeHeightOf(MovementMode.Slide));
        Assert.Equal(CombatRules.EyeHeight, CombatRules.EyeHeightOf(MovementMode.Ground));
    }

    [Fact]
    public void TheHistory_KeepsTheModeOfEachTick()
    {
        var history = new PositionHistory();
        history.Reset(10, Vector3.Zero, MovementMode.Ground);
        history.Record(11, Vector3.UnitX, MovementMode.Crouch);
        history.Record(12, 2f * Vector3.UnitX, MovementMode.Transport);

        Assert.Equal(new Vector3(0.5f, 0f, 0f), history.Sample(10.5, out MovementMode between));
        Assert.Equal(MovementMode.Ground, between);   // the older record's
        history.Sample(11, out MovementMode at11);
        Assert.Equal(MovementMode.Crouch, at11);
        history.Sample(30, out MovementMode past);
        Assert.Equal(MovementMode.Transport, past);
        history.Sample(1, out MovementMode before);
        Assert.Equal(MovementMode.Ground, before);
    }

    [Fact]
    public void TracePlayer_UsesTheGivenHeight()
    {
        var feet = new Vector3(0f, 0f, 5f);
        Assert.True(HitScan.TracePlayer(new Vector3(0f, 1.1f, 0f), Vector3.UnitZ, 100f, feet, 1.2f, out _));
        Assert.False(HitScan.TracePlayer(new Vector3(0f, 1.3f, 0f), Vector3.UnitZ, 100f, feet, 1.2f, out _));
        Assert.True(HitScan.TracePlayer(new Vector3(0f, 1.3f, 0f), Vector3.UnitZ, 100f, feet, out _));   // standing
    }
}
