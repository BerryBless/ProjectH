using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 10 §2: seeded random bytes through every parser the server runs on client data and every parser the client
// runs on server data. A parser may refuse the bytes; it must never throw. Same seed = same bytes, so a failure
// reproduces.
public class ProtocolFuzzTests
{
    private const int Buffers = 100_000;
    // Phase 15: TeamMarkers reads into the caller's fixed arrays.
    private static readonly MarkerPing[] Pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
    private static readonly MarkerWaypoint[] Waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
    // Phase 16: SupplyDrops reads into the caller's fixed array.
    private static readonly SupplyDropInfo[] Drops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
    // Phase 19: VehicleStates reads into the caller's fixed array too.
    private static readonly VehicleRecord[] Vehicles = new VehicleRecord[VehicleSettings.MaxVehicles];

    // ---- Review fix D3 (SEC-24, 25, 26): values a correct server never sends are refused by the client's parsers. ----

    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    // 기능: 쓴 패킷의 PacketId를 건너뛴 읽기 도구를 만든다.
    // 입력: writer - 쓴 패킷.
    // 출력: 본문 앞의 PacketReader.
    private PacketReader Body(PacketWriter writer)
    {
        var reader = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length).ToArray());
        Assert.True(reader.TryReadPacketId(out _));
        return reader;
    }

    [Theory]
    [InlineData(float.NaN, 0f, "Alice", false)]
    [InlineData(0f, float.PositiveInfinity, "Alice", false)]
    [InlineData(1f, 2f, "Al\u0001ce", false)]   // a control character, which the server never lets join
    [InlineData(1f, 2f, "Alice", true)]
    public void PlayerSpawned_RefusesNonFiniteValues_AndInvalidNames(float x, float yaw, string name, bool valid)
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new System.Numerics.Vector3(x, 0f, 1f), Yaw = yaw, Name = name });
        PacketReader reader = Body(writer);
        Assert.Equal(valid, PlayerSpawned.TryRead(ref reader, out _));
    }

    // Ok and Resumed name the client's entity, which is never 0; MatchFull carries 0 (no entity) and must still arrive.
    [Theory]
    [InlineData(JoinResult.Ok, (ushort)0, false)]
    [InlineData(JoinResult.Resumed, (ushort)0, false)]
    [InlineData(JoinResult.Ok, (ushort)7, true)]
    [InlineData(JoinResult.MatchFull, (ushort)0, true)]
    [InlineData((JoinResult)4, (ushort)7, false)]
    public void JoinMatchResponse_RefusesEntityZeroForAJoin_AndUnknownResults(JoinResult result, ushort entity, bool valid)
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse { Result = result, MyEntityId = entity, ServerTick = 5, SimHz = 30, SnapshotHz = 15 });
        PacketReader reader = Body(writer);
        Assert.Equal(valid, JoinMatchResponse.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(5f, false)]
    [InlineData(30f, false)]
    [InlineData(20f, true)]
    [InlineData(40f, true)]
    [InlineData(80f, true)]
    [InlineData(160f, true)]
    public void BuildCatalog_TakesOnlyTheServersInterestCellSizes(float cellSize, bool valid)
    {
        var c = new BuildCatalogData
        {
            MaxResource = 500, BuildRange = 7f, ViewAngleDegrees = 75f, HarvestRange = 2.5f, HarvestCooldownTicks = 12, MinBuildIntervalTicks = 3,
            InterestCellSize = cellSize, InterestRadius = 2, InterestKeepMargin = 1,
        };
        for (int m = 0; m < 3; m++)
        {
            c.ResourceCost[m] = 10;
            c.MaxHealth[m] = (ushort)(150 + 100 * m);
            c.InitialHealth[m] = 45;
            c.ConstructionTicks[m] = (ushort)(45 * (m + 1));
        }
        var writer = new PacketWriter(_buffer);
        BuildCatalogPacket.Write(ref writer, c);
        PacketReader reader = Body(writer);
        Assert.Equal(valid, BuildCatalogPacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(3e38f, 10f, false)]
    [InlineData(0f, 3e38f, false)]
    [InlineData(ProtocolLimits.ZoneCoordLimit + 1f, 10f, false)]
    [InlineData(0f, ProtocolLimits.ZoneRadiusLimit + 1f, false)]
    [InlineData(-60f, 150f, true)]
    public void ZoneState_RefusesHugeCoordinatesAndRadii(float x, float radius, bool valid)
    {
        var writer = new PacketWriter(_buffer);
        ZoneState.Write(ref writer, new ZoneState { Phase = 1, FromX = x, FromZ = 0f, FromRadius = radius, ToX = 0f, ToZ = 0f, ToRadius = 10f, ShrinkStartTick = 1, ShrinkEndTick = 2 });
        PacketReader reader = Body(writer);
        Assert.Equal(valid, ZoneState.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(0f, 1e6f, false)]
    [InlineData(600f, 10f, false)]
    [InlineData(0f, ProtocolLimits.ProjectileSpeedLimit + 1f, false)]
    [InlineData(70f, 40f, true)]
    public void Projectiles_RefuseFarPositionsAndHugeSpeeds(float x, float speed, bool valid)
    {
        var position = new System.Numerics.Vector3(x, 2f, 0f);
        var velocity = new System.Numerics.Vector3(speed, 0f, 0f);
        var writer = new PacketWriter(_buffer);
        ProjectileSpawned.Write(ref writer, new ProjectileSpawned { Id = 1, Kind = ProjectileKind.Rocket, OwnerId = 2, Position = position, Velocity = velocity, StartTick = 9 });
        PacketReader reader = Body(writer);
        Assert.Equal(valid, ProjectileSpawned.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        ProjectileState.Write(ref writer, new ProjectileState { Id = 1, Position = position, Velocity = velocity, Tick = 9 });
        reader = Body(writer);
        Assert.Equal(valid, ProjectileState.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(40f, 0f, 4f, true)]
    [InlineData(ProtocolLimits.ProjectileSpeedLimit + 1f, 0f, 4f, false)]
    [InlineData(40f, ProtocolLimits.ProjectileGravityLimit + 1f, 4f, false)]
    [InlineData(40f, 0f, ProtocolLimits.ProjectileRadiusLimit + 1f, false)]
    public void WeaponCatalog_RefusesProjectileKindsBeyondTheLimits(float speed, float gravity, float radius, bool valid)
    {
        var weapon = new WeaponInfo
        {
            WeaponId = 6, Name = "Thunder RL", Damage = 75, FireIntervalTicks = 30, MagazineSize = 1, ReloadTicks = 90, Range = 160f,
            AmmoType = AmmoType.Rockets, Pellets = 1, Projectile = ProjectileKind.Rocket,
        };
        var rocket = new ProjectileInfo { Kind = ProjectileKind.Rocket, Speed = speed, Gravity = gravity, ExplosionRadius = radius, LifetimeTicks = 120 };
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { weapon }, new[] { rocket });
        PacketReader reader = Body(writer);
        Assert.Equal(valid, WeaponCatalogPacket.TryRead(ref reader, out _, out _));
    }

    [Fact]
    public void RandomBytes_NeverThrow_InAnyParser()
    {
        var random = new Random(10);
        var buffer = new byte[ProtocolConstants.MaxPacketSize + 100];
        int serverParsed = 0;
        int clientParsed = 0;
        for (int n = 0; n < Buffers; n++)
        {
            // Mostly short (where the length checks matter), every 100th up to a full datagram and beyond.
            int length = n % 100 == 0 ? random.Next(buffer.Length + 1) : random.Next(65);
            random.NextBytes(buffer.AsSpan(0, length));
            // Half of them start with a valid packet id, so the body parsers also see plausible headers.
            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.VehicleStates + 1);
            ReadOnlySpan<byte> data = buffer.AsSpan(0, length);

            serverParsed += ServerSide(data);
            clientParsed += ClientSide(data);
            clientParsed += ClientSide(length > 0 ? data.Slice(1) : data);   // the body after the id byte
        }
        // Body parsers only (reading the id byte alone does not count): random bytes must also reach the success
        // paths on both sides, not only the early refusals.
        Assert.True(serverParsed > 0, "no server-side body ever parsed");
        Assert.True(clientParsed > 0, "no client-side body ever parsed");
    }

    // 기능: NetworkListener가 Client 바이트에 돌리는 모든 Parser(Connect·PlayerInput·BuildRequest·MapMarker·DisconnectCodes)에 같은 바이트를 넣어 본다.
    // 입력: data - 임의 바이트.
    // 출력: 본문 파싱에 성공한 Parser 수. 어느 Parser도 예외를 던지면 안 된다.
    // What NetworkListener runs on client bytes. Counts the bodies that parsed.
    private static int ServerSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (ConnectRequestData.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (r.TryReadPacketId(out PacketId id) && id == PacketId.PlayerInput && PlayerInputPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerInputPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (r.TryReadPacketId(out PacketId buildId) && buildId == PacketId.BuildRequest && BuildRequest.TryRead(ref r, out _)) ok++;   // Phase 13
        r = new PacketReader(data);
        if (BuildRequest.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (r.TryReadPacketId(out PacketId markerId) && markerId == PacketId.MapMarker && MapMarker.TryRead(ref r, out _)) ok++;   // Phase 15
        r = new PacketReader(data);
        if (MapMarker.TryRead(ref r, out _)) ok++;
        DisconnectCodes.Read(data);   // must not throw; not counted (any first byte 1-5 "parses")
        return ok;
    }

    // 기능: NetClient와 봇이 서버 바이트에 돌리는 모든 본문 Parser에 같은 바이트를 넣어 본다.
    // 입력: data - 임의 바이트(Packet Id 다음의 본문으로 취급).
    // 출력: 파싱에 성공한 본문·항목 수. 어느 Parser도 예외를 던지면 안 된다.
    // What NetClient (and the bots) run on server bytes, body only (the id byte already read).
    private static int ClientSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (JoinMatchResponse.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerSpawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header))
        {
            ok++;
            for (int i = 0; i < header.Count && SnapshotEntity.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (SnapshotSelf.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WeaponCatalogPacket.TryRead(ref r, out _, out _)) ok++;   // Phase 17: with the projectile list
        r = new PacketReader(data);
        if (ShotFired.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (HitConfirmed.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (DamageTaken.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDied.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerRespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldItemsPacket.TryReadHeader(ref r, out int count))
        {
            ok++;
            for (int i = 0; i < count && WorldItemData.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (ItemSpawnedPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemRemoved.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (InventoryState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PickupResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ZoneState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (StatsResponse.TryRead(ref r, out _)) ok++;   // Phase 11
        r = new PacketReader(data);
        if (TransportRoutePacket.TryRead(ref r, out _)) ok++;   // Phase 12
        r = new PacketReader(data);
        if (DoorStatesPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ResourcesState.TryRead(ref r, out _)) ok++;   // Phase 13
        r = new PacketReader(data);
        if (HarvestHit.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (HarvestStatesPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (BuildResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (BuildEditRequest.TryRead(ref r, out _)) ok++;   // Phase 13.5 D4
        r = new PacketReader(data);
        if (TeamState.TryRead(ref r, out _)) ok++;   // Phase 14
        r = new PacketReader(data);
        if (PlayerDowned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ChannelState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (RebootStationsState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (TeamMarkersPacket.TryRead(ref r, Pings, Waypoints, out _, out _)) ok++;   // Phase 15
        r = new PacketReader(data);
        if (ContainerStatesPacket.TryRead(ref r, out _, out _)) ok++;   // Phase 16
        r = new PacketReader(data);
        if (SupplyDropsPacket.TryRead(ref r, Drops, out _)) ok++;
        r = new PacketReader(data);
        if (ProjectileSpawned.TryRead(ref r, out _)) ok++;   // Phase 17
        r = new PacketReader(data);
        if (ProjectileState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ProjectileExploded.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldSound.TryRead(ref r, out _)) ok++;   // Phase 18
        r = new PacketReader(data);
        if (VehicleStatesPacket.TryRead(ref r, Vehicles, out _, out _, out _)) ok++;   // Phase 19
        r = new PacketReader(data);
        if (BuildCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (BuildInterestPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (BuildEventsPacket.TryReadHeader(ref r, out _, out int placed, out int edited, out int health, out int destroyed))
        {
            ok++;
            for (int i = 0; i < placed && BuildPieceRecord.TryReadPlaced(ref r, out _); i++) ok++;
            for (int i = 0; i < edited && BuildEventsPacket.TryReadEdited(ref r, out _, out _); i++) ok++;
            for (int i = 0; i < health && BuildEventsPacket.TryReadHealth(ref r, out _, out _); i++) ok++;
            for (int i = 0; i < destroyed && BuildEventsPacket.TryReadDestroyed(ref r, out _, out _); i++) ok++;   // Phase 18: 5 bytes
        }
        r = new PacketReader(data);
        if (BuildSyncPacket.TryReadHeader(ref r, out _, out _, out int synced))
        {
            ok++;
            for (int i = 0; i < synced && BuildPieceRecord.TryReadSync(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (BuildPieceRecord.TryReadSync(ref r, out _)) ok++;
        return ok;
    }
}
