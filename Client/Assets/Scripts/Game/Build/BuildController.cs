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
        // 기능: i번째 대기 배치를 돌려준다(미리보기가 그린다).
        // 입력: i - 0..PendingCount-1.
        // 출력: 그 대기 배치.
        public PendingBuild PendingAt(int i) => _pending[i];
        // Debug (F1): requests sent, refusals heard, and the newest refusal's reason.
        public int Sent { get; private set; }
        public int Refused { get; private set; }
        public BuildResultCode LastRefusal { get; private set; }
        // Changes whenever the pending set does (views redraw only then).
        public int PendingVersion { get; private set; }

        // 기능: 화면에 보일 재료 수를 낸다(request §107): 서버 수치에서 아직 답이 없는 대기 배치의 비용을 뺀다(Ok를 받은 것은 빼지 않는다).
        // 입력: material - 재료.
        // 출력: 0 이상의 보이는 수. 카탈로그가 없으면 서버 수치 그대로.
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

        // 기능: 서버보다 먼저 Client가 알 수 있는 것으로 후보를 판정한다: 사거리 밖, 자리 차지, 재료 부족.
        // 입력: shape - 후보 모양, eye - 서버 기준 눈 위치, store - 확정 조각(null이면 자리 검사를 건너뛴다).
        // 출력: 사거리 밖이거나 자리가 차 있으면 Invalid, 보이는 재료가 비용보다 적으면 NoResource, 아니면 Valid.
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

        // 기능: 배치 요청의 BuildResult를 그 순번의 대기 배치에 반영한다.
        // 입력: result - 서버 결과.
        // 출력: 반환값 없음. 거절이면 Refused·LastRefusal이 갱신되고 대기 배치가 빠진다. Ok(조각 id 있음)면 확정 조각이 올 때까지
        //   AcceptedId를 적어 두고 남긴다. 모르는 순번이면 대기 배치는 그대로다.
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

        // 기능: Ok를 받은 대기 배치 중 확정 조각이 저장소에 들어온 것을 뺀다(깜빡임 없이 확정 조각으로 바뀐다).
        // 입력: store - 확정 조각 저장소(null이면 아무것도 하지 않는다).
        // 출력: 반환값 없음. 뺀 것이 있으면 PendingVersion이 오른다.
        private void DropConfirmed(BuildStore store)
        {
            if (store == null) return;
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (_pending[i].AcceptedId != 0 && store.TryGet(_pending[i].AcceptedId, out _)) RemoveAt(i);
            }
        }

        // 기능: 같은 모양의 배치가 이미 답을 기다리고 있는지 본다.
        // 입력: shape - 후보 모양.
        // 출력: 대기 중이면 true.
        private bool IsPending(in BuildPieceShape shape)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Shape.Equals(shape)) return true;
            }
            return false;
        }

        // 기능: 보낸 지 TimeoutSeconds가 지난 대기 배치를 뺀다(답이 오지 않은 경우).
        // 입력: now - 현재 시각(초).
        // 출력: 반환값 없음. 뺀 것이 있으면 PendingVersion이 오른다.
        private void Expire(float now)
        {
            for (int i = _pendingCount - 1; i >= 0; i--)
            {
                if (now - _pending[i].SentAt >= TimeoutSeconds) RemoveAt(i);
            }
        }

        // 기능: 대기 배치 하나를 뺀다(마지막 것을 그 자리로 옮긴다: 순서는 의미가 없다).
        // 입력: i - 대기 배열의 위치.
        // 출력: 반환값 없음. 대기 수가 하나 줄고 PendingVersion이 오른다.
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
