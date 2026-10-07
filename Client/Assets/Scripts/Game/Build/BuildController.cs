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
    // Phase 13.5 D4: the numbering and the per-second cap are a BuildRequestCounter shared with edits; GameClient routes a
    // BuildResult here only when it is not an edit's (BuildEditController.OwnsSequence).
    public sealed class BuildController
    {
        public const int MaxPending = 8;
        public const float TimeoutSeconds = 1f;
        // The server's building.maxRequestsPerSecond default: never send more in a second (placements and edits together).
        public const int MaxRequestsPerSecond = BuildRequestCounter.MaxRequestsPerSecond;

        private readonly Action<BuildRequest> _send;
        private readonly PendingBuild[] _pending = new PendingBuild[MaxPending];
        private int _pendingCount;
        private readonly BuildRequestCounter _counter;

        // 기능: 배치 쪽 컨트롤러를 만든다.
        // 입력: send - 요청을 보내는 함수, counter - 편집과 같이 쓰는 순번·전송 상한(null이면 혼자 쓰는 새 카운터).
        // 출력: 대기 배치가 없고 순번이 0인 컨트롤러.
        public BuildController(Action<BuildRequest> send, BuildRequestCounter counter = null)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _counter = counter ?? new BuildRequestCounter();
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
        public PendingBuild PendingAt(int i) => _pending[i];
        // Debug (F1): requests sent, refusals heard, and the newest refusal's reason.
        public int Sent { get; private set; }
        public int Refused { get; private set; }
        public BuildResultCode LastRefusal { get; private set; }
        // Changes whenever the pending set does (views redraw only then).
        public int PendingVersion { get; private set; }

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

        // 기능: 건설 모드 한 프레임: 후보를 고르고 판정하며, 버튼을 누르고 있으면 배치 요청을 보낸다.
        // 입력: now - 현재 시각, inBuildMode - 예측 도구가 Build이고 살아서 지상에 있으며 편집 중이 아님, pressed·held - 건설 버튼,
        //   feet·eye·yaw·pitch - 서버 기준 발·눈 위치와 시점, store - 확정 조각.
        // 출력: 요청을 보냈으면 true. 순번과 1초 상한은 편집과 같이 쓰는 카운터에서 받는다.
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
            if (held && !_counter.Allows(now)) return false;   // the rolling-second cap (shared with edits)
            if (Catalog != null) Turbo.Interval = Math.Max(0.05f, Catalog.MinBuildIntervalTicks / (float)SimHz);
            if (!Turbo.ShouldSend(now, pressed, held, CandidateState == BuildPreviewState.Valid && _pendingCount < MaxPending, BuildGrid.SlotKey(shape)))
                return false;
            var request = new BuildRequest
            {
                Sequence = _counter.Next(now), Piece = (byte)shape.Type, Material = (byte)Selection.Material, X = shape.X, Y = shape.Y, Z = shape.Z,
                Rotation = shape.Rotation,
            };
            _pending[_pendingCount++] = new PendingBuild { Sequence = request.Sequence, Shape = shape, Material = Selection.Material, SentAt = now };
            PendingVersion++;
            Sent++;
            _send(request);
            return true;
        }

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

        // 기능: 새 연결의 처음 상태로 되돌린다(서버 순번은 참가·재개마다 새로 시작한다).
        // 입력: 없음.
        // 출력: 반환값 없음. 대기 배치·후보·선택·자원이 비고, 같이 쓰는 카운터도 1부터 다시 센다.
        public void Reset()
        {
            _counter.Reset();
            _pendingCount = 0;
            PendingVersion++;
            HasCandidate = false;
            Selection.Reset();
            Resources = default;
        }

        private void DropConfirmed(BuildStore store)
        {
            if (store == null) return;
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (_pending[i].AcceptedId != 0 && store.TryGet(_pending[i].AcceptedId, out _)) RemoveAt(i);
            }
        }

        private bool IsPending(in BuildPieceShape shape)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Shape.Equals(shape)) return true;
            }
            return false;
        }

        private void Expire(float now)
        {
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (now - _pending[i].SentAt >= TimeoutSeconds) RemoveAt(i);
            }
        }

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
