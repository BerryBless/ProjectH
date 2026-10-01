using System;
using System.Collections.Generic;
using LiteNetLib;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D4, D6-D9, D11 (spec §2): the client's Korean UI strings. Compiled here through a source link like UiFlow.
public class UiTextTests
{
    private static bool HasHangul(string text)
    {
        foreach (char c in text)
        {
            if (c >= '가' && c <= '힣') return true;
        }
        return false;
    }

    // Every reason the client can see has its own Korean sentence, and asking again returns the same constant (no
    // allocation when the screen asks every frame).
    [Fact]
    public void EveryLiteNetLibReason_HasAKoreanText()
    {
        var seen = new HashSet<string>();
        foreach (DisconnectReason reason in Enum.GetValues<DisconnectReason>())
        {
            string text = UiText.Reason(reason);
            Assert.True(HasHangul(text), reason.ToString());
            Assert.Same(text, UiText.Reason(reason));
            seen.Add(text);
        }
        Assert.Equal(Enum.GetValues<DisconnectReason>().Length, seen.Count);
    }

    [Fact]
    public void EveryDisconnectCode_AndRejectReason_HasAKoreanText()
    {
        var codes = new HashSet<string>();
        foreach (DisconnectCode code in Enum.GetValues<DisconnectCode>())
        {
            Assert.True(HasHangul(UiText.Code(code)), code.ToString());
            Assert.Same(UiText.Code(code), UiText.Code(code));
            codes.Add(UiText.Code(code));
        }
        Assert.Equal(Enum.GetValues<DisconnectCode>().Length, codes.Count);

        var rejects = new HashSet<string>();
        foreach (RejectReason reason in Enum.GetValues<RejectReason>())
        {
            Assert.True(HasHangul(UiText.Reject(reason)), reason.ToString());
            Assert.Same(UiText.Reject(reason), UiText.Reject(reason));
            rejects.Add(UiText.Reject(reason));
        }
        Assert.Equal(Enum.GetValues<RejectReason>().Length, rejects.Count);
    }

