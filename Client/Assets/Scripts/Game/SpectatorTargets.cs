namespace ProjectH.Client.Game
{
    // D5: who a dead player watches. Pure (EditMode tests): the camera passes the living remote players' entity
    // ids (any order, from RemotePlayers.CollectAlive into a reused buffer) and gets an id back, 0 = nobody.
    // Order is ascending entity id, wrapping around, so every client cycles the same way.
    public static class SpectatorTargets
    {
        // 기능: current 다음으로 큰 살아 있는 id를 고른다(current가 살아 있을 필요는 없다). 더 큰 id가 없으면 가장 작은 id로 돈다.
        // 입력: alive - 살아 있는 Entity id 배열(0은 건너뛴다), count - 앞에서부터 쓸 개수, current - 지금 대상.
        // 출력: 다음 id. 아무도 없으면 0. 한 명뿐이면 current여도 그 사람.
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

        // 기능: 이번 프레임 따라갈 대상을 정한다. 지금 대상이 살아 있으면 유지, 아니면 선호 대상(죽은 직후의 처치자)이 살아 있으면 그 사람, 아니면 current 다음 사람.
        // 입력: alive·count - 살아 있는 Entity id, current - 지금 대상, preferred - 선호 대상(0이면 없음).
        // 출력: 따라갈 id, 아무도 없으면 0.
        public static ushort Resolve(ushort[] alive, int count, ushort current, ushort preferred)
        {
            if (current != 0 && Contains(alive, count, current)) return current;
            if (preferred != 0 && Contains(alive, count, preferred)) return preferred;
            return Next(alive, count, current);
        }

        // 기능: 카메라의 매 프레임용 Resolve. 선호 대상(처치자)은 첫 대상만 정하고, 대상을 찾으면 지워 뒤에 대상이 죽어도 처치자로 되돌아가지 않게 한다.
        // 입력: alive·count - 살아 있는 Entity id, current - 지금 대상, preferred - 선호 대상(대상을 찾으면 0이 된다, 못 찾으면 유지).
        // 출력: 따라갈 id, 아무도 없으면 0.
        // Kept while nobody is found (the killer may not be listed yet).
        public static ushort Follow(ushort[] alive, int count, ushort current, ref ushort preferred)
        {
            ushort target = Resolve(alive, count, current, preferred);
            if (target != 0) preferred = 0;
            return target;
        }

        // 기능: 분대 관전의 이번 프레임 대상을 고른다(Phase 14 D12). 살아 있는 팀원이 있으면 그중에서 고르고(지금 대상이 팀원이면 유지,
        //   아니면 다음 팀원), 이때 처치자 선호는 건드리지 않는다. 팀원이 모두 탈락했으면 기존 Follow(처치자 → 다음 사람)를 따른다.
        // 입력: alive·count - 살아 있는 원격 플레이어, teammates·teamCount - 그중 경기 안의 팀원, current - 지금 대상,
        //   preferred - 처치자(대상을 찾으면 Follow가 지운다).
        // 출력: 따라갈 id, 아무도 없으면 0.
        public static ushort FollowSquad(ushort[] alive, int count, ushort[] teammates, int teamCount, ushort current, ref ushort preferred)
        {
            if (teamCount > 0)
                return current != 0 && Contains(teammates, teamCount, current) ? current : Next(teammates, teamCount, current);
            return Follow(alive, count, current, ref preferred);
        }

        // 기능: id가 목록의 앞 count개 안에 있는지 본다.
        // 입력: alive - Entity id 배열, count - 앞에서부터 쓸 개수, id - 찾을 id.
        // 출력: 있으면 true.
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
