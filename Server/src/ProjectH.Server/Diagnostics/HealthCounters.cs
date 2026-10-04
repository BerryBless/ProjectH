using System;
using System.Threading;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D5: why a packet counted as invalid. Index into HealthCounters' bad-packet array; keep Count last.
public enum BadPacketReason
{
    UnknownId,        // empty, or a first byte that is no PacketId
    Malformed,        // a known id whose body does not parse
    InputBeforeJoin,
    DuplicateJoin,
    InputRate,        // above ServerOptions.MaxInputPacketsPerSecond (dropped, never kicks: server review M5)
    WrongDirection,   // a server-to-client packet id
    HandlerException, // the receive handler threw (a server bug, counted against the peer)
    BuildRate,        // Phase 13 D8: above the building catalog's maxRequestsPerSecond
    Count,
}

// Phase 13 D18: the match's building and harvesting numbers (since the match object was made; HealthCounters carries them
// over a match reset, final review B12). Requests counts every request processed or dropped as a duplicate. SyncDeferred
// (final review A4): ticks a client's sync waited for its backed-up building channel.
public readonly record struct BuildCounts(int Pieces, int Cells, long Requests, long Accepted, long Rejected, long Destroyed, long Collapsed,
    long Duplicates, long HarvestHits, long EnvironmentDestroyed, long EventPackets, long SyncPackets, long DamageDestroyed = 0,
    long SyncDeferred = 0);

// Phase 10 D9: totals since the server started, for the Health line and the "ProjectH.Server" Meter. Written from
// LiteNetLib's threads and the game loop, read by the game loop (Health line) and by the Meter's observers on
// whatever thread polls them. Interlocked/Volatile only, no lock: each value is independent, so a slightly
// inconsistent view across values is acceptable for monitoring.
public sealed class HealthCounters
{
    private const int RejectSlots = (int)RejectReason.BadRequest + 1;
    private const int CodeSlots = (int)DisconnectCode.Congested + 1;

    private readonly long[] _rejects = new long[RejectSlots];
    private readonly long[] _kicks = new long[CodeSlots];
    private readonly long[] _badPackets = new long[(int)BadPacketReason.Count];
    private long _connections;
    private long _joins;
    private long _resumes;
    private long _graceStarts;
    private long _graceExpiries;
    private long _disconnectTimeouts;
    private long _disconnectOthers;
    private long _tickFailures;
    private long _loopFailures;
    private long _matchResets;
    private long _stalls;
    private long _movementAnomalies;
    // Server review M1: socket errors LiteNetLib reported (OnNetworkError; logged once per stats interval).
    private long _networkErrors;
    // Server review M2: connection requests refused by the per-IP rate (sent as ServerFull, counted apart from it).
    private long _connectRateRejects;
    // Server review M7: players whose own tick threw; each was taken out of the match and its connection closed.
    private long _playerFailures;
    // Server review M8: stalls that lasted FatalStallSeconds and stopped the server (at most 1 per process).
    private long _stallExits;
    // Server review L9: exceptions caught at an entry point other code calls us through (LiteNetLib's connection request,
    // disconnect and socket-error callbacks, the stall watchdog's timer).
    private long _callbackErrors;
    // Phase 13 D18: written by the game loop after every tick (BuildCounts); the fields are read one by one.
    private long _buildPieces;
    private long _buildCells;
    private long _buildRequests;
    private long _buildAccepted;
    private long _buildRejected;
    private long _buildDestroyed;
    private long _buildCollapsed;
    private long _buildDuplicates;
    private long _harvestHits;
    private long _environmentDestroyed;
    private long _buildEventPackets;
    private long _buildSyncPackets;
    private long _buildDamageDestroyed;
    private long _buildSyncDeferred;
    // Final review B12 (game loop only): the totals of the matches a reset threw away, added to the current match's.
    private BuildCounts _buildBase;
    private readonly long[] _buildRejectBase = new long[(int)BuildResultCode.BudgetFull + 1];
    private long _buildInboxDrops;
    private readonly long[] _buildRejects = new long[(int)BuildResultCode.BudgetFull + 1];
    // Gauges, written by the game loop once per tick.
    private int _peers;
    private int _players;
    private int _graced;
    private int _matchState;

    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
    // Phase 11 D8 counters of the statistics path (StatsQueryQueue). Set once by GameLoop's constructor.
    public Func<StatsQueryCounts>? StatsQueries { get; set; }

