using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 19 D6, D11, D15: which vehicle the "[E] 탑승" hint names, and the vehicle health the HUD bar and the smoke compare
    // against. Display only: the server decides (its maxHealth is vehicles.json's; this copy is the default and the server test
    // compares the two, and runs FindEnterTarget against the server's enter rule, like SquadPrompt and DoorRule). Pure, no
    // allocation, no UnityEngine (the server test project links this file).
    public static class VehiclePrompt
    {
        public const int MaxHealth = 400;   // vehicles.json maxHealth

        // 기능: E가 탈 차량을 서버 규칙과 같게 고른다: Active이고 빈 좌석이 있으며 발에서 차체까지 EnterRange 이하인 차량 중 가장 가까운 것,
        //   거리가 같으면 Id가 낮은 것. 모드(Ground·Crouch)·생존·소생 진행·이미 탄 상태는 호출자가 본다.
        // 입력: feet - 내 발, records - 받은 차량 기록(선로 값 그대로), count - 앞에서부터 쓸 개수(records 길이로 자른다).
        // 출력: 고른 기록의 번호(0..count-1), 없으면 -1.
        public static int FindEnterTarget(Vector3 feet, ReadOnlySpan<VehicleRecord> records, int count)
        {
            int n = Math.Min(Math.Max(count, 0), records.Length);
            int best = -1;
            float bestDistance = 0f;
            for (int i = 0; i < n; i++)
            {
                ref readonly VehicleRecord r = ref records[i];
                if (r.State != VehicleState.Active) continue;
                if (r.Driver != 0 && r.Passenger != 0) continue;
                float d = VehicleSimulation.DistanceToBody(feet, r.Position, r.Heading);
                if (!(d <= VehicleSettings.EnterRange)) continue;
                if (best >= 0 && (d > bestDistance || (d == bestDistance && r.Id > records[best].Id))) continue;
                best = i;
                bestDistance = d;
            }
            return best;
        }
    }
}
