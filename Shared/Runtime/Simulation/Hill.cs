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

        // 기능: 언덕 하나를 격자 단위로 만든다(검증하지 않는다).
        // 입력: centerI·centerJ - 중심 정점 번호, innerRadius - 평평한 꼭대기 반지름(칸), outerRadius - 높이 0이 되는 반지름(칸,
        //   innerRadius보다 큼), height - 꼭대기 높이(1/32 m).
        // 출력: 주어진 값을 담은 언덕.
        public Hill(int centerI, int centerJ, int innerRadius, int outerRadius, int height)
        {
            CenterI = centerI;
            CenterJ = centerJ;
            InnerRadius = innerRadius;
            OuterRadius = outerRadius;
            Height = height;
        }

        // 기능: 정점 하나에서 이 언덕의 높이를 정수로 계산한다(모든 쪽이 같은 결과를 내도록 정수 연산만).
        // 입력: i·j - 정점 번호.
        // 출력: 1/32 m 단위 높이. OuterRadius 밖은 0, InnerRadius 안은 Height, 사이는 Height x t^2.
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

        // 기능: 언덕들로 지형 격자를 만든다. 정점마다 가장 높은 언덕 하나를 쓴다(합치지 않으므로 평지는 평평하게 남는다).
        // 입력: originX·originZ - 격자 원점, cellSize - 칸 한 변, vertsX·vertsZ - 정점 수, hills - 언덕 목록.
        // 출력: 미터 단위 높이로 변환한 새 HeightField.
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
