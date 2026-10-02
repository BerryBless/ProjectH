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

    public bool TryGetSlot(uint id, out int slot)
    {
        slot = Grid.SlotOf(id);
        return slot >= 0;
    }

    public ref BuildPiece At(int slot) => ref _pieces[slot];

    public bool Contains(uint id) => Grid.Contains(id);

    // The id in that grid slot, or 0.
    public uint IdAtSlotKey(uint slotKey) => _bySlot.TryGetValue(slotKey, out uint id) ? id : 0;

    public int OwnerCount(ushort owner) => _ownerCounts.TryGetValue(owner, out int n) ? n : 0;

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

    // Every piece gone (a round reset, a match start). Ids keep growing.
    public void Clear()
    {
        Grid.Clear();
        _bySlot.Clear();
        _ownerCounts.Clear();
        Array.Clear(_pieces);
    }

    // D10: construction progress 0..1 at tick now: (now - created) / the material's construction ticks.
    public float Progress(in BuildPiece piece, uint now)
    {
        uint ticks = _catalog.Material(piece.Material).ConstructionTicks;
        uint age = now > piece.CreatedTick ? now - piece.CreatedTick : 0;
        return age >= ticks ? 1f : age / (float)ticks;
    }

    // D10: health at tick now: initial + (max - initial) x progress - damage taken (request §59-§62).
    public int Health(in BuildPiece piece, uint now)
    {
        BuildMaterialConfig m = _catalog.Material(piece.Material);
        float grown = m.InitialHealth + (m.MaxHealth - m.InitialHealth) * Progress(piece, now);
        return (int)MathF.Floor(grown) - piece.Damage;
    }
}
