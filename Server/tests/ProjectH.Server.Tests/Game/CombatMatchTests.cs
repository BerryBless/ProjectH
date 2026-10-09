using System;
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

// Combat through Match.Tick with the test catalog and the combat loadout (TestGameData.CombatLoadout,
// slot 0: 30 damage, 3-tick interval, 6 rounds; shield 50). Players stand still unless a test moves them;
// with no input the server repeats a zero move.
public class CombatMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    // 기능: 테스트 카탈로그로 3인 개발 모드 Match를 만든다.
    // 입력: 없음.
    // 출력: 참가자 없는 Match를 든 테스트 인스턴스.
    public CombatMatchTests()
    {
        _match = NewMatch(TestGameData.Create());
    }

    // 기능: 보낸 패킷을 _sent에 모으는 3인 개발 모드 Match(Snapshot 2 Tick마다)를 만든다.
    // 입력: data - 무기·아이템 카탈로그.
    // 출력: 전투 장비로 시작하는 참가자 없는 Match.
    private Match NewMatch(GameData data) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2, DevRespawn = true }, data,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), TestGameData.CombatLoadout);

    // 기능: 플레이어를 경기에 들여보내고 주어진 자리에 세운다(지연 보상 이력도 그 자리로 맞춘다).
    // 입력: peer - 연결 id, feet - 발 위치.
    // 출력: 들어온 플레이어. 입장이 거절되면 테스트가 실패한다.
    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        // Shots rewind targets through their history (lag compensation), so the history must see the move too.
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    // 기능: 입력 하나에 그 플레이어의 다음 순번을 붙여 경기 입력 큐에 넣는다(Tick은 돌리지 않는다).
    // 입력: player - 보내는 플레이어, command - 보낼 입력(Seq는 덮어쓴다).
    // 출력: 반환값 없음. 입력이 큐에 쌓이고 순번이 올라간다.
    private void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        _match.EnqueueInput(player.PeerId, packet);
    }

    // 기능: 사수의 눈에서 월드 한 점을 겨눈 입력을 현재 Tick의 ViewTick으로 큐에 넣는다.
    // 입력: shooter - 사수, point - 겨눌 월드 좌표, buttons - 누를 버튼(기본 Fire).
    // 출력: 반환값 없음. 조준 입력이 큐에 쌓인다.
    // An input aimed from the shooter's eye at a world point, rendered at the current tick.
    private void FireAt(PlayerEntity shooter, Vector3 point, InputButtons buttons = InputButtons.Fire)
    {
        TestAim.YawPitch(shooter.State.Position, point, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = buttons, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
    }

    // 기능: 한 peer에게 간 특정 종류의 패킷을 보낸 순서대로 모은다.
    // 입력: peer - 받는 연결 id, id - 패킷 종류.
    // 출력: 조건에 맞는 Sent 목록.
    private List<Sent> SentTo(int peer, PacketId id) => _sent.Where(s => s.PeerId == peer && s.Id == id).ToList();

    // 기능: 보낸 패킷의 id 바이트를 건너뛴 본문 Reader를 만든다.
    // 입력: s - 보낸 패킷.
    // 출력: 본문 첫 바이트에 놓인 PacketReader.
    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    // 기능: 보낸 패킷을 ShotFired로 읽는다.
    // 입력: s - 보낸 패킷.
    // 출력: 읽은 ShotFired. 읽기에 실패하면 테스트가 실패한다.
    private static ShotFired ReadShot(Sent s) { var r = Reader(s); Assert.True(ShotFired.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 HitConfirmed로 읽는다.
    // 입력: s - 보낸 패킷.
    // 출력: 읽은 HitConfirmed. 읽기에 실패하면 테스트가 실패한다.
    private static HitConfirmed ReadHit(Sent s) { var r = Reader(s); Assert.True(HitConfirmed.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 DamageTaken으로 읽는다.
    // 입력: s - 보낸 패킷.
    // 출력: 읽은 DamageTaken. 읽기에 실패하면 테스트가 실패한다.
    private static DamageTaken ReadDamage(Sent s) { var r = Reader(s); Assert.True(DamageTaken.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 PlayerDied로 읽는다.
    // 입력: s - 보낸 패킷.
    // 출력: 읽은 PlayerDied. 읽기에 실패하면 테스트가 실패한다.
    private static PlayerDied ReadDied(Sent s) { var r = Reader(s); Assert.True(PlayerDied.TryRead(ref r, out var v)); return v; }
    // 기능: 보낸 패킷을 PlayerRespawned로 읽는다.
    // 입력: s - 보낸 패킷.
    // 출력: 읽은 PlayerRespawned. 읽기에 실패하면 테스트가 실패한다.
    private static PlayerRespawned ReadRespawned(Sent s) { var r = Reader(s); Assert.True(PlayerRespawned.TryRead(ref r, out var v)); return v; }

    // 기능: 한 peer가 받은 마지막 WorldSnapshot을 머리와 엔티티 목록으로 읽는다.
    // 입력: peer - 받는 연결 id.
    // 출력: (Snapshot 머리, 엔티티 목록). Snapshot이 없거나 읽기에 실패하면 테스트가 실패한다.
    private (WorldSnapshotHeader header, List<SnapshotEntity> entities) LastSnapshotFor(int peer)
    {
        var reader = Reader(_sent.Last(s => s.PeerId == peer && s.Id == PacketId.WorldSnapshot));
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        var list = new List<SnapshotEntity>();
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref reader, out var e));
            list.Add(e);
        }
        return (header, list);
    }

    // 기능: 사수가 대상의 가슴을 3 Tick 간격으로 다섯 번 쏴 죽인다(30 x 5 = 보호막 50 + 체력 100).
    // 입력: shooter - 사수, target - 대상.
    // 출력: 반환값 없음. 대상이 죽은 상태가 된다. 살아 있으면 테스트가 실패한다.
    // Five hits of 30 = shield 50 + health 100. The auto weapon fires every 3 ticks.
    private void KillWithFiveHits(PlayerEntity shooter, PlayerEntity target)
    {
        for (int i = 0; i < 5; i++)
        {
            FireAt(shooter, target.State.Position + Chest);
            _match.Tick();
            _match.Tick();
            _match.Tick();
        }
        Assert.False(target.Alive);
    }

    [Fact]
    public void Hit_TakesShieldFirst_AndNotifiesShooterAndTarget()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, b.Shield);

        var hit = ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed)));
        Assert.Equal(b.EntityId, hit.TargetId);
        Assert.Equal(TestWeapons.AutoDamage, hit.Damage);
        Assert.False(hit.Killed);
        Assert.Equal(DeliveryMethod.ReliableOrdered, SentTo(1, PacketId.HitConfirmed)[0].Method);

        var damage = ReadDamage(Assert.Single(SentTo(2, PacketId.DamageTaken)));
        Assert.Equal(a.EntityId, damage.AttackerId);
        Assert.Equal(-1f, damage.FromDirection.Z, 4);   // the attacker is towards -Z

        // Everyone sees the tracer, Unreliable, from the eye to the target's front face.
        foreach (int peer in new[] { 1, 2 })
        {
            var sent = Assert.Single(SentTo(peer, PacketId.ShotFired));
            Assert.Equal(DeliveryMethod.Unreliable, sent.Method);
            var shot = ReadShot(sent);
            Assert.Equal(a.EntityId, shot.ShooterId);
            Assert.Equal(new Vector3(0f, 1.6f, -3f), shot.Start);
            Assert.Equal(3f - MoveSettings.HalfWidth, shot.End.Z, 3);
        }
    }

    // Review Focus: a wall between the eye and the target stops the shot, whatever the client aimed at.
    [Fact]
    public void Shot_AtTargetBehindPillar_HitsThePillar()
    {
        // Ruins pillar (-46, 1.5, -46), 1 x 3 x 1: faces at z -46.5 and -45.5. The target stands right behind it.
        var a = Join(1, new Vector3(-46f, 0f, -49f));
        var b = Join(2, new Vector3(-46f, 0f, -43f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Empty(SentTo(2, PacketId.DamageTaken));
        Assert.Equal(-46.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
    }

    // Task 3 review (b): the world distance is the players' max distance and a tie goes to the player.
    // A target pressed flat against the pillar's back face (its front face at z -45.5) is still behind
    // the pillar's front face (z -46.5), so it must be missed.
    [Fact]
    public void Shot_AtTargetFlushAgainstPillarBack_HitsThePillar()
    {
        var a = Join(1, new Vector3(-46f, 0f, -49f));
        var b = Join(2, new Vector3(-46f, 0f, -45.5f + MoveSettings.HalfWidth));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(-46.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
    }

    // Phase 6 D11: the north hill (4 m at (0, 46)) stands between the two. The shot stops on the near slope, at the
    // height where the chest-high line meets the terrain, and nobody is hit.
    [Fact]
    public void Shot_AtTargetBehindAHill_HitsTheHill()
    {
        var a = Join(1, new Vector3(-30f, 0f, 46f));
        var b = Join(2, new Vector3(30f, 0f, 46f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        ShotFired shot = ReadShot(SentTo(1, PacketId.ShotFired)[0]);
        Assert.InRange(shot.End.X, -18f, 0f);
        Assert.Equal(GameMap.Terrain.Height(shot.End.X, shot.End.Z), shot.End.Y, 2);
    }

    [Fact]
    public void TwoTargetsInLine_NearerIsHit()
    {
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var near = Join(2, new Vector3(0f, 0f, -1f));
        var far = Join(3, new Vector3(0f, 0f, 3f));

        FireAt(a, far.State.Position + Chest);   // the line passes through the nearer player
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, near.Shield);
        Assert.Equal(TestGameData.LoadoutShield, far.Shield);
    }

    [Fact]
    public void TargetBeyondRange_IsMissed()
    {
        _match = NewMatch(TestGameData.Create(autoRange: 5f));
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));   // front face 5.65 m from the eye line

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
    }

    [Fact]
    public void ShooterCannotHitItself()
    {
        var a = Join(1, new Vector3(0f, 0f, 0f));
        FireAt(a, new Vector3(0f, 0f, 0.01f));   // straight down through its own box
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(0f, ReadShot(SentTo(1, PacketId.ShotFired).Last()).End.Y, 3);   // stopped at the floor
    }

    [Fact]
    public void DeadPlayer_IsNotHit_AndDoesNotBlock()
    {
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var dead = Join(2, new Vector3(0f, 0f, -1f));
        var behind = Join(3, new Vector3(0f, 0f, 3f));
        KillWithFiveHits(a, dead);
        _sent.Clear();

        FireAt(a, behind.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, behind.Shield);
        Assert.Equal(behind.EntityId, ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed))).TargetId);
    }

    [Fact]
    public void FiveHits_Kill_AndEveryoneIsTold()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        KillWithFiveHits(a, b);

        Assert.Equal(0, b.Health);
        Assert.Equal(0, b.Shield);
        var hits = SentTo(1, PacketId.HitConfirmed).Select(ReadHit).ToList();
        Assert.Equal(5, hits.Count);
        Assert.Equal(new[] { false, false, false, false, true }, hits.Select(h => h.Killed));
        foreach (int peer in new[] { 1, 2 })
        {
            var died = ReadDied(Assert.Single(SentTo(peer, PacketId.PlayerDied)));
            Assert.Equal(b.EntityId, died.VictimId);
            Assert.Equal(a.EntityId, died.KillerId);
        }
        // The victim hears the last DamageTaken before its PlayerDied (same ReliableOrdered channel).
        var toB = _sent.Where(s => s.PeerId == 2 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(toB.LastIndexOf(PacketId.DamageTaken) < toB.IndexOf(PacketId.PlayerDied));

        _match.Tick();   // the kill took 15 ticks; tick 16 sends a snapshot
        var (header, entities) = LastSnapshotFor(2);
        Assert.Equal(0, header.Self.Health);
        Assert.False(entities.Single(e => e.EntityId == b.EntityId).IsAlive);
        Assert.True(entities.Single(e => e.EntityId == a.EntityId).IsAlive);
    }

    [Fact]
    public void DeadPlayer_InputIsAcked_ButMovesAndFiresNothing()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        KillWithFiveHits(a, b);
        Vector3 body = b.State.Position;
        _sent.Clear();

        for (int i = 0; i < 5; i++)
        {
            TestAim.YawPitch(b.State.Position, a.State.Position + Chest, out float yaw, out float pitch);
            Send(b, new InputCommand { MoveY = 1f, Buttons = InputButtons.Fire | InputButtons.Jump, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.Tick();
        }

        Assert.Equal(body, b.State.Position);
        Assert.Equal(_seq[2], b.LastProcessedSeq);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
    }

    [Fact]
    public void Respawn_ThreeSecondsLater_AtSpawnWithFullState()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 1;
        KillWithFiveHits(a, b);
        uint diedAt = b.RespawnAtTick - 90;   // 3 s at 30 Hz

        while (_match.ServerTick < diedAt + 90)
        {
            Assert.False(b.Alive);
            _match.Tick();
        }
        Assert.False(b.Alive);   // the respawn happens at the start of the tick where now == RespawnAtTick
        _match.Tick();

        Assert.True(b.Alive);
        Assert.Equal(Match.SpawnPosition(b.EntityId), b.State.Position);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Equal(TestWeapons.AutoMagazine, b.Inventory.Slots[0].MagAmmo);
        Assert.Equal(TestWeapons.SemiMagazine, b.Inventory.Slots[1].MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, b.Inventory.GetAmmo(AmmoType.Medium));
        Assert.False(b.Reloading);

        foreach (int peer in new[] { 1, 2 })
        {
            var respawned = ReadRespawned(Assert.Single(SentTo(peer, PacketId.PlayerRespawned)));
            Assert.Equal(b.EntityId, respawned.EntityId);
            Assert.Equal(Match.SpawnPosition(b.EntityId), respawned.Position);
            var reliable = _sent.Where(s => s.PeerId == peer && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
            Assert.True(reliable.IndexOf(PacketId.PlayerDied) < reliable.IndexOf(PacketId.PlayerRespawned));
        }
    }

    // The missed-input repeat must not carry a move from the old life into the new one: a respawned
    // player whose inputs are late stands still at the spawn point (and keeps its facing).
    [Fact]
    public void Respawn_ResetsTheMissedInputRepeat()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        KillWithFiveHits(a, b);

        // b keeps sending a sprint while dead (a client may), then goes quiet exactly at the respawn.
        while (_match.ServerTick + 1 < b.RespawnAtTick)
        {
            Send(b, new InputCommand { MoveY = 1f, Buttons = InputButtons.Sprint, Yaw = 90f });
            _match.Tick();
        }
        b.MissedTicks = 7;   // stale count from the old life must not survive either
        b.State.Yaw = 123f;  // the facing the respawn keeps (the dead inputs said 90)
        _match.Tick();       // last dead tick, already without input (a missed tick)
        Assert.False(b.Alive);
        float yaw = b.State.Yaw;
        Assert.Equal(123f, yaw);

        _match.Tick();       // respawn + first tick without input
        Assert.True(b.Alive);
        for (int i = 0; i < 5; i++) _match.Tick();

        Assert.Equal(Match.SpawnPosition(b.EntityId), b.State.Position);
        Assert.Equal(yaw, b.State.Yaw);
        Assert.Equal(6, b.MissedTicks);

        uint acked = b.LastProcessedSeq;
        Send(b, new InputCommand());
        _match.Tick();
        Assert.Equal(acked + 1, b.LastProcessedSeq);
    }

    // A kill ends a running reload: the dead player's snapshot must not report one.
    [Fact]
    public void Kill_DuringReload_ClearsTheReload()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 1;
        Send(b, new InputCommand { Buttons = InputButtons.Reload });
        _match.Tick();
        Assert.True(b.Reloading);

        KillWithFiveHits(a, b);
        Assert.False(b.Reloading);
        _match.Tick();
        _match.Tick();

        var (header, _) = LastSnapshotFor(2);
        Assert.False(b.Alive);
        Assert.Equal(0, header.Self.ReloadRemainingTicks);
    }

    // Review Focus: the server repeats the last input while inputs are missing (grace window).
    // That repeat must never fire, or a held trigger would keep shooting after the client let go.
    [Fact]
    public void MissedInputTicks_DoNotRepeatFire()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        FireAt(a, b.State.Position + Chest);
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks

        Assert.Single(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(TestWeapons.AutoMagazine - 1, a.Inventory.Slots[0].MagAmmo);
    }

    // Spec §5: a flood of fire inputs (one per tick, as fast as the server takes them) is limited to the interval.
    [Fact]
    public void FireEveryTick_IsLimitedToWeaponInterval()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        for (int i = 0; i < 9; i++)
        {
            FireAt(a, b.State.Position + Chest);
            _match.Tick();
        }

        Assert.Equal(3, SentTo(1, PacketId.ShotFired).Count);
    }

    // Review Focus: malformed aim from a client is no shot and costs nothing; an out-of-range pitch is clamped.
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void NonFiniteAim_IsNoShot_AndAmmoUnchanged(float yaw, float pitch)
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Join(2, new Vector3(0f, 0f, 3f));

        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();

        Assert.Empty(SentTo(1, PacketId.ShotFired));
        Assert.Equal(TestWeapons.AutoMagazine, a.Inventory.Slots[0].MagAmmo);
        Assert.Equal(0u, a.Inventory.Slots[0].NextFireTick);
    }

    [Fact]
    public void HugePitch_IsClampedTo89_AndShotStaysFinite()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = 1e30f, AimPitch = 1e30f, ViewTick = _match.ServerTick });
        _match.Tick();

        var shot = ReadShot(Assert.Single(SentTo(1, PacketId.ShotFired)));
        Assert.True(float.IsFinite(shot.End.X) && float.IsFinite(shot.End.Z));
        Assert.Equal(0f, shot.End.Y, 3);   // 89 degrees down hits the floor right in front
    }

    [Fact]
    public void Snapshot_CarriesEachRecipientsOwnCombatValues()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        FireAt(a, b.State.Position + Chest);
        _match.Tick();
        Send(b, new InputCommand { Buttons = InputButtons.Slot2 });
        _match.Tick();

        var (selfA, _) = LastSnapshotFor(1);
        Assert.Equal(CombatRules.MaxHealth, selfA.Self.Health);
        Assert.Equal(TestGameData.LoadoutShield, selfA.Self.Shield);
        Assert.Equal(0, selfA.Self.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, selfA.Self.Ammo);
        Assert.Equal(0, selfA.Self.ReloadRemainingTicks);

        var (selfB, _) = LastSnapshotFor(2);
        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, selfB.Self.Shield);
        Assert.Equal(1, selfB.Self.WeaponSlot);
        Assert.Equal(TestWeapons.SemiMagazine, selfB.Self.Ammo);
    }

    [Fact]
    public void Snapshot_ReloadRemaining_IsAtLeastOneUntilTheReloadEnds()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f, ViewTick = _match.ServerTick });
        _match.Tick();
        Send(a, new InputCommand { Buttons = InputButtons.Reload, ViewTick = _match.ServerTick });
        _match.Tick();   // reload starts at now = 1, ends at 31

        while (a.Reloading)
        {
            if (_match.ServerTick % 2 == 1)   // the next Tick sends a snapshot
            {
                _match.Tick();
                var (header, _) = LastSnapshotFor(1);
                if (a.Reloading) Assert.True(header.Self.ReloadRemainingTicks >= 1);
            }
            else
            {
                _match.Tick();
            }
        }
        _match.Tick();
        _match.Tick();
        Assert.Equal(0, LastSnapshotFor(1).header.Self.ReloadRemainingTicks);
        Assert.Equal(TestWeapons.AutoMagazine, LastSnapshotFor(1).header.Self.Ammo);
    }
}
