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
        public const string BuildKeys = "[Z] 벽  [X] 바닥  [V] 경사로  [B] 지붕  [T] 재료  [R] 회전  [Q] 나가기";
        public const string Connecting = "접속하는 중...";
        public const string StatsLoading = "불러오는 중...";
        public const string StatsNoAnswer = "응답 없음";
        public const string NameRule = "이름은 1-32바이트이고 제어 문자를 쓸 수 없습니다 (한글은 한 글자에 3바이트).";
        public const string PortRule = "포트는 1-65535 사이의 숫자여야 합니다.";
        public const string HostRule = "주소를 입력하세요.";

        // ---- Disconnects (D6) ----

        // 기능: 마지막 연결 종료 요약을 한국어 사유 문자열로 바꾼다.
        // 입력: s - GameClient가 보고한 연결 종료 요약.
        // 출력: 상수 사유 문자열(할당 없음).
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

        // 기능: 서버의 접속 거절 사유를 문자열로 바꾼다.
        // 입력: reason - 서버의 거절 사유.
        // 출력: 상수 사유 문자열.
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

        // 기능: 서버의 연결 종료 코드를 문자열로 바꾼다.
        // 입력: code - 서버가 보낸 종료 코드.
        // 출력: 상수 사유 문자열.
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

        // 기능: LiteNetLib의 연결 종료 사유를 문자열로 바꾼다.
        // 입력: reason - LiteNetLib 종료 사유.
        // 출력: 상수 사유 문자열.
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

        // 기능: 재접속 진행 줄을 만든다.
        // 입력: attempt - 현재 시도 번호, maxAttempts - 최대 시도 수, secondsLeft - 다음 시도까지 남은 초(0 이하면 연결 중).
        // 출력: 진행 줄 문자열. 직전 호출과 숫자가 같으면 캐시된 같은 문자열.
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

        // 기능: 결과 화면 제목을 고른다.
        // 입력: won - 승리 여부.
        // 출력: 승리면 "승리!", 아니면 "탈락".
        public static string ResultTitle(bool won) => won ? "승리!" : "탈락";

        // 기능: 순위 줄을 만든다.
        // 입력: placement - 순위, participants - 참가자 수.
        // 출력: "순위 N / M명" 문자열.
        public static string Placement(int placement, int participants) => "순위 " + Int(placement) + " / " + Int(participants) + "명";

        // 기능: 처치 수 줄을 만든다.
        // 입력: kills - 처치 수.
        // 출력: "처치 N" 문자열.
        public static string Kills(int kills) => "처치 " + Int(kills);

        // 기능: 승자 줄을 만든다.
        // 입력: winnerName - 승자 이름(null이면 승자 없음).
        // 출력: "승자: 이름" 또는 "승자 없음".
        // winnerName null = no winner among the connected players (WinnerId 0).
        public static string Winner(string winnerName) => winnerName == null ? "승자 없음" : "승자: " + winnerName;

        // 기능: 처치자 없는 사망을 자기장으로 보고 탈락 원인 줄을 만든다.
        // 입력: died - 사망 여부, byZone - 처치자 없이 죽었는지 여부, killerName - 처치한 플레이어 이름.
        // 출력: 사망하지 않았으면 빈 문자열, 아니면 원인 또는 처치자 줄.
        // Who ended this player's match: a player, the zone, or nobody yet (won, or still alive).
        public static string KilledBy(bool died, bool byZone, string killerName) => KilledBy(died, byZone, DeathCause.Zone, killerName);

        // 기능: 탈락 원인 줄을 만든다.
        // 입력: died - 사망 여부, noKiller - 처치자 없이 죽었는지 여부, cause - 처치자 없는 사망의 원인, killerName - 처치한 플레이어 이름.
        // 출력: 사망하지 않았으면 빈 문자열, 처치자가 없으면 원인 줄, 있으면 처치자 줄.
        // Phase 12 D10: noKiller (KillerId 0) is the zone or a fall, as the cause says.
        public static string KilledBy(bool died, bool noKiller, DeathCause cause, string killerName)
        {
            if (!died) return string.Empty;
            if (noKiller) return "탈락 원인: " + CauseName(cause);
            return "나를 처치한 플레이어: " + killerName;
        }

        // 기능: 처치자 없는 사망 원인의 이름을 고른다.
        // 입력: cause - 사망 원인.
        // 출력: 낙하면 FallName, 그 외에는 ZoneName.
        public static string CauseName(DeathCause cause) => cause == DeathCause.Fall ? FallName : ZoneName;

        // 기능: 다음 판까지 남은 시간 줄을 만든다.
        // 입력: secondsLeft - 남은 초.
        // 출력: 0 이하이면 준비 중 문구, 아니면 "다음 판까지 N초". 직전과 같은 초면 캐시된 같은 문자열.
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

            // 기능: 한 칸 캐시 항목을 만든다.
            // 입력: a, b, c - 문자열을 만든 숫자, text - 만든 문자열.
            // 출력: 값이 고정된 Cached 객체.
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

        // 기능: 표시할 플레이어 이름을 고른다.
        // 입력: name - 알려진 이름(null 가능), entityId - 플레이어 Entity ID.
        // 출력: name이 있으면 그대로, 없으면 "플레이어 N".
        // A name from PlayerSpawned, or "플레이어 3" when that player's spawn is not known (it already left).
        public static string NameOr(string name, ushort entityId) => name ?? "플레이어 " + Int(entityId);

        // 기능: 처치자 없는 사망을 자기장으로 보고 킬 피드 줄을 만든다.
        // 입력: killer - 처치자 이름(null이면 자기장), victim - 사망자 이름.
        // 출력: "가해자 ▸ 피해자" 문자열.
        // "가해자 ▸ 피해자"; killer null = the zone (KillerId 0).
        public static string KillLine(string killer, string victim) => KillLine(killer, victim, DeathCause.Zone);

        // 기능: 킬 피드 줄을 만든다.
        // 입력: killer - 처치자 이름(null이면 원인 이름), victim - 사망자 이름, cause - 처치자 없는 사망의 원인.
        // 출력: "가해자 ▸ 피해자" 문자열.
        // Phase 12 D10: killer null = the zone or a fall ("낙하 ▸ 피해자").
        public static string KillLine(string killer, string victim, DeathCause cause) => (killer ?? CauseName(cause)) + " ▸ " + victim;

        // ---- Statistics (D8) ----

        // 기능: 전적 응답 상태를 상태 줄 문자열로 바꾼다.
        // 입력: status - 서버의 전적 응답 상태.
        // 출력: 상수 상태 문자열. Ok면 빈 문자열.
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

        // 기능: 전적 합계 두 줄을 만든다.
        // 입력: s - 서버가 보낸 누적 전적.
        // 출력: 경기·승리·처치·사망과 피해·생존 시간 문자열.
        public static string StatsSummaryText(in StatsSummary s) =>
            "경기 " + Int(s.Matches) + "   승리 " + Int(s.Wins) + "   처치 " + Int(s.Kills) + "   사망 " + Int(s.Deaths) +
            "\n피해 " + Int(s.Damage) + "   생존 시간 " + Duration(s.SurvivalSeconds);

        // 기능: 최근 경기 목록을 경기당 한 줄로 만든다.
        // 입력: rows - 최근 경기 행(null 가능), utcOffset - 시각 표시용 시간대.
        // 출력: 줄바꿈으로 이은 문자열. 행이 없으면 빈 문자열.
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

        // 기능: 최근 경기 한 줄을 만든다.
        // 입력: row - 경기 행, utcOffset - 시각 표시용 시간대.
        // 출력: 종료 시각, 순위, 처치, 피해, 생존 시간 문자열. 순위 0은 "순위 없음".
        public static string StatsRowText(in StatsRow row, TimeSpan utcOffset)
        {
            string when = DateTimeOffset.FromUnixTimeSeconds(row.EndedUnixSeconds).ToOffset(utcOffset)
                .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            string place = row.Placement == 0 ? "순위 없음" : Int(row.Placement) + "위 / " + Int(row.Players) + "명";
            return when + "   " + place + "   처치 " + Int(row.Kills) + "   피해 " + Int(row.Damage) +
                   "   생존 " + Duration(row.SurvivalMs / 1000);
        }

        // 기능: 초를 시간 문자열로 바꾼다.
        // 입력: totalSeconds - 총 초(음수는 0으로 본다).
        // 출력: 한 시간 미만이면 "m:ss", 이상이면 "h:mm:ss".
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

        // 기능: 건축 조각 이름을 고른다.
        // 입력: piece - 조각 종류.
        // 출력: 상수 이름 문자열. 목록에 없는 값은 "지붕".
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

        // 기능: 건축 재료 이름을 고른다.
        // 입력: material - 재료 종류.
        // 출력: 상수 이름 문자열. 목록에 없는 값은 "금속".
        public static string MaterialName(BuildMaterialType material)
        {
            switch (material)
            {
                case BuildMaterialType.Wood: return "나무";
                case BuildMaterialType.Stone: return "돌";
                default: return "금속";
            }
        }

        // 기능: 도구 이름을 고른다.
        // 입력: tool - 도구 종류.
        // 출력: 상수 이름 문자열. 목록에 없는 값은 "무기".
        public static string ToolName(ToolKind tool)
        {
            switch (tool)
            {
                case ToolKind.Harvest: return "채집";
                case ToolKind.Build: return "건축";
                default: return "무기";
            }
        }

        // 기능: 자원 줄을 만든다.
        // 입력: wood - 나무, stone - 돌, metal - 금속 보유량.
        // 출력: "나무 N   돌 N   금속 N" 문자열.
        // The resources line (the server's numbers minus pending placements). Rebuilt only when a number changes.
        public static string ResourcesLine(int wood, int stone, int metal) =>
            "나무 " + Int(wood) + "   돌 " + Int(stone) + "   금속 " + Int(metal);

        // 기능: 건축 모드 선택 줄을 만든다.
        // 입력: piece - 선택한 조각, material - 선택한 재료.
        // 출력: "건축: 조각 · 재료" 문자열.
        // Build mode: the chosen piece and material.
        public static string BuildModeLine(BuildPieceType piece, BuildMaterialType material) =>
            "건축: " + PieceName(piece) + " · " + MaterialName(material);

        // 기능: 건축 거절 코드를 안내 문자열로 바꾼다.
        // 입력: code - 서버의 BuildResult 코드.
        // 출력: 상수 안내 문자열. Ok면 null.
        // A refused placement (BuildResult). Constants; null for Ok.
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
                default: return "지을 수 없습니다";
            }
        }

        // 기능: F1 건축 디버그 줄을 만든다.
        // 입력: tool - 현재 도구, piece - 선택한 조각, material - 선택한 재료, stored - 저장된 조각 수, drawn - 그려진 조각 수, ignored - 무시된 이벤트 수, sent - 요청 누계, refused - 거절 누계, lastRefusal - 마지막 거절 코드, requestsPerSecond - 초당 요청 수.
        // 출력: 건축 디버그 줄 문자열.
        // F1: the tool, the selection, the stored and shown pieces, refusals and requests per second.
        public static string BuildDebugLine(ToolKind tool, BuildPieceType piece, BuildMaterialType material, int stored, int drawn,
            int ignored, int sent, int refused, BuildResultCode lastRefusal, int requestsPerSecond) =>
            "도구 " + ToolName(tool) + "   " + PieceName(piece) + "/" + MaterialName(material) + "   구조물 " + Int(stored) + " (표시 " + Int(drawn) +
            ", 무시 " + Int(ignored) + ")   요청 " + Int(sent) + " (" + Int(requestsPerSecond) + "/s)   거절 " + Int(refused) + " " + lastRefusal;

        // ---- Debug line (D4, F1) ----

        // 기능: F1 연결 디버그 줄을 만든다.
        // 입력: state - 연결 상태 이름, roundTripMs - 왕복 지연(ms), entityId - 내 Entity ID.
        // 출력: 상태·RTT·Entity 문자열.
        public static string DebugLine(string state, int roundTripMs, ushort entityId) =>
            "상태 " + state + "   RTT " + Int(roundTripMs) + " ms   Entity " + Int(entityId) + "   (F1)";

        // 기능: F1 이동 디버그 줄을 만든다.
        // 입력: mode - 이동 모드 이름, horizontalTenths - 수평 속도(0.1 m/s 단위), verticalTenths - 수직 속도(0.1 m/s 단위), energy - 기력, correctionCentimetres - 예측 보정 거리(cm).
        // 출력: 이동 디버그 줄 문자열.
        // Phase 12 D14: the movement line. Speeds and the correction in tenths and hundredths, so the caller can rebuild it
        // only when a shown digit changes (DebugOverlay).
        public static string MovementLine(string mode, int horizontalTenths, int verticalTenths, int energy, int correctionCentimetres) =>
            "이동 " + mode + "   수평 " + Tenths(horizontalTenths) + " m/s   수직 " + Tenths(verticalTenths) + " m/s   기력 " + Int(energy) +
            "   보정 " + Hundredths(correctionCentimetres) + " m";

        // 기능: 수송기 경로 줄을 만든다.
        // 입력: startX, startZ - 시작 좌표, endX, endZ - 끝 좌표.
        // 출력: 정수로 반올림한 "수송기 (x, z) → (x, z)" 문자열.
        // D14: the drop transport's route (there is no map UI).
        public static string RouteLine(float startX, float startZ, float endX, float endZ) =>
            "수송기 (" + Int(RoundToInt(startX)) + ", " + Int(RoundToInt(startZ)) + ") → (" + Int(RoundToInt(endX)) + ", " + Int(RoundToInt(endZ)) + ")";

        // 기능: 실수를 가장 가까운 정수로 반올림한다.
        // 입력: value - 반올림할 값.
        // 출력: 반올림한 정수(중간값은 짝수 쪽, Math.Round 기본).
        private static int RoundToInt(float value) => (int)Math.Round(value);

        // 기능: 0.1 단위 정수를 소수 한 자리 문자열로 바꾼다.
        // 입력: tenths - 0.1 단위 값.
        // 출력: 부호를 포함한 "N.N" 문자열.
        private static string Tenths(int tenths)
        {
            string sign = tenths < 0 ? "-" : string.Empty;
            int a = Math.Abs(tenths);
            return sign + Int(a / 10) + "." + Int(a % 10);
        }

        // 기능: 0.01 단위 정수를 소수 두 자리 문자열로 바꾼다.
        // 입력: hundredths - 0.01 단위 값.
        // 출력: 부호를 포함한 "N.NN" 문자열.
        private static string Hundredths(int hundredths)
        {
            string sign = hundredths < 0 ? "-" : string.Empty;
            int a = Math.Abs(hundredths);
            return sign + Int(a / 100) + "." + Two(a % 100);
        }

        // ---- Title input (D4) ----

        // 기능: 타이틀에 입력한 이름을 다듬고 서버의 이름 규칙으로 검사한다.
        // 입력: input - 입력한 이름(null 가능).
        // 출력: 규칙에 맞으면 true와 앞뒤 공백을 뺀 이름, 아니면 false와 null.
        // The name typed on the title screen: trimmed, then the server's connect-request rule
        // (ProtocolConstants.IsValidPlayerName: 1-32 bytes of valid UTF-8, no control characters).
        public static bool TryNormalizeName(string input, out string name)
        {
            name = input == null ? string.Empty : input.Trim();
            if (ProtocolConstants.IsValidPlayerName(name)) return true;
            name = null;
            return false;
        }

        // 기능: 타이틀에 입력한 주소를 다듬고 길이를 검사한다.
        // 입력: input - 입력한 주소(null 가능).
        // 출력: 1-253자이면 true와 앞뒤 공백을 뺀 주소, 아니면 false와 null.
        public static bool TryNormalizeHost(string input, out string host)
        {
            host = input == null ? string.Empty : input.Trim();
            if (host.Length > 0 && host.Length <= 253) return true;
            host = null;
            return false;
        }

        // 기능: 타이틀에 입력한 포트를 파싱한다.
        // 입력: input - 입력한 포트 문자열(null 가능).
        // 출력: 1-65535 사이 숫자이면 true와 포트, 아니면 false와 0.
        public static bool TryParsePort(string input, out int port)
        {
            if (int.TryParse(input == null ? null : input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port >= 1 && port <= 65535)
                return true;
            port = 0;
            return false;
        }

        // 기능: 정수를 InvariantCulture 문자열로 바꾼다.
        // 입력: value - 바꿀 값.
        // 출력: 10진수 문자열.
        private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

        // 기능: 정수를 최소 두 자리 문자열로 바꾼다.
        // 입력: value - 바꿀 값(0 이상).
        // 출력: 10 미만이면 앞에 0을 붙인 문자열.
        private static string Two(long value) => value < 10 ? "0" + Int(value) : Int(value);
    }
}
