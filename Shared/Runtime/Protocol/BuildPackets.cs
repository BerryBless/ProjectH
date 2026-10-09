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
        NotOwner = 10,      // Phase 13.5 D9: an edit of a piece the player may not edit (BuildRules.CanEdit)
        NotFound = 11,      // Phase 13.5 D9: an edit of a piece that is not there (never was, or already destroyed)
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

        // 기능: 건설 요청을 BuildRequest 패킷(id, 순번, 조각, 재료, 격자 x·y·z, 회전)으로 쓴다.
        // 입력: writer - 쓸 곳, r - 요청.
        // 출력: 반환값 없음. writer에 Size(9)바이트가 쓰인다.
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

        // 기능: 건설 요청을 읽는다(PacketId 다음부터). 길이만 확인하고 값 검증은 서버(BuildRules)가 한다.
        // 입력: reader - 남은 바이트가 정확히 8이어야 한다, r - 결과.
        // 출력: 길이가 맞으면 true와 요청, 아니면 false.
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

        // 기능: 순번 a가 b보다 뒤인지 u16 순환 산술로 판단한다(서버의 중복 요청 걸러내기).
        // 입력: a - 비교할 순번, b - 기준 순번.
        // 출력: a가 b보다 1..32767만큼 앞서면 true, 같거나 뒤처지면 false.
        // Sequence a is after b (u16 serial number arithmetic: up to 32767 ahead).
        public static bool IsNewer(ushort a, ushort b)
        {
            ushort ahead = (ushort)(a - b);
            return ahead != 0 && ahead < 0x8000;
        }
    }

    // Phase 13.5 D4: C->S on the build channel, ReliableOrdered: edit one piece (also Reset: Edit 0). Sequence is the same
    // per-connection counter BuildRequest uses (one duplicate check, one queue on the server). State: the piece's new edit
    // state in bits 0-11 and its rotation in bits 12-13 (a ramp's new direction; every other piece its current rotation),
    // bits 14-15 zero (BuildEdit.PackState). Values are checked by the server (Match.TryEdit), not here: the reader only
    // checks the length. Layout: [PacketId 1][Sequence 2][PieceId 4][State 2] = 9 bytes.
    public struct BuildEditRequest
    {
        public const int Size = 9;   // with the packet id

        public ushort Sequence;
        public uint PieceId;
        public ushort State;

        // 기능: 편집 요청을 쓴다.
        // 입력: writer - 쓸 곳, r - 요청.
        // 출력: 반환값 없음. writer에 9바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in BuildEditRequest r)
        {
            writer.WriteByte((byte)PacketId.BuildEditRequest);
            writer.WriteUInt16(r.Sequence);
            writer.WriteUInt32(r.PieceId);
            writer.WriteUInt16(r.State);
        }

        // 기능: 편집 요청을 읽는다(PacketId 다음부터). 길이만 확인한다.
        // 입력: reader - 남은 바이트가 정확히 8이어야 한다, r - 결과.
        // 출력: 길이가 맞으면 true와 요청, 아니면 false.
        public static bool TryRead(ref PacketReader reader, out BuildEditRequest r)
        {
            r = default;
            if (reader.Remaining != Size - 1) return false;
            reader.TryReadUInt16(out r.Sequence);
            reader.TryReadUInt32(out r.PieceId);
            reader.TryReadUInt16(out r.State);
            return true;
        }
    }

    // Phase 13 D13: S->C, to the requester: the request's sequence, the result, the new piece's id (0 when refused).
    // Phase 13.5 D5: an edit's result carries the edited piece's id when Ok (0 when refused), so (Ok) == (PieceId != 0)
    // holds for both; the client tells them apart by the sequence it is waiting on.
    public struct BuildResult
    {
        public const int Size = 8;   // with the packet id

        public ushort Sequence;
        public BuildResultCode Code;
        public uint PieceId;

        // 기능: 건설·편집 요청의 결과를 BuildResult 패킷(id, 순번, 결과 코드, 조각 id)으로 쓴다.
        // 입력: writer - 쓸 곳, r - 결과(거절이면 PieceId 0).
        // 출력: 반환값 없음. writer에 Size(8)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in BuildResult r)
        {
            writer.WriteByte((byte)PacketId.BuildResult);
            writer.WriteUInt16(r.Sequence);
            writer.WriteByte((byte)r.Code);
            writer.WriteUInt32(r.PieceId);
        }

        // 기능: BuildResult 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문, r - 결과.
        // 출력: 성공하면 true와 결과. 짧거나, 코드가 NotFound보다 크거나, (Ok) == (PieceId != 0)이 깨지면 false.
        public static bool TryRead(ref PacketReader reader, out BuildResult r)
        {
            r = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out r.Sequence);
            reader.TryReadByte(out byte code);
            reader.TryReadUInt32(out r.PieceId);
            r.Code = (BuildResultCode)code;
            return code <= (byte)BuildResultCode.NotFound && (r.Code == BuildResultCode.Ok) == (r.PieceId != 0);
        }
    }

    // Phase 13 D13: one piece as clients learn of it. Placed: Id 4 + grid 4 (x 5, z 5, level 4, rotation 2, type 2,
    // material 2 bits, Phase 13.5 D8: edit state 12 bits) + owner 2 + created tick 4 = 14 bytes. A sync record adds the
    // damage taken so far (16 bytes). The edit state rides in the grid word, so a join, a resume or entering a cell gets
    // the piece's latest edit without a size change.
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

        // 기능: Placed 기록(id, 격자 단어(x·z·y·회전·종류·재료·편집 상태), 주인, 생성 Tick)을 쓴다.
        // 입력: writer - 쓸 곳, p - 조각 기록(Damage는 쓰지 않는다).
        // 출력: 반환값 없음. writer에 PlacedSize(14)바이트가 쓰인다.
        public static void WritePlaced(ref PacketWriter writer, in BuildPieceRecord p)
        {
            writer.WriteUInt32(p.Id);
            uint grid = (uint)(p.Shape.X & 31) | ((uint)(p.Shape.Z & 31) << 5) | ((uint)(p.Shape.Y & 15) << 10) |
                        ((uint)(p.Shape.Rotation & 3) << 14) | ((uint)((byte)p.Shape.Type & 3) << 16) | ((uint)((byte)p.Material & 3) << 18) |
                        ((uint)(p.Shape.Edit & BuildEdit.Mask) << 20);
            writer.WriteUInt32(grid);
            writer.WriteUInt16(p.Owner);
            writer.WriteUInt32(p.CreatedTick);
        }

        // 기능: Sync 기록(Placed 기록 뒤에 지금까지 받은 피해)을 쓴다.
        // 입력: writer - 쓸 곳, p - 조각 기록.
        // 출력: 반환값 없음. writer에 SyncSize(16)바이트가 쓰인다.
        public static void WriteSync(ref PacketWriter writer, in BuildPieceRecord p)
        {
            WritePlaced(ref writer, p);
            writer.WriteUInt16(p.Damage);
        }

        // 기능: Placed 기록을 읽는다.
        // 입력: reader - 기록이 시작되는 곳, p - 결과.
        // 출력: 읽고 검증되면 true와 기록. id 0, Metal 위 재료, 정규형이 아닌 모양(BuildGrid.TryNormalize), 그 종류에서
        //   허용되지 않는 편집 상태(BuildEdit.IsValid, Phase 13.5 D8)면 false.
        public static bool TryReadPlaced(ref PacketReader reader, out BuildPieceRecord p)
        {
            p = default;
            if (reader.Remaining < PlacedSize) return false;
            reader.TryReadUInt32(out p.Id);
            reader.TryReadUInt32(out uint grid);
            reader.TryReadUInt16(out p.Owner);
            reader.TryReadUInt32(out p.CreatedTick);
            if (p.Id == 0) return false;
            int material = (int)(grid >> 18) & 3;
            if (material > (int)BuildMaterialType.Metal) return false;
            p.Material = (BuildMaterialType)material;
            var type = (BuildPieceType)((grid >> 16) & 3);
            int x = (int)(grid & 31);
            int z = (int)((grid >> 5) & 31);
            int y = (int)((grid >> 10) & 15);
            int rotation = (int)((grid >> 14) & 3);
            if (!BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape)) return false;
            if (shape.X != x || shape.Z != z || shape.Rotation != rotation) return false;
            int edit = (int)(grid >> 20);
            if (!BuildEdit.IsValid(type, edit)) return false;
            p.Shape = shape.WithEdit(edit, rotation);
            return true;
        }

        // 기능: Sync 기록(Placed 기록 + 피해)을 읽는다.
        // 입력: reader - 기록이 시작되는 곳, p - 결과.
        // 출력: Placed 기록이 검증되고 피해 2바이트가 있으면 true와 기록, 아니면 false.
        public static bool TryReadSync(ref PacketReader reader, out BuildPieceRecord p)
        {
            if (!TryReadPlaced(ref reader, out p)) return false;
            return reader.TryReadUInt16(out p.Damage);
        }
    }

    // Phase 18 D6: why a piece left the world (the reason byte of a BuildEvents Destroyed record). Values are the wire format.
    public enum BuildDestroyReason : byte
    {
        Destroyed = 0,   // its health reached 0 (a shot, an explosion, a harvest tool, QA damageBuild)
        Collapsed = 1,   // nothing held it up any more after a destroy or an edit in the same tick
    }

    // Phase 13 D13: S->C on the build channel, ReliableOrdered: one tick's building events for one client, in this order:
    // pieces placed, Phase 13.5 D8: pieces edited (the piece's state after its last edit of the tick, once per piece),
    // health changes (the damage a piece has taken so far, once per piece per tick), pieces destroyed.
    // Layout: [PacketId 1][Version 4][Placed 1][Edited 1][Health 1][Destroyed 1] (9 bytes), then the records: Placed 14,
    // Edited 6 (id + state: BuildEdit.PackState), Health 6 (id + damage), Destroyed 5 (id + Phase 18 D6 reason:
    // BuildDestroyReason). At most MaxPacketSize bytes;
    // a tick with more is split into several packets that keep that order. Version is the match's building event count
    // after the tick (debugging and order checks, D14).
    public static class BuildEventsPacket
    {
        public const int HeaderSize = 9;
        public const int EditedSize = 6;
        public const int HealthSize = 6;
        public const int DestroyedSize = 5;   // Phase 18 D6: id 4 + reason 1
        public const int VersionOffset = 1;
        public const int CountsOffset = 5;

        // 기능: 헤더를 쓴다. 개수는 0으로 두고, 기록을 다 쓴 뒤 Patch가 실제 값을 쓴다.
        // 입력: writer - 쓸 곳, version - 이 Tick 뒤 경기의 건설 이벤트 수.
        // 출력: 반환값 없음. writer에 9바이트가 쓰인다.
        public static void WriteHeader(ref PacketWriter writer, uint version)
        {
            writer.WriteByte((byte)PacketId.BuildEvents);
            writer.WriteUInt32(version);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        // 기능: 헤더의 기록 개수를 쓴다.
        // 입력: packet - PacketId 바이트부터 시작하는 패킷, placed·edited·health·destroyed - 종류별 기록 수(각 255 이하).
        // 출력: 반환값 없음. 헤더의 개수 4바이트가 바뀐다.
        public static void Patch(Span<byte> packet, int placed, int edited, int health, int destroyed)
        {
            packet[CountsOffset] = (byte)placed;
            packet[CountsOffset + 1] = (byte)edited;
            packet[CountsOffset + 2] = (byte)health;
            packet[CountsOffset + 3] = (byte)destroyed;
        }

        // 기능: Edited 기록 하나를 쓴다.
        // 입력: writer - 쓸 곳, id - 조각 id, state - 편집 뒤 상태(BuildEdit.PackState).
        // 출력: 반환값 없음. writer에 6바이트가 쓰인다.
        public static void WriteEdited(ref PacketWriter writer, uint id, ushort state)
        {
            writer.WriteUInt32(id);
            writer.WriteUInt16(state);
        }

        // 기능: Health 기록 하나를 쓴다.
        // 입력: writer - 쓸 곳, id - 조각 id, damage - 지금까지 받은 피해.
        // 출력: 반환값 없음. writer에 6바이트가 쓰인다.
        public static void WriteHealth(ref PacketWriter writer, uint id, ushort damage)
        {
            writer.WriteUInt32(id);
            writer.WriteUInt16(damage);
        }

        // 기능: Destroyed 기록 하나를 쓴다(Phase 18 D6: 이유 포함).
        // 입력: writer - 쓸 곳, id - 조각 id, reason - 피해로 부서졌는지(Destroyed), 지지를 잃고 무너졌는지(Collapsed).
        // 출력: 반환값 없음. writer에 5바이트가 쓰인다.
        public static void WriteDestroyed(ref PacketWriter writer, uint id, BuildDestroyReason reason)
        {
            writer.WriteUInt32(id);
            writer.WriteByte((byte)reason);
        }

        // 기능: 헤더를 읽는다(PacketId 다음부터).
        // 입력: reader - 패킷, version·placed·edited·health·destroyed - 결과.
        // 출력: 헤더가 있고 남은 길이가 기록 개수와 정확히 맞으면 true.
        public static bool TryReadHeader(ref PacketReader reader, out uint version, out int placed, out int edited, out int health, out int destroyed)
        {
            version = 0;
            placed = edited = health = destroyed = 0;
            if (reader.Remaining < HeaderSize - 1) return false;
            reader.TryReadUInt32(out version);
            reader.TryReadByte(out byte p);
            reader.TryReadByte(out byte e);
            reader.TryReadByte(out byte h);
            reader.TryReadByte(out byte d);
            placed = p;
            edited = e;
            health = h;
            destroyed = d;
            return reader.Remaining == placed * BuildPieceRecord.PlacedSize + edited * EditedSize + health * HealthSize + destroyed * DestroyedSize;
        }

        // 기능: Edited 기록 하나를 읽는다. 종류를 모르므로 편집 상태의 유효성은 받는 쪽이 BuildEdit.TryApply로 확인한다.
        // 입력: reader - 기록이 시작되는 곳, id·state - 결과.
        // 출력: 읽혔고 id가 0이 아니며 state의 14–15비트가 0이면 true.
        public static bool TryReadEdited(ref PacketReader reader, out uint id, out ushort state)
        {
            state = 0;
            return reader.TryReadUInt32(out id) && reader.TryReadUInt16(out state) && id != 0 && (state >> (BuildEdit.RotationShift + 2)) == 0;
        }

        // 기능: Health 기록 하나를 읽는다.
        // 입력: reader - 기록이 시작되는 곳, id·damage - 결과.
        // 출력: 6바이트가 읽혔고 id가 0이 아니면 true.
        public static bool TryReadHealth(ref PacketReader reader, out uint id, out ushort damage)
        {
            damage = 0;
            return reader.TryReadUInt32(out id) && reader.TryReadUInt16(out damage) && id != 0;
        }

        // 기능: Destroyed 기록 하나를 읽는다(Phase 18 D6).
        // 입력: reader - 기록이 시작되는 곳, id·reason - 결과.
        // 출력: 5바이트가 읽혔고 id가 0이 아니며 이유가 Collapsed(1) 이하면 true.
        public static bool TryReadDestroyed(ref PacketReader reader, out uint id, out BuildDestroyReason reason)
        {
            reason = BuildDestroyReason.Destroyed;
            if (!reader.TryReadUInt32(out id) || !reader.TryReadByte(out byte raw)) return false;
            if (id == 0 || raw > (byte)BuildDestroyReason.Collapsed) return false;
            reason = (BuildDestroyReason)raw;
            return true;
        }

        // 기능: Destroyed 기록 하나를 읽고 이유는 버린다(이유가 필요 없는 받는 쪽용). 기록 전체(5바이트)를 읽는다.
        // 입력: reader - 기록이 시작되는 곳, id - 결과.
        // 출력: TryReadDestroyed(reader, id, reason)와 같다.
        public static bool TryReadDestroyed(ref PacketReader reader, out uint id) => TryReadDestroyed(ref reader, out id, out _);
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

        // 기능: BuildSync 헤더(id, 버전, 플래그, 기록 수)를 쓴다. 뒤에 count개의 Sync 기록이 따른다.
        // 입력: writer - 쓸 곳, version - 경기의 건설 이벤트 수, reset - 받는 쪽이 가진 조각을 먼저 다 버릴지, count - 기록 수(MaxRecords 이하).
        // 출력: 반환값 없음. writer에 HeaderSize(7)바이트가 쓰인다.
        public static void WriteHeader(ref PacketWriter writer, uint version, bool reset, int count)
        {
            writer.WriteByte((byte)PacketId.BuildSync);
            writer.WriteUInt32(version);
            writer.WriteByte(reset ? ResetFlag : (byte)0);
            writer.WriteByte((byte)count);
        }

        // 기능: BuildSync 헤더를 읽는다(PacketId 다음부터).
        // 입력: reader - 패킷, version·reset·count - 결과.
        // 출력: 헤더가 있고, 모르는 플래그가 없고, 기록 수가 MaxRecords 이하이며, 남은 길이가 정확히 count x 16이면 true.
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

        // 기능: 관심 영역 칸 비트 마스크를 BuildInterest 패킷(id + 하위·상위 uint32)으로 쓴다.
        // 입력: writer - 쓸 곳, cells - 비트 = cellX + 8 x cellZ.
        // 출력: 반환값 없음. writer에 Size(9)바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, ulong cells)
        {
            writer.WriteByte((byte)PacketId.BuildInterest);
            writer.WriteUInt32((uint)cells);
            writer.WriteUInt32((uint)(cells >> 32));
        }

        // 기능: BuildInterest 본문(PacketId 뒤)의 칸 마스크를 읽는다.
        // 입력: reader - 본문, cells - 결과.
        // 출력: 8바이트가 있으면 true와 마스크(값 검사는 없다), 짧으면 false.
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

    // Phase 13 D4: S->C, ReliableOrdered on the building channel (1), once per join or resume as that channel's first packet,
    // right before the reset sync (final review A3). 49 bytes.
    public static class BuildCatalogPacket
    {
        public const int Size = 49;

        // 기능: 건설 카탈로그를 BuildCatalog 패킷(재료별 비용·체력·초기 체력·건설 Tick, 그 뒤 공통 값)으로 쓴다.
        // 입력: writer - 쓸 곳, c - 서버의 건설 수치(배열은 재료 3종).
        // 출력: 반환값 없음. writer에 Size(49)바이트가 쓰인다.
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

        // 기능: BuildCatalog 본문을 읽는다(조인 때 한 번, 카탈로그를 할당한다). 리뷰 수정 D3(SEC-26): 관심 영역 칸 크기는 서버가 쓸 수 있는
        //   20·40·80·160 m(ProtocolLimits.InterestCellSizes)만 받는다.
        // 입력: reader - 본문(PacketId 뒤).
        // 출력: 맞으면 true와 카탈로그, 아니면 false.
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
            if (!Positive(data.BuildRange) || !Positive(data.ViewAngleDegrees) || !Positive(data.HarvestRange) ||
                !ProtocolLimits.IsInterestCellSize(data.InterestCellSize))
                return false;
            if (data.MaxResource == 0 || data.HarvestCooldownTicks == 0 || data.MinBuildIntervalTicks == 0) return false;
            c = data;
            return true;
        }

        // 기능: 카탈로그의 거리·각도 값이 양수이고 터무니없이 크지 않은지 본다.
        // 입력: value - 검사할 값.
        // 출력: 0 초과 1000 미만이면 true(NaN·Infinity는 false).
        private static bool Positive(float value) => value > 0f && value < 1000f;   // also refuses NaN
    }
}
