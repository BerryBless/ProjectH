# Phase 10 Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 연결 끊김·Timeout·잘못된 패킷·서버 종료·예외 복구·관측을 단단하게 한다.
- 서버가 끊을 때 이유 코드를 보내고, Client와 봇은 다시 해도 되는 끊김에만 자동으로 재접속한다.
- 경기 중 끊긴 참가자는 10초 안에 같은 DevPlayerId로 돌아오면 자기 캐릭터로 돌아간다.
- Join하지 않는 연결과 입력이 끊긴 플레이어는 정해진 시간 뒤 끊긴다.
- 반복되는 Tick 예외는 경기를 초기화하고, 그래도 계속되면 서버를 종료 코드 1로 끝낸다.
- 종료는 Client에 "서버 종료"를 알리고 시간 제한 안에 끝난다.
- 연결·보호·DB 상태가 Health 줄과 `dotnet-counters`로 보인다. Game Loop가 멈추면 Critical 로그가 남는다.
- DB가 서버보다 늦게 떠도 기록이 저장된다.

**Architecture:**
- **Protocol v8(Shared).** `DisconnectCode`(1바이트)를 LiteNetLib 끊기 데이터로 보낸다. `JoinResult.Resumed`를 더한다. 재접속 판단 표 `DisconnectCodes.ShouldReconnect`는 LiteNetLib를 모르는 순수 함수로 Shared Protocol에 둔다. 그래서 Unity Client와 봇이 소스를 링크하지 않고 같은 표를 쓴다.
- **Network 스레드(`NetworkListener`).** 잘못된 패킷과 거절을 이유별로 센다(`HealthCounters`, Interlocked). 받기 핸들러는 try/catch로 감싼다. 서버가 끊을 때는 `PeerState.CloseCode`를 먼저 쓰고 `Disconnect(data)`를 부른다.
- **Game Loop.**
  - Join·Input Timeout을 Loop 자신의 Tick 수로 잰다.
  - 끊긴 peer는 `Match.Disconnect(peerId, allowGrace)`로 넘긴다. 서버가 끊은 연결은 유예가 없다.
  - 반복 전체를 보호한다. Tick이 3초 연속 실패하면 경기를 새로 만들고, 10분 안에 3번이면 `onFatal`을 부른다.
  - 종료 순서는 Tick 멈춤 → `ServerShutdown` 끊기 → 최대 1초 대기 → 소켓 닫기다. 스레드 Join은 5초 제한이다.
- **Match.** 유예 목록(`_graced`, 최대 MaxPlayers)을 둔다. 유예 중인 플레이어의 PeerId는 `NoPeer`이고, 송신 래퍼 한 곳에서 걸러진다. 같은 id로 Join하면 `Resume`이 캐릭터를 새 peer에 묶고 입력 순번을 처음부터 시작한다.
- **관측.**
  - Stats 줄 다음에 Health 줄을 남긴다.
  - `Meter("ProjectH.Server")`의 Observable 계측기로 같은 수치를 낸다.
  - `StallWatchdog`이 1초마다 마지막 Tick 시각을 본다.
  - 콘솔 로그에 시각을 넣는다.
- **DB.** Writer는 시작할 때 DB가 없어도 계속 돈다. 기록마다 스키마 준비부터 다시 시도한다.
- **Client·봇.** 끊기 코드를 읽고, 같은 표로 재접속을 정한다. Client는 1·2·4초 뒤 최대 3번, 봇은 `--reconnect true`일 때 1초 뒤 최대 3번 다시 접속한다.

**Tech Stack:** .NET 10, C# 9(Shared netstandard2.1), LiteNetLib 2.1.4, MySqlConnector 2.6.2, xUnit, `System.Diagnostics.Metrics`, `TimeProvider`. 새 패키지는 없다.

**Spec:** `Docs/specs/2026-10-01-phase10-hardening-design.md`

## Global Constraints

- **Commit:** 작업 Branch(`phase10-hardening`)에서 Task마다 Commit한다. Push는 Phase가 끝난 뒤 `github-push` 스킬로 한다. Force Push는 하지 않는다. `.claude/settings.json`과 `.superpowers`는 Stage하지 않는다.
- **코드 규칙:** 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다.
  - **Lock은 없다.**
    - Network 스레드와 함께 쓰는 값은 `Interlocked`·`Volatile`이다(`HealthCounters`, `PeerState.CloseCode`).
    - 유예 목록과 Timeout 검사는 Game Loop 스레드만 쓴다.
    - Watchdog은 읽기만 한다.
  - **Collection 상한:**
    - `_graced` ≤ MaxPlayers(`_players`의 부분집합)
    - `_timedOut`은 Tick마다 비운다.
    - 초기화 시각은 고정 배열 3칸이다.
  - **Shared(`Shared/Runtime/Protocol`)는 netstandard2.1, C# 9다.** `record`·`init`이 없고 LiteNetLib도 UnityEngine도 쓰지 않는다. 새 `DisconnectCode.cs`의 `.meta`는 Unity가 만든다(직접 만들지 않는다).
- **수치(spec):**
  - `ProtocolVersion` 8
  - `DisconnectCode`: None 0, ServerShutdown 1, Kicked 2, JoinTimeout 3, InputTimeout 4, ServerError 5
  - `JoinResult.Resumed` 3
  - `ReconnectGraceSeconds` 10(0–60, 0은 끔), `JoinTimeoutSeconds` 5(1–60), `InputTimeoutSeconds` 10(0 또는 2–300)
  - 초기화: `SimHz × 3`번 연속 실패. 종료: 10분 안에 초기화 3번 → 종료 코드 1
  - 종료: 스레드 Join 5초, 끊기 통지 최대 1초
  - Watchdog: 1초 간격, 2초 넘게 Tick이 없으면 Stall
  - Client 재접속: 1·2·4초 뒤 최대 3번. 봇: 1초 뒤 최대 3번
- **범위:** spec D12의 항목(IP 제한, 인증, 디스크 보관, HTTP Health, 프로세스 감시자, 게임 UI 끊김 화면)은 하지 않는다. Scene, Prefab, `.meta`는 만들거나 고치지 않는다.
- **Docker:** 띄우거나 내리지 않는다. DB 테스트는 `PROJECTH_TEST_MYSQL`이 있을 때만 돈다.
- **부하 측정 프로세스:** 7790 같은 빈 포트를 쓴다. 자기가 띄운 프로세스는 pid로만 끈다. 이름으로 끄지 않는다(`taskkill /IM`, `pkill -f` 금지).
- **명령 실행 위치:** 저장소 루트(`E:/popol/ProjectH`)에서 실행한다.
  - 시작 기준은 서버 테스트 664개다(658 통과, MySQL 6개 건너뜀).
  - 이 계획의 코드는 계획 단계에서 스크래치 복사본에 그대로 적용했다. 그 상태에서 빌드 경고 0, 아래 Task별 테스트 수를 확인했다.
- **스크립트:** Python 스크립트는 저장소 밖에 저장하고 저장소 경로를 인자로 실행한다. `edit()`는 기준 텍스트가 정확히 한 번 나와야 바꾸고, 파일의 줄바꿈(CRLF/LF)을 지킨다. 실패하면 멈춘다. 그때 파일을 손으로 고치지 말고 원인(앞 단계 누락 등)을 먼저 확인한다.

## Review Focus

- **LiteNetLib의 peer id 재사용.** 유예 중인 캐릭터의 패킷이 같은 id를 받은 새 연결로 가면 안 된다. 그래서 PeerId를 `NoPeer`로 바꾸고 `Match._send` 래퍼 한 곳에서 거른다(Task 2 `ADroppedParticipant_StaysInTheWorld_WithoutADespawn`).
- **Resume 뒤의 입력.** 새 연결은 Seq를 1부터 보낸다. 서버의 입력 상태(`PlayerInputBuffer.Reset`, `LastProcessedSeq`)가 초기화되지 않으면 캐릭터가 얼어붙는다(Task 2 `JoiningAgain_ResumesTheSameCharacter_WithTheFullState`, `ACrashedClient_RejoinsItsCharacter_...`).
- **Kick된 연결이 유예를 받는 경우.** `CloseCode`는 `Disconnect` 호출 **전에** 기록한다. UnsyncedEvents에서는 `OnPeerDisconnected`가 `Disconnect` 안에서 불릴 수 있기 때문이다(Task 2 `AKickedClient_GetsNoGrace`).
- **유예 중 사망·경기 종료·판 재시작.** 유예 중인 플레이어를 `_players` 순회 안에서 지우면 안 된다. 그래서 `Tick` 맨 앞의 `ExpireGrace`와 `CloseRound`에서만 지운다(Task 2 `KilledWhileAway_ComesBackAsASpectator`, `AMatchThatEndsDuringTheGrace_...`).
- **경기 초기화와 서버 종료 판단.** 성공한 Tick 하나가 연속 실패 수를 0으로 돌려야 한다. 10분 창은 주입한 시계로 잰다(Task 3 `FailingForThreeSeconds_...`, `ThreeResetsWithinTenMinutes_StopTheServer`).
- **멈춘 Game Loop가 종료를 막는 경우.** `Stop`은 제한 시간 안에 돌아와야 한다. 스레드는 Background라 프로세스 종료를 막지 않는다(Task 3 `Stop_ReturnsWithinItsLimit_WhenTheLoopIsStuck`).
- **죽은 봇·관전자의 Input Timeout.** Unity Client는 죽어 있어도 빈 입력을 보낸다. 봇도 같게 바꾼다. 그러지 않으면 배틀로얄 부하 측정에서 죽은 봇이 10초 뒤 끊긴다(Task 1 `BotBrain`).
- **DB가 서버보다 늦게 뜨는 경우.** 기록마다 스키마 준비를 다시 시도한다. DB가 없을 때의 기록은 `Failed`로 센다(Task 3 `TheWriter_WithNoDatabase_CountsEachRecordAsFailed_AndKeepsRunning`, `[MySqlFact] TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears`).

## Spec 해석

1. **재접속 판단 표의 위치.** spec §2는 "Client 소스 링크로 시험"이라고 했다. 이 계획은 표를 Shared Protocol(`DisconnectCodes.ShouldReconnect`)에 둔다.
   - 봇이 Client 소스를 링크하면 CS0436이 난다(Phase 7). 봇은 Client 코드를 링크하지 않는다는 규칙도 있다(`game-core-rules`).
   - 표는 LiteNetLib를 모르는 `DisconnectCode` 해석의 일부(프로토콜 수준)다.
   - 입력은 둘이다. 원격 종료인지와 그 코드, 그리고 네트워크 손실(Timeout·ConnectionFailed·Unreachable)인지다. LiteNetLib `DisconnectReason`을 이 두 값으로 바꾸는 일은 `NetClient`와 `BotConnection`이 각자 한다(각 4줄).
2. **"재접속 사이클을 시작해도 되는가"는 표 밖에 둔다.** 사용자가 처음 누른 Connect가 실패하면 다시 시도하지 않는다. 연결된 적이 있는 연결이 끊겼거나, 진행 중인 사이클의 시도가 실패한 경우만 다음 시도를 한다(`GameClient._established`, `BotRunner._established`). 그래서 네트워크가 아직 없을 때 `ConnectionFailed`로 끝난 시도도 다음 시도로 이어진다.
3. **유예 중에 죽은 플레이어.** 다음 Tick의 `ExpireGrace`에서 바로 나간다. 순위·Drop·기록은 사망 때 이미 정해졌다. 다시 오면 보통의 늦은 합류(관전자)다. spec D2 "다시 오면 관전자다"를 이렇게 구현한다. 죽은 캐릭터를 다시 묶지 않는다.
4. **경기가 끝난 뒤의 유예 플레이어.** 결과 화면(`Finished`) 동안은 유예가 이어진다(돌아오면 결과 화면을 본다). 판 재시작(`CloseRound`)에서 모두 지운다.
5. **Timeout은 Game Loop의 Tick 수로 잰다.** `Match.ServerTick`은 경기 초기화 때 0으로 돌아가기 때문이다.
6. **`Kick(코드별)` 카운터**는 서버가 끊은 모든 코드(Kicked, JoinTimeout, InputTimeout, ServerError)를 센다. 로그는 다음과 같다.
   - Kicked: Warning
   - JoinTimeout·InputTimeout: Information(연결마다 한 번)
   - 경기 초기화: Error 한 줄
7. **Health 줄의 값은 시작 이후 누적값이다.** 구간 값은 Stats 줄에 있다.
8. **"무작위 바이트 10만 개"는 10만 개의 무작위 버퍼로 해석한다.** 길이는 대부분 0–64바이트이고, 100개마다 하나는 1300바이트까지다. 각 버퍼를 서버 파서 전부와 Client 파서 전부(ID 바이트 뒤 본문 포함)에 넣는다.

## Spec과 다른 점

계획 단계의 프로토타입에서 spec과 다르게 정한 것이다. 컨트롤러가 spec에 반영한다.

1. **Meter 등록(spec §1 `Program`의 "Meter 등록").** DI에 등록하지 않고 `GameServerService`가 `ServerMeter`를 만들고 버린다.
   - Meter가 읽는 `HealthCounters`는 `GameLoop`가 소유한다.
   - `dotnet-counters`는 DI와 관계없이 프로세스의 모든 `Meter`를 본다.
   - `Program`은 처리되지 않은 예외 로그만 더한다.
2. **Game Loop 스레드를 Background로 바꾼다.** spec에 없는 변경이다. Foreground 스레드가 멈추면 Host가 끝나도 프로세스가 끝나지 않는다. 그러면 D7의 "5초 제한"이 의미가 없다. `Stop`은 여전히 먼저 Join한다.
3. **봇은 죽어 있어도 빈 입력을 보낸다(D4 보완).**
   - D4는 "Client는 살았든 죽었든 매 Tick 입력을 보낸다"를 전제로 한다. Unity Client는 그렇지만 봇은 죽으면 입력을 멈췄다(Phase 7).
   - 바꾸지 않으면 배틀로얄에서 죽은 봇이 `InputTimeout`으로 끊긴다.
   - `DevRespawn` 부하 측정에서는 죽은 3초 동안의 빈 입력만큼 `pktIn/s`가 조금 늘 수 있다.
4. **DB가 없을 때의 집계(D8).** Phase 9에서는 DB 없이 시작하면 기록을 `Discarded`로 셌다. 이제는 기록마다 시도하므로 `Failed`로 센다(spec §2 "DB 없이는 기록마다 실패로 센다"와 같다). `Persistence:Enabled=false`일 때만 `Discarded`다. 시작할 때의 연결 실패 로그는 Error에서 Warning으로 바꾼다(계속 시도하므로).
5. **`DisconnectCode` 읽기 API.** spec §1의 `TryRead(...)` 대신 `DisconnectCodes.Read(ReadOnlySpan<byte>) → DisconnectCode`다. 실패가 없다(빈 데이터·모르는 값은 `None`). 데이터가 2바이트 이상이면 첫 바이트만 쓴다.
6. **원격 종료인데 코드가 없는 경우(`None`).** 재접속하지 않는다. 옛 서버, 그리고 Control 채널이 가득 차서 코드 없이 끊은 경우다. spec D10의 목록(Timeout·네트워크 오류·ServerError만 재접속)을 그대로 따른 결과다.

---

### Task 1: Protocol v8, 끊는 이유 코드, Join·Input Timeout, 잘못된 패킷 이유별 집계

**Files:**
- Create: `Shared/Runtime/Protocol/DisconnectCode.cs`
- Modify: `Shared/Runtime/Protocol/ProtocolConstants.cs`, `RejectReason.cs`
- Create: `Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs`
- Modify: `Server/src/ProjectH.Server/ServerOptions.cs`, `Net/PeerState.cs`, `Net/NetworkListener.cs`, `GameLoop.cs`
- Modify: `Server/src/ProjectH.Bots/BotBrain.cs`(죽어 있어도 빈 입력)
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Shared/DisconnectCodeTests.cs`, `Integration/HardeningIntegrationTests.cs`
  - Modify: `ServerOptionsTests.cs`, `Shared/ProtocolConstantsTests.cs`, `Integration/HeadlessClient.cs`, `Integration/ServerIntegrationTests.cs`, `Bots/BotBrainTests.cs`

**Interfaces:**
- Produces:
  - Shared:
    - `enum DisconnectCode : byte`
    - `static class DisconnectCodes`: `MaxReconnectAttempts`(3), `Read(ReadOnlySpan<byte>)`, `ShouldReconnect(bool remoteClose, DisconnectCode code, bool networkLoss)`
    - `JoinResult.Resumed`
    - `ProtocolConstants.ProtocolVersion` 8
  - `ServerOptions`: `ReconnectGraceSeconds`, `JoinTimeoutSeconds`, `InputTimeoutSeconds`
  - `enum BadPacketReason`(Diagnostics)
  - `HealthCounters`:
    - `Add*`
    - 읽기: `Rejects(RejectReason)`, `Kicks(DisconnectCode)`, `BadPackets(BadPacketReason)`, `Connections`, `Joins`, `Resumes`, `GraceStarts`, `DisconnectTimeouts`, `DisconnectOthers`, `TickFailures`, `LoopFailures`, `MatchResets`, `Stalls`, `Peers`, `Players`, `Graced`, `MatchState`, `SetGauges(...)`
  - `PeerState`:
    - Game Loop 전용: `ConnectedTick`, `Joined`, `LastInputTick`
    - `CloseCode`, `TrySetCloseCode`
  - `NetworkListener`:
    - 생성자 `(ServerOptions, InboundChannels, ServerStats, HealthCounters, ILogger)`
    - `static DataOf(DisconnectCode)`, `static Close(NetPeer, DisconnectCode)`, `ResetLogLimits()`
    - internal `ReceiveFaultHook`
  - `GameLoop`: `Health`, internal `Listener`
  - 테스트: `HeadlessClient.DisconnectCode`, `HeadlessClient.ConnectRaw(int, byte[])`, `HardeningIntegrationTests.StartServer(...)`·`Join(...)`·`SendInputsUntil(...)`(internal static, 뒤 Task가 쓴다)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/HardeningIntegrationTests.cs`:

```csharp
using System;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D1, D3-D5 over real UDP: the server says why it closes a connection, times out connections that never
// join and players that stop sending input, and counts invalid packets and rejects by reason.
public sealed class HardeningIntegrationTests
{
    internal static GameLoop StartServer(int maxPlayers = 4, int joinTimeout = 5, int inputTimeout = 10, int grace = 10,
        int minPlayers = 2, int countdown = 10)
    {
        var loop = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = maxPlayers,
            MinPlayers = minPlayers,
            StartCountdownSeconds = countdown,
            ResultSeconds = 1,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            JoinTimeoutSeconds = joinTimeout,
            InputTimeoutSeconds = inputTimeout,
            ReconnectGraceSeconds = grace,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        loop.Start();
        return loop;
    }

    internal static HeadlessClient Join(GameLoop server, string devId, JoinResult expected = JoinResult.Ok)
    {
        var client = new HeadlessClient();
        client.Connect(server.LocalPort, devId);
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join response");
        Assert.Equal(expected, client.JoinResponse?.Result);
        return client;
    }

    [Fact]
    public void AConnectionThatNeverJoins_IsClosedWithJoinTimeout()
    {
        using GameLoop server = StartServer(joinTimeout: 1);
        using var idle = new HeadlessClient();
        idle.Connect(server.LocalPort, "idle");
        Assert.True(Pump.Until(() => idle.Connected, 3000, idle), "connect");

        Assert.True(Pump.Until(() => idle.Disconnected, 4000, idle), "timed out");
        Assert.Equal(DisconnectReason.RemoteConnectionClose, idle.DisconnectReason);
        Assert.Equal(DisconnectCode.JoinTimeout, idle.DisconnectCode);
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.JoinTimeout));
    }

    [Fact]
    public void APlayerThatStopsSendingInput_IsClosedWithInputTimeout_AndOneThatSendsIsNot()
    {
        using GameLoop server = StartServer(inputTimeout: 2);
        using var silent = Join(server, "silent");
        using var active = Join(server, "active");

        Assert.True(SendInputsUntil(active, () => silent.Disconnected, 5000, silent), "silent player timed out");
        Assert.Equal(DisconnectCode.InputTimeout, silent.DisconnectCode);

        // The active player keeps going well past the timeout.
        Assert.False(SendInputsUntil(active, () => active.Disconnected, 2500), "active player kept");
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.InputTimeout));
    }

    // Sends one input about every tick (well below the 60/s cap) while polling, until the condition holds.
    internal static bool SendInputsUntil(HeadlessClient sender, Func<bool> condition, int timeoutMs, params HeadlessClient[] others)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            sender.SendMove(0f, 0f, 0f);
            if (Pump.Until(condition, 33, [sender, .. others])) return true;
        }
        return condition();
    }

    [Fact]
    public void InputTimeoutZero_NeverClosesASilentPlayer()
    {
        using GameLoop server = StartServer(inputTimeout: 0);
        using var silent = Join(server, "silent");
        Assert.False(Pump.Until(() => silent.Disconnected, 3000, silent), "kept");
        Assert.Equal(0, server.Health.Kicks(DisconnectCode.InputTimeout));
    }

    [Fact]
    public void AKick_CarriesTheKickedCode_AndCountsTheReason()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        for (int i = 0; i < 25; i++) a.SendRaw(new byte[] { 0xFF, 0x01 });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a, b), "kicked");
        Assert.Equal(DisconnectCode.Kicked, a.DisconnectCode);
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.Kicked));
        Assert.True(server.Health.BadPackets(BadPacketReason.UnknownId) >= 20);
        Assert.False(b.Disconnected);
    }

    [Fact]
    public void InvalidPackets_AreCountedByReason()
    {
        using GameLoop server = StartServer();
        HealthCounters health = server.Health;
        using var c = new HeadlessClient();
        c.Connect(server.LocalPort, "c");
        Assert.True(Pump.Until(() => c.Connected, 3000, c), "connect");

        c.SendMove(0f, 1f, 0f);
        Assert.True(Pump.Until(() => health.BadPackets(BadPacketReason.InputBeforeJoin) == 1, 3000, c), "input before join");

        c.SendJoin();
        Assert.True(Pump.Until(() => c.JoinResponse.HasValue, 3000, c), "join");
        c.SendJoin();
        c.SendRaw(new byte[] { (byte)PacketId.WorldSnapshot });
        c.SendRaw(new byte[] { (byte)PacketId.PlayerSpawned, 1, 2 });
        c.SendRaw(new byte[] { 0 });
        c.SendRaw(new byte[] { (byte)PacketId.PlayerInput, 0 });   // zero inputs: malformed
        Assert.True(Pump.Until(() =>
            health.BadPackets(BadPacketReason.DuplicateJoin) == 1 &&
            health.BadPackets(BadPacketReason.WrongDirection) == 2 &&
            health.BadPackets(BadPacketReason.UnknownId) == 1 &&
            health.BadPackets(BadPacketReason.Malformed) == 1, 3000, c), "reasons");

        // Above SimHz * 2 = 60 inputs in one second: the rest count as InputRate (below the kick threshold of 20).
        for (int i = 0; i < 70; i++) c.SendMove(0f, 0f, 0f);
        Assert.True(Pump.Until(() => health.BadPackets(BadPacketReason.InputRate) >= 5, 3000, c), "rate");
        Assert.False(c.Disconnected);
    }

    [Fact]
    public void AThrowingReceiveHandler_CountsAgainstThePeer_AndTheServerGoesOn()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        server.Listener.ReceiveFaultHook = () => throw new InvalidOperationException("test fault");
        a.SendMove(0f, 0f, 0f);
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.HandlerException) >= 1, 3000, a), "counted");
        server.Listener.ReceiveFaultHook = null;

        int before = a.SnapshotsReceived;
        Assert.True(Pump.Until(() => a.SnapshotsReceived > before + 3, 3000, a), "server keeps ticking");
        Assert.False(a.Disconnected);
    }

    [Fact]
    public void Rejects_AreCountedByReason()
    {
        using GameLoop server = StartServer(maxPlayers: 2);
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        using var full = new HeadlessClient();
        full.Connect(server.LocalPort, "full");
        using var old = new HeadlessClient();
        old.Connect(server.LocalPort, "old", protocolVersion: 7);
        using var bad = new HeadlessClient();
        bad.ConnectRaw(server.LocalPort, Array.Empty<byte>());
        Assert.True(Pump.Until(() => full.Disconnected && old.Disconnected && bad.Disconnected, 3000, full, old, bad, a, b), "rejected");

        // The server is full, so all three are refused as ServerFull before their data is read.
        Assert.Equal(RejectReason.ServerFull, old.RejectReason);
        Assert.Equal(3, server.Health.Rejects(RejectReason.ServerFull));
        Assert.Equal(0, server.Health.Rejects(RejectReason.VersionMismatch));
    }

    [Fact]
    public void Rejects_ByVersionAndBadRequest_AreCountedApart()
    {
        using GameLoop server = StartServer();
        using var old = new HeadlessClient();
        old.Connect(server.LocalPort, "old", protocolVersion: 7);
        using var bad = new HeadlessClient();
        bad.ConnectRaw(server.LocalPort, Array.Empty<byte>());
        Assert.True(Pump.Until(() => old.Disconnected && bad.Disconnected, 3000, old, bad), "rejected");
        Assert.Equal(RejectReason.VersionMismatch, old.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.VersionMismatch));
        Assert.Equal(1, server.Health.Rejects(RejectReason.BadRequest));
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/DisconnectCodeTests.cs`:

