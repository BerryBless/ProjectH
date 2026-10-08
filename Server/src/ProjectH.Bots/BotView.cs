using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

// Phase 7 D3: everything one bot knows, built only from what a client receives (snapshots and events) — never
// server state. Owned by the runner thread; every collection is bounded (players by the snapshot limit, items by the
// server's world item store).
public sealed class BotView
{
    public const int MaxItems = 256;   // the server's WorldItems.Capacity

    // Join.
    public bool Joined;
    public ushort MyId;
    public byte SimHz = 30;

    // Latest snapshot.
    public bool HasSnapshot;
    public uint ServerTick;
    public uint AckInputSeq;
    public SnapshotSelf Self;
    public Vector3 MyPosition;
    public bool Alive;
    // Phase 12 D15: our movement mode (our entity's flags, or PlayerRespawned).
    public MovementMode MyMode;
    public readonly SnapshotEntity[] Others = new SnapshotEntity[ProtocolConstants.MaxSnapshotEntities];
    public int OtherCount;

    // Events.
    public WeaponInfo[]? Weapons;
    public ItemCatalogData? Catalog;
    public bool HasInventory;
    public InventoryState Inventory;
    public readonly Dictionary<ushort, WorldItemData> Items = new(MaxItems);
    public bool HasMatchState;
    public MatchState Match;
    public ZoneState Zone;
    public int HitsLanded;        // HitConfirmed received
    public int DeathsSeen;        // PlayerDied received (anyone)
    public int MatchResults;      // MatchResult received
    public MatchResult LastResult;
    // Phase 12 D15: the running match's drop transport route (TransportRoute).
    public bool HasRoute;
    public DropRoute Route;
    // Phase 13 D17: the building numbers, our resources, the latest hit's direction (towards the attacker), and what
    // the building stream told us: results by code, and how many pieces the server keeps in our window (bounded: only
    // ids, at most MaxPieces).
    public const int MaxPieces = 4096;
    public BuildCatalogData? BuildCatalog;
    public ResourcesState Resources;
    public int DamageTakenCount;
    public Vector3 LastDamageDirection;
    public readonly long[] BuildResults = new long[(int)BuildResultCode.NotFound + 1];
    public readonly HashSet<uint> Pieces = new(MaxPieces);
    // Phase 13.5 D8: each kept piece's latest shape (edit state included), in step with Pieces (QA buildEdit and the
    // reconnect check read it). Same bound and same removal as Pieces.
    public readonly Dictionary<uint, BuildPieceShape> PieceShapes = new(MaxPieces);
    // Grows with every change to Pieces or PieceShapes (readers republish only on a change).
    public long PieceVersion;

    // Phase 14 D2: our team as the server last told it (TeamState; only our own team ever comes), the knock-downs heard,
    // the latest channel event of our team, and the reboot stations' cooldowns. Fixed-size values, nothing grows.
    public bool HasTeam;
    public TeamState Team;
    public int DownsSeen;
    public bool HasChannel;
    public ChannelState LastChannel;
    public RebootStationsState Stations;
    // Phase 15 D10: our team's pings and waypoints as the last TeamMarkers had them (fixed arrays; the counts say how many
    // are filled), and how many TeamMarkers packets arrived. The bots never ping (D15); the QA tool reads these.
    public readonly MarkerPing[] Pings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
    public int PingCount;
    public readonly MarkerWaypoint[] Waypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
    public int WaypointCount;
    public long TeamMarkersReceived;
    // Phase 16 D3, D7: the loot containers that spawned and opened (ContainerStates), the match's supply drops as the last
    // SupplyDrops had them (fixed array; the count says how many are filled), and how many of each packet arrived. The bots
    // never open containers (D11); the QA tool reads these.
    public ulong ContainersSpawned;
    public ulong ContainersOpened;
    public long ContainerStatesReceived;
    public readonly SupplyDropInfo[] SupplyDrops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
    public int SupplyDropCount;
    public long SupplyDropsReceived;
    // Phase 19 D13: the vehicles as the newest VehicleStates had them (fixed array; the count says how many are filled), its
    // server tick (older packets are dropped: Unreliable), how many arrived, and the vehicle and seat our entity sits in
    // (0 / -1 = on foot). The bots never drive (D16); the QA tool reads these.
    public readonly VehicleRecord[] Vehicles = new VehicleRecord[VehicleSettings.MaxVehicles];
    public int VehicleCount;
    public uint VehicleTick;
    public long VehicleStatesReceived;
    public byte MyVehicleId;
    public int MySeat = -1;

