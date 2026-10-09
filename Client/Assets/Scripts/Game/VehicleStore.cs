using System;
using System.Numerics;
using ProjectH.Client.Game.Audio;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 19 D15: a vehicle change worth a sound, found when a VehicleStates record is applied (AudioEventRules.VehicleSoundsFor).
    public struct VehicleChange
    {
        public byte Id;
        public VehicleSounds Sounds;
        public Vector3 Position;
    }

    // Phase 19 D4, D9, D15: the vehicles as the client knows them from VehicleStates (Unreliable, channel 0). A fixed pool of
    // VehicleSettings.MaxVehicles slots, one per vehicle id, each with a fixed ring of SampleCapacity records for the
    // interpolation at the render tick (the same delay as remote players). Rules:
    //  - a packet whose ServerTick is not newer than the last applied one is dropped (Unreliable may reorder);
    //  - a vehicle missing from the packets for HideSeconds is hidden: its slot is freed, so when it comes back it starts a new
    //    ring and its first record is a baseline (no sound), like a vehicle entering the interest range;
    //  - the latest packet's records are the truth for "who sits where" (IsSeated, the enter prompt) as long as a packet came
    //    within HideSeconds (with no vehicle at all the server sends nothing);
    //  - Driver and Passenger are kept per sample, so a remote player is drawn seated from the render tick on, not early.
    // Cleared at a disconnect and at a join or resume (Reset). Pure (no UnityEngine): the EditMode tests drive it. Main thread.
    public sealed class VehicleStore
    {
        public const float HideSeconds = 1f;
        public const int SampleCapacity = 8;
        // Phase 19 review: the handoff offset after the driver's prediction ends shrinks by e every 1/3 s (about 1 s to 5 %),
        // ends under 1 cm, and is not kept when larger than 10 m (a teleport or a different car, not a prediction lead).
        // Not faster: the sample keeps moving at the car's speed v while the offset (about v * lead, lead = how far the
        // prediction ran ahead of the render tick: the interpolation delay plus the round trip) shrinks at decay * v * lead,
        // so the drawn car would go backward once decay * lead > 1. 3/s keeps it moving forward for a lead up to 0.33 s.
        public const float HandoffDecayPerSecond = 3f;
        public const float HandoffEndOffset = 0.01f;
        public const float MaxHandoffOffset = 10f;

        private sealed class Slot
        {
            public bool Used;
            public byte Id;
            public float LastSeen;
            public uint LatestTick;
            public VehicleRecord Latest;
            public readonly uint[] Ticks = new uint[SampleCapacity];
            public readonly VehicleRecord[] Samples = new VehicleRecord[SampleCapacity];
            public int Count;
            public int Newest = -1;
        }

        private readonly Slot[] _slots = new Slot[VehicleSettings.MaxVehicles];
        private readonly VehicleRecord[] _latest = new VehicleRecord[VehicleSettings.MaxVehicles];
        private readonly VehicleChange[] _changes = new VehicleChange[VehicleSettings.MaxVehicles];
        // This frame's sample of every slot at the render tick (Render), read by the views and the seated checks.
        private readonly VehicleRecord[] _drawn = new VehicleRecord[VehicleSettings.MaxVehicles];
        private readonly bool[] _drawnValid = new bool[VehicleSettings.MaxVehicles];
        // Phase 19 review: the vehicle the driver's prediction drew last (-1 = none since the last DrawHandoff) and where, and the
        // vehicle drawn with a shrinking offset after the prediction ended (-1 = none). Ids are bytes; -1 means none.
        private int _overrideId = -1;
        private Vector3 _overridePosition;
        private int _handoffId = -1;
        private Vector3 _handoffOffset;
        private int _latestCount;
        private float _latestAt;
        private bool _hasTick;
        private uint _lastTick;

        // 기능: 빈 슬롯 풀을 만든다.
        // 입력: 없음.
        // 출력: 차량이 없는 저장소.
        public VehicleStore()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        }

        // The last applied packet's tick and its AckInputSeq (the driver's prediction reads them).
        public uint LastTick => _lastTick;
        public uint LastAck { get; private set; }
        // Packets dropped as older than the last applied one, and records dropped for want of a slot (debug, tests).
        public int DroppedOld { get; private set; }
        public int DroppedRecords { get; private set; }
        // Review fix D3 (SEC-26): packets refused as too far ahead of the last applied tick (F1 line).
        public int TickRejects { get; private set; }
        // How far past the last applied tick a packet may be, in seconds of server time, on top of the local time since it
        // (the server sends nothing while no vehicle is near, so a long quiet time is real). Same window as ServerClock.
        public const float MaxAheadSeconds = 10f;
        // The changes the last Apply found (valid until the next Apply).
        public int ChangeCount { get; private set; }
        // 기능: 마지막 Apply가 찾은 i번째 소리 변화를 돌려준다.
        // 입력: i - 0..ChangeCount-1.
        // 출력: 그 변화(차량 id, 소리, 위치).
        public VehicleChange Change(int i) => _changes[i];
        // The latest packet's records (empty once no packet came for HideSeconds).
        public ReadOnlySpan<VehicleRecord> Latest => new ReadOnlySpan<VehicleRecord>(_latest, 0, _latestCount);
        public int SlotCount => _slots.Length;

        // 기능: 슬롯이 지금 보이는 차량을 갖는지 본다.
        // 입력: slot - 슬롯 번호.
        // 출력: 보이면 true.
        public bool IsVisible(int slot) => _slots[slot].Used;

        // 기능: 보이는 차량 수를 센다(QA, 디버그).
        // 입력: 없음.
        // 출력: 보이는 슬롯 수.
        public int VisibleCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Used) n++;
                }
                return n;
            }
        }

        // 기능: VehicleStates 하나를 적용한다: 오래된 Tick이면 버리고, 아니면 최신 기록을 바꾸고 차량마다 표본을 더하며 소리 낼 변화를 모은다.
        //   리뷰 수정 D3: 마지막 적용 Tick보다 SimHz × (10초 + 그 뒤 지난 로컬 시간)을 넘게 앞선 Tick도 버린다(TickRejects).
        // 입력: serverTick·ackInputSeq - 패킷 헤더, records·count - 읽은 기록(NetClient의 재사용 배열), now - 받은 시각(초), simHz - 서버 Tick률.
        // 출력: 적용했으면 true(ChangeCount·Change가 이번 변화), 오래되었거나 너무 앞선 패킷이면 false(아무것도 바뀌지 않는다). 할당 없음.
        public bool Apply(uint serverTick, uint ackInputSeq, ReadOnlySpan<VehicleRecord> records, int count, float now, int simHz)
        {
            ChangeCount = 0;
            if (_hasTick && serverTick <= _lastTick)
            {
                DroppedOld++;
                return false;
            }
            float hz = simHz > 0 ? simHz : 30f;
            if (_hasTick && serverTick > _lastTick + (double)hz * (MaxAheadSeconds + Math.Max(0f, now - _latestAt)))
            {
                TickRejects++;
                return false;
            }
            _hasTick = true;
            _lastTick = serverTick;
            LastAck = ackInputSeq;
            int n = Math.Min(Math.Max(count, 0), Math.Min(records.Length, _latest.Length));
            for (int i = 0; i < n; i++) _latest[i] = records[i];
            _latestCount = n;
            _latestAt = now;
            for (int i = 0; i < n; i++)
            {
                VehicleRecord r = records[i];
                if (!IsFinite(r.Position) || !IsFinite(r.Heading) || !IsFinite(r.Speed)) continue;
                int index = Find(r.Id);
                bool baseline = index < 0;
                if (baseline) index = Allocate(r.Id);
                if (index < 0)
                {
                    DroppedRecords++;
                    continue;
                }
                Slot s = _slots[index];
                float elapsed = baseline ? 0f : (serverTick - s.LatestTick) / hz;
                VehicleSounds sounds = AudioEventRules.VehicleSoundsFor(s.Latest, r, baseline, elapsed);
                if (sounds != VehicleSounds.None && ChangeCount < _changes.Length)
                    _changes[ChangeCount++] = new VehicleChange { Id = r.Id, Sounds = sounds, Position = r.Position };
                s.Latest = r;
                s.LatestTick = serverTick;
                s.LastSeen = now;
                s.Newest = (s.Newest + 1) % SampleCapacity;
                s.Ticks[s.Newest] = serverTick;
                s.Samples[s.Newest] = r;
                if (s.Count < SampleCapacity) s.Count++;
            }
            return true;
        }

        // 기능: HideSeconds 동안 패킷에 없던 차량을 숨기고(슬롯을 비운다), 그동안 패킷이 하나도 없었으면 최신 기록도 비운다. 매 프레임 부른다.
        // 입력: now - 지금 시각(초, Apply와 같은 시계).
        // 출력: 반환값 없음.
        public void Expire(float now)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot s = _slots[i];
                if (s.Used && now - s.LastSeen > HideSeconds) Free(s);
            }
            if (_latestCount > 0 && now - _latestAt > HideSeconds) _latestCount = 0;
        }

        // 기능: 모두 잊는다(끊김, 입장·재개: 다음 기록은 모두 기준이고 Tick 비교도 처음부터. Phase 19 리뷰: 이어 그리기도 끝난다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            _overrideId = -1;
            _handoffId = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                Free(_slots[i]);
                _drawnValid[i] = false;
            }
            _latestCount = 0;
            _hasTick = false;
            _lastTick = 0;
            LastAck = 0;
            ChangeCount = 0;
        }

        // 기능: 이번 프레임에 모든 보이는 차량을 renderTick에서 표본으로 뽑아 둔다(Update에서 한 번, 원격 플레이어 렌더 전).
        // 입력: renderTick - 원격 플레이어와 같은 렌더 Tick.
        // 출력: 반환값 없음. Drawn·TrySeatAt이 이 값을 읽는다. 할당 없음.
        public void Render(double renderTick)
        {
            for (int i = 0; i < _slots.Length; i++) _drawnValid[i] = TrySample(i, renderTick, out _drawn[i]);
        }

        // 기능: 이번 프레임 표본 중 한 차량의 운동 값을 운전자 예측으로 바꾼다(D9: 내가 운전하는 차량은 예측 위치에 그리고, 그 조수석
        //   사람도 예측 좌석에 앉힌다). 상태·좌석·체력은 표본 그대로다. Phase 19 리뷰: 그린 예측 위치를 기억한다(예측이 끝나는 프레임의
        //   이어 그리기, DrawHandoff).
        // 입력: id - 차량 id, move - 그리는 예측 상태(위치·방향·속도·조향).
        // 출력: 반환값 없음. 그 차량이 이번 프레임에 보이지 않으면 아무것도 하지 않는다.
        public void OverrideDrawn(byte id, in VehicleMove move)
        {
            int i = FindDrawn(id);
            if (i < 0) return;
            _drawn[i].Position = move.Position;
            _drawn[i].Heading = move.Heading;
            _drawn[i].Speed = move.Speed;
            _drawn[i].Steer = move.Steer;
            _overrideId = id;
            _overridePosition = move.Position;
            _handoffId = -1;
        }

        // 기능: 운전 예측이 끝난 뒤 그 차량을 이어 그린다(Phase 19 리뷰: 달리다 내리면 예측 위치에서 렌더 Tick 표본으로 뒤로 튀었다). 예측이
        //   끝난 첫 프레임에 마지막으로 그린 예측 위치와 이번 표본의 차이를 오프셋으로 남기고, 이후 프레임마다 줄여 0이 되면 끝낸다
        //   (LocalPlayerPredictor의 교정 오프셋과 같은 방식). 위치만 잇고 방향은 표본 그대로다. 운전 중이 아닌 프레임마다 Render 뒤, 좌석·원격
        //   플레이어·차량 뷰가 표본을 읽기 전에 부른다.
        // 입력: deltaTime - 프레임 시간(초).
        // 출력: 반환값 없음. 오프셋이 남아 있으면 그 차량의 이번 프레임 표본 위치가 바뀐다. 할당 없음.
        public void DrawHandoff(float deltaTime)
        {
            if (_overrideId >= 0)
            {
                int first = FindDrawn((byte)_overrideId);
                Vector3 offset = first >= 0 ? _overridePosition - _drawn[first].Position : Vector3.Zero;
                _handoffId = first >= 0 && offset.LengthSquared() <= MaxHandoffOffset * MaxHandoffOffset ? _overrideId : -1;
                _handoffOffset = offset;
                _overrideId = -1;
            }
            else if (_handoffId >= 0)
            {
                _handoffOffset *= (float)Math.Exp(-HandoffDecayPerSecond * deltaTime);
            }
            if (_handoffId < 0) return;
            int i = FindDrawn((byte)_handoffId);
            if (i < 0 || _handoffOffset.LengthSquared() < HandoffEndOffset * HandoffEndOffset)
            {
                _handoffId = -1;
                return;
            }
            _drawn[i].Position += _handoffOffset;
        }

        // 기능: 지금 이어 그리는 오프셋을 돌려준다(테스트·디버그).
        // 입력: 없음.
        // 출력: 이어 그리는 중이면 남은 오프셋, 아니면 0.
        public Vector3 HandoffOffset => _handoffId >= 0 ? _handoffOffset : Vector3.Zero;

        // 기능: 이번 프레임에 그려지는 차량 id의 슬롯을 찾는다.
        // 입력: id - 차량 id.
        // 출력: 슬롯 번호, 이번 프레임에 그 차량 표본이 없으면 -1.
        private int FindDrawn(byte id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_drawnValid[i] && _slots[i].Id == id) return i;
            }
            return -1;
        }

        // 기능: id 차량의 이번 프레임 표본을 돌려준다.
        // 입력: id - 차량 id, record - 결과.
        // 출력: 이번 프레임에 그려지면 true와 그 표본.
        public bool TryGetDrawnById(byte id, out VehicleRecord record)
        {
            int i = FindDrawn(id);
            record = i >= 0 ? _drawn[i] : default;
            return i >= 0;
        }

        // 기능: 내가 앉은 차량의 이번 프레임 표본과 좌석을 찾는다(내 좌석 위치·카메라). 렌더 Tick 표본이 나를 적었으면 그것을 쓰고, 아직
        //   적지 않았으면(Phase 19 리뷰: 탄 직후 약 렌더 지연 동안) 최신 패킷의 차량 id·좌석으로 그 차량의 이번 프레임 표본을 쓴다.
        //   원격 플레이어는 렌더 Tick부터 앉히므로(TrySeatAt) 이 함수를 쓰지 않는다.
        // 입력: entityId - 내 Entity id, vehicle - 결과(그 차량의 이번 프레임 표본), seat - 결과(좌석 번호).
        // 출력: 앉아 있고 그 차량이 이번 프레임에 그려지면 true.
        public bool TryGetOwnSeat(ushort entityId, out VehicleRecord vehicle, out int seat)
        {
            if (TrySeatAt(entityId, out vehicle, out seat)) return true;
            if (TryFindSeat(entityId, out int index, out seat) && TryGetDrawnById(_latest[index].Id, out vehicle)) return true;
            seat = -1;
            return false;
        }

        // 기능: 이번 프레임에 그릴 슬롯의 표본을 돌려준다(Render가 뽑은 것).
        // 입력: slot - 슬롯 번호, record - 결과.
        // 출력: 보이고 표본이 있으면 true와 보간한 기록.
        public bool TryGetDrawn(int slot, out VehicleRecord record)
        {
            record = _drawn[slot];
            return _drawnValid[slot];
        }

        // 기능: 한 플레이어가 renderTick(이번 프레임 Render)에 어느 차량에 앉아 있는지 찾는다(원격 몸 자세·발소리).
        // 입력: entityId - 플레이어, vehicle - 결과(그 차량의 보간 표본), seat - 결과(좌석 번호).
        // 출력: 앉아 있으면 true.
        public bool TrySeatAt(ushort entityId, out VehicleRecord vehicle, out int seat)
        {
            seat = -1;
            vehicle = default;
            if (entityId == 0) return false;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_drawnValid[i]) continue;
                ref readonly VehicleRecord r = ref _drawn[i];
                if (r.Driver == entityId) seat = VehicleSettings.DriverSeat;
                else if (r.Passenger == entityId) seat = VehicleSettings.PassengerSeat;
                else continue;
                vehicle = r;
                return true;
            }
            return false;
        }

        // 기능: 최신 패킷 기준으로 한 플레이어가 앉아 있는지 찾는다(내 탄 상태, D15 IsSeated).
        // 입력: entityId - 플레이어, index - 결과(Latest 번호), seat - 결과(좌석 번호).
        // 출력: 앉아 있으면 true.
        public bool TryFindSeat(ushort entityId, out int index, out int seat)
        {
            index = -1;
            seat = -1;
            if (entityId == 0) return false;
            for (int i = 0; i < _latestCount; i++)
            {
                if (_latest[i].Driver == entityId) seat = VehicleSettings.DriverSeat;
                else if (_latest[i].Passenger == entityId) seat = VehicleSettings.PassengerSeat;
                else continue;
                index = i;
                return true;
            }
            return false;
        }

        // 기능: 최신 패킷 기준으로 한 플레이어가 앉아 있는지 본다.
        // 입력: entityId - 플레이어.
        // 출력: 앉아 있으면 true.
        public bool IsSeated(ushort entityId) => TryFindSeat(entityId, out _, out _);

        // 기능: 한 슬롯을 renderTick에서 보간한다(위치·방향·속도·조향은 두 표본 사이를 보간, 상태·좌석·체력은 renderTick 이하의 가장 새 표본,
        //   첫 표본보다 앞이면 첫 표본, 마지막보다 뒤면 마지막 표본 그대로: 외삽하지 않는다).
        // 입력: slot - 슬롯 번호, renderTick - 렌더 Tick, record - 결과.
        // 출력: 보이고 표본이 있으면 true.
        public bool TrySample(int slot, double renderTick, out VehicleRecord record)
        {
            record = default;
            Slot s = _slots[slot];
            if (!s.Used || s.Count == 0) return false;
            for (int i = 0; i < s.Count; i++)
            {
                int index = (s.Newest - i + SampleCapacity) % SampleCapacity;
                if (s.Ticks[index] > renderTick) continue;
                record = s.Samples[index];
                if (i == 0) return true;
                int next = (index + 1) % SampleCapacity;
                ref readonly VehicleRecord b = ref s.Samples[next];
                float t = (float)((renderTick - s.Ticks[index]) / (s.Ticks[next] - s.Ticks[index]));
                record.Position = Vector3.Lerp(record.Position, b.Position, t);
                record.Heading = LerpHeading(record.Heading, b.Heading, t);
                record.Speed += (b.Speed - record.Speed) * t;
                record.Steer += (b.Steer - record.Steer) * t;
                return true;
            }
            record = s.Samples[(s.Newest - s.Count + 1 + SampleCapacity) % SampleCapacity];
            return true;
        }

        // 기능: 두 방향 사이를 짧은 쪽으로 보간한다.
        // 입력: a, b - 도, t - 0..1.
        // 출력: 0..360의 도.
        public static float LerpHeading(float a, float b, float t)
        {
            float delta = (b - a) % 360f;
            if (delta > 180f) delta -= 360f;
            else if (delta < -180f) delta += 360f;
            return VehicleSimulation.NormalizeHeading(a + delta * t);
        }

        // 기능: id의 슬롯을 찾는다.
        // 입력: id - 차량 id.
        // 출력: 슬롯 번호, 없으면 -1.
        private int Find(byte id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Used && _slots[i].Id == id) return i;
            }
            return -1;
        }

        // 기능: 빈 슬롯을 id에 준다(표본 고리는 비어 있다).
        // 입력: id - 차량 id.
        // 출력: 슬롯 번호, 빈 슬롯이 없으면 -1.
        private int Allocate(byte id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot s = _slots[i];
                if (s.Used) continue;
                s.Used = true;
                s.Id = id;
                s.Count = 0;
                s.Newest = -1;
                return i;
            }
            return -1;
        }

        // 기능: 슬롯을 비운다(숨김).
        // 입력: s - 슬롯.
        // 출력: 반환값 없음.
        private static void Free(Slot s)
        {
            s.Used = false;
            s.Count = 0;
            s.Newest = -1;
            s.Latest = default;
        }

        // 기능: 값이 NaN도 무한대도 아닌지 본다.
        // 입력: v - 검사할 값.
        // 출력: 유한하면 true.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // 기능: 벡터의 세 성분이 모두 유한한지 본다.
        // 입력: v - 검사할 벡터.
        // 출력: 세 성분 모두 유한하면 true.
        private static bool IsFinite(Vector3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
    }
}