```csharp
using System;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 10 D1, D10: the disconnect code on the wire and the reconnect table the client and the bots share.
public class DisconnectCodeTests
{
    [Fact]
    public void Read_EmptyOrUnknownData_IsNone()
    {
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(ReadOnlySpan<byte>.Empty));
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(new byte[] { 6 }));
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(new byte[] { 255 }));
    }

    [Theory]
    [InlineData(DisconnectCode.ServerShutdown)]
    [InlineData(DisconnectCode.Kicked)]
    [InlineData(DisconnectCode.JoinTimeout)]
    [InlineData(DisconnectCode.InputTimeout)]
    [InlineData(DisconnectCode.ServerError)]
    public void Read_KnownCodes_RoundTrip(DisconnectCode code)
    {
        Assert.Equal(code, DisconnectCodes.Read(new[] { (byte)code }));
        Assert.Equal(code, DisconnectCodes.Read(new[] { (byte)code, (byte)99 }));   // only the first byte counts
    }

    // D10: retry a lost connection and a match reset; never a server decision that would repeat, a local disconnect
    // or a reject.
    [Theory]
    [InlineData(true, DisconnectCode.ServerError, false, true)]
    [InlineData(true, DisconnectCode.ServerShutdown, false, false)]
    [InlineData(true, DisconnectCode.Kicked, false, false)]
    [InlineData(true, DisconnectCode.JoinTimeout, false, false)]
    [InlineData(true, DisconnectCode.InputTimeout, false, false)]
    [InlineData(true, DisconnectCode.None, false, false)]
    [InlineData(false, DisconnectCode.None, true, true)]    // Timeout, ConnectionFailed, unreachable
    [InlineData(false, DisconnectCode.None, false, false)]  // Disconnect() called locally, or rejected
    public void ShouldReconnect_FollowsTheTable(bool remoteClose, DisconnectCode code, bool networkLoss, bool expected)
    {
        Assert.Equal(expected, DisconnectCodes.ShouldReconnect(remoteClose, code, networkLoss));
    }

    [Fact]
    public void JoinResultResumed_IsThree()
    {
        Assert.Equal(3, (byte)JoinResult.Resumed);
        Assert.Equal(3, DisconnectCodes.MaxReconnectAttempts);
    }
}
```

**전체 교체** `Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs`:

```csharp
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotPacket_FitsInOneDatagram_AndTheMatchFitsInTheParts()
    {
        // Phase 8: header 13 + self block 6, then 13 bytes per entity; 100 players in 2 packets of at most 90.
        Assert.Equal(19, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(13, SnapshotEntity.Size);
        Assert.Equal(100, ProtocolConstants.MaxSnapshotEntities);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxEntitiesPerSnapshotPacket * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(WorldSnapshotHeader.Size + (ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1) * SnapshotEntity.Size > ProtocolConstants.MaxPacketSize);
        Assert.Equal(2, ProtocolConstants.MaxSnapshotParts);
        Assert.True(ProtocolConstants.MaxSnapshotParts * ProtocolConstants.MaxEntitiesPerSnapshotPacket >= ProtocolConstants.MaxSnapshotEntities);
    }

    [Fact]
    public void ProtocolVersion_IsEight()
    {
        // Phase 10 added the disconnect codes and JoinResult.Resumed; v7 clients must be rejected at connect.
        Assert.Equal((ushort)8, ProtocolConstants.ProtocolVersion);
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t1_tests.py`로 저장하고 `python <경로>/p10_t1_tests.py E:/popol/ProjectH`로 실행한다. `BotBrainTests.cs`, `HeadlessClient.cs`, `ServerIntegrationTests.cs`, `ServerOptionsTests.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/tests/ProjectH.Server.Tests/Bots/BotBrainTests.cs', [
(r"""    public void NotJoined_NoSnapshot_OrDead_SendsNothing()
""",
 r"""    public void NotJoined_OrNoSnapshot_SendsNothing_AndDead_SendsAnEmptyInput()
"""),
(r"""        view = Create();
        view.Alive = false;
        Assert.False(brain.Tick(view, 0f, out _));
""",
 r"""        // Phase 10 D4: dead (or spectating) the bot still sends, as the client does, so it is not closed by the input
        // timeout; the input moves and fires nothing.
        view = Create();
        view.Alive = false;
        Assert.True(brain.Tick(view, 0f, out InputCommand dead));
        Assert.Equal(0f, dead.MoveX);
        Assert.Equal(0f, dead.MoveY);
        Assert.Equal(InputButtons.None, dead.Buttons);
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs', [
(r"""            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                RejectReason = (RejectReason)info.AdditionalData.GetByte();
""",
 r"""            // Phase 10 D1: a reject carries a RejectReason, a server close a DisconnectCode, both as one byte.
            if (info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected && info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                RejectReason = (RejectReason)info.AdditionalData.GetByte();
            if (info.Reason == LiteNetLib.DisconnectReason.RemoteConnectionClose && info.AdditionalData != null)
                DisconnectCode = DisconnectCodes.Read(info.AdditionalData.GetRemainingBytesSpan());
"""),
(r"""    public RejectReason RejectReason { get; private set; }
""",
 r"""    public RejectReason RejectReason { get; private set; }
    public DisconnectCode DisconnectCode { get; private set; }
"""),
(r"""        data.Put(_buffer, 0, writer.Length);
""",
 r"""        data.Put(_buffer, 0, writer.Length);
        _peer = _net.Connect("127.0.0.1", port, data);
    }

    // A connect request with arbitrary payload (Phase 10: malformed requests are rejected and counted).
    public void ConnectRaw(int port, byte[] payload)
    {
        var data = new NetDataWriter();
        data.Put(payload);
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Integration/ServerIntegrationTests.cs', [
(r"""            StatsIntervalSeconds = 60,
""",
 r"""            StatsIntervalSeconds = 60,
            // Phase 10 D3: FullMatch connects 100 clients before any of them joins; on a slow machine that can take
            // longer than the 5 s default.
            JoinTimeoutSeconds = 30,
"""),
])

edit('Server/tests/ProjectH.Server.Tests/ServerOptionsTests.cs', [
(r"""        Assert.Null(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }
}
""",
 r"""        Assert.Null(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }

    // Phase 10 spec §1: reconnect grace 10 s (0-60, 0 = off), join timeout 5 s (1-60), input timeout 10 s (0 or 2-300).
    [Fact]
    public void HardeningDefaults_MatchSpec()
    {
        var options = new ServerOptions();
        Assert.Equal(10, options.ReconnectGraceSeconds);
        Assert.Equal(5, options.JoinTimeoutSeconds);
        Assert.Equal(10, options.InputTimeoutSeconds);
        Assert.Null(new ServerOptions { ReconnectGraceSeconds = 0, InputTimeoutSeconds = 0 }.Validate());
        Assert.Null(new ServerOptions { ReconnectGraceSeconds = 60, JoinTimeoutSeconds = 60, InputTimeoutSeconds = 300 }.Validate());
        Assert.Null(new ServerOptions { JoinTimeoutSeconds = 1, InputTimeoutSeconds = 2 }.Validate());
    }

    [Theory]
    [InlineData(-1, 5, 10)]
    [InlineData(61, 5, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 61, 10)]
    [InlineData(10, 5, 1)]
    [InlineData(10, 5, -1)]
    [InlineData(10, 5, 301)]
    public void Validate_RejectsBadHardeningSettings(int grace, int joinTimeout, int inputTimeout)
    {
        Assert.NotNull(new ServerOptions
        {
            ReconnectGraceSeconds = grace, JoinTimeoutSeconds = joinTimeout, InputTimeoutSeconds = inputTimeout,
        }.Validate());
    }
}
"""),
])
print('p10_t1_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `DisconnectCode`가 없다(CS0246, CS0103). 계획 단계에서 오류는 이 두 종류뿐이었다.

- [ ] **Step 3: Protocol·Server·봇을 구현한다**

서버가 끊을 때는 항상 `NetworkListener.Close(peer, code)`를 쓴다. 이 함수는 `PeerState.TrySetCloseCode`를 먼저 하고 `peer.Disconnect(data)`를 부른다(Review Focus). `GameLoop.SweepPeers`는 stale peer 정리와 두 Timeout을 한 번의 순회로 한다(최대 MaxPlayers, 할당 없음).

**새 파일** `Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs`:

```csharp
using System;
using System.Threading;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D5: why a packet counted as invalid. Index into HealthCounters' bad-packet array; keep Count last.
public enum BadPacketReason
{
    UnknownId,        // empty, or a first byte that is no PacketId
    Malformed,        // a known id whose body does not parse
    InputBeforeJoin,
    DuplicateJoin,
    InputRate,        // above ServerOptions.MaxInputPacketsPerSecond
    WrongDirection,   // a server-to-client packet id
    HandlerException, // the receive handler threw (a server bug, counted against the peer)
    Count,
}

// Phase 10 D9: totals since the server started, for the Health line and the "ProjectH.Server" Meter. Written from
// LiteNetLib's threads and the game loop, read by the game loop (Health line) and by the Meter's observers on
// whatever thread polls them. Interlocked/Volatile only, no lock: each value is independent, so a slightly
// inconsistent view across values is acceptable for monitoring.
public sealed class HealthCounters
{
    private const int RejectSlots = (int)RejectReason.BadRequest + 1;
    private const int CodeSlots = (int)DisconnectCode.ServerError + 1;

    private readonly long[] _rejects = new long[RejectSlots];
    private readonly long[] _kicks = new long[CodeSlots];
    private readonly long[] _badPackets = new long[(int)BadPacketReason.Count];
    private long _connections;
    private long _joins;
    private long _resumes;
    private long _graceStarts;
    private long _disconnectTimeouts;
    private long _disconnectOthers;
    private long _tickFailures;
    private long _loopFailures;
    private long _matchResets;
    private long _stalls;
    // Gauges, written by the game loop once per tick.
    private int _peers;
    private int _players;
    private int _graced;
    private int _matchState;

    public void AddReject(RejectReason reason) => Interlocked.Increment(ref _rejects[(int)reason]);
    public void AddKick(DisconnectCode code) => Interlocked.Increment(ref _kicks[(int)code]);
    public void AddBadPacket(BadPacketReason reason) => Interlocked.Increment(ref _badPackets[(int)reason]);
    public void AddConnection() => Interlocked.Increment(ref _connections);
    public void AddJoin() => Interlocked.Increment(ref _joins);
    public void AddResume() => Interlocked.Increment(ref _resumes);
    public void AddGraceStart() => Interlocked.Increment(ref _graceStarts);
    public void AddDisconnect(bool timeout) => Interlocked.Increment(ref timeout ? ref _disconnectTimeouts : ref _disconnectOthers);
    public void AddTickFailure() => Interlocked.Increment(ref _tickFailures);
    public void AddLoopFailure() => Interlocked.Increment(ref _loopFailures);
    public void AddMatchReset() => Interlocked.Increment(ref _matchResets);
    public void AddStall() => Interlocked.Increment(ref _stalls);

    public void SetGauges(int peers, int players, int graced, MatchFlowState state)
    {
        Volatile.Write(ref _peers, peers);
        Volatile.Write(ref _players, players);
        Volatile.Write(ref _graced, graced);
        Volatile.Write(ref _matchState, (int)state);
    }

    public long Rejects(RejectReason reason) => Interlocked.Read(ref _rejects[(int)reason]);
    public long Kicks(DisconnectCode code) => Interlocked.Read(ref _kicks[(int)code]);
    public long BadPackets(BadPacketReason reason) => Interlocked.Read(ref _badPackets[(int)reason]);
    public long Connections => Interlocked.Read(ref _connections);
    public long Joins => Interlocked.Read(ref _joins);
    public long Resumes => Interlocked.Read(ref _resumes);
    public long GraceStarts => Interlocked.Read(ref _graceStarts);
    public long DisconnectTimeouts => Interlocked.Read(ref _disconnectTimeouts);
    public long DisconnectOthers => Interlocked.Read(ref _disconnectOthers);
    public long TickFailures => Interlocked.Read(ref _tickFailures);
    public long LoopFailures => Interlocked.Read(ref _loopFailures);
    public long MatchResets => Interlocked.Read(ref _matchResets);
    public long Stalls => Interlocked.Read(ref _stalls);
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
```

**새 파일** `Shared/Runtime/Protocol/DisconnectCode.cs`:

```csharp
using System;

namespace ProjectH.Shared.Protocol
{
    // Phase 10 D1: why the server closed a connection. Sent as the single byte of LiteNetLib's disconnect data, so it
    // arrives with the disconnect itself (a separate packet could arrive after it or be lost). Keep values stable.
    public enum DisconnectCode : byte
    {
        None = 0,
        ServerShutdown = 1,
        Kicked = 2,         // too many invalid packets
        JoinTimeout = 3,    // connected but never sent a JoinMatchRequest
        InputTimeout = 4,   // joined but sent no PlayerInput for too long
        ServerError = 5,    // the match was reset after repeated tick failures (D6)
    }

    // Pure helpers shared by the Unity client and the bots. No LiteNetLib types here: each caller maps its own
    // DisconnectReason to the two flags below, so neither has to link the other's sources.
    public static class DisconnectCodes
    {
        // Phase 10 D10, D11: attempts per disconnect (1 s, 2 s, 4 s apart on the client: all inside the server's 10 s grace).
        public const int MaxReconnectAttempts = 3;

        // The disconnect data of a remote close. Empty data or a value this build does not know is None, so an older
        // client reading a newer server's code never breaks.
        public static DisconnectCode Read(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return DisconnectCode.None;
            byte value = data[0];
            return value <= (byte)DisconnectCode.ServerError ? (DisconnectCode)value : DisconnectCode.None;
        }

