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
    MarkerRate,       // Phase 15 D7: more MapMarker packets in one second than map.json maxMarkerPacketsPerSecond
    Count,
}

// Phase 13 D18: the match's building and harvesting numbers (since the match object was made; HealthCounters carries them
// over a match reset, final review B12). Requests counts every request processed or dropped as a duplicate. SyncDeferred
// (final review A4): ticks a client's sync waited for its backed-up building channel.
public readonly record struct BuildCounts(int Pieces, int Cells, long Requests, long Accepted, long Rejected, long Destroyed, long Collapsed,
    long Duplicates, long HarvestHits, long EnvironmentDestroyed, long EventPackets, long SyncPackets, long DamageDestroyed = 0,
    long SyncDeferred = 0, long Edits = 0);

// Phase 14: knock-downs, revives, reboots, bleed-outs, reboot cards dropped and expired, squads wiped out and revives or
// reboots cancelled (since the match object was made; HealthCounters carries them over a match reset).
public readonly record struct SquadCounts(long Downs, long Revives, long Reboots, long BleedOuts, long CardsDropped, long CardsExpired,
    long Wipes, long ChannelsCancelled);

// Phase 15: pings accepted (Enemy ones confirmed included), Enemy pings the server confirmed, Enemy pings demoted to
// Location (not an enemy, too far, out of sight, outside the map), requests refused (not in a match, not a living
// participant with a team, outside the map, an Item that is not there or too far), pings replaced by the per-player or
// per-team limit, pings expired, waypoint sets and clears, and TeamMarkers packets sent (since the match object was made;
// HealthCounters carries them over a match reset).
public readonly record struct MapCounts(long Pings, long EnemyConfirmed, long EnemyDemoted, long Refused, long Replaced, long Expired,
    long Waypoints, long Packets);

