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

        // Index into the list, or -1.
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
