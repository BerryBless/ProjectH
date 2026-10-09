using System;

namespace ProjectH.Shared.Protocol
{
    // Phase 11 D8: what the server answers to a StatsRequest. The values are the wire format.
    public enum StatsStatus : byte
    {
        Ok = 0,           // the summary and up to StatsResponse.MaxRows recent matches follow
        NoRecord = 1,     // the database has no saved match for this DevPlayerId
        Unavailable = 2,  // persistence is off, or the database failed or did not answer in time
        Busy = 3,         // the server's query queue was full; asking again later may work
    }

    // Totals over every saved match of one DevPlayerId. uint32 on the wire: the server clamps larger values (the
    // database keeps damage and survival time as 64-bit sums), and survival is whole seconds.
    public struct StatsSummary
    {
        public uint Matches;
        public uint Wins;
        public uint Kills;
        public uint Deaths;
        public uint Damage;
        public uint SurvivalSeconds;
    }

    // One recent match of the player (D8).
    public struct StatsRow
    {
        public uint EndedUnixSeconds;   // UTC
        public uint Round;
        public byte Players;
        public byte Placement;          // 1 = won, 0 = unranked
        public ushort Kills;
        public uint Damage;
        public uint SurvivalMs;
    }

    // C->S, ReliableOrdered, no body: "send me my statistics". The server answers each accepted request with one
    // StatsResponse and silently drops requests above its per-connection rate (D8).
    public static class StatsRequest
    {
        // 기능: 본문 없는 StatsRequest 패킷(id 1바이트)을 쓴다.
        // 입력: writer - 쓸 Writer.
        // 출력: 반환값 없음. writer에 1바이트가 쓰인다.
        public static void Write(ref PacketWriter writer)
        {
            writer.WriteByte((byte)PacketId.StatsRequest);
        }
    }

    // S->C, ReliableOrdered, the answer to one StatsRequest (D8). Layout after the packet id: status 1, summary 24
    // (six uint32), row count 1, then count x 20-byte rows, newest first. A status other than Ok always carries a zero
    // summary and no rows. A class because it is built off the game loop and handed over whole; the reader allocates
    // it and its rows once per answer (a person pressed a button), never on the per-tick path.
    public sealed class StatsResponse
    {
        public const int MaxRows = 10;
        public const int SummarySize = 24;
        public const int RowSize = 20;
        // 227 bytes with the packet id: far below one datagram.
        public const int MaxSize = 1 + 1 + SummarySize + 1 + MaxRows * RowSize;

        public StatsStatus Status;
        public StatsSummary Summary;
        public StatsRow[] Rows = Array.Empty<StatsRow>();   // newest first; only the first MaxRows are written

        // 기능: 요약과 행이 없는 응답(NoRecord·Unavailable·Busy 같은 오류 답)을 만든다.
        // 입력: status - 응답 상태.
        // 출력: Status만 설정되고 Summary는 0, Rows는 빈 배열인 새 StatsResponse.
        public static StatsResponse Of(StatsStatus status) => new StatsResponse { Status = status };

        // 기능: 통계 응답을 StatsResponse 패킷(id, 상태, 24바이트 요약, 행 수, 20바이트 행들)으로 쓴다.
        // 입력: writer - 쓸 Writer, r - 보낼 응답(Rows는 newest first, 처음 MaxRows개만 쓴다).
        // 출력: 반환값 없음. Status가 Ok가 아니면 요약은 0, 행 수는 0으로 쓰인다.
        public static void Write(ref PacketWriter writer, StatsResponse r)
        {
            bool ok = r.Status == StatsStatus.Ok;
            StatsRow[] rows = r.Rows ?? Array.Empty<StatsRow>();
            int count = ok ? Math.Min(rows.Length, MaxRows) : 0;
            StatsSummary s = ok ? r.Summary : default;

            writer.WriteByte((byte)PacketId.StatsResponse);
            writer.WriteByte((byte)r.Status);
            writer.WriteUInt32(s.Matches);
            writer.WriteUInt32(s.Wins);
            writer.WriteUInt32(s.Kills);
            writer.WriteUInt32(s.Deaths);
            writer.WriteUInt32(s.Damage);
            writer.WriteUInt32(s.SurvivalSeconds);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                StatsRow row = rows[i];
                writer.WriteUInt32(row.EndedUnixSeconds);
                writer.WriteUInt32(row.Round);
                writer.WriteByte(row.Players);
                writer.WriteByte(row.Placement);
                writer.WriteUInt16(row.Kills);
                writer.WriteUInt32(row.Damage);
                writer.WriteUInt32(row.SurvivalMs);
            }
        }

        // 기능: 패킷 id 다음부터 StatsResponse 본문을 읽어 새 응답 객체를 만든다(응답마다 한 번 할당).
        // 입력: reader - 패킷 id를 지난 Reader, r - 읽은 응답을 받을 변수.
        // 출력: 상태가 아는 값이고 행 수가 MaxRows 이하(Ok가 아니면 0)이며 바이트가 충분하면 true와 응답, 아니면 false와 null.
        public static bool TryRead(ref PacketReader reader, out StatsResponse r)
        {
            r = null;
            if (reader.Remaining < 1 + SummarySize + 1) return false;
            reader.TryReadByte(out byte status);
            if (status > (byte)StatsStatus.Busy) return false;
            var s = new StatsSummary();
            reader.TryReadUInt32(out s.Matches);
            reader.TryReadUInt32(out s.Wins);
            reader.TryReadUInt32(out s.Kills);
            reader.TryReadUInt32(out s.Deaths);
            reader.TryReadUInt32(out s.Damage);
            reader.TryReadUInt32(out s.SurvivalSeconds);
            reader.TryReadByte(out byte count);
            if (count > MaxRows) return false;
            if (status != (byte)StatsStatus.Ok && count != 0) return false;
            if (reader.Remaining < count * RowSize) return false;

            StatsRow[] rows = count == 0 ? Array.Empty<StatsRow>() : new StatsRow[count];
            for (int i = 0; i < count; i++)
            {
                reader.TryReadUInt32(out rows[i].EndedUnixSeconds);
                reader.TryReadUInt32(out rows[i].Round);
                reader.TryReadByte(out rows[i].Players);
                reader.TryReadByte(out rows[i].Placement);
                reader.TryReadUInt16(out rows[i].Kills);
                reader.TryReadUInt32(out rows[i].Damage);
                reader.TryReadUInt32(out rows[i].SurvivalMs);
            }
            r = new StatsResponse { Status = (StatsStatus)status, Summary = s, Rows = rows };
            return true;
        }
    }
}