        // D10: may the client connect again on its own?
        //   remoteClose: the server closed the connection (LiteNetLib RemoteConnectionClose); code is what it sent.
        //   networkLoss: the connection was lost or could not be made (Timeout, ConnectionFailed, Host/NetworkUnreachable).
        // Only ServerError among the server's codes is worth a retry: a shutdown, a kick or a timeout would happen
        // again. A local Disconnect() or a rejected connect is neither flag, so it never retries.
        // The caller decides when a cycle may start at all: a first, manual connect that fails is not retried.
        public static bool ShouldReconnect(bool remoteClose, DisconnectCode code, bool networkLoss)
        {
            if (remoteClose) return code == DisconnectCode.ServerError;
            return networkLoss;
        }
    }
}
```

**전체 교체** `Server/src/ProjectH.Server/Net/NetworkListener.cs`:

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Runs on LiteNetLib's threads (UnsyncedEvents). Validates and parses packets, then hands plain
// structs to the game loop through InboundChannels. Never touches Match or any game state.
public sealed class NetworkListener : INetEventListener
{
    private static readonly byte[] RejectVersionMismatch = { (byte)RejectReason.VersionMismatch };
    private static readonly byte[] RejectServerFull = { (byte)RejectReason.ServerFull };
    private static readonly byte[] RejectBadRequest = { (byte)RejectReason.BadRequest };
    // Phase 10 D1: one shared array per code; LiteNetLib copies the data into its own packet.
    private static readonly byte[][] CloseData =
    {
        new[] { (byte)DisconnectCode.None },
        new[] { (byte)DisconnectCode.ServerShutdown },
        new[] { (byte)DisconnectCode.Kicked },
        new[] { (byte)DisconnectCode.JoinTimeout },
        new[] { (byte)DisconnectCode.InputTimeout },
        new[] { (byte)DisconnectCode.ServerError },
    };

    private readonly ServerOptions _options;
    private readonly InboundChannels _channels;
    private readonly ServerStats _stats;
    private readonly HealthCounters _health;
    private readonly ILogger _logger;
    // 1 once a receive-handler exception was logged in the current stats interval (D5: the first one only).
    private int _handlerErrorLogged;

    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, HealthCounters health, ILogger logger)
    {
        _options = options;
        _channels = channels;
        _stats = stats;
        _health = health;
        _logger = logger;
    }

    // Set once by GameLoop right after creating the NetManager (the two reference each other).
    public NetManager Manager { get; set; } = null!;

    // The disconnect data that carries code (D1).
    public static byte[] DataOf(DisconnectCode code) => CloseData[(int)code];

    // Phase 10 D1: every server-side close of one peer goes through here. The code is stored before Disconnect is
    // called (see PeerState.CloseCode). Safe from any thread: NetPeer.Disconnect is thread-safe.
    public static void Close(NetPeer peer, DisconnectCode code)
    {
        if (peer.Tag is PeerState state) state.TrySetCloseCode(code);
        peer.Disconnect(DataOf(code));
    }

    // Called by the game loop at each stats line: the next receive-handler exception is logged again.
    public void ResetLogLimits() => Volatile.Write(ref _handlerErrorLogged, 0);

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (Manager.ConnectedPeersCount >= _options.MaxPlayers)
        {
            Reject(request, RejectReason.ServerFull, RejectServerFull);
            return;
        }

        var data = request.Data;
        if (data == null || data.AvailableBytes == 0)
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }

        var reader = new PacketReader(new ReadOnlySpan<byte>(data.RawData, data.Position, data.AvailableBytes));
        if (!ConnectRequestData.TryRead(ref reader, out var connect))
        {
            Reject(request, RejectReason.BadRequest, RejectBadRequest);
            return;
        }
        if (connect.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            Reject(request, RejectReason.VersionMismatch, RejectVersionMismatch);
            return;
        }

        NetPeer peer = request.Accept();
        peer.Tag = new PeerState(connect.DevPlayerId);
        _health.AddConnection();
        _logger.LogInformation("Peer {PeerId} ({DevPlayerId}) connected from {EndPoint}", peer.Id, connect.DevPlayerId, request.RemoteEndPoint);

        // Connected is announced here, not in OnPeerConnected: with UnsyncedEvents LiteNetLib raises
        // OnPeerConnected synchronously inside Accept(), before Tag is assigned above. Writing after
        // the Tag assignment guarantees the game loop never sees a peer without its PeerState.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Connected, peer.Id, peer, connect.DevPlayerId)))
        {
            _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
            peer.Disconnect();
        }
    }

    // D5: rejects are counted by reason and logged at Debug only, because a flood of connection requests must not
    // flood the log.
    private void Reject(ConnectionRequest request, RejectReason reason, byte[] data)
    {
        _health.AddReject(reason);
        _logger.LogDebug("Rejected connection from {EndPoint}: {Reason}", request.RemoteEndPoint, reason);
        request.Reject(data);
    }

    public void OnPeerConnected(NetPeer peer)
    {
        // Runs inside request.Accept() (see OnConnectionRequest); peer.Tag is not set yet here.
        // The server never connects out, so there is nothing else to handle.
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _health.AddDisconnect(disconnectInfo.Reason == DisconnectReason.Timeout);
        if (peer.Tag is PeerState state)
        {
            _logger.LogInformation("Peer {PeerId} ({DevPlayerId}) disconnected: {Reason} (server code {Code})",
                peer.Id, state.DevPlayerId, disconnectInfo.Reason, state.CloseCode);
        }
        // If this message is lost the session is still removed: the game loop also drops
        // sessions whose peer is no longer Connected.
        if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.Disconnected, peer.Id, peer, null)))
            _logger.LogWarning("Control channel full; disconnect of peer {PeerId} will be detected by the stale-peer sweep", peer.Id);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // D5: nothing a packet does may escape into LiteNetLib's thread, which serves every connection. A throw is a
        // server bug; it is counted against this peer like an invalid packet and logged once per stats interval.
        try
        {
            Receive(peer, reader);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _handlerErrorLogged, 1) == 0)
                _logger.LogError(ex, "Exception while handling a packet from peer {PeerId}", peer.Id);
            OnBadPacket(peer, BadPacketReason.HandlerException);
        }
    }

    // Test seam (D5): a hook that runs at the start of every receive, so a test can make the handler throw.
    internal Action? ReceiveFaultHook { get; set; }

    private void Receive(NetPeer peer, NetPacketReader reader)
    {
        ReceiveFaultHook?.Invoke();
        // AutoRecycle is on: the reader's buffer is reused after this returns, so everything the
        // game loop needs is copied into value-type messages here.
        ReadOnlySpan<byte> data = reader.GetRemainingBytesSpan();
        _stats.AddIn(data.Length);

        var packet = new PacketReader(data);
        if (!packet.TryReadPacketId(out PacketId id))
        {
            OnBadPacket(peer, BadPacketReason.UnknownId);
            return;
        }

        switch (id)
        {
            case PacketId.JoinMatchRequest:
                // Only the first Join per connection reaches the game loop; repeats would let one
                // client fill the Control channel and get other peers disconnected.
                if (peer.Tag is not PeerState joinState || joinState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.DuplicateJoin);
                    break;
                }
                joinState.JoinRequested = true;
                if (!_channels.Control.Writer.TryWrite(new ControlMessage(ControlKind.JoinRequested, peer.Id, peer, null)))
                {
                    _logger.LogCritical("Control channel full; disconnecting peer {PeerId}", peer.Id);
                    peer.Disconnect();
                }
                break;

            case PacketId.PlayerInput:
                // The Input channel is shared by all peers and drops the oldest message when full,
                // so one peer must not be able to fill it: inputs before Join and inputs above the
                // per-peer rate are rejected here and count toward the bad-packet kick.
                if (peer.Tag is not PeerState inputState || !inputState.JoinRequested)
                {
                    OnBadPacket(peer, BadPacketReason.InputBeforeJoin);
                    break;
                }
                if (!inputState.TryCountInputPacket(Environment.TickCount64, _options.MaxInputPacketsPerSecond))
                {
                    OnBadPacket(peer, BadPacketReason.InputRate);
                    break;
                }
                if (PlayerInputPacket.TryRead(ref packet, out var input))
                    _channels.Input.Writer.TryWrite(new InputMessage(peer.Id, peer, input));
                else
                    OnBadPacket(peer, BadPacketReason.Malformed);
                break;

            default:
                // Server-to-client packet ids are never valid from a client.
                OnBadPacket(peer, BadPacketReason.WrongDirection);
                break;
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        _logger.LogWarning("Network error {SocketError} from {EndPoint}", socketError, endPoint);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Unconnected messages are disabled on the NetManager; nothing to do.
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    private void OnBadPacket(NetPeer peer, BadPacketReason reason)
    {
        _stats.AddBadPacket();
        _health.AddBadPacket(reason);
        if (peer.Tag is not PeerState state) return;
        state.BadPackets++;
        if (state.BadPackets >= _options.BadPacketDisconnectThreshold && !state.Kicked)
        {
            state.Kicked = true;
            _health.AddKick(DisconnectCode.Kicked);
            _logger.LogWarning("Kicking peer {PeerId} ({DevPlayerId}) after {Count} invalid packets (last: {Reason})",
                peer.Id, state.DevPlayerId, state.BadPackets, reason);
            Close(peer, DisconnectCode.Kicked);
        }
    }
}
```

**전체 교체** `Server/src/ProjectH.Server/Net/PeerState.cs`:

```csharp
using System.Threading;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Stored in NetPeer.Tag at accept time. Three groups of fields, each with one owner:
//   - DevPlayerId is immutable and may be read by any thread.
//   - BadPackets, Kicked, JoinRequested and the input rate window are touched only on LiteNetLib's receive path, so
//     they need no synchronization: LiteNetLib runs one receive thread per socket and the server binds IPv4 only, so
//     all of a peer's packets arrive on that single thread.
//   - ConnectedTick, Joined and LastInputTick (Phase 10 D3, D4) belong to the game loop thread only.
// CloseCode is the one field both threads write; it is first-writer-wins through Interlocked.
public sealed class PeerState
{
    private int _closeCode;

    public PeerState(string devPlayerId)
    {
        DevPlayerId = devPlayerId;
    }

    public string DevPlayerId { get; }
    public int BadPackets;
    public bool Kicked;
    // Set when the first JoinMatchRequest is enqueued. Later Joins are bad packets and are not
    // enqueued, which keeps each connection at <= 3 control messages (see ControlChannelCapacity).
    public bool JoinRequested;

    // Game loop only: the loop tick the Connected message was handled at, whether the Join was handled, and the loop
    // tick of the newest input (the Join counts as one, so the input timeout starts at the join).
    public long ConnectedTick;
    public bool Joined;
    public long LastInputTick;

    // Phase 10 D1, D2: the code the server closed this connection with (None = the server did not close it). Set
    // before peer.Disconnect is called, so the game loop always sees it when the Disconnected message arrives (with
    // UnsyncedEvents LiteNetLib may raise OnPeerDisconnected inside Disconnect itself). Decides whether the player
    // gets a reconnect grace: never after a server close.
    public DisconnectCode CloseCode => (DisconnectCode)Volatile.Read(ref _closeCode);

    // Returns false when another close already set the code (that one stays).
    public bool TrySetCloseCode(DisconnectCode code) =>
        Interlocked.CompareExchange(ref _closeCode, (int)code, (int)DisconnectCode.None) == (int)DisconnectCode.None;

    private long _inputWindowStartMs;
    private int _inputPacketsInWindow;

    // Fixed 1-second window. Returns false once the peer exceeds maxPerSecond in the current window.
    public bool TryCountInputPacket(long nowMs, int maxPerSecond)
    {
        if (nowMs - _inputWindowStartMs >= 1000)
        {
            _inputWindowStartMs = nowMs;
            _inputPacketsInWindow = 0;
        }
        return ++_inputPacketsInWindow <= maxPerSecond;
    }
}
```

**전체 교체** `Server/src/ProjectH.Server/ServerOptions.cs`:

```csharp
using ProjectH.Shared.Protocol;

namespace ProjectH.Server;

// Bound from the "Server" section of appsettings.json. Validate() runs at startup so a bad
// config fails fast instead of producing oversized snapshots or unbounded queues at runtime.
public sealed class ServerOptions
{
    public int Port { get; set; } = 7777;
    public int MaxPlayers { get; set; } = 16;
    public int SimHz { get; set; } = 30;
    public int SnapshotEveryTicks { get; set; } = 2;
    public int InputBufferPerPlayer { get; set; } = 8;
    public int MaxInputMessagesPerTick { get; set; } = 512;
    public int BadPacketDisconnectThreshold { get; set; } = 20;
    public int DisconnectTimeoutMs { get; set; } = 5000;
    public int StatsIntervalSeconds { get; set; } = 10;
    // Phase 4 (D5, D7): seed of the game loop's loot Random (same seed = same loot), and how long a
    // looted spawn point stays empty. Phase 5 (D4): used only with DevRespawn; a match never refills loot.
    // Each match rolls its loot with LootSeed + round number (D3).
    public int LootSeed { get; set; } = 1;
    public int LootRespawnSeconds { get; set; } = 30;

    // Phase 5 (D1, D4, D6): the match flow. A countdown starts once MinPlayers are connected; the result
    // screen lasts ResultSeconds. DevRespawn = the Phase 3/4 sandbox (no match flow, respawn and loot refill
    // on, loot from the start); off in production. Each match rolls its zone with ZoneSeed + round number.
    public int MinPlayers { get; set; } = 2;
    public int StartCountdownSeconds { get; set; } = 10;
    public int ResultSeconds { get; set; } = 10;
    public bool DevRespawn { get; set; }
    public int ZoneSeed { get; set; } = 1;
    // Phase 6 D9: each match shuffles the drop points with SpawnSeed + round number.
    public int SpawnSeed { get; set; } = 1;

    // Phase 10 (D2-D4): a participant who drops during a match keeps its character this long (0 = off); a connection
    // must join within JoinTimeoutSeconds; a joined player that sends no input for InputTimeoutSeconds is disconnected
    // (0 = off).
    public int ReconnectGraceSeconds { get; set; } = 10;
    public int JoinTimeoutSeconds { get; set; } = 5;
    public int InputTimeoutSeconds { get; set; } = 10;

    // Each connection produces at most Connected + JoinRequested + Disconnected.
    public int ControlChannelCapacity => MaxPlayers * 3;
    public int InputChannelCapacity => MaxPlayers * InputBufferPerPlayer;
    public byte SnapshotHz => (byte)(SimHz / SnapshotEveryTicks);
    // The client sends at most one input packet per simulation step; 2x leaves room for bursts
    // after network jitter. Anything above is flooding and counts as bad packets.
    public int MaxInputPacketsPerSecond => SimHz * 2;

    public string? Validate()
    {
        if (Port < 0 || Port > 65535) return "Port must be 0-65535.";
        if (MaxPlayers < 1 || MaxPlayers > ProtocolConstants.MaxSnapshotEntities)
            return $"MaxPlayers must be 1-{ProtocolConstants.MaxSnapshotEntities}: a snapshot is at most {ProtocolConstants.MaxSnapshotParts} unfragmented datagrams.";
        if (SimHz < 10 || SimHz > 128) return "SimHz must be 10-128.";
        if (SnapshotEveryTicks < 1 || SnapshotEveryTicks > SimHz) return "SnapshotEveryTicks must be 1-SimHz.";
        if (SimHz % SnapshotEveryTicks != 0) return "SnapshotEveryTicks must divide SimHz: SnapshotHz is SimHz / SnapshotEveryTicks.";
        if (InputBufferPerPlayer < 2 || InputBufferPerPlayer > 64) return "InputBufferPerPlayer must be 2-64.";
        if (MaxInputMessagesPerTick < 1) return "MaxInputMessagesPerTick must be positive.";
        if (BadPacketDisconnectThreshold < 1) return "BadPacketDisconnectThreshold must be positive.";
        if (DisconnectTimeoutMs < 500) return "DisconnectTimeoutMs must be at least 500.";
        if (StatsIntervalSeconds < 1) return "StatsIntervalSeconds must be positive.";
        if (LootSeed < 0) return "LootSeed must be 0 or more.";
        if (LootRespawnSeconds < 0 || LootRespawnSeconds > 3600) return "LootRespawnSeconds must be 0-3600 (0 = off).";
        // 1 is rejected: a lone player is the only one alive, so the match would finish on its first tick and the
        // flow would cycle Starting -> Finished forever.
        if (MinPlayers < 2 || MinPlayers > MaxPlayers) return "MinPlayers must be 2-MaxPlayers (a match needs at least two players).";
        if (StartCountdownSeconds < 1 || StartCountdownSeconds > 300) return "StartCountdownSeconds must be 1-300.";
        if (ResultSeconds < 1 || ResultSeconds > 300) return "ResultSeconds must be 1-300.";
        if (ZoneSeed < 0) return "ZoneSeed must be 0 or more.";
        if (SpawnSeed < 0) return "SpawnSeed must be 0 or more.";
        if (ReconnectGraceSeconds < 0 || ReconnectGraceSeconds > 60) return "ReconnectGraceSeconds must be 0-60 (0 = off).";
        if (JoinTimeoutSeconds < 1 || JoinTimeoutSeconds > 60) return "JoinTimeoutSeconds must be 1-60.";
        // Below 2 s a normal hitch (a scene load, a GC pause on a weak machine) would disconnect live players.
        if (InputTimeoutSeconds != 0 && (InputTimeoutSeconds < 2 || InputTimeoutSeconds > 300))
            return "InputTimeoutSeconds must be 0 (off) or 2-300.";
        return null;
    }
}
```

**전체 교체** `Shared/Runtime/Protocol/ProtocolConstants.cs`:

```csharp
namespace ProjectH.Shared.Protocol
{
    public static class ProtocolConstants
    {
        // Bump whenever any packet layout changes; the server rejects other versions at connect time.
        // 2: Phase 1 box collision changed movement results. 3: Phase 3 combat (aim in inputs, snapshot flags and self block, combat packets).
        // 4: Phase 4 inventory (2-byte buttons, ammo type in the weapon catalog, item packets).
        // 5: Phase 5 battle royale (MatchState, ZoneState, MatchResult, Placement in PlayerDied).
        // 6: Phase 6 map (terrain and the new boxes change movement results; packet layouts are unchanged).
        // 7: Phase 8 snapshots (13-byte quantized entities, a snapshot split into up to MaxSnapshotParts packets).
        // 8: Phase 10 hardening (DisconnectCode in the disconnect data, JoinResult.Resumed).
        public const ushort ProtocolVersion = 8;

        public const int MaxDevPlayerIdBytes = 32;
        public const int MaxInputsPerPacket = 3;

        // LiteNetLib does not fragment Unreliable/Sequenced packets, so one snapshot packet must fit one datagram (a snapshot is up to MaxSnapshotParts packets).
        public const int MaxPacketSize = 1200;

        // UDP payload size LiteNetLib may use per datagram (NetManager.MtuOverride). LiteNetLib's
        // default starting MTU is 1024 (1020 bytes of user data), which is below MaxPacketSize.
        // 1232 is the common internet-safe payload (IPv6 minimum MTU 1280 - 48 bytes of headers)
        // and leaves 1228 bytes for a Sequenced packet, so MaxPacketSize always fits.
        public const int Mtu = 1232;

        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 19 header bytes) / 13 bytes per entity = 90, so 100 players
        // take MaxSnapshotParts = 2 packets (pinned by PacketTests).
        public const int MaxSnapshotEntities = 100;
        public const int MaxEntitiesPerSnapshotPacket = 90;
        public const int MaxSnapshotParts = (MaxSnapshotEntities + MaxEntitiesPerSnapshotPacket - 1) / MaxEntitiesPerSnapshotPacket;
    }
}
```

**전체 교체** `Shared/Runtime/Protocol/RejectReason.cs`:

```csharp
namespace ProjectH.Shared.Protocol
{
    // Sent as the single byte of LiteNetLib's reject data when a connection request is refused.
    public enum RejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,
        ServerFull = 2,
        BadRequest = 3,
    }

    public enum JoinResult : byte
    {
        Ok = 0,
        AlreadyJoined = 1,
        MatchFull = 2,
        // Phase 10 D2: the same DevPlayerId came back within the reconnect grace and took over its character
        // (same entity id). The client handles it like Ok; the server sends the full state again.
        Resumed = 3,
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t1_impl.py`로 저장하고 `python <경로>/p10_t1_impl.py E:/popol/ProjectH`로 실행한다. `BotBrain.cs`, `GameLoop.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/src/ProjectH.Bots/BotBrain.cs', [
(r"""        if (!view.Joined || !view.HasSnapshot || !view.Alive)
        {
            Goal = BotGoal.None;
            return false;
""",
 r"""        if (!view.Joined || !view.HasSnapshot)
        {
            Goal = BotGoal.None;
            return false;
        }
        if (!view.Alive)
        {
            // Phase 10 D4: like the Unity client, a dead or spectating bot keeps sending empty inputs (no move, no
            // buttons) while joined, so the server's input timeout does not close it.
            Goal = BotGoal.None;
            command.Yaw = _bodyYaw;
            command.ViewTick = view.ServerTick;
            return true;
"""),
])

edit('Server/src/ProjectH.Server/GameLoop.cs', [
(r"""    private readonly InboundChannels _channels;
""",
 r"""    // Phase 10 D9: totals for the Health line and the Meter, shared with the network threads (Interlocked).
    private readonly HealthCounters _health = new();
    private readonly InboundChannels _channels;
    private readonly NetworkListener _listener;
"""),
(r"""    private readonly List<int> _stalePeers = new();
""",
 r"""    private readonly List<int> _stalePeers = new();
    // Phase 10 D3, D4: peers the sweep found past a timeout, closed after the sweep. Cleared every tick.
    private readonly List<(int PeerId, DisconnectCode Code)> _timedOut = new();
    // Ticks run by this loop. Timeouts count on it, not on Match.ServerTick, so they survive a match reset (D6).
    private long _loopTick;
    private readonly long _joinTimeoutTicks;
    private readonly long _inputTimeoutTicks;   // 0 = off
"""),
(r"""        var listener = new NetworkListener(options, _channels, _stats, logger);
        _net = new NetManager(listener, null)
""",
 r"""        _joinTimeoutTicks = (long)options.JoinTimeoutSeconds * options.SimHz;
        _inputTimeoutTicks = (long)options.InputTimeoutSeconds * options.SimHz;
        _listener = new NetworkListener(options, _channels, _stats, _health, logger);
        _net = new NetManager(_listener, null)
"""),
(r"""        listener.Manager = _net;
""",
 r"""        _listener.Manager = _net;
"""),
(r"""    internal Match Match => _match;
""",
 r"""    internal Match Match => _match;
    internal NetworkListener Listener => _listener;
    public HealthCounters Health => _health;
"""),
(r"""        DrainControl();
        DrainInput();
        RemoveStalePeers();
""",
 r"""        _loopTick++;
        DrainControl();
        DrainInput();
        SweepPeers();
"""),
(r"""                    break;

                case ControlKind.JoinRequested:
                    if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                        _match.TryJoin(message.PeerId, ((PeerState)peer.Tag).DevPlayerId);
""",
 r"""                    ((PeerState)message.Peer.Tag).ConnectedTick = _loopTick;
                    break;

                case ControlKind.JoinRequested:
                    if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                        Join(message.PeerId, (PeerState)peer.Tag);
"""),
(r"""                _match.EnqueueInput(message.PeerId, message.Packet);
        }
    }

    private void RemoveStalePeers()
    {
        // Safety net for lost Disconnected messages. ConnectionState is written by LiteNetLib's
        // thread; a stale read only delays removal by one tick.
        foreach (var pair in _peers)
        {
            if (pair.Value.ConnectionState != ConnectionState.Connected) _stalePeers.Add(pair.Key);
        }
        foreach (int peerId in _stalePeers) RemovePeer(peerId);
        _stalePeers.Clear();
""",
 r"""            {
                ((PeerState)peer.Tag).LastInputTick = _loopTick;
                _match.EnqueueInput(message.PeerId, message.Packet);
            }
        }
    }

    private void Join(int peerId, PeerState state)
    {
        state.Joined = true;
        state.LastInputTick = _loopTick;   // the input timeout starts at the join (D4)
        JoinResult result = _match.TryJoin(peerId, state.DevPlayerId);
        if (result == JoinResult.Ok) _health.AddJoin();
        _logger.LogInformation("Peer {PeerId} ({DevPlayerId}) join: {Result}", peerId, state.DevPlayerId, result);
    }

    // Once per tick over at most MaxPlayers peers, no allocation.
    // 1. Safety net for lost Disconnected messages. ConnectionState is written by LiteNetLib's thread; a stale read
    //    only delays removal by one tick.
    // 2. Phase 10 D3, D4: a connection that has not joined within JoinTimeoutSeconds, and a joined player that sent no
    //    input for InputTimeoutSeconds (dead and spectating players included: the client sends input while joined).
    private void SweepPeers()
    {
        foreach (var pair in _peers)
        {
            NetPeer peer = pair.Value;
            if (peer.ConnectionState != ConnectionState.Connected)
            {
                _stalePeers.Add(pair.Key);
                continue;
            }
            var state = (PeerState)peer.Tag;
            if (!state.Joined)
            {
                if (_loopTick - state.ConnectedTick >= _joinTimeoutTicks) _timedOut.Add((pair.Key, DisconnectCode.JoinTimeout));
            }
            else if (_inputTimeoutTicks > 0 && _loopTick - state.LastInputTick >= _inputTimeoutTicks)
            {
                _timedOut.Add((pair.Key, DisconnectCode.InputTimeout));
            }
        }
        foreach (int peerId in _stalePeers) RemovePeer(peerId);
        _stalePeers.Clear();

        foreach ((int peerId, DisconnectCode code) in _timedOut)
        {
            NetPeer peer = _peers[peerId];
            _health.AddKick(code);
            _logger.LogInformation("Disconnecting peer {PeerId} ({DevPlayerId}): {Code}", peerId, ((PeerState)peer.Tag).DevPlayerId, code);
            NetworkListener.Close(peer, code);
            RemovePeer(peerId);
        }
        _timedOut.Clear();
"""),
(r"""        _exceptionsSinceStats = 0;
""",
 r"""        _exceptionsSinceStats = 0;
        _listener.ResetLogLimits();
"""),
])
print('p10_t1_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Hardening|FullyQualifiedName~DisconnectCode|FullyQualifiedName~ProtocolConstants|FullyQualifiedName~BotBrain"`
Expected: 모두 통과.

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 695개다(689 통과, MySQL 6개 건너뜀). 계획 단계에서 같은 결과였다.

- [ ] **Step 5: Commit** — `feat: add Phase 10 disconnect codes, join and input timeouts, invalid-packet breakdown (protocol v8)`

Unity는 새 Shared 파일 `DisconnectCode.cs`의 `.meta`를 다음에 Editor가 열릴 때 만든다. Unity 컴파일 확인은 Task 5 Step 5에서 한 번에 한다.

---

### Task 2: 재접속 유예(Grace)와 Resume

**Files:**
- Modify: `Server/src/ProjectH.Server/Game/PlayerEntity.cs`, `Game/PlayerInputBuffer.cs`, `Game/Match.cs`, `GameLoop.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Game/ReconnectGraceTests.cs`, `Integration/ReconnectIntegrationTests.cs`
  - Modify: `Game/RoyaleHarness.cs`(`reconnectGraceSeconds` 인자)

**Interfaces:**
- Consumes: Task 1 `PeerState.CloseCode`, `HealthCounters`, `JoinResult.Resumed`, `HardeningIntegrationTests` 도우미
- Produces:
  - `PlayerEntity`: `NoPeer`(-1), `PeerId { get; internal set; }`, `GraceEndTick`, `IsGraced`
  - `PlayerInputBuffer.Reset()`
  - `Match`: `bool Disconnect(int peerId, bool allowGrace)`, `GracedCount`. `TryJoin`은 `JoinResult.Resumed`를 돌려줄 수 있다.
  - `RoyaleHarness(..., int reconnectGraceSeconds = 10)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/ReconnectGraceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 10 D2 through Match: a participant who drops during the match keeps its character for the grace and gets it
// back by joining again with the same DevPlayerId. Grace 10 s = 300 ticks at 30 Hz.
public class ReconnectGraceTests
{
    private const int GraceTicks = 300;

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b, PlayerEntity c) InMatch(int grace = 10,
        List<MatchRecord>? records = null)
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout, reconnectGraceSeconds: grace, matchSink: records == null ? null : records.Add);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        PlayerEntity c = h.Join(3);
        h.RunToMatch();
        h.Ticks(5);
        return (h, a, b, c);
    }

    [Fact]
    public void ADroppedParticipant_StaysInTheWorld_WithoutADespawn()
    {
        var (h, a, b, _) = InMatch();
        h.Packets.Clear();

        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.True(a.IsGraced);
        Assert.Equal(1, h.Match.GracedCount);
        Assert.Equal(3, h.Match.PlayerCount);
        Assert.False(h.Match.TryGetPlayer(1, out _));   // no connection maps to it any more

        h.Ticks(GraceTicks - 10);
        Assert.DoesNotContain(h.Packets, p => p.Id == PacketId.PlayerDespawned);
        Assert.DoesNotContain(h.Packets, p => p.PeerId == PlayerEntity.NoPeer || p.PeerId == 1);   // nothing to the dead connection
        Assert.True(a.Alive);
        Assert.Equal(3, h.Match.Flow.Alive);
        // Still drawn for the others.
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.WorldSnapshot));
    }

    [Fact]
    public void JoiningAgain_ResumesTheSameCharacter_WithTheFullState()
    {
        var (h, a, b, _) = InMatch();
        h.Place(a, new Vector3(3f, 0f, 4f));
        a.Health = 60;
        ushort entity = a.EntityId;
        int weapons = a.Inventory.Slots.Count(s => !s.IsEmpty);
        h.Match.Disconnect(1, allowGrace: true);
        h.Ticks(30);
        h.Packets.Clear();

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity resumed));
        Assert.Same(a, resumed);
        Assert.Equal(11, a.PeerId);
        Assert.False(a.IsGraced);
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(60, a.Health);
        Assert.Equal(weapons, a.Inventory.Slots.Count(s => !s.IsEmpty));
        Assert.True(Vector3.Distance(new Vector3(3f, 0f, 4f), a.State.Position) < 0.5f);

        // What a late joiner gets, in the join order, to the new connection only.
        List<PacketId> toNew = h.Packets.Where(p => p.PeerId == 11).Select(p => p.Id).ToList();
        Assert.Equal(PacketId.JoinMatchResponse, toNew[0]);
        var r = RoyaleHarness.Reader(h.Packets.First(p => p.PeerId == 11));
        Assert.True(JoinMatchResponse.TryRead(ref r, out JoinMatchResponse response));
        Assert.Equal(JoinResult.Resumed, response.Result);
        Assert.Equal(entity, response.MyEntityId);
        Assert.Contains(PacketId.WeaponCatalog, toNew);
        Assert.Contains(PacketId.ItemCatalog, toNew);
        Assert.Contains(PacketId.InventoryState, toNew);
        Assert.Equal(3, toNew.Count(id => id == PacketId.PlayerSpawned));
        Assert.Contains(PacketId.MatchState, toNew);
        Assert.Contains(PacketId.ZoneState, toNew);
        // The others never saw it leave: no spawn and no despawn for them.
        Assert.DoesNotContain(h.Packets, p => p.PeerId != 11 && (p.Id == PacketId.PlayerSpawned || p.Id == PacketId.PlayerDespawned));

        // The new connection numbers its inputs from 1: they are taken, they move the player and the ack follows.
        Vector3 before = a.State.Position;
        for (int i = 0; i < 10; i++)
        {
            h.Send(a, new InputCommand { MoveY = 1f });
            h.Match.Tick();
        }
        Assert.Equal(10u, a.LastProcessedSeq);
        Assert.True(Vector3.Distance(before, a.State.Position) > 0.5f);
        _ = b;
    }

    [Fact]
    public void WhenTheGraceRunsOut_ThePlayerIsEliminated_DropsItsLoot_AndIsRecordedAsLeft()
    {
        var records = new List<MatchRecord>();
        var (h, a, b, c) = InMatch(records: records);
        h.Match.Disconnect(1, allowGrace: true);
        h.Packets.Clear();

        h.Ticks(GraceTicks - 1);
        Assert.True(a.Alive);
        Assert.DoesNotContain(h.Packets, p => p.Id == PacketId.PlayerDespawned);
        h.Ticks(2);

        Assert.False(a.Alive);
        Assert.Equal(3, a.Placement);   // the first one out of three
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.ItemSpawned));   // its weapons and ammo on the ground

        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Place(c, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(b, c);
        h.Ticks(2);
        MatchRecord record = Assert.Single(records);
        PlayerRecord left = record.Players.Single(p => p.DevPlayerId == "p1");
        Assert.Equal(3, left.Placement);
    }

    [Fact]
    public void KilledWhileAway_ComesBackAsASpectator()
    {
        var (h, a, b, _) = InMatch();
        h.Place(a, new Vector3(0f, 0f, 3f));
        h.Place(b, new Vector3(0f, 0f, -3f));
        h.Match.Disconnect(1, allowGrace: true);

        h.ShootUntilDead(b, a);   // a graced character can be shot
        h.Ticks(1);
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(3, a.Placement);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));

        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p1"));
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity again));
        Assert.NotSame(a, again);
        Assert.False(again.Alive);   // spectates (Phase 5 D10)
        Assert.False(again.Participant);
    }

    [Theory]
    [InlineData(false)]   // the server closed the connection (Kicked, InputTimeout)
    [InlineData(true)]
    public void OutsideTheGraceRules_TheDisconnectIsALeave(bool allowGrace)
    {
        var (h, a, b, _) = InMatch();
        if (allowGrace)
        {
            // A dead participant has nothing to come back to.
            h.Place(a, new Vector3(0f, 0f, 3f));
            h.Place(b, new Vector3(0f, 0f, -3f));
            h.ShootUntilDead(b, a);
        }
        h.Packets.Clear();
        Assert.False(h.Match.Disconnect(1, allowGrace));
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
    }

    [Fact]
    public void BeforeTheMatch_ADisconnectIsALeave()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        h.Join(1);
        h.Join(2);
        h.Ticks(3);   // counting down
        Assert.Equal(MatchFlowState.Starting, h.Match.Flow.State);
        Assert.False(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(1, h.Match.PlayerCount);
    }

    [Fact]
    public void AConnectedPlayerWithTheSameId_IsNotTakenOver()
    {
        var (h, a, _, _) = InMatch();
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(11, "p2"));   // p2 is connected: a new spectator, not p2's character
        Assert.True(h.Match.TryGetPlayer(11, out PlayerEntity newcomer));
        Assert.False(newcomer.Alive);

        // A second p1 joins while p1 plays (a spectator copy). p1 drops: with that copy connected, a third p1 is a new
        // player as well, and the graced character stays where it is.
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "p1"));
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(13, "p1"));
        Assert.True(a.IsGraced);
    }

    [Fact]
    public void TwoGracedCopies_TheOldestIsResumedFirst()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(1, "same"));
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(2, "same"));
        h.Join(3);
        h.RunToMatch();
        h.Match.TryGetPlayer(1, out PlayerEntity first);
        h.Match.TryGetPlayer(2, out PlayerEntity second);
        h.Match.Disconnect(2, allowGrace: true);
        h.Match.Disconnect(1, allowGrace: true);

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "same"));
        Assert.Equal(11, second.PeerId);   // dropped first
        Assert.True(first.IsGraced);
        // Now a "same" is connected, so the other copy is not handed out.
        Assert.Equal(JoinResult.Ok, h.Match.TryJoin(12, "same"));
        Assert.True(first.IsGraced);
    }

    // D2: a match that ends while a participant is away ranks it with the others still in; the round reset then
    // removes it (the grace list is empty for the next round).
    [Fact]
    public void AMatchThatEndsDuringTheGrace_RanksTheGracedPlayer_AndTheRoundResetDropsIt()
    {
        var records = new List<MatchRecord>();
        var (h, _, _, c) = InMatch(records: records);
        Assert.True(h.Match.Disconnect(3, allowGrace: true));
        h.Match.Leave(1);
        h.Match.Leave(2);
        h.Ticks(1);

        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);
        Assert.Equal(1, c.Placement);   // the last one in, though away
        Assert.Equal(c.EntityId, h.Match.WinnerId);
        Assert.Equal("p3", Assert.Single(records).WinnerDevPlayerId);
        Assert.Equal(1, h.Match.GracedCount);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Equal(0, h.Match.GracedCount);
        Assert.Equal(0, h.Match.PlayerCount);
        Assert.Equal(MatchFlowState.WaitingForPlayers, h.Match.Flow.State);
    }

    [Fact]
    public void GraceZero_IsTheOldLeave()
    {
        var (h, _, b, _) = InMatch(grace: 0);
        Assert.False(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(2, h.Match.PlayerCount);
        Assert.NotEmpty(h.SentTo(b.PeerId, PacketId.PlayerDespawned));
        Assert.Equal(2, h.Match.Flow.Alive);
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/ReconnectIntegrationTests.cs`:

```csharp
using System.Linq;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D2 over real UDP: a client that crashes during the match comes back to its own character; a kicked one
// does not get the grace.
public sealed class ReconnectIntegrationTests
{
    private static bool InMatch(HeadlessClient c) =>
        c.MatchStates.Count > 0 && c.MatchStates[^1].State is MatchFlowState.Playing or MatchFlowState.FinalPhase;

    [Fact]
    public void ACrashedClient_RejoinsItsCharacter_AndTheOthersNeverSawItLeave()
    {
        using GameLoop server = StartServer(countdown: 1);
        var a = Join(server, "a");
        using var b = Join(server, "b");
        Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");
        ushort entity = a.MyEntityId;

        a.Kill();   // no disconnect message: the server finds out by its 1 s timeout
        Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

        using var back = Join(server, "a", expected: JoinResult.Resumed);
        Assert.Equal(entity, back.MyEntityId);
        Assert.True(Pump.Until(() => back.LastSnapshot.ContainsKey(entity) && back.Spawned.Contains(b.MyEntityId) &&
                                     back.Inventories.Count > 0 && back.MatchStates.Count > 0, 3000, back, b), "full state");
        Assert.True(InMatch(back));
        Assert.DoesNotContain(entity, b.Despawned);
        Assert.Equal(1, server.Health.Resumes);

        // Its new inputs count from 1 and are acknowledged.
        Assert.True(SendInputsUntil(back, () => back.LastAckInputSeq >= 5, 3000, b), "inputs taken");
        a.Dispose();
    }

    [Fact]
    public void AKickedClient_GetsNoGrace()
    {
        using GameLoop server = StartServer(countdown: 1);
        using var a = Join(server, "a");
        using var b = Join(server, "b");
        Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");

        for (int i = 0; i < 25; i++) a.SendRaw(new byte[] { 0xFF });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a, b), "kicked");
        Assert.True(Pump.Until(() => b.Despawned.Contains(a.MyEntityId), 3000, b), "despawned at once");
        Assert.Equal(0, server.Health.GraceStarts);
        // a was eliminated at once, so b is the last one in.
        Assert.True(Pump.Until(() => b.MatchStates.Any(s => s.State == MatchFlowState.Finished), 3000, b), "match over");
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t2_tests.py`로 저장하고 `python <경로>/p10_t2_tests.py E:/popol/ProjectH`로 실행한다. `RoyaleHarness.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/tests/ProjectH.Server.Tests/Game/RoyaleHarness.cs', [
(r"""        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null)
""",
 r"""        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null,
        int reconnectGraceSeconds = 10)
"""),
(r"""                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
""",
 r"""                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds,
"""),
])
print('p10_t2_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `PlayerEntity.NoPeer`·`IsGraced`, `Match.Disconnect`·`GracedCount`가 없다(CS0117, CS1061).

- [ ] **Step 3: 유예와 Resume을 구현한다**

`Match`의 모든 송신은 생성자에서 감싼 `_send`를 지나므로, 유예 중인 플레이어(`NoPeer`)에게는 아무것도 가지 않는다. `GameLoop.RemovePeer`는 `CloseCode == None`일 때만 유예를 허락한다.

**전체 교체** `Server/src/ProjectH.Server/Game/PlayerEntity.cs`:

```csharp
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// One joined player. Owned by Match on the game loop thread.
public sealed class PlayerEntity
{
    // Phase 10 D2: the PeerId of a player whose connection dropped and who waits for a reconnect. LiteNetLib reuses
    // peer ids, so a graced player must not keep its old one: the next connection with that id would get its packets.
    public const int NoPeer = -1;

