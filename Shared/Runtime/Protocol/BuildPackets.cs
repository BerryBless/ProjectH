using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 13 D9, request §108: why a build request was refused. Values are the wire format.
    public enum BuildResultCode : byte
    {
        Ok = 0,
        NoResource = 1,
        OutOfRange = 2,     // too far, or not where the player looks
        Blocked = 3,        // through a wall, in the terrain, in a map box, through a player
        Unsupported = 4,
        Occupied = 5,
        RateLimited = 6,    // more requests waiting than the server keeps
        InvalidState = 7,   // dead, spectating, the match finished, not in build mode, in the air
        InvalidRequest = 8, // no such piece, material, rotation or cell
        BudgetFull = 9,     // the match's or the player's piece limit
    }

    // Phase 13 D8: C->S on the build channel, ReliableOrdered: place one piece. Grid coordinates only, never a world
    // position. Sequence grows by one per request (u16, wrapping); the server drops any it already processed. Values are
    // checked by the server (BuildRules), not here: the reader only checks the length.
    public struct BuildRequest
    {
        public const int Size = 9;   // with the packet id

        public ushort Sequence;
        public byte Piece;      // BuildPieceType
        public byte Material;   // BuildMaterialType
        public byte X;
        public byte Y;
        public byte Z;
        public byte Rotation;   // a wall's edge (0-3) or a ramp's direction (0-3)

        public static void Write(ref PacketWriter writer, in BuildRequest r)
        {
            writer.WriteByte((byte)PacketId.BuildRequest);
            writer.WriteUInt16(r.Sequence);
            writer.WriteByte(r.Piece);
            writer.WriteByte(r.Material);
            writer.WriteByte(r.X);
            writer.WriteByte(r.Y);
            writer.WriteByte(r.Z);
            writer.WriteByte(r.Rotation);
        }

        public static bool TryRead(ref PacketReader reader, out BuildRequest r)
        {
            r = default;
            if (reader.Remaining != Size - 1) return false;
            reader.TryReadUInt16(out r.Sequence);
            reader.TryReadByte(out r.Piece);
            reader.TryReadByte(out r.Material);
            reader.TryReadByte(out r.X);
            reader.TryReadByte(out r.Y);
            reader.TryReadByte(out r.Z);
            reader.TryReadByte(out r.Rotation);
            return true;
        }

        // Sequence a is after b (u16 serial number arithmetic: up to 32767 ahead).
        public static bool IsNewer(ushort a, ushort b)
        {
            ushort ahead = (ushort)(a - b);
            return ahead != 0 && ahead < 0x8000;
        }
    }

    // Phase 13 D13: S->C, to the requester: the request's sequence, the result, the new piece's id (0 when refused).
    public struct BuildResult
    {
        public const int Size = 8;   // with the packet id

        public ushort Sequence;
        public BuildResultCode Code;
        public uint PieceId;

        public static void Write(ref PacketWriter writer, in BuildResult r)
        {
            writer.WriteByte((byte)PacketId.BuildResult);
            writer.WriteUInt16(r.Sequence);
            writer.WriteByte((byte)r.Code);
            writer.WriteUInt32(r.PieceId);
        }

        public static bool TryRead(ref PacketReader reader, out BuildResult r)
        {
            r = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out r.Sequence);
            reader.TryReadByte(out byte code);
            reader.TryReadUInt32(out r.PieceId);
            r.Code = (BuildResultCode)code;
            return code <= (byte)BuildResultCode.BudgetFull && (r.Code == BuildResultCode.Ok) == (r.PieceId != 0);
        }
    }

    // Phase 13 D13: one piece as clients learn of it. Placed: Id 4 + grid 4 (x 5, z 5, level 4, rotation 2, type 2,
    // material 2 bits) + owner 2 + created tick 4 = 14 bytes. A sync record adds the damage taken so far (16 bytes).
    public struct BuildPieceRecord
    {
        public const int PlacedSize = 14;
        public const int SyncSize = 16;

        public uint Id;
        public BuildPieceShape Shape;
        public BuildMaterialType Material;
        public ushort Owner;          // the builder's entity id (Phase 13.5 edit permission; shown nowhere yet)
        public uint CreatedTick;      // construction progress = (now - CreatedTick) / the material's construction ticks
        public ushort Damage;         // sync only (events carry it as Health records)

        public static void WritePlaced(ref PacketWriter writer, in BuildPieceRecord p)
        {
            writer.WriteUInt32(p.Id);
            uint grid = (uint)(p.Shape.X & 31) | ((uint)(p.Shape.Z & 31) << 5) | ((uint)(p.Shape.Y & 15) << 10) |
                        ((uint)(p.Shape.Rotation & 3) << 14) | ((uint)((byte)p.Shape.Type & 3) << 16) | ((uint)((byte)p.Material & 3) << 18);
            writer.WriteUInt32(grid);
            writer.WriteUInt16(p.Owner);
            writer.WriteUInt32(p.CreatedTick);
        }

        public static void WriteSync(ref PacketWriter writer, in BuildPieceRecord p)
        {
            WritePlaced(ref writer, p);
            writer.WriteUInt16(p.Damage);
        }

        // Refuses id 0, unused bits, a material above Metal and a shape that is not canonical (BuildGrid.TryNormalize).
        public static bool TryReadPlaced(ref PacketReader reader, out BuildPieceRecord p)
        {
            p = default;
            if (reader.Remaining < PlacedSize) return false;
            reader.TryReadUInt32(out p.Id);
            reader.TryReadUInt32(out uint grid);
            reader.TryReadUInt16(out p.Owner);
            reader.TryReadUInt32(out p.CreatedTick);
            if (p.Id == 0 || grid >> 20 != 0) return false;
            int material = (int)(grid >> 18) & 3;
            if (material > (int)BuildMaterialType.Metal) return false;
            p.Material = (BuildMaterialType)material;
            var type = (BuildPieceType)((grid >> 16) & 3);
            int x = (int)(grid & 31);
            int z = (int)((grid >> 5) & 31);
            int y = (int)((grid >> 10) & 15);
            int rotation = (int)((grid >> 14) & 3);
            if (!BuildGrid.TryNormalize(type, x, y, z, rotation, out p.Shape)) return false;
            return p.Shape.X == x && p.Shape.Z == z && p.Shape.Rotation == rotation;
        }

        public static bool TryReadSync(ref PacketReader reader, out BuildPieceRecord p)
        {
            if (!TryReadPlaced(ref reader, out p)) return false;
            return reader.TryReadUInt16(out p.Damage);
        }
    }

    // Phase 13 D13: S->C on the build channel, ReliableOrdered: one tick's building events for one client, in this order:
    // pieces placed, health changes (the damage a piece has taken so far, once per piece per tick), pieces destroyed.
    // Layout: [PacketId 1][Version 4][Placed 1][Health 1][Destroyed 1] (8 bytes), then the records: Placed 14, Health 6
    // (id + damage), Destroyed 4 (id). At most MaxPacketSize bytes; a tick with more is split into several packets that
    // keep that order. Version is the match's building event count after the tick (debugging and order checks, D14).
    public static class BuildEventsPacket
    {
        public const int HeaderSize = 8;
        public const int HealthSize = 6;
        public const int DestroyedSize = 4;
        public const int VersionOffset = 1;
        public const int CountsOffset = 5;

        // Placeholder counts; Patch writes the real ones once the records are in.
        public static void WriteHeader(ref PacketWriter writer, uint version)
        {
            writer.WriteByte((byte)PacketId.BuildEvents);
            writer.WriteUInt32(version);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        // packet starts with the PacketId byte.
        public static void Patch(Span<byte> packet, int placed, int health, int destroyed)
        {
            packet[CountsOffset] = (byte)placed;
            packet[CountsOffset + 1] = (byte)health;
            packet[CountsOffset + 2] = (byte)destroyed;
        }

        public static void WriteHealth(ref PacketWriter writer, uint id, ushort damage)
        {
            writer.WriteUInt32(id);
            writer.WriteUInt16(damage);
        }

        public static void WriteDestroyed(ref PacketWriter writer, uint id) => writer.WriteUInt32(id);

        public static bool TryReadHeader(ref PacketReader reader, out uint version, out int placed, out int health, out int destroyed)
        {
            version = 0;
            placed = health = destroyed = 0;
            if (reader.Remaining < HeaderSize - 1) return false;
            reader.TryReadUInt32(out version);
            reader.TryReadByte(out byte p);
            reader.TryReadByte(out byte h);
            reader.TryReadByte(out byte d);
            placed = p;
            health = h;
            destroyed = d;
            return reader.Remaining == placed * BuildPieceRecord.PlacedSize + health * HealthSize + destroyed * DestroyedSize;
        }

        public static bool TryReadHealth(ref PacketReader reader, out uint id, out ushort damage)
        {
            damage = 0;
            return reader.TryReadUInt32(out id) && reader.TryReadUInt16(out damage) && id != 0;
        }

        public static bool TryReadDestroyed(ref PacketReader reader, out uint id) => reader.TryReadUInt32(out id) && id != 0;
    }

    // Phase 13 D14: S->C on the build channel, ReliableOrdered: the current pieces of some interest cells (a join, a
    // resume, cells entered). Reset first: the client drops every piece it has before applying (a join, a resume, a
    // round reset). Layout: [PacketId 1][Version 4][Flags 1][Count 1] then Count x 16-byte records; at most
    // MaxRecords per packet (1191 bytes).
    public static class BuildSyncPacket
    {
        public const int HeaderSize = 7;
        public const byte ResetFlag = 1;
        public const int MaxRecords = (ProtocolConstants.MaxPacketSize - HeaderSize) / BuildPieceRecord.SyncSize;   // 74

        public static void WriteHeader(ref PacketWriter writer, uint version, bool reset, int count)
        {
            writer.WriteByte((byte)PacketId.BuildSync);
            writer.WriteUInt32(version);
            writer.WriteByte(reset ? ResetFlag : (byte)0);
            writer.WriteByte((byte)count);
        }

        public static bool TryReadHeader(ref PacketReader reader, out uint version, out bool reset, out int count)
        {
            version = 0;
            reset = false;
            count = 0;
            if (reader.Remaining < HeaderSize - 1) return false;
            reader.TryReadUInt32(out version);
            reader.TryReadByte(out byte flags);
            reader.TryReadByte(out byte n);
            reset = (flags & ResetFlag) != 0;
            count = n;
            return (flags & ~ResetFlag) == 0 && count <= MaxRecords && reader.Remaining == count * BuildPieceRecord.SyncSize;
        }
    }

    // Phase 13 D14: S->C on the build channel, ReliableOrdered: the interest cells (8 x 8 over the map, bit = cellX +
    // 8 x cellZ) whose pieces this client keeps. Pieces in any other cell are dropped (the server sends nothing for them
    // until a later BuildSync brings them back). Sent when the set changes.
    public static class BuildInterestPacket
    {
        public const int Size = 9;   // with the packet id

        public static void Write(ref PacketWriter writer, ulong cells)
        {
            writer.WriteByte((byte)PacketId.BuildInterest);
            writer.WriteUInt32((uint)cells);
            writer.WriteUInt32((uint)(cells >> 32));
        }

        public static bool TryRead(ref PacketReader reader, out ulong cells)
        {
            cells = 0;
            if (!reader.TryReadUInt32(out uint low) || !reader.TryReadUInt32(out uint high)) return false;
            cells = low | ((ulong)high << 32);
            return true;
        }
    }

    // Phase 13 D4: what a client needs of the server's building numbers (preview, HUD, the harvest swing, construction
    // visuals), once at join. The server alone decides with them.
    public sealed class BuildCatalogData
    {
        public ushort[] ResourceCost = new ushort[3];        // by BuildMaterialType
        public ushort[] MaxHealth = new ushort[3];
        public ushort[] InitialHealth = new ushort[3];
        public ushort[] ConstructionTicks = new ushort[3];
        public ushort MaxResource;
        public float BuildRange;
        public float ViewAngleDegrees;
        public float HarvestRange;
        public ushort HarvestCooldownTicks;
        public ushort MinBuildIntervalTicks;
        public float InterestCellSize;
        public byte InterestRadius;
        public byte InterestKeepMargin;
    }

    // Phase 13 D4: S->C, ReliableOrdered, once after the item catalog. 49 bytes.
    public static class BuildCatalogPacket
    {
        public const int Size = 49;

        public static void Write(ref PacketWriter writer, BuildCatalogData c)
        {
            writer.WriteByte((byte)PacketId.BuildCatalog);
            for (int m = 0; m < BuildMaterials.Count; m++)
            {
                writer.WriteUInt16(c.ResourceCost[m]);
                writer.WriteUInt16(c.MaxHealth[m]);
                writer.WriteUInt16(c.InitialHealth[m]);
                writer.WriteUInt16(c.ConstructionTicks[m]);
            }
            writer.WriteUInt16(c.MaxResource);
            writer.WriteSingle(c.BuildRange);
            writer.WriteSingle(c.ViewAngleDegrees);
            writer.WriteSingle(c.HarvestRange);
            writer.WriteUInt16(c.HarvestCooldownTicks);
            writer.WriteUInt16(c.MinBuildIntervalTicks);
            writer.WriteSingle(c.InterestCellSize);
            writer.WriteByte(c.InterestRadius);
            writer.WriteByte(c.InterestKeepMargin);
        }

        // Allocates the catalog: read once per join.
        public static bool TryRead(ref PacketReader reader, out BuildCatalogData c)
        {
            c = null;
            if (reader.Remaining < Size - 1) return false;
            var data = new BuildCatalogData();
            for (int m = 0; m < BuildMaterials.Count; m++)
            {
                reader.TryReadUInt16(out data.ResourceCost[m]);
                reader.TryReadUInt16(out data.MaxHealth[m]);
                reader.TryReadUInt16(out data.InitialHealth[m]);
                reader.TryReadUInt16(out data.ConstructionTicks[m]);
                if (data.MaxHealth[m] == 0 || data.InitialHealth[m] == 0 || data.InitialHealth[m] > data.MaxHealth[m] || data.ConstructionTicks[m] == 0)
                    return false;
            }
            reader.TryReadUInt16(out data.MaxResource);
            reader.TryReadSingle(out data.BuildRange);
            reader.TryReadSingle(out data.ViewAngleDegrees);
            reader.TryReadSingle(out data.HarvestRange);
            reader.TryReadUInt16(out data.HarvestCooldownTicks);
            reader.TryReadUInt16(out data.MinBuildIntervalTicks);
            reader.TryReadSingle(out data.InterestCellSize);
            reader.TryReadByte(out data.InterestRadius);
            reader.TryReadByte(out data.InterestKeepMargin);
            if (!Positive(data.BuildRange) || !Positive(data.ViewAngleDegrees) || !Positive(data.HarvestRange) || !Positive(data.InterestCellSize))
                return false;
            if (data.MaxResource == 0 || data.HarvestCooldownTicks == 0 || data.MinBuildIntervalTicks == 0) return false;
            c = data;
            return true;
        }

        private static bool Positive(float value) => value > 0f && value < 1000f;   // also refuses NaN
    }
}
