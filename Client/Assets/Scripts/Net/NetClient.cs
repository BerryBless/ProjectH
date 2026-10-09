#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Net
{
    public enum ClientState
    {
        Disconnected,
        Connecting,
        Connected,
        Joined,
    }

    public delegate void SnapshotHandler(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count);
    // Phase 15 D10: the whole team marker list, in NetClient's reused arrays (valid only during the call).
    public delegate void TeamMarkersHandler(MarkerPing[] pings, int pingCount, MarkerWaypoint[] waypoints, int waypointCount);
    // Phase 16 D7: the whole supply drop list, in NetClient's reused array (valid only during the call).
    public delegate void SupplyDropsHandler(SupplyDropInfo[] drops, int count);
    // Phase 19 D4: one VehicleStates, its records in NetClient's reused array (valid only during the call).
    public delegate void VehicleStatesHandler(uint serverTick, uint ackInputSeq, VehicleRecord[] records, int count);

    // Review fix A3: what the data of a refused connection is (NetClient.ClassifyReject).
    public enum RejectKind
    {
        None,     // no data, or a length that is neither: a reject without a known reason
        Reason,   // 1 byte: a RejectReason
        Cookie,   // ProtocolLimits.CookieBytes: the server's cookie; send the request again with it
    }

    // Owns the LiteNetLib client. Main thread only: UnsyncedEvents is off and Poll() is called from
    // Update, so every callback below runs on the Unity main thread. Buffers are reused: receiving
    // and sending do not allocate per packet.
    public sealed class NetClient : INetEventListener, IDisposable
    {
        private readonly NetManager _net;
        private readonly NetDataWriter _connectData = new NetDataWriter();
        private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
        private readonly SnapshotEntity[] _snapshotEntities = new SnapshotEntity[ProtocolConstants.MaxSnapshotEntities];
        // Phase 15 D10: TeamMarkers is read into these (TryRead may leave them half written on a bad packet, so the event is
        // raised only after a whole read; the receiver copies what it keeps).
        private readonly MarkerPing[] _markerPings = new MarkerPing[MapMarkerConstants.MaxTeamPings];
        private readonly MarkerWaypoint[] _markerWaypoints = new MarkerWaypoint[MapMarkerConstants.MaxWaypoints];
        // Phase 16 D7: SupplyDrops is read into this (same rule: the event only after a whole read; the receiver copies).
        private readonly SupplyDropInfo[] _supplyDrops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
        // Phase 19 D4: VehicleStates is read into this (same rule: the event only after a whole read; the receiver copies).
        private readonly VehicleRecord[] _vehicles = new VehicleRecord[VehicleSettings.MaxVehicles];
        // The current connection. Events from any other peer (an attempt CancelConnect gave up on) are ignored.
        private NetPeer _server;
        private bool _disposed;
        // LiteNetLib's own connect budget, used by a manual Connect (Phase 10 D10).
        private readonly int _defaultReconnectDelay;
        private readonly int _defaultMaxConnectAttempts;
        // Review fix A3: the address of the current connect, for the cookie retry.
        private string _host;
        private int _port;
        private string _devPlayerId;
        // Review fix A3: the cookie of the server's first reject (copied: the reject's reader is recycled after the
        // callback), sent with the retry. One retry per connect: a second cookie ends the connect. Cleared when the
        // connect succeeds, ends or is given up, and at every new Connect (a cookie is only valid for its address and 30-60 s).
        private readonly byte[] _cookie = new byte[ProtocolLimits.CookieBytes];
        private bool _hasCookie;
        private bool _cookieRetried;
        // Review fix B3: the datagram tail (AuthPacketLayer). It owns the SessionKeys of each request; NetClient keeps a
        // reference to the newest (_connectKeys) for the resume key at join.
        private readonly AuthPacketLayer _authLayer = new AuthPacketLayer();
        private SessionKeys _connectKeys;
        // Review fix B1, B3-1: the pinned server public key (parsed once; null with _publicKeyError when the text is missing or
        // not an RSA key: every Connect then fails with that error, there is no fallback key), the session key generator and
        // the 32-byte session key buffer (filled per request, cleared once the request and its keys are made).
        private readonly RSA _serverKey;
        private readonly string _publicKeyError;
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private readonly byte[] _sessionKey = new byte[ProtocolLimits.SessionKeyBytes];
        // Review fix B4: the resume key of the last joined connection, the name it was earned under and the nonce of its last
        // use. Replaced at every join (Ok or Resumed), dropped by ForgetResume (leaving, another server or name).
        private byte[] _resumeKey;
        private string _resumeName;
        private uint _resumeNonce;
        private readonly byte[] _resumeProof = new byte[ProtocolLimits.ResumeProofBytes];

        // 기능: LiteNetLib 클라이언트를 인증 계층과 서버와 같은 MTU·채널 수·조각 상한으로 만들고 서버 공개키를 읽는다(소켓은 첫 Connect가 연다).
        // 입력: serverPublicKeyXml - 서버 RSA-2048 공개키(RSA.ToXmlString 형식, Unity는 Resources/ServerPublicKey.txt). null이거나
        //   읽을 수 없으면 Connect가 그 이유로 실패한다.
        // 출력: Disconnected 상태의 NetClient. LiteNetLib의 기본 연결 예산을 기억해 둔다(직접 Connect가 되돌릴 값).
        public NetClient(string serverPublicKeyXml)
        {
            _net = new NetManager(this, _authLayer)
            {
                UnsyncedEvents = false,
                AutoRecycle = true,
                DisconnectTimeout = 5000,   // default PingInterval (1000 ms) stays below a quarter of this
                IPv6Enabled = false,   // also keeps one receive thread, which AuthPacketLayer's unlocked TryOpen relies on
                // Review fix B3: the wire budget minus the layer's tail (LiteNetLib does not count it), same as the server.
                MtuOverride = ProtocolLimits.UserMtu,
                ChannelsCount = ProtocolConstants.ChannelCount,   // Phase 13 D13: channel 1 is the building stream
                MaxFragmentsCount = ProtocolLimits.MaxFragments,  // review fix A1: same cap as the server; no packet needs fragments
            };
            _defaultReconnectDelay = _net.ReconnectDelay;
            _defaultMaxConnectAttempts = _net.MaxConnectAttempts;
            _serverKey = ParseServerKey(serverPublicKeyXml, out _publicKeyError);
        }

        // 기능: 서버 공개키 XML을 읽는다(한 번, 생성자에서).
        // 입력: xml - RSA.ToXmlString 형식의 공개키, error - 실패 이유를 받을 곳.
        // 출력: 읽었으면 RSA(NetClient가 Dispose에서 해제)와 error null, 아니면 null과 이유 문장.
        private static RSA ParseServerKey(string xml, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "No server public key (Resources/ServerPublicKey.txt).";
                return null;
            }
            RSA rsa = RSA.Create();
            try
            {
                rsa.FromXmlString(xml);
                return rsa;
            }
            catch (Exception ex)
            {
                // Once, at startup, on a configuration file: Mono and .NET throw different types for bad XML
                // (CryptographicException, XmlSyntaxException, FormatException), and all of them mean the same thing here.
                rsa.Dispose();
                error = "The server public key cannot be read: " + ex.Message;
                return null;
            }
        }

        public event Action Connected;
        public event Action<JoinMatchResponse> Joined;
        public event Action<PlayerSpawned> SpawnReceived;
        public event Action<ushort> DespawnReceived;
        public event SnapshotHandler SnapshotReceived;
        public event Action<string> Disconnected;
        // Phase 3 combat (D4, D9, D11). Payloads are structs, so raising them does not allocate
        // (the catalog array is allocated once per join by its reader).
        // Phase 17 D2: with the projectile kinds (their gravity, radius and lifetime; both arrays allocated once per join).
        public event Action<WeaponInfo[], ProjectileInfo[]> CatalogReceived;
        public event Action<ShotFired> ShotReceived;
        public event Action<HitConfirmed> HitConfirmedReceived;
        public event Action<DamageTaken> DamageTakenReceived;
        public event Action<PlayerDied> PlayerDiedReceived;
        public event Action<PlayerRespawned> PlayerRespawnedReceived;
        // Phase 4 items (D14). WorldItems chunks and ItemSpawned both arrive as ItemReceived (an upsert).
        // The item catalog is allocated once per join by its reader; the rest are structs.
        public event Action<ItemCatalogData> ItemCatalogReceived;
        public event Action<WorldItemData> ItemReceived;
        public event Action<ushort> ItemRemovedReceived;
        public event Action<InventoryState> InventoryReceived;
        public event Action<PickupResult> PickupResultReceived;
        // Phase 5 match flow (D11): structs, so raising them does not allocate. A dev-respawn server sends none.
        public event Action<MatchState> MatchStateReceived;
        public event Action<ZoneState> ZoneStateReceived;
        public event Action<MatchResult> MatchResultReceived;
        // Phase 11 D8: the answer to RequestStats (a class allocated by its reader, once per answer).
        public event Action<StatsResponse> StatsReceived;
        // Phase 12 D5, D9: the drop transport route, and which doors are open.
        public event Action<DropRoute> TransportRouteReceived;
        public event Action<byte> DoorStatesReceived;
        // Phase 13 D6: which harvestables are destroyed.
        public event Action<ulong> HarvestStatesReceived;
        // Phase 13 D4, D7, D13-D15: the building numbers, our resources, our harvest hits, and the building stream (channel
        // 1): a request's result, pieces placed or synced (with the stream's version), health, destroyed, a reset sync, the
        // interest window. Structs, so raising them does not allocate (the catalog is allocated once per join).
        public event Action<BuildCatalogData> BuildCatalogReceived;
        public event Action<ResourcesState> ResourcesReceived;
        public event Action<HarvestHit> HarvestHitReceived;
        public event Action<BuildResult> BuildResultReceived;
        public event Action<BuildPieceRecord, uint> BuildPieceReceived;
        public event Action<uint, ushort, uint> BuildHealthReceived;
        // Phase 13.5 D8: a piece's state after its last edit of a tick (id, BuildEdit state, version). Applied by id.
        public event Action<uint, ushort, uint> BuildEditedReceived;
        // Phase 18 D6: a Placed record of the event stream (not a Sync), raised right after its BuildPieceReceived: a piece placed
        // now (sounds), not one this client is only told about.
        public event Action<BuildPieceRecord> BuildPlacedEventReceived;
        // Phase 18 D6, D11: a destroyed record with why it went (Destroyed by damage or Collapsed).
        public event Action<uint, BuildDestroyReason, uint> BuildDestroyedReceived;
        public event Action<uint> BuildResetReceived;
        public event Action<ulong> BuildInterestReceived;
        // Phase 14 D2, D5, D8, D10: our team, a knock-down (to everyone), a revive or reboot channel of our team, and the
        // reboot stations' cooldowns. Structs, so raising them does not allocate.
        public event Action<TeamState> TeamStateReceived;
        public event Action<PlayerDowned> PlayerDownedReceived;
        public event Action<ChannelState> ChannelStateReceived;
        public event Action<RebootStationsState> RebootStationsReceived;
        // Phase 15 D10: our team's pings and waypoints (the whole list each time).
        public event TeamMarkersHandler TeamMarkersReceived;
        // Phase 16 D3, D7: which loot containers spawned and which are open (spawned, opened), and the match's supply drops.
        public event Action<ulong, ulong> ContainerStatesReceived;
        public event SupplyDropsHandler SupplyDropsReceived;
        // Phase 17 D7: a projectile launched (or resent at a join or resume), bounced or came to rest, and exploded. Structs.
        public event Action<ProjectileSpawned> ProjectileSpawnedReceived;
        public event Action<ProjectileState> ProjectileStateReceived;
        public event Action<ProjectileExploded> ProjectileExplodedReceived;
        // Phase 18 D7: a harvest hit near us by another player (Unreliable). Struct.
        public event Action<WorldSound> WorldSoundReceived;
        // Phase 19 D4: the vehicles in our interest range (Unreliable channel 0: may come late or out of order).
        public event VehicleStatesHandler VehicleStatesReceived;

        public ClientState State { get; private set; } = ClientState.Disconnected;
        public string LastError { get; private set; }
        // Phase 10 D1, D10: why the last connection ended (None unless the server closed it with a code), and whether
        // that kind of end may be retried (DisconnectCodes.ShouldReconnect, the same table the bots use).
        public DisconnectCode LastDisconnectCode { get; private set; }
        public bool LastDisconnectRetryable { get; private set; }
        // Phase 11 D6: the rest of what the disconnected screen explains (UiText.Disconnect). Connect resets the reject,
        // the join answer and the start failure; a disconnect sets the reason (and the reject, when refused).
        public DisconnectReason LastDisconnectReason { get; private set; }
        public RejectReason LastRejectReason { get; private set; }
        public JoinResult LastJoinResult { get; private set; }
        public bool LastConnectStartFailed { get; private set; }
        public int RoundTripMs => _server != null ? _server.RoundTripTime : 0;

        // 기능: 서버에 새로 접속을 시작한다. 요청마다 새 세션 키(RSA로 감싼 blob)를 싣고, 같은 이름으로 얻은 Resume 키가 있으면
        //   Resume 증명도 싣는다. 쿠키 없이 보내고, 서버가 쿠키로 거절하면 OnPeerDisconnected가 한 번 다시 보낸다.
        // 입력: host·port - 서버 주소, devPlayerId - 이름, reconnectAttempt - 자동 재접속 시도면 true(짧은 연결 예산).
        // 출력: 반환값 없음. 시작되면 State가 Connecting이고 LastError가 null, 못 하면 Disconnected와 LastError·LastConnectStartFailed.
        // reconnectAttempt (Phase 10 D10): an automatic attempt gets the short connect budget of DisconnectCodes, so it
        // gives up within its slot (about 1.5 s instead of LiteNetLib's 5.5 s). A manual connect gets the defaults
        // back. LiteNetLib reads both fields on every update of a connecting peer (they are plain public fields,
        // written only by its constructor), so setting them before Connect applies to this connect (and its cookie retry).
        public void Connect(string host, int port, string devPlayerId, bool reconnectAttempt = false)
        {
            if (_disposed || State != ClientState.Disconnected) return;
            LastRejectReason = RejectReason.None;
            LastJoinResult = JoinResult.Ok;
            LastConnectStartFailed = true;   // until the connect below is under way
            _net.ReconnectDelay = reconnectAttempt ? DisconnectCodes.ReconnectRequestIntervalMs : _defaultReconnectDelay;
            _net.MaxConnectAttempts = reconnectAttempt ? DisconnectCodes.ReconnectRequestAttempts : _defaultMaxConnectAttempts;
            if (!_net.IsRunning && !_net.Start())
            {
                LastError = "Failed to open a local UDP socket.";
                return;
            }

            _host = host;
            _port = port;
            _devPlayerId = devPlayerId;
            ClearCookie();
            _cookieRetried = false;
            string error = StartConnect(null);
            if (error != null)
            {
                LastError = error;
                return;
            }
            LastError = null;
            LastConnectStartFailed = _server == null;
        }

        // 기능: 새 세션 키로 접속 요청 하나를 만들어(쿠키가 있으면 HasCookie, 같은 이름의 Resume 키가 있으면 HasResume과 증명)
        //   그 키를 인증 계층에 등록한 뒤 LiteNetLib 접속을 시작한다. Connect와 쿠키 재시도가 쓴다(재시도도 새 세션 키).
        // 입력: endPoint - 보낼 주소(쿠키 재시도는 거절한 peer의 주소, null이면 _host·_port의 이름을 푼다).
        // 출력: 시작되면 null(_server가 새 peer, State Connecting), 못 하면 오류 문장(LiteNetLib이 peer를 주지 않으면 null)과
        //   State Disconnected. Last* 필드는 바꾸지 않는다(부르는 쪽이 정한다). 연결 시점에만 할당한다(blob·키·peer).
        private string StartConnect(IPEndPoint endPoint)
        {
            if (_serverKey == null) return FailStart(_publicKeyError);
            _rng.GetBytes(_sessionKey);
            byte[] blob;
            try
            {
                blob = EncryptSessionKey(_serverKey, _sessionKey);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is NotSupportedException)
            {
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                return FailStart("Could not encrypt the session key: " + ex.Message);
            }
            if (blob == null || blob.Length != ProtocolLimits.RsaBlobBytes)
            {
                // ConnectRequestData.Write would send a 0-length blob, which the server refuses as BadRequest: say why here.
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                return FailStart("The server public key is not an RSA-2048 key.");
            }

            bool resume = _resumeKey != null && _resumeName == _devPlayerId;
            uint nonce = resume ? ++_resumeNonce : 0u;   // every request a new nonce, the cookie retry too
            ConnectRequestData request = BuildConnectRequest(_devPlayerId, blob, _hasCookie ? _cookie : null,
                resume ? _resumeKey : null, nonce, _sessionKey, _resumeProof);
            var writer = new PacketWriter(_sendBuffer);
            ConnectRequestData.Write(ref writer, request);
            if (writer.Overflowed)
            {
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                return FailStart("DevPlayerId is longer than 32 bytes.");
            }
            _connectData.Reset();
            _connectData.Put(_sendBuffer, 0, writer.Length);

            // Registered before Connect: LiteNetLib sends the request at once, and the server's accept is signed with these keys.
            _connectKeys = new SessionKeys(_sessionKey, isServer: false);
            Array.Clear(_sessionKey, 0, _sessionKey.Length);
            _authLayer.SetKeys(_connectKeys);

            try
            {
                // Host comes from user input or command line: an unresolvable name is an expected failure.
                _server = endPoint != null ? _net.Connect(endPoint, _connectData) : _net.Connect(_host, _port, _connectData);
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
            {
                _server = null;
                State = ClientState.Disconnected;
                return "Connect failed: " + ex.Message;
            }
            State = _server != null ? ClientState.Connecting : ClientState.Disconnected;
            return null;
        }

        // 기능: 접속을 시작하지 못한 상태로 둔다.
        // 입력: error - 이유 문장.
        // 출력: error 그대로. _server는 null, State는 Disconnected.
        private string FailStart(string error)
        {
            _server = null;
            State = ClientState.Disconnected;
            return error;
        }

        // 기능: 세션 키를 서버 공개키로 RSA-OAEP-SHA1 암호화한다(설계 B3-1). Unity Mono가 RSAEncryptionPadding.OaepSHA1을 받지
        //   않으면 RSACryptoServiceProvider의 OAEP(같은 SHA-1)로 다시 한다.
        // 입력: serverKey - 서버 공개키, sessionKey - 32 B 세션 키.
        // 출력: 암호문(RSA-2048이면 ProtocolLimits.RsaBlobBytes = 256 B). 둘 다 실패하면 CryptographicException·NotSupportedException.
        public static byte[] EncryptSessionKey(RSA serverKey, byte[] sessionKey)
        {
            try
            {
                return serverKey.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is NotSupportedException)
            {
                using (var csp = new RSACryptoServiceProvider())
                {
                    csp.ImportParameters(serverKey.ExportParameters(false));
                    return csp.Encrypt(sessionKey, true);
                }
            }
        }

        // 기능: 접속 요청 하나의 데이터를 만든다(설계 B2·B4). 쿠키가 있으면 HasCookie, Resume 키가 있으면 HasResume과 증명
        //   (HMAC(resumeKey, nonce ‖ 이번 세션 키 ‖ 이름)[0..16]).
        // 입력: devPlayerId - 이름, sessionKeyBlob - 암호화한 세션 키(256 B), cookie - 서버 쿠키(없으면 null), resumeKey - 이전 연결의
        //   Resume 키(없으면 null), resumeNonce - 이 요청의 nonce, sessionKey - 이번 요청의 세션 키 32 B, proofBuffer - 증명을 받을 16 B.
        // 출력: ProtocolVersion·Flags·쿠키·blob·이름·Resume 필드가 채워진 ConnectRequestData(배열은 넘겨받은 것을 그대로 가리킨다).
        public static ConnectRequestData BuildConnectRequest(string devPlayerId, byte[] sessionKeyBlob, byte[] cookie, byte[] resumeKey,
            uint resumeNonce, byte[] sessionKey, byte[] proofBuffer)
        {
            var data = new ConnectRequestData
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                Flags = ConnectFlags.None,
                SessionKeyBlob = sessionKeyBlob,
                DevPlayerId = devPlayerId,
            };
            if (cookie != null)
            {
                data.Flags |= ConnectFlags.HasCookie;
                data.Cookie = cookie;
            }
            if (resumeKey != null)
            {
                SessionAuth.ComputeResumeProof(resumeKey, resumeNonce, sessionKey, devPlayerId, proofBuffer);
                data.Flags |= ConnectFlags.HasResume;
                data.ResumeNonce = resumeNonce;
                data.ResumeProof = proofBuffer;
            }
            return data;
        }

        // 기능: 보관한 Resume 키를 버린다(사용자가 나가거나 다른 서버·이름으로 접속할 때). 다음 접속은 새 캐릭터로 들어간다.
        // 입력: 없음.
        // 출력: 반환값 없음. 다음 접속 요청에 HasResume이 없다.
        public void ForgetResume()
        {
            _resumeKey = null;
            _resumeName = null;
            _resumeNonce = 0;
        }

        // Review fix B3: datagrams the layer dropped after this connect's keys verified one (forged, tampered or replayed).
        public long AuthDrops => _authLayer.AuthDrops;

        // 기능: 거절 데이터가 쿠키인지 RejectReason인지 가른다(설계 A3·B5의 데이터 규약).
        // 입력: data - ConnectionRejected의 추가 데이터(없으면 빈 Span).
        // 출력: ProtocolLimits.CookieBytes(16 B)면 Cookie, 1 B면 Reason(그 바이트가 RejectReason), 그 밖의 길이는 None.
        public static RejectKind ClassifyReject(ReadOnlySpan<byte> data)
        {
            if (data.Length == ProtocolLimits.CookieBytes) return RejectKind.Cookie;
            return data.Length == 1 ? RejectKind.Reason : RejectKind.None;
        }

        // 기능: 보관한 쿠키를 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 다음 접속 요청은 쿠키 없이 나간다.
        private void ClearCookie()
        {
            if (!_hasCookie) return;
            Array.Clear(_cookie, 0, _cookie.Length);
            _hasCookie = false;
        }

        // 기능: 쌓인 LiteNetLib 이벤트를 메인 스레드에서 처리한다(아래 INetEventListener 콜백이 이 안에서 불린다). Update마다 부른다.
        // 입력: 없음.
        // 출력: 반환값 없음. 소켓이 열려 있으면 받은 패킷·연결 변화가 이벤트로 올라간다.
        public void Poll()
        {
            if (_net.IsRunning) _net.PollEvents();
        }

        // 기능: 플레이어 입력 패킷 하나를 비신뢰(Unreliable)로 보낸다.
        // 입력: packet - 보낼 입력(순번·버튼·조준 등).
        // 출력: 반환값 없음. 참가 중(Joined)일 때만 전송되고, 아니면 아무것도 하지 않는다.
        public void SendInput(in PlayerInputPacket packet)
        {
            if (State != ClientState.Joined) return;
            var writer = new PacketWriter(_sendBuffer);
            PlayerInputPacket.Write(ref writer, packet);
            _server.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
        }

        // 기능: 지금 연결을 끊는다(CancelConnect와 달리 끝이 OnPeerDisconnected로 처리되어 Disconnected 이벤트가 올라간다).
        // 입력: 없음.
        // 출력: 반환값 없음. 연결된 peer가 있으면 끊기 요청이 나가고, 없으면 아무것도 하지 않는다.
        public void Disconnect()
        {
            if (_server != null) _net.DisconnectPeer(_server);
        }

        // 기능: 조각 배치 요청 하나를 건설 채널(1)로 신뢰 순서 전송한다(Phase 13 D8).
        // 입력: request - 배치 요청(순번·조각 종류·격자 위치·재질).
        // 출력: 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        // Phase 13 D8: one placement on the building channel. False when not joined.
        public bool SendBuild(in BuildRequest request)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            BuildRequest.Write(ref writer, request);
            _server.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 조각 편집 요청 하나를 건설 채널(1)로 보낸다(Phase 13.5 D4, Reset도 같은 요청).
        // 입력: request - 순번(배치와 같은 카운터), 조각 id, 상태(BuildEdit.PackState).
        // 출력: 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        public bool SendBuildEdit(in BuildEditRequest request)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            BuildEditRequest.Write(ref writer, request);
            _server.Send(writer.WrittenSpan, ProtocolConstants.BuildChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: Ping 하나나 내 Waypoint 설정·삭제 요청을 신뢰 채널 0으로 보낸다(Phase 15 D7).
        // 입력: marker - 종류·위치·대상(Enemy·Item만 대상 id, 그 밖은 0: 서버 Reader가 강제한다).
        // 출력: 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        public bool SendMapMarker(in MapMarker marker)
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            MapMarker.Write(ref writer, marker);
            _server.Send(writer.WrittenSpan, ProtocolConstants.ReliableChannel, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 내 누적 전적을 서버에 요청한다(Phase 11 D8). 답은 StatsReceived 이벤트로 온다.
        // 입력: 없음.
        // 출력: 요청을 보냈으면 true, 참가 중이 아니면 false(아무것도 보내지 않음).
        // Phase 11 D8: asks for this player's statistics; the answer comes as StatsReceived. False when not joined.
        public bool RequestStats()
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            StatsRequest.Write(ref writer);
            _server.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 기능: 진행 중인 접속(연결 중, 또는 연결됐지만 Join 전)을 Disconnected 이벤트 없이 버린다. 쿠키 재시도 중이면 쿠키도 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 버렸으면 State가 Disconnected이고 Last* 필드는 그대로다.
        // Phase 10 D10: gives up a connect that is still in progress, without a Disconnected event: the caller starts
        // the next attempt at once. Phase 11: also a connection that is connected but not joined yet (the disconnected
        // screen's Cancel), so the Last* fields keep the end that started the reconnect cycle. The peer is forgotten
        // before DisconnectPeer, because LiteNetLib may raise that peer's disconnect inside the call; its events are
        // then ignored (not _server). A joined connection is never given up this way (Disconnect raises the event).
        public void CancelConnect()
        {
            if ((State != ClientState.Connecting && State != ClientState.Connected) || _server == null) return;
            NetPeer abandoned = _server;
            _server = null;
            State = ClientState.Disconnected;
            ClearCookie();
            _net.DisconnectPeer(abandoned);
        }

        // 기능: 접속을 끊고 소켓·스레드를 멈춘 뒤 키·RSA·난수 생성기를 해제한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 다시 쓸 수 없는 NetClient가 된다.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_net.IsRunning) _net.Stop(true);
            // After Stop: LiteNetLib's threads no longer seal or open with the keys.
            _authLayer.DisposeKeys();
            _connectKeys = null;
            _serverKey?.Dispose();
            _rng.Dispose();
            ForgetResume();
            _server = null;
            State = ClientState.Disconnected;
        }

        // 기능: 접속이 성립하면 Join을 요청하고 Connected를 올린다. 쓴 쿠키는 지운다.
        // 입력: peer - 연결된 peer(지금 접속이 아니면 무시).
        // 출력: 반환값 없음. State가 Connected가 되고 JoinMatchRequest가 전송된다.
        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (peer != _server) return;   // an abandoned attempt (CancelConnect)
            ClearCookie();
            State = ClientState.Connected;
            var writer = new PacketWriter(_sendBuffer);
            JoinMatchRequest.Write(ref writer);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            Connected?.Invoke();
        }

        // 기능: 지금 접속의 끝을 처리한다. 첫 쿠키 거절(16 B)이면 쿠키를 보관하고 같은 주소로 바로 다시 접속한다(끝이 아니다).
        //   두 번째 쿠키 거절이나 그 밖의 끝은 Last* 필드를 정하고 Disconnected를 올린다.
        // 입력: peer - 끊긴 peer(지금 접속이 아니면 무시), disconnectInfo - 이유와 추가 데이터(거절 데이터·끊기 코드).
        // 출력: 반환값 없음. 쿠키 재시도면 State가 Connecting이고 이벤트·Last* 필드는 그대로, 아니면 State Disconnected,
        //   LastDisconnect*·LastRejectReason·LastError가 정해지고 Disconnected 이벤트가 올라간다.
        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (peer != _server) return;   // an abandoned attempt (CancelConnect): not this connection's end
            _server = null;
            State = ClientState.Disconnected;
            DisconnectReason why = disconnectInfo.Reason;

            // Review fix A3: the server answers a request without a valid cookie with RejectForce + a 16 B cookie (no peer on
            // its side). The retry goes out inside this call, so the reconnect cycle (GameClient) never sees an end.
            ReadOnlySpan<byte> rejectData = why == DisconnectReason.ConnectionRejected && disconnectInfo.AdditionalData != null
                ? disconnectInfo.AdditionalData.GetRemainingBytesSpan()
                : ReadOnlySpan<byte>.Empty;
            RejectKind rejectKind = ClassifyReject(rejectData);
            string cookieError = null;
            if (rejectKind == RejectKind.Cookie)
            {
                if (!_cookieRetried)
                {
                    rejectData.CopyTo(_cookie);   // the reader is recycled after this callback (AutoRecycle)
                    _hasCookie = true;
                    _cookieRetried = true;
                    // The rejected peer's own address (NetPeer is an IPEndPoint): no second name lookup, and the cookie is
                    // bound to that IP and port. One allocation per connect, not per frame.
                    cookieError = StartConnect(new IPEndPoint(peer.Address, peer.Port));
                    if (_server != null) return;
                    cookieError = cookieError ?? "Connect failed: the cookie retry could not start";
                }
                else
                {
                    cookieError = "cookie challenge loop";
                }
            }
            ClearCookie();
            bool remoteClose = why == DisconnectReason.RemoteConnectionClose;
            LastDisconnectCode = remoteClose && disconnectInfo.AdditionalData != null
                ? DisconnectCodes.Read(disconnectInfo.AdditionalData.GetRemainingBytesSpan())
                : DisconnectCode.None;
            bool networkLoss = why == DisconnectReason.Timeout || why == DisconnectReason.ConnectionFailed ||
                               why == DisconnectReason.HostUnreachable || why == DisconnectReason.NetworkUnreachable;
            LastDisconnectRetryable = DisconnectCodes.ShouldReconnect(remoteClose, LastDisconnectCode, networkLoss);
            LastDisconnectReason = why;
            LastRejectReason = RejectReason.None;

            string reason = why.ToString();
            if (cookieError != null)
            {
                reason = cookieError;
            }
            else if (rejectKind == RejectKind.Reason)
            {
                LastRejectReason = (RejectReason)rejectData[0];
                reason = "Rejected: " + LastRejectReason;
            }
            else if (LastDisconnectCode != DisconnectCode.None)
            {
                reason = Describe(LastDisconnectCode);
            }
            LastError = reason;
            Disconnected?.Invoke(reason);
        }

        // 기능: 서버가 보낸 끊기 코드를 사람이 읽을 문장으로 바꾼다.
        // 입력: code - 서버의 DisconnectCode.
        // 출력: 코드별 상수 문장. 모르는 코드는 "Disconnected".
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
                case DisconnectCode.Congested: return "Disconnected: the connection was too slow";
                default: return "Disconnected";
            }
        }

        // 기능: 받은 패킷 하나를 읽어 해당 이벤트를 올린다(Phase 13.5: BuildEvents의 Edited 기록 포함, Phase 14: 분대 패킷 4종,
        //   Phase 15: TeamMarkers, Phase 16: ContainerStates·SupplyDrops, Phase 17: 투사체 카탈로그와 투사체 패킷 3종,
        //   Phase 18: 사건 Placed 기록은 BuildPlacedEventReceived도 올리고(Sync는 아님), Destroyed 기록은 이유와 함께, WorldSound.
        //   Phase 19: VehicleStates. 리뷰 B4: 참가 성공(Ok·Resumed)이면 이 연결의 Resume 키를 보관한다).
        //   메인 스레드에서 Poll이 부른다.
        // 입력: peer - 보낸 쪽(지금 연결이 아니면 무시), reader - 패킷, channelNumber·deliveryMethod - 쓰지 않는다.
        // 출력: 반환값 없음. 읽기에 실패한 기록이 있으면 그 패킷의 나머지는 버린다.
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (peer != _server) return;
            var packet = new PacketReader(reader.GetRemainingBytesSpan());
            if (!packet.TryReadPacketId(out PacketId id)) return;

            switch (id)
            {
                case PacketId.JoinMatchResponse:
                    if (JoinMatchResponse.TryRead(ref packet, out var response))
                    {
                        LastJoinResult = response.Result;
                        // Phase 10 D2: Resumed is a join into our own character; the server resends the full state.
                        if (response.Result == JoinResult.Ok || response.Result == JoinResult.Resumed)
                        {
                            State = ClientState.Joined;
                            // Review fix B4: this connection's resume key is what the server now holds for our character.
                            _resumeKey = _connectKeys.ResumeKey;
                            _resumeName = _devPlayerId;
                            _resumeNonce = 0;
                        }
                        Joined?.Invoke(response);
                    }
                    break;

                case PacketId.PlayerSpawned:
                    if (PlayerSpawned.TryRead(ref packet, out var spawned)) SpawnReceived?.Invoke(spawned);
                    break;

                case PacketId.PlayerDespawned:
                    if (PlayerDespawned.TryRead(ref packet, out var despawned)) DespawnReceived?.Invoke(despawned.EntityId);
                    break;

                case PacketId.WorldSnapshot:
                    if (!WorldSnapshotHeader.TryRead(ref packet, out var header)) return;
                    int count = 0;
                    for (int i = 0; i < header.Count && i < _snapshotEntities.Length; i++)
                    {
                        if (!SnapshotEntity.TryRead(ref packet, out _snapshotEntities[count])) break;
                        count++;
                    }
                    SnapshotReceived?.Invoke(header, _snapshotEntities, count);
                    break;

                case PacketId.WeaponCatalog:
                    if (WeaponCatalogPacket.TryRead(ref packet, out var weapons, out var projectiles)) CatalogReceived?.Invoke(weapons, projectiles);
                    break;

                case PacketId.ShotFired:
                    if (ShotFired.TryRead(ref packet, out var shot)) ShotReceived?.Invoke(shot);
                    break;

                case PacketId.HitConfirmed:
                    if (HitConfirmed.TryRead(ref packet, out var hit)) HitConfirmedReceived?.Invoke(hit);
                    break;

                case PacketId.DamageTaken:
                    if (DamageTaken.TryRead(ref packet, out var damage)) DamageTakenReceived?.Invoke(damage);
                    break;

                case PacketId.PlayerDied:
                    if (PlayerDied.TryRead(ref packet, out var died)) PlayerDiedReceived?.Invoke(died);
                    break;

                case PacketId.PlayerRespawned:
                    if (PlayerRespawned.TryRead(ref packet, out var respawned)) PlayerRespawnedReceived?.Invoke(respawned);
                    break;

                case PacketId.ItemCatalog:
                    if (ItemCatalogPacket.TryRead(ref packet, out var items)) ItemCatalogReceived?.Invoke(items);
                    break;

                case PacketId.WorldItems:
                    if (!WorldItemsPacket.TryReadHeader(ref packet, out int itemCount)) return;
                    for (int i = 0; i < itemCount; i++)
                    {
                        if (!WorldItemData.TryRead(ref packet, out var listed)) break;
                        ItemReceived?.Invoke(listed);
                    }
                    break;

                case PacketId.ItemSpawned:
                    if (ItemSpawnedPacket.TryRead(ref packet, out var item)) ItemReceived?.Invoke(item);
                    break;

                case PacketId.ItemRemoved:
                    if (ItemRemoved.TryRead(ref packet, out var removed)) ItemRemovedReceived?.Invoke(removed.ItemId);
                    break;

                case PacketId.InventoryState:
                    if (InventoryState.TryRead(ref packet, out var inventory)) InventoryReceived?.Invoke(inventory);
                    break;

                case PacketId.PickupResult:
                    if (PickupResult.TryRead(ref packet, out var pickup)) PickupResultReceived?.Invoke(pickup);
                    break;

                case PacketId.MatchState:
                    if (MatchState.TryRead(ref packet, out var match)) MatchStateReceived?.Invoke(match);
                    break;

                case PacketId.ZoneState:
                    if (ZoneState.TryRead(ref packet, out var zone)) ZoneStateReceived?.Invoke(zone);
                    break;

                case PacketId.MatchResult:
                    if (MatchResult.TryRead(ref packet, out var result)) MatchResultReceived?.Invoke(result);
                    break;

                case PacketId.StatsResponse:
                    if (StatsResponse.TryRead(ref packet, out var stats)) StatsReceived?.Invoke(stats);
                    break;

                case PacketId.TransportRoute:
                    if (TransportRoutePacket.TryRead(ref packet, out var route)) TransportRouteReceived?.Invoke(route);
                    break;

                case PacketId.DoorStates:
                    if (DoorStatesPacket.TryRead(ref packet, out byte doors)) DoorStatesReceived?.Invoke(doors);
                    break;

                case PacketId.HarvestStates:
                    if (HarvestStatesPacket.TryRead(ref packet, out ulong destroyed)) HarvestStatesReceived?.Invoke(destroyed);
                    break;

                case PacketId.BuildCatalog:
                    if (BuildCatalogPacket.TryRead(ref packet, out var buildCatalog)) BuildCatalogReceived?.Invoke(buildCatalog);
                    break;

                case PacketId.ResourcesState:
                    if (ResourcesState.TryRead(ref packet, out var resources)) ResourcesReceived?.Invoke(resources);
                    break;

                case PacketId.HarvestHit:
                    if (HarvestHit.TryRead(ref packet, out var harvestHit)) HarvestHitReceived?.Invoke(harvestHit);
                    break;

                case PacketId.BuildResult:
                    if (BuildResult.TryRead(ref packet, out var buildResult)) BuildResultReceived?.Invoke(buildResult);
                    break;

                case PacketId.BuildEvents:
                    // Phase 13.5 D8: Placed -> Edited -> Health -> Destroyed. A bad record stops the packet, as before.
                    if (!BuildEventsPacket.TryReadHeader(ref packet, out uint version, out int placed, out int edited, out int health, out int gone)) return;
                    for (int i = 0; i < placed; i++)
                    {
                        if (!BuildPieceRecord.TryReadPlaced(ref packet, out var piece)) return;
                        BuildPieceReceived?.Invoke(piece, version);
                        BuildPlacedEventReceived?.Invoke(piece);
                    }
                    for (int i = 0; i < edited; i++)
                    {
                        if (!BuildEventsPacket.TryReadEdited(ref packet, out uint editedId, out ushort editedState)) return;
                        BuildEditedReceived?.Invoke(editedId, editedState, version);
                    }
                    for (int i = 0; i < health; i++)
                    {
                        if (!BuildEventsPacket.TryReadHealth(ref packet, out uint pieceId, out ushort pieceDamage)) return;
                        BuildHealthReceived?.Invoke(pieceId, pieceDamage, version);
                    }
                    for (int i = 0; i < gone; i++)
                    {
                        if (!BuildEventsPacket.TryReadDestroyed(ref packet, out uint goneId, out BuildDestroyReason goneReason)) return;
                        BuildDestroyedReceived?.Invoke(goneId, goneReason, version);
                    }
                    break;

                case PacketId.BuildSync:
                    if (!BuildSyncPacket.TryReadHeader(ref packet, out uint syncVersion, out bool reset, out int synced)) return;
                    if (reset) BuildResetReceived?.Invoke(syncVersion);
                    for (int i = 0; i < synced; i++)
                    {
                        if (!BuildPieceRecord.TryReadSync(ref packet, out var piece)) return;
                        BuildPieceReceived?.Invoke(piece, syncVersion);
                    }
                    break;

                case PacketId.BuildInterest:
                    if (BuildInterestPacket.TryRead(ref packet, out ulong cells)) BuildInterestReceived?.Invoke(cells);
                    break;

                case PacketId.TeamState:
                    if (TeamState.TryRead(ref packet, out var team)) TeamStateReceived?.Invoke(team);
                    break;

                case PacketId.PlayerDowned:
                    if (PlayerDowned.TryRead(ref packet, out var downed)) PlayerDownedReceived?.Invoke(downed);
                    break;

                case PacketId.ChannelState:
                    if (ChannelState.TryRead(ref packet, out var channel)) ChannelStateReceived?.Invoke(channel);
                    break;

                case PacketId.RebootStations:
                    if (RebootStationsState.TryRead(ref packet, out var stations)) RebootStationsReceived?.Invoke(stations);
                    break;

                case PacketId.TeamMarkers:
                    if (TeamMarkersPacket.TryRead(ref packet, _markerPings, _markerWaypoints, out int pingCount, out int waypointCount))
                        TeamMarkersReceived?.Invoke(_markerPings, pingCount, _markerWaypoints, waypointCount);
                    break;

                case PacketId.ContainerStates:
                    if (ContainerStatesPacket.TryRead(ref packet, out ulong spawnedMask, out ulong openedMask))
                        ContainerStatesReceived?.Invoke(spawnedMask, openedMask);
                    break;

                case PacketId.SupplyDrops:
                    if (SupplyDropsPacket.TryRead(ref packet, _supplyDrops, out int dropCount)) SupplyDropsReceived?.Invoke(_supplyDrops, dropCount);
                    break;

                case PacketId.ProjectileSpawned:
                    if (ProjectileSpawned.TryRead(ref packet, out var projectileSpawned)) ProjectileSpawnedReceived?.Invoke(projectileSpawned);
                    break;

                case PacketId.ProjectileState:
                    if (ProjectileState.TryRead(ref packet, out var projectileState)) ProjectileStateReceived?.Invoke(projectileState);
                    break;

                case PacketId.ProjectileExploded:
                    if (ProjectileExploded.TryRead(ref packet, out var projectileExploded)) ProjectileExplodedReceived?.Invoke(projectileExploded);
                    break;

                case PacketId.WorldSound:
                    if (WorldSound.TryRead(ref packet, out var worldSound)) WorldSoundReceived?.Invoke(worldSound);
                    break;

                case PacketId.VehicleStates:
                    if (VehicleStatesPacket.TryRead(ref packet, _vehicles, out uint vehicleTick, out uint vehicleAck, out int vehicleCount))
                        VehicleStatesReceived?.Invoke(vehicleTick, vehicleAck, _vehicles, vehicleCount);
                    break;
            }
        }

        // 기능: 들어오는 접속 요청을 거절한다(Client는 접속을 받지 않는다).
        // 입력: request - 들어온 접속 요청.
        // 출력: 반환값 없음. 요청이 거절된다.
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            // A client never accepts incoming connections.
            request.Reject();
        }

        // 기능: 소켓 오류를 LastError에 기록한다.
        // 입력: endPoint - 오류가 난 주소(쓰지 않는다), socketError - 소켓 오류 코드.
        // 출력: 반환값 없음. LastError가 오류 문장으로 바뀐다.
        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            LastError = "Network error: " + socketError;
        }

        // 기능: 연결 없는 메시지를 무시한다.
        // 입력: remoteEndPoint - 보낸 주소, reader - 데이터, messageType - 종류(모두 쓰지 않는다).
        // 출력: 반환값 없음. 아무것도 바뀌지 않는다.
        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        // 기능: 지연 갱신 알림을 무시한다(RoundTripMs가 peer에서 직접 읽는다).
        // 입력: peer - 대상 peer, latency - 새 지연(ms)(모두 쓰지 않는다).
        // 출력: 반환값 없음. 아무것도 바뀌지 않는다.
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }
    }
}
