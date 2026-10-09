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

        // 기능: 조각의 건설 진행도를 낸다(서버 BuildWorld.Progress와 같은 식).
        // 입력: createdTick - 조각이 놓인 서버 Tick, serverTick - 추정 서버 Tick, constructionTicks - 재료의 건설 Tick 수.
        // 출력: 0..1. 건설 Tick이 0 이하이거나 다 지었으면 1, 아직 놓인 Tick 이전이면 0.
        public static float Progress(uint createdTick, double serverTick, int constructionTicks)
        {
            if (constructionTicks <= 0 || serverTick >= createdTick + (double)constructionTicks) return 1f;
            if (serverTick <= createdTick) return 0f;
            return (float)((serverTick - createdTick) / constructionTicks);
        }

        // 기능: 조각의 현재 체력을 낸다(서버 BuildWorld.Health와 같은 식: 초기 + (최대 - 초기) × 진행도 - 피해).
        // 입력: piece - 조각 기록, catalog - 건설 수치(null이면 1), serverTick - 추정 서버 Tick.
        // 출력: 체력(정수로 내림한 성장 체력에서 피해를 뺀 값, 음수일 수 있다).
        public static int Health(in BuildPieceRecord piece, BuildCatalogData catalog, double serverTick)
        {
            if (catalog == null) return 1;
            int m = (int)piece.Material;
            float progress = Progress(piece.CreatedTick, serverTick, catalog.ConstructionTicks[m]);
            float grown = catalog.InitialHealth[m] + (catalog.MaxHealth[m] - catalog.InitialHealth[m]) * progress;
            return (int)Math.Floor(grown) - piece.Damage;
        }

        // 기능: 체력 비율로 피해 단계를 고른다.
        // 입력: health - 현재 체력, maxHealth - 기준 최대 체력.
        // 출력: 최대의 2/3 초과면 0(온전), 1/3 초과면 1(손상), 그 이하면 2(부서지기 직전). maxHealth가 0 이하면 0.
        // 0 above two thirds of the material's full health, 1 above one third, 2 below.
        public static int Stage(int health, int maxHealth)
        {
            if (maxHealth <= 0 || health * 3 > maxHealth * 2) return 0;
            return health * 3 > maxHealth ? 1 : 2;
        }

        // 기능: 조각을 그릴 피해 단계를 낸다. 지금까지 자란 체력을 기준으로 피해를 재므로, 짓는 중이어도 피해가 없으면 온전하다.
        // 입력: piece - 조각 기록, catalog - 건설 수치(null이면 0), serverTick - 추정 서버 Tick.
        // 출력: 0(온전)·1(손상)·2(부서지기 직전).
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

        // 기능: 건설 진행도를 그릴 높이 비율로 바꾼다.
        // 입력: progress - 건설 진행도(0..1 밖은 자른다).
        // 출력: MinConstructionScale..1의 높이 비율.
        // The drawn height while building: from MinConstructionScale up to full.
        public static float ConstructionScale(float progress) =>
            MinConstructionScale + (1f - MinConstructionScale) * Math.Max(0f, Math.Min(1f, progress));
    }
}
