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

    public void ApplyDamage(in DamageTaken damage)
    {
        DamageTakenCount++;
        LastDamageDirection = damage.FromDirection;
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

    public int Reserve(AmmoType type) => type switch
    {
        AmmoType.Light => Inventory.LightAmmo,
        AmmoType.Medium => Inventory.MediumAmmo,
        AmmoType.Heavy => Inventory.HeavyAmmo,
        _ => 0,
    };
}
