#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System.Net;
using System.Threading;
using LiteNetLib.Layers;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Net
{
    // Review fix B3: the client's adapter of the Shared datagram rule (SessionAuth). Every datagram gets the 20-byte tail.
    // The client talks to one server, so it holds one set of keys (the current connect request's), not a table.
    //
    // Threads: LiteNetLib calls ProcessInboundPacket on its one receive thread (IPv6 is off, so one socket) and
    // ProcessOutBoundPacket on whichever thread sends (main thread, logic thread, receive thread for acks). The keys and the
    // verified mark are swapped by the main thread (NetClient.Connect / the cookie retry), so both are volatile references.
    // SessionKeys.Seal has its own leaf lock; TryOpen is used only by the receive thread. No lock here.
    public sealed class AuthPacketLayer : PacketLayerBase
    {
        private volatile SessionKeys _keys;
        // The keys a datagram has opened with. "Verified" means _verifiedKeys == _keys, so a new set of keys starts unverified
        // with no separate reset, and a late datagram of an older connection opening with the old keys cannot mark the new
        // keys verified (which would drop the server's unsigned cookie reject of the new request).
        private volatile SessionKeys _verifiedKeys;
        // Keys replaced by the last swap, disposed at the next swap: the receive thread may still be inside TryOpen with them
        // right after a swap, and TryOpen takes no lock.
        private SessionKeys _retired;
        private long _authDrops;

        // 기능: 꼬리 20 B(ProtocolLimits.AuthTagBytes)를 쓰는 계층을 만든다. 키는 첫 Connect가 등록한다.
        // 입력: 없음.
        // 출력: 키가 없는 계층(보내는 데이터그램에 0 꼬리, 받는 꼬리는 검증 없이 벗긴다).
        public AuthPacketLayer() : base(ProtocolLimits.AuthTagBytes)
        {
        }

        // Datagrams dropped after this connect's keys verified one (tail missing, wrong or replayed). Any thread may read it.
        public long AuthDrops => Interlocked.Read(ref _authDrops);

        // 기능: 다음 접속 요청의 키로 바꾼다(접속 요청을 보내기 전에 부른다). 새 키는 아직 검증되지 않은 상태로 시작한다.
        //   바로 전에 밀려난 키는 지금 해제하고, 방금 밀려난 키는 다음 교체 때 해제한다.
        // 입력: keys - 이번 요청의 세션 키에서 만든 SessionKeys(Client 쪽, isServer false). 소유권이 계층으로 넘어온다.
        // 출력: 반환값 없음. 이후 보내는 데이터그램은 새 키로 봉인되고, 받는 데이터그램은 새 키로 처음 열릴 때까지 검증 없이 통과한다.
        public void SetKeys(SessionKeys keys)
        {
            SessionKeys replaced = _keys;
            _keys = keys;
            _retired?.Dispose();
            _retired = replaced;
        }

        // 기능: 계층이 가진 키를 모두 해제한다. NetManager가 멈춘 뒤(_net.Stop) 부른다.
        // 입력: 없음.
        // 출력: 반환값 없음. 키가 없는 계층이 된다.
        public void DisposeKeys()
        {
            SessionKeys current = _keys;
            _keys = null;
            _verifiedKeys = null;
            _retired?.Dispose();
            _retired = null;
            current?.Dispose();
        }

        // 기능: 받은 데이터그램의 꼬리를 검사하고 벗긴다. 수신 스레드에서만 불린다. 할당 없음.
        //   지금 키로 열리면 통과(그 키를 검증됨으로 표시). 지금 키로 아직 아무것도 열리지 않았으면 검증 없이 벗겨 통과
        //   (서버의 쿠키 RejectForce는 키가 없는 서버가 0 꼬리로 보낸다). 검증된 뒤 실패하면 버리고 AuthDrops를 센다.
        // 입력: endPoint - 보낸 쪽(쓰지 않는다), data - 데이터그램(0부터), length - 길이.
        // 출력: 반환값 없음. 통과면 length가 꼬리만큼 줄고, 버리면 length = 0.
        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length < ProtocolLimits.AuthTagBytes)
            {
                length = 0;
                return;
            }
            SessionKeys keys = _keys;
            if (keys != null && keys.TryOpen(data, ref length))
            {
                _verifiedKeys = keys;
                return;
            }
            if (keys == null || _verifiedKeys != keys)
            {
                SessionAuth.TryStripUnverified(ref length);
                return;
            }
            length = 0;
            Interlocked.Increment(ref _authDrops);
        }

        // 기능: 보낼 데이터그램에 꼬리를 붙인다. 키가 있으면 봉인(counter + MAC), 없으면 0 꼬리. 어느 스레드에서나 불린다. 할당 없음.
        // 입력: endPoint - 받는 쪽(쓰지 않는다), data - 버퍼(LiteNetLib이 ExtraPacketSizeForLayer만큼 여유를 둔다), offset·length - 보낼 부분.
        // 출력: 반환값 없음. length가 AuthTagBytes 는다.
        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            SessionKeys keys = _keys;
            if (keys != null) keys.Seal(data, offset, ref length);
            else SessionAuth.WriteUnsignedTail(data, offset, ref length);
        }
    }
}
