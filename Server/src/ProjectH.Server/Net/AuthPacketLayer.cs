using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib.Layers;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Review fix B3: the server's datagram authentication, a thin LiteNetLib PacketLayerBase over Shared SessionKeys (wire rule
// in SessionAuth). Keys are kept per remote endpoint, because the layer sees endpoints, not peers.
//   - Registered by NetworkListener (receive thread) after the session key is decrypted and before Accept, so the accept
//     answer is already sealed.
//   - Retired, not removed, at the disconnect (OnPeerDisconnected runs on whatever thread closed the peer): LiteNetLib keeps
//     resending the disconnect packet after the event, and those resends must stay sealed or a verified client drops them
//     and loses its DisconnectCode. A retired entry still seals and opens; a LiteNetLib ConnectRequest that fails to open on
//     it is taken as a new connection from the same endpoint and stripped unverified. Review B round 1: any other datagram
//     that fails to open on it is dropped, so a forged ShutdownOk cannot end the disconnect resends. Review B round 2: counted
//     as authDropsRetired, apart from authDrops (a live key), because a same-port reconnect's ShutdownOk to the cookie reject
//     lands here too and authDrops must stay 0 in a normal run.
//   - Removed after RetireMs by the receive thread at the next Register (expired entries), and the oldest retired entries are
//     evicted early when more than MaxRetired are waiting. A new Register for the same endpoint replaces the entry.
// Size: at most the live connections (<= MaxPlayers) + MaxRetired retired entries, plus the entries retired since the last
// Register (each disconnect retires one live entry, so that is still <= MaxPlayers).
// Threads: the table is a ConcurrentDictionary read by the receive thread (inbound) and by every sending thread (outbound),
// written by the receive thread (Register, sweep) and, for the retire mark only, by the disconnecting thread (Volatile /
// Interlocked). No lock here; SessionKeys holds the one leaf lock (its send side).
public sealed class AuthPacketLayer : PacketLayerBase
{
    private sealed class Entry
    {
        public Entry(SessionKeys keys) => Keys = keys;
        public SessionKeys Keys { get; }
        public long RetireAtMs;   // 0 = live; otherwise removable from this time (Environment.TickCount64)
    }

    // Keyed by address and port through EndPointComparer: LiteNetLib hands the send callback its NetPeer, an IPEndPoint whose
    // GetHashCode is its own (not the address's), so the default comparer would miss the entry and send unsealed.
    // Review B round 1: LiteNetLib's packet property is the low 5 bits of the first byte (connection number and fragment flag
    // above them), and ConnectRequest is property 6. Checked against LiteNetLib 2.1.4 (NetPacket.Property is RawData[0] & 0x1F,
    // PacketProperty.ConnectRequest = 6) and pinned by AuthPacketLayerTests on a captured ConnectRequest; recheck on an upgrade.
    internal const byte PropertyMask = 0x1F;
    internal const byte ConnectRequestProperty = 6;
    private readonly ConcurrentDictionary<IPEndPoint, Entry> _keys = new(EndPointComparer.Instance);
    private readonly HealthCounters _health;
    private readonly long _retireMs;
    private readonly int _maxRetired;
    private int _retiredCount;
    private readonly List<KeyValuePair<IPEndPoint, Entry>> _sweep = new();   // receive thread only, reused

    // Address and port equality, whatever IPEndPoint subclass is passed (NetPeer overrides GetHashCode). No allocation.
    private sealed class EndPointComparer : IEqualityComparer<IPEndPoint>
    {
        public static readonly EndPointComparer Instance = new();

        // 기능: 주소와 포트가 같은지 본다.
        // 입력: a·b - 비교할 endpoint.
        // 출력: 같으면 true.
        public bool Equals(IPEndPoint? a, IPEndPoint? b) =>
            ReferenceEquals(a, b) || (a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address));

