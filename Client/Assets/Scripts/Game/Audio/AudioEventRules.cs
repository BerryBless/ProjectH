using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D6-D9: which game events make which sound, and the state the change-based sounds compare against. The source
    // table (one event = one sound, Spec D9):
    //   our shot             -> the predicted shot (the server's ShotFired of our own is ignored)
    //   our rocket           -> the predicted shot only (our own ProjectileSpawned is ignored)
    //   a grenade            -> ProjectileSpawned only (there is no throw prediction)
    //   our placement        -> BuildResult Ok of a placement; a Placed record makes a sound only when its owner is not us
    //   our edit             -> the confirmed (sent) edit prediction; an Edited record makes a sound only when the owner is not us
    //   our door             -> the predicted door change; the server mask that equals the prediction makes none
    // The baseline rule: the first state after a join, a resume, a round reset or entering a build interest cell is a starting
    // point, not a change (Sync records make no sound; the trackers below say how each state does it). Pure (no UnityEngine):
    // the EditMode tests drive every rule here.
    public static class AudioEventRules
    {
        // Phase 19 D15: an impact sounds when the speed of an Active vehicle falls by more than braking can take off between two
        // records plus this margin (m/s), from at least ImpactMinSpeed. A blocked tick sets the speed to 0 at once; braking (24
        // m/s^2) removes about 1.6 m/s between two 15 Hz records.
        public const float ImpactSpeedMargin = 2f;
        public const float ImpactMinSpeed = 5f;
        // Records further apart than this are not compared for an impact (a gap hides what happened between them).
        public const float ImpactMaxGapSeconds = 0.25f;

        // 기능: 같은 차량의 이전 기록과 새 기록을 비교해 낼 소리를 고른다(Phase 19 D15: Phase 18 기준 상태 규칙). 기준 기록(입장·재개 뒤,
        //   관심 영역에 들어온 첫 기록, 숨김 뒤 다시 보인 첫 기록)은 소리가 없다. Active → Wrecked는 파괴(폭발 소리, 그때 내리는 사람의 내림
        //   소리는 내지 않는다), 좌석에 새 사람이 앉으면 탐, 좌석이 비거나 사람이 바뀌면 내림, 두 기록 사이 속도가 제동보다 크게 줄면 충돌.
        // 입력: previous - 이전 기록, current - 새 기록, baseline - 기준 기록인지, elapsedSeconds - 두 기록의 서버 시간 차(초).
        // 출력: 낼 소리 비트(없으면 None).
        public static VehicleSounds VehicleSoundsFor(in VehicleRecord previous, in VehicleRecord current, bool baseline, float elapsedSeconds)
        {
            if (baseline) return VehicleSounds.None;
            if (previous.State == VehicleState.Active && current.State == VehicleState.Wrecked) return VehicleSounds.Wrecked;
            if (current.State != VehicleState.Active || previous.State != VehicleState.Active) return VehicleSounds.None;
            VehicleSounds sounds = VehicleSounds.None;
            SeatChanged(previous.Driver, current.Driver, ref sounds);
            SeatChanged(previous.Passenger, current.Passenger, ref sounds);
            float before = Math.Abs(previous.Speed);
            float after = Math.Abs(current.Speed);
            if (elapsedSeconds > 0f && elapsedSeconds <= ImpactMaxGapSeconds && before >= ImpactMinSpeed &&
                before - after > VehicleSettings.BrakeDeceleration * elapsedSeconds + ImpactSpeedMargin)
                sounds |= VehicleSounds.Impact;
            return sounds;
        }

        // 기능: 좌석 하나의 바뀜을 소리 비트에 더한다.
        // 입력: before·after - 이전·새 좌석의 Entity id(0 = 빔), sounds - 더할 비트.
        // 출력: 반환값 없음. 비었던 좌석에 앉으면 Entered, 앉은 사람이 떠나면 Exited, 사람이 바뀌면 둘 다 더해진다.
        private static void SeatChanged(ushort before, ushort after, ref VehicleSounds sounds)
        {
            if (before == after) return;
            if (before != 0) sounds |= VehicleSounds.Exited;
            if (after != 0) sounds |= VehicleSounds.Entered;
        }

        // 기능: 받은 피해의 소리를 고른다(D8: DamageTaken 플래그).
        // 입력: damage - 받은 DamageTaken.
        // 출력: 실드 깨짐이면 ShieldBreak, 실드 맞음이면 ShieldHit, 아니면 HealthHit.
        public static SoundKind DamageSound(in DamageTaken damage)
        {
            if (damage.ShieldBroken) return SoundKind.ShieldBreak;
            return damage.ShieldHit ? SoundKind.ShieldHit : SoundKind.HealthHit;
        }

        // 기능: 투사체 생성 사건이 발사·던지기 소리를 내는지 정한다(D4, D9: 출처 표와 재전송 규칙). 서버는 입장·재개 때 날고 있는 투사체를
        //   StartTick = 마지막으로 끝난 Tick(= 입장 응답의 ServerTick)으로 다시 보내고, 실제 발사는 시뮬레이션 중인 Tick(ServerTick + 1)을 싣는다.
        //   그래서 StartTick이 입장 Tick 이하면 재전송이다.
        // 입력: spawned - 받은 생성 사건, myId - 내 Entity id, joinTick - 이번 연결의 입장·재개 응답 ServerTick, kind - 결과 소리,
        //   own - 결과(내 수류탄이면 true: 2D로 낸다).
        // 출력: 소리를 내면 true. 주인이 0(경기에 없음: 재전송)이거나, StartTick ≤ joinTick(재전송)이거나, 내 로켓(예측이 이미 냈다)이거나
        //   모르는 종류면 false.
        public static bool LaunchSound(in ProjectileSpawned spawned, ushort myId, uint joinTick, out SoundKind kind, out bool own)
        {
            kind = SoundKind.GrenadeThrow;
            own = spawned.OwnerId != 0 && spawned.OwnerId == myId;
            if (spawned.OwnerId == 0 || spawned.StartTick <= joinTick) return false;
            switch (spawned.Kind)
            {
                case ProjectileKind.Grenade:
                    kind = SoundKind.GrenadeThrow;
                    return true;
                case ProjectileKind.Rocket:
                    kind = SoundKind.RocketLaunch;
                    return !own;
                default:
                    return false;
            }
        }

        // 기능: 다른 사람의 사격(ShotFired)이 총성을 내는지 본다(D9: 내 사격은 예측이 냈다).
        // 입력: shooterId - 쏜 사람, myId - 내 Entity id.
        // 출력: 다른 사람이면 true.
        public static bool RemoteShotSounds(ushort shooterId, ushort myId) => shooterId != myId;

        // 기능: 사건 Placed·Edited 기록이 소리를 내는지 본다(D6: 내 조각은 BuildResult Ok·확정 예측 때 이미 냈다).
        // 입력: owner - 조각 주인, myId - 내 Entity id.
        // 출력: 주인이 내가 아니면 true.
        public static bool OthersBuildSounds(ushort owner, ushort myId) => owner != myId;

        // 기능: Health 기록이 피해 소리를 내는지 본다(D6: 체력이 줄 때만. 간격은 믹서의 조각별 중복 규칙 0.15초).
        // 입력: oldDamage - 적용 전 피해, newDamage - 기록의 피해.
        // 출력: 피해가 늘었으면 true.
        public static bool HealthDropped(ushort oldDamage, ushort newDamage) => newDamage > oldDamage;

        // A reboot puts the player RebootSpotRadius (1.2 m) from its station; this is the margin the check allows.
        public const float RebootStationRange = 3f;

        // 기능: 부활이 재투입인지 본다(D9 재투입 완료: 경기 중 탈락했다가, 또는 스테이션 옆에서 Ground 모드로 돌아옴. 경기 시작은 Transport이거나
        //   경기 전 상태이고, 라운드 리셋은 Finished 뒤라 경기 중이 아니다).
        // 입력: wasDead - 부활 전 탈락 상태였는지(원격은 Snapshot이 먼저 살아 있음을 알릴 수 있다), nearStation - 스테이션 옆에서 부활했는지,
        //   mode - 부활 모드, hasMatch - MatchState를 받았는지, state - 경기 상태.
        // 출력: 재투입이면 true.
        public static bool IsReboot(bool wasDead, bool nearStation, MovementMode mode, bool hasMatch, MatchFlowState state) =>
            (wasDead || nearStation) && mode == MovementMode.Ground && hasMatch &&
            (state == MatchFlowState.Playing || state == MatchFlowState.FinalPhase);

        // 기능: 위치가 어느 Reboot Station에서 RebootStationRange(수평) 안인지 본다.
        // 입력: position - 부활 위치.
        // 출력: 안이면 true.
        public static bool NearRebootStation(Vector3 position)
        {
            ReadOnlySpan<Vector3> stations = RebootStations.All;
            for (int i = 0; i < stations.Length; i++)
            {
                float dx = stations[i].X - position.X;
                float dz = stations[i].Z - position.Z;
                if (dx * dx + dz * dz <= RebootStationRange * RebootStationRange) return true;
            }
            return false;
        }
    }

    // Phase 19 D15: the sounds one vehicle record can make (AudioEventRules.VehicleSoundsFor).
    [Flags]
    public enum VehicleSounds : byte
    {
        None = 0,
        Entered = 1,
        Exited = 2,
        Impact = 4,
        Wrecked = 8,
    }

    // Phase 18 D9: door sounds. The server's changes come through OnServerState (right after PredictedDoors.ApplyServer), our own
    // predicted changes through Poll (PredictedDoors.OpenMask once per frame), each compared with the last mask heard:
    //  - our predicted change sounds at once (Poll) and is remembered for PredictionSeconds; the server's mask that equals it
    //    then changes nothing;
    //  - another player's change sounds when its DoorStates arrives;
    //  - one key press = one sound: ApplyServer drops every prediction, so a DoorStates about another door that arrives before
    //    ours is confirmed shows our door back in the server's old state for a moment. A change of a door against our own
    //    prediction within PredictionSeconds is therefore not heard (in OnServerState or Poll); the heard bit keeps the
    //    predicted state, so the confirmation that follows is silent too. A prediction the server never confirms is heard
    //    going back once its window has passed.
    // Baseline: after a join or resume the first DoorStates is only heard (the server always sends one then). At a match start
    // the server closes every door (CloseAll) and sends DoorStates only if one was open, so that baseline expires after
    // BaselineSeconds instead of waiting for a packet that may never come. A round reset and a dev respawn do not touch doors
    // and arm nothing. Pure, fixed arrays.
    public sealed class DoorSoundTracker
    {
        public const float BaselineSeconds = 1f;
        public const float PredictionSeconds = PredictedDoors.PredictionSeconds;

        private byte _heard;
        private bool _pending;
        private bool _expires;
        private float _until;
        // Per door: when our own predicted change stops counting (0 = none), and the predicted state (bit i).
        private readonly float[] _predictedUntil = new float[8];
        private byte _predicted;

        // 기능: 다음 서버 문 상태를 기준으로 삼게 한다.
        // 입력: now - 현재 시각, expires - true면 BaselineSeconds 뒤에 풀린다(경기 시작), false면 다음 DoorStates까지(입장·재개).
        // 출력: 반환값 없음. 기억한 예측도 지운다.
        public void ArmBaseline(float now, bool expires)
        {
            _pending = true;
            _expires = expires;
            _until = now + BaselineSeconds;
            ClearPredictions();
        }

        // 기능: 모두 닫힌 상태를 들은 것으로 하고 다음 서버 상태를 기다린다(끊김: PredictedDoors.Reset과 같은 0).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            _heard = 0;
            _pending = true;
            _expires = false;
            ClearPredictions();
        }

        // 기능: DoorStates를 적용한 직후 부른다. 기준을 기다리는 중이면 그 마스크를 소리 없이 들은 것으로 한다. 아니면 서버가 바꾼 문을 낸다
        //   (PredictionSeconds 안의 내 예측과 반대로 바뀐 문은 소리 없음, 예측과 같아진 문은 예측을 지운다).
        // 입력: mask - 적용 뒤의 예측 마스크(PredictedDoors.OpenMask), now - 현재 시각, opened·closed - 결과(소리 낼 열린·닫힌 문 비트).
        // 출력: 소리 낼 문이 있으면 true. 들은 마스크가 갱신된다.
        public bool OnServerState(byte mask, float now, out int opened, out int closed)
        {
            opened = 0;
            closed = 0;
            if (_pending)
            {
                _pending = false;
                if (!_expires || now <= _until)
                {
                    _heard = mask;
                    ClearPredictions();
                    return false;
                }
            }
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                int bit = 1 << i;
                bool active = _predictedUntil[i] > now;
                if (active && (mask & bit) == (_predicted & bit))
                {
                    _predictedUntil[i] = 0f;   // the server confirmed our prediction
                    continue;
                }
                if ((mask & bit) == (_heard & bit) || active) continue;
                if ((mask & bit) != 0) opened |= bit;
                else closed |= bit;
            }
            _heard = (byte)(_heard ^ (opened | closed));
            return (opened | closed) != 0;
        }

        // 기능: 지금 예측 마스크를 마지막으로 들은 마스크와 비교한다(LateUpdate에서 한 번). 서버 변화는 OnServerState가 이미 들었으므로
        //   여기 차이는 내 예측(또는 확인되지 않은 예측이 돌아감)이다. 바뀐 문은 예측으로 기억한다.
        // 입력: mask - 지금 예측 마스크, now - 현재 시각, opened·closed - 결과(새로 열린 문 비트, 새로 닫힌 문 비트).
        // 출력: 바뀐 문이 있으면 true. 기억한 예측과 반대인 동안(서버 상태가 예측을 잠깐 지운 경우)은 소리 없이 들은 상태를 유지한다.
        public bool Poll(byte mask, float now, out int opened, out int closed)
        {
            opened = 0;
            closed = 0;
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                int bit = 1 << i;
                if ((mask & bit) == (_heard & bit)) continue;
                if (_predictedUntil[i] > now && (mask & bit) != (_predicted & bit)) continue;
                if ((mask & bit) != 0) opened |= bit;
                else closed |= bit;
                _predictedUntil[i] = now + PredictionSeconds;
                _predicted = (byte)((_predicted & ~bit) | (mask & bit));
            }
            _heard = (byte)(_heard ^ (opened | closed));
            return (opened | closed) != 0;
        }

        // 기능: 기억한 예측을 모두 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        private void ClearPredictions()
        {
            Array.Clear(_predictedUntil, 0, _predictedUntil.Length);
            _predicted = 0;
        }
    }

    // Phase 18 D9: a container opening = its opened bit going 0 -> 1 while it was already spawned in the previous state. The
    // first state after a join or resume (Reset) is only remembered; a round reset clears the masks (no opening).
    public sealed class ContainerSoundTracker
    {
        private bool _hasState;
        private ulong _spawned;
        private ulong _opened;

        // 기능: 상태를 잊는다(입장·재개·끊김). 다음 상태는 기준이다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            _hasState = false;
            _spawned = 0;
            _opened = 0;
        }

        // 기능: 받은 ContainerStates를 이전 상태와 비교한다.
        // 입력: spawned - 생성 비트, opened - 열림 비트.
        // 출력: 새로 열린 Container 비트(기준 상태면 0). 이 상태가 다음 비교의 기준이 된다.
        public ulong Apply(ulong spawned, ulong opened)
        {
            ulong fresh = _hasState ? opened & ~_opened & _spawned : 0UL;
            _hasState = true;
            _spawned = spawned;
            _opened = opened;
            return fresh;
        }
    }

    // Phase 18 D9: a supply drop landing = an Id that was Falling in the previous list and is Landed (or already opened) now.
    // A drop first seen landed (a join, a resume) makes no sound; a round reset empties the list.
    public sealed class SupplyDropSoundTracker
    {
        private readonly SupplyDropState[] _states = new SupplyDropState[SupplyDropsPacket.MaxSupplyDrops];
        private readonly bool[] _known = new bool[SupplyDropsPacket.MaxSupplyDrops];

        // 기능: 상태를 잊는다(입장·재개·끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            Array.Clear(_known, 0, _known.Length);
        }

        // 기능: 받은 SupplyDrops 목록을 이전 목록과 비교한다.
        // 입력: drops·count - 받은 목록(NetClient의 재사용 배열, 읽기만 한다).
        // 출력: 이번에 착지한 Id 비트. 이 목록이 다음 비교의 기준이 된다(목록에 없는 Id는 잊는다).
        public int Apply(SupplyDropInfo[] drops, int count)
        {
            int landed = 0;
            int n = drops == null ? 0 : Math.Max(0, Math.Min(count, drops.Length));
            int seen = 0;
            for (int i = 0; i < n; i++)
            {
                SupplyDropInfo d = drops[i];
                if (d.Id >= _states.Length) continue;
                if (_known[d.Id] && _states[d.Id] == SupplyDropState.Falling && d.State != SupplyDropState.Falling) landed |= 1 << d.Id;
                _states[d.Id] = d.State;
                _known[d.Id] = true;
                seen |= 1 << d.Id;
            }
            for (int id = 0; id < _known.Length; id++)
            {
                if ((seen & (1 << id)) == 0) _known[id] = false;
            }
            return landed;
        }
    }

    // Phase 18 D9: the zone shrink warning sounds only when the client itself sees the estimated server tick pass ShrinkStartTick.
    // A ZoneState whose shrink has already started when it arrives (a join, a resume, a late packet) is not armed.
    public sealed class ZoneShrinkWatch
    {
        private bool _armed;
        private uint _start;
        private double _lastTick;

        // 기능: 받은 ZoneState로 경고를 준비한다.
        // 입력: zone - 받은 상태, estimatedTick - 받은 순간의 추정 서버 Tick(0 = 모름).
        // 출력: 반환값 없음. 시작 Tick이 아직 앞이고 단계가 있으면 준비된다.
        public void OnZoneState(in ZoneState zone, double estimatedTick)
        {
            _armed = zone.Phase != 0 && estimatedTick > 0 && zone.ShrinkStartTick > estimatedTick;
            _start = zone.ShrinkStartTick;
            _lastTick = estimatedTick;
        }

        // 기능: 프레임마다 지금 Tick을 보고 축소 시작을 지났는지 본다.
        // 입력: tick - 지금 추정 서버 Tick(0 = 모름), inMatch - 경기 중(Playing·FinalPhase).
        // 출력: 준비된 상태에서 지난 프레임 Tick < 시작 ≤ 지금 Tick이고 경기 중이면 true(한 번만). 경기 밖에서 지나면 소리 없이 풀린다.
        public bool Update(double tick, bool inMatch)
        {
            if (tick <= 0) return false;
            bool crossed = _armed && _lastTick < _start && tick >= _start;
            _lastTick = tick;
            if (!crossed) return false;
            _armed = false;
            return inMatch;
        }

        // 기능: 준비를 푼다(끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset()
        {
            _armed = false;
            _lastTick = 0;
        }
    }

    // Phase 18 D6: a collapse makes one sound however many pieces fell at once: the collapsed pieces of a frame are gathered and
    // the nearest one sounds.
    public sealed class CollapseCollector
    {
        private bool _has;
        private Vector3 _nearest;
        private float _nearestSq;

        // 기능: 무너진 조각 하나를 더한다.
        // 입력: position - 조각 중심, listener - 듣는 위치.
        // 출력: 반환값 없음. 가장 가까운 것만 남는다.
        public void Add(Vector3 position, Vector3 listener)
        {
            float sq = Vector3.DistanceSquared(position, listener);
            if (_has && sq >= _nearestSq) return;
            _has = true;
            _nearest = position;
            _nearestSq = sq;
        }

        // 기능: 모은 붕괴 중 가장 가까운 위치를 꺼내고 비운다.
        // 입력: position - 결과.
        // 출력: 모은 것이 있었으면 true와 위치.
        public bool TryTake(out Vector3 position)
        {
            position = _nearest;
            bool had = _has;
            _has = false;
            return had;
        }
    }

    // Phase 18 D9: the outside-the-zone damage tick (the server sends no DamageTaken for zone damage): while we are outside, a
    // tick every IntervalSeconds, the first one IntervalSeconds after leaving the zone.
    public sealed class ZoneDamageTicker
    {
        public const float IntervalSeconds = 1f;

        private bool _outside;
        private float _next;

        // 기능: 프레임마다 밖에 있는지 받아 틱 소리를 낼지 정한다.
        // 입력: outside - 살아서 경기 중 자기장 밖인지, now - 현재 시각.
        // 출력: 이번 프레임에 틱이면 true.
        public bool Update(bool outside, float now)
        {
            if (!outside)
            {
                _outside = false;
                return false;
            }
            if (!_outside)
            {
                _outside = true;
                _next = now + IntervalSeconds;
                return false;
            }
            if (now < _next) return false;
            _next = now + IntervalSeconds;
            return true;
        }
    }

    // Phase 18 D9: our reload start = WeaponState.Reloading going false -> true (the mixer's 0.5 s interval absorbs a reconcile
    // flicker).
    public sealed class RisingEdge
    {
        private bool _last;

        // 기능: 값을 받아 false -> true로 바뀌었는지 본다.
        // 입력: value - 이번 프레임 값.
        // 출력: 바뀌었으면 true.
        public bool Update(bool value)
        {
            bool rose = value && !_last;
            _last = value;
            return rose;
        }

        // 기능: 이전 값을 false로 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset() => _last = false;
    }
}