    public PlayerEntity(ushort entityId, int peerId, string devPlayerId, int inputCapacity)
    {
        EntityId = entityId;
        PeerId = peerId;
        DevPlayerId = devPlayerId;
        Inputs = new PlayerInputBuffer(inputCapacity);
    }

    public ushort EntityId { get; }
    // Changes only for the reconnect grace (D2): NoPeer while graced, the new connection's id after a resume.
    public int PeerId { get; internal set; }
    public string DevPlayerId { get; }
    public PlayerInputBuffer Inputs { get; }

    // Fields, not properties, so MovementSimulation can step State by ref without copies.
    public MoveState State;
    public InputCommand LastInput;
    public uint LastProcessedSeq;
    // Consecutive ticks without a buffered input, reset when one is taken. Match stops counting at
    // SimHz / 2 (the repeat grace window), so it cannot overflow on a long-silent connection.
    public int MissedTicks;

    // Combat (Phase 3). Set by Match.ResetCombat at join and respawn; game loop thread only.
    public int Health;
    public int Shield;
    public bool Alive;
    public uint RespawnAtTick;

    // Phase 5 (D3, D9, D12): set when a match starts. A participant keeps its placement once eliminated
    // (0 = still in, or not a participant); kills count only during the match.
    public bool Participant;
    public byte Placement;
    public int Kills;
    // Phase 9 (§37): damage this player dealt to others during the match (shield and health actually removed, no
    // overkill), and the tick it was eliminated (0 = still in). Reset when a match starts.
    public int DamageDealt;
    public uint EliminatedTick;

    // Phase 4 (D10): weapons, magazines, per-slot fire intervals, ammo reserves and consumables. Replaced
    // by the starting loadout at join and respawn.
    public readonly Inventory Inventory = new();
    // The reload of the current slot (a switch cancels it).
    public bool Reloading;
    public uint ReloadEndTick;
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
    public bool FireHeld;

    // Feet position at the end of each recent tick, for rewinding this player as a target (D6).
    public readonly PositionHistory History = new();

    // Phase 10 D2: the tick the reconnect grace ends at (only meaningful while PeerId is NoPeer).
    public uint GraceEndTick;
    public bool IsGraced => PeerId == NoPeer;
}
```

**전체 교체** `Server/src/ProjectH.Server/Game/PlayerInputBuffer.cs`:

```csharp
using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Per-player input queue sorted by Seq. Owned by the game loop thread only, so no locking.
// Bounded: when full the oldest input is dropped. Because the game loop takes one input per tick,
// this also caps a client that sends faster than the tick rate (speed hack or clock drift).
public sealed class PlayerInputBuffer
{
    private readonly InputCommand[] _items;
    private int _count;

    public PlayerInputBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new InputCommand[capacity];
    }

    public int Count => _count;
    public uint LastTakenSeq { get; private set; }
    public long DroppedCount { get; private set; }

    public bool Add(in InputCommand command)
    {
        if (command.Seq <= LastTakenSeq) return false;

        int insertAt = _count;
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Seq == command.Seq) return false;
            if (_items[i].Seq > command.Seq)
            {
                insertAt = i;
                break;
            }
        }

        if (_count == _items.Length)
        {
            DroppedCount++;
            // The new input would be the oldest one kept: dropping it is the same as dropping the oldest.
            if (insertAt == 0) return false;
            Array.Copy(_items, 1, _items, 0, _count - 1);
            _count--;
            insertAt--;
        }

        Array.Copy(_items, insertAt, _items, insertAt + 1, _count - insertAt);
        _items[insertAt] = command;
        _count++;
        return true;
    }

    // Phase 10 D2: a resumed player's new connection numbers its inputs from 1 again, so what the old connection
    // sent and the Seq order it set are forgotten. DroppedCount is a total and stays.
    public void Reset()
    {
        _count = 0;
        LastTakenSeq = 0;
    }

    public bool TryTake(out InputCommand command)
    {
        if (_count == 0)
        {
            command = default;
            return false;
        }
        command = _items[0];
        Array.Copy(_items, 1, _items, 0, _count - 1);
        _count--;
        LastTakenSeq = command.Seq;
        return true;
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t2_impl.py`로 저장하고 `python <경로>/p10_t2_impl.py E:/popol/ProjectH`로 실행한다. `Match.cs`, `GameLoop.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/src/ProjectH.Server/Game/Match.cs', [
(r"""    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in Leave(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
""",
 r"""    // Connected players only. A graced player (Phase 10 D2) is in _players and _graced but not here.
    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in RemovePlayer(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
    // Phase 10 D2: participants whose connection dropped during the match, oldest first. A subset of _players, so at
    // most MaxPlayers. An entry leaves on a resume, when its grace ends, when it dies, and at the round reset.
    private readonly List<PlayerEntity> _graced = new();
    private readonly uint _graceTicks;
"""),
(r"""        _send = send ?? throw new ArgumentNullException(nameof(send));
""",
 r"""        SendPacket raw = send ?? throw new ArgumentNullException(nameof(send));
        // Phase 10 D2: the one place packets to a graced player (NoPeer) are dropped. Every send goes through _send.
        _send = (peerId, data, method) =>
        {
            if (peerId != PlayerEntity.NoPeer) raw(peerId, data, method);
        };
        _graceTicks = (uint)options.ReconnectGraceSeconds * (uint)options.SimHz;
"""),
(r"""    public int PlayerCount => _players.Count;
""",
 r"""    public int PlayerCount => _players.Count;
    // Phase 10 D2: players kept for a reconnect (included in PlayerCount).
    public int GracedCount => _graced.Count;
"""),
(r"""        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
""",
 r"""        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
        // Phase 10 D2: before the full check, because a graced player's slot is its own.
        PlayerEntity? graced = FindGraced(devPlayerId);
        if (graced != null)
        {
            Resume(peerId, graced);
            return JoinResult.Resumed;
        }
"""),
(r"""    public void Leave(int peerId)
    {
        if (!_playersByPeer.Remove(peerId, out var player)) return;
        _players.Remove(player);
""",
 r"""    // Phase 10 D2: the connection of peerId is gone. During the match a living participant whose connection was not
    // closed by the server (allowGrace) stays in the world for ReconnectGraceSeconds: no input, it can be shot and the
    // zone still hurts it. Everyone else leaves at once. Returns true when the player was kept.
    public bool Disconnect(int peerId, bool allowGrace)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return false;
        if (!allowGrace || _graceTicks == 0 || !_flow.InMatch || !player.Participant || !player.Alive)
        {
            Leave(peerId);
            return false;
        }
        _playersByPeer.Remove(peerId);
        player.PeerId = PlayerEntity.NoPeer;
        player.GraceEndTick = ServerTick + _graceTicks;
        player.Inputs.Reset();
        _graced.Add(player);
        return true;
    }

    public void Leave(int peerId)
    {
        if (_playersByPeer.Remove(peerId, out var player)) RemovePlayer(player);
    }

    // Leave for a connected or a graced player. Never called inside a loop over _players.
    private void RemovePlayer(PlayerEntity player)
    {
        _players.Remove(player);
        _graced.Remove(player);
"""),
(r"""        uint now = ServerTick;
""",
 r"""        uint now = ServerTick;

        // Phase 10 D2: first, outside every loop over _players, the graced players whose grace is over.
        ExpireGrace(now);
"""),
(r"""    private void CloseRound(uint now)
    {
        ClearWorldItems();
""",
 r"""    private void CloseRound(uint now)
    {
        // Phase 10 D2: the grace ends with the round. Before Reopen counts the players for the next countdown.
        while (_graced.Count > 0) RemovePlayer(_graced[0]);
        ClearWorldItems();
"""),
(r"""
    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
""",
 r"""
    // Phase 10 D2: a graced player leaves when its grace is over, and also once it is dead (killed while away: its
    // placement is fixed and there is nothing left to resume; coming back is a new spectator, like any late join).
    private void ExpireGrace(uint now)
    {
        for (int i = _graced.Count - 1; i >= 0; i--)
        {
            PlayerEntity player = _graced[i];
            if (player.Alive && now < player.GraceEndTick) continue;
            RemovePlayer(player);
        }
    }

    // D2: the oldest graced, living player with this DevPlayerId, unless a connected player already uses the id (then
    // the newcomer joins as a new player and takes nothing over).
    private PlayerEntity? FindGraced(string devPlayerId)
    {
        if (_graced.Count == 0) return null;
        foreach (var player in _playersByPeer.Values)
        {
            if (player.DevPlayerId == devPlayerId) return null;
        }
        foreach (var player in _graced)
        {
            if (player.Alive && player.DevPlayerId == devPlayerId) return player;
        }
        return null;
    }

    // D2: the character goes to the new connection with everything it has (position, health, inventory, placement
    // state). The new client numbers its inputs from 1, so the input state starts over. It gets what a late joiner
    // gets; the others never saw it leave, so they are told nothing.
    private void Resume(int peerId, PlayerEntity player)
    {
        _graced.Remove(player);
        player.PeerId = peerId;
        _playersByPeer.Add(peerId, player);
        player.Inputs.Reset();
        player.LastProcessedSeq = 0;
        player.LastInput = new InputCommand { Yaw = player.State.Yaw };
        player.MissedTicks = 0;
        player.FireHeld = false;

        SendJoinResponse(peerId, JoinResult.Resumed, player.EntityId);
        SendCatalogs(peerId);
        SendWorldItems(peerId);
        SendInventory(player);
        foreach (var other in _players) SendSpawned(peerId, other);
        if (!_flow.DevRespawn)
        {
            SendMatchState(peerId, _flow.ToWire(_players.Count));
            SendZoneState(peerId, _zone.ToWire());
        }
    }

    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
"""),
])

edit('Server/src/ProjectH.Server/GameLoop.cs', [
(r"""        _match.Tick();
    }
""",
 r"""        _match.Tick();
        _health.SetGauges(_peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State);
    }
"""),
(r"""        if (result == JoinResult.Ok) _health.AddJoin();
""",
 r"""        if (result == JoinResult.Ok) _health.AddJoin();
        else if (result == JoinResult.Resumed) _health.AddResume();
"""),
(r"""    private void RemovePeer(int peerId)
    {
        _peers.Remove(peerId);
        _match.Leave(peerId);
""",
 r"""    // Phase 10 D2: a connection the server did not close itself (a client quit, crash or network loss) may keep its
    // character for the reconnect grace; Match decides whether the player qualifies.
    private void RemovePeer(int peerId)
    {
        if (!_peers.Remove(peerId, out NetPeer? peer)) return;
        var state = (PeerState)peer.Tag;
        if (_match.Disconnect(peerId, allowGrace: state.CloseCode == DisconnectCode.None))
        {
            _health.AddGraceStart();
            _logger.LogInformation("Peer {PeerId} ({DevPlayerId}) dropped mid-match; character kept for {Seconds} s",
                peerId, state.DevPlayerId, _options.ReconnectGraceSeconds);
        }
"""),
])
print('p10_t2_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~ReconnectGrace|FullyQualifiedName~ReconnectIntegration"`
Expected: 모두 통과(13개).

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 708개다(702 통과, 6개 건너뜀). 기존 `Match.Leave` 테스트는 그대로 통과한다(`Leave`는 바로 나가는 경로로 남는다).

- [ ] **Step 5: Commit** — `feat(server): keep a dropped participant's character for a reconnect grace (Phase 10 D2)`

---

### Task 3: 예외 복구, 종료 순서, DB 재연결

**Files:**
- Modify: `Server/src/ProjectH.Server/GameLoop.cs`, `GameServerService.cs`, `Program.cs`, `Persistence/MatchHistoryWriter.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/ManualTime.cs`, `Integration/ExceptionRecoveryTests.cs`
  - Modify: `Persistence/MySqlTests.cs`(DB 없는 Writer 테스트를 D8에 맞춘다. DB 재연결 `[MySqlFact]`를 더한다)

**Interfaces:**
- Consumes: Task 1 `NetworkListener.Close`·`DataOf`, `HealthCounters`; Task 2 `Match`
- Produces:
  - `GameLoop(..., Action? onFatal = null, TimeProvider? time = null)`
  - `GameLoop`:
    - internal `RunTickGuarded()`, `Stop(TimeSpan joinTimeout)`, `TickFaultHook`, `LoopFaultHook`, `LoopTicks`
    - `Match`는 이제 초기화 때 바뀐다(읽을 때마다 현재 것을 돌려준다).
  - `GameServerService(..., IHostApplicationLifetime lifetime)`
  - 테스트 `ManualTime : TimeProvider`(`Advance(TimeSpan)`)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/ExceptionRecoveryTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D6, D7: a failing tick is skipped, a failing match is reset, failing resets stop the server; nothing
// outside the tick kills the loop thread; shutdown tells clients and has a time limit.
public sealed class ExceptionRecoveryTests
{
    private const int SimHz = 30;
    private const int TicksBeforeReset = SimHz * 3;

    private static GameLoop Loop(Action? onFatal = null, TimeProvider? time = null) =>
        new(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, onFatal: onFatal, time: time);

    private static void Fail(GameLoop loop, int ticks)
    {
        loop.TickFaultHook = () => throw new InvalidOperationException("test fault");
        for (int i = 0; i < ticks; i++) loop.RunTickGuarded();
        loop.TickFaultHook = null;
    }

    [Fact]
    public void OneFailedTick_IsSkipped_AndTheMatchGoesOn()
    {
        using GameLoop loop = Loop();
        Match match = loop.Match;
        loop.RunTickGuarded();
        Fail(loop, 1);
        loop.RunTickGuarded();
        Assert.Same(match, loop.Match);
        Assert.Equal(2u, match.ServerTick);   // the failed tick did not run
        Assert.Equal(1, loop.Health.TickFailures);
        Assert.Equal(0, loop.Health.MatchResets);
    }

    [Fact]
    public void FailingForThreeSeconds_ResetsTheMatch_AndASuccessBreaksTheRun()
    {
        using GameLoop loop = Loop();
        Match first = loop.Match;
        Fail(loop, TicksBeforeReset - 1);
        loop.RunTickGuarded();   // a good tick: the run starts over
        Fail(loop, TicksBeforeReset - 1);
        Assert.Same(first, loop.Match);
        Assert.Equal(0, loop.Health.MatchResets);

        Fail(loop, 1);
        Assert.NotSame(first, loop.Match);
        Assert.Equal(0u, loop.Match.ServerTick);
        Assert.Equal(1, loop.Health.MatchResets);
    }

    [Fact]
    public void ThreeResetsWithinTenMinutes_StopTheServer()
    {
        var time = new ManualTime();
        int fatal = 0;
        using GameLoop loop = Loop(() => fatal++, time);

        Fail(loop, TicksBeforeReset);   // t = 0
        time.Advance(TimeSpan.FromMinutes(6));
        Fail(loop, TicksBeforeReset);   // t = 6 min
        time.Advance(TimeSpan.FromMinutes(6));
        Fail(loop, TicksBeforeReset);   // t = 12 min: the first is 12 minutes old, so 2 in the window
        Assert.Equal(0, fatal);
        Assert.Equal(3, loop.Health.MatchResets);

        time.Advance(TimeSpan.FromMinutes(3));
        Fail(loop, TicksBeforeReset);   // t = 15 min: 6, 12 and 15 are within ten minutes
        Assert.Equal(1, fatal);
        Assert.Equal(4, loop.Health.MatchResets);
    }

    [Fact]
    public void AMatchReset_ClosesEveryClientWithServerError_AndTheyCanJoinTheNewMatch()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        server.TickFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(Pump.Until(() => a.Disconnected && b.Disconnected, 6000, a, b), "closed");
        server.TickFaultHook = null;
        Assert.Equal(DisconnectCode.ServerError, a.DisconnectCode);
        Assert.Equal(DisconnectCode.ServerError, b.DisconnectCode);
        Assert.True(DisconnectCodes.ShouldReconnect(remoteClose: true, a.DisconnectCode, networkLoss: false));
        Assert.Equal(1, server.Health.MatchResets);

        using var again = Join(server, "a");
        Assert.Equal(1, server.Match.PlayerCount);
    }

    [Fact]
    public void ExceptionsOutsideTheTick_DoNotEndTheLoopThread()
    {
        using GameLoop server = StartServer();
        server.LoopFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(SpinUntil(() => server.Health.LoopFailures >= 5, 3000), "failures seen");
        long ticks = server.LoopTicks;
        server.LoopFaultHook = null;
        Assert.True(SpinUntil(() => server.LoopTicks > ticks + 10, 3000), "still ticking");
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task Shutdown_TellsConnectedClients_ServerShutdown()
    {
        GameLoop server = StartServer();
        using var a = Join(server, "a");
        var stopping = Task.Run(server.Dispose);
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a), "closed");
        Assert.Equal(DisconnectCode.ServerShutdown, a.DisconnectCode);
        Assert.False(DisconnectCodes.ShouldReconnect(remoteClose: true, a.DisconnectCode, networkLoss: false));
        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(3000)));
    }

    [Fact]
    public void Stop_ReturnsWithinItsLimit_WhenTheLoopIsStuck()
    {
        GameLoop server = StartServer();
        using var stuck = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        server.TickFaultHook = () =>
        {
            entered.Set();
            stuck.Wait();
        };
        Assert.True(entered.Wait(3000), "loop stuck");

        var clock = Stopwatch.StartNew();
        server.Stop(TimeSpan.FromMilliseconds(300));
        Assert.True(clock.ElapsedMilliseconds < 2500, $"Stop took {clock.ElapsedMilliseconds} ms");
        Assert.True(server.IsRunning);   // left behind, as a background thread

        stuck.Set();   // let it finish: it sees the cancelled token and exits
        Assert.True(SpinUntil(() => !server.IsRunning, 3000), "thread ended");
        server.Dispose();
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs) => SpinWait.SpinUntil(condition, timeoutMs);
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/ManualTime.cs`:

```csharp
using System;
using System.Threading;

