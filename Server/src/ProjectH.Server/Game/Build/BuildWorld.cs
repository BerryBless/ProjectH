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
    public bool Grounded;         // D12: touches the terrain or a map box (fixed: neither ever changes)
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

    // 기능: 건설 조각 저장소를 Match 최대 조각 수에 맞춰 만든다.
    // 입력: catalog - 재료 설정과 Match당 최대 조각 수를 담은 건설 카탈로그.
    // 출력: 조각이 하나도 없고 다음 id가 1인 BuildWorld.
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

    // 기능: 조각 id로 저장 슬롯 번호를 찾는다.
    // 입력: id - 찾을 조각 id.
    // 출력: 조각이 있으면 true와 저장 슬롯 번호, 없으면 false.
    public bool TryGetSlot(uint id, out int slot)
    {
        slot = Grid.SlotOf(id);
        return slot >= 0;
    }

    // 기능: 슬롯에 저장된 조각을 참조로 돌려준다.
    // 입력: slot - 조각 슬롯 번호(TryGetSlot으로 얻은 값).
    // 출력: 그 슬롯의 BuildPiece 참조. 수정하면 저장된 조각이 바로 바뀐다.
    public ref BuildPiece At(int slot) => ref _pieces[slot];

    // 기능: 조각 id가 격자에 놓여 있는지 확인한다.
    // 입력: id - 조각 ID.
    // 출력: 놓여 있으면 true, 없으면 false.
    public bool Contains(uint id) => Grid.Contains(id);

    // 기능: 격자 슬롯 키에 놓인 조각 id를 찾는다.
    // 입력: slotKey - BuildGrid.SlotKey로 만든 격자 슬롯 키.
    // 출력: 그 슬롯의 조각 id, 비어 있으면 0.
    public uint IdAtSlotKey(uint slotKey) => _bySlot.TryGetValue(slotKey, out uint id) ? id : 0;

    // 기능: 한 건설자가 현재 가진 조각 수를 센다.
    // 입력: owner - 건설자의 entity id.
    // 출력: 그 건설자의 조각 수, 없으면 0.
    public int OwnerCount(ushort owner) => _ownerCounts.TryGetValue(owner, out int n) ? n : 0;

    // 기능: 빈 격자 슬롯에 새 조각을 추가하고 슬롯·소유자 색인을 갱신한다.
    // 입력: shape - 조각의 격자 위치와 종류, material - 재료, owner - 건설자 entity id, createdTick - 건설 시작 Tick, grounded - 지형이나 맵 상자에 닿는지 여부(D12).
    // 출력: 새 조각 id. 저장소가 가득 찼거나 슬롯이 이미 차 있으면 0.
    // A new piece in an empty slot (the caller checked it and the budgets). Returns its id, 0 when full.
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

    // 기능: 조각 하나를 저장소와 슬롯·소유자·격자 색인에서 지운다.
    // 입력: id - 지울 조각 id.
    // 출력: 지웠으면 true, 그 id의 조각이 없으면 false.
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

    // 기능: 모든 조각과 색인을 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 조각이 하나도 없는 상태가 되며 다음 id는 그대로 이어진다.
    // Every piece gone (a round reset, a match start). Ids keep growing.
    public void Clear()
    {
        Grid.Clear();
        _bySlot.Clear();
        _ownerCounts.Clear();
        Array.Clear(_pieces);
    }

    // 기능: 조각의 건설 진행도를 계산한다.
    // 입력: piece - 대상 조각, now - 현재 서버 Tick.
    // 출력: 0~1 사이의 건설 진행도. 재료의 건설 Tick이 지났으면 1.
    // D10: construction progress 0..1 at tick now: (now - created) / the material's construction ticks.
    public float Progress(in BuildPiece piece, uint now)
    {
        uint ticks = _catalog.Material(piece.Material).ConstructionTicks;
        uint age = now > piece.CreatedTick ? now - piece.CreatedTick : 0;
        return age >= ticks ? 1f : age / (float)ticks;
    }

    // 기능: 건설 진행도와 받은 피해로 조각의 현재 체력을 계산한다.
    // 입력: piece - 대상 조각, now - 현재 서버 Tick.
    // 출력: 현재 체력. 0 이하면 파괴되어야 하는 상태.
    // D10: health at tick now: initial + (max - initial) x progress - damage taken (request §59-§62).
    public int Health(in BuildPiece piece, uint now)
    {
        BuildMaterialConfig m = _catalog.Material(piece.Material);
        float grown = m.InitialHealth + (m.MaxHealth - m.InitialHealth) * Progress(piece, now);
        return (int)MathF.Floor(grown) - piece.Damage;
    }
}
