using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D6. The target sprints along +X at 7 m/s (0.233 m per tick) across the shooter's view, so its
// positions 4 ticks apart are 0.93 m apart: more than the 0.35 m half-width of its hit box. A shot
// aimed where the target was at tick T only hits if the server rewinds the target to T.
public class LagCompensationTests
{
    private const int MovedTicks = 20;   // more than the 12-tick limit, so older claims can be tested
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly Match _match;
    private readonly PlayerEntity _shooter;
    private readonly PlayerEntity _target;
    private readonly Dictionary<uint, Vector3> _targetAt = new();
    private uint _shooterSeq;
    private uint _targetSeq;
    private readonly uint _latest;

    // 기능: 2인 개발 모드 Match에 사수(0, 0, -6)와 대상(-2, 0, 0)을 세우고, 대상을 MovedTicks Tick 동안 +X로 달리게 하며 Tick마다의 위치를 기록한다.
    // 입력: 없음.
    // 출력: 대상 위치 이력(_targetAt)과 마지막 Tick(_latest)이 채워진 테스트 인스턴스.
    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true },TestGameData.Create(), (_, _, _) => { }, TestGameData.CombatLoadout);
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

    // 기능: 대상(peer 2)의 +X 질주 입력 하나를 다음 순번으로 큐에 넣는다(Tick은 돌리지 않는다).
    // 입력: 없음.
    // 출력: 반환값 없음. 입력이 큐에 쌓이고 _targetSeq가 올라간다.
    private void MoveTarget()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_targetSeq, MoveX = 1f, Buttons = InputButtons.Sprint });
        _match.EnqueueInput(2, packet);
    }

    // 기능: 대상이 aimTick에 있던 자리의 가슴을 겨눈 Fire 입력을 viewTick 주장으로 넣고, 대상을 계속 달리게 하며 Tick을 한 번 돌린다.
    // 입력: viewTick - 사수가 봤다고 주장하는 Tick, aimTick - 조준점을 가져올 대상 위치 이력의 Tick.
    // 출력: 그 Tick에 대상의 보호막이 줄었으면(맞았으면) true, 아니면 false.
    // Fires once, aimed at where the target was at aimTick, claiming the shooter saw tick viewTick.
    private bool Shoot(uint viewTick, uint aimTick)
    {
        Vector3 aimPoint = _targetAt[aimTick] + Chest;
        TestAim.YawPitch(_shooter.State.Position, aimPoint, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_shooterSeq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = viewTick });
        _match.EnqueueInput(1, packet);
        MoveTarget();   // the target keeps running during the shot's tick
        int shieldBefore = _target.Shield;
        _match.Tick();
        return _target.Shield < shieldBefore;
    }

    [Fact]
    public void Setup_TargetReallyMoves()
    {
        Assert.True(Vector3.Distance(_targetAt[_latest], _targetAt[_latest - 4]) > 0.9f);
    }

    [Theory]
    [InlineData(-4, -4, true)]    // aimed where it was seen: hit (spec §5)
    [InlineData(0, -4, false)]    // same aim, but the client claims it saw "now": no rewind, miss
    [InlineData(0, 0, true)]
    [InlineData(-12, -12, true)]  // the limit itself
    [InlineData(-16, -16, false)] // older than 12 ticks: clamped to -12, where the target was 0.93 m away (spec §5)
    [InlineData(-16, -12, true)]
    public void Shot_RewindsTargetToViewTick(int viewOffset, int aimOffset, bool expectedHit)
    {
        Assert.Equal(expectedHit, Shoot((uint)(_latest + viewOffset), (uint)(_latest + aimOffset)));
    }

    // Review Focus: ViewTick comes from the client. "Now" (uint.MaxValue), a far future tick or the oldest tick must not
    // rewind further than 12 ticks, and must not crash or reach outside the history ring. Review fix D2: a uint (the old
    // NaN and +Infinity cases are uint.MaxValue, the huge past is 0).
    [Theory]
    [InlineData(uint.MaxValue, 0, true)]
    [InlineData(uint.MaxValue, -4, false)]
    [InlineData(1_000_000_000u, 0, true)]
    [InlineData(1_000_000_000u, -4, false)]
    [InlineData(0u, -12, true)]
    [InlineData(0u, -16, false)]
    public void UntrustedViewTick_IsClampedToTheLastTwelveTicks(uint viewTick, int aimOffset, bool expectedHit)
    {
        Assert.Equal(expectedHit, Shoot(viewTick, (uint)(_latest + aimOffset)));
    }

    // The shooter's own position is never rewound: the shot starts at its current eye.
    [Fact]
    public void Shooter_FiresFromCurrentPosition()
    {
        var shots = new List<Vector3>();
        var match = new Match(new ServerOptions { MaxPlayers = 1, DevRespawn = true },TestGameData.Create(), (_, data, _) =>
        {
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
                shots.Add(shot.Start);
        }, TestGameData.CombatLoadout);
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        a.State.Position = new Vector3(1f, 0f, 1f);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, ViewTick = 0 });   // review fix D2: the oldest claim
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(new Vector3(1f, 1.6f, 1f), Assert.Single(shots));
    }

    // A respawn is a teleport. A rewind into the ticks before it must find the spawn point, not the body.
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true },TestGameData.Create(), static (_, _, _) => { }, TestGameData.CombatLoadout);
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var target);

        // Put the target down far away by hand; the kill itself is covered by CombatMatchTests.
        var body = new Vector3(10f, 0f, -10f);
        target.State.Position = body;
        target.History.Reset(match.ServerTick, body);
        target.Alive = false;
        target.RespawnAtTick = match.ServerTick + 5;
        for (int i = 0; i < 300 && !target.Alive; i++) match.Tick();
        Assert.True(target.Alive);
        Vector3 spawn = Match.SpawnPosition(target.EntityId);
        Assert.Equal(spawn, target.State.Position);

        TestAim.YawPitch(shooter.State.Position, spawn + Chest, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick - 6 });
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, target.Shield);
    }

    // Server hot path: a tick that fires, rewinds, hits and sends allocates nothing (no per-shot garbage).
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2, DevRespawn = true },TestGameData.Create(), static (_, _, _) => { }, TestGameData.CombatLoadout);
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var target);
        TestAim.YawPitch(shooter.State.Position, target.State.Position + Chest, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };

        long allocated = 0;
        for (uint shot = 1; shot <= 4; shot++)   // shots 1-3 warm up JIT and first-use paths; 4 is measured
        {
            packet.Set(0, new InputCommand { Seq = shot, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick });
            match.EnqueueInput(1, packet);
            int before = target.Shield + target.Health;

            long start = GC.GetAllocatedBytesForCurrentThread();
            match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.True(target.Shield + target.Health < before, "every measured tick must include a hit");
            match.Tick();
            match.Tick();
        }
        Assert.Equal(0, allocated);
    }
}