namespace ProjectH.Server.Tests;

// Phase 10: a clock that moves only when a test says so (the reset window of D6, the stall watchdog of D9).
// Timestamps are TimeSpan ticks; GetElapsedTime works from them through TimestampFrequency.
public sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t3_tests.py`로 저장하고 `python <경로>/p10_t3_tests.py E:/popol/ProjectH`로 실행한다. `MySqlTests.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/tests/ProjectH.Server.Tests/Persistence/MySqlTests.cs', [
(r"""
    // The hosted writer: start, enqueue, the record lands in the database, stop drains.
""",
 r"""
    // Phase 10 D8: the writer starts while the database is unreachable, then the database appears (a TCP forwarder to
    // the test MySQL opens on the port the writer uses): the next record creates the schema and is saved.
    [MySqlFact]
    public async Task TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears()
    {
        var target = new MySqlConnectionStringBuilder(MySqlFactAttribute.ConnectionString);
        int port = UnreachableDatabaseTests.FreePort();
        var through = new MySqlConnectionStringBuilder(MySqlFactAttribute.ConnectionString)
        {
            Server = "127.0.0.1", Port = (uint)port, Pooling = false, ConnectionTimeout = 2,
        };
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = through.ConnectionString, MaxAttempts = 1 });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(queue.TryEnqueue(Match(10, new PlayerRecord(NewId("n"), 1, 0, 0, 1000))));
        await UnreachableDatabaseTests.WaitFor(() => writer.Failed == 1);
        Assert.Equal(1, writer.Failed);

        using var forwarder = new TcpForwarder(port, target.Server, (int)target.Port);
        string a = NewId("y");
        Assert.True(queue.TryEnqueue(Match(11, new PlayerRecord(a, 1, 2, 50, 2000))));
        await UnreachableDatabaseTests.WaitFor(() => writer.Saved == 1);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.Saved);
        Assert.Equal(1, writer.Failed);
        Assert.Equal(2, (await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(a, CancellationToken.None))!.Kills);
    }

    // Accepts on 127.0.0.1:listenPort and pipes each connection to host:port, until disposed.
    private sealed class TcpForwarder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public TcpForwarder(int listenPort, string host, int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, listenPort);
            _listener.Start();
            _ = AcceptAsync(host, port);
        }

        private async Task AcceptAsync(string host, int port)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = PipeAsync(inbound, host, port);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task PipeAsync(TcpClient inbound, string host, int port)
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                try
                {
                    await outbound.ConnectAsync(host, port, _stop.Token);
                    NetworkStream a = inbound.GetStream(), b = outbound.GetStream();
                    await Task.WhenAny(a.CopyToAsync(b, _stop.Token), b.CopyToAsync(a, _stop.Token));
                }
                catch (Exception)
                {
                    // A closed side ends the pipe.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    // The hosted writer: start, enqueue, the record lands in the database, stop drains.
"""),
(r"""// Without a database the writer must not fail the server: records are discarded and counted. Needs no MySQL.
public class UnreachableDatabaseTests
{
    [Fact]
    public async Task TheWriter_WithNoDatabase_DiscardsAndKeepsRunning()
""",
 r"""// Without a database the writer must not fail the server. Phase 10 D8: it keeps trying, so every record without a
// database is a failed save, counted. Needs no MySQL.
public class UnreachableDatabaseTests
{
    // A loopback port nothing listens on (taken from the OS, then released).
    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 15000) await Task.Delay(50);
    }

    [Fact]
    public async Task TheWriter_WithNoDatabase_CountsEachRecordAsFailed_AndKeepsRunning()
"""),
(r"""            ConnectionString = "Server=127.0.0.1;Port=1;Database=projecth;User ID=nobody;Password=none;Connection Timeout=1",
""",
 r"""            ConnectionString = $"Server=127.0.0.1;Port={FreePort()};Database=projecth;User ID=nobody;Password=none;Connection Timeout=1;Pooling=false",
            MaxAttempts = 1,
"""),
(r"""        var clock = Stopwatch.StartNew();
        while (writer.Discarded == 0 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(1, writer.Discarded);
        Assert.Equal(0, writer.Saved);
""",
 r"""        await WaitFor(() => writer.Failed == 1);
        Assert.True(queue.TryEnqueue(new MatchRecord(2, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>())));
        await WaitFor(() => writer.Failed == 2);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, writer.Failed);
        Assert.Equal(0, writer.Discarded);
        Assert.Equal(0, writer.Saved);
        Assert.Equal(TaskStatus.RanToCompletion, writer.ExecuteTask!.Status);
"""),
])
print('p10_t3_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. 없는 것은 다음과 같다.
- `GameLoop`의 `onFatal` 인자(CS1739)
- `RunTickGuarded`, `TickFaultHook`, `LoopFaultHook`, `LoopTicks`(CS1061)
- `Stop(TimeSpan)`(CS1501)

- [ ] **Step 3: 예외 복구·종료·DB 재연결을 구현한다**

`onFatal`은 Game Loop 스레드에서 불린다. 그래서 `GameServerService`는 `Environment.ExitCode = 1`만 하고 `StopApplication`은 `Task.Run`으로 넘긴다(Game Loop가 Host를 기다리지 않는다). `Stop`은 `Interlocked`로 한 번만 돈다. `Dispose`가 Thread Pool의 `Stop`과 겹쳐도 같다.

**전체 교체** `Server/src/ProjectH.Server/GameServerService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop and disposes it.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;
    private readonly ILogger _logger;

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        // Phase 10 D6: when match resets keep failing, the server stops with exit code 1 so a supervisor or a person
        // notices. StopApplication runs on the thread pool: the game loop thread must not wait on the host.
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record),
            onFatal: () =>
            {
                Environment.ExitCode = 1;
                _ = Task.Run(lifetime.StopApplication);
            });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        return Task.CompletedTask;
    }

    // Phase 10 D7: GameLoop.Stop blocks (thread join up to 5 s, shutdown notices up to 1 s), so it runs on the thread
    // pool and the host's thread only awaits it, for as long as the host's shutdown token allows.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(_loop.Stop, CancellationToken.None).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The host shutdown timeout ended before the game loop finished stopping");
        }
    }

    public void Dispose() => _loop.Dispose();
}
```

**전체 교체** `Server/src/ProjectH.Server/Program.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server;
using ProjectH.Server.Persistence;

// Content root = the build output folder, so appsettings.json is found no matter where
// `dotnet run` is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
// Phase 9: match history. The writer is registered before the game server, so on shutdown the host stops the game
// loop first and the writer drains the queue after it.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"));
builder.Services.AddSingleton(services =>
{
    PersistenceOptions persistence = services.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    string? error = persistence.Validate();
    if (error != null) throw new InvalidOperationException(error);
    return new MatchHistoryQueue(persistence.QueueCapacity);
});
// The host's default 30 s ShutdownTimeout is shared by every hosted service's StopAsync, so it would cut the writer's
// drain short: allow the longest drain plus 30 s for GameServerService.StopAsync.
builder.Services.Configure<HostOptions>(host =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(PersistenceOptions.MaxShutdownDrainSeconds + 30));
builder.Services.AddHostedService<MatchHistoryWriter>();
builder.Services.AddHostedService<GameServerService>();

using IHost host = builder.Build();

// Phase 10 D6: whatever escapes every other handler is at least logged. An unhandled exception on any thread still
// ends the process (the runtime decides that); an unobserved task exception does not.
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

await host.RunAsync();
// 1 after a fatal stop (GameServerService sets it, D6), otherwise 0.
return Environment.ExitCode;
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t3_impl.py`로 저장하고 `python <경로>/p10_t3_impl.py E:/popol/ProjectH`로 실행한다. `GameLoop.cs`, `MatchHistoryWriter.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/src/ProjectH.Server/GameLoop.cs', [
(r"""//     Match, _peers and _stalePeers.
// This class takes no locks, so there is no lock ordering to keep.
public sealed class GameLoop : IDisposable
{
""",
 r"""//     Match, _peers and _stalePeers (and replaces Match on a reset, D6).
//   - Stop() runs on the host's thread once the loop thread has stopped (or after its join limit, D7).
// This class takes no locks, so there is no lock ordering to keep.
public sealed class GameLoop : IDisposable
{
    // Phase 10 D6: a match that fails this many seconds of ticks in a row is reset; three resets within ResetWindow
    // stop the server. D7: how long Stop waits for the loop thread, and for clients to take the shutdown notice.
    private const int FailingSecondsBeforeReset = 3;
    private const int MaxResetsInWindow = 3;
    private static readonly TimeSpan ResetWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownNoticeTimeout = TimeSpan.FromSeconds(1);

"""),
(r"""    private readonly Match _match;
""",
 r"""    // What a new Match is built from (D6 reset).
    private readonly GameData _data;
    private readonly StartingLoadout? _loadout;
    private readonly System.Numerics.Vector3[]? _dropPoints;
    private readonly Action<Persistence.MatchRecord>? _matchSink;
    private Match _match;
"""),
(r"""    private long _exceptionCount;
    private long _exceptionsSinceStats;
""",
 r"""    private int _stopping;   // Stop runs once (Interlocked), even when Dispose calls it again while it runs
    private long _exceptionCount;
    private long _exceptionsSinceStats;
    // Phase 10 D6 (game loop thread only): failed ticks in a row, the times of the latest resets, and whether the
    // server gave up (then the loop only waits for Stop).
    private readonly int _failuresBeforeReset;
    private int _consecutiveTickFailures;
    private readonly long[] _resetTimestamps = new long[MaxResetsInWindow];
    private int _resetCount;
    private bool _fatal;
    private long _loopFailuresSinceStats;
    private readonly Action? _onFatal;
    private readonly TimeProvider _time;
    // Test seams (D6): run at the start of every tick / after every tick, outside it. Set by tests only.
    private volatile Action? _tickFaultHook;
    private volatile Action? _loopFaultHook;
"""),
(r"""    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null)
""",
 r"""    // onFatal: Phase 10 D6, called once (on the loop thread, must not block) when resets keep failing; production stops
    // the host with exit code 1. time: the clock of the reset window (tests pass a manual one).
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null,
        Action? onFatal = null, TimeProvider? time = null)
"""),
(r"""        _logger = logger;
        _channels = new InboundChannels(options, _stats);
""",
 r"""        _logger = logger;
        _data = data;
        _loadout = loadout;
        _dropPoints = dropPoints;
        _matchSink = matchSink;
        _onFatal = onFatal;
        _time = time ?? TimeProvider.System;
        _failuresBeforeReset = options.SimHz * FailingSecondsBeforeReset;
        _channels = new InboundChannels(options, _stats);
"""),
(r"""        _match = new Match(options, data, SendToPeer, loadout, dropPoints: dropPoints, matchSink: matchSink);
    }
""",
 r"""        _match = NewMatch();
    }

    private Match NewMatch() => new(_options, _data, SendToPeer, _loadout, dropPoints: _dropPoints, matchSink: _matchSink);
"""),
(r"""    internal NetworkListener Listener => _listener;
""",
 r"""    internal NetworkListener Listener => _listener;
    internal Action? TickFaultHook { get => _tickFaultHook; set => _tickFaultHook = value; }
    internal Action? LoopFaultHook { get => _loopFaultHook; set => _loopFaultHook = value; }
    // Ticks run so far; readable from any thread (tests).
    internal long LoopTicks => Interlocked.Read(ref _loopTick);
"""),
(r"""        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = false };
""",
 r"""        // Background: a loop thread that never returns (a deadlock, an endless loop) must not keep the process alive
        // after the host has finished shutting down (D7). Stop still joins it first.
        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = true };
"""),
(r"""    public void Stop()
    {
        if (_thread != null)
        {
            _stop.Cancel();
            _thread.Join();
        }
        // The loop has exited, so nothing sends anymore: tell clients and release the socket.
        if (_net.IsRunning) _net.Stop(true);
""",
 r"""    public void Stop() => Stop(ThreadJoinTimeout);

    // Phase 10 D7: 1. stop the ticks, 2. close every connection with ServerShutdown, 3. give those notices up to
    // ShutdownNoticeTimeout to be acknowledged (at once when no peer is left), 4. close the socket. A loop thread that
    // does not stop within joinTimeout is logged and left behind (it is a background thread). Blocks the caller for
    // at most joinTimeout + ShutdownNoticeTimeout; GameServerService runs it off the host's thread.
    internal void Stop(TimeSpan joinTimeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        if (_thread != null)
        {
            _stop.Cancel();
            if (!_thread.Join(joinTimeout))
                _logger.LogCritical("The game loop thread did not stop within {Seconds} s; shutting down without it", joinTimeout.TotalSeconds);
        }
        if (!_net.IsRunning) return;

        _net.DisconnectAll(NetworkListener.DataOf(DisconnectCode.ServerShutdown), 0, 1);
        long deadline = Environment.TickCount64 + (long)ShutdownNoticeTimeout.TotalMilliseconds;
        // A peer stays ShutdownRequested until its client acknowledges the disconnect (or LiteNetLib gives up).
        while (_net.GetPeersCount(ConnectionState.Connected | ConnectionState.ShutdownRequested) > 0 &&
               Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
        _net.Stop(true);
"""),
(r"""            long tickStart = clock.ElapsedTicks;
            try
            {
                RunTick();
            }
            catch (Exception ex)
            {
                // One bad tick must not stop the server. Log the first of each stats interval only,
                // so a repeating failure cannot flood the log 30 times per second.
                _exceptionCount++;
                if (++_exceptionsSinceStats == 1)
                    _logger.LogError(ex, "Unhandled exception in game tick {Tick}", _match.ServerTick);
            }
            _tickMetrics.Record((clock.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency);

            if (clock.ElapsedTicks >= nextStats)
            {
                LogStats();
                nextStats += statsTicks;
            }

            nextTick += tickTicks;
            long behind = clock.ElapsedTicks - nextTick;
            if (behind > tickTicks * 5)
            {
                // Far behind (debugger pause, machine stall): skip the backlog instead of bursting ticks.
                _lateTicksSkipped += behind / tickTicks;
                nextTick = clock.ElapsedTicks;
            }
            WaitUntil(clock, nextTick, token);
""",
 r"""            // Phase 10 D6: the whole iteration is guarded, not only the tick. An exception in the stats line or the
            // wait must not end this thread (it would take the server down with it).
            try
            {
                long tickStart = clock.ElapsedTicks;
                if (!_fatal) RunTickGuarded();
                _tickMetrics.Record((clock.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency);
                _loopFaultHook?.Invoke();

                if (clock.ElapsedTicks >= nextStats)
                {
                    nextStats += statsTicks;   // first, so a stats line that throws is not retried every tick
                    LogStats();
                }

                nextTick += tickTicks;
                long behind = clock.ElapsedTicks - nextTick;
                if (behind > tickTicks * 5)
                {
                    // Far behind (debugger pause, machine stall): skip the backlog instead of bursting ticks.
                    _lateTicksSkipped += behind / tickTicks;
                    nextTick = clock.ElapsedTicks;
                }
                WaitUntil(clock, nextTick, token);
            }
            catch (Exception ex)
            {
                _health.AddLoopFailure();
                if (++_loopFailuresSinceStats == 1) _logger.LogError(ex, "Unhandled exception in the game loop outside the tick");
                // One tick of rest, so a failure that repeats every iteration cannot spin; then pace from now.
                Thread.Sleep((int)(1000 / _options.SimHz));
                nextTick = clock.ElapsedTicks;
            }
        }
    }

    // One tick that never throws (D6). A failure is counted and logged (the first of each stats interval, so a
    // repeating one cannot flood the log 30 times per second). One bad tick is skipped; FailingSecondsBeforeReset
    // seconds of failures in a row reset the match.
    internal void RunTickGuarded()
    {
        try
        {
            _tickFaultHook?.Invoke();
            RunTick();
            _consecutiveTickFailures = 0;
        }
        catch (Exception ex)
        {
            _exceptionCount++;
            _health.AddTickFailure();
            if (++_exceptionsSinceStats == 1)
            {
                _logger.LogError(ex, "Unhandled exception in game tick {Tick} (match {State}, round {Round})",
                    _match.ServerTick, _match.Flow.State, _match.Flow.Round);
            }
            if (++_consecutiveTickFailures >= _failuresBeforeReset) ResetMatch();
        }
    }

    // Phase 10 D6: the match state is most likely broken half-way, so it is thrown away (no record) and every client
    // is closed with ServerError; their clients reconnect into the new match. Three resets within ResetWindow mean the
    // fault is in the code, not in one match: the server stops (onFatal) instead of failing forever.
    private void ResetMatch()
    {
        _consecutiveTickFailures = 0;
        _health.AddMatchReset();
        _logger.LogError("{Count} ticks failed in a row: resetting the match (round {Round}) and closing {Peers} connections with ServerError",
            _failuresBeforeReset, _match.Flow.Round, _peers.Count);
        foreach (NetPeer peer in _peers.Values)
        {
            _health.AddKick(DisconnectCode.ServerError);
            NetworkListener.Close(peer, DisconnectCode.ServerError);
        }
        _peers.Clear();
        _match = NewMatch();

        // The newest MaxResetsInWindow reset times, oldest first.
        if (_resetCount == MaxResetsInWindow)
        {
            Array.Copy(_resetTimestamps, 1, _resetTimestamps, 0, MaxResetsInWindow - 1);
            _resetCount--;
        }
        _resetTimestamps[_resetCount++] = _time.GetTimestamp();
        if (_resetCount == MaxResetsInWindow && _time.GetElapsedTime(_resetTimestamps[0]) <= ResetWindow)
        {
            _fatal = true;
            _logger.LogCritical("{Count} match resets within {Minutes} minutes: stopping the server", MaxResetsInWindow, ResetWindow.TotalMinutes);
            _onFatal?.Invoke();
"""),
(r"""    {
        _loopTick++;
""",
 r"""    {
        Interlocked.Increment(ref _loopTick);   // read by tests from another thread (LoopTicks)
"""),
(r"""        _exceptionsSinceStats = 0;
""",
 r"""        _exceptionsSinceStats = 0;
        _loopFailuresSinceStats = 0;
"""),
])

edit('Server/src/ProjectH.Server/Persistence/MatchHistoryWriter.cs', [
(r"""// creates the schema; if the database cannot be reached the server keeps running and the records are discarded and
// counted (the game must not depend on the database, §36). Each record is saved with up to MaxAttempts tries. On
// shutdown the queue is completed and whatever is left is saved for at most ShutdownDrainSeconds; a save cut off by
// that limit and the records still queued are logged and counted as Discarded.
""",
 r"""// creates the schema. Phase 10 D8: if the database cannot be reached then, the writer keeps going and tries again
// with every record (a developer may start the database after the server), so a record without a database counts as
// Failed after its attempts; the game never depends on the database (§36). Each record is saved with up to MaxAttempts
// tries. On shutdown the queue is completed and whatever is left is saved for at most ShutdownDrainSeconds; a save cut
// off by that limit and the records still queued are logged and counted as Discarded.
"""),
(r"""    private MatchStore? _store;
""",
 r"""    private MatchStore? _store;
    // D8: false until EnsureSchemaAsync succeeded once. Only the ExecuteAsync task reads or writes it.
    private bool _schemaReady;
"""),
(r"""                {
                    Interlocked.Increment(ref _discarded);
""",
 r"""                {
                    // Persistence is disabled: nothing will ever be saved.
                    Interlocked.Increment(ref _discarded);
"""),
(r"""        try
        {
            var store = new MatchStore(_options.ConnectionString);
            await store.EnsureSchemaAsync(token);
            _logger.LogInformation("Match history: connected, schema ready.");
            return store;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Match history: database unavailable; this run will not save matches.");
            return null;
        }
""",
 r"""        var store = new MatchStore(_options.ConnectionString);
        try
        {
            await EnsureSchemaAsync(store, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // D8: not fatal. Each record tries again first (SaveWithRetryAsync).
            _logger.LogWarning("Match history: database unavailable at start ({Message}); each finished match will try again.", e.Message);
        }
        return store;
    }

    private async Task EnsureSchemaAsync(MatchStore store, CancellationToken token)
    {
        await store.EnsureSchemaAsync(token);
        _schemaReady = true;
        _logger.LogInformation("Match history: connected, schema ready.");
"""),
(r"""            {
                long matchId = await store.SaveAsync(record, token);
""",
 r"""            {
                // D8: a database that came up after the server gets its schema here, within this record's attempts.
                if (!_schemaReady) await EnsureSchemaAsync(store, token);
                long matchId = await store.SaveAsync(record, token);
"""),
])
print('p10_t3_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~ExceptionRecovery|FullyQualifiedName~UnreachableDatabase"`
Expected: 모두 통과(8개).

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 716개다(709 통과, MySQL 7개 건너뜀).

DB가 있으면 다음을 실행한다(컨테이너는 이 계획이 띄우지 않는다). Bash 기준이다.

`PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev" dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"`

Expected: 모두 통과, 건너뜀 0.
- 새 `TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears`는 빈 포트로 시작한다. 한 번 `Failed`가 난 뒤, 그 포트에 테스트 안의 TCP 중계기가 열려 MySQL로 이어진다. 다음 기록은 스키마 준비부터 해서 저장된다.
- 계획 단계에서는 DB가 꺼져 있어 이 테스트를 돌리지 못했다. 컴파일과 DB 없는 경로만 확인했다. 실패하면 중계기(`TcpForwarder`)부터 확인한다.

- [ ] **Step 5: Commit** — `feat(server): recover from failing ticks and matches, bounded shutdown with ServerShutdown, DB reconnect`

---

### Task 4: 관측 (Health 줄, Meter, Stall Watchdog, 로그 시각)

**Files:**
- Create: `Server/src/ProjectH.Server/Diagnostics/ServerMeter.cs`, `Diagnostics/StallWatchdog.cs`
- Modify: `Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs`, `Game/Match.cs`, `GameLoop.cs`, `GameServerService.cs`, `Program.cs`, `Persistence/MatchHistoryWriter.cs`, `appsettings.json`
- Test: Create `Server/tests/ProjectH.Server.Tests/Diagnostics/MonitoringTests.cs`

**Interfaces:**
- Consumes: Task 1–3의 `HealthCounters`, `GameLoop`, `MatchHistoryWriter`
- Produces:
  - `readonly record struct PersistenceCounts(long Saved, long Failed, long Discarded, long Dropped)`
  - `MatchHistoryWriter.Counts`
  - `HealthCounters.Persistence`(`Func<PersistenceCounts>?`)
  - `ServerMeter(HealthCounters)`, `ServerMeter.Name` = `"ProjectH.Server"`
  - `StallWatchdog(Func<long> lastTickTimestamp, TimeProvider, HealthCounters, ILogger, TimeSpan? threshold)`: `Start()`, `Check()`, `Dispose()`
  - `GameLoop`: `Time`, `LastTickTimestamp`, internal `LogPeriodic()`
  - internal `Match.TakeSinkError()`
  - `GameServerService(..., MatchHistoryWriter writer, IHostApplicationLifetime lifetime)`
  - Meter 계측기:
    - Gauge: `projecth.peers`, `projecth.players`, `projecth.graced`
    - Counter: `projecth.connections`, `projecth.joins`, `projecth.resumes`, `projecth.tick_failures`, `projecth.loop_failures`, `projecth.match_resets`, `projecth.stalls`
    - 태그 있는 Counter:
      - `projecth.disconnects{reason}`
      - `projecth.rejects{reason}`
      - `projecth.kicks{code}`
      - `projecth.bad_packets{reason}`
      - `projecth.db_records{result}`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Diagnostics/MonitoringTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Diagnostics;

// Records every log entry (level and formatted message). Thread-safe: the loop and timer threads may log.
public sealed class ListLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
    }
}

