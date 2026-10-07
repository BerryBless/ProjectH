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

        public int Count => _entries.Count;

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

        // 기능: 한 원격 플레이어의 팀원 표시를 정한다(Phase 14 D14).
        // 입력: entityId - 플레이어 id, teammate - 우리 팀인지.
        // 출력: 반환값 없음. 모르는 id면 아무것도 하지 않는다.
        public void SetTeammate(ushort entityId, bool teammate)
        {
            if (_entries.TryGetValue(entityId, out Entry entry)) entry.View.SetTeammate(teammate);
        }

        // 기능: 모든 원격 플레이어의 팀원 표시를 팀 상태에 맞춘다(TeamState를 받거나 팀을 비울 때).
        // 입력: squad - 우리 팀.
        // 출력: 반환값 없음. 할당 없음(구조체 열거자).
        public void ApplyTeam(SquadState squad)
        {
            foreach (var pair in _entries) pair.Value.View.SetTeammate(squad.Contains(pair.Key));
        }

        // 기능: 원격 플레이어가 renderTick에 그려지는 발·모드와 생존 여부를 돌려준다(팀원 표지, 소생 안내).
        // 입력: entityId - 플레이어 id, renderTick - 렌더 Tick.
        // 출력: 샘플이 있으면 true와 발·모드·생존(Snapshot 비트), 없으면 false.
        public bool TryGetPose(ushort entityId, double renderTick, out Vector3 feet, out MovementMode mode, out bool alive)
        {
            feet = default;
            mode = MovementMode.Ground;
            alive = false;
            if (!_entries.TryGetValue(entityId, out Entry entry) ||
                !entry.Interpolator.TrySample(renderTick, out feet, out _, out mode, out _, out _))
                return false;
            alive = entry.Alive;
            return true;
        }

        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) entry.View.Destroy();
        }

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

        // Phase 5: PlayerRespawned for a remote player (match start and round reset are alive -> alive, so the
        // snapshot flag does not flip): drop its pre-teleport samples so the view snaps instead of sliding.
        // Alive and the view stay with the snapshot flags. Unknown id: nothing.
        public void Teleport(ushort entityId, Vector3 to)
        {
            if (_entries.TryGetValue(entityId, out Entry entry)) entry.Interpolator.Teleport(to);
        }

        // Where a remote player's feet are drawn at renderTick (the same interpolation as its view).
        public bool TryGetFeet(ushort entityId, double renderTick, out Vector3 feet)
        {
            feet = default;
            return _entries.TryGetValue(entityId, out Entry entry) && entry.Interpolator.TrySample(renderTick, out feet, out _);
        }

        public void Clear()
        {
            foreach (var pair in _entries) pair.Value.View.Destroy();
            _entries.Clear();
        }
    }
}
