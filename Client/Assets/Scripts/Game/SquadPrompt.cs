using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 14 D8, D10, D14: which revive or reboot the "[E] 길게 눌러 ..." hint names. Display only: the server decides
    // (its ranges are squad.json's; these copies are the defaults and must be kept equal to them, like
    // CombatHud.RespawnSeconds). The server also checks the line of sight for a revive; the hint does not. Pure, no
    // allocation (EditMode tests run it outside Unity).
    public static class SquadPrompt
    {
        public const float ReviveRange = 2f;     // squad.json reviveRange
        public const float RebootRange = 3f;     // squad.json rebootRange, horizontal from the feet
        public const float RebootHeight = 2f;    // and at most this far up or down
        // squad.json downedHealth and bleedOutSeconds: the bleed-out seconds shown from the downed health.
        public const float DownedHealth = 100f;
        public const float BleedOutSeconds = 30f;

        // 기능: 소생할 수 있는 가장 가까운 기절 팀원을 고른다.
        // 입력: feet - 내 발, downedFeet - 기절 팀원들의 발(그려지는 위치), count - 앞에서부터 쓸 개수.
        // 출력: ReviveRange 안에서 가장 가까운 칸의 번호, 없으면 -1.
        public static int NearestDowned(Vector3 feet, Vector3[] downedFeet, int count)
        {
            int best = -1;
            float bestSq = ReviveRange * ReviveRange;
            for (int i = 0; i < count; i++)
            {
                float sq = Vector3.DistanceSquared(feet, downedFeet[i]);
                if (sq > bestSq || (best >= 0 && sq == bestSq)) continue;
                best = i;
                bestSq = sq;
            }
            return best;
        }

        // 기능: 재투입할 수 있는 거리 안의 가장 가까운 스테이션을 고른다(대기 여부는 보지 않는다).
        // 입력: feet - 내 발.
        // 출력: RebootStations.All 번호, 범위 안에 없으면 -1.
        public static int NearestStation(Vector3 feet)
        {
            int best = -1;
            float bestSq = RebootRange * RebootRange;
            var stations = RebootStations.All;
            for (int i = 0; i < stations.Length; i++)
            {
                Vector3 d = stations[i] - feet;
                if (d.Y > RebootHeight || d.Y < -RebootHeight) continue;
                float sq = d.X * d.X + d.Z * d.Z;
                if (sq > bestSq || (best >= 0 && sq == bestSq)) continue;
                best = i;
                bestSq = sq;
            }
            return best;
        }

        // 기능: 스테이션이 지금 대기 중인지 서버 Tick 추정으로 판단한다.
        // 입력: state - 최신 RebootStations 상태, index - 스테이션 번호, nowTick - 추정 서버 Tick.
        // 출력: 대기 비트가 켜져 있고 끝 Tick이 아직 오지 않았으면 true.
        public static bool IsCoolingDown(in RebootStationsState state, int index, double nowTick) =>
            state.IsCoolingDown(index) && nowTick < state.GetEndTick(index);

        // 기능: 기절 체력으로 출혈 탈락까지 남은 초를 추정한다(서버가 매 Tick 같은 양씩 줄인다, D5).
        // 입력: health - Snapshot Self의 체력(기절 중이면 기절 체력).
        // 출력: 올림한 남은 초(0 이상).
        public static int BleedSecondsLeft(int health)
        {
            if (health <= 0) return 0;
            return (int)System.Math.Ceiling(health * BleedOutSeconds / DownedHealth - 1e-4);
        }
    }
}