    [Fact]
    public void Disconnect_PicksTheMostSpecificReason()
    {
        Assert.Equal("서버가 종료되었습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Code = DisconnectCode.ServerShutdown,
        }));
        Assert.Equal("서버와 게임 버전이 다릅니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.ConnectionRejected, Reject = RejectReason.VersionMismatch,
        }));
        // A full match: the server answered MatchFull, then closed without a code.
        Assert.Equal("경기가 가득 찼습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Join = JoinResult.MatchFull,
        }));
        Assert.Equal("서버의 응답이 끊겼습니다.", UiText.Disconnect(new DisconnectSummary { Reason = DisconnectReason.Timeout }));
        // A plain remote close without a code and without a refused join.
        Assert.Equal("서버가 연결을 끊었습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Code = DisconnectCode.None,
        }));
        Assert.Equal("접속을 시작할 수 없습니다. 주소와 포트를 확인하세요.", UiText.Disconnect(new DisconnectSummary { StartFailed = true }));
    }

    // MatchFull is the answer only when the connection then ended the way a refused join ends: the server's close
    // without a code. A timeout or a coded close after it says what really happened.
    [Fact]
    public void MatchFull_WinsOnlyWithARemoteCloseWithoutACode()
    {
        Assert.Equal("경기가 가득 찼습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Code = DisconnectCode.None, Join = JoinResult.MatchFull,
        }));
        Assert.Equal("서버의 응답이 끊겼습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.Timeout, Join = JoinResult.MatchFull,
        }));
        Assert.Equal("서버가 종료되었습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Code = DisconnectCode.ServerShutdown, Join = JoinResult.MatchFull,
        }));
    }

    [Fact]
    public void Reconnecting_ShowsTheAttemptAndTheSeconds()
    {
        Assert.Equal("재접속 중 (1/3) - 2초 뒤 다시 시도", UiText.Reconnecting(1, 3, 2));
        Assert.Equal("재접속 중 (2/3) - 연결하는 중", UiText.Reconnecting(2, 3, 0));
    }

    // One-slot caches: the same numbers again return the very same string (no allocation per frame); new numbers build
    // a new one.
    [Fact]
    public void Reconnecting_AndNextRound_ReuseTheStringForTheSameNumbers()
    {
        string first = UiText.Reconnecting(1, 3, 5);
        Assert.Same(first, UiText.Reconnecting(1, 3, 5));
        string next = UiText.Reconnecting(1, 3, 4);
        Assert.Equal("재접속 중 (1/3) - 4초 뒤 다시 시도", next);
        Assert.Same(next, UiText.Reconnecting(1, 3, 4));

        string seven = UiText.NextRound(7);
        Assert.Same(seven, UiText.NextRound(7));
        Assert.Equal("다음 판까지 6초", UiText.NextRound(6));
        Assert.Same(UiText.NextRound(0), UiText.NextRound(-1));
    }

    [Fact]
    public void Result_Texts()
    {
        Assert.Equal("승리!", UiText.ResultTitle(true));
        Assert.Equal("탈락", UiText.ResultTitle(false));
        Assert.Equal("순위 3 / 12명", UiText.Placement(3, 12));
        Assert.Equal("처치 2", UiText.Kills(2));
        Assert.Equal("승자: alice", UiText.Winner("alice"));
        Assert.Equal("승자 없음", UiText.Winner(null!));
        Assert.Equal("나를 처치한 플레이어: bob", UiText.KilledBy(true, false, "bob"));
        Assert.Equal("탈락 원인: 자기장", UiText.KilledBy(true, true, null!));
        Assert.Equal(string.Empty, UiText.KilledBy(false, false, null!));
        Assert.Equal("다음 판까지 7초", UiText.NextRound(7));
        Assert.Equal("다음 판을 준비하는 중", UiText.NextRound(0));
    }

    [Fact]
    public void Names_AndTheKillLine()
    {
        Assert.Equal("alice", UiText.NameOr("alice", 3));
        Assert.Equal("플레이어 3", UiText.NameOr(null!, 3));
        Assert.Equal("alice ▸ bob", UiText.KillLine("alice", "bob"));
        Assert.Equal("자기장 ▸ bob", UiText.KillLine(null!, "bob"));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(245, "4:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "0:00")]
    public void Duration_Format(long seconds, string expected)
    {
        Assert.Equal(expected, UiText.Duration(seconds));
    }

    [Fact]
    public void Stats_Texts()
    {
        Assert.Equal(string.Empty, UiText.StatsStatusText(StatsStatus.Ok));
        Assert.Equal("아직 기록이 없습니다.", UiText.StatsStatusText(StatsStatus.NoRecord));
        Assert.Equal("기록을 볼 수 없음", UiText.StatsStatusText(StatsStatus.Unavailable));
        Assert.True(HasHangul(UiText.StatsStatusText(StatsStatus.Busy)));
        Assert.Equal("응답 없음", UiText.StatsNoAnswer);

        Assert.Equal("경기 12   승리 3   처치 40   사망 9\n피해 12345   생존 시간 1:02:03", UiText.StatsSummaryText(new StatsSummary
        {
            Matches = 12, Wins = 3, Kills = 40, Deaths = 9, Damage = 12345, SurvivalSeconds = 3723,
        }));

        // 2026-10-01 12:00:00 UTC, shown in UTC+9.
        var row = new StatsRow
        {
            EndedUnixSeconds = (uint)new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Round = 7, Players = 16, Placement = 3, Kills = 2, Damage = 340, SurvivalMs = 245_900,
        };
        Assert.Equal("10-01 21:00   3위 / 16명   처치 2   피해 340   생존 4:05", UiText.StatsRowText(row, TimeSpan.FromHours(9)));
        row.Placement = 0;
        Assert.StartsWith("10-01 12:00   순위 없음", UiText.StatsRowText(row, TimeSpan.Zero));

        Assert.Equal(string.Empty, UiText.StatsRowsText(Array.Empty<StatsRow>(), TimeSpan.Zero));
        string two = UiText.StatsRowsText(new[] { row, row }, TimeSpan.Zero);
        Assert.Equal(2, two.Split('\n').Length);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  bob \t", "bob")]
    [InlineData("플레이어", "플레이어")]
    [InlineData("abcdefghijabcdefghijabcdefghij12", "abcdefghijabcdefghijabcdefghij12")]   // 32 bytes
    [InlineData("가나다라마바사아자차", "가나다라마바사아자차")]                                  // 10 x 3 = 30 bytes
    public void Name_IsTrimmed_AndAccepted(string input, string expected)
    {
        Assert.True(UiText.TryNormalizeName(input, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData(null)]
    [InlineData("abcdefghijabcdefghijabcdefghij123")]   // 33 bytes
    [InlineData("가나다라마바사아자차카")]                 // 11 x 3 = 33 bytes
    [InlineData("a\u0000b")]                              // the server's rule: no control characters (C0, DEL, C1)
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    [InlineData("a�")]                               // what invalid UTF-8 decodes to
    [InlineData("a\uD800")]                               // a lone surrogate has no UTF-8 form
    public void Name_EmptyTooLongOrNotTheServersRule_IsRefused(string? input)
    {
        Assert.False(UiText.TryNormalizeName(input!, out string name));
        Assert.Null(name);
    }

    [Theory]
    [InlineData("7777", true, 7777)]
    [InlineData(" 1 ", true, 1)]
    [InlineData("65535", true, 65535)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("77a", false, 0)]
    [InlineData("", false, 0)]
    public void Port_Rule(string input, bool ok, int expected)
    {
        Assert.Equal(ok, UiText.TryParsePort(input, out int port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void Host_IsTrimmed_AndRequired()
    {
        Assert.True(UiText.TryNormalizeHost(" 127.0.0.1 ", out string host));
        Assert.Equal("127.0.0.1", host);
        Assert.False(UiText.TryNormalizeHost("  ", out _));
    }

    [Fact]
    public void DebugLine()
    {
        Assert.Equal("상태 Joined   RTT 23 ms   Entity 5   (F1)", UiText.DebugLine("Joined", 23, 5));
    }
}
