#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System;
using System.Globalization;
using System.Text;
using LiteNetLib;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.UI
{
    // Phase 11 D6: what ended the last connection, as GameClient reports it.
    public struct DisconnectSummary
    {
        public bool StartFailed;          // Connect failed before a connection existed (no socket, bad address)
        public DisconnectReason Reason;   // LiteNetLib's reason
        public DisconnectCode Code;       // the server's code, with RemoteConnectionClose
        public RejectReason Reject;       // the server's reason, with ConnectionRejected
        public JoinResult Join;           // the server's answer to the last join (MatchFull: it closes a second later)
    }

    // Phase 11 D11: every UI string that depends on a value, in Korean. Pure functions (no UnityEngine), tested by the
    // server test project through a source link. The reason texts are constants, so asking every frame allocates nothing;
    // the others build a string, so callers call them only when a shown value changed (a whole second, a new answer).
    public static class UiText
    {
        public const string ZoneName = "자기장";
        // Phase 12 D10, D14: a fall's name in the kill feed and the result, and the movement hints.
        public const string FallName = "낙하";
        public const string HintJump = "[Space] 뛰어내리기";
        public const string HintGlide = "[Space] 글라이더 펼치기";
        public const string HintDoorOpen = "[E] 문 열기";
        public const string HintDoorClose = "[E] 문 닫기";
        // Phase 13 D16: the build mode keys.
        public const string BuildKeys = "[Z] 벽  [X] 바닥  [V] 경사로  [B] 지붕  [T] 재료  [R] 회전  [H] 편집  [Q] 나가기";
        // Phase 13.5 D10: edit mode's keys, and why an edit could not start or be confirmed.
        public const string EditKeys = "[클릭] 칸 선택  [H] 확정  [우클릭] 원래대로  [Esc] 취소";
        public const string EditInvalid = "이 모양으로는 편집할 수 없습니다";
        public const string Connecting = "접속하는 중...";
        public const string StatsLoading = "불러오는 중...";
        public const string StatsNoAnswer = "응답 없음";
        public const string NameRule = "이름은 1-32바이트이고 제어 문자를 쓸 수 없습니다 (한글은 한 글자에 3바이트).";
        public const string PortRule = "포트는 1-65535 사이의 숫자여야 합니다.";
        public const string HostRule = "주소를 입력하세요.";

        // ---- Disconnects (D6) ----

        public static string Disconnect(in DisconnectSummary s)
        {
            if (s.StartFailed) return "접속을 시작할 수 없습니다. 주소와 포트를 확인하세요.";
            // A full match is the server's close without a code that follows its MatchFull answer. Any other end (a
            // timeout, a code) says what it is, even though the join answer of that connection was MatchFull.
            if (s.Join == JoinResult.MatchFull && s.Reason == DisconnectReason.RemoteConnectionClose && s.Code == DisconnectCode.None)
                return "경기가 가득 찼습니다.";
            switch (s.Reason)
            {
                case DisconnectReason.ConnectionRejected: return Reject(s.Reject);
                case DisconnectReason.RemoteConnectionClose: return Code(s.Code);
                default: return Reason(s.Reason);
            }
        }

        public static string Reject(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.VersionMismatch: return "서버와 게임 버전이 다릅니다.";
                case RejectReason.ServerFull: return "서버가 가득 찼습니다.";
                case RejectReason.BadRequest: return "서버가 접속 요청을 받지 않았습니다.";
                default: return "서버가 접속을 거절했습니다.";
            }
        }

        public static string Code(DisconnectCode code)
        {
            switch (code)
            {
                case DisconnectCode.ServerShutdown: return "서버가 종료되었습니다.";
                case DisconnectCode.Kicked: return "잘못된 패킷이 많아 연결이 끊겼습니다.";
                case DisconnectCode.JoinTimeout: return "경기 참가가 늦어 연결이 끊겼습니다.";
                case DisconnectCode.InputTimeout: return "입력이 오래 없어 연결이 끊겼습니다.";
                case DisconnectCode.ServerError: return "서버 오류로 경기가 초기화되었습니다.";
                case DisconnectCode.Congested: return "연결이 너무 느려 끊겼습니다.";
                default: return "서버가 연결을 끊었습니다.";
            }
        }

        public static string Reason(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed: return "서버에 연결할 수 없습니다.";
                case DisconnectReason.Timeout: return "서버의 응답이 끊겼습니다.";
                case DisconnectReason.HostUnreachable: return "서버에 닿을 수 없습니다.";
                case DisconnectReason.NetworkUnreachable: return "네트워크에 연결되어 있지 않습니다.";
                case DisconnectReason.RemoteConnectionClose: return "서버가 연결을 끊었습니다.";
                case DisconnectReason.DisconnectPeerCalled: return "연결을 끊었습니다.";
                case DisconnectReason.ConnectionRejected: return "서버가 접속을 거절했습니다.";
                case DisconnectReason.InvalidProtocol: return "서버와 통신 형식이 맞지 않습니다.";
                case DisconnectReason.UnknownHost: return "서버 주소를 찾을 수 없습니다.";
                case DisconnectReason.Reconnect: return "같은 주소에서 다시 연결되어 끊겼습니다.";
                case DisconnectReason.PeerToPeerConnection: return "다른 연결로 바뀌어 끊겼습니다.";
                case DisconnectReason.PeerNotFound: return "서버가 이 연결을 찾지 못했습니다.";
                default: return "연결이 끊겼습니다.";
            }
        }

        // "재접속 중 (1/3) - 2초 뒤 다시 시도", or "... - 연결하는 중" while an attempt runs (secondsLeft <= 0).
        // Asked again with the same numbers, it returns the same string (a one-slot cache), so a caller that asks every
        // frame allocates only when a number changes.
        public static string Reconnecting(int attempt, int maxAttempts, int secondsLeft)
        {
            if (secondsLeft < 0) secondsLeft = 0;
            Cached last = _reconnecting;
            if (last != null && last.A == attempt && last.B == maxAttempts && last.C == secondsLeft) return last.Text;
            string head = "재접속 중 (" + Int(attempt) + "/" + Int(maxAttempts) + ") - ";
            string text = secondsLeft > 0 ? head + Int(secondsLeft) + "초 뒤 다시 시도" : head + "연결하는 중";
            _reconnecting = new Cached(attempt, maxAttempts, secondsLeft, text);
            return text;
        }

        // ---- Result (D7) ----

        public static string ResultTitle(bool won) => won ? "승리!" : "탈락";

        public static string Placement(int placement, int participants) => "순위 " + Int(placement) + " / " + Int(participants) + "명";

        public static string Kills(int kills) => "처치 " + Int(kills);

        // winnerName null = no winner among the connected players (WinnerId 0).
        public static string Winner(string winnerName) => winnerName == null ? "승자 없음" : "승자: " + winnerName;

        // Who ended this player's match: a player, the zone, or nobody yet (won, or still alive).
        public static string KilledBy(bool died, bool byZone, string killerName) => KilledBy(died, byZone, DeathCause.Zone, killerName);

        // Phase 12 D10: noKiller (KillerId 0) is the zone or a fall, as the cause says.
        public static string KilledBy(bool died, bool noKiller, DeathCause cause, string killerName)
        {
            if (!died) return string.Empty;
            if (noKiller) return "탈락 원인: " + CauseName(cause);
            return "나를 처치한 플레이어: " + killerName;
        }

        public static string CauseName(DeathCause cause) => cause == DeathCause.Fall ? FallName : ZoneName;

        // One-slot cache like Reconnecting.
        public static string NextRound(int secondsLeft)
        {
            if (secondsLeft <= 0) return "다음 판을 준비하는 중";
            Cached last = _nextRound;
            if (last != null && last.A == secondsLeft) return last.Text;
            string text = "다음 판까지 " + Int(secondsLeft) + "초";
            _nextRound = new Cached(secondsLeft, 0, 0, text);
            return text;
        }

        // The one-slot caches: the numbers a string was built from, and the string. Immutable and swapped as one reference,
        // so a reader never pairs one call's numbers with another call's text.
        private sealed class Cached
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;
            public readonly string Text;

            public Cached(int a, int b, int c, string text)
            {
                A = a;
                B = b;
                C = c;
                Text = text;
            }
        }

        private static Cached _reconnecting;
        private static Cached _nextRound;

        // ---- Names (D9, D10) ----

        // A name from PlayerSpawned, or "플레이어 3" when that player's spawn is not known (it already left).
        public static string NameOr(string name, ushort entityId) => name ?? "플레이어 " + Int(entityId);

        // "가해자 ▸ 피해자"; killer null = the zone (KillerId 0).
        public static string KillLine(string killer, string victim) => KillLine(killer, victim, DeathCause.Zone);

        // Phase 12 D10: killer null = the zone or a fall ("낙하 ▸ 피해자").
        public static string KillLine(string killer, string victim, DeathCause cause) => (killer ?? CauseName(cause)) + " ▸ " + victim;

        // ---- Squads (Phase 14 D5, D6, D8-D10, D14) ----

        // Constant hints and labels: asking every frame allocates nothing.
        public const string HintRevive = "[E] 길게 눌러 소생";
        public const string HintReboot = "[E] 길게 눌러 재투입";
        public const string HintRebootCooling = "재투입 스테이션 대기 중";
        public const string ChannelReviving = "소생 중";
        public const string ChannelRevived = "소생 받는 중";
        public const string ChannelRebooting = "재투입 중";
        public const string RebootCardName = "재투입 카드";
        public const string Teammate = "팀원";

        // 기능: Kill Feed의 기절 줄을 만든다("가해자 ▸ 피해자 기절").
        // 입력: attacker - 기절시킨 사람의 이름(null이면 원인 이름), victim - 기절한 사람의 이름, cause - 공격자가 없을 때의 원인.
        // 출력: 기절 줄 문자열(기절 사건마다 한 번 만든다).
        public static string DownedLine(string attacker, string victim, DeathCause cause) =>
            (attacker ?? CauseName(cause)) + " ▸ " + victim + " 기절";

        // 기능: 분대 HUD 한 줄의 상태 문구를 고른다.
        // 입력: state - TeamState의 구성원 상태, flags - 탈락한 구성원의 카드 위치.
        // 출력: 상수 문자열. 살아 있으면 빈 문자열.
        public static string MemberStatus(TeamMemberState state, TeamMemberFlags flags)
        {
            switch (state)
            {
                case TeamMemberState.Downed: return "기절";
                case TeamMemberState.Rebooting: return "재투입 중";
                case TeamMemberState.Eliminated:
                    if ((flags & TeamMemberFlags.CardHeld) != 0) return "탈락 · 카드 보유";
                    if ((flags & TeamMemberFlags.CardDropped) != 0) return "탈락 · 카드 떨어짐";
                    return "탈락";
                default: return string.Empty;
            }
        }

        // 기능: 분대 HUD 한 줄을 만든다("alice (나)  기절").
        // 입력: name - 구성원 이름(null이면 "플레이어 n"), entityId - 구성원 id, status - MemberStatus 문구, self - 나인지.
        // 출력: 줄 문자열(값이 바뀔 때만 부른다).
        public static string SquadRow(string name, ushort entityId, string status, bool self)
        {
            string head = self ? NameOr(name, entityId) + " (나)" : NameOr(name, entityId);
            return string.IsNullOrEmpty(status) ? head : head + "  " + status;
        }

        // 기능: 소지한 재투입 카드 수 줄을 만든다.
        // 입력: cards - 소지 카드 수.
        // 출력: "재투입 카드 2장", 0이면 빈 문자열.
        public static string CardsLine(int cards) => cards <= 0 ? string.Empty : RebootCardName + " " + Int(cards) + "장";

        // 기능: 기절 막대의 문구를 만든다.
        // 입력: seconds - 출혈로 탈락할 때까지 남은 초(표시용 추정).
        // 출력: "기절 · 출혈 12초".
        public static string Bleeding(int seconds) => "기절 · 출혈 " + Int(seconds < 0 ? 0 : seconds) + "초";

        // 기능: 결과 화면의 순위 줄(Phase 14 D6: 분대면 팀 단위).
        // 입력: placement - 팀 배치, participants - 팀 수(Solo는 사람 수), teams - 분대 경기인지.
        // 출력: "순위 2 / 4팀" 또는 Solo의 "순위 2 / 8명".
        public static string Placement(int placement, int participants, bool teams) =>
            teams ? "순위 " + Int(placement) + " / " + Int(participants) + "팀" : Placement(placement, participants);

        // 기능: 결과 화면의 우승 줄(Phase 14 D6: 분대면 우승 팀의 가장 작은 id 구성원 이름).
        // 입력: winnerName - 우승자(분대는 우승 팀 대표) 이름, null이면 없음, teams - 분대 경기인지.
        // 출력: "우승 팀: alice" 또는 Solo의 Winner 문구.
        public static string Winner(string winnerName, bool teams) =>
            teams && winnerName != null ? "우승 팀: " + winnerName : Winner(winnerName);

        // ---- Statistics (D8) ----

        // Ok has no status line; the summary and the rows say it all.
        public static string StatsStatusText(StatsStatus status)
        {
            switch (status)
            {
                case StatsStatus.Ok: return string.Empty;
                case StatsStatus.NoRecord: return "아직 기록이 없습니다.";
                case StatsStatus.Unavailable: return "기록을 볼 수 없음";
                case StatsStatus.Busy: return "서버가 바쁩니다. 잠시 뒤 다시 열어 주세요.";
                default: return StatsNoAnswer;
            }
        }

        public static string StatsSummaryText(in StatsSummary s) =>
            "경기 " + Int(s.Matches) + "   승리 " + Int(s.Wins) + "   처치 " + Int(s.Kills) + "   사망 " + Int(s.Deaths) +
            "\n피해 " + Int(s.Damage) + "   생존 시간 " + Duration(s.SurvivalSeconds);

        // One line per match, newest first: "10-01 21:00   3위 / 16명   처치 2   피해 340   생존 4:05".
        // utcOffset: the viewer's time zone (UiRoot passes the local one).
        public static string StatsRowsText(StatsRow[] rows, TimeSpan utcOffset)
        {
            if (rows == null || rows.Length == 0) return string.Empty;
            var text = new StringBuilder(rows.Length * 64);
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) text.Append('\n');
                text.Append(StatsRowText(rows[i], utcOffset));
            }
            return text.ToString();
        }

        public static string StatsRowText(in StatsRow row, TimeSpan utcOffset)
        {
            string when = DateTimeOffset.FromUnixTimeSeconds(row.EndedUnixSeconds).ToOffset(utcOffset)
                .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            string place = row.Placement == 0 ? "순위 없음" : Int(row.Placement) + "위 / " + Int(row.Players) + "명";
            return when + "   " + place + "   처치 " + Int(row.Kills) + "   피해 " + Int(row.Damage) +
                   "   생존 " + Duration(row.SurvivalMs / 1000);
        }

        // "4:05" below an hour, "1:02:03" from an hour on.
        public static string Duration(long totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            long hours = totalSeconds / 3600;
            long minutes = totalSeconds / 60 % 60;
            long seconds = totalSeconds % 60;
            return hours > 0
                ? Int(hours) + ":" + Two(minutes) + ":" + Two(seconds)
                : Int(minutes) + ":" + Two(seconds);
        }

        // ---- Building (Phase 13 D16) ----

        // Constant names: no allocation.
        public static string PieceName(BuildPieceType piece)
        {
            switch (piece)
            {
                case BuildPieceType.Wall: return "벽";
                case BuildPieceType.Floor: return "바닥";
                case BuildPieceType.Ramp: return "경사로";
                default: return "지붕";
            }
        }

        public static string MaterialName(BuildMaterialType material)
        {
            switch (material)
            {
                case BuildMaterialType.Wood: return "나무";
                case BuildMaterialType.Stone: return "돌";
                default: return "금속";
            }
        }

        public static string ToolName(ToolKind tool)
        {
            switch (tool)
            {
                case ToolKind.Harvest: return "채집";
                case ToolKind.Build: return "건축";
                default: return "무기";
            }
        }

        // The resources line (the server's numbers minus pending placements). Rebuilt only when a number changes.
        public static string ResourcesLine(int wood, int stone, int metal) =>
            "나무 " + Int(wood) + "   돌 " + Int(stone) + "   금속 " + Int(metal);

        // Build mode: the chosen piece and material.
        public static string BuildModeLine(BuildPieceType piece, BuildMaterialType material) =>
            "건축: " + PieceName(piece) + " · " + MaterialName(material);

        // 기능: 편집 모드의 안내 줄(Phase 13.5 D10).
        // 입력: piece - 편집하는 조각 종류.
        // 출력: "편집: 벽" 같은 문자열(상수 이어 붙이기: 편집을 시작할 때만 만든다).
        public static string EditModeLine(BuildPieceType piece) => "편집: " + PieceName(piece);

        // 기능: 거절된 건설·편집 요청(BuildResult)의 안내 문구를 낸다(Phase 13.5 D9: NotOwner, NotFound 추가).
        // 입력: code - 결과 코드.
        // 출력: 상수 문자열. Ok면 null, 모르는 코드면 일반 문구.
        public static string BuildRefusal(BuildResultCode code)
        {
            switch (code)
            {
                case BuildResultCode.Ok: return null;
                case BuildResultCode.NoResource: return "자원이 부족합니다";
                case BuildResultCode.OutOfRange: return "너무 멉니다";
                case BuildResultCode.Blocked: return "막혀 있습니다";
                case BuildResultCode.Unsupported: return "받쳐 줄 구조물이 없습니다";
                case BuildResultCode.Occupied: return "이미 지어져 있습니다";
                case BuildResultCode.RateLimited: return "너무 빠릅니다";
                case BuildResultCode.BudgetFull: return "더 지을 수 없습니다";
                case BuildResultCode.NotOwner: return "내 구조물만 편집할 수 있습니다";
                case BuildResultCode.NotFound: return "구조물이 없습니다";
                default: return "지을 수 없습니다";
            }
        }

        // F1: the tool, the selection, the stored and shown pieces, refusals and requests per second.
        public static string BuildDebugLine(ToolKind tool, BuildPieceType piece, BuildMaterialType material, int stored, int drawn,
            int ignored, int sent, int refused, BuildResultCode lastRefusal, int requestsPerSecond) =>
            "도구 " + ToolName(tool) + "   " + PieceName(piece) + "/" + MaterialName(material) + "   구조물 " + Int(stored) + " (표시 " + Int(drawn) +
            ", 무시 " + Int(ignored) + ")   요청 " + Int(sent) + " (" + Int(requestsPerSecond) + "/s)   거절 " + Int(refused) + " " + lastRefusal;

        // ---- Debug line (D4, F1) ----

        public static string DebugLine(string state, int roundTripMs, ushort entityId) =>
            "상태 " + state + "   RTT " + Int(roundTripMs) + " ms   Entity " + Int(entityId) + "   (F1)";

        // Phase 12 D14: the movement line. Speeds and the correction in tenths and hundredths, so the caller can rebuild it
        // only when a shown digit changes (DebugOverlay).
        public static string MovementLine(string mode, int horizontalTenths, int verticalTenths, int energy, int correctionCentimetres) =>
            "이동 " + mode + "   수평 " + Tenths(horizontalTenths) + " m/s   수직 " + Tenths(verticalTenths) + " m/s   기력 " + Int(energy) +
            "   보정 " + Hundredths(correctionCentimetres) + " m";

        // D14: the drop transport's route (there is no map UI).
        public static string RouteLine(float startX, float startZ, float endX, float endZ) =>
            "수송기 (" + Int(RoundToInt(startX)) + ", " + Int(RoundToInt(startZ)) + ") → (" + Int(RoundToInt(endX)) + ", " + Int(RoundToInt(endZ)) + ")";

        private static int RoundToInt(float value) => (int)Math.Round(value);

        private static string Tenths(int tenths)
        {
            string sign = tenths < 0 ? "-" : string.Empty;
            int a = Math.Abs(tenths);
            return sign + Int(a / 10) + "." + Int(a % 10);
        }

        private static string Hundredths(int hundredths)
        {
            string sign = hundredths < 0 ? "-" : string.Empty;
            int a = Math.Abs(hundredths);
            return sign + Int(a / 100) + "." + Two(a % 100);
        }

        // ---- Title input (D4) ----

        // The name typed on the title screen: trimmed, then the server's connect-request rule
        // (ProtocolConstants.IsValidPlayerName: 1-32 bytes of valid UTF-8, no control characters).
        public static bool TryNormalizeName(string input, out string name)
        {
            name = input == null ? string.Empty : input.Trim();
            if (ProtocolConstants.IsValidPlayerName(name)) return true;
            name = null;
            return false;
        }

        public static bool TryNormalizeHost(string input, out string host)
        {
            host = input == null ? string.Empty : input.Trim();
            if (host.Length > 0 && host.Length <= 253) return true;
            host = null;
            return false;
        }

        public static bool TryParsePort(string input, out int port)
        {
            if (int.TryParse(input == null ? null : input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port >= 1 && port <= 65535)
                return true;
            port = 0;
            return false;
        }

        private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Two(long value) => value < 10 ? "0" + Int(value) : Int(value);
    }
}