        // 기능: 주소와 포트로 해시를 만든다.
        // 입력: endPoint - endpoint.
        // 출력: 해시 값.
        public int GetHashCode(IPEndPoint endPoint) => HashCode.Combine(endPoint.Address.GetHashCode(), endPoint.Port);
    }

    // 기능: 서버 인증 계층을 만든다(데이터그램마다 꼬리 20 B).
    // 입력: health - authDrops를 셀 곳, retireMs - 끊긴 뒤 키를 남겨 둘 시간(DisconnectTimeoutMs + 1000), maxRetired - 은퇴 항목 상한.
    // 출력: 키가 하나도 없는 AuthPacketLayer.
    public AuthPacketLayer(HealthCounters health, long retireMs, int maxRetired) : base(ProtocolLimits.AuthTagBytes)
    {
        _health = health;
        _retireMs = retireMs;
        _maxRetired = Math.Max(1, maxRetired);
    }

    // Entries in the table (live and retired). Takes the dictionary's locks: tests and diagnostics only, never per datagram.
    internal int Count => _keys.Count;

    // 기능: endpoint의 세션 키를 등록한다(있던 항목은 바꾼다). 먼저 만료된 은퇴 항목을 지우고, 은퇴 항목이 상한을 넘으면 오래된 것부터 지운다.
    //   수신 스레드 전용(연결 요청 처리 중, Accept 전).
    // 입력: endPoint - 원격 주소(복사해 둔다), keys - 이 연결의 키, nowMs - 단조 증가 ms 시계.
    // 출력: 반환값 없음. 이 endpoint의 데이터그램은 이제 봉인·검증된다.
    public void Register(IPEndPoint endPoint, SessionKeys keys, long nowMs)
    {
        Sweep(nowMs);
        var key = new IPEndPoint(endPoint.Address, endPoint.Port);
        var entry = new Entry(keys);
        _keys.AddOrUpdate(key, entry, (_, old) =>
        {
            if (Volatile.Read(ref old.RetireAtMs) != 0) Interlocked.Decrement(ref _retiredCount);
            old.Keys.Dispose();
            return entry;
        });
    }

    // 기능: 끊긴 연결의 키에 은퇴 표시를 한다(지우지 않는다: 끊기 패킷 재전송이 계속 봉인되게). 같은 키일 때만. 어느 스레드에서 불러도 된다.
    // 입력: endPoint - 원격 주소, keys - 그 연결의 키(같은 endpoint에 새 키가 들어왔으면 아무것도 하지 않는다), nowMs - 시계.
    // 출력: 반환값 없음.
    public void Retire(IPEndPoint endPoint, SessionKeys keys, long nowMs)
    {
        if (!_keys.TryGetValue(endPoint, out Entry? entry) || !ReferenceEquals(entry.Keys, keys)) return;
        if (Interlocked.CompareExchange(ref entry.RetireAtMs, nowMs + _retireMs, 0) == 0) Interlocked.Increment(ref _retiredCount);
    }

    // 기능: 만료된 은퇴 항목을 지우고, 은퇴 항목이 상한을 넘으면 은퇴 시각이 이른 것부터 지운다(키를 해제한다). 수신 스레드 전용.
    // 입력: nowMs - 시계.
    // 출력: 반환값 없음.
    private void Sweep(long nowMs)
    {
        if (Volatile.Read(ref _retiredCount) == 0) return;
        _sweep.Clear();
        foreach (var pair in _keys)
        {
            long at = Volatile.Read(ref pair.Value.RetireAtMs);
            if (at != 0) _sweep.Add(pair);
        }
        _sweep.Sort((a, b) => Volatile.Read(ref a.Value.RetireAtMs).CompareTo(Volatile.Read(ref b.Value.RetireAtMs)));
        int over = _sweep.Count - _maxRetired + 1;   // room for the one about to retire after this register
        foreach (var pair in _sweep)
        {
            if (Volatile.Read(ref pair.Value.RetireAtMs) > nowMs && over <= 0) break;
            if (_keys.TryRemove(pair))
            {
                Interlocked.Decrement(ref _retiredCount);
                pair.Value.Keys.Dispose();
            }
            over--;
        }
        _sweep.Clear();
    }

    // 기능: 받은 데이터그램의 꼬리를 처리한다. 키가 있으면 검증하고 벗긴다(실패면 버리고 authDrops. 단 은퇴 항목에서 실패한 LiteNetLib
    //   ConnectRequest는 같은 endpoint의 새 연결로 보고 검증 없이 벗긴다. 리뷰 B 1차: 다른 종류는 은퇴 항목에서도 버린다. 리뷰 B 2차:
    //   은퇴 항목에서 버린 것은 authDropsRetired로 따로 센다).
    //   키가 없으면(연결 요청 단계) 검증 없이 벗긴다. 수신 스레드, 할당 없음.
    // 입력: endPoint - 보낸 곳, data·length - 데이터그램.
    // 출력: 반환값 없음. 통과면 length가 꼬리만큼 줄고, 버리면 length = 0.
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        if (_keys.TryGetValue(endPoint, out Entry? entry))
        {
            if (entry.Keys.TryOpen(data, ref length)) return;
            if (Volatile.Read(ref entry.RetireAtMs) != 0)
            {
                if (length > 0 && (data[0] & PropertyMask) == ConnectRequestProperty && SessionAuth.TryStripUnverified(ref length)) return;
                _health.AddAuthDropRetired();
            }
            else
            {
                _health.AddAuthDrop();
            }
            length = 0;
            return;
        }
        if (!SessionAuth.TryStripUnverified(ref length)) length = 0;
    }

    // 기능: 보낼 데이터그램에 꼬리를 붙인다. 키가 있으면 봉인(SessionKeys의 송신 leaf lock), 없으면 0 꼬리. 어느 송신 스레드에서든, 할당 없음
    //   (LiteNetLib는 계층 크기만큼 여유를 둔다. 모자라면 한 번 새 버퍼를 만든다).
    // 입력: endPoint - 받는 곳, data·offset·length - 보낼 부분.
    // 출력: 반환값 없음. length가 AuthTagBytes 는다.
    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (data.Length - offset - length < ProtocolLimits.AuthTagBytes)
        {
            var bigger = new byte[offset + length + ProtocolLimits.AuthTagBytes];
            Buffer.BlockCopy(data, 0, bigger, 0, offset + length);
            data = bigger;
        }
        if (_keys.TryGetValue(endPoint, out Entry? entry)) entry.Keys.Seal(data, offset, ref length);
        else SessionAuth.WriteUnsignedTail(data, offset, ref length);
    }
}
