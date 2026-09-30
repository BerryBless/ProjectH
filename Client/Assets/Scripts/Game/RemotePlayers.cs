using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Views of other players. An entry is added on PlayerSpawned and removed on PlayerDespawned or
    // Clear() (disconnect / destroy), so the dictionary cannot outlive the match.
    public sealed class RemotePlayers
    {
        private sealed class Entry
        {
            public Transform View;
            public RemotePlayerInterpolator Interpolator;
        }

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        public int Count => _entries.Count;

        public void Spawn(in PlayerSpawned spawned, uint tick)
        {
            if (_entries.ContainsKey(spawned.EntityId)) return;
            var entry = new Entry
            {
                View = PlayerViewFactory.Create($"Player {spawned.EntityId}", false),
                Interpolator = new RemotePlayerInterpolator(),
            };
            // A non-finite spawn position is rejected by the interpolator; the view then stays
            // at the origin until the first finite snapshot arrives.
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out float startYaw))
                entry.View.SetPositionAndRotation(start + Vector3.up, Quaternion.Euler(0f, startYaw, 0f));
            _entries.Add(spawned.EntityId, entry);
        }

        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) Object.Destroy(entry.View.gameObject);
        }

        public void Push(uint tick, in SnapshotEntity entity)
        {
            if (_entries.TryGetValue(entity.EntityId, out Entry entry))
                entry.Interpolator.Push(tick, entity.Position.ToUnity(), entity.Yaw);
        }

        public void Render(double renderTick)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.Interpolator.TrySample(renderTick, out Vector3 position, out float yaw))
                    pair.Value.View.SetPositionAndRotation(position + Vector3.up, Quaternion.Euler(0f, yaw, 0f));
            }
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
