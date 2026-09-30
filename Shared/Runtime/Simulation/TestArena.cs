using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Code-constant test map (D5), used by both the server and client prediction so they collide
    // with the same boxes. Replaced by a real map loader in the map phase.
    // Layout rules checked by TestArenaTests: at most 30 boxes, nothing within ClearRadius of the
    // origin (players spawn on a 5 m ring, Match.SpawnPosition), no gap between two boxes that is
    // narrower than the character (D4: depenetration in narrow gaps can pick an odd direction), and
    // no two boxes touching or overlapping side by side.
    public static class TestArena
    {
        public const float ClearRadius = 7f;

        // Static readonly array: the Boxes property wraps it without allocating.
        // Rule: no two boxes touch or overlap side by side (stacking is fine), because depenetrating
        // one box at a time can trap a character at a shared or overlapping vertical face.
        private static readonly Box[] s_boxes =
        {
            // Outer walls: 40 x 40 m inside, 3 m high, 1 m thick. The east/west walls stop 0.5 m short
            // of the north/south walls, so each corner is a 0.5 m gap (narrower than the character, so the arena stays sealed) instead of a touching seam.
            Box.FromCenterSize(new Vector3(0f, 1.5f, 20f), new Vector3(42f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(0f, 1.5f, -20f), new Vector3(42f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(20f, 1.5f, 0f), new Vector3(1f, 3f, 38f)),
            Box.FromCenterSize(new Vector3(-20f, 1.5f, 0f), new Vector3(1f, 3f, 38f)),

            // Wall pieces, 3 m high, for camera collision checks.
            Box.FromCenterSize(new Vector3(12f, 1.5f, 6f), new Vector3(0.5f, 3f, 6f)),
            Box.FromCenterSize(new Vector3(-12f, 1.5f, -6f), new Vector3(0.5f, 3f, 6f)),
            Box.FromCenterSize(new Vector3(6f, 1.5f, -12f), new Vector3(6f, 3f, 0.5f)),
            Box.FromCenterSize(new Vector3(-6f, 1.5f, 12f), new Vector3(6f, 3f, 0.5f)),
            Box.FromCenterSize(new Vector3(-14f, 1.5f, 15f), new Vector3(4f, 3f, 0.5f)),

            // Pillars 1 x 1 x 3 m.
            Box.FromCenterSize(new Vector3(9f, 1.5f, 9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(-9f, 1.5f, 9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(9f, 1.5f, -9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(-9f, 1.5f, -9f), new Vector3(1f, 3f, 1f)),

            // Low boxes, 1 m: reachable by jumping (apex about 1.34 m).
            Box.FromCenterSize(new Vector3(0f, 0.5f, 12f), new Vector3(2f, 1f, 2f)),
            Box.FromCenterSize(new Vector3(12f, 0.5f, -3f), new Vector3(2f, 1f, 2f)),
            Box.FromCenterSize(new Vector3(-12f, 0.5f, 3f), new Vector3(2f, 1f, 2f)),

            // High boxes, 1.5 m: not reachable by jumping.
            Box.FromCenterSize(new Vector3(0f, 0.75f, -12f), new Vector3(2f, 1.5f, 2f)),
            Box.FromCenterSize(new Vector3(14f, 0.75f, 14f), new Vector3(2f, 1.5f, 2f)),

            // Platform: 1 m base with a second 1 m step on top (stacked: Y ranges only meet).
            Box.FromCenterSize(new Vector3(-14f, 0.5f, -14f), new Vector3(4f, 1f, 4f)),
            Box.FromCenterSize(new Vector3(-14.5f, 1.5f, -14.5f), new Vector3(2f, 1f, 2f)),
        };

        public static ReadOnlySpan<Box> Boxes => s_boxes;
    }
}
