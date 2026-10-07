using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 13 D2, D16: the three shared meshes every piece is drawn with, built once in code: a unit box (walls and
    // floors, scaled to BuildGrid.BoxOf), a ramp slab and a roof pyramid, both in metres with their pivot on the cell's
    // centre at the slope's base height (BuildGrid.SlopeOf), so a ramp only turns by its rotation. The shapes are the
    // ones Shared collides with: the ramp's top is the plane from the low edge to RampRise, the roof's top the pyramid
    // to RoofRise, each over a SlopeThickness slab. Place puts a piece's root (full size: its collider) and its body
    // (the drawn part, lowered to the construction height). Dispose destroys the meshes.
    // Phase 13.5 D12: an edited piece's mesh is made the first time its key (CacheKey: type, Edit and, for a wall, its
    // rotation) is drawn and kept: an edited wall or floor, a flat roof or a roof passage is the boxes of BuildGrid.PartsOf
    // (the same boxes movement and shots see), in metres around the piece's pivot (PivotOf), unrotated (the root of a box
    // shape never turns), so a south wall (along X) and a west wall (along Z) with the same Edit are two meshes; a one-way
    // roof (Edit 1-4) is one shared wedge turned by its direction. Only valid edits reach here (the reader and
    // BuildEdit.TryApply check), so the cache holds at most 2 x 511 walls, 15 floors and 2 roofs; MaxCachedMeshes is a hard
    // stop past which the unedited mesh is drawn. Edit 0 uses the shared meshes as before.
    public sealed class PieceMeshes : System.IDisposable
    {
        // 1024 wall keys (9 bits x 2 rotations) + 16 floor + 7 roof keys: the most distinct cache keys there can be.
        public const int MaxCachedMeshes = 1024 + 16 + 7;

        private readonly Dictionary<int, Mesh> _edited = new Dictionary<int, Mesh>();
        private readonly Box[] _parts = new Box[BuildGrid.MaxPartsPerPiece];

        // 기능: 공유 Mesh(상자, 경사로, 사각뿔 지붕, 한쪽 경사 지붕)를 만든다.
        // 입력: 없음.
        // 출력: 편집 Mesh 캐시가 빈 PieceMeshes.
        public PieceMeshes()
        {
            Box = BuildBox();
            Ramp = BuildRamp();
            Roof = BuildRoof();
            RoofSlope = BuildRoofSlope();
        }

        public Mesh Box { get; }
        public Mesh Ramp { get; }
        public Mesh Roof { get; }
        // Phase 13.5 D3: a one-way roof rising toward +Z (turned by its direction like a ramp).
        public Mesh RoofSlope { get; }
        public int CachedCount => _edited.Count;

        public Mesh MeshOf(BuildPieceType type) => type == BuildPieceType.Ramp ? Ramp : type == BuildPieceType.Roof ? Roof : Box;

        // 기능: 조각의 실제 모양(편집 포함)을 그릴 Mesh를 낸다. 편집된 상자 모양은 처음 쓸 때 만들어 CacheKey(종류·Edit·벽 회전)로 캐시한다.
        // 입력: shape - 조각 모양(유효한 Edit).
        // 출력: Mesh. Edit 0은 공유 Mesh, 한쪽 경사 지붕은 공유 쐐기, 그 밖의 편집은 캐시된 PartsOf 상자 Mesh.
        //   캐시가 MaxCachedMeshes에 닿았으면 편집하지 않은 Mesh(도달하지 않는 상한).
        public Mesh MeshOf(in BuildPieceShape shape)
        {
            if (shape.Type == BuildPieceType.Ramp) return Ramp;
            if (shape.Type == BuildPieceType.Roof)
            {
                if (shape.Edit == 0) return Roof;
                if (BuildGrid.HasSlope(shape)) return RoofSlope;
            }
            else if (shape.Edit == 0)
            {
                return Box;
            }
            int key = CacheKey(shape);
            if (_edited.TryGetValue(key, out Mesh cached)) return cached;
            if (_edited.Count >= MaxCachedMeshes) return MeshOf(shape.Type);
            Mesh mesh = BuildParts(shape);
            _edited.Add(key, mesh);
            return mesh;
        }

        // 기능: 편집 Mesh 캐시의 키를 낸다. 상자 Mesh는 회전 없이 월드 축으로 만들므로 벽은 회전(남쪽 0 = X축, 서쪽 1 = Z축)도 키에 넣는다.
        //   바닥·지붕은 회전이 늘 0이라 넣지 않는다.
        // 입력: shape - 조각 모양.
        // 출력: 종류·Edit·(벽이면) 회전으로 정해지는 정수 키. 모양이 다른 Mesh면 키도 다르다.
        public static int CacheKey(in BuildPieceShape shape)
        {
            int rotation = shape.Type == BuildPieceType.Wall ? shape.Rotation & 1 : 0;
            return ((((int)shape.Type << 1) | rotation) << BuildEdit.Bits) | shape.Edit;
        }

        // 기능: 조각 Mesh의 기준점을 낸다. 벽·바닥은 틀 상자(BoxOf)의 중심, 경사로·지붕은 칸 중심의 경사면 바닥(경사로 낮은 끝,
        //   지붕 처마) 높이. 편집해도 바뀌지 않는다.
        // 입력: shape - 조각 모양.
        // 출력: 월드 위치.
        public static Vector3 PivotOf(in BuildPieceShape shape)
        {
            if (shape.Type == BuildPieceType.Wall || shape.Type == BuildPieceType.Floor) return BuildGrid.BoxOf(shape).Center.ToUnity();
            Slope slope = BuildGrid.SlopeOf(shape);
            return new Vector3((slope.MinX + slope.MaxX) * 0.5f, slope.BaseY, (slope.MinZ + slope.MaxZ) * 0.5f);
        }

        // root: position and rotation; body: a child, scaled (box: to the piece's size) and lowered to height (0-1].
        public void Place(Transform root, Transform body, in BuildPieceShape shape, float height)
        {
            PlaceRoot(root, shape);
            PlaceBody(body, shape, height);
        }

        // 기능: 조각 루트(Collider를 가진 부분)의 위치와 회전을 정한다. 조각이 새로 나오거나 모양이 바뀔 때만 부른다.
        // 입력: root - 루트 Transform, shape - 조각 모양.
        // 출력: 반환값 없음. 루트가 PivotOf에 놓이고, 경사로는 회전, 한쪽 경사 지붕은 높아지는 방향만큼 돈다(나머지는 회전 없음).
        public void PlaceRoot(Transform root, in BuildPieceShape shape)
        {
            float yaw = 0f;
            if (shape.Type == BuildPieceType.Ramp) yaw = shape.Rotation * 90f;
            else if (shape.Type == BuildPieceType.Roof && shape.Edit >= BuildEdit.RoofSlopeFirst && shape.Edit <= BuildEdit.RoofSlopeLast)
                yaw = (shape.Edit - BuildEdit.RoofSlopeFirst) * 90f;
            root.SetPositionAndRotation(PivotOf(shape), Quaternion.Euler(0f, yaw, 0f));
        }

        // 기능: 조각 몸체(그려지는 자식)를 건설 높이만큼 낮춰 놓는다. 높이가 바뀔 때마다 부른다.
        // 입력: body - 몸체 Transform, shape - 조각 모양, height - 건설 높이 비율(0–1].
        // 출력: 반환값 없음. 단위 상자는 크기로 늘리고 아래 면을 고정한 채 줄인다. 편집된 벽·바닥(미터 Mesh)도 아래 면을 고정하고,
        //   경사로·지붕은 기준점에서 세로로만 줄인다.
        public void PlaceBody(Transform body, in BuildPieceShape shape, float height)
        {
            body.localRotation = Quaternion.identity;
            if (shape.Type == BuildPieceType.Wall || shape.Type == BuildPieceType.Floor)
            {
                Vector3 size = BuildGrid.BoxOf(shape).Size.ToUnity();
                body.localPosition = new Vector3(0f, -size.y * (1f - height) * 0.5f, 0f);
                body.localScale = shape.Edit == 0 ? new Vector3(size.x, size.y * height, size.z) : new Vector3(1f, height, 1f);
                return;
            }
            body.localPosition = Vector3.zero;
            body.localScale = new Vector3(1f, height, 1f);
        }

        // 기능: 공유 Mesh와 캐시된 편집 Mesh를 모두 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 캐시가 빈다.
        public void Dispose()
        {
            Object.Destroy(Box);
            Object.Destroy(Ramp);
            Object.Destroy(Roof);
            Object.Destroy(RoofSlope);
            foreach (KeyValuePair<int, Mesh> kv in _edited)
            {
                if (kv.Value != null) Object.Destroy(kv.Value);
            }
            _edited.Clear();
        }

        // A unit cube centred on the origin, one quad per face (flat normals).
        private static Mesh BuildBox()
        {
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++) c[i] = new Vector3((i & 1) != 0 ? 0.5f : -0.5f, (i & 2) != 0 ? 0.5f : -0.5f, (i & 4) != 0 ? 0.5f : -0.5f);
            return Hull("PieceBox", c);
        }

        // Rising toward +Z: the top from (z -2.5, y 0) to (z +2.5, y RampRise), the bottom SlopeThickness lower.
        private static Mesh BuildRamp()
        {
            const float h = BuildGrid.CellSize * 0.5f;
            const float t = BuildGrid.SlopeThickness;
            const float rise = BuildGrid.RampRise;
            var c = new[]
            {
                new Vector3(-h, -t, -h), new Vector3(h, -t, -h), new Vector3(-h, 0f, -h), new Vector3(h, 0f, -h),
                new Vector3(-h, rise - t, h), new Vector3(h, rise - t, h), new Vector3(-h, rise, h), new Vector3(h, rise, h),
            };
            return Hull("PieceRamp", c);
        }

        // 기능: 한쪽 경사 지붕 쐐기를 만든다(Phase 13.5 D3, SlopeKind.RoofSlope). +Z로 처마(0)에서 RoofRise까지 오르는 윗면과,
        //   처마 SlopeThickness 아래의 평평한 천장(사각뿔 지붕과 같은 고체 규칙).
        // 입력: 없음.
        // 출력: 기준점이 칸 중심·처마 높이인 Mesh.
        private static Mesh BuildRoofSlope()
        {
            const float h = BuildGrid.CellSize * 0.5f;
            const float t = BuildGrid.SlopeThickness;
            const float rise = BuildGrid.RoofRise;
            var c = new[]
            {
                new Vector3(-h, -t, -h), new Vector3(h, -t, -h), new Vector3(-h, 0f, -h), new Vector3(h, 0f, -h),
                new Vector3(-h, -t, h), new Vector3(h, -t, h), new Vector3(-h, rise, h), new Vector3(h, rise, h),
            };
            return Hull("PieceRoofSlope", c);
        }

        // 기능: 편집된 모양의 PartsOf 상자들을 Mesh 하나로 만든다(상자마다 면 6개, 평평한 법선). 기준점은 PivotOf다.
        // 입력: shape - 상자 모양인 조각(편집된 벽·바닥, 평지붕, 통로).
        // 출력: 새 Mesh(호출자가 캐시하고 Dispose에서 파괴한다). 처음 쓸 때 한 번만 할당한다.
        private Mesh BuildParts(in BuildPieceShape shape)
        {
            int count = BuildGrid.PartsOf(shape, _parts, out bool _);
            Vector3 pivot = PivotOf(shape);
            var vertices = new Vector3[count * 24];
            var triangles = new int[count * 36];
            var c = new Vector3[8];
            for (int b = 0; b < count; b++)
            {
                Vector3 min = _parts[b].Min.ToUnity() - pivot;
                Vector3 max = _parts[b].Max.ToUnity() - pivot;
                for (int i = 0; i < 8; i++)
                    c[i] = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                WriteHull(c, vertices, triangles, b * 24, b * 36);
            }
            var mesh = new Mesh { name = "PieceEdited " + shape.Type + " " + shape.Edit, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // 기능: 상자 꼭짓점 순서(비트 0 x, 1 y, 2 z)로 준 육면체 하나를 평평한 사각형 6개의 Mesh로 만든다.
        // 입력: name - Mesh 이름, c - 꼭짓점 8개.
        // 출력: 새 Mesh(호출자가 파괴한다).
        private static Mesh Hull(string name, Vector3[] c)
        {
            var vertices = new Vector3[24];
            var triangles = new int[36];
            WriteHull(c, vertices, triangles, 0, 0);
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // 기능: 상자 꼭짓점 순서(비트 0 x, 1 y, 2 z)의 육면체 하나를 사각형 6개로 배열에 쓴다.
        // 입력: c - 꼭짓점 8개, vertices·triangles - 쓸 배열, v0·t0 - 쓰기 시작할 위치(정점 24개, 색인 36개를 쓴다).
        // 출력: 반환값 없음.
        private static void WriteHull(Vector3[] c, Vector3[] vertices, int[] triangles, int v0, int t0)
        {
            for (int f = 0; f < 6; f++)
            {
                int a = Faces[f * 4], b = Faces[f * 4 + 1], d = Faces[f * 4 + 2], e = Faces[f * 4 + 3];
                int v = v0 + f * 4;
                vertices[v] = c[a];
                vertices[v + 1] = c[b];
                vertices[v + 2] = c[d];
                vertices[v + 3] = c[e];
                int t = t0 + f * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
        }

        private static readonly int[] Faces =
        {
            0, 2, 3, 1, 4, 5, 7, 6,   // -z, +z
            0, 4, 6, 2, 1, 3, 7, 5,   // -x, +x
            0, 1, 5, 4, 2, 6, 7, 3,   // -y, +y
        };

        // Four sloped faces to the apex (RoofRise over the eaves), a SlopeThickness skirt under the eaves and a flat
        // ceiling. Separate vertices per face for flat normals; clockwise seen from outside (Unity's front face).
        private static Mesh BuildRoof()
        {
            const float h = BuildGrid.CellSize * 0.5f;
            const float t = BuildGrid.SlopeThickness;
            var apex = new Vector3(0f, BuildGrid.RoofRise, 0f);
            Vector3[] eave = { new Vector3(-h, 0f, -h), new Vector3(-h, 0f, h), new Vector3(h, 0f, h), new Vector3(h, 0f, -h) };
            var vertices = new Vector3[4 * 3 + 4 * 4 + 4];
            var triangles = new int[4 * 3 + 4 * 6 + 6];
            int vi = 0;
            int ti = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = eave[i];
                Vector3 b = eave[(i + 1) % 4];
                vertices[vi] = a;
                vertices[vi + 1] = apex;
                vertices[vi + 2] = b;
                triangles[ti++] = vi;
                triangles[ti++] = vi + 2;
                triangles[ti++] = vi + 1;
                vi += 3;
                // The skirt: a, b and the same points SlopeThickness lower, facing out.
                vertices[vi] = a;
                vertices[vi + 1] = b;
                vertices[vi + 2] = b + Vector3.down * t;
                vertices[vi + 3] = a + Vector3.down * t;
                triangles[ti++] = vi;
                triangles[ti++] = vi + 2;
                triangles[ti++] = vi + 1;
                triangles[ti++] = vi;
                triangles[ti++] = vi + 3;
                triangles[ti++] = vi + 2;
                vi += 4;
            }
            // The ceiling, facing down.
            for (int i = 0; i < 4; i++) vertices[vi + i] = eave[i] + Vector3.down * t;
            triangles[ti++] = vi;
            triangles[ti++] = vi + 3;
            triangles[ti++] = vi + 2;
            triangles[ti++] = vi;
            triangles[ti++] = vi + 2;
            triangles[ti++] = vi + 1;
            var mesh = new Mesh { name = "PieceRoof", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