    // 기능: 거절한 연결 요청 하나를 사유별로 센다. LiteNetLib의 연결 요청 Callback에서 호출한다.
    // 입력: reason - 거절 사유.
    // 출력: 반환값 없음. 해당 사유의 거절 수가 1 증가한다.
    public void AddReject(RejectReason reason) => Interlocked.Increment(ref _rejects[(int)reason]);
    // 기능: 서버가 끊은 연결 하나를 종료 코드별로 센다. Game Loop와 LiteNetLib Thread 어디서 불러도 된다.
    // 입력: code - 연결을 끊은 종료 코드.
    // 출력: 반환값 없음. 해당 코드의 Kick 수가 1 증가한다.
    public void AddKick(DisconnectCode code) => Interlocked.Increment(ref _kicks[(int)code]);
    // 기능: 잘못된 Packet 하나를 사유별로 센다. LiteNetLib 수신 Thread에서 호출한다.
    // 입력: reason - 잘못된 Packet으로 본 사유.
    // 출력: 반환값 없음. 해당 사유의 잘못된 Packet 수가 1 증가한다.
    public void AddBadPacket(BadPacketReason reason) => Interlocked.Increment(ref _badPackets[(int)reason]);
    // 기능: 수락한 연결 하나를 센다. LiteNetLib의 연결 요청 Callback에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 수락한 연결 누적 수가 1 증가한다.
    public void AddConnection() => Interlocked.Increment(ref _connections);
    // 기능: 새로 성공한 Join(JoinResult.Ok) 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 새로 성공한 Join(JoinResult.Ok) 누적 수가 1 증가한다.
    public void AddJoin() => Interlocked.Increment(ref _joins);
    // 기능: 재접속으로 이어진 참가(JoinResult.Resumed) 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 재접속으로 이어진 참가(JoinResult.Resumed) 누적 수가 1 증가한다.
    public void AddResume() => Interlocked.Increment(ref _resumes);
    // 기능: 재접속 유예를 시작한 참가자 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 재접속 유예를 시작한 참가자 누적 수가 1 증가한다.
    public void AddGraceStart() => Interlocked.Increment(ref _graceStarts);
    // 기능: 재접속하지 못하고 유예가 끝난 참가자 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 재접속하지 못하고 유예가 끝난 참가자 누적 수가 1 증가한다.
    // A graced player that left without resuming: its grace ran out, it died while away, or the round reset.
    public void AddGraceExpiry() => Interlocked.Increment(ref _graceExpiries);
    // 기능: 연결 종료 하나를 Timeout 또는 그 밖의 사유로 나눠 센다.
    // 입력: timeout - Timeout으로 끊겼으면 true.
    // 출력: 반환값 없음. Timeout 종료 수 또는 기타 종료 수가 1 증가한다.
    public void AddDisconnect(bool timeout) => Interlocked.Increment(ref timeout ? ref _disconnectTimeouts : ref _disconnectOthers);
    // 기능: 예외로 실패한 Tick 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 예외로 실패한 Tick 누적 수가 1 증가한다.
    public void AddTickFailure() => Interlocked.Increment(ref _tickFailures);
    // 기능: Tick 바깥 Loop 본문에서 잡힌 예외 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Tick 바깥 Loop 본문에서 잡힌 예외 누적 수가 1 증가한다.
    public void AddLoopFailure() => Interlocked.Increment(ref _loopFailures);
    // 기능: Match Reset 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Match Reset 누적 수가 1 증가한다.
    public void AddMatchReset() => Interlocked.Increment(ref _matchResets);
    // 기능: 감지된 Game Loop Stall 하나를 센다. Stall Watchdog의 Timer Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 감지된 Game Loop Stall 누적 수가 1 증가한다.
    public void AddStall() => Interlocked.Increment(ref _stalls);
    // 기능: 이동 모드 허용 속도를 넘은 이동 하나를 센다. Game Loop Thread(Match 자체 점검)에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 이동 모드 허용 속도를 넘은 이동 누적 수가 1 증가한다.
    // Phase 12 D12: a move faster than its mode allows (Match's self-check; should stay 0).
    public void AddMovementAnomaly() => Interlocked.Increment(ref _movementAnomalies);
    // 기능: LiteNetLib가 알린 Socket 오류 하나를 센다. LiteNetLib의 OnNetworkError Callback에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. LiteNetLib가 알린 Socket 오류 누적 수가 1 증가한다.
    public void AddNetworkError() => Interlocked.Increment(ref _networkErrors);
    // 기능: IP별 연결 비율 초과로 거절한 연결 요청 하나를 센다. LiteNetLib의 연결 요청 Callback에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. IP별 연결 비율 초과로 거절한 연결 요청 누적 수가 1 증가한다.
    public void AddConnectRateReject() => Interlocked.Increment(ref _connectRateRejects);
    // 기능: 자기 Tick 처리 중 예외가 나서 Match에서 빠진 참가자 하나를 센다. Game Loop Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 자기 Tick 처리 중 예외가 나서 Match에서 빠진 참가자 누적 수가 1 증가한다.
    public void AddPlayerFailure() => Interlocked.Increment(ref _playerFailures);
    // 기능: FatalStallSeconds 동안 이어져 서버를 멈춘 Stall 하나를 센다. Stall Watchdog의 Timer Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. FatalStallSeconds 동안 이어져 서버를 멈춘 Stall 누적 수가 1 증가한다.
    public void AddStallExit() => Interlocked.Increment(ref _stallExits);
    // 기능: 외부 진입점(LiteNetLib Callback, Watchdog Timer)에서 잡힌 예외 하나를 센다. 해당 Callback의 Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 외부 진입점(LiteNetLib Callback, Watchdog Timer)에서 잡힌 예외 누적 수가 1 증가한다.
    public void AddCallbackError() => Interlocked.Increment(ref _callbackErrors);

