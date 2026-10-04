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

        // 기능: 사망 후 관전을 시작하고 처음 따라갈 대상으로 킬러를 지정한다.
        // 입력: killerId - 킬러 Entity ID(0이면 첫 생존자).
        // 출력: 반환값 없음. Active가 true, Target이 0이 되고 선호 대상이 기록된다.
        public void Begin(ushort killerId)
        {
            Active = true;
            Target = 0;
            _preferred = killerId;   // 0 for the zone or a newcomer: the first living player instead
        }

        // 기능: 관전을 끝낸다.
        // 입력: 없음.
        // 출력: 반환값 없음. Active가 false가 되고 Target·선호 대상·생존자 목록이 비워진다.
        public void End()
        {
            Active = false;
            Target = 0;
            _preferred = 0;
            _count = 0;
        }

        // 기능: 관전 중 매 프레임 생존자 목록을 다시 모으고 따라갈 대상을 정한다.
        // 입력: players - 원격 플레이어 목록.
        // 출력: 반환값 없음. 생존자 버퍼와 Target이 갱신되고, 대상을 찾으면 선호 대상이 지워진다. 관전 중이 아니면 변화 없음.
        // Once per frame while active, before the camera follows: the living players from the snapshot flags.
        // Follow clears _preferred once a target is found, so the killer only decides the first target
        // (tested in SpectatorTargetsTests).
        public void Update(RemotePlayers players)
        {
            if (!Active) return;
            _count = players.CollectAlive(_alive);
            Target = SpectatorTargets.Follow(_alive, _count, Target, ref _preferred);
        }

        // 기능: 관전 대상을 다음 생존자로 바꾼다.
        // 입력: 없음.
        // 출력: 반환값 없음. 관전 중이면 Target이 ID 오름차순 다음 생존자로 바뀐다.
        // Left click while dead (D12: the click never fires then).
        public void Cycle()
        {
            if (Active) Target = SpectatorTargets.Next(_alive, _count, Target);
        }

        // 기능: 관전 대상의 발이 렌더 Tick에 그려지는 위치를 구한다.
        // 입력: players - 원격 플레이어 목록, renderTick - 화면에 그릴 서버 Tick(소수).
        // 출력: 관전 중이고 대상의 위치를 구하면 true와 발 위치, 아니면 false.
        public bool TryGetFeet(RemotePlayers players, double renderTick, out Vector3 feet)
        {
            feet = default;
            return Active && Target != 0 && players.TryGetFeet(Target, renderTick, out feet);
        }
    }
}
