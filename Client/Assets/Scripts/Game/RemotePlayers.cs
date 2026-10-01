using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Views of other players. An entry is added on PlayerSpawned and removed on PlayerDespawned or
    // Clear() (disconnect / destroy), so the dictionary cannot outlive the match.
    // Alive or dead comes from each snapshot's flag, not from the PlayerDied/PlayerRespawned events: a
    // client that joins while someone is dead gets no event for it, but every snapshot carries the flag.
    public sealed class RemotePlayers
    {
        private sealed class Entry
        {
            public Transform View;
            public Renderer Renderer;
            public Collider Collider;
            public RemotePlayerInterpolator Interpolator;
            public bool Alive = true;
        }

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        public int Count => _entries.Count;

        public void Spawn(in PlayerSpawned spawned, uint tick)
        {
            if (_entries.ContainsKey(spawned.EntityId)) return;
            Transform view = PlayerViewFactory.Create($"Player {spawned.EntityId}", false);
            var entry = new Entry
            {
                View = view,
                Renderer = view.GetComponent<Renderer>(),
                Collider = view.GetComponent<Collider>(),
                Interpolator = new RemotePlayerInterpolator(),
            };
            // A non-finite spawn position is rejected by the interpolator; the view then stays
            // at the origin until the first finite snapshot arrives.
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out _))
                entry.View.SetPositionAndRotation(start + Vector3.up, Quaternion.identity);   // alive: axis-aligned, see Render
            _entries.Add(spawned.EntityId, entry);
        }

        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) Object.Destroy(entry.View.gameObject);
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
                PlayerViewFactory.SetAlive(entry.Renderer, entry.Collider, false, alive);
            }
            entry.Interpolator.Push(tick, entity.Position.ToUnity(), entity.Yaw);
        }

        public void Render(double renderTick)
        {
            foreach (var pair in _entries)
            {
                Entry entry = pair.Value;
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw)) continue;
                // Alive: no yaw, so the box collider stays axis-aligned like the server's AABB (D7). The capsule
                // mesh is round about Y, so this looks the same. Dead: lies along the facing direction.
                PlayerViewFactory.Pose(feet, entry.Alive ? 0f : yaw, entry.Alive, out Vector3 position, out Quaternion rotation);
                entry.View.SetPositionAndRotation(position, rotation);
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
            foreach (var pair in _entries)
            {
                if (pair.Value.View != null) Object.Destroy(pair.Value.View.gameObject);
            }
            _entries.Clear();
        }
    }
}
