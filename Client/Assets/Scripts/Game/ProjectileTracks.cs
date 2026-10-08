using System.Numerics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // Phase 17 D6, D7: the grenades and rockets the server told us about, in a fixed table of Capacity slots (the server's own
    // limit). Each slot keeps the newest event (ProjectileSpawned or ProjectileState): its position, velocity and tick, so the
    // flight between events is extrapolated with the kind's gravity (Extrapolate, the server's constant-acceleration step).
    // Presentation only: the server decides every bounce, rest and explosion.
    // Removal: ProjectileExploded, Clear (a disconnect, a join or resume, a match state change to Waiting, Starting or
    // Finished: ClearsOnMatchState), and Expire as a backstop for one the server dropped without a word (a match start or a
    // round reset). A full table replaces the slot with the oldest event, so a missed clear never blocks new projectiles.
    // Pure (System.Numerics, no UnityEngine) and allocation free.
    public sealed class ProjectileTracks
    {
        public const int Capacity = 32;

        private struct Track
        {
            public bool Active;
            public ushort Id;
            public ProjectileKind Kind;
            public ushort OwnerId;
            public Vector3 Position;   // at EventTick
            public Vector3 Velocity;   // at EventTick; zero = resting
            public uint EventTick;     // the newest event's tick (Spawned StartTick or State Tick)
            public uint SpawnTick;     // the Spawned's StartTick (a resent one carries its last event tick)
            public float Gravity;      // m/s^2 downwards
            public uint LifetimeTicks; // fuse or lifetime from the catalog
        }

        private readonly Track[] _tracks = new Track[Capacity];

        public int Count { get; private set; }

        // 기능: 칸이 쓰이고 있는지 알려 준다.
        // 입력: slot - 칸(0..Capacity-1).
        // 출력: 투사체가 있으면 true.
        public bool IsActive(int slot) => _tracks[slot].Active;

        // 기능: 칸의 서버 투사체 id를 읽는다.
        // 입력: slot - 칸.
        // 출력: id(빈 칸이면 0).
        public ushort IdAt(int slot) => _tracks[slot].Id;

        // 기능: 칸의 투사체 종류를 읽는다.
        // 입력: slot - 칸.
        // 출력: Grenade 또는 Rocket(빈 칸이면 None).
        public ProjectileKind KindAt(int slot) => _tracks[slot].Kind;

        // 기능: 칸의 투사체를 쏜 사람을 읽는다.
        // 입력: slot - 칸.
        // 출력: 주인 Entity id(나갔으면 0).
        public ushort OwnerAt(int slot) => _tracks[slot].OwnerId;

        // 기능: 생성 사건을 넣는다. 같은 id가 있으면 덮어쓴다(Join·Resume 재전송). 표가 가득 차면 가장 오래된 사건의 칸을 쓴다.
        // 입력: spawned - 받은 생성 사건, gravity - 그 종류의 중력(m/s², 아래로), lifetimeTicks - 그 종류의 퓨즈·수명 Tick.
        // 출력: 반환값 없음.
        public void Spawn(in ProjectileSpawned spawned, float gravity, uint lifetimeTicks)
        {
            int slot = Find(spawned.Id);
            if (slot < 0) slot = FreeOrOldest();
            if (!_tracks[slot].Active) Count++;
            _tracks[slot] = new Track
            {
                Active = true,
                Id = spawned.Id,
                Kind = spawned.Kind,
                OwnerId = spawned.OwnerId,
                Position = spawned.Position,
                Velocity = spawned.Velocity,
                EventTick = spawned.StartTick,
                SpawnTick = spawned.StartTick,
                Gravity = gravity > 0f && float.IsFinite(gravity) ? gravity : 0f,
                LifetimeTicks = lifetimeTicks,
            };
        }

        // 기능: 튕김·멈춤 사건으로 위치·속도·기준 Tick을 고친다.
        // 입력: state - 받은 상태 사건.
        // 출력: 아는 id면 true, 모르는 id(이미 지웠거나 생성을 못 받음)면 false(아무것도 바꾸지 않는다).
        public bool ApplyState(in ProjectileState state)
        {
            int slot = Find(state.Id);
            if (slot < 0) return false;
            _tracks[slot].Position = state.Position;
            _tracks[slot].Velocity = state.Velocity;
            _tracks[slot].EventTick = state.Tick;
            return true;
        }

        // 기능: 투사체 하나를 지운다(폭발).
        // 입력: id - 서버 투사체 id.
        // 출력: 있었으면 true, 없었으면 false(폭발 효과는 호출자가 그래도 보인다).
        public bool Remove(ushort id)
        {
            int slot = Find(id);
            if (slot < 0) return false;
            _tracks[slot] = default;
            Count--;
            return true;
        }

        // 기능: 모든 투사체를 지운다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) _tracks[i] = default;
            Count = 0;
        }

        // 기능: 퓨즈·수명이 지나고도 소식이 없는 투사체를 지운다(서버가 말없이 지운 경기 시작·라운드 리셋의 보조).
        // 입력: tick - 지금 서버 Tick 추정, graceTicks - 수명 뒤 더 기다릴 Tick.
        // 출력: 지운 개수.
        public int Expire(double tick, uint graceTicks)
        {
            int removed = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_tracks[i].Active) continue;
                double limit = (double)_tracks[i].SpawnTick + _tracks[i].LifetimeTicks + graceTicks;
                if (tick <= limit) continue;
                _tracks[i] = default;
                Count--;
                removed++;
            }
            return removed;
        }

        // 기능: 한 칸의 투사체를 tick 시점으로 외삽한다.
        // 입력: slot - 칸, tick - 그릴 서버 Tick, simHz - 서버 SimHz(0 이하면 사건 위치 그대로).
        // 출력: position·velocity - 그 시점의 위치와 속도. 사건보다 이른 tick은 사건 시점으로 본다.
        public void Sample(int slot, double tick, int simHz, out Vector3 position, out Vector3 velocity)
        {
            Track t = _tracks[slot];
            float dt = simHz > 0 ? (float)((tick - t.EventTick) / simHz) : 0f;
            Extrapolate(t.Position, t.Velocity, t.Gravity, dt, out position, out velocity);
        }

        // 기능: 등가속 탄도를 계산한다(서버 적분과 같은 식): p + v·dt + ½·(0, −g, 0)·dt², v + (0, −g, 0)·dt. 속도 0이면 멈춰 있다(중력 없음).
        // 입력: position·velocity - 사건 시점 상태, gravity - 아래로 당기는 m/s², dt - 사건 뒤 초(음수·NaN이면 0).
        // 출력: position·velocity 결과 - dt 뒤의 위치와 속도.
        public static void Extrapolate(Vector3 position, Vector3 velocity, float gravity, float dt, out Vector3 resultPosition, out Vector3 resultVelocity)
        {
            if (velocity == Vector3.Zero || !(dt > 0f))
            {
                resultPosition = position;
                resultVelocity = velocity;
                return;
            }
            var acceleration = new Vector3(0f, -gravity, 0f);
            resultPosition = position + velocity * dt + acceleration * (0.5f * dt * dt);
            resultVelocity = velocity + acceleration * dt;
        }

        // 기능: 받은 MatchState로 투사체를 비울지 정한다. 이미 경기 상태가 있었고, 상태가 바뀌었고, 새 상태가 Waiting·Starting·Finished일
        //   때만 비운다. 입장 뒤 첫 MatchState는 바뀜으로 보지 않는다(입장 때 이미 비웠고, 늦게 온 MatchState가 막 다시 받은 투사체를 지우면
        //   안 된다: Phase 16과 같은 문제).
        // 입력: hadMatch - 이전 MatchState가 있었는지, previous - 이전 상태, next - 받은 상태.
        // 출력: 비워야 하면 true.
        public static bool ClearsOnMatchState(bool hadMatch, MatchFlowState previous, MatchFlowState next)
        {
            if (!hadMatch || previous == next) return false;
            return next == MatchFlowState.WaitingForPlayers || next == MatchFlowState.Starting || next == MatchFlowState.Finished;
        }

        // 기능: id의 칸을 찾는다.
        // 입력: id - 서버 투사체 id.
        // 출력: 칸 번호, 없으면 -1.
        private int Find(ushort id)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (_tracks[i].Active && _tracks[i].Id == id) return i;
            }
            return -1;
        }

        // 기능: 빈 칸을, 없으면 가장 오래된 사건의 칸을 고른다.
        // 입력: 없음.
        // 출력: 칸 번호.
        private int FreeOrOldest()
        {
            int oldest = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_tracks[i].Active) return i;
                if (_tracks[i].EventTick < _tracks[oldest].EventTick) oldest = i;
            }
            return oldest;
        }
    }
}
