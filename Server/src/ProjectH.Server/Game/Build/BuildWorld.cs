using System;
using System.Collections.Generic;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Build;

// Phase 13 D10: what the server keeps of one piece. Small and plain; construction progress and current health are not
// stored but computed when needed (Health), so nothing runs over every piece every tick (request §63, §64).
public struct BuildPiece
{
    public uint Id;
    public BuildPieceShape Shape;
    public BuildMaterialType Material;
    public ushort Owner;          // the builder's entity id
    public uint CreatedTick;
    public ushort Damage;         // taken so far
    public bool Grounded;         // D12: touches the terrain or a map box; set at placement, recomputed only by Match.ApplyEdit when an edit turns a ramp (Phase 13.5 D7)
}

// Phase 13 D10 (request §124, §125): every piece of the current match, with one index per kind of lookup:
//  - id -> storage slot: PieceGrid's dictionary;
//  - grid slot (BuildSlotKey) -> id: _bySlot, for "is this slot taken" and the support neighbours (D12);
//  - build cell -> ids: PieceGrid's columns, for collision (CollisionWorld), shots (D11) and interest cells (D14).
// Storage is per-slot arrays that grow with PieceGrid's slots up to MaxPiecesPerMatch (an empty match costs little) and
// are reused through it; ids only grow (never reused, also across rounds). Clear empties everything at a round reset or a
// match start. Game loop thread only.
public sealed class BuildWorld
{
    private readonly BuildingCatalog _catalog;
    private BuildPiece[] _pieces;
    private readonly Dictionary<uint, uint> _bySlot = new();
    private readonly Dictionary<ushort, int> _ownerCounts = new();
    private uint _nextId = 1;

    // 기능: 빈 조각 저장소를 만든다(배열은 256 또는 상한까지로 시작해 상한까지 자란다).
    // 입력: catalog - 조각 상한과 재료 수치를 담은 건설 데이터.
    // 출력: 조각이 없고 다음 id가 1인 BuildWorld.
    public BuildWorld(BuildingCatalog catalog)
    {
        _catalog = catalog;
        Capacity = catalog.MaxPiecesPerMatch;
        Grid = new PieceGrid(Capacity);
        _pieces = new BuildPiece[Math.Min(Capacity, 256)];
    }

    public int Capacity { get; }
    public int Count => Grid.Count;
    // The pieces' shapes by id and by cell: the collision side, shared with client prediction (CollisionWorld.Gather).
    public PieceGrid Grid { get; }
    // The next id to hand out (ids before it were used in this server's life).
    public uint NextId => _nextId;

    // 기능: 조각 id의 저장 slot을 찾는다.
    // 입력: id - 조각 id, slot - 결과.
    // 출력: 있으면 true와 slot, 없으면 false(slot = -1).
    public bool TryGetSlot(uint id, out int slot)
    {
        slot = Grid.SlotOf(id);
        return slot >= 0;
    }

    // 기능: 저장 slot의 조각에 참조로 접근한다(빈 slot이면 Id가 0인 기본값).
    // 입력: slot - 저장 slot(범위 안이어야 한다).
    // 출력: 그 slot의 BuildPiece 참조.
    public ref BuildPiece At(int slot) => ref _pieces[slot];

    // 기능: 조각 id가 지금 서 있는지 본다.
    // 입력: id - 조각 id.
    // 출력: 있으면 true.
    public bool Contains(uint id) => Grid.Contains(id);

    // 기능: 격자 slot 키 자리에 서 있는 조각의 id를 찾는다.
    // 입력: slotKey - BuildGrid.SlotKey로 만든 격자 slot 키.
    // 출력: 그 자리의 조각 id. 비어 있으면 0.
    public uint IdAtSlotKey(uint slotKey) => _bySlot.TryGetValue(slotKey, out uint id) ? id : 0;

    // 기능: 한 플레이어가 세운 조각 수를 센다.
    // 입력: owner - 플레이어의 Entity id.
    // 출력: 서 있는 조각 수. 없으면 0.
    public int OwnerCount(ushort owner) => _ownerCounts.TryGetValue(owner, out int n) ? n : 0;