    // 기능: 현재 연결 수·참가자 수·유예 중 참가자 수·Match 상태 Gauge를 기록한다. Game Loop가 Tick마다 호출한다.
    // 입력: peers - 열린 연결 수, players - 유예 포함 Match 참가자 수, graced - 재접속 대기 중인 참가자 수, state - 현재 Match 흐름 상태.
    // 출력: 반환값 없음. Health 줄과 Meter가 읽는 Gauge 값이 갱신된다.
    public void SetGauges(int peers, int players, int graced, MatchFlowState state)
    {
        Volatile.Write(ref _peers, peers);
        Volatile.Write(ref _players, players);
        Volatile.Write(ref _graced, graced);
        Volatile.Write(ref _matchState, (int)state);
    }

    // 기능: 현재 Match의 건설·채집 수치를 이전 Match에서 넘겨받은 기준값에 더해 서버 시작 이후 누적값으로 기록한다.
    // 입력: c - 현재 Match의 건설 수치, rejects - 거절 사유별 현재 Match 거절 수를 돌려주는 함수.
    // 출력: 반환값 없음. 건설 누적 Counter와 Pieces·Cells Gauge가 갱신된다.
    // The current match's numbers, written as totals since the start: the carried base plus these (the gauges Pieces and
    // Cells as they are).
    public void SetBuild(in BuildCounts c, Func<BuildResultCode, long> rejects)
    {
        BuildCounts b = _buildBase;
        Volatile.Write(ref _buildPieces, c.Pieces);
        Volatile.Write(ref _buildCells, c.Cells);
        Volatile.Write(ref _buildRequests, b.Requests + c.Requests);
        Volatile.Write(ref _buildAccepted, b.Accepted + c.Accepted);
        Volatile.Write(ref _buildRejected, b.Rejected + c.Rejected);
        Volatile.Write(ref _buildDestroyed, b.Destroyed + c.Destroyed);
        Volatile.Write(ref _buildCollapsed, b.Collapsed + c.Collapsed);
        Volatile.Write(ref _buildDuplicates, b.Duplicates + c.Duplicates);
        Volatile.Write(ref _harvestHits, b.HarvestHits + c.HarvestHits);
        Volatile.Write(ref _environmentDestroyed, b.EnvironmentDestroyed + c.EnvironmentDestroyed);
        Volatile.Write(ref _buildEventPackets, b.EventPackets + c.EventPackets);
        Volatile.Write(ref _buildSyncPackets, b.SyncPackets + c.SyncPackets);
        Volatile.Write(ref _buildDamageDestroyed, b.DamageDestroyed + c.DamageDestroyed);
        Volatile.Write(ref _buildSyncDeferred, b.SyncDeferred + c.SyncDeferred);
        for (int i = 1; i < _buildRejects.Length; i++) Volatile.Write(ref _buildRejects[i], _buildRejectBase[i] + rejects((BuildResultCode)i));
    }

    // 기능: Match Reset 직전에 마지막으로 기록된 건설 누적값을 다음 Match의 기준값으로 저장한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 건설 기준값(Pieces·Cells는 0)과 거절 사유별 기준값이 갱신된다.
    // Final review B12: a match reset replaces the match (whose numbers start at 0): what was written last becomes the
    // base, so the totals (and the Meter's counters) never go back. Game loop only, before the new match's first SetBuild.
    public void CarryBuildTotals()
    {
        _buildBase = Build with { Pieces = 0, Cells = 0 };
        for (int i = 1; i < _buildRejects.Length; i++) _buildRejectBase[i] = Volatile.Read(ref _buildRejects[i]);
    }

