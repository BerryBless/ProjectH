using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Views of other players. An entry is added on PlayerSpawned and removed on PlayerDespawned or
    // Clear() (disconnect / destroy), so the dictionary cannot outlive the match.
    // Alive or dead comes from each snapshot's flag, not from the PlayerDied/PlayerRespawned events: a
    // client that joins while someone is dead gets no event for it, but every snapshot carries the flag.
    // Phase 12 D14: so do the movement mode and sprinting, which pick the pose. They are kept per interpolator sample and
    // read at the render tick, so the pose and hit box match the drawn position (and the server's rewound mode).
    public sealed class RemotePlayers
    {
        private sealed class Entry
        {
            public PlayerView View;
            public RemotePlayerInterpolator Interpolator;
            public bool Alive = true;
        }

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        // 기능: 표시 중인 원격 플레이어 수를 돌려준다.
        // 입력: 없음.
        // 출력: 등록된 Entry 수.
        public int Count => _entries.Count;

        // 기능: PlayerSpawned로 원격 플레이어 뷰와 보간기를 만들고 스폰 위치에 놓는다.
        // 입력: spawned - 서버 스폰 이벤트, tick - 첫 샘플로 기록할 서버 Tick(호출자는 ServerClock.LatestTick).
        // 출력: 반환값 없음. 새 Entry가 추가된다. 이미 있는 ID면 무시한다.
        public void Spawn(in PlayerSpawned spawned, uint tick)
        {
            if (_entries.ContainsKey(spawned.EntityId)) return;
            PlayerView view = PlayerViewFactory.Create($"Player {spawned.EntityId}", false);
            var entry = new Entry
            {
                View = view,
                Interpolator = new RemotePlayerInterpolator(),
            };
            // A non-finite spawn position is rejected by the interpolator; the view then stays
            // at the origin until the first finite snapshot arrives.
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out float yaw)) entry.View.Place(start, yaw, MovementMode.Ground, false);
            _entries.Add(spawned.EntityId, entry);
        }

        // 기능: 원격 플레이어의 Entry를 지우고 뷰를 파괴한다.
        // 입력: entityId - 대상 Entity ID.
        // 출력: 반환값 없음. 있으면 Entry 제거와 뷰 파괴, 없으면 변화 없음.
        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) entry.View.Destroy();
        }

        // 기능: Snapshot의 원격 플레이어 상태를 보간 기록에 넣고 생존 여부 변화를 반영한다.
        // 입력: tick - Snapshot의 서버 Tick, entity - Snapshot의 Entity 항목.
        // 출력: 반환값 없음. 샘플이 추가되고, 생존 여부가 바뀌면 뷰 색이 바뀌며 부활이면 이전 샘플이 지워진다. 모르는 ID는 무시한다.
        public void Push(uint tick, in SnapshotEntity entity)
        {
            if (!_entries.TryGetValue(entity.EntityId, out Entry entry)) return;

            bool alive = entity.IsAlive;
            if (alive != entry.Alive)
            {
                // A respawn is a teleport: drop the old samples so the view does not slide from the body to
                // the spawn point. Sequenced snapshots arrive in order, so no older "dead" sample follows.
                if (alive) entry.Interpolator.Clear();
                entry.Alive = alive;
                entry.View.SetAlive(alive);
            }
            entry.Interpolator.Push(tick, entity.Position.ToUnity(), entity.Yaw, entity.Mode, entity.IsSprinting, entity.IsExhausted);
        }

        // 기능: 매 프레임 모든 원격 플레이어 뷰를 렌더 Tick의 보간 위치와 자세로 옮긴다.
        // 입력: renderTick - 화면에 그릴 서버 Tick(소수).
        // 출력: 반환값 없음. 샘플이 있는 뷰의 Transform과 자세가 갱신된다.
        public void Render(double renderTick)
        {
            foreach (var pair in _entries)
            {
                Entry entry = pair.Value;
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw, out MovementMode mode, out bool sprinting, out _))
                    continue;
                // The hit box stays axis-aligned like the server's AABB (D7); only the capsule turns and leans.
                entry.View.Place(feet, yaw, mode, sprinting);
            }
        }

        // 기능: 살아 있는 원격 플레이어 ID를 버퍼에 채운다.
        // 입력: buffer - ID를 쓸 재사용 버퍼.
        // 출력: 버퍼에 쓴 ID 수(버퍼 길이를 넘지 않음).
        // Phase 5 D5 (spectating): the entity ids of the living remote players, written into buffer; returns how
        // many. Alive comes from the snapshot flags. No allocation (struct enumerator).
        public int CollectAlive(ushort[] buffer)
        {
            int count = 0;
            foreach (var pair in _entries)
            {
                if (count == buffer.Length) break;
                if (pair.Value.Alive) buffer[count++] = pair.Key;
            }
            return count;
        }

        // 기능: PlayerRespawned를 받은 원격 플레이어의 순간이동 전 샘플을 버린다.
        // 입력: entityId - 대상 Entity ID, to - 리스폰 위치.
        // 출력: 반환값 없음. 보간 기록이 정리된다. 모르는 ID는 무시한다.
        // Phase 5: PlayerRespawned for a remote player (match start and round reset are alive -> alive, so the
        // snapshot flag does not flip): drop its pre-teleport samples so the view snaps instead of sliding.
        // Alive and the view stay with the snapshot flags. Unknown id: nothing.
        public void Teleport(ushort entityId, Vector3 to)
        {
            if (_entries.TryGetValue(entityId, out Entry entry)) entry.Interpolator.Teleport(to);
        }

        // 기능: 원격 플레이어의 발이 렌더 Tick에 그려지는 위치를 구한다.
        // 입력: entityId - 대상 Entity ID, renderTick - 화면에 그릴 서버 Tick(소수).
        // 출력: 등록되어 있고 샘플이 있으면 true와 발 위치, 아니면 false.
        // Where a remote player's feet are drawn at renderTick (the same interpolation as its view).
        public bool TryGetFeet(ushort entityId, double renderTick, out Vector3 feet)
        {
            feet = default;
            return _entries.TryGetValue(entityId, out Entry entry) && entry.Interpolator.TrySample(renderTick, out feet, out _);
        }

        // 기능: 모든 원격 플레이어 뷰를 파괴하고 목록을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Entry가 모두 제거된다.
        public void Clear()
        {
            foreach (var pair in _entries) pair.Value.View.Destroy();
            _entries.Clear();
        }
    }
}