// Phase 16: containers opened, supply drops spawned, landed and opened, container loot items put in the world, opens
// refused for the line of sight, and ContainerStates/SupplyDrops packets sent (since the match object was made; HealthCounters
// carries them over a match reset).
public readonly record struct LootCounts(long ContainersOpened, long DropsSpawned, long DropsLanded, long DropsOpened, long LootItems,
    long OpensBlocked, long Packets);

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
    // Review fix A2: requests refused because their address held MaxConnectionsPerIp connections, because it was penalized
    // (A6), and because the global accept bucket was empty (all sent as ServerFull, counted apart from it).
    private long _perIpRejects;
    private long _penaltyRejects;
    private long _acceptRateRejects;
    // Review fix A3: requests that carried a wrong or expired cookie (refused, no peer), and first requests without one
    // that were answered with a cookie (the normal first step of every connect, not a refusal).
    private long _cookieRejects;
    private long _cookieChallenges;
    // Review fix A6: addresses penalized for repeated player failures.
    private long _penalties;
    // Review fix B3: datagrams from a keyed endpoint whose tail failed (forged, tampered, replayed or out of the window).
    private long _authDrops;
    // Review B round 2: the same on a retired key (a connection closed within RetireMs). Kept apart so authDrops stays 0 in a
    // normal run: a same-port reconnect's ShutdownOk to the cookie reject lands here, and so does a forgery in the retire window.
    private long _authDropsRetired;
    // Review fix A4: inputs dropped for a Seq too far ahead (game loop writes the running total, see SetInputSeqDrops).
    private long _inputSeqDrops;
    private long _inputSeqDropBase;
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
    // Phase 13.5: edits that changed a piece.
    private long _buildEdits;
    // Final review B12 (game loop only): the totals of the matches a reset threw away, added to the current match's.
    private BuildCounts _buildBase;
    private readonly long[] _buildRejectBase = new long[(int)BuildResultCode.NotFound + 1];
    private long _buildInboxDrops;
    private readonly long[] _buildRejects = new long[(int)BuildResultCode.NotFound + 1];
    // Phase 14 (game loop writes, any thread reads): the squad totals and the base a match reset carried over.
    private long _downs;
    private long _revives;
    private long _reboots;
    private long _bleedOuts;
    private long _cardsDropped;
    private long _cardsExpired;
    private long _wipes;
    private long _channelsCancelled;
    private SquadCounts _squadBase;
    // Phase 15 (game loop writes, any thread reads): the map marker totals and the base a match reset carried over.
    private long _pings;
    private long _enemyConfirmed;
    private long _enemyDemoted;
    private long _markersRefused;
    private long _pingsReplaced;
    private long _pingsExpired;
    private long _waypoints;
    private long _markerPackets;
    private MapCounts _mapBase;
    // Phase 16 (game loop writes, any thread reads): the loot container totals and the base a match reset carried over.
    private long _containersOpened;
    private long _dropsSpawned;
    private long _dropsLanded;
    private long _dropsOpened;
    private long _lootItems;
    private long _opensBlocked;
    private long _lootPackets;
    private LootCounts _lootBase;
    // Phase 15 D7 (LiteNetLib threads): MapMarker packets the per-connection bucket dropped, and requests the full inbound
    // Marker channel dropped.
    private long _markerDrops;
    private long _markerInboxDrops;
    // Gauges, written by the game loop once per tick.
    private int _peers;
    private int _players;
    private int _graced;
    private int _matchState;

    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
    // Phase 11 D8 counters of the statistics path (StatsQueryQueue). Set once by GameLoop's constructor.
    public Func<StatsQueryCounts>? StatsQueries { get; set; }

    // 기능: 연결 요청 거절 하나를 이유별로 센다(수신 스레드).
    // 입력: reason - 거절 이유(프로토콜의 RejectReason).
    // 출력: 반환값 없음.
    public void AddReject(RejectReason reason) => Interlocked.Increment(ref _rejects[(int)reason]);
    // 기능: 서버가 연결 하나를 끊은 횟수를 코드별로 센다.
    // 입력: code - 끊은 이유(DisconnectCode).
    // 출력: 반환값 없음.
    public void AddKick(DisconnectCode code) => Interlocked.Increment(ref _kicks[(int)code]);
    // 기능: 잘못된 패킷 하나를 이유별로 센다(수신 스레드).
    // 입력: reason - 잘못된 이유(BadPacketReason, Count 제외).
    // 출력: 반환값 없음.
    public void AddBadPacket(BadPacketReason reason) => Interlocked.Increment(ref _badPackets[(int)reason]);
    // 기능: 수락된 연결 하나를 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddConnection() => Interlocked.Increment(ref _connections);
    // 기능: 경기 참가(Join) 하나를 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddJoin() => Interlocked.Increment(ref _joins);
    // 기능: 유예 중 재접속(Resume) 하나를 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddResume() => Interlocked.Increment(ref _resumes);
    // 기능: 재접속 유예 시작 하나를 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddGraceStart() => Interlocked.Increment(ref _graceStarts);
    // 기능: 재접속하지 못하고 떠난 유예 플레이어 하나를 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    // A graced player that left without resuming: its grace ran out, it died while away, or the round reset.
    public void AddGraceExpiry() => Interlocked.Increment(ref _graceExpiries);
    // 기능: 연결 종료 하나를 Timeout과 그 외로 나눠 센다.
    // 입력: timeout - LiteNetLib Timeout으로 끊겼으면 true.
    // 출력: 반환값 없음.
    public void AddDisconnect(bool timeout) => Interlocked.Increment(ref timeout ? ref _disconnectTimeouts : ref _disconnectOthers);
    // 기능: 예외로 끝난 Tick 하나를 센다(Game Loop).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddTickFailure() => Interlocked.Increment(ref _tickFailures);
    // 기능: Tick 밖(Loop 자체)에서 난 예외 하나를 센다(Game Loop).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddLoopFailure() => Interlocked.Increment(ref _loopFailures);
    // 기능: 경기 객체를 새로 만든 리셋 하나를 센다(Game Loop).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddMatchReset() => Interlocked.Increment(ref _matchResets);
    // 기능: Watchdog이 감지한 Game Loop 정지 하나를 센다(Timer 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddStall() => Interlocked.Increment(ref _stalls);
    // 기능: 이동 모드 상한을 넘은 이동 하나를 센다(Game Loop, 0이어야 정상).
    // 입력: 없음.
    // 출력: 반환값 없음.
    // Phase 12 D12: a move faster than its mode allows (Match's self-check; should stay 0).
    public void AddMovementAnomaly() => Interlocked.Increment(ref _movementAnomalies);
    // 기능: LiteNetLib가 보고한 소켓 오류 하나를 센다(서버 리뷰 M1, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddNetworkError() => Interlocked.Increment(ref _networkErrors);
    // 기능: 주소별 연결 속도 제한으로 거절한 요청 하나를 센다(서버 리뷰 M2, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddConnectRateReject() => Interlocked.Increment(ref _connectRateRejects);
    // 기능: 자기 Tick이 예외를 던져 경기에서 빠지고 연결이 닫힌 플레이어 하나를 센다(서버 리뷰 M7, Game Loop).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddPlayerFailure() => Interlocked.Increment(ref _playerFailures);

    // 기능: 동시 연결 상한(리뷰 수정 A2)으로 거절한 요청 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddPerIpReject() => Interlocked.Increment(ref _perIpRejects);

    // 기능: 벌점 중인 주소(리뷰 수정 A6)라 거절한 요청 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddPenaltyReject() => Interlocked.Increment(ref _penaltyRejects);

    // 기능: 전역 수락 Token Bucket(리뷰 수정 A2)이 비어 거절한 요청 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddAcceptRateReject() => Interlocked.Increment(ref _acceptRateRejects);

    // 기능: 틀리거나 지난 쿠키(리뷰 수정 A3)를 가진 요청 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddCookieReject() => Interlocked.Increment(ref _cookieRejects);

    // 기능: 쿠키 없는 첫 요청에 쿠키를 돌려준 횟수 하나를 센다(리뷰 수정 A3, 정상 접속의 첫 단계, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddCookieChallenge() => Interlocked.Increment(ref _cookieChallenges);

    // 기능: 벌점을 준 주소 하나를 센다(리뷰 수정 A6: 반복 플레이어 실패, Game Loop. 리뷰 B 1·2차: 같은 칸의 1분 안 세 번째 복호되지 않는 세션 키 blob, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddPenalty() => Interlocked.Increment(ref _penalties);

    // 기능: 인증 꼬리 검증에 실패해 버린 데이터그램 하나를 센다(리뷰 수정 B3, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddAuthDrop() => Interlocked.Increment(ref _authDrops);

    // 기능: 은퇴한 키(끊긴 지 RetireMs 안의 endpoint)에서 열리지 않아 버린 데이터그램 하나를 센다(리뷰 B 2차, 수신 스레드). authDrops와 따로 센다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddAuthDropRetired() => Interlocked.Increment(ref _authDropsRetired);

    // 기능: 지금 경기의 Seq 창 밖 입력 드롭 합계를 시작부터의 합계로 쓴다(리셋으로 넘어온 기준값 + 이 경기 값, 리뷰 수정 A4). Game Loop만 부른다.
    // 입력: matchTotal - 지금 경기 객체의 합계(Match.InputSeqDrops).
    // 출력: 반환값 없음.
    public void SetInputSeqDrops(long matchTotal) => Volatile.Write(ref _inputSeqDrops, _inputSeqDropBase + matchTotal);

    // 기능: 경기 리셋 때 지금까지의 Seq 창 밖 드롭 합계를 기준값으로 넘긴다(합계가 줄지 않게, CarryLootTotals와 같다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void CarryInputSeqDrops() => _inputSeqDropBase = Volatile.Read(ref _inputSeqDrops);
    // 기능: FatalStallSeconds를 넘긴 정지로 서버를 멈춘 횟수 하나를 센다(서버 리뷰 M8, 프로세스당 최대 1, Timer 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddStallExit() => Interlocked.Increment(ref _stallExits);
    // 기능: 외부가 부르는 진입점(LiteNetLib 콜백, Watchdog Timer)에서 잡은 예외 하나를 센다(서버 리뷰 L9).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddCallbackError() => Interlocked.Increment(ref _callbackErrors);

    // 기능: 매 Tick 끝의 현재값(Gauge)을 쓴다. Game Loop만 부른다.
    // 입력: peers - 열린 연결 수, players - 경기 참가자 수(유예 포함), graced - 재접속 대기 중인 수, state - 경기 진행 상태.
    // 출력: 반환값 없음. Health 줄과 Meter가 읽는 Gauge 네 개가 바뀐다.
    public void SetGauges(int peers, int players, int graced, MatchFlowState state)
    {
        Volatile.Write(ref _peers, peers);
        Volatile.Write(ref _players, players);
        Volatile.Write(ref _graced, graced);
        Volatile.Write(ref _matchState, (int)state);
    }

    // 기능: 지금 경기의 건설·채집 수치를 시작부터의 합계로 쓴다(리셋으로 넘어온 기준값 + 이 경기 값, Pieces·Cells는 Gauge라 그대로). Game Loop만 부른다.
    // 입력: c - 경기 객체의 수치, rejects - BuildResultCode별 거절 수를 돌려주는 함수(Ok 제외).
    // 출력: 반환값 없음. Build 합계와 코드별 거절 합계가 바뀐다.
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
        Volatile.Write(ref _buildEdits, b.Edits + c.Edits);
        for (int i = 1; i < _buildRejects.Length; i++) Volatile.Write(ref _buildRejects[i], _buildRejectBase[i] + rejects((BuildResultCode)i));
    }

    // 기능: 경기 리셋 때 지금까지 쓴 건설 합계(Pieces·Cells 제외)와 코드별 거절 합계를 기준값으로 넘긴다(합계가 줄지 않게). Game Loop만, 새 경기의 첫 SetBuild 전에 부른다.
    // 입력: 없음.
    // 출력: 반환값 없음.
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
        Volatile.Read(ref _buildSyncDeferred), Volatile.Read(ref _buildEdits));

    // 기능: 지금 경기의 분대 수치를 시작부터의 합계로 쓴다(리셋으로 넘어온 기준값 + 이 경기 값). Game Loop만 부른다.
    // 입력: c - 경기 객체의 수치.
    // 출력: 반환값 없음.
    public void SetSquad(in SquadCounts c)
    {
        SquadCounts b = _squadBase;
        Volatile.Write(ref _downs, b.Downs + c.Downs);
        Volatile.Write(ref _revives, b.Revives + c.Revives);
        Volatile.Write(ref _reboots, b.Reboots + c.Reboots);
        Volatile.Write(ref _bleedOuts, b.BleedOuts + c.BleedOuts);
        Volatile.Write(ref _cardsDropped, b.CardsDropped + c.CardsDropped);
        Volatile.Write(ref _cardsExpired, b.CardsExpired + c.CardsExpired);
        Volatile.Write(ref _wipes, b.Wipes + c.Wipes);
        Volatile.Write(ref _channelsCancelled, b.ChannelsCancelled + c.ChannelsCancelled);
    }

    // 기능: 경기 리셋 때 지금까지 쓴 분대 합계를 기준값으로 넘긴다(합계가 줄지 않게, CarryBuildTotals와 같다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void CarrySquadTotals() => _squadBase = Squad;

    public SquadCounts Squad => new(Volatile.Read(ref _downs), Volatile.Read(ref _revives), Volatile.Read(ref _reboots), Volatile.Read(ref _bleedOuts),
        Volatile.Read(ref _cardsDropped), Volatile.Read(ref _cardsExpired), Volatile.Read(ref _wipes), Volatile.Read(ref _channelsCancelled));

    // 기능: 지금 경기의 지도 표시 수치를 시작부터의 합계로 쓴다(리셋으로 넘어온 기준값 + 이 경기 값). Game Loop만 부른다.
    // 입력: c - 경기 객체의 수치.
    // 출력: 반환값 없음.
    public void SetMap(in MapCounts c)
    {
        MapCounts b = _mapBase;
        Volatile.Write(ref _pings, b.Pings + c.Pings);
        Volatile.Write(ref _enemyConfirmed, b.EnemyConfirmed + c.EnemyConfirmed);
        Volatile.Write(ref _enemyDemoted, b.EnemyDemoted + c.EnemyDemoted);
        Volatile.Write(ref _markersRefused, b.Refused + c.Refused);
        Volatile.Write(ref _pingsReplaced, b.Replaced + c.Replaced);
        Volatile.Write(ref _pingsExpired, b.Expired + c.Expired);
        Volatile.Write(ref _waypoints, b.Waypoints + c.Waypoints);
        Volatile.Write(ref _markerPackets, b.Packets + c.Packets);
    }

    // 기능: 경기 리셋 때 지금까지 쓴 지도 표시 합계를 기준값으로 넘긴다(합계가 줄지 않게, CarrySquadTotals와 같다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void CarryMapTotals() => _mapBase = Map;

    public MapCounts Map => new(Volatile.Read(ref _pings), Volatile.Read(ref _enemyConfirmed), Volatile.Read(ref _enemyDemoted),
        Volatile.Read(ref _markersRefused), Volatile.Read(ref _pingsReplaced), Volatile.Read(ref _pingsExpired), Volatile.Read(ref _waypoints),
        Volatile.Read(ref _markerPackets));

    // 기능: 지금 경기의 Loot Container·Supply Drop 수치를 시작부터의 합계로 쓴다(리셋으로 넘어온 기준값 + 이 경기 값). Game Loop만 부른다.
    // 입력: c - 경기 객체의 수치.
    // 출력: 반환값 없음.
    public void SetLoot(in LootCounts c)
    {
        LootCounts b = _lootBase;
        Volatile.Write(ref _containersOpened, b.ContainersOpened + c.ContainersOpened);
        Volatile.Write(ref _dropsSpawned, b.DropsSpawned + c.DropsSpawned);
        Volatile.Write(ref _dropsLanded, b.DropsLanded + c.DropsLanded);
        Volatile.Write(ref _dropsOpened, b.DropsOpened + c.DropsOpened);
        Volatile.Write(ref _lootItems, b.LootItems + c.LootItems);
        Volatile.Write(ref _opensBlocked, b.OpensBlocked + c.OpensBlocked);
        Volatile.Write(ref _lootPackets, b.Packets + c.Packets);
    }

    // 기능: 경기 리셋 때 지금까지 쓴 Loot 합계를 기준값으로 넘긴다(합계가 줄지 않게, CarryMapTotals와 같다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void CarryLootTotals() => _lootBase = Loot;

    public LootCounts Loot => new(Volatile.Read(ref _containersOpened), Volatile.Read(ref _dropsSpawned), Volatile.Read(ref _dropsLanded),
        Volatile.Read(ref _dropsOpened), Volatile.Read(ref _lootItems), Volatile.Read(ref _opensBlocked), Volatile.Read(ref _lootPackets));

    // 기능: 연결별 토큰 버킷이 버린 MapMarker 패킷 하나를 센다(Phase 15 D7, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddMarkerDrop() => Interlocked.Increment(ref _markerDrops);
    public long MarkerDrops => Interlocked.Read(ref _markerDrops);

    // 기능: 가득 찬 Marker 채널이 밀어낸 요청 하나를 센다(Phase 15 D7, 수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void AddMarkerInboxDrop() => Interlocked.Increment(ref _markerInboxDrops);
    public long MarkerInboxDrops => Interlocked.Read(ref _markerInboxDrops);

    // 기능: 가득 찬 건설 채널이 밀어낸(DropOldest) 요청 하나를 센다(수신 스레드).
    // 입력: 없음.
    // 출력: 반환값 없음.
    // Build requests the inbound channel dropped (full, DropOldest); written by LiteNetLib threads.
    public void AddBuildInboxDrop() => Interlocked.Increment(ref _buildInboxDrops);
    public long BuildInboxDrops => Interlocked.Read(ref _buildInboxDrops);

    // 기능: 건설 요청 거절 합계를 결과 코드별로 읽는다.
    // 입력: code - 거절 코드(BuildResultCode, Ok는 항상 0).
    // 출력: 시작부터 그 코드로 거절한 수.
    public long BuildRejects(BuildResultCode code) => Volatile.Read(ref _buildRejects[(int)code]);

    // 기능: 연결 요청 거절 합계를 이유별로 읽는다.
    // 입력: reason - 거절 이유.
    // 출력: 시작부터 그 이유로 거절한 수.
    public long Rejects(RejectReason reason) => Interlocked.Read(ref _rejects[(int)reason]);
    // 기능: 서버가 끊은 연결 합계를 코드별로 읽는다.
    // 입력: code - 끊은 이유.
    // 출력: 시작부터 그 코드로 끊은 수.
    public long Kicks(DisconnectCode code) => Interlocked.Read(ref _kicks[(int)code]);
    // 기능: 잘못된 패킷 합계를 이유별로 읽는다.
    // 입력: reason - 잘못된 이유.
    // 출력: 시작부터 그 이유로 센 수.
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
    public long PerIpRejects => Interlocked.Read(ref _perIpRejects);
    public long PenaltyRejects => Interlocked.Read(ref _penaltyRejects);
    public long AcceptRateRejects => Interlocked.Read(ref _acceptRateRejects);
    public long CookieRejects => Interlocked.Read(ref _cookieRejects);
    public long CookieChallenges => Interlocked.Read(ref _cookieChallenges);
    public long Penalties => Interlocked.Read(ref _penalties);
    public long AuthDrops => Interlocked.Read(ref _authDrops);
    public long AuthDropsRetired => Interlocked.Read(ref _authDropsRetired);
    public long InputSeqDrops => Volatile.Read(ref _inputSeqDrops);
    public long StallExits => Interlocked.Read(ref _stallExits);
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);
    public int Peers => Volatile.Read(ref _peers);
    public int Players => Volatile.Read(ref _players);
    public int Graced => Volatile.Read(ref _graced);
    public MatchFlowState MatchState => (MatchFlowState)Volatile.Read(ref _matchState);

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