// Phase 10 D9: the Health line, the Meter and the stall watchdog.
public class MonitoringTests
{
    [Fact]
    public void TheHealthLine_FollowsTheStatsLine_WithEveryD9Item()
    {
        var log = new ListLogger();
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), log);
        loop.Health.Persistence = () => new PersistenceCounts(3, 1, 2, 4);
        loop.Health.AddReject(RejectReason.ServerFull);
        loop.Health.AddKick(DisconnectCode.InputTimeout);
        loop.Health.AddBadPacket(BadPacketReason.WrongDirection);
        loop.RunTickGuarded();

        loop.LogPeriodic();

        List<string> lines = log.Entries.Select(e => e.Message).ToList();
        int stats = lines.FindIndex(l => l.StartsWith("Stats "));
        int health = lines.FindIndex(l => l.StartsWith("Health "));
        Assert.True(stats >= 0 && health == stats + 1, string.Join("\n", lines));
        string line = lines[health];
        foreach (string item in new[]
                 {
                     "peers=0", "players=0", "graced=0", "match=WaitingForPlayers#1",
                     "connections=", "joins=", "resumed=", "graceStarts=", "disconnects timeout=", "other=",
                     "rejects full=1", "badRequest=0", "version=0",
                     "kicks badPackets=0", "joinTimeout=0", "inputTimeout=1", "serverError=0",
                     "badPackets unknownId=0", "malformed=", "beforeJoin=", "duplicateJoin=", "inputRate=", "wrongDirection=1", "handlerException=",
                     "tickFailures=0", "loopFailures=0", "matchResets=0", "stalls=0",
                     "db saved=3 failed=1 discarded=2 dropped=4",
                 })
        {
            Assert.Contains(item, line);
        }
    }

    [Fact]
    public void TheMeter_PublishesTheHealthCounters()
    {
        var health = new HealthCounters { Persistence = () => new PersistenceCounts(5, 0, 0, 1) };
        health.AddConnection();
        health.AddConnection();
        health.AddKick(DisconnectCode.Kicked);
        health.AddBadPacket(BadPacketReason.Malformed);
        health.AddDisconnect(timeout: true);
        health.SetGauges(peers: 3, players: 2, graced: 1, MatchFlowState.Playing);

        using var meter = new ServerMeter(health);
        var seen = new List<(string Name, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMeter.Name) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => seen.Add((instrument.Name, value, "")));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(("projecth.connections", 2L, ""), seen);
        Assert.Contains(("projecth.peers", 3L, ""), seen);
        Assert.Contains(("projecth.graced", 1L, ""), seen);
        Assert.Contains(("projecth.kicks", 1L, "code=Kicked"), seen);
        Assert.Contains(("projecth.bad_packets", 1L, "reason=Malformed"), seen);
        Assert.Contains(("projecth.disconnects", 1L, "reason=timeout"), seen);
        Assert.Contains(("projecth.db_records", 5L, "result=saved"), seen);
        Assert.Contains(("projecth.db_records", 1L, "result=dropped"), seen);
    }

    [Fact]
    public void TheWatchdog_ReportsAStallOnce_AndTheRecovery()
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var health = new HealthCounters();
        long lastTick = time.GetTimestamp();
        using var watchdog = new StallWatchdog(() => lastTick, time, health, log);

        time.Advance(TimeSpan.FromSeconds(1));
        watchdog.Check();
        Assert.Equal(0, health.Stalls);

        time.Advance(TimeSpan.FromSeconds(1.5));   // 2.5 s without a tick
        watchdog.Check();
        time.Advance(TimeSpan.FromSeconds(3));
        watchdog.Check();                           // still stalled: no second report
        Assert.Equal(1, health.Stalls);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("stalled"));

        lastTick = time.GetTimestamp();             // ticks again, 5.5 s after the last one
        watchdog.Check();
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("recovered after a stall of 5500 ms"));

        time.Advance(TimeSpan.FromSeconds(0.5));
        lastTick = time.GetTimestamp();
        watchdog.Check();
        Assert.Equal(1, health.Stalls);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void TheShippedSettings_HaveTheHardeningValues_AndTimestamps()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var options = new ServerOptions();
        config.GetSection("Server").Bind(options);
        Assert.Null(options.Validate());
        Assert.Equal(10, options.ReconnectGraceSeconds);
        Assert.Equal(5, options.JoinTimeoutSeconds);
        Assert.Equal(10, options.InputTimeoutSeconds);
        Assert.False(string.IsNullOrEmpty(config["Logging:Console:FormatterOptions:TimestampFormat"]));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `ServerMeter`, `StallWatchdog`, `HealthCounters.Persistence`, `GameLoop.LogPeriodic`가 없다(CS0246, CS0103, CS0117, CS1061).

- [ ] **Step 3: 관측을 구현한다**

Meter 계측기는 모두 Observable이다. Game Loop의 Tick 경로에는 아무것도 기록하지 않는다. `MatchHistoryWriter`는 하나의 Singleton을 Hosted Service로도 등록한다(`GameServerService`가 그 카운터를 읽는다). 등록 순서는 그대로라 종료 순서도 같다.

**새 파일** `Server/src/ProjectH.Server/Diagnostics/ServerMeter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D9: the Health numbers as .NET metrics, for `dotnet-counters monitor -n ProjectH.Server --counters
// ProjectH.Server`. Observable instruments only: nothing is recorded on the game loop; the values are read from
// HealthCounters when a listener polls (allocating a few small arrays then, never on the tick path). No new package
// and no open port: dotnet-counters attaches through the runtime's diagnostics channel.
// Owned by GameServerService; Dispose unregisters every instrument.
public sealed class ServerMeter : IDisposable
{
    public const string Name = "ProjectH.Server";

    private readonly Meter _meter = new(Name);

    public ServerMeter(HealthCounters h)
    {
        _meter.CreateObservableGauge("projecth.peers", () => h.Peers, description: "Open connections");
        _meter.CreateObservableGauge("projecth.players", () => h.Players, description: "Players in the match, graced included");
        _meter.CreateObservableGauge("projecth.graced", () => h.Graced, description: "Players waiting for a reconnect");
        _meter.CreateObservableCounter("projecth.connections", () => h.Connections);
        _meter.CreateObservableCounter("projecth.joins", () => h.Joins);
        _meter.CreateObservableCounter("projecth.resumes", () => h.Resumes);
        _meter.CreateObservableCounter("projecth.disconnects", () => new[]
        {
            new Measurement<long>(h.DisconnectTimeouts, Tag("reason", "timeout")),
            new Measurement<long>(h.DisconnectOthers, Tag("reason", "other")),
        });
        _meter.CreateObservableCounter("projecth.rejects", () => new[]
        {
            new Measurement<long>(h.Rejects(RejectReason.ServerFull), Tag("reason", nameof(RejectReason.ServerFull))),
            new Measurement<long>(h.Rejects(RejectReason.BadRequest), Tag("reason", nameof(RejectReason.BadRequest))),
            new Measurement<long>(h.Rejects(RejectReason.VersionMismatch), Tag("reason", nameof(RejectReason.VersionMismatch))),
        });
        _meter.CreateObservableCounter("projecth.kicks", () => ByCode(h));
        _meter.CreateObservableCounter("projecth.bad_packets", () => ByReason(h));
        _meter.CreateObservableCounter("projecth.tick_failures", () => h.TickFailures);
        _meter.CreateObservableCounter("projecth.loop_failures", () => h.LoopFailures);
        _meter.CreateObservableCounter("projecth.match_resets", () => h.MatchResets);
        _meter.CreateObservableCounter("projecth.stalls", () => h.Stalls);
        _meter.CreateObservableCounter("projecth.db_records", () => DbRecords(h));
    }

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    private static Measurement<long>[] ByCode(HealthCounters h)
    {
        var result = new Measurement<long>[(int)DisconnectCode.ServerError];
        for (int code = 1; code <= (int)DisconnectCode.ServerError; code++)
            result[code - 1] = new Measurement<long>(h.Kicks((DisconnectCode)code), Tag("code", ((DisconnectCode)code).ToString()));
        return result;
    }

    private static Measurement<long>[] ByReason(HealthCounters h)
    {
        var result = new Measurement<long>[(int)BadPacketReason.Count];
        for (int reason = 0; reason < result.Length; reason++)
            result[reason] = new Measurement<long>(h.BadPackets((BadPacketReason)reason), Tag("reason", ((BadPacketReason)reason).ToString()));
        return result;
    }

    private static Measurement<long>[] DbRecords(HealthCounters h)
    {
        if (h.Persistence is not { } source) return Array.Empty<Measurement<long>>();
        PersistenceCounts c = source();
        return new[]
        {
            new Measurement<long>(c.Saved, Tag("result", "saved")),
            new Measurement<long>(c.Failed, Tag("result", "failed")),
            new Measurement<long>(c.Discarded, Tag("result", "discarded")),
            new Measurement<long>(c.Dropped, Tag("result", "dropped")),
        };
    }
}
```

**새 파일** `Server/src/ProjectH.Server/Diagnostics/StallWatchdog.cs`:

```csharp
using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Diagnostics;

// Phase 10 D9: notices a game loop that stopped ticking (a deadlock, an endless loop, a blocking call) even when
// nothing throws. A timer checks once per second when the loop last finished a tick; past the threshold it logs one
// Critical and counts a stall, and when ticks come back it logs how long the stall lasted.
// Owned by GameServerService (created after the loop starts, disposed before it stops). Check runs on timer threads,
// which do not overlap at a 1 s period for a check this short; the loop's timestamp is read with Volatile (the
// delegate), so no lock is involved.
public sealed class StallWatchdog : IDisposable
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly Func<long> _lastTickTimestamp;
    private readonly TimeProvider _time;
    private readonly HealthCounters _health;
    private readonly ILogger _logger;
    private readonly TimeSpan _threshold;
    private ITimer? _timer;
    private bool _stalled;
    private long _stalledFrom;   // the last tick before the stall (TimeProvider timestamp)

    public StallWatchdog(Func<long> lastTickTimestamp, TimeProvider time, HealthCounters health, ILogger logger, TimeSpan? threshold = null)
    {
        _lastTickTimestamp = lastTickTimestamp;
        _time = time;
        _health = health;
        _logger = logger;
        _threshold = threshold ?? DefaultThreshold;
    }

    public void Start() => _timer ??= _time.CreateTimer(_ => Check(), null, Period, Period);

    // One check; public so tests can drive it with a manual clock.
    public void Check()
    {
        long last = _lastTickTimestamp();
        TimeSpan since = _time.GetElapsedTime(last);
        if (!_stalled && since > _threshold)
        {
            _stalled = true;
            _stalledFrom = last;
            _health.AddStall();
            _logger.LogCritical("Game loop stalled: no tick for {Ms:F0} ms", since.TotalMilliseconds);
        }
        else if (_stalled && last != _stalledFrom)
        {
            _stalled = false;
            _logger.LogWarning("Game loop recovered after a stall of {Ms:F0} ms", _time.GetElapsedTime(_stalledFrom, last).TotalMilliseconds);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
```

**전체 교체** `Server/src/ProjectH.Server/GameServerService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop, the Meter and the stall watchdog and disposes them.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;
    private readonly ILogger _logger;
    private readonly ServerMeter _meter;
    private StallWatchdog? _watchdog;

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        // Phase 10 D6: when match resets keep failing, the server stops with exit code 1 so a supervisor or a person
        // notices. StopApplication runs on the thread pool: the game loop thread must not wait on the host.
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record),
            onFatal: () =>
            {
                Environment.ExitCode = 1;
                _ = Task.Run(lifetime.StopApplication);
            });
        // Phase 10 D9: the writer's totals go into the Health line and the Meter.
        _loop.Health.Persistence = () => writer.Counts;
        _meter = new ServerMeter(_loop.Health);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        _watchdog = new StallWatchdog(() => _loop.LastTickTimestamp, _loop.Time, _loop.Health, _logger);
        _watchdog.Start();
        return Task.CompletedTask;
    }

    // Phase 10 D7: GameLoop.Stop blocks (thread join up to 5 s, shutdown notices up to 1 s), so it runs on the thread
    // pool and the host's thread only awaits it, for as long as the host's shutdown token allows.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A stopping loop does not tick: that is no stall.
        _watchdog?.Dispose();
        try
        {
            await Task.Run(_loop.Stop, CancellationToken.None).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The host shutdown timeout ended before the game loop finished stopping");
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _loop.Dispose();
        _meter.Dispose();
    }
}
```

**전체 교체** `Server/src/ProjectH.Server/Program.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server;
using ProjectH.Server.Persistence;

// Content root = the build output folder, so appsettings.json is found no matter where
// `dotnet run` is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
// Phase 9: match history. The writer is registered before the game server, so on shutdown the host stops the game
// loop first and the writer drains the queue after it.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"));
builder.Services.AddSingleton(services =>
{
    PersistenceOptions persistence = services.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    string? error = persistence.Validate();
    if (error != null) throw new InvalidOperationException(error);
    return new MatchHistoryQueue(persistence.QueueCapacity);
});
// The host's default 30 s ShutdownTimeout is shared by every hosted service's StopAsync, so it would cut the writer's
// drain short: allow the longest drain plus 30 s for GameServerService.StopAsync.
builder.Services.Configure<HostOptions>(host =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(PersistenceOptions.MaxShutdownDrainSeconds + 30));
// One writer instance, both a hosted service and a dependency of GameServerService (Phase 10: its counters go into the
// Health line). Still registered before the game server, so it still stops after it.
builder.Services.AddSingleton<MatchHistoryWriter>();
builder.Services.AddHostedService(services => services.GetRequiredService<MatchHistoryWriter>());
builder.Services.AddHostedService<GameServerService>();

using IHost host = builder.Build();

// Phase 10 D6: whatever escapes every other handler is at least logged. An unhandled exception on any thread still
// ends the process (the runtime decides that); an unobserved task exception does not.
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

await host.RunAsync();
// 1 after a fatal stop (GameServerService sets it, D6), otherwise 0.
return Environment.ExitCode;
```

**전체 교체** `Server/src/ProjectH.Server/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    },
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-dd HH:mm:ss.fff "
      }
    }
  },
  "Server": {
    "Port": 7777,
    "MaxPlayers": 16,
    "SimHz": 30,
    "SnapshotEveryTicks": 2,
    "InputBufferPerPlayer": 8,
    "MaxInputMessagesPerTick": 512,
    "BadPacketDisconnectThreshold": 20,
    "DisconnectTimeoutMs": 5000,
    "StatsIntervalSeconds": 10,
    "LootSeed": 1,
    "LootRespawnSeconds": 30,
    "MinPlayers": 2,
    "StartCountdownSeconds": 10,
    "ResultSeconds": 10,
    "DevRespawn": false,
    "ZoneSeed": 1,
    "SpawnSeed": 1,
    "ReconnectGraceSeconds": 10,
    "JoinTimeoutSeconds": 5,
    "InputTimeoutSeconds": 10
  },
  "Persistence": {
    "Enabled": true,
    "ConnectionString": "Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev;Connection Timeout=5",
    "QueueCapacity": 16,
    "MaxAttempts": 3,
    "ShutdownDrainSeconds": 5
  }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t4_impl.py`로 저장하고 `python <경로>/p10_t4_impl.py E:/popol/ProjectH`로 실행한다. `HealthCounters.cs`, `Match.cs`, `GameLoop.cs`, `MatchHistoryWriter.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs', [
(r"""using System.Threading;
""",
 r"""using System.Threading;
using ProjectH.Server.Persistence;
"""),
(r"""    private int _matchState;
""",
 r"""    private int _matchState;

    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
"""),
])

edit('Server/src/ProjectH.Server/Game/Match.cs', [
(r"""    public long MatchSinkFailures { get; private set; }
""",
 r"""    public long MatchSinkFailures { get; private set; }
    // Phase 10 D6: the first sink exception since the game loop last took it (logged once per stats interval).
    private Exception? _sinkError;

    internal Exception? TakeSinkError()
    {
        Exception? error = _sinkError;
        _sinkError = null;
        return error;
    }
"""),
(r"""        catch (Exception)
        {
            MatchSinkFailures++;
""",
 r"""        catch (Exception e)
        {
            MatchSinkFailures++;
            _sinkError ??= e;
"""),
])

edit('Server/src/ProjectH.Server/GameLoop.cs', [
(r"""using ProjectH.Server.Net;
""",
 r"""using ProjectH.Server.Net;
using ProjectH.Server.Persistence;
"""),
(r"""    private readonly TimeProvider _time;
""",
 r"""    private readonly TimeProvider _time;
    // Phase 10 D9: when the last tick attempt ended (TimeProvider timestamp), read by StallWatchdog on a timer thread.
    private long _lastTickTimestamp;
"""),
(r"""    public HealthCounters Health => _health;
""",
 r"""    public HealthCounters Health => _health;
    public TimeProvider Time => _time;
    // D9: any thread. Set when the loop starts and after every tick attempt, failed or not.
    public long LastTickTimestamp => Volatile.Read(ref _lastTickTimestamp);
"""),
(r"""        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = true };
""",
 r"""        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = true };
        Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());
"""),
(r"""                if (!_fatal) RunTickGuarded();
""",
 r"""                if (!_fatal) RunTickGuarded();
                else Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());   // idle on purpose until Stop: no stall
"""),
(r"""                    nextStats += statsTicks;   // first, so a stats line that throws is not retried every tick
                    LogStats();
""",
 r"""                    nextStats += statsTicks;   // first, so a stats line that throws is not retried every tick
                    LogPeriodic();
"""),
(r"""            if (++_consecutiveTickFailures >= _failuresBeforeReset) ResetMatch();
""",
 r"""            if (++_consecutiveTickFailures >= _failuresBeforeReset) ResetMatch();
        }
        finally
        {
            Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());
"""),
(r"""
    private void LogStats()
""",
 r"""
    // Every StatsIntervalSeconds: the Stats line (performance of the interval), then the Health line (state and totals
    // since the start, D9). Internal so tests can read both lines.
    internal void LogPeriodic()
    {
        LogStats();
        LogHealth();
        Exception? sinkError = _match.TakeSinkError();
        if (sinkError != null) _logger.LogError(sinkError, "Recording a finished match failed (first failure of this interval)");
    }

    private void LogStats()
"""),
(r"""
    // Total processor time of this process (all threads), every StatsIntervalSeconds: not on the tick path.
""",
 r"""
    // Phase 10 D9: connections, protection and database in one line, as totals since the start.
    private void LogHealth()
    {
        HealthCounters h = _health;
        PersistenceCounts db = h.Persistence?.Invoke() ?? default;
        _logger.LogInformation(
            "Health peers={Peers} players={Players} graced={Graced} match={State}#{Round} " +
            "connections={Connections} joins={Joins} resumed={Resumed} graceStarts={GraceStarts} " +
            "disconnects timeout={DisconnectTimeouts} other={DisconnectOthers} " +
            "rejects full={RejectFull} badRequest={RejectBad} version={RejectVersion} " +
            "kicks badPackets={KickBad} joinTimeout={KickJoin} inputTimeout={KickInput} serverError={KickError} " +
            "badPackets unknownId={BadUnknown} malformed={BadMalformed} beforeJoin={BadBeforeJoin} duplicateJoin={BadDuplicate} " +
            "inputRate={BadRate} wrongDirection={BadDirection} handlerException={BadHandler} " +
            "tickFailures={TickFailures} loopFailures={LoopFailures} matchResets={Resets} stalls={Stalls} " +
            "db saved={DbSaved} failed={DbFailed} discarded={DbDiscarded} dropped={DbDropped}",
            _peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State, _match.Flow.Round,
            h.Connections, h.Joins, h.Resumes, h.GraceStarts,
            h.DisconnectTimeouts, h.DisconnectOthers,
            h.Rejects(RejectReason.ServerFull), h.Rejects(RejectReason.BadRequest), h.Rejects(RejectReason.VersionMismatch),
            h.Kicks(DisconnectCode.Kicked), h.Kicks(DisconnectCode.JoinTimeout), h.Kicks(DisconnectCode.InputTimeout), h.Kicks(DisconnectCode.ServerError),
            h.BadPackets(BadPacketReason.UnknownId), h.BadPackets(BadPacketReason.Malformed), h.BadPackets(BadPacketReason.InputBeforeJoin),
            h.BadPackets(BadPacketReason.DuplicateJoin), h.BadPackets(BadPacketReason.InputRate), h.BadPackets(BadPacketReason.WrongDirection),
            h.BadPackets(BadPacketReason.HandlerException),
            h.TickFailures, h.LoopFailures, h.MatchResets, h.Stalls,
            db.Saved, db.Failed, db.Discarded, db.Dropped);
    }

    // Total processor time of this process (all threads), every StatsIntervalSeconds: not on the tick path.
"""),
])

