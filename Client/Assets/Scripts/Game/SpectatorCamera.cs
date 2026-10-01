using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D5: what a dead player's camera follows during a match. On by Begin (our own PlayerDied while a match runs),
    // off by End (our respawn: the round start or reset). It first follows the killer, a left click moves to the
    // next living player (SpectatorTargets), and a target that dies is replaced by the next one. The target is
    // drawn where RemotePlayers interpolates it, so the server is not involved. The id buffer is allocated once.
    public sealed class SpectatorCamera
    {
        private readonly ushort[] _alive = new ushort[ProtocolConstants.MaxSnapshotEntities];
        private int _count;
        private ushort _preferred;

        public bool Active { get; private set; }
        // 0 = nobody to watch (everyone else is dead or gone): the camera stays on our own body.
        public ushort Target { get; private set; }

        public void Begin(ushort killerId)
        {
            Active = true;
            Target = 0;
            _preferred = killerId;   // 0 for the zone or a newcomer: the first living player instead
        }

        public void End()
        {
            Active = false;
            Target = 0;
            _preferred = 0;
            _count = 0;
        }

        // Once per frame while active, before the camera follows: the living players from the snapshot flags.
        // Follow clears _preferred once a target is found, so the killer only decides the first target
        // (tested in SpectatorTargetsTests).
        public void Update(RemotePlayers players)
        {
            if (!Active) return;
            _count = players.CollectAlive(_alive);
            Target = SpectatorTargets.Follow(_alive, _count, Target, ref _preferred);
        }

        // Left click while dead (D12: the click never fires then).
        public void Cycle()
        {
            if (Active) Target = SpectatorTargets.Next(_alive, _count, Target);
        }

        public bool TryGetFeet(RemotePlayers players, double renderTick, out Vector3 feet)
        {
            feet = default;
            return Active && Target != 0 && players.TryGetFeet(Target, renderTick, out feet);
        }
    }
}