    public BuildCounts Build => new((int)Volatile.Read(ref _buildPieces), (int)Volatile.Read(ref _buildCells), Volatile.Read(ref _buildRequests),
        Volatile.Read(ref _buildAccepted), Volatile.Read(ref _buildRejected), Volatile.Read(ref _buildDestroyed), Volatile.Read(ref _buildCollapsed),
        Volatile.Read(ref _buildDuplicates), Volatile.Read(ref _harvestHits), Volatile.Read(ref _environmentDestroyed),
        Volatile.Read(ref _buildEventPackets), Volatile.Read(ref _buildSyncPackets), Volatile.Read(ref _buildDamageDestroyed),
        Volatile.Read(ref _buildSyncDeferred));

    // 기능: 가득 찬 수신 Channel이 버린 건설 요청 하나를 센다. LiteNetLib Thread에서 호출한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 가득 찬 수신 Channel이 버린 건설 요청 누적 수가 1 증가한다.
    // Build requests the inbound channel dropped (full, DropOldest); written by LiteNetLib threads.
    public void AddBuildInboxDrop() => Interlocked.Increment(ref _buildInboxDrops);
    public long BuildInboxDrops => Interlocked.Read(ref _buildInboxDrops);

    // 기능: 건설 거절 수를 결과 코드별로 읽는다.
    // 입력: code - 읽을 건설 결과 코드.
    // 출력: 서버 시작 이후 해당 코드로 거절된 건설 요청 누적 수.
    public long BuildRejects(BuildResultCode code) => Volatile.Read(ref _buildRejects[(int)code]);

    // 기능: 거절한 연결 요청 수를 사유별로 읽는다.
    // 입력: reason - 읽을 거절 사유.
    // 출력: 서버 시작 이후 해당 사유로 거절한 연결 요청 누적 수.
    public long Rejects(RejectReason reason) => Interlocked.Read(ref _rejects[(int)reason]);
    // 기능: 서버가 끊은 연결 수를 종료 코드별로 읽는다.
    // 입력: code - 읽을 종료 코드.
    // 출력: 서버 시작 이후 해당 코드로 끊은 연결 누적 수.
    public long Kicks(DisconnectCode code) => Interlocked.Read(ref _kicks[(int)code]);
    // 기능: 잘못된 Packet 수를 사유별로 읽는다.
    // 입력: reason - 읽을 잘못된 Packet 사유.
    // 출력: 서버 시작 이후 해당 사유로 센 잘못된 Packet 누적 수.
    public long BadPackets(BadPacketReason reason) => Interlocked.Read(ref _badPackets[(int)reason]);
    public long Connections => Interlocked.Read(ref _connections);
    public long Joins => Interlocked.Read(ref _joins);
    public long Resumes => Interlocked.Read(ref _resumes);
    public long GraceStarts => Interlocked.Read(ref _graceStarts);
    public long GraceExpiries => Interlocked.Read(ref _graceExpiries);
    public long DisconnectTimeouts => Interlocked.Read(ref _disconnectTimeouts);
    public long DisconnectOthers => Interlocked.Read(ref _disconnectOthers);
    public long TickFailures => Interlocked.Read(ref _tickFailures);
    public long LoopFailures => Interlocked.Read(ref _loopFailures);
    public long MatchResets => Interlocked.Read(ref _matchResets);
    public long Stalls => Interlocked.Read(ref _stalls);
    public long MovementAnomalies => Interlocked.Read(ref _movementAnomalies);
    public long NetworkErrors => Interlocked.Read(ref _networkErrors);
    public long ConnectRateRejects => Interlocked.Read(ref _connectRateRejects);
    public long PlayerFailures => Interlocked.Read(ref _playerFailures);
    public long StallExits => Interlocked.Read(ref _stallExits);
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);
    public int Peers => Volatile.Read(ref _peers);
    public int Players => Volatile.Read(ref _players);
    public int Graced => Volatile.Read(ref _graced);
    public MatchFlowState MatchState => (MatchFlowState)Volatile.Read(ref _matchState);

    // 기능: 모든 사유의 잘못된 Packet 수를 더해 읽는다.
    // 입력: 없음.
    // 출력: 서버 시작 이후 잘못된 Packet 누적 수의 사유별 합계.
    public long BadPacketsTotal
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _badPackets.Length; i++) total += Interlocked.Read(ref _badPackets[i]);
            return total;
        }
    }
}
