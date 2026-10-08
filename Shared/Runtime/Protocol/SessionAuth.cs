using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ProjectH.Shared.Protocol
{
    // Review fix B3 (SEC-2, SEC-7): the per-connection keys and the datagram tail every side (server, Unity client, bots, test
    // client) puts on and checks, so all of them follow one rule. Pure code: no LiteNetLib, no UnityEngine; each side wraps it
    // in a thin PacketLayerBase adapter.
    //
    // Wire rule (every datagram, connection requests and LiteNetLib's own packets included):
    //   payload ‖ counter u32 LE ‖ HMAC-SHA256(K_dir, payload ‖ counter)[0..16]   (ProtocolLimits.AuthTagBytes = 20)
    //   - A side that has keys for the peer seals with SessionKeys.Seal and opens with SessionKeys.TryOpen (a failure is
    //     dropped and counted).
    //   - A side with no keys for the peer writes a zero tail (WriteUnsignedTail) and strips a received tail without checking
    //     it (TryStripUnverified). The server has no keys before it accepts a request (the request itself, the cookie and
    //     reject answers); a client strips unverified only until its first datagram opens.
    // Keys: the client makes a 32-byte session key K (RandomNumberGenerator) and sends it RSA-encrypted in the connect
    // request. K_c2s = HMAC(K, "c2s"), K_s2c = HMAC(K, "s2c"), K_resume = HMAC(K, "resume").
    // Not encrypted: integrity and replay protection only (spec D0-2).
    public sealed class SessionKeys : IDisposable
    {
        // The send side runs on several threads (LiteNetLib sends acks on its receive thread, resends and pings on its logic
        // thread, and the game thread may flush), so the send counter and the send HMAC are behind _sendLock.
        // Lock ordering: _sendLock is a leaf lock. Inside it only the HMAC of one datagram is computed: no callback, no I/O,
        // no other lock. It may be taken while LiteNetLib holds its own locks (it calls the layer from inside them), which is
        // safe because nothing inside _sendLock calls back into LiteNetLib or takes any other lock.
        private readonly object _sendLock = new object();
        private readonly HMACSHA256 _sendMac;   // _sendLock
        private readonly byte[] _sendHash = new byte[32];   // _sendLock
        private uint _sendCounter;   // _sendLock
        private bool _disposed;   // _sendLock

        // The receive side runs on the one thread that receives this peer's datagrams (one socket, one receive thread), so
        // it needs no lock.
        private readonly HMACSHA256 _recvMac;
        private readonly byte[] _recvHash = new byte[32];
        private bool _recvStarted;
        private uint _recvHigh;
        private ulong _recvWindow;   // bit i = counter (_recvHigh - i) was accepted
        private long _rejected;

        // 기능: 세션 키 32 B에서 방향별 HMAC 키와 Resume 키를 파생한다(연결마다 한 번, HMAC 객체 2개를 만든다).
        // 입력: sessionKey - Client가 만든 32 B 키, isServer - 서버 쪽이면 true(보낼 때 s2c, 받을 때 c2s), Client 쪽이면 false.
        // 출력: 송신 counter 0, 수신 창이 빈 SessionKeys. 길이가 32 B가 아니면 ArgumentException.
        public SessionKeys(ReadOnlySpan<byte> sessionKey, bool isServer)
        {
            if (sessionKey.Length != ProtocolLimits.SessionKeyBytes)
                throw new ArgumentException("The session key must be " + ProtocolLimits.SessionKeyBytes + " bytes.", nameof(sessionKey));
            byte[] c2s = SessionAuth.Derive(sessionKey, "c2s");
            byte[] s2c = SessionAuth.Derive(sessionKey, "s2c");
            _sendMac = new HMACSHA256(isServer ? s2c : c2s);
            _recvMac = new HMACSHA256(isServer ? c2s : s2c);
            ResumeKey = SessionAuth.Derive(sessionKey, "resume");
        }

        // HMAC(K, "resume"), 32 bytes: the key a later connection proves it holds to resume this one's character (B4).
        public byte[] ResumeKey { get; }

        // Datagrams TryOpen refused (written by the receive thread, readable from any thread).
        public long Rejected => Interlocked.Read(ref _rejected);

        // 기능: data[offset..offset+length) 뒤에 counter 4 B + MAC 16 B를 붙인다. 송신 leaf lock 안에서 HMAC만 한다. 할당 없음.
        //   Dispose 뒤에는 0 꼬리를 붙인다(받는 쪽이 버린다).
        // 입력: data - 버퍼(offset+length 뒤에 AuthTagBytes 이상 여유가 있어야 한다), offset·length - 보낼 부분.
        // 출력: 반환값 없음. length가 AuthTagBytes 늘어난다. 여유가 모자라면 ArgumentException.
        public void Seal(byte[] data, int offset, ref int length)
        {
            if (data.Length - offset - length < ProtocolLimits.AuthTagBytes) throw new ArgumentException("No room for the tail.", nameof(data));
            int tail = offset + length;
            lock (_sendLock)
            {
                if (_disposed)
                {
                    Array.Clear(data, tail, ProtocolLimits.AuthTagBytes);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(data, tail, 4), _sendCounter++);
                    _sendMac.TryComputeHash(new ReadOnlySpan<byte>(data, offset, length + 4), _sendHash, out _);
                    Buffer.BlockCopy(_sendHash, 0, data, tail + 4, ProtocolLimits.MacBytes);
                }
            }
            length += ProtocolLimits.AuthTagBytes;
        }

        // 기능: data[0..length) 끝의 꼬리를 검증하고, 재전송·역순(64칸 창 밖) counter를 거부한 뒤 꼬리를 벗긴다. 수신 스레드 전용, 할당 없음.
        //   MAC이 맞을 때만 창을 바꾼다(위조 패킷이 창을 움직이지 못한다).
        // 입력: data - 받은 데이터그램(0부터), length - 길이.
        // 출력: 통과면 true와 length -= AuthTagBytes, 아니면 false(Rejected가 는다. 호출자가 버린다).
        public bool TryOpen(byte[] data, ref int length)
        {
            if (length < ProtocolLimits.AuthTagBytes || _disposed) return Reject();
            int body = length - ProtocolLimits.AuthTagBytes;
            _recvMac.TryComputeHash(new ReadOnlySpan<byte>(data, 0, body + 4), _recvHash, out _);
            if (!CryptographicOperations.FixedTimeEquals(new ReadOnlySpan<byte>(_recvHash, 0, ProtocolLimits.MacBytes),
                    new ReadOnlySpan<byte>(data, body + 4, ProtocolLimits.MacBytes)))
                return Reject();
            uint counter = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, body, 4));
            if (!AcceptCounter(counter)) return Reject();
            length = body;
            return true;
        }

        // 기능: 64칸 sliding window로 counter를 받는다. 첫 counter는 어떤 값이든 받는다.
        // 입력: counter - MAC이 맞은 데이터그램의 counter.
        // 출력: 처음 보는 창 안(또는 앞) counter면 true, 재전송이거나 64 이상 뒤처졌으면 false.
        private bool AcceptCounter(uint counter)
        {
            if (!_recvStarted)
            {
                _recvStarted = true;
                _recvHigh = counter;
                _recvWindow = 1;
                return true;
            }
            if (counter > _recvHigh)
            {
                uint shift = counter - _recvHigh;
                _recvWindow = shift >= 64 ? 1UL : (_recvWindow << (int)shift) | 1UL;
                _recvHigh = counter;
                return true;
            }
            uint behind = _recvHigh - counter;
            if (behind >= 64) return false;
            ulong bit = 1UL << (int)behind;
            if ((_recvWindow & bit) != 0) return false;
            _recvWindow |= bit;
            return true;
        }

        // 기능: 수신 거부를 센다.
        // 입력: 없음.
        // 출력: 언제나 false(TryOpen이 그대로 돌려준다).
        private bool Reject()
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        // 기능: HMAC 객체를 해제한다. 송신 HMAC은 송신 Lock 안에서 해제하고 그 뒤 Seal은 0 꼬리를 쓴다. 수신 HMAC은 수신 스레드에서만
        //   쓰므로 이 메서드는 수신 스레드(또는 수신이 더 없는 때)에서 부른다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            lock (_sendLock)
            {
                if (_disposed) return;
                _disposed = true;
                _sendMac.Dispose();
            }
            _recvMac.Dispose();
        }
    }

    // Review fixes B3, B4: the key derivation, the keyless tail rule and the resume proof.
    public static class SessionAuth
    {
        // 기능: 세션 키에서 용도 이름으로 32 B 키를 파생한다(HMAC-SHA256(K, ASCII(label))).
        // 입력: sessionKey - 세션 키, label - "c2s"·"s2c"·"resume".
        // 출력: 32 B 키(새 배열, 연결마다 한 번).
        public static byte[] Derive(ReadOnlySpan<byte> sessionKey, string label)
        {
            using (var mac = new HMACSHA256(sessionKey.ToArray()))
                return mac.ComputeHash(Encoding.ASCII.GetBytes(label));
        }

        // 기능: Resume 키 HMAC(K, "resume")을 구한다(SessionKeys.ResumeKey와 같다).
        // 입력: sessionKey - 세션 키 32 B.
        // 출력: 32 B Resume 키.
        public static byte[] DeriveResumeKey(ReadOnlySpan<byte> sessionKey) => Derive(sessionKey, "resume");

        // 기능: 키가 없는 쪽이 보내는 데이터그램에 0으로 채운 꼬리 20 B를 붙인다(서버의 쿠키·거절 응답 등).
        // 입력: data - 버퍼(offset+length 뒤에 AuthTagBytes 이상 여유), offset·length - 보낼 부분.
        // 출력: 반환값 없음. length가 AuthTagBytes 는다. 여유가 모자라면 ArgumentException.
        public static void WriteUnsignedTail(byte[] data, int offset, ref int length)
        {
            if (data.Length - offset - length < ProtocolLimits.AuthTagBytes) throw new ArgumentException("No room for the tail.", nameof(data));
            Array.Clear(data, offset + length, ProtocolLimits.AuthTagBytes);
            length += ProtocolLimits.AuthTagBytes;
        }

        // 기능: 키가 없는 쪽(또는 Client의 첫 검증 성공 전)이 받은 데이터그램의 꼬리를 검증 없이 벗긴다.
        // 입력: length - 받은 길이.
        // 출력: 꼬리가 들어갈 길이면 true와 length -= AuthTagBytes, 짧으면 false(호출자가 length = 0으로 버린다).
        public static bool TryStripUnverified(ref int length)
        {
            if (length < ProtocolLimits.AuthTagBytes) return false;
            length -= ProtocolLimits.AuthTagBytes;
            return true;
        }

        // 기능: Resume 증명 16 B를 만든다 = HMAC(resumeKey, nonce LE ‖ 새 세션 키 32 B ‖ UTF-8(devPlayerId))[0..16].
        //   새 연결의 세션 키를 묶어, 엿본 증명을 다른 연결(다른 세션 키)이 쓰지 못하게 한다.
        // 입력: resumeKey - 이전 연결의 Resume 키, nonce - 이 키로 보내는 Resume 순번(1부터), newSessionKey - 이번 연결의 세션 키,
        //   devPlayerId - 이름, proof - 받을 곳(16 B 이상).
        // 출력: 반환값 없음. proof 앞 16 B가 채워진다.
        public static void ComputeResumeProof(ReadOnlySpan<byte> resumeKey, uint nonce, ReadOnlySpan<byte> newSessionKey,
            string devPlayerId, Span<byte> proof)
        {
            byte[] name = Encoding.UTF8.GetBytes(devPlayerId ?? string.Empty);
            byte[] message = new byte[4 + newSessionKey.Length + name.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(message, 0, 4), nonce);
            newSessionKey.CopyTo(new Span<byte>(message, 4, newSessionKey.Length));
            Buffer.BlockCopy(name, 0, message, 4 + newSessionKey.Length, name.Length);
            byte[] hash;
            using (var mac = new HMACSHA256(resumeKey.ToArray())) hash = mac.ComputeHash(message);
            new ReadOnlySpan<byte>(hash, 0, ProtocolLimits.ResumeProofBytes).CopyTo(proof);
        }

        // 기능: Resume 증명을 상수 시간으로 검사한다.
        // 입력: resumeKey - 유예 캐릭터의 Resume 키, nonce·newSessionKey·devPlayerId - 요청 값, proof - 받은 증명.
        // 출력: 같으면 true. 키가 없거나 길이가 틀리면 false.
        public static bool VerifyResumeProof(ReadOnlySpan<byte> resumeKey, uint nonce, ReadOnlySpan<byte> newSessionKey,
            string devPlayerId, ReadOnlySpan<byte> proof)
        {
            if (resumeKey.Length == 0 || proof.Length != ProtocolLimits.ResumeProofBytes) return false;
            Span<byte> expected = stackalloc byte[ProtocolLimits.ResumeProofBytes];
            ComputeResumeProof(resumeKey, nonce, newSessionKey, devPlayerId, expected);
            return CryptographicOperations.FixedTimeEquals(expected, proof);
        }
    }
}
