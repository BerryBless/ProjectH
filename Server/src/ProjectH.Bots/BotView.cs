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
    public readonly long[] BuildResults = new long[(int)BuildResultCode.BudgetFull + 1];
    public readonly HashSet<uint> Pieces = new(MaxPieces);

    // 기능: 받은 DamageTaken을 기록해 BotBuilder가 방어벽을 세울 근거로 쓰게 한다.
    // 입력: damage - 서버가 보낸 피격 이벤트.
    // 출력: 반환값 없음. 피격 횟수가 늘고 마지막 피격 방향(공격자 쪽)이 갱신된다.
    public void ApplyDamage(in DamageTaken damage)
    {
        DamageTakenCount++;
        LastDamageDirection = damage.FromDirection;
    }

    // 기능: 건설 패킷으로 알게 된 조각 ID를 MaxPieces 한도 안에서 기억한다.
    // 입력: id - 배치·동기화된 건설 조각 ID.
    // 출력: 반환값 없음. 한도 미만이면 Pieces에 추가되고, 가득 찼으면 무시된다.
    // A building packet's pieces (placed or synced: kept up to MaxPieces; destroyed or out of the window: forgotten).
    public void AddPiece(uint id)
    {
        if (Pieces.Count < MaxPieces) Pieces.Add(id);
    }

    // 기능: 최신 스냅샷의 다른 플레이어 목록에서 엔티티 ID로 한 명을 찾는다.
    // 입력: id - 찾을 엔티티 ID.
    // 출력: 있으면 true와 그 엔티티, 없으면 false와 default.
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

    // 기능: 받은 MatchState를 반영하고, 새 라운드 대기·카운트다운이면 이전 라운드의 수송기 경로를 버린다.
    // 입력: match - 서버가 보낸 매치 상태.
    // 출력: 반환값 없음. Match·HasMatchState가 갱신되고, WaitingForPlayers·Starting이면 HasRoute가 false가 된다.
    // MatchState. Phase 12: a new round's countdown (WaitingForPlayers, Starting) ends the last round's route; the next one
    // comes with the next match start, after this state on the same ordered channel.
    public void ApplyMatch(in MatchState match)
    {
        Match = match;
        HasMatchState = true;
        if (match.State == MatchFlowState.WaitingForPlayers || match.State == MatchFlowState.Starting) HasRoute = false;
    }

    // Phase 7 D4 rule 2: the dev sandbox (no MatchState ever) is always "in a match".
    public bool InMatch => !HasMatchState || Match.State == MatchFlowState.Playing || Match.State == MatchFlowState.FinalPhase;

    // 기능: 스냅샷 헤더를 반영한다. 새 Tick의 첫 패킷이면 다른 플레이어 목록을 비운다.
    // 입력: header - 스냅샷 패킷 헤더(서버 Tick, 처리된 입력 Seq, 내 상태).
    // 출력: 반환값 없음. ServerTick·AckInputSeq·Self가 갱신되고, Tick이 바뀌었으면 OtherCount가 0이 된다.
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

    // 기능: 방금 반영한 스냅샷의 엔티티 하나를 반영한다.
    // 입력: entity - 스냅샷 엔티티.
    // 출력: 반환값 없음. 내 엔티티면 내 위치·생존·이동 모드가 갱신되고, 아니면 Others 배열 한도 안에서 추가된다.
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

    // 기능: 월드 아이템 하나를 추가하거나 갱신한다(MaxItems 한도).
    // 입력: item - 서버가 알린 월드 아이템.
    // 출력: 반환값 없음. 이미 아는 ID이거나 한도 미만이면 Items에 저장되고, 새 ID인데 가득 찼으면 무시된다.
    public void ApplyItem(in WorldItemData item)
    {
        // Bounded like the server's store; an id already known is an update and always fits.
        if (Items.Count < MaxItems || Items.ContainsKey(item.ItemId)) Items[item.ItemId] = item;
    }

    // 기능: 사망 이벤트를 기록한다(누구의 죽음이든 센다).
    // 입력: died - 서버가 보낸 사망 이벤트.
    // 출력: 반환값 없음. DeathsSeen이 늘고, 희생자가 이 봇이면 Alive가 false가 된다.
    public void ApplyDeath(in PlayerDied died)
    {
        DeathsSeen++;
        if (died.VictimId == MyId) Alive = false;
    }

    // 기능: 부활 이벤트가 내 것이면 생존 상태와 위치를 되살린다.
    // 입력: respawned - 서버가 보낸 부활 이벤트.
    // 출력: 반환값 없음. 내 엔티티면 Alive가 true가 되고 위치·이동 모드가 갱신된다. 다른 플레이어면 변화 없음.
    public void ApplyRespawn(in PlayerRespawned respawned)
    {
        if (respawned.EntityId != MyId) return;
        Alive = true;
        MyPosition = respawned.Position;
        MyMode = respawned.Mode;
    }

    // 기능: 인벤토리 슬롯에 든 무기의 카탈로그 정보를 찾는다.
    // 입력: slot - 무기 슬롯 번호(0부터).
    // 출력: 무기 정보. 슬롯이 비었거나 인벤토리·무기 카탈로그를 아직 받지 못했거나 카탈로그에 없으면 null.
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

    // 기능: 탄종별 예비 탄약 수를 인벤토리에서 읽는다.
    // 입력: type - 탄약 종류.
    // 출력: 그 탄종의 예비 탄약 수. 알 수 없는 탄종이면 0.
    public int Reserve(AmmoType type) => type switch
    {
        AmmoType.Light => Inventory.LightAmmo,
        AmmoType.Medium => Inventory.MediumAmmo,
        AmmoType.Heavy => Inventory.HeavyAmmo,
        _ => 0,
    };
}
