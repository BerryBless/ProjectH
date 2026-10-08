using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Review fix C3 (SEC-5): Match rewinds a shot by at most what the shooter's own RTT explains (GameLoop hands Match the
// peer's RoundTripTime). The LagCompensationTests set-up (a target sprinting 0.233 m per tick across the shooter's view), with
// an RTT per test: 20 ms allows 6 ticks, 200 ms the full 12. A claim older than the allowance is cut to it and counted.
public class RewindAllowanceTests
{
    private const int MovedTicks = 20;
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly Dictionary<uint, Vector3> _targetAt = new();
    private Match _match = null!;
    private PlayerEntity _shooter = null!;
    private PlayerEntity _target = null!;
    private uint _shooterSeq;
    private uint _targetSeq;
    private uint _latest;

    // 기능: 사수의 RTT를 정해 경기를 만들고 대상을 20 Tick 달리게 한다.
    // 입력: rttMs - 사수 연결의 RTT(ms).
    // 출력: 반환값 없음.
    private void Setup(int rttMs)
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true }, TestGameData.Create(), (_, _, _) => { },
            TestGameData.CombatLoadout, rttOf: peer => peer == 1 ? rttMs : 0);
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
        _match.TryGetPlayer(2, out _target);
        _shooter.State.Position = new Vector3(0f, 0f, -6f);
        _target.State.Position = new Vector3(-2f, 0f, 0f);
        _shooter.History.Reset(_match.ServerTick, _shooter.State.Position);
        _target.History.Reset(_match.ServerTick, _target.State.Position);
        for (int i = 0; i < MovedTicks; i++)
        {
            MoveTarget();
            _match.Tick();
            _targetAt[_match.ServerTick] = _target.State.Position;
        }
        _latest = _match.ServerTick;
    }

    // 기능: 대상에게 오른쪽으로 달리는 입력 하나를 넣는다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void MoveTarget()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_targetSeq, MoveX = 1f, Buttons = InputButtons.Sprint });
        _match.EnqueueInput(2, packet);
    }

    // 기능: aimTick의 대상 위치를 겨눠 한 번 쏘고 viewTick을 보았다고 주장한다.
    // 입력: viewTick - 주장하는 Tick, aimTick - 겨눈 대상 위치의 Tick.
    // 출력: 맞혔으면 true.
    private bool Shoot(float viewTick, uint aimTick)
    {
        TestAim.YawPitch(_shooter.State.Position, _targetAt[aimTick] + Chest, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_shooterSeq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = viewTick });
        _match.EnqueueInput(1, packet);
        MoveTarget();
        int shieldBefore = _target.Shield;
        _match.Tick();
        return _target.Shield < shieldBefore;
    }

    [Fact]
    public void AViewTickBeyondTheAllowance_IsClampedAndCounted()
    {
        Setup(rttMs: 20);
        Assert.False(Shoot(_latest - 12, _latest - 12));   // cut to latest - 6, where the target was 1.4 m away
        Assert.Equal(1, _shooter.RewindClamped);
        Assert.Equal(6, _shooter.RewindTicksSum);
    }

    [Fact]
    public void WithinTheAllowance_TheShotRewindsAndHits_WithoutAClamp()
    {
        Setup(rttMs: 20);
        Assert.True(Shoot(_latest - 6, _latest - 6));
        Assert.Equal(0, _shooter.RewindClamped);
        Assert.Equal(6, _shooter.RewindTicksSum);
    }

    // A normal client at 200 ms keeps the full rewind it had before.
    [Fact]
    public void At200ms_TheFullTwelveTicks_StillHit()
    {
        Setup(rttMs: 200);
        Assert.True(Shoot(_latest - 12, _latest - 12));
        Assert.Equal(0, _shooter.RewindClamped);
        Assert.Equal(12, _shooter.RewindTicksSum);
    }
}
