namespace ProjectH.Client.Game
{
    // D5: who a dead player watches. Pure (EditMode tests): the camera passes the living remote players' entity
    // ids (any order, from RemotePlayers.CollectAlive into a reused buffer) and gets an id back, 0 = nobody.
    // Order is ascending entity id, wrapping around, so every client cycles the same way.
    public static class SpectatorTargets
    {
        // The next living id after current (current itself need not be alive); wraps to the smallest. 0 when
        // nobody is alive. With one living player that player is returned, even when it is current.
        public static ushort Next(ushort[] alive, int count, ushort current)
        {
            ushort smallest = 0;
            ushort after = 0;
            for (int i = 0; i < count; i++)
            {
                ushort id = alive[i];
                if (id == 0) continue;
                if (smallest == 0 || id < smallest) smallest = id;
                if (id > current && (after == 0 || id < after)) after = id;
            }
            return after != 0 ? after : smallest;
        }

        // The target to follow this frame: the current one while it lives; else the preferred one (the killer,
        // right after the death) if it lives; else the next one after the current.
        public static ushort Resolve(ushort[] alive, int count, ushort current, ushort preferred)
        {
            if (current != 0 && Contains(alive, count, current)) return current;
            if (preferred != 0 && Contains(alive, count, preferred)) return preferred;
            return Next(alive, count, current);
        }

        // Resolve for the camera's per-frame update. The preference (the killer) only decides the first target:
        // it is cleared once a target is found, so a later target that dies is replaced by the next player and
        // the view does not jump back to the killer. Kept while nobody is found (the killer may not be listed yet).
        public static ushort Follow(ushort[] alive, int count, ushort current, ref ushort preferred)
        {
            ushort target = Resolve(alive, count, current, preferred);
            if (target != 0) preferred = 0;
            return target;
        }

        public static bool Contains(ushort[] alive, int count, ushort id)
        {
            for (int i = 0; i < count; i++)
            {
                if (alive[i] == id) return true;
            }
            return false;
        }
    }
}
