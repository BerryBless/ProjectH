using System;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // Phase 13 D10, D16 (request §44-§47, §59-§62): how a confirmed piece looks at a server tick: how far it is built
    // (the server's BuildWorld.Progress), its health (BuildWorld.Health: initial + (max - initial) x progress - damage),
    // the damage stage its material is drawn with, and the height its construction is drawn at. Presentation only; the
    // server decides health. Pure, no UnityEngine, no allocation.
    public static class BuildPieceLook
    {
        public const int Stages = 3;               // whole, damaged, nearly broken
        public const float MinConstructionScale = 0.2f;

        public static float Progress(uint createdTick, double serverTick, int constructionTicks)
        {
            if (constructionTicks <= 0 || serverTick >= createdTick + (double)constructionTicks) return 1f;
            if (serverTick <= createdTick) return 0f;
            return (float)((serverTick - createdTick) / constructionTicks);
        }

        public static int Health(in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            if (catalog == null) return 1;
            int m = (int)piece.Material;
            float progress = Progress(piece.CreatedTick, serverTick, catalog.ConstructionTicks[m]);
            float grown = catalog.InitialHealth[m] + (catalog.MaxHealth[m] - catalog.InitialHealth[m]) * progress;
            return (int)Math.Floor(grown) - piece.Damage;
        }

        // 0 above two thirds of the material's full health, 1 above one third, 2 below.
        public static int Stage(int health, int maxHealth)
        {
            if (maxHealth <= 0 || health * 3 > maxHealth * 2) return 0;
            return health * 3 > maxHealth ? 1 : 2;
        }

        // The stage the piece is drawn with: damage against the health grown so far, so a piece that is still building
        // but undamaged is whole (the server's low initial health is not damage).
        public static int DamageStage(in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            if (catalog == null) return 0;
            int m = (int)piece.Material;
            float progress = Progress(piece.CreatedTick, serverTick, catalog.ConstructionTicks[m]);
            int grown = (int)Math.Floor(catalog.InitialHealth[m] + (catalog.MaxHealth[m] - catalog.InitialHealth[m]) * progress);
            return Stage(grown - piece.Damage, grown);
        }

        // The drawn height while building: from MinConstructionScale up to full.
        public static float ConstructionScale(float progress) =>
            MinConstructionScale + (1f - MinConstructionScale) * Math.Max(0f, Math.Min(1f, progress));
    }
}
