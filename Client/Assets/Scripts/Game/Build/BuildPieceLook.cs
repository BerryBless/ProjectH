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

        // 기능: Server BuildWorld.Progress와 같은 방식으로 조각의 건설 진행률을 계산한다.
        // 입력: createdTick - 조각 생성 Tick, serverTick - 기준 Server Tick(소수 포함), constructionTicks - 재료의 건설 Tick 수.
        // 출력: 0~1 진행률. 건설 Tick이 0 이하이거나 건설이 끝났으면 1.
        public static float Progress(uint createdTick, double serverTick, int constructionTicks)
        {
            if (constructionTicks <= 0 || serverTick >= createdTick + (double)constructionTicks) return 1f;
            if (serverTick <= createdTick) return 0f;
            return (float)((serverTick - createdTick) / constructionTicks);
        }

        // 기능: Server BuildWorld.Health와 같은 식으로 기준 Tick의 조각 체력을 계산한다(표시용).
        // 입력: piece - 확정 조각, catalog - 건설 Catalog, serverTick - 기준 Server Tick.
        // 출력: 진행률만큼 자란 체력에서 피해를 뺀 값. Catalog가 없으면 1.
        public static int Health(in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            if (catalog == null) return 1;
            int m = (int)piece.Material;
            float progress = Progress(piece.CreatedTick, serverTick, catalog.ConstructionTicks[m]);
            float grown = catalog.InitialHealth[m] + (catalog.MaxHealth[m] - catalog.InitialHealth[m]) * progress;
            return (int)Math.Floor(grown) - piece.Damage;
        }

        // 기능: 체력 비율로 손상 단계를 정한다.
        // 입력: health - 현재 체력, maxHealth - 기준 체력.
        // 출력: 2/3 초과면 0, 1/3 초과면 1, 그 외 2. maxHealth가 0 이하면 0.
        // 0 above two thirds of the material's full health, 1 above one third, 2 below.
        public static int Stage(int health, int maxHealth)
        {
            if (maxHealth <= 0 || health * 3 > maxHealth * 2) return 0;
            return health * 3 > maxHealth ? 1 : 2;
        }

        // 기능: 지금까지 자란 체력 대비 피해로 조각을 그릴 손상 단계를 정한다.
        // 입력: piece - 확정 조각, catalog - 건설 Catalog, serverTick - 기준 Server Tick.
        // 출력: 0~2 손상 단계. Catalog가 없으면 0.
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

        // 기능: 건설 진행률을 그릴 높이 비율로 바꾼다.
        // 입력: progress - 건설 진행률(0~1 밖은 잘린다).
        // 출력: MinConstructionScale~1 사이 높이 비율.
        // The drawn height while building: from MinConstructionScale up to full.
        public static float ConstructionScale(float progress) =>
            MinConstructionScale + (1f - MinConstructionScale) * Math.Max(0f, Math.Min(1f, progress));
    }
}
