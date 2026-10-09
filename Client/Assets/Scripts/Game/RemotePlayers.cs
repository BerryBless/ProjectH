using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 18 D5: one remote player as drawn this frame (RemotePlayers.CollectPoses), for the footsteps.
    public struct RemotePose
    {
        public ushort EntityId;
        public Vector3 Feet;
        public MovementMode Mode;
        public bool Sprinting;
        public bool Alive;
        public bool Seated;   // Phase 19 D15: in a vehicle seat at the render tick (no footsteps)
    }

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
            // Phase 19 D15: seated at this frame's render tick (set by Render).
            public bool Seated;
        }

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        public int Count => _entries.Count;
        // Review fix D3 (SEC-24): spawns ignored because the table already held ProtocolConstants.MaxSnapshotEntities players
        // (an honest server never sends more than MaxPlayers - 1). F1 line.
        public int SpawnRejects { get; private set; }

        // 기능: 그 id의 원격 플레이어가 있는지 본다.
        // 입력: entityId - 플레이어 id.
        // 출력: 있으면 true.
        public bool Contains(ushort entityId) => _entries.ContainsKey(entityId);

        // 기능: 원격 플레이어 하나를 만든다(PlayerSpawned). 이미 있으면 아무것도 하지 않는다. 리뷰 수정 D3: 이미
        //   ProtocolConstants.MaxSnapshotEntities명이면 뷰를 만들지 않고 SpawnRejects를 센다.
        // 입력: spawned - 등장 정보, tick - 첫 보간 표본의 Tick.
        // 출력: 반환값 없음. 받아들였으면 뷰와 보간기가 생긴다.
        public void Spawn(in PlayerSpawned spawned, uint tick)
        {
            if (_entries.ContainsKey(spawned.EntityId)) return;
            if (_entries.Count >= ProtocolConstants.MaxSnapshotEntities)
            {
                SpawnRejects++;
                return;
            }
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

        // 기능: 모든 원격 플레이어가 renderTick에 그려지는 발·모드·질주·생존(Phase 19: 이번 프레임 Render의 탑승 여부)을 버퍼에 쓴다(Phase 18 D5: 발소리).
        // 입력: renderTick - 렌더 Tick, buffer - 결과를 쓸 고정 배열(가득 차면 나머지는 쓰지 않는다).
        // 출력: 쓴 수. 샘플이 없는 플레이어는 빠진다. 할당 없음(구조체 열거자).
        public int CollectPoses(double renderTick, RemotePose[] buffer)
        {
            int count = 0;
            foreach (var pair in _entries)
            {
                if (count == buffer.Length) break;
                Entry entry = pair.Value;
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out _, out MovementMode mode, out bool sprinting, out _)) continue;
                buffer[count++] = new RemotePose { EntityId = pair.Key, Feet = feet, Mode = mode, Sprinting = sprinting, Alive = entry.Alive, Seated = entry.Seated };
            }
            return count;
        }

        // 기능: 원격 플레이어가 Snapshot 기준으로 살아 있는지 본다(Phase 18 D9: 재투입 판단).
        // 입력: entityId - 플레이어 id.
        // 출력: 알고 있고 살아 있으면 true.
        public bool IsAlive(ushort entityId) => _entries.TryGetValue(entityId, out Entry entry) && entry.Alive;

        // 기능: 조준 Raycast가 맞힌 Collider가 어느 원격 플레이어의 피격 상자인지 찾는다(Phase 15 D6: Enemy Ping).
        // 입력: hit - 맞은 Collider(null 가능).
        // 출력: 원격 플레이어의 상자(뷰 뿌리에 붙어 있다)면 true와 Entity id, 아니면 false. 누를 때만 부르고 할당 없음(구조체 열거자).
        public bool TryFindEntity(Collider hit, out ushort entityId)
        {
            entityId = 0;
            if (hit == null || hit.gameObject.layer != PlayerViewFactory.RemoteHitLayer) return false;
            Transform root = hit.transform;
            foreach (var pair in _entries)
            {
                if (pair.Value.View.Root != root) continue;
                entityId = pair.Key;
                return true;
            }
            return false;
        }

        // 기능: 원격 플레이어 하나를 없앤다(PlayerDespawned). 모르는 id면 아무것도 하지 않는다.
        // 입력: entityId - 플레이어 id.
        // 출력: 반환값 없음. 항목이 지워지고 뷰가 파괴된다.
        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) entry.View.Destroy();
        }

        // 기능: Snapshot 항목 하나를 그 플레이어의 보간기에 넣고 생존 비트를 따라간다. 죽음→생존이면 재투입이므로 옛 표본을 비운다. 모르는 id면 아무것도 하지 않는다.
        // 입력: tick - Snapshot의 서버 Tick, entity - 그 플레이어의 Snapshot 항목.
        // 출력: 반환값 없음. 보간기에 표본이 들어가고, 생존이 바뀌면 Alive와 뷰의 생존 표시가 바뀐다.
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

        // 기능: 모든 원격 플레이어를 renderTick에 놓는다. Phase 19 D15: 이번 프레임 차량 표본(VehicleStore.Render, 내가 운전하는 차량은 예측
        //   위치)에서 앉아 있는 사람은 그 차량의 좌석 위치·방향에 앉은 자세로 놓는다(피격 상자 없음).
        // 입력: renderTick - 렌더 Tick, vehicles - 이번 프레임의 차량 표본(null이면 차량 없음).
        // 출력: 반환값 없음. 뷰와 각 항목의 Seated가 바뀐다. 할당 없음(구조체 열거자).
        public void Render(double renderTick, VehicleStore vehicles = null)
        {
            foreach (var pair in _entries)
            {
                Entry entry = pair.Value;
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw, out MovementMode mode, out bool sprinting, out _))
                {
                    entry.Seated = false;
                    continue;
                }
                VehicleRecord vehicle = default;
                int seat = -1;
                entry.Seated = entry.Alive && vehicles != null && vehicles.TrySeatAt(pair.Key, out vehicle, out seat);
                if (entry.Seated)
                {
                    feet = VehicleSimulation.SeatPosition(vehicle.Position, vehicle.Heading, seat).ToUnity();
                    yaw = vehicle.Heading;
                }
                // The hit box stays axis-aligned like the server's AABB (D7); only the capsule turns and leans.
                entry.View.Place(feet, yaw, mode, sprinting, entry.Seated);
            }
        }

        // 기능: 살아 있는(Snapshot 비트) 원격 플레이어의 Entity id를 버퍼에 쓴다(Phase 5 D5: 관전).
        // 입력: buffer - 결과를 쓸 고정 배열(가득 차면 나머지는 쓰지 않는다).
        // 출력: 쓴 수. 할당 없음(구조체 열거자).
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

        // 기능: 원격 플레이어의 PlayerRespawned(Phase 5)로 순간이동 전 표본을 버려 뷰가 미끄러지지 않고 튀게 한다. 생존 비트와 뷰는 Snapshot을 따르고, 모르는 id면 아무것도 하지 않는다.
        // 입력: entityId - 플레이어 id, to - 재투입 위치.
        // 출력: 반환값 없음. 그 플레이어 보간기의 옛 표본이 지워진다.
        // Phase 5: match start and round reset are alive -> alive, so the snapshot flag does not flip.
        public void Teleport(ushort entityId, Vector3 to)
        {
            if (_entries.TryGetValue(entityId, out Entry entry)) entry.Interpolator.Teleport(to);
        }

        // 기능: 원격 플레이어의 발이 renderTick에 그려지는 위치를 구한다(뷰와 같은 보간).
        // 입력: entityId - 플레이어 id, renderTick - 렌더 Tick, feet - 그려지는 발 위치.
        // 출력: 알고 있고 표본이 있으면 true와 발 위치, 아니면 false.
        public bool TryGetFeet(ushort entityId, double renderTick, out Vector3 feet)
        {
            feet = default;
            return _entries.TryGetValue(entityId, out Entry entry) && entry.Interpolator.TrySample(renderTick, out feet, out _);
        }

        // 기능: 모든 원격 플레이어를 없앤다(연결 끊김·파괴).
        // 입력: 없음.
        // 출력: 반환값 없음. 모든 뷰가 파괴되고 표가 빈다.
        public void Clear()
        {
            foreach (var pair in _entries) pair.Value.View.Destroy();
            _entries.Clear();
        }
    }
}
