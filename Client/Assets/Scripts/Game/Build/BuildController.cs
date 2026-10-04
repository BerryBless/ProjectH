using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §52-§54): a placement the client has sent and not heard back on. It is drawn at once in the
    // "waiting" look (BuildPieceViews) and goes when its BuildResult comes (accepted: the confirmed piece arrives in the
    // building stream; refused: it just goes) or after TimeoutSeconds without one. It never collides.
    public struct PendingBuild
    {
        public ushort Sequence;
        public BuildPieceShape Shape;
        public BuildMaterialType Material;
        public float SentAt;
        // Set by an Ok result: kept (waiting look) until the confirmed piece is in the store, so it does not flicker.
        public uint AcceptedId;
    }

    // Phase 13 D16: build mode's local side. Each frame (Update) it picks the candidate (BuildTargeting), judges it as far
    // as the client can (inside the server's range, not taken, affordable: the preview's Valid / Invalid / NoResource),
    // and, while the build button is held, sends it through TurboGate. Requests are numbered from 1 per connection; a
    // placement is shown at once as pending (at most MaxPending, the server's queue). The resources shown are the server's
    // minus the pending costs (request §107). Pure, no UnityEngine; sending is a delegate. Main thread only.
    public sealed class BuildController
    {
        public const int MaxPending = 8;
        public const float TimeoutSeconds = 1f;
        // The server's building.maxRequestsPerSecond default (BuildingCatalog.MaxRequestsPerSecond): never send more in a second.
        public const int MaxRequestsPerSecond = 20;

        private readonly Action<BuildRequest> _send;
        private readonly PendingBuild[] _pending = new PendingBuild[MaxPending];
        private int _pendingCount;
        private ushort _sequence;
        private readonly float[] _sendTimes = new float[MaxRequestsPerSecond];
        private int _sendTotal;

        // 기능: 요청 전송 Delegate를 받아 건설 Controller를 만든다.
        // 입력: send - BuildRequest를 Server로 보낼 Delegate(null이면 ArgumentNullException).
        // 출력: 대기 배치가 없고 요청 번호가 0인 BuildController.
        public BuildController(Action<BuildRequest> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public BuildSelection Selection { get; } = new BuildSelection();
        public TurboGate Turbo { get; } = new TurboGate();
        public BuildCatalogData Catalog { get; set; }
        public ResourcesState Resources { get; set; }
        public int SimHz { get; set; } = 30;

        // This frame's candidate.
        public bool HasCandidate { get; private set; }
        public BuildPieceShape Candidate { get; private set; }
        public BuildPreviewState CandidateState { get; private set; }
        public int PendingCount => _pendingCount;
        // 기능: 응답을 기다리는 배치 하나를 Index로 읽는다.
        // 입력: i - 0 이상 PendingCount 미만의 Index.
        // 출력: 해당 Index의 PendingBuild 복사본.
        public PendingBuild PendingAt(int i) => _pending[i];
        // Debug (F1): requests sent, refusals heard, and the newest refusal's reason.
        public int Sent { get; private set; }
        public int Refused { get; private set; }
        public BuildResultCode LastRefusal { get; private set; }
        // Changes whenever the pending set does (views redraw only then).
        public int PendingVersion { get; private set; }

        // 기능: Server 자원에서 아직 수락되지 않은 대기 배치의 비용을 뺀 표시용 자원량을 계산한다.
        // 입력: material - 계산할 재료 종류.
        // 출력: 표시할 자원량(0 이상). Catalog가 없으면 Server 값 그대로.
        // Wood, stone and metal as shown: the server's numbers minus what pending placements will cost.
        public int ShownResource(BuildMaterialType material)
        {
            int value = Resources.Get(material);
            if (Catalog == null) return value;
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].AcceptedId == 0 && _pending[i].Material == material) value -= Catalog.ResourceCost[(int)material];
            }
            return Math.Max(0, value);
        }

        // 기능: 한 프레임의 건설 처리: 만료·확정된 대기 배치를 정리하고 후보 조각을 골라 판정한 뒤, 조건이 맞으면 BuildRequest를 보낸다.
        // 입력: now - 현재 시간(초), inBuildMode - 건설 모드 여부, pressed - 이번 프레임에 건설 버튼을 눌렀는지, held - 건설 버튼을 누르고 있는지, feet - 발 위치, eye - Server 기준 눈 위치, yaw - 수평 시선 각도(도), pitch - 수직 시선 각도(도, 아래가 +), store - 확정 조각 저장소.
        // 출력: 요청을 보냈으면 true(대기 배치가 추가됨), 아니면 false. 후보와 후보 판정은 매번 갱신된다.
        // One frame. inBuildMode: the predicted tool is Build and the player is alive and on foot; eye: the server's eye
        // (feet + eye height of the mode). Returns true when a request went out.
        public bool Update(float now, bool inBuildMode, bool pressed, bool held, Vector3 feet, Vector3 eye, float yaw, float pitch, BuildStore store)
        {
            Expire(now);
            DropConfirmed(store);
            BuildPieceShape shape = default;
            HasCandidate = inBuildMode && BuildTargeting.TryPick(Selection.Piece, feet, yaw, pitch, Selection.RotationOffset, out shape);
            if (!HasCandidate)
            {
                Turbo.ShouldSend(now, false, false, false, 0);
                return false;
            }
            Candidate = shape;
            // A slot already waiting for its answer is not offered again.
            CandidateState = IsPending(shape) ? BuildPreviewState.Invalid : Judge(shape, eye, store);
            if (held && !RateAllows(now)) return false;   // the rolling-second cap
            if (Catalog != null) Turbo.Interval = Math.Max(0.05f, Catalog.MinBuildIntervalTicks / (float)SimHz);
            if (!Turbo.ShouldSend(now, pressed, held, CandidateState == BuildPreviewState.Valid && _pendingCount < MaxPending, BuildGrid.SlotKey(shape)))
                return false;
            var request = new BuildRequest
            {
                Sequence = ++_sequence, Piece = (byte)shape.Type, Material = (byte)Selection.Material, X = shape.X, Y = shape.Y, Z = shape.Z,
                Rotation = shape.Rotation,
            };
            _sendTimes[_sendTotal % MaxRequestsPerSecond] = now;
            _sendTotal++;
            _pending[_pendingCount++] = new PendingBuild { Sequence = request.Sequence, Shape = shape, Material = Selection.Material, SentAt = now };
            PendingVersion++;
            Sent++;
            _send(request);
            return true;
        }

        // 기능: Client가 미리 알 수 있는 범위(사거리, 점유, 자원)로 후보 조각을 판정한다.
        // 입력: shape - 판정할 조각, eye - 눈 위치, store - 확정 조각 저장소(null이면 점유 검사 생략).
        // 출력: 사거리 밖이거나 점유되었으면 Invalid, 자원이 모자라면 NoResource, 그 외 Valid.
        // What the client can tell before the server: out of reach, taken, or not affordable.
        public BuildPreviewState Judge(in BuildPieceShape shape, Vector3 eye, BuildStore store)
        {
            Box bounds = BuildGrid.BoundsOf(shape);
            float range = Catalog != null ? Catalog.BuildRange : 7f;
            if (Vector3.Distance(eye, bounds.Center) > range + bounds.Size.Length() * 0.5f) return BuildPreviewState.Invalid;
            if (store != null && store.Occupied(shape)) return BuildPreviewState.Invalid;
            int cost = Catalog != null ? Catalog.ResourceCost[(int)Selection.Material] : 0;
            return ShownResource(Selection.Material) < cost ? BuildPreviewState.NoResource : BuildPreviewState.Valid;
        }

        // 기능: Server BuildResult를 받아 같은 Sequence의 대기 배치를 처리한다.
        // 입력: result - 수신한 건설 결과.
        // 출력: 반환값 없음. 거절이면 거절 수와 마지막 거절 사유가 기록되고 대기 배치가 지워지며, 수락이면 확정 조각이 올 때까지 AcceptedId가 기록된다.
        // BuildResult: the pending placement with this sequence goes (an accepted one comes back as a confirmed piece).
        public void OnResult(in BuildResult result)
        {
            if (result.Code != BuildResultCode.Ok)
            {
                Refused++;
                LastRefusal = result.Code;
            }
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Sequence != result.Sequence) continue;
                if (result.Code == BuildResultCode.Ok && result.PieceId != 0)
                {
                    _pending[i].AcceptedId = result.PieceId;   // goes when the confirmed piece arrives (or at the timeout)
                    PendingVersion++;
                }
                else RemoveAt(i);
                return;
            }
        }

        // 기능: 새 연결에 맞게 요청 번호, 대기 배치, 전송 기록, 선택, 자원을 초기화한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 요청 번호가 1부터 다시 시작되고 PendingVersion이 바뀐다.
        // A new connection numbers its requests from 1 again (the server's sequence starts over at a join and a resume).
        public void Reset()
        {
            _sequence = 0;
            _pendingCount = 0;
            _sendTotal = 0;
            PendingVersion++;
            HasCandidate = false;
            Selection.Reset();
            Resources = default;
        }

        // 기능: 최근 1초 안에 보낸 요청이 MaxRequestsPerSecond 미만인지 확인한다.
        // 입력: now - 현재 시간(초).
        // 출력: 지금 보낼 수 있으면 true, 1초 상한에 걸리면 false.
        private bool RateAllows(float now) => _sendTotal < MaxRequestsPerSecond || now - _sendTimes[_sendTotal % MaxRequestsPerSecond] >= 1f;

        // 기능: 수락된 대기 배치 중 확정 조각이 저장소에 도착한 것을 지운다.
        // 입력: store - 확정 조각 저장소(null이면 아무것도 하지 않음).
        // 출력: 반환값 없음. 해당 대기 배치가 제거된다.
        private void DropConfirmed(BuildStore store)
        {
            if (store == null) return;
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (_pending[i].AcceptedId != 0 && store.TryGet(_pending[i].AcceptedId, out _)) RemoveAt(i);
            }
        }

        // 기능: 같은 조각이 이미 응답을 기다리는 중인지 확인한다.
        // 입력: shape - 확인할 조각.
        // 출력: 대기 중이면 true, 아니면 false.
        private bool IsPending(in BuildPieceShape shape)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Shape.Equals(shape)) return true;
            }
            return false;
        }

        // 기능: TimeoutSeconds 동안 응답이 없는 대기 배치를 지운다.
        // 입력: now - 현재 시간(초).
        // 출력: 반환값 없음. 만료된 대기 배치가 제거된다.
        private void Expire(float now)
        {
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (now - _pending[i].SentAt >= TimeoutSeconds) RemoveAt(i);
            }
        }

        // 기능: 대기 배치 하나를 마지막 항목과 바꿔 지운다(순서는 유지하지 않는다).
        // 입력: i - 지울 대기 배치 Index.
        // 출력: 반환값 없음. PendingCount가 줄고 PendingVersion이 바뀐다.
        private void RemoveAt(int i)
        {
            _pending[i] = _pending[--_pendingCount];
            PendingVersion++;
        }
    }

    // Phase 13 D16 (request §37): how the preview looks.
    public enum BuildPreviewState : byte
    {
        Valid = 0,
        Invalid = 1,
        NoResource = 2,
    }
}
