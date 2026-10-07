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
        // Phase 14 D12: the living teammates among _alive (watched first).
        private readonly ushort[] _team = new ushort[SquadConstants.MaxTeamSize];
        private int _count;
        private int _teamCount;
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

        // 기능: 관전을 끝낸다(부활·라운드 시작, 끊김).
        // 입력: 없음.
        // 출력: 반환값 없음. 대상·처치자 선호·살아 있는 목록(Phase 14: 팀원 목록 포함)이 비워진다.
        public void End()
        {
            Active = false;
            Target = 0;
            _preferred = 0;
            _count = 0;
            _teamCount = 0;
        }

        // 기능: 관전 중이면 이번 프레임의 대상을 정한다(카메라가 따라가기 전, 매 프레임). Phase 14 D12: 살아 있는 팀원이 먼저이고,
        //   팀원이 없으면 처치자 → 다음 사람(Follow는 대상을 찾으면 처치자 선호를 지운다, SpectatorTargetsTests).
        // 입력: players - 원격 플레이어(Snapshot 생존 비트), squad - 우리 팀(경기 안의 구성원).
        // 출력: 반환값 없음. Target이 바뀔 수 있다. 할당 없음.
        public void Update(RemotePlayers players, SquadState squad)
        {
            if (!Active) return;
            _count = players.CollectAlive(_alive);
            _teamCount = 0;
            for (int i = 0; i < _count && _teamCount < _team.Length; i++)
            {
                if (squad.IsInPlay(_alive[i])) _team[_teamCount++] = _alive[i];
            }
            Target = SpectatorTargets.FollowSquad(_alive, _count, _team, _teamCount, Target, ref _preferred);
        }

        // 기능: 관전 중 왼쪽 클릭으로 다음 대상으로 넘긴다(D12: 그 클릭은 쏘지 않는다). Phase 14: 살아 있는 팀원이 있으면 팀원 안에서만 돈다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Cycle()
        {
            if (!Active) return;
            Target = _teamCount > 0 ? SpectatorTargets.Next(_team, _teamCount, Target) : SpectatorTargets.Next(_alive, _count, Target);
        }

        public bool TryGetFeet(RemotePlayers players, double renderTick, out Vector3 feet)
        {
            feet = default;
            return Active && Target != 0 && players.TryGetFeet(Target, renderTick, out feet);
        }
    }
}
