using System.Numerics;

namespace ProjectH.Client.Game
{
    // Copy of the server's pickup target rule (Server ItemRules + WorldItems.FindNearest, D8), used only to
    // show the "[E]" prompt on the item the server will pick. The server still decides. Keep the two in step:
    // within 2 m on the ground plane and 2 m up or down from the feet, nearest by 3D distance, ties to the
    // lower ItemId. Pure, no allocation.
    public static class PickupRule
    {
        public const float Range = 2f;
        public const float Height = 2f;

        // 기능: E가 주울 아이템을 서버 규칙(ItemRules + WorldItems.FindNearest, D8)과 같게 고른다: 발에서 수평 2 m·높이 ±2 m 안에서 3D 거리가
        //   가장 가까운 것, 같은 거리면 작은 ItemId. 재료(Material)는 닿으면 줍는 것이라 제외한다(Phase 13 D15).
        // 입력: items - 월드 아이템 목록, feet - 예측된 내 발 위치.
        // 출력: 목록 안의 색인, 범위 안에 없으면 -1.
        public static int FindNearest(WorldItemList items, Vector3 feet)
        {
            int best = -1;
            float bestDistance = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                // Phase 13 D15: resources are picked up on touch, never with E.
                if (items[i].Kind == ProjectH.Shared.Protocol.ItemKind.Material) continue;
                Vector3 d = items[i].Position - feet;
                float horizontalSq = d.X * d.X + d.Z * d.Z;
                if (horizontalSq > Range * Range || d.Y > Height || d.Y < -Height) continue;
                float distance = horizontalSq + d.Y * d.Y;
                if (best < 0 || distance < bestDistance ||
                    (distance == bestDistance && items[i].ItemId < items[best].ItemId))
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