    // 기능: 빈 격자 slot에 새 조각을 넣는다(배치 검사와 한도는 호출자가 마쳤다). id는 새로 발급되며 재사용하지 않는다.
    // 입력: shape - 모양, material - 재료, owner - 세운 플레이어의 Entity id, createdTick - 놓인 Tick, grounded - 땅에 닿는지.
    // 출력: 새 조각 id. 상한에 차 있거나 자리가 이미 차 있으면 0.
    public uint Add(in BuildPieceShape shape, BuildMaterialType material, ushort owner, uint createdTick, bool grounded)
    {
        uint key = BuildGrid.SlotKey(shape);
        if (Grid.Count >= Capacity || _bySlot.ContainsKey(key)) return 0;
        uint id = _nextId++;
        if (!Grid.TryAdd(id, shape, out int slot)) return 0;
        if (slot >= _pieces.Length) Array.Resize(ref _pieces, Math.Min(Capacity, Math.Max(slot + 1, _pieces.Length * 2)));
        _pieces[slot] = new BuildPiece
        {
            Id = id, Shape = shape, Material = material, Owner = owner, CreatedTick = createdTick, Grounded = grounded,
        };
        _bySlot.Add(key, id);
        _ownerCounts[owner] = OwnerCount(owner) + 1;
        return id;
    }

    // 기능: 조각을 저장소·격자·소유자 수에서 뺀다.
    // 입력: id - 뺄 조각 id.
    // 출력: 뺐으면 true, 없는 id면 false.
    public bool Remove(uint id)
    {
        int slot = Grid.SlotOf(id);
        if (slot < 0) return false;
        ref BuildPiece piece = ref _pieces[slot];
        _bySlot.Remove(BuildGrid.SlotKey(piece.Shape));
        int owned = OwnerCount(piece.Owner) - 1;
        if (owned > 0) _ownerCounts[piece.Owner] = owned;
        else _ownerCounts.Remove(piece.Owner);
        Grid.Remove(id);
        piece = default;
        return true;
    }

    // 기능: 조각의 모양만 바꾼다(Phase 13.5 D6 편집). id·slot·칸·소유자·재료·CreatedTick·Damage는 그대로라 slot으로 묶인
    //   BuildSupport·BuildReplication 배열이 어긋나지 않는다. _bySlot 키도 그대로다(편집은 슬롯 키를 바꾸지 않는다).
    // 입력: slot - 조각의 저장 위치, shape - 새 모양(같은 슬롯 키, BuildEdit.TryApply의 결과).
    // 출력: 바꿨으면 true. 빈 slot이거나 슬롯 키가 다르면 false(아무것도 바뀌지 않는다).
    public bool SetShape(int slot, in BuildPieceShape shape)
    {
        ref BuildPiece piece = ref _pieces[slot];
        if (piece.Id == 0 || !Grid.SetShape(piece.Id, shape)) return false;
        piece.Shape = shape;
        return true;
    }

    // 기능: 모든 조각을 없앤다(라운드 Reset·경기 시작). id는 이어서 커진다.
    // 입력: 없음.
    // 출력: 반환값 없음. 저장소·격자·색인이 비워진다.
    public void Clear()
    {
        Grid.Clear();
        _bySlot.Clear();
        _ownerCounts.Clear();
        Array.Clear(_pieces);
    }

    // 기능: Tick now 기준 건설 진행도를 구한다(D10: 경과 Tick / 재료의 건설 Tick).
    // 입력: piece - 조각, now - 현재 Tick.
    // 출력: 0..1 진행도(건설 Tick이 지났으면 1).
    public float Progress(in BuildPiece piece, uint now)
    {
        uint ticks = _catalog.Material(piece.Material).ConstructionTicks;
        uint age = now > piece.CreatedTick ? now - piece.CreatedTick : 0;
        return age >= ticks ? 1f : age / (float)ticks;
    }

    // 기능: Tick now 기준 조각의 체력을 구한다(D10: 초기 + (최대 - 초기) x 진행도 - 누적 피해, 요청서 §59-§62).
    // 입력: piece - 조각, now - 현재 Tick.
    // 출력: 현재 체력(피해가 크면 0 이하일 수 있다).
    public int Health(in BuildPiece piece, uint now)
    {
        BuildMaterialConfig m = _catalog.Material(piece.Material);
        float grown = m.InitialHealth + (m.MaxHealth - m.InitialHealth) * Progress(piece, now);
        return (int)MathF.Floor(grown) - piece.Damage;
    }
}