    // 기능: VehicleStates 하나를 반영한다(마지막으로 반영한 것보다 오래된 Tick이면 버린다). 우리 Entity가 Driver·Passenger인 차량에서 좌석을 정한다.
    // 입력: records - 읽은 기록, count - 수, serverTick - 패킷 Tick.
    // 출력: 반영했으면 true.
    public bool ApplyVehicles(ReadOnlySpan<VehicleRecord> records, int count, uint serverTick)
    {
        if (VehicleStatesReceived > 0 && serverTick < VehicleTick) return false;
        VehicleTick = serverTick;
        VehicleStatesReceived++;
        VehicleCount = Math.Min(count, Vehicles.Length);
        MyVehicleId = 0;
        MySeat = -1;
        for (int i = 0; i < VehicleCount; i++)
        {
            VehicleRecord v = records[i];
            Vehicles[i] = v;
            if (MyId == 0) continue;
            if (v.Driver == MyId) { MyVehicleId = v.Id; MySeat = VehicleSettings.DriverSeat; }
            else if (v.Passenger == MyId) { MyVehicleId = v.Id; MySeat = VehicleSettings.PassengerSeat; }
        }
        return true;
    }

    // 기능: 이 Entity가 우리 팀원(자기 제외)인지 본다(D15: 봇은 팀원을 겨누지 않는다).
    // 입력: id - Entity id.
    // 출력: 마지막 TeamState에 있는 다른 구성원이면 true.
    public bool IsTeammate(ushort id)
    {
        if (!HasTeam || id == MyId) return false;
        for (int i = 0; i < Team.Count; i++)
        {
            if (Team.Get(i).EntityId == id) return true;
        }
        return false;
    }

    // 기능: 우리 팀의 경기 밖 상태를 지운다(새 라운드: 다음 경기 시작에 새 TeamState가 온다). Phase 15: 팀 Ping·Waypoint도.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void ClearTeam()
    {
        HasTeam = false;
        Team = default;
        HasChannel = false;
        PingCount = 0;
        WaypointCount = 0;
    }

    // QA tool: the latest BuildResults in arrival order (a ring of RecentBuildResultCount; BuildResultCount counts
    // every one received, so a reader that remembers the count it saw finds the new ones).
    public const int RecentBuildResultCount = 16;
    public readonly BuildResult[] RecentBuildResults = new BuildResult[RecentBuildResultCount];
    public long BuildResultCount;

    public void AddBuildResult(in BuildResult result)
    {
        RecentBuildResults[BuildResultCount % RecentBuildResultCount] = result;
        BuildResultCount++;
    }

    // 기능: 받은 DamageTaken을 센다(Phase 18 D8: 실드 맞음·깨짐도 따로 센다).
    // 입력: damage - 받은 피해 사건.
    // 출력: 반환값 없음. 피해 수, 마지막 방향, 실드 맞음·깨짐 수가 바뀐다.
    public void ApplyDamage(in DamageTaken damage)
    {
        DamageTakenCount++;
        LastDamageDirection = damage.FromDirection;
        if (damage.ShieldHit) ShieldHitsTaken++;
        if (damage.ShieldBroken) ShieldBreaksTaken++;
    }

    // 기능: 건설 패킷의 조각(배치 또는 동기화)을 기억한다. 이미 있는 id면 모양만 새로 쓴다(재접속 Sync의 최종 편집 상태).
    // 입력: piece - 받은 조각 기록.
    // 출력: 반환값 없음. Pieces와 PieceShapes가 갱신된다(MaxPieces까지만, 넘으면 새 id는 버린다).
    public void AddPiece(in BuildPieceRecord piece)
    {
        if (!Pieces.Contains(piece.Id) && Pieces.Count >= MaxPieces) return;
        Pieces.Add(piece.Id);
        PieceShapes[piece.Id] = piece.Shape;
        PieceVersion++;
    }

    // 기능: Edited 기록을 아는 조각에 적용한다(Phase 13.5 D8).
    // 입력: id - 조각 id, state - 편집 뒤 상태(BuildEdit.PackState).
    // 출력: 아는 조각이고 상태가 그 종류에 유효하면 true(모양 갱신), 모르는 조각(창 밖)이거나 잘못된 상태면 false.
    public bool ApplyEdited(uint id, ushort state)
    {
        if (!PieceShapes.TryGetValue(id, out BuildPieceShape shape)) return false;
        if (!BuildEdit.TryApply(shape, state, out BuildPieceShape edited)) return false;
        PieceShapes[id] = edited;
        PieceVersion++;
        return true;
    }

    // 기능: 조각 하나를 잊는다(파괴).
    // 입력: id - 조각 id.
    // 출력: 반환값 없음.
    public void RemovePiece(uint id)
    {
        Pieces.Remove(id);
        if (PieceShapes.Remove(id)) PieceVersion++;
    }

    // 기능: 아는 조각을 모두 잊는다(Sync reset, 관심 창 이동).
    // 입력: 없음.
    // 출력: 반환값 없음. Pieces와 PieceShapes가 빈다.
    public void ClearPieces()
    {
        Pieces.Clear();
        PieceShapes.Clear();
        PieceVersion++;
    }