edit('Server/src/ProjectH.Server/Persistence/MatchHistoryWriter.cs', [
(r"""    public long Discarded => Interlocked.Read(ref _discarded);
""",
 r"""    public long Discarded => Interlocked.Read(ref _discarded);

    // Phase 10 D9: the four totals for the Health line and the Meter (any thread).
    public PersistenceCounts Counts => new(Saved, Failed, Discarded, _queue.Dropped);
"""),
(r"""                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
    }
}
""",
 r"""                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
    }
}

// Phase 10 D9: what the writer did with the match records so far (Dropped: refused by the full queue).
public readonly record struct PersistenceCounts(long Saved, long Failed, long Discarded, long Dropped);
"""),
])
print('p10_t4_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~MonitoringTests"`
Expected: 모두 통과(4개).

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 720개다(713 통과, 7개 건너뜀).

- [ ] **Step 5: 서버 Host 확인**

`dotnet build Server/src/ProjectH.Server -c Release`

`dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7793 --Server:StatsIntervalSeconds=2`를 백그라운드로 7초 띄운다. 로그에서 다음을 확인한다.
- 줄마다 `yyyy-MM-dd HH:mm:ss.fff` 시각이 붙는다.
- `Stats ...` 바로 다음 줄이 `Health peers=0 players=0 graced=0 match=WaitingForPlayers#1 ... db saved=0 failed=0 discarded=0 dropped=0`이다.
- DB가 꺼져 있으면 `Match history: database unavailable at start (...); each finished match will try again.`(Warning)이 나온다. 서버는 계속 돈다.

그 프로세스를 pid로 끈다. 계획 단계에서 위 세 가지를 확인했다.

선택: 실행 중에 `dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server`로 계측기가 보이는지 본다. `dotnet-counters`가 설치되어 있지 않으면 건너뛴다. 도구를 새로 설치하지 않는다.

- [ ] **Step 6: Commit** — `feat(server): add the Health line, the ProjectH.Server meter, a stall watchdog and log timestamps`

---

### Task 5: Client 자동 재접속과 봇 `--reconnect`

**Files:**
- Modify (봇): `Server/src/ProjectH.Bots/BotConnection.cs`, `BotOptions.cs`, `BotRunner.cs`, `Program.cs`
- Modify (Unity): `Client/Assets/Scripts/Net/NetClient.cs`, `Game/GameClient.cs`, `Bootstrap/DevConnectPanel.cs`
- Test: Modify `Server/tests/ProjectH.Server.Tests/Bots/BotPartsTests.cs`, `Bots/BotIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1 `DisconnectCodes`, `JoinResult.Resumed`; Task 3 `GameLoop.TickFaultHook`
- Produces:
  - 봇:
    - `BotConnection.Code`, `BotConnection.Retryable`
    - `BotOptions.Reconnect`(`--reconnect true|false`, 기본 false)
    - `BotRunner.ReconnectDelaySeconds`(1.0), `BotRunner.Reconnects`
  - Unity:
    - `NetClient.LastDisconnectCode`, `NetClient.LastDisconnectRetryable`. `JoinResult.Resumed`도 `ClientState.Joined`다.
    - `GameClient.ReconnectAttempt`(0 = 재접속 없음). `Connect`·`Disconnect`는 자동 재접속을 멈춘다.

- [ ] **Step 1: 봇의 실패하는 테스트를 쓴다**

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t5_tests.py`로 저장하고 `python <경로>/p10_t5_tests.py E:/popol/ProjectH`로 실행한다. `BotIntegrationTests.cs`, `BotPartsTests.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/tests/ProjectH.Server.Tests/Bots/BotIntegrationTests.cs', [
(r"""    private static BotRunner Bots(GameLoop server, int count) =>
        new(new BotOptions { Port = server.LocalPort, Count = count, ConnectIntervalMs = 0 }, _ => { });
""",
 r"""    private static BotRunner Bots(GameLoop server, int count, bool reconnect = false) =>
        new(new BotOptions { Port = server.LocalPort, Count = count, ConnectIntervalMs = 0, Reconnect = reconnect }, _ => { });
"""),
(r"""    }

    [Fact]
    public void FourBots_PlayAWholeMatch_AndTheNextRoundStarts()
""",
 r"""    }

    // Phase 10 D11: a match reset closes the bots with ServerError, which the shared table retries; they come back about
    // a second later and join the new match.
    [Fact]
    public void BotsWithReconnect_ComeBackAfterAMatchReset()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4 }, TestGameData.Create());
        using BotRunner bots = Bots(server, 2, reconnect: true);
        Assert.True(RunUntil(bots, () => bots.Connection(0).View.Joined && bots.Connection(1).View.Joined, 10000), "joined");

        server.TickFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(RunUntil(bots, () => server.Health.MatchResets == 1, 10000), "reset");
        server.TickFaultHook = null;

        Assert.True(RunUntil(bots, () => bots.Reconnects == 2 && bots.Connection(0).View.Joined && bots.Connection(1).View.Joined, 10000),
            $"rejoined (reconnects {bots.Reconnects})");
        Assert.Equal(4, server.Health.Joins);   // two joins before the reset, two after
    }

    // D11: a kick is not retried, even with --reconnect true.
    [Fact]
    public void ABotClosedWithANonRetryableCode_StaysOut()
    {
        using GameLoop server = StartServer(new ServerOptions { MaxPlayers = 4, InputTimeoutSeconds = 2 }, TestGameData.Create());
        using BotRunner bots = Bots(server, 1, reconnect: true);
        Assert.True(RunUntil(bots, () => bots.Connection(0).View.Joined, 10000), "joined");
        // Stop stepping the bot: it sends no input, so the server closes it with InputTimeout.
        var clock = Stopwatch.StartNew();
        while (server.Health.Kicks(DisconnectCode.InputTimeout) == 0 && clock.ElapsedMilliseconds < 6000) Thread.Sleep(50);
        Assert.True(RunUntil(bots, () => bots.Connection(0).Disconnected, 3000), "closed");
        Assert.Equal(DisconnectCode.InputTimeout, bots.Connection(0).Code);
        Assert.False(bots.Connection(0).Retryable);
        RunUntil(bots, () => false, 1500);
        Assert.Equal(0, bots.Reconnects);
    }

    [Fact]
    public void FourBots_PlayAWholeMatch_AndTheNextRoundStarts()
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Bots/BotPartsTests.cs', [
(r"""    }

    [Fact]
    public void Runner_LogsEachDisconnectOnce_AndStopsWhenAllAreGone()
""",
 r"""    }

    // Phase 10 D11: --reconnect true|false, off by default.
    [Fact]
    public void Options_Reconnect_IsOffByDefault_AndParsed()
    {
        Assert.True(BotOptions.TryParse(Array.Empty<string>(), out BotOptions defaults, out _));
        Assert.False(defaults.Reconnect);
        Assert.True(BotOptions.TryParse(new[] { "--reconnect", "true" }, out BotOptions on, out string? error), error);
        Assert.True(on.Reconnect);
        Assert.True(BotOptions.TryParse(new[] { "--reconnect", "false" }, out BotOptions off, out _));
        Assert.False(off.Reconnect);
        Assert.False(BotOptions.TryParse(new[] { "--reconnect", "maybe" }, out _, out _));
    }

    // D11 with D10's rule: a first connect that fails is no reason to reconnect, even with --reconnect true.
    [Fact]
    public void Runner_WithReconnect_DoesNotRetryAFirstConnectThatFails()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;   // free now, nobody listens

        var lines = new List<string>();
        var options = new BotOptions { Port = port, Count = 1, ConnectIntervalMs = 0, StatsIntervalSeconds = 1000, Reconnect = true };
        using var runner = new BotRunner(options, lines.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        runner.Run(cts.Token);

        Assert.False(cts.IsCancellationRequested, "Run did not return on its own");
        Assert.Equal(0, runner.Reconnects);
        Assert.True(runner.Connection(0).Retryable);   // ConnectionFailed is a network loss; the runner still did not retry
    }

    [Fact]
    public void Runner_LogsEachDisconnectOnce_AndStopsWhenAllAreGone()
"""),
])
print('p10_t5_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `BotOptions.Reconnect`, `BotConnection.Code`·`Retryable`, `BotRunner.Reconnects`가 없다(CS0117, CS1061).

- [ ] **Step 3: 봇 재접속을 구현한다**

봇은 Client와 같은 표(`DisconnectCodes.ShouldReconnect`)를 쓴다. 재접속은 새 `BotConnection`(Seq 1부터)과 새 `BotBrain`으로 한다. 바뀐 연결이 쌓은 송수신 수는 Stats 줄 합계에 남긴다.

**전체 교체** `Server/src/ProjectH.Bots/Program.cs`:

```csharp
using ProjectH.Bots;

// Phase 7: headless bots for match and load tests (Docs/LoadTest.md).
// Example: dotnet run -c Release --project Server/src/ProjectH.Bots -- --count 16 --port 7777
const string Usage = "Options: --host 127.0.0.1 --port 7777 --count 1-50 --seed 1 --duration 0 (s, 0 = until Ctrl+C) " +
                     "--connect-interval-ms 100 --name-prefix bot --stats-interval 10 --reconnect false";

if (!BotOptions.TryParse(args, out BotOptions options, out string? error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // stop the loop and close the sockets instead of killing the process
    cancel.Cancel();
};

using var runner = new BotRunner(options, Console.WriteLine);
Console.WriteLine($"Starting {options.Count} bots against {options.Host}:{options.Port} (seed {options.Seed}).");
runner.Run(cancel.Token);
Console.WriteLine("Bots stopped.");
return 0;
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t5_bots.py`로 저장하고 `python <경로>/p10_t5_bots.py E:/popol/ProjectH`로 실행한다. `BotConnection.cs`, `BotOptions.cs`, `BotRunner.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Server/src/ProjectH.Bots/BotConnection.cs', [
(r"""            DisconnectReason = info.Reason.ToString();
""",
 r"""            // Phase 10 D1, D11: the server's code, and the same reconnect rule as the Unity client (DisconnectCodes).
            bool remoteClose = info.Reason == LiteNetLib.DisconnectReason.RemoteConnectionClose;
            Code = remoteClose && info.AdditionalData != null
                ? DisconnectCodes.Read(info.AdditionalData.GetRemainingBytesSpan())
                : DisconnectCode.None;
            bool networkLoss = info.Reason is LiteNetLib.DisconnectReason.Timeout or LiteNetLib.DisconnectReason.ConnectionFailed
                or LiteNetLib.DisconnectReason.HostUnreachable or LiteNetLib.DisconnectReason.NetworkUnreachable;
            Retryable = DisconnectCodes.ShouldReconnect(remoteClose, Code, networkLoss);
            DisconnectReason = Code == DisconnectCode.None ? info.Reason.ToString() : $"{info.Reason} ({Code})";
"""),
(r"""    public string DisconnectReason { get; private set; } = string.Empty;
""",
 r"""    public string DisconnectReason { get; private set; } = string.Empty;
    public DisconnectCode Code { get; private set; }
    // Phase 10 D11: whether this disconnect may be retried (DisconnectCodes.ShouldReconnect).
    public bool Retryable { get; private set; }
"""),
(r"""                if (JoinMatchResponse.TryRead(ref r, out var response) && response.Result == JoinResult.Ok)
""",
 r"""                // Phase 10 D2: Resumed is a join too (the server's grace kept this bot's character).
                if (JoinMatchResponse.TryRead(ref r, out var response) &&
                    (response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed))
"""),
])

edit('Server/src/ProjectH.Bots/BotOptions.cs', [
(r"""    public int StatsIntervalSeconds { get; set; } = 10;
""",
 r"""    public int StatsIntervalSeconds { get; set; } = 10;
    // Phase 10 D11: reconnect after a retryable disconnect (off by default, so load numbers stay comparable).
    public bool Reconnect { get; set; }
"""),
(r"""                "--stats-interval" => SetInt(value, v => parsed.StatsIntervalSeconds = v),
""",
 r"""                "--stats-interval" => SetInt(value, v => parsed.StatsIntervalSeconds = v),
                "--reconnect" => SetBool(value, v => parsed.Reconnect = v),
"""),
(r"""
    private static bool SetInt(string value, Action<int> set)
""",
 r"""
    private static bool SetBool(string value, Action<bool> set)
    {
        if (!bool.TryParse(value, out bool parsed)) return false;
        set(parsed);
        return true;
    }

    private static bool SetInt(string value, Action<int> set)
"""),
])

edit('Server/src/ProjectH.Bots/BotRunner.cs', [
(r"""public sealed class BotRunner : IDisposable
{
    public const int MaxCatchUpTicks = 3;
""",
 r"""// Phase 10 D11: with Reconnect, a bot whose established connection drops for a retryable reason (the client's table,
// DisconnectCodes.ShouldReconnect) gets a new connection and brain after ReconnectDelaySeconds, at most
// DisconnectCodes.MaxReconnectAttempts times until it has joined again.
public sealed class BotRunner : IDisposable
{
    public const int MaxCatchUpTicks = 3;
    public const double ReconnectDelaySeconds = 1.0;
"""),
(r"""    private readonly bool[] _disconnectLogged;   // each bot's disconnect is logged once, when Step first sees it
""",
 r"""    private readonly bool[] _disconnectLogged;   // each bot's disconnect is logged once, when Step first sees it
    // Phase 10 D11, per bot: when the next connection is due (NaN = none), attempts since the bot last joined, and
    // whether the current connection was ever established (a first connect that fails is not retried).
    private readonly double[] _reconnectAt;
    private readonly int[] _attempts;
    private readonly bool[] _established;
    private long _reconnects;
    // Totals of the connections a reconnect replaced, so the stats line keeps counting across them.
    private long _retiredInputs;
    private long _retiredPackets;
    private long _retiredBytes;
"""),
(r"""        _disconnectLogged = new bool[options.Count];
""",
 r"""        _disconnectLogged = new bool[options.Count];
        _reconnectAt = new double[options.Count];
        Array.Fill(_reconnectAt, double.NaN);
        _attempts = new int[options.Count];
        _established = new bool[options.Count];
"""),
(r"""    public int Count => _connections.Length;
""",
 r"""    public int Count => _connections.Length;
    public long Reconnects => _reconnects;
"""),
(r"""            BotConnection connection = _connections[i];
            if (!connection.Disconnected) connection.Update(elapsedMs);
""",
 r"""            if (!double.IsNaN(_reconnectAt[i]) && start >= _reconnectAt[i]) Reconnect(i);
            BotConnection connection = _connections[i];
            if (!connection.Disconnected) connection.Update(elapsedMs);
            if (connection.Connected) _established[i] = true;
            if (connection.View.Joined) _attempts[i] = 0;
"""),
(r"""                    _log($"{_options.BotName(i)} disconnected: {connection.DisconnectReason}");
""",
 r"""                    _log($"{_options.BotName(i)} disconnected: {connection.DisconnectReason}");
                    ScheduleReconnect(i, connection, start);
"""),
(r"""        if (_loopCount < LoopSampleCount) _loopCount++;
""",
 r"""        if (_loopCount < LoopSampleCount) _loopCount++;
    }

    private void ScheduleReconnect(int i, BotConnection connection, double now)
    {
        bool cycle = _established[i] || _attempts[i] > 0;
        if (!_options.Reconnect || !connection.Retryable || !cycle || _attempts[i] >= DisconnectCodes.MaxReconnectAttempts) return;
        _attempts[i]++;
        _reconnectAt[i] = now + ReconnectDelaySeconds;
    }

    // A fresh connection (its own Seq from 1, as the server expects after a resume) and a fresh brain, same name.
    private void Reconnect(int i)
    {
        _reconnectAt[i] = double.NaN;
        BotConnection old = _connections[i];
        _retiredInputs += old.InputsSent;
        _retiredPackets += old.PacketsIn;
        _retiredBytes += old.BytesIn;
        old.Dispose();
        _connections[i] = new BotConnection();
        _brains[i] = new BotBrain(unchecked(_options.Seed + i));
        _disconnectLogged[i] = false;
        _established[i] = false;
        _reconnects++;
        _log($"{_options.BotName(i)} reconnecting (attempt {_attempts[i]}/{DisconnectCodes.MaxReconnectAttempts})");
        _connections[i].Connect(_options.Host, _options.Port, _options.BotName(i));
"""),
(r"""        long inputs = 0, packets = 0, bytes = 0;
""",
 r"""        long inputs = _retiredInputs, packets = _retiredPackets, bytes = _retiredBytes;
"""),
(r"""             $"bytesIn/s={(bytes - _lastBytes) / seconds:F0} loopMs p95={LoopP95():F2}");
""",
 r"""             $"bytesIn/s={(bytes - _lastBytes) / seconds:F0} loopMs p95={LoopP95():F2} reconnects={_reconnects}");
"""),
(r"""    // True once every bot has been started and every one of them is gone: nothing is left to run.
""",
 r"""    // True once every bot has been started and every one of them is gone for good (no reconnect pending): nothing is
    // left to run.
"""),
(r"""            if (!_connections[i].Disconnected) return false;
""",
 r"""            if (!_connections[i].Disconnected || !double.IsNaN(_reconnectAt[i])) return false;
"""),
])
print('p10_t5_bots ok')
```

- [ ] **Step 4: Unity Client 재접속을 구현한다**

`NetClient`는 끊기 코드를 읽어 `LastError`를 사람이 읽는 문장으로 만들고, 재접속 여부를 표로 정한다. `GameClient`는 재접속 상태 기계를 가진다(`_reconnectAttempt`, `_reconnectPending`, `_reconnectAt`, `_established`). `Update`에서 `Poll` 바로 다음의 `UpdateReconnect`가 정해진 시각에 다시 접속한다. 게임 쪽 할당은 끊김·재접속 때의 로그 문자열뿐이다(개발용 IMGUI 패널은 원래 그리면서 할당한다). `DevConnectPanel`은 "Reconnecting n/3"을 보여 준다(IMGUI, 개발용). Unity 코드는 이 저장소의 테스트로 돌릴 수 없다. 같은 사이클 규칙은 봇 테스트(`Runner_WithReconnect_DoesNotRetryAFirstConnectThatFails`, `BotsWithReconnect_ComeBackAfterAMatchReset`)로 확인하고, Unity 쪽은 컴파일(Step 5)과 Editor 확인(Task 7의 `Client.md` 확인 항목)으로 본다.

**전체 교체** `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`:

```csharp
using ProjectH.Client.Game;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectH.Client.Bootstrap
{
    // Development-only connect UI (IMGUI). IMGUI allocates while drawing, which is acceptable for a
    // dev panel; it hides itself after joining (F1 toggles) and must not become the game UI.
    [RequireComponent(typeof(GameClient))]
    public sealed class DevConnectPanel : MonoBehaviour
    {
        private GameClient _client;
        private string _host;
        private string _port;
        private string _devId;
        private bool _visible = true;
        private bool _wasJoined;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            LaunchArgs args = LaunchArgs.FromCommandLine();
            _host = args.Host;
            _port = args.Port.ToString();
            _devId = args.DevPlayerId;
            if (args.AutoConnect) _client.Connect(_host, args.Port, _devId);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) _visible = !_visible;

            bool joined = _client.State == ClientState.Joined;
            if (joined && !_wasJoined) _visible = false;
            if (!joined && _wasJoined) _visible = true;
            _wasJoined = joined;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            GUILayout.BeginArea(new Rect(10, 10, 340, 190), GUI.skin.box);
            GUILayout.Label($"State: {_client.State}   RTT: {_client.RoundTripMs} ms   Entity: {_client.MyEntityId}");
            if (!string.IsNullOrEmpty(_client.LastError)) GUILayout.Label(_client.LastError);
            // Phase 10 D10: Connect below stops the automatic reconnect and starts over.
            if (_client.ReconnectAttempt > 0)
                GUILayout.Label($"Reconnecting {_client.ReconnectAttempt}/{DisconnectCodes.MaxReconnectAttempts}");

            if (_client.State == ClientState.Disconnected)
            {
                _host = Field("Host", _host);
                _port = Field("Port", _port);
                _devId = Field("DevPlayerId", _devId);
                if (GUILayout.Button("Connect") && int.TryParse(_port, out int port)) _client.Connect(_host, port, _devId);
            }
            else if (GUILayout.Button("Disconnect"))
            {
                _client.Disconnect();
            }

            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   R: reload   1/2/3: weapon   Esc: unlock");
            GUILayout.Label("E: pick up   G: drop weapon   4: Medkit   5: Shield Cell");
            GUILayout.EndArea();
        }

        private static string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            value = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
            return value;
        }
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p10_t5_client.py`로 저장하고 `python <경로>/p10_t5_client.py E:/popol/ProjectH`로 실행한다. `GameClient.cs`, `NetClient.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)


edit('Client/Assets/Scripts/Game/GameClient.cs', [
(r"""        private const float DefaultAimRange = 300f;
""",
 r"""        private const float DefaultAimRange = 300f;
        // Phase 10 D10: the n-th reconnect attempt waits 1, 2, 4 s, so all three fit in the server's 10 s grace.
        private const float FirstReconnectDelaySeconds = 1f;
"""),
(r"""        private bool _fireBlockedUntilRelease;
""",
 r"""        private bool _fireBlockedUntilRelease;
        // Phase 10 D10: where the last Connect went, and the automatic reconnect. An attempt counts until a join
        // succeeds; _reconnectAt is the unscaled time the pending attempt starts (only while _reconnectPending).
        // _established: the current connection got as far as connected (a first connect that fails is not retried).
        private string _host;
        private int _port;
        private string _devPlayerId;
        private int _reconnectAttempt;
        private bool _reconnectPending;
        private float _reconnectAt;
        private bool _established;
"""),
(r"""
        public void Connect(string host, int port, string devPlayerId) => _net.Connect(host, port, devPlayerId);

        public void Disconnect() => _net.Disconnect();
""",
 r"""        // Phase 10 D10: 0 = no automatic reconnect running; otherwise the attempt (1..MaxReconnectAttempts).
        public int ReconnectAttempt => _reconnectAttempt;

        // A manual connect: stops any automatic reconnect and starts over.
        public void Connect(string host, int port, string devPlayerId)
        {
            CancelReconnect();
            _host = host;
            _port = port;
            _devPlayerId = devPlayerId;
            _net.Connect(host, port, devPlayerId);
        }

        public void Disconnect()
        {
            CancelReconnect();
            _net.Disconnect();
        }

        private void CancelReconnect()
        {
            _reconnectAttempt = 0;
            _reconnectPending = false;
        }
"""),
(r"""            _net = new NetClient();
""",
 r"""            _net = new NetClient();
            _net.Connected += OnConnected;
"""),
(r"""            _net.Poll();
            _input.Update();
""",
 r"""            _net.Poll();
            UpdateReconnect();
            _input.Update();
"""),
(r"""        {
            _net.Joined -= OnJoined;
""",
 r"""        {
            _net.Connected -= OnConnected;
            _net.Joined -= OnJoined;
"""),
(r"""        private void OnJoined(JoinMatchResponse response)
        {
            if (response.Result != JoinResult.Ok)
            {
                Debug.LogWarning($"Join failed: {response.Result}");
                return;
            }
""",
 r"""        // Phase 10 D10: the pending attempt starts once its time has come. Connect sets State to Connecting at once, or
        // leaves it Disconnected when it failed right away (LastError says why): then the cycle ends.
        private void UpdateReconnect()
        {
            if (!_reconnectPending || Time.unscaledTime < _reconnectAt || _net.State != ClientState.Disconnected) return;
            _reconnectPending = false;
            _net.Connect(_host, _port, _devPlayerId);
            if (_net.State == ClientState.Disconnected) _reconnectAttempt = 0;
        }

        private void OnConnected()
        {
            _established = true;
        }

        private void OnJoined(JoinMatchResponse response)
        {
            // Phase 10 D2: Resumed is our own character back; everything else arrives as after a join.
            if (response.Result != JoinResult.Ok && response.Result != JoinResult.Resumed)
            {
                Debug.LogWarning($"Join failed: {response.Result}");
                return;
            }
            _reconnectAttempt = 0;
"""),
(r"""            Debug.Log($"Disconnected: {reason}");
""",
 r"""            Debug.Log($"Disconnected: {reason}");
            ScheduleReconnect();
        }

        // Phase 10 D10: retry only what DisconnectCodes allows (a lost connection, a match reset), only for a connection
        // that was established or an attempt of a running cycle, and at most MaxReconnectAttempts times until a join.
        private void ScheduleReconnect()
        {
            bool cycle = _established || _reconnectAttempt > 0;
            _established = false;
            if (!_net.LastDisconnectRetryable || !cycle || _host == null || _reconnectAttempt >= DisconnectCodes.MaxReconnectAttempts)
            {
                CancelReconnect();
                return;
            }
            _reconnectAttempt++;
            _reconnectPending = true;
            _reconnectAt = Time.unscaledTime + FirstReconnectDelaySeconds * (1 << (_reconnectAttempt - 1));
            Debug.Log($"Reconnecting in {FirstReconnectDelaySeconds * (1 << (_reconnectAttempt - 1))} s (attempt {_reconnectAttempt}/{DisconnectCodes.MaxReconnectAttempts})");
"""),
])

edit('Client/Assets/Scripts/Net/NetClient.cs', [
(r"""        public string LastError { get; private set; }
""",
 r"""        public string LastError { get; private set; }
        // Phase 10 D1, D10: why the last connection ended (None unless the server closed it with a code), and whether
        // that kind of end may be retried (DisconnectCodes.ShouldReconnect, the same table the bots use).
        public DisconnectCode LastDisconnectCode { get; private set; }
        public bool LastDisconnectRetryable { get; private set; }
"""),
(r"""            string reason = disconnectInfo.Reason.ToString();
            if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                reason = "Rejected: " + (RejectReason)disconnectInfo.AdditionalData.GetByte();
            }
            LastError = reason;
            Disconnected?.Invoke(reason);
""",
 r"""            DisconnectReason why = disconnectInfo.Reason;
            bool remoteClose = why == DisconnectReason.RemoteConnectionClose;
            LastDisconnectCode = remoteClose && disconnectInfo.AdditionalData != null
                ? DisconnectCodes.Read(disconnectInfo.AdditionalData.GetRemainingBytesSpan())
                : DisconnectCode.None;
            bool networkLoss = why == DisconnectReason.Timeout || why == DisconnectReason.ConnectionFailed ||
                               why == DisconnectReason.HostUnreachable || why == DisconnectReason.NetworkUnreachable;
            LastDisconnectRetryable = DisconnectCodes.ShouldReconnect(remoteClose, LastDisconnectCode, networkLoss);

            string reason = why.ToString();
            if (why == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                reason = "Rejected: " + (RejectReason)disconnectInfo.AdditionalData.GetByte();
            }
            else if (LastDisconnectCode != DisconnectCode.None)
            {
                reason = Describe(LastDisconnectCode);
            }
            LastError = reason;
            Disconnected?.Invoke(reason);
        }

        // Constant strings: no allocation.
        private static string Describe(DisconnectCode code)
        {
            switch (code)
            {
                case DisconnectCode.ServerShutdown: return "Server shut down";
                case DisconnectCode.Kicked: return "Kicked: too many invalid packets";
                case DisconnectCode.JoinTimeout: return "Join timed out";
                case DisconnectCode.InputTimeout: return "Disconnected: no input for too long";
                case DisconnectCode.ServerError: return "Server error: the match was reset";
                default: return "Disconnected";
            }
"""),
(r"""                        if (response.Result == JoinResult.Ok) State = ClientState.Joined;
""",
 r"""                        // Phase 10 D2: Resumed is a join into our own character; the server resends the full state.
                        if (response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed) State = ClientState.Joined;
"""),
])
print('p10_t5_client ok')
```

- [ ] **Step 5: Unity 컴파일을 확인한다**

이전 Phase의 스크래치 `UnityCompile` 프로젝트를 쓴다. 이 프로젝트는 실제 Unity 6000.3.24f1 DLL(`Editor/Data/Managed/UnityEngine/*.dll`)과 `Client/Library/ScriptAssemblies`의 `UnityEngine.UI`·`Unity.InputSystem`, Client의 `LiteNetLib.dll`을 참조한다. 그리고 `Client/Assets/Scripts/**/*.cs`와 `Shared/Runtime/**/*.cs`를 netstandard2.1, C# 9로 컴파일한다.
- 위치: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj`
- 원본 csproj의 Compile 경로가 `E:/popol/ProjectH`이므로 그대로 쓴다.
- 없어졌으면 같은 구성으로 스크래치에 다시 만든다.

