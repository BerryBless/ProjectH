namespace ProjectH.Client.Game
{
    // D5: who a dead player watches. Pure (EditMode tests): the camera passes the living remote players' entity
    // ids (any order, from RemotePlayers.CollectAlive into a reused buffer) and gets an id back, 0 = nobody.
    // Order is ascending entity id, wrapping around, so every client cycles the same way.
    public static class SpectatorTargets
    {
        // 기능: 현재 대상 다음의 생존자 ID를 오름차순으로 찾는다(끝이면 가장 작은 ID로 돌아감).
        // 입력: alive - 생존자 ID 버퍼, count - 유효한 ID 수, current - 현재 대상 ID.
        // 출력: 다음 생존자 ID, 생존자가 없으면 0.
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

        // 기능: 이번 프레임에 따라갈 대상을 고른다.
        // 입력: alive - 생존자 ID 버퍼, count - 유효한 ID 수, current - 현재 대상 ID, preferred - 선호 대상(킬러, 0이면 없음).
        // 출력: 현재 대상이 살아 있으면 현재, 아니면 살아 있는 선호 대상, 아니면 Next 결과(없으면 0).
        // The target to follow this frame: the current one while it lives; else the preferred one (the killer,
        // right after the death) if it lives; else the next one after the current.
        public static ushort Resolve(ushort[] alive, int count, ushort current, ushort preferred)
        {
            if (current != 0 && Contains(alive, count, current)) return current;
            if (preferred != 0 && Contains(alive, count, preferred)) return preferred;
            return Next(alive, count, current);
        }

        // 기능: Resolve로 대상을 고르고 대상을 찾으면 선호 대상을 지운다.
        // 입력: alive - 생존자 ID 버퍼, count - 유효한 ID 수, current - 현재 대상 ID, preferred - 선호 대상(대상을 찾으면 0이 됨).
        // 출력: 따라갈 대상 ID, 없으면 0.
        // Resolve for the camera's per-frame update. The preference (the killer) only decides the first target:
        // it is cleared once a target is found, so a later target that dies is replaced by the next player and
        // the view does not jump back to the killer. Kept while nobody is found (the killer may not be listed yet).
        public static ushort Follow(ushort[] alive, int count, ushort current, ref ushort preferred)
        {
            ushort target = Resolve(alive, count, current, preferred);
            if (target != 0) preferred = 0;
            return target;
        }

        // 기능: 생존자 목록에 ID가 있는지 확인한다.
        // 입력: alive - 생존자 ID 버퍼, count - 유효한 ID 수, id - 찾을 ID.
        // 출력: 있으면 true, 없으면 false.
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