    // The other player with this entity id in the latest snapshot.
    public bool TryGetOther(ushort id, out SnapshotEntity entity)
    {
        for (int i = 0; i < OtherCount; i++)
        {
            if (Others[i].EntityId != id) continue;
            entity = Others[i];
            return true;
        }
        entity = default;
        return false;
    }

    // 기능: MatchState를 반영한다. 새 라운드의 대기·카운트다운이면 지난 라운드의 경로와 팀(Phase 14)을 지운다.
    // 입력: match - 받은 경기 상태.
    // 출력: 반환값 없음.
    // MatchState. Phase 12: a new round's countdown (WaitingForPlayers, Starting) ends the last round's route; the next one
    // comes with the next match start, after this state on the same ordered channel.
    public void ApplyMatch(in MatchState match)
    {
        Match = match;
        HasMatchState = true;
        if (match.State == MatchFlowState.WaitingForPlayers || match.State == MatchFlowState.Starting)
        {
            HasRoute = false;
            ClearTeam();   // Phase 14: the lobby has no teams
        }
    }

    // Phase 7 D4 rule 2: the dev sandbox (no MatchState ever) is always "in a match".
    public bool InMatch => !HasMatchState || Match.State == MatchFlowState.Playing || Match.State == MatchFlowState.FinalPhase;

    // Phase 8: a snapshot can come in several packets of the same tick. The first packet of a new tick starts the list
    // of others over; later packets of that tick add to it.
    public void ApplySnapshot(in WorldSnapshotHeader header)
    {
        if (!HasSnapshot || header.ServerTick != ServerTick) OtherCount = 0;
        HasSnapshot = true;
        ServerTick = header.ServerTick;
        AckInputSeq = header.AckInputSeq;
        Self = header.Self;
    }

    // One entity of the snapshot just applied. Our own entity sets our position and life; the rest are others.
    public void ApplyEntity(in SnapshotEntity entity)
    {
        if (entity.EntityId == MyId)
        {
            MyPosition = entity.Position;
            Alive = entity.IsAlive;
            MyMode = entity.Mode;
            return;
        }
        if (OtherCount < Others.Length) Others[OtherCount++] = entity;
    }

    public void ApplyItem(in WorldItemData item)
    {
        // Bounded like the server's store; an id already known is an update and always fits.
        if (Items.Count < MaxItems || Items.ContainsKey(item.ItemId)) Items[item.ItemId] = item;
    }

    public void ApplyDeath(in PlayerDied died)
    {
        DeathsSeen++;
        if (died.VictimId == MyId) Alive = false;
    }

    public void ApplyRespawn(in PlayerRespawned respawned)
    {
        if (respawned.EntityId != MyId) return;
        Alive = true;
        MyPosition = respawned.Position;
        MyMode = respawned.Mode;
    }

    // The weapon in a slot, or null when the slot is empty or the catalog has not arrived.
    public WeaponInfo? WeaponInSlot(int slot)
    {
        if (!HasInventory || Weapons == null) return null;
        byte id = Inventory.GetSlot(slot).WeaponId;
        if (id == 0) return null;
        foreach (WeaponInfo w in Weapons)
        {
            if (w.WeaponId == id) return w;
        }
        return null;
    }

    // 기능: 탄 종류의 예비탄 수를 돌려준다(Phase 17: 다섯 종류 모두, InventoryState.GetAmmo).
    // 입력: type - 탄 종류.
    // 출력: 예비탄 수(모르는 종류는 0).
    public int Reserve(AmmoType type) => Inventory.GetAmmo(type);

    // 기능: 무기 id의 카탈로그 항목을 찾는다(Phase 17: 바닥의 무기가 로켓인지 보려고).
    // 입력: id - 무기 id.
    // 출력: 항목, 카탈로그가 없거나 모르는 id면 null.
    public WeaponInfo? WeaponById(byte id)
    {
        if (Weapons == null) return null;
        foreach (WeaponInfo w in Weapons)
        {
            if (w.WeaponId == id) return w;
        }
        return null;
    }

    // Phase 17 D7: projectile events heard (bots do not draw them; counted for tests and the stress report).
    public int ProjectilesSpawned;
    public int ProjectileStates;
    public int ProjectilesExploded;

    // Phase 18 (QA observation of the v17 fields; bots play no sounds): ShotFired heard (anyone's, this bot's own included)
    // and the last one's shooter and weapon id, DamageTaken shield flags, BuildEvents Destroyed records with the Collapsed
    // reason, WorldSound heard and the last one's kind and source.
    public int ShotsSeen;
    public ushort LastShotShooterId;
    public byte LastShotWeaponId;
    public int ShieldHitsTaken;
    public int ShieldBreaksTaken;
    public int CollapsesSeen;
    public int WorldSoundsReceived;
    public WorldSoundKind LastWorldSoundKind;
    public ushort LastWorldSoundSource;
}