Run: `dotnet build <위 경로>/UnityCompile.csproj`
Expected: 경고 0, 오류 0. 계획 단계에서 이 계획의 Client·Shared 코드로 같은 결과였다.

그 다음 사용자에게 Unity Editor를 포커스해 달라고 요청한다. 자동 import가 끝나면 `Editor.log`(`%LOCALAPPDATA%/Unity/Editor/Editor.log`)의 새 줄에 `error CS`가 없는지 확인한다. Unity가 `Shared/Runtime/Protocol/DisconnectCode.cs.meta`를 만든다. 사용자가 Editor를 열 수 없으면 "Unity 확인 대기"로 기록하고 넘어간다.

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Bot"`
Expected: 모두 통과.

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 724개다(717 통과, 7개 건너뜀).

- [ ] **Step 7: Commit** — `feat: reconnect automatically on retryable disconnects (Unity client 1/2/4 s, bots --reconnect)`

---

### Task 6: Fuzz 테스트 (시드 고정)

**Files:**
- Create: `Server/tests/ProjectH.Server.Tests/Shared/ProtocolFuzzTests.cs`
- Create: `Server/tests/ProjectH.Server.Tests/Game/InputFuzzTests.cs`
- Create: `Server/tests/ProjectH.Server.Tests/Integration/FuzzIntegrationTests.cs`

**Interfaces:**
- Consumes: 모든 Shared 파서, `Match`, `RoyaleHarness.LobbyRingDrops`, `TestGameData.ShortZonesJson`, Task 1 `HardeningIntegrationTests` 도우미

이 Task는 제품 코드를 바꾸지 않는다. 계획 단계에서 세 테스트는 지금 코드로 통과했다. Fuzz가 실패하면 그것은 진짜 결함이다.
- 시드가 고정이라 같은 입력으로 재현된다.
- 고칠 곳은 `game-core-rules`에 맞춰 정한다. 이동 계산이면 `Shared/Runtime/Simulation`, 패킷이면 그 `TryRead`다.
- 고친 뒤 이 계획에 단계를 더해 보고한다.

- [ ] **Step 1: Fuzz 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/InputFuzzTests.cs`:

```csharp
using System;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 10 §2: seeded inputs full of hostile values (NaN, ±Infinity, huge numbers, random Seq and ViewTick, every
// button) through the real wire parser into Match for hundreds of ticks. Nothing may throw, and every position and
// health value must stay finite and in range.
public class InputFuzzTests
{
    private static readonly float[] Hostile =
    {
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e30f, -1e30f,
        float.Epsilon, -0f, 0f, 1f, -1f, 360f, -720f, 1e6f,
    };

    private static float Value(Random random) =>
        random.Next(3) == 0 ? (float)(random.NextDouble() * 4 - 2) : Hostile[random.Next(Hostile.Length)];

    // Through PlayerInputPacket.Write and TryRead, as NetworkListener sees it (unknown buttons are masked there).
    // Seq: mostly the next one (so most inputs are taken and acted on), sometimes old or repeated ones; near the end
    // also the top of the uint range ("negative" Seq), after which this player's normal inputs are refused.
    private static PlayerInputPacket RandomPacket(Random random, byte[] buffer, ref uint nextSeq, bool endgame)
    {
        var packet = new PlayerInputPacket { Count = (byte)random.Next(1, ProtocolConstants.MaxInputsPerPacket + 1) };
        for (int i = 0; i < packet.Count; i++)
        {
            packet.Set(i, new InputCommand
            {
                Seq = endgame && random.Next(20) == 0 ? uint.MaxValue - (uint)random.Next(3)
                    : random.Next(5) == 0 ? nextSeq - (uint)Math.Min(nextSeq, (uint)random.Next(10))
                    : nextSeq++,
                MoveX = Value(random),
                MoveY = Value(random),
                Yaw = Value(random),
                Buttons = (InputButtons)random.Next(ushort.MaxValue + 1),
                AimYaw = Value(random),
                AimPitch = Value(random),
                ViewTick = Value(random),
            });
        }
        var writer = new PacketWriter(buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = new PacketReader(writer.WrittenSpan);
        Assert.True(reader.TryReadPacketId(out _));
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket read));
        return read;
    }

    private static void AssertSane(Match match, int peers)
    {
        for (int peer = 1; peer <= peers; peer++)
        {
            if (!match.TryGetPlayer(peer, out PlayerEntity p)) continue;
            Assert.True(float.IsFinite(p.State.Position.X) && float.IsFinite(p.State.Position.Y) && float.IsFinite(p.State.Position.Z),
                $"position {p.State.Position} at tick {match.ServerTick}");
            Assert.True(float.IsFinite(p.State.VelocityY) && float.IsFinite(p.State.Yaw), $"velocity/yaw at tick {match.ServerTick}");
            Assert.InRange(p.Health, 0, 100);
            Assert.InRange(p.Shield, 0, 100);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void HostileInputs_NeverBreakTheMatch(bool devRespawn, int seed)
    {
        const int players = 4;
        var random = new Random(seed);
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var match = new Match(new ServerOptions
            {
                MaxPlayers = players, MinPlayers = 2, StartCountdownSeconds = 1, ResultSeconds = 1, DevRespawn = devRespawn,
            },
            TestGameData.Create(zonesJson: TestGameData.ShortZonesJson), static (_, _, _) => { }, TestGameData.CombatLoadout,
            dropPoints: RoyaleHarness.LobbyRingDrops);
        for (int peer = 1; peer <= players; peer++) Assert.Equal(JoinResult.Ok, match.TryJoin(peer, "f" + peer));
        var nextSeq = new uint[players + 1];
        Array.Fill(nextSeq, 1u);

        uint mostTaken = 0;
        for (int tick = 0; tick < 900; tick++)
        {
            for (int peer = 1; peer <= players; peer++)
            {
                if (random.Next(4) != 0) match.EnqueueInput(peer, RandomPacket(random, buffer, ref nextSeq[peer], tick > 800));
            }
            match.Tick();
            AssertSane(match, players);
            for (int peer = 1; peer <= players; peer++)
            {
                if (match.TryGetPlayer(peer, out PlayerEntity p) && p.LastProcessedSeq < uint.MaxValue - 3)
                    mostTaken = Math.Max(mostTaken, p.LastProcessedSeq);
            }
        }
        Assert.True(mostTaken > 300, $"the hostile inputs were hardly taken ({mostTaken})");
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/FuzzIntegrationTests.cs`:

```csharp
using System;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 §2: one client sends seeded random packets over real UDP. Only that client is closed (Kicked); the other
// keeps getting snapshots.
public sealed class FuzzIntegrationTests
{
    [Fact]
    public void APeerSendingRandomPackets_IsKicked_AndTheOtherPlaysOn()
    {
        using GameLoop server = StartServer();
        using var fuzzer = Join(server, "fuzzer");
        using var other = Join(server, "other");
        var random = new Random(7);

        for (int i = 0; i < 200 && !fuzzer.Disconnected; i++)
        {
            var packet = new byte[random.Next(1, 65)];
            random.NextBytes(packet);
            fuzzer.SendRaw(packet);
            if (i % 20 == 0) Pump.Until(() => false, 10, fuzzer, other);
        }
        Assert.True(Pump.Until(() => fuzzer.Disconnected, 3000, fuzzer, other), "fuzzer kicked");
        Assert.Equal(DisconnectCode.Kicked, fuzzer.DisconnectCode);

        int before = other.SnapshotsReceived;
        Assert.True(SendInputsUntil(other, () => other.SnapshotsReceived > before + 5, 3000), "the other keeps getting snapshots");
        Assert.False(other.Disconnected);
        Assert.Equal(1, server.Match.PlayerCount);
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/ProtocolFuzzTests.cs`:

```csharp
using System;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 10 §2: seeded random bytes through every parser the server runs on client data and every parser the client
// runs on server data. A parser may refuse the bytes; it must never throw. Same seed = same bytes, so a failure
// reproduces.
public class ProtocolFuzzTests
{
    private const int Buffers = 100_000;

    [Fact]
    public void RandomBytes_NeverThrow_InAnyParser()
    {
        var random = new Random(10);
        var buffer = new byte[ProtocolConstants.MaxPacketSize + 100];
        int parsed = 0;
        for (int n = 0; n < Buffers; n++)
        {
            // Mostly short (where the length checks matter), every 100th up to a full datagram and beyond.
            int length = n % 100 == 0 ? random.Next(buffer.Length + 1) : random.Next(65);
            random.NextBytes(buffer.AsSpan(0, length));
            // Half of them start with a valid packet id, so the body parsers also see plausible headers.
            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.MatchResult + 1);
            ReadOnlySpan<byte> data = buffer.AsSpan(0, length);

            parsed += ServerSide(data) + ClientSide(data);
            ClientSide(length > 0 ? data.Slice(1) : data);   // the body after the id byte
        }
        Assert.True(parsed > 0, "nothing ever parsed: the fuzz does not reach the parsers' success paths");
    }

    // What NetworkListener runs on client bytes.
    private static int ServerSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (ConnectRequestData.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (r.TryReadPacketId(out PacketId id))
        {
            ok++;
            if (id == PacketId.PlayerInput && PlayerInputPacket.TryRead(ref r, out _)) ok++;
        }
        r = new PacketReader(data);
        if (PlayerInputPacket.TryRead(ref r, out _)) ok++;
        if (DisconnectCodes.Read(data) != DisconnectCode.None) ok++;
        return ok;
    }

    // What NetClient (and the bots) run on server bytes, body only (the id byte already read).
    private static int ClientSide(ReadOnlySpan<byte> data)
    {
        int ok = 0;
        var r = new PacketReader(data);
        if (JoinMatchResponse.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerSpawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header))
        {
            ok++;
            for (int i = 0; i < header.Count && SnapshotEntity.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (SnapshotSelf.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WeaponCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ShotFired.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (HitConfirmed.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (DamageTaken.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerDied.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PlayerRespawned.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemCatalogPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (WorldItemsPacket.TryReadHeader(ref r, out int count))
        {
            ok++;
            for (int i = 0; i < count && WorldItemData.TryRead(ref r, out _); i++) ok++;
        }
        r = new PacketReader(data);
        if (ItemSpawnedPacket.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ItemRemoved.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (InventoryState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (PickupResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (ZoneState.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (MatchResult.TryRead(ref r, out _)) ok++;
        return ok;
    }
}
```

- [ ] **Step 2: 테스트를 실행한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Fuzz"`
Expected: 모두 통과(4개: 파서 1, `Match` 2, UDP 1). 파서 Fuzz는 1초 안쪽이다.

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 728개다(721 통과, 7개 건너뜀).

- [ ] **Step 3: Commit** — `test: add seeded fuzz tests for every parser, hostile inputs and a fuzzing peer (Phase 10)`

---

### Task 7: 문서

**Files:**
- Modify: `Docs/Networking.md`, `Docs/Server.md`, `Docs/Client.md`, `Docs/Bots.md`, `Docs/Database.md`, `Docs/Architecture.md`

숫자와 이름은 코드와 같게 쓴다. 이 Task의 내용은 Task 1–6의 코드에서 나온 것만이다.

- [ ] **Step 1: `Docs/Networking.md`**

- **"Packets" 절:**
  - `ProtocolVersion` 8을 적는다.
  - `JoinResult`에 `Resumed`(3)를 더한다.
  - "Join 결과" 줄에 Resumed를 더한다(같은 Entity로 돌아온다. 늦은 합류와 같은 전체 상태가 이어진다).
- **"접속 순서" 절:** 4번 뒤에 Resume 순서를 더한다.
  - `JoinMatchResponse(Resumed, 같은 Entity)` → `WeaponCatalog` → `ItemCatalog` → `WorldItems` → `InventoryState` → 전원의 `PlayerSpawned`(자기 포함, 지금 위치) → `MatchState` → `ZoneState`
  - 다른 플레이어에게는 아무것도 보내지 않는다(떠난 적이 없다).
- **새 절 "끊기와 재접속 (Phase 10)":**
  - **끊는 코드:** `DisconnectCode` 표(값, 뜻, Client의 재접속 여부). LiteNetLib 끊기 데이터 1바이트다. 빈 데이터·모르는 값은 `None`이다.
  - **재접속 표(`DisconnectCodes.ShouldReconnect`):**
    - 원격 종료는 `ServerError`만 다시 한다.
    - Timeout·ConnectionFailed·HostUnreachable·NetworkUnreachable은 다시 한다.
    - 직접 끊기·거절·코드 없는 원격 종료는 다시 하지 않는다.
    - 처음 연결이 실패한 것은 사이클을 시작하지 않는다.
  - **재접속 유예:**
    - 대상: 경기 중(`Playing`·`FinalPhase`) 살아 있는 참가자, 서버가 끊지 않은 연결, 기본 10초다.
    - 유예 중: 캐릭터는 남는다. 입력 없음(0.5초 뒤 정지), 맞으면 죽음, Zone 피해 있음.
    - 같은 DevPlayerId로 Join하면 Resume한다. 연결된 같은 id가 있으면 새 플레이어다. 여럿이면 먼저 끊긴 것이다.
    - 죽거나 시간이 다 되면 나간다. 시간이 다 된 경우는 탈락·Drop·이탈 기록이다. 판 재시작에서 비운다.
    - 위험: 인증이 없어 남의 id로 가져갈 수 있다(spec D2).
  - **Timeout:**
    - Join Timeout 5초
    - Input Timeout 10초(죽음·관전 중에도. Client와 봇은 Join한 동안 늘 입력을 보낸다)
    - Game Loop의 Tick 수로 잰다.
    - 디버거로 Client를 10초 넘게 멈추면 `InputTimeout`으로 끊긴다(재접속하지 않는다).
- **"Validation (서버)" 절:**
  - 잘못된 패킷 이유 7가지(`BadPacketReason`)와 Kick 코드(`Kicked`)를 적는다.
  - 받기 핸들러 예외는 그 peer의 잘못된 패킷(`HandlerException`)이다.
  - 거절을 이유별로 센다(로그 Debug).
  - Fuzz 테스트(시드 고정, 파서·입력 값·UDP)를 적는다.

- [ ] **Step 2: `Docs/Server.md`**

- **"실행" 설정 표:** `ReconnectGraceSeconds` 10(0–60, 0 끔), `JoinTimeoutSeconds` 5(1–60), `InputTimeoutSeconds` 10(0 끔, 또는 2–300) 세 줄을 더한다.
- **"스레드와 소유권" 절:**
  - Game Loop 스레드는 Background다(멈춰도 프로세스 종료를 막지 않는다).
  - `StallWatchdog` Timer 스레드(1초마다 마지막 Tick 시각을 `Volatile`로 읽기만 한다)를 표에 더한다.
- **Tick 루프 문장:**
  - `DrainControl` → `DrainInput` → `SweepPeers`(stale 정리 + Join·Input Timeout) → `Match.Tick`(맨 앞 `ExpireGrace`)
  - 예외는 "Tick 예외는 삼키고 계속" 대신 "예외 복구" 절을 가리킨다.
- **새 절 "예외 복구 (Phase 10 D6)":**
  - 반복 전체 보호(Stats·대기 예외도 스레드가 산다, `loopFailures`)
  - Tick 예외 로그(Tick 번호·경기 상태·판 번호, 구간마다 첫 하나)
  - `SimHz × 3`번 연속 실패 → 모든 peer `ServerError`로 끊고 `Match`를 새로 만든다(기록 없음)
  - 10분 안에 3번 → Critical, `StopApplication`, 종료 코드 1
  - sink 예외와 `UnhandledException`·`UnobservedTaskException` 로그
- **"Lifetime"의 종료 항목:**
  - 순서: Tick 멈춤(스레드 Join 5초, 넘으면 Critical) → `DisconnectAll(ServerShutdown)` → peer가 0이 되거나 1초 → `NetManager.Stop` → Writer Drain
  - `GameServerService.StopAsync`는 이 일을 Thread Pool에서 하고 Host 토큰까지만 기다린다. 진행 중인 판은 기록하지 않는다.
- **"Lifetime"의 Session 항목:** 유예 목록(`_graced`, 최대 MaxPlayers, 제거 조건 4가지)을 더한다.
- **"관측" 절:**
  - Health 줄의 항목을 모두 적는다(Task 4 `LogHealth`의 키 그대로, 누적값).
  - Meter 계측기 목록과 `dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server`
  - Watchdog(2초, Critical 한 번, 회복 Warning, `stalls`)
  - 로그 시각(`appsettings.json` `Logging:Console:FormatterOptions:TimestampFormat`)
  - 로그 수준: 접속·Join·이탈·재접속은 Information, Kick Warning, 거절 Debug
  - "Tick 예외는 통계 주기당 1회만 로그한다"는 그대로 둔다.

- [ ] **Step 3: `Docs/Client.md`**

- **"Lifetime" 절:** 이벤트 구독 수를 20개로 고친다(`Connected` 추가).
- **새 절 "끊김과 자동 재접속 (Phase 10)":**
  - `NetClient.LastError`가 코드를 사람이 읽는 문장으로 보여 준다(예: "Server shut down", "Kicked: too many invalid packets").
  - 재접속 조건(위 표 + 연결된 적이 있거나 사이클 중)
  - 1·2·4초, 최대 3번. Join(또는 Resumed)이 되면 0으로 돌아간다.
  - `DevConnectPanel`의 "Reconnecting n/3"
  - Connect·Disconnect를 누르면 멈춘다.
  - Resumed는 Join과 같다(서버가 전체 상태를 다시 보낸다. 예측기는 자기 `PlayerSpawned` 위치에서 새로 시작하고 Seq는 1부터다).
- **"실행과 두 Client 확인" 절:** 확인 항목을 더한다.
  - 경기 중 Client 하나를 끄고 10초 안에 같은 `-devId`로 다시 켜면 같은 캐릭터로 돌아온다.
  - 서버를 Ctrl+C로 끄면 "Server shut down"이 보이고 재접속하지 않는다.
  - Unity Editor에서의 확인은 사용자가 한다.

- [ ] **Step 4: `Docs/Bots.md`**

- **옵션 표:** `--reconnect` | false | true면 다시 해도 되는 끊김(Client와 같은 표)에서 1초 뒤 같은 이름으로 다시 접속한다. 끊김마다 최대 3번이고, 처음 접속 실패는 다시 하지 않는다.
- **Stats 줄 설명:** `reconnects`를 더한다. "끊긴 봇은 다시 접속하지 않는다(재접속은 Phase 10)" 문장을 "`--reconnect true`가 아니면 다시 접속하지 않는다"로 바꾼다.
- **"판단 규칙"(또는 "봇이 쓰는 정보") 절:** 죽어 있거나 관전 중이어도 빈 입력을 보낸다(서버 Input Timeout, Client와 같다)를 더한다.

- [ ] **Step 5: `Docs/Database.md`**

- **"실패 처리와 종료" 절의 첫 항목을 D8로 바꾼다.**
  - 시작할 때 DB가 없어도 Writer는 계속 돈다(Warning 로그).
  - 기록마다 스키마 준비부터 다시 시도하고, 실패하면 보통 재시도(`MaxAttempts`)를 거쳐 `Failed`가 된다.
  - `Enabled=false`일 때만 `Discarded`다.
  - DB가 오래 없으면 기록마다 연결 시간 초과(5초)만큼 Writer가 늦어진다. 큐 16개로 버틴다.
- **"카운터와 로그" 표:**
  - `Failed`·`Discarded` 뜻을 고친다.
  - 시작 로그 문장을 새 것(`database unavailable at start (...); each finished match will try again.`)으로 바꾼다.
  - Health 줄의 `db saved/failed/discarded/dropped`와 Meter `projecth.db_records`를 더한다.
- **"테스트" 절:** `TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears`(TCP 중계기)를 더한다.

- [ ] **Step 6: `Docs/Architecture.md`**

- 첫 줄을 "Phase 10 Hardening 기준"으로 바꾸고, 설계 근거에 `Docs/specs/2026-10-01-phase10-hardening-design.md`(끊기 코드, 재접속 유예, Timeout, 예외 복구, 관측)를 더한다.
- Mermaid의 Server에 `Loop --> Health[HealthCounters] --> Meter[ServerMeter: ProjectH.Server]`와 `Watchdog[StallWatchdog] -.reads.-> Loop`를 더한다.
- Shared 행에 `DisconnectCode`·재접속 표(`DisconnectCodes`)를 더한다.

- [ ] **Step 7: Commit** — `docs: Phase 10 hardening`

---

## Phase 완료 확인

1. **빌드·테스트.**
   - `dotnet build Server/ProjectH.Server.slnx --no-incremental`: 경고 0
   - `dotnet test Server/ProjectH.Server.slnx`: 모두 통과. 728개 중 721 통과, MySQL 7개 건너뜀.
   - 두세 번 반복한다. UDP 통합 테스트가 Timeout에 민감하다. 한 번이라도 실패하면 원인을 찾는다. 같은 테스트가 다시 실패하면 테스트 서버의 Timeout을 늘리지 말고 보고한다.
2. **DB가 있으면 `PROJECTH_TEST_MYSQL`을 켜고 `Persistence` 테스트를 돌린다.** 모두 통과, 건너뜀 0이어야 한다(Task 3 Step 4 명령). 컨테이너는 이 계획이 띄우지 않는다. 꺼져 있으면 "DB 테스트 대기"로 보고한다.
3. **봇 50명 부하 측정(Phase 8 비교).** 컨트롤러가 실행한다. `Docs/LoadTest.md` Phase 8과 같은 조건이다.
   - Release
   - 서버: `--Server:Port=7790 --Server:MaxPlayers=100 --Server:DevRespawn=true`
   - 봇: `--port 7790 --count 50 --duration 120 --connect-interval-ms 30`. `--reconnect`는 기본 false로 둔다.
   - 2분 실행. 집계는 Stats 줄 12개 중 처음 2개를 버린 10개다.

   ```bash
   dotnet build Server/ProjectH.Server.slnx -c Release
   dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7790 --Server:MaxPlayers=100 --Server:DevRespawn=true > load-server-50.log 2>&1 &
   # 4초 뒤
   dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7790 --count 50 --duration 120 --connect-interval-ms 30 > load-bots-50.log 2>&1
   # 서버는 자기가 띄운 pid로만 끈다
   ```

   **비교 기준:** Phase 8 50명 Tick p95 0.11 ms(p99 0.16, max 0.24). 이번 Tick p95(10개 줄의 최댓값)가 이보다 나빠지지 않아야 한다(spec §2 회귀).
   - 측정은 0.01 ms 단위이고 실행 사이 편차가 있다. 크게 벗어나면 한 번 더 잰다. 그래도 나쁘면 보고한다.
   - Health 줄도 확인한다: `kicks ... inputTimeout=0`, `joinTimeout=0`, `tickFailures=0`, `stalls=0`.
   - 봇이 죽어 있는 동안 빈 입력을 보내므로 `pktIn/s`가 Phase 8(1315)보다 조금 높을 수 있다(Spec과 다른 점 3).
   - 결과는 `Docs/LoadTest.md`에 "Phase 10 확인" 짧은 절로 남긴다. 숫자는 실제로 잰 값만 적는다.
4. **Unity.** Task 5 Step 5의 `UnityCompile` 빌드가 경고·오류 0이다. Editor 확인은 사용자에게 받거나 "대기"로 기록한다.
5. **범위 확인.**
   - `git diff --stat main -- Client`에는 `Client/Assets/Scripts`의 세 파일만 있어야 한다(`NetClient.cs`, `GameClient.cs`, `DevConnectPanel.cs`).
   - `git diff --stat main -- Shared`에는 `Protocol`의 세 파일(`DisconnectCode.cs`, `ProtocolConstants.cs`, `RejectReason.cs`)만 있어야 한다. Unity가 만든 `DisconnectCode.cs.meta`가 있으면 그것도 함께 커밋한다.
6. **Push.** `github-push` 스킬로 `main`에 Squash Commit·Push한다.
