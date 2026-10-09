using System;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D1, D3-D5 over real UDP: the server says why it closes a connection, times out connections that never
// join and players that stop sending input, and counts invalid packets and rejects by reason.
public sealed class HardeningIntegrationTests
{
    // 기능: 전투 장비를 든 실제 UDP 서버를 포트 0에 띄운다(결과 1초, 끊김 판정 1초, 통계 60초).
    // 입력: maxPlayers - 최대 인원, joinTimeout - Join 제한 시간(초), inputTimeout - 입력 제한 시간(초, 0 = 없음), grace - 재접속 유예(초),
    //   minPlayers - 시작 최소 인원, countdown - 시작 카운트다운(초), statsQueries - 전적 조회 큐(null = 없음).
    // 출력: 스레드가 시작된 GameLoop(호출자가 Dispose한다).
    internal static GameLoop StartServer(int maxPlayers = 4, int joinTimeout = 5, int inputTimeout = 10, int grace = 10,
        int minPlayers = 2, int countdown = 10, ProjectH.Server.Persistence.StatsQueryQueue? statsQueries = null)
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
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout, statsQueries: statsQueries);
        loop.Start();
        return loop;
    }

    // 기능: 새 HeadlessClient로 서버에 접속해 Join을 보내고 응답이 기대한 결과인지 확인한다.
    // 입력: server - 접속할 서버, devId - 플레이어 이름, expected - 기대하는 Join 결과.
    // 출력: Join 응답을 받은 HeadlessClient(호출자가 Dispose한다). 3초 안에 접속·응답이 없거나 결과가 다르면 테스트가 실패한다.
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
        using GameLoop server = StartServer(inputTimeout: 3);   // the smallest valid value with a 1 s disconnect timeout
        using var silent = Join(server, "silent");
        using var active = Join(server, "active");

        Assert.True(SendInputsUntil(active, () => silent.Disconnected, 5000, silent), "silent player timed out");
        Assert.Equal(DisconnectCode.InputTimeout, silent.DisconnectCode);

        // The active player keeps going well past the timeout.
        Assert.False(SendInputsUntil(active, () => active.Disconnected, 2500), "active player kept");
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.InputTimeout));
    }

    // 기능: 조건이 참이 될 때까지 sender가 약 Tick마다 정지 입력을 보내며 모든 Client를 Poll한다.
    // 입력: sender - 입력을 보낼 Client, condition - 기다릴 조건, timeoutMs - 제한 시간(ms), others - 함께 Poll할 Client들.
    // 출력: 제한 시간 안에 조건이 참이 되면 true, 아니면 false.
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

    // Review fix A4 (SEC-7): a packet whose inputs are all refused (here: a Seq far past the window) is not input. Before,
    // it kept refreshing the input timeout, so a player locked out by an injected Seq was never closed.
    [Fact]
    public void AnInputPacket_WithOnlyRejectedSeqs_DoesNotRefreshTheInputTimeout()
    {
        using GameLoop server = StartServer(inputTimeout: 3);
        using var stuck = Join(server, "stuck");
        stuck.SendMove(0f, 0f, 0f);   // Seq 1, taken: the window starts there
        Assert.True(Pump.Until(() => stuck.LastAckInputSeq == 1, 3000, stuck), "first input taken");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!stuck.Disconnected && clock.ElapsedMilliseconds < 6000)
        {
            stuck.SendInputWithSeq(new InputCommand { Seq = 0xFFFFFFF0 });
            Pump.Until(() => stuck.Disconnected, 33, stuck);
        }
        Assert.True(stuck.Disconnected, "timed out");
        Assert.Equal(DisconnectCode.InputTimeout, stuck.DisconnectCode);
        Assert.True(server.Health.InputSeqDrops > 0);
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

        // More than the bucket of SimHz = 30 at once: the rest are dropped and count as InputRate (never a kick, server
        // review M5).
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

    // Review fix A3 (SEC-4): the first request has no cookie and gets one back in a RejectForce (no peer on the server);
    // the client sends it again with the cookie at once and is accepted. A normal connect costs one more round trip only.
    [Fact]
    public void AConnect_WithoutACookie_GetsARejectForceCookie_AndTheRetryIsAccepted()
    {
        using GameLoop server = StartServer();
        using var client = Join(server, "cookie");
        Assert.Equal(1, client.CookieRetries);
        Assert.False(client.Disconnected);
        Assert.Equal(1, server.Health.CookieChallenges);
        Assert.Equal(0, server.Health.CookieRejects);
        Assert.Equal(1, server.Health.Connections);
    }

    // A wrong cookie (a forged or replayed one) is refused without a peer and counted; the answer is a fresh cookie, which
    // this client does not use.
    [Fact]
    public void AConnect_WithAWrongCookie_IsRejectedWithoutAPeer()
    {
        using GameLoop server = StartServer();
        using var forged = new HeadlessClient();
        forged.ConnectWithCookie(server.LocalPort, "forged", new byte[ProtocolLimits.CookieBytes]);
        Assert.True(Pump.Until(() => forged.Disconnected, 3000, forged), "refused");
        Assert.Equal(DisconnectReason.ConnectionRejected, forged.DisconnectReason);
        Assert.Equal(ProtocolLimits.CookieBytes, forged.RejectDataLength);
        Assert.Equal(1, server.Health.CookieRejects);
        Assert.Equal(0, server.Health.Connections);
        Assert.Equal(0, server.Listener.Manager.ConnectedPeersCount);
    }

    // A client of the previous protocol (v18 layout: version, then the name) is told VersionMismatch, not BadRequest, before
    // the cookie step (the answer is one byte, no peer).
    [Fact]
    public void AnOldClient_IsToldVersionMismatch_BeforeTheCookieStep()
    {
        using GameLoop server = StartServer();
        using var old = new HeadlessClient();
        old.ConnectRaw(server.LocalPort, new byte[] { 18, 0, 3, (byte)'o', (byte)'l', (byte)'d' });
        Assert.True(Pump.Until(() => old.Disconnected, 3000, old), "refused");
        Assert.Equal(RejectReason.VersionMismatch, old.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.VersionMismatch));
        Assert.Equal(0, server.Health.CookieChallenges);
    }

    // Review fixes B2, B3: the client's session key goes RSA-encrypted in the request; from the accept on, every datagram both
    // ways is sealed and opens (the client is verified, no side drops anything), and the game runs over it.
    [Fact]
    public void AClient_WithTheSessionKeyAndCookie_Joins_AndTheSnapshotArrives_WithNoAuthDrops()
    {
        using GameLoop server = StartServer();
        using var client = Join(server, "sealed");
        Assert.True(SendInputsUntil(client, () => client.SnapshotsReceived > 5 && client.LastAckInputSeq > 3, 3000), "snapshots and acks");
        Assert.True(client.Verified);
        Assert.Equal(0, client.AuthDrops);
        Assert.Equal(0, server.Health.AuthDrops);
    }

    // A blob encrypted with another public key (a client configured for another server) is refused as BadRequest after the
    // cookie step, with no peer.
    [Fact]
    public void ARequest_ForAnotherServerKey_IsRefusedAsBadRequest()
    {
        using GameLoop server = StartServer();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var other = new HeadlessClient();
        other.Connect(server.LocalPort, "other", serverPublicKeyXml: rsa.ToXmlString(false));
        Assert.True(Pump.Until(() => other.Disconnected, 3000, other), "refused");
        Assert.Equal(DisconnectReason.ConnectionRejected, other.DisconnectReason);
        Assert.Equal(RejectReason.BadRequest, other.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.BadRequest));
        Assert.Equal(0, server.Health.Connections);
    }

    // Review B rounds 1 and 2: blobs that do not decrypt penalize their source from the third within 60 s, so one address cannot
    // keep making the receive thread run the RSA decrypt with garbage, while one misconfigured client does not shut out every
    // client behind the same NAT address at its first try. Two are BadRequest only (a good client still gets in); the third is
    // a BadRequest too, and the next request from that address, a good one, is refused as Penalized.
    [Fact]
    public void TheThirdBlobThatDoesNotDecrypt_PenalizesTheSource()
    {
        using GameLoop server = StartServer();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        for (int i = 0; i < 2; i++) RefusedAsBadRequest(server, rsa);
        Assert.Equal(0, server.Health.Penalties);
        using (var first = Join(server, "first"))
        {
            Assert.Equal(1, server.Health.Connections);
        }

        RefusedAsBadRequest(server, rsa);
        Assert.Equal(1, server.Health.Penalties);
        using var good = new HeadlessClient();
        good.Connect(server.LocalPort, "good");
        Assert.True(Pump.Until(() => good.Disconnected, 3000, good), "refused");
        Assert.Equal(RejectReason.ServerFull, good.RejectReason);
        Assert.Equal(1, server.Health.PenaltyRejects);
        Assert.Equal(3, server.Health.Rejects(RejectReason.BadRequest));
    }

    // 기능: 다른 서버 키로 암호화한 blob으로 접속해 BadRequest로 거절되는지 확인한다.
    // 입력: server - 서버, rsa - 서버와 다른 RSA 키.
    // 출력: 반환값 없음. 거절되지 않으면 테스트가 실패한다.
    private static void RefusedAsBadRequest(GameLoop server, System.Security.Cryptography.RSA rsa)
    {
        using var other = new HeadlessClient();
        other.Connect(server.LocalPort, "other", serverPublicKeyXml: rsa.ToXmlString(false));
        Assert.True(Pump.Until(() => other.Disconnected, 3000, other), "refused");
        Assert.Equal(RejectReason.BadRequest, other.RejectReason);
    }

    // The server keeps a closed connection's keys for a while (its disconnect resends stay sealed): a client that leaves and
    // connects again from the same local port is taken as a new connection, not dropped as a forgery. Review B round 1: only
    // its ConnectRequests pass the retired entry unverified. The cookie answer is a LiteNetLib reject (a Disconnect packet),
    // and the client's ShutdownOk to it, sealed with the new keys, is dropped and counted there; LiteNetLib has no peer for a
    // rejected request, so nothing waits for that ShutdownOk. Review B round 2: it is counted as authDropsRetired, apart from
    // authDrops (drops on a live key), which stays 0 in a normal run.
    [Fact]
    public void AClient_ThatLeavesAndReconnectsFromTheSamePort_GetsIn()
    {
        using GameLoop server = StartServer();
        using var client = Join(server, "again");
        client.Leave();
        Assert.True(SpinUntil(() => server.Health.DisconnectOthers == 1, 3000), "left");
        Assert.True(Pump.Until(() => client.Disconnected, 3000, client), "closed");

        client.Connect(server.LocalPort, "again");
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connected again");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "joined again");
        Assert.True(Pump.Until(() => client.Verified, 3000, client), "verified with the new keys");
        Assert.Equal(1, client.CookieRetries);
        Assert.Equal(0, server.Health.AuthDrops);          // review B round 2: the live-key signal stays at zero
        Assert.Equal(1, server.Health.AuthDropsRetired);   // that one ShutdownOk
        Assert.Equal(0, client.AuthDrops);
    }

    // The kick code arrives in the sealed disconnect packet (the keys are retired, not removed, at the disconnect event).
    [Fact]
    public void AServerClose_ReachesAVerifiedClient_WithItsCode()
    {
        using GameLoop server = StartServer(joinTimeout: 1);
        using var idle = new HeadlessClient();
        idle.Connect(server.LocalPort, "idle");
        Assert.True(Pump.Until(() => idle.Connected && idle.Verified, 3000, idle), "connected");
        Assert.True(Pump.Until(() => idle.Disconnected, 4000, idle), "timed out");
        Assert.Equal(DisconnectReason.RemoteConnectionClose, idle.DisconnectReason);
        Assert.Equal(DisconnectCode.JoinTimeout, idle.DisconnectCode);
        Assert.Equal(0, idle.AuthDrops);
    }

    // 기능: 조건이 참이 될 때까지 제한 시간 안에서 기다린다.
    // 입력: condition - 기다릴 조건, timeoutMs - 제한 시간(ms).
    // 출력: 제한 시간 안에 조건이 참이 되면 true, 아니면 false.
    private static bool SpinUntil(Func<bool> condition, int timeoutMs) => System.Threading.SpinWait.SpinUntil(condition, timeoutMs);
}
