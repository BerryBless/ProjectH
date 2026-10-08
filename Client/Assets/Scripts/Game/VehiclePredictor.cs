using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Game
{
    // Phase 19 D9, D15: the driver's prediction of its own vehicle. LocalPlayerPredictor owns one and steps it with every input
    // it makes while the player drives (one fixed-step clock, one render alpha), against its own CollisionWorld gathered exactly
    // as the server's vehicle step does (Gather at the position before the step, then VehicleSimulation.Step). The results of the
    // last HistorySize inputs are kept by seq; a VehicleStates record of our vehicle with its AckInputSeq is compared with the
    // result of that input (within the wire quantization) and, when it differs, the state restarts from the record and the
    // unacked inputs are replayed. A correction over SnapDistance jumps, a smaller one is smoothed. The prediction starts from the
    // first record naming us as Driver (Begin through Reconcile) and stops when we leave the driver seat (Stop).
    // Pure (no UnityEngine): the EditMode tests drive it. Main thread only.
    public sealed class VehiclePredictor
    {
        public const float SnapDistance = 2f;
        private const float ErrorDecayPerSecond = 10f;
        // A little above the wire resolution (1/256 m, 360/65536 deg, 1/256 m/s), so a matching prediction is not a correction.
        public const float PositionEpsilon = 0.02f;
        public const float HeadingEpsilon = 0.05f;
        public const float SpeedEpsilon = 0.02f;

        private readonly LocalPlayerPredictor _owner;
        private readonly float _stepSeconds;
        private readonly CollisionWorld _collision = new CollisionWorld();
        private readonly VehicleMove[] _results = new VehicleMove[LocalPlayerPredictor.HistorySize];
        private VehicleMove _state;
        private VehicleMove _previous;
        private NVector3 _renderError;
        // The first input seq whose result is in _results (inputs before it were not predicted for this vehicle).
        private uint _validFrom;

        // 기능: 운전 예측기를 만든다(아직 예측하지 않는다).
        // 입력: owner - 입력·충돌 세계(문·채집 대상·조각)를 주는 이동 예측기, stepSeconds - 서버 Tick 길이(초).
        // 출력: 비활성 예측기.
        internal VehiclePredictor(LocalPlayerPredictor owner, float stepSeconds)
        {
            _owner = owner;
            _stepSeconds = stepSeconds;
        }

        public bool Active { get; private set; }
        public byte VehicleId { get; private set; }
        public VehicleMove State => _state;
        public NVector3 RenderPosition { get; private set; }
        public float RenderHeading { get; private set; }
        // The newest step was blocked (debug), how far the newest correction moved the prediction, and how many there were.
        public bool LastBlocked { get; private set; }
        public float LastCorrection { get; private set; }
        public int Corrections { get; private set; }

        // 기능: 앉은 좌석의 발 위치를 예측 상태(가장 새 Step)로 구한다.
        // 입력: seat - 좌석 번호.
        // 출력: 월드 발 위치.
        public NVector3 SeatFeet(int seat) => VehicleSimulation.SeatPosition(_state.Position, _state.Heading, seat);

        // 기능: 앉은 좌석의 발 위치를 그리는 상태(보간 + 교정 오프셋)로 구한다.
        // 입력: seat - 좌석 번호.
        // 출력: 월드 발 위치.
        public NVector3 RenderSeat(int seat) => VehicleSimulation.SeatPosition(RenderPosition, RenderHeading, seat);

        // 기능: 입력 하나로 차량을 한 Tick 예측한다(서버처럼 Step 전 위치에서 Gather, 운전자 있음).
        // 입력: command - 이번 입력.
        // 출력: 반환값 없음. 상태가 바뀌고 그 seq의 결과가 기록된다. 비활성이면 아무것도 하지 않는다. 할당 없음.
        internal void Step(in InputCommand command)
        {
            if (!Active) return;
            _previous = _state;
            Simulate(ref _state, command);
            _results[(int)(command.Seq % LocalPlayerPredictor.HistorySize)] = _state;
        }

        // 기능: 그리는 위치·방향을 정한다(두 Step 사이 보간 + 줄어드는 교정 오프셋). Advance 끝에서 부른다.
        // 입력: alpha - 두 Step 사이 비율(0..1), deltaTime - 프레임 시간.
        // 출력: 반환값 없음. RenderPosition·RenderHeading이 바뀐다.
        internal void UpdateRender(float alpha, float deltaTime)
        {
            if (!Active) return;
            _renderError *= (float)Math.Exp(-ErrorDecayPerSecond * deltaTime);
            RenderPosition = NVector3.Lerp(_previous.Position, _state.Position, alpha) + _renderError;
            RenderHeading = VehicleStore.LerpHeading(_previous.Heading, _state.Heading, alpha);
        }

        // 기능: 우리 차량의 서버 기록으로 예측을 맞춘다. 처음이거나 다른 차량이면 그 기록과 ack에서 예측을 시작한다(D15 인계).
        //   ack 입력의 예측 결과가 기록과 양자화 오차 안이면 그대로 두고, 아니면 기록에서 다시 시작해 ack 뒤 입력을 재실행한다.
        // 입력: record - 우리가 Driver인 기록, ackSeq - 그 패킷의 AckInputSeq.
        // 출력: 반환값 없음. 교정이 2 m를 넘으면 바로 옮기고, 아니면 차이를 부드럽게 줄인다. 유한하지 않은 기록은 무시한다.
        public void Reconcile(in VehicleRecord record, uint ackSeq)
        {
            if (!IsFinite(record.Position.X) || !IsFinite(record.Position.Y) || !IsFinite(record.Position.Z) ||
                !IsFinite(record.Heading) || !IsFinite(record.Speed) || !IsFinite(record.Steer))
                return;
            var authoritative = new VehicleMove { Position = record.Position, Heading = record.Heading, Speed = record.Speed, Steer = record.Steer };
            uint last = _owner.LastSeq;
            if (!Active || VehicleId != record.Id)
            {
                Active = true;
                VehicleId = record.Id;
                Restart(authoritative, ackSeq, last);
                _renderError = NVector3.Zero;
                RenderPosition = _state.Position;
                RenderHeading = _state.Heading;
                LastCorrection = 0f;
                return;
            }

            NVector3 old = _state.Position;
            bool comparable = ackSeq >= _validFrom && ackSeq > 0 && ackSeq <= last && last - ackSeq < LocalPlayerPredictor.HistorySize;
            if (comparable && Matches(_results[(int)(ackSeq % LocalPlayerPredictor.HistorySize)], authoritative)) return;
            Restart(authoritative, ackSeq, last);
            NVector3 correction = old - _state.Position;
            _renderError = correction.LengthSquared() > SnapDistance * SnapDistance ? NVector3.Zero : _renderError + correction;
            LastCorrection = correction.Length();
            Corrections++;
        }

        // 기능: 예측을 멈춘다(운전석을 떠남, 사망, 끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Stop()
        {
            Active = false;
            VehicleId = 0;
            _renderError = NVector3.Zero;
        }

        // 기능: 기록 상태에서 다시 시작하고 ack 뒤의 입력(기록이 남아 있으면)을 재실행한다.
        // 입력: authoritative - 서버 상태, ackSeq - 그 상태가 반영한 마지막 입력, last - 지금 가장 새 입력 seq.
        // 출력: 반환값 없음. _state·_previous·결과 기록이 바뀐다.
        private void Restart(in VehicleMove authoritative, uint ackSeq, uint last)
        {
            _state = authoritative;
            _previous = authoritative;
            bool replay = ackSeq > 0 && ackSeq <= last && last - ackSeq < LocalPlayerPredictor.HistorySize;
            if (!replay)
            {
                _validFrom = last + 1;
                return;
            }
            _results[(int)(ackSeq % LocalPlayerPredictor.HistorySize)] = authoritative;
            _validFrom = ackSeq;
            for (uint seq = ackSeq + 1; seq <= last; seq++)
            {
                _previous = _state;
                Simulate(ref _state, _owner.InputAt(seq));
                _results[(int)(seq % LocalPlayerPredictor.HistorySize)] = _state;
            }
        }

        // 기능: 서버 Match의 차량 Step과 같은 순서로 한 Tick을 계산한다(Step 전 위치에서 Gather, 운전자 있음).
        // 입력: state - 상태, command - 입력.
        // 출력: 반환값 없음. state가 바뀌고 LastBlocked가 정해진다.
        private void Simulate(ref VehicleMove state, in InputCommand command)
        {
            _collision.Gather(state.Position, _owner.DoorMask, _owner.DestroyedHarvestables, _owner.Pieces);
            VehicleSimulation.Step(ref state, command, true, _stepSeconds, _collision, GameMap.Terrain, out VehicleStepResult result);
            LastBlocked = result.Blocked;
        }

        // 기능: 예측 결과가 서버 기록과 선로 양자화 오차 안에서 같은지 본다.
        // 입력: predicted - 그 입력의 예측 결과, server - 기록 상태.
        // 출력: 같으면 true.
        public static bool Matches(in VehicleMove predicted, in VehicleMove server)
        {
            float dh = Math.Abs((predicted.Heading - server.Heading) % 360f);
            if (dh > 180f) dh = 360f - dh;
            return NVector3.DistanceSquared(predicted.Position, server.Position) < PositionEpsilon * PositionEpsilon &&
                   dh < HeadingEpsilon && Math.Abs(predicted.Speed - server.Speed) < SpeedEpsilon;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
