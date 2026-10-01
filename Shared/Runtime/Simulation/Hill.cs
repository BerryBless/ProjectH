namespace ProjectH.Shared.Simulation
{
    // Phase 6 D3: one round hill of the terrain, in grid units. Flat on top out to InnerRadius (a plateau when > 0),
    // falling to 0 at OuterRadius. Integer math only, so the server and every client build bit-identical heights.
    public readonly struct Hill
    {
        public const int UnitsPerMeter = 32;

        public readonly int CenterI;
        public readonly int CenterJ;
        public readonly int InnerRadius;   // cells
        public readonly int OuterRadius;   // cells, > InnerRadius
        public readonly int Height;        // 1/32 m

        public Hill(int centerI, int centerJ, int innerRadius, int outerRadius, int height)
        {
            CenterI = centerI;
            CenterJ = centerJ;
            InnerRadius = innerRadius;
            OuterRadius = outerRadius;
            Height = height;
        }

        // Height at vertex (i, j) in 1/32 m: Height * t^2 with t = clamp((R^2 - d^2) / (R^2 - r0^2), 0, 1).
        public int HeightAt(int i, int j)
        {
            long di = i - CenterI;
            long dj = j - CenterJ;
            long d2 = di * di + dj * dj;
            long r2 = (long)OuterRadius * OuterRadius;
            long den = r2 - (long)InnerRadius * InnerRadius;
            long num = r2 - d2;
            if (num <= 0) return 0;
            if (num >= den) return Height;
            return (int)(Height * num * num / (den * den));
        }

        // The terrain of a grid: at every vertex the highest hill wins (hills never add up, so a plateau stays flat).
        public static HeightField Build(float originX, float originZ, float cellSize, int vertsX, int vertsZ, Hill[] hills)
        {
            var heights = new float[vertsX * vertsZ];
            for (int j = 0; j < vertsZ; j++)
            {
                for (int i = 0; i < vertsX; i++)
                {
                    int best = 0;
                    for (int h = 0; h < hills.Length; h++)
                    {
                        int value = hills[h].HeightAt(i, j);
                        if (value > best) best = value;
                    }
                    heights[i + j * vertsX] = best / (float)UnitsPerMeter;
                }
            }
            return new HeightField(originX, originZ, cellSize, vertsX, vertsZ, heights);
        }
    }
}
