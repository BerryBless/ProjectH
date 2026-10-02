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
    public sealed class PieceMeshes : System.IDisposable
    {
        public PieceMeshes()
        {
            Box = BuildBox();
            Ramp = BuildRamp();
            Roof = BuildRoof();
        }

        public Mesh Box { get; }
        public Mesh Ramp { get; }
        public Mesh Roof { get; }

        public Mesh MeshOf(BuildPieceType type) => type == BuildPieceType.Ramp ? Ramp : type == BuildPieceType.Roof ? Roof : Box;

        // root: position and rotation; body: a child, scaled (box: to the piece's size) and lowered to height (0-1].
        public void Place(Transform root, Transform body, in BuildPieceShape shape, float height)
        {
            if (shape.Type == BuildPieceType.Wall || shape.Type == BuildPieceType.Floor)
            {
                Shared.Simulation.Box box = BuildGrid.BoxOf(shape);
                Vector3 size = box.Size.ToUnity();
                root.SetPositionAndRotation(box.Center.ToUnity(), Quaternion.identity);
                body.localRotation = Quaternion.identity;
                body.localScale = new Vector3(size.x, size.y * height, size.z);
                body.localPosition = new Vector3(0f, -size.y * (1f - height) * 0.5f, 0f);
                return;
            }
            Slope slope = BuildGrid.SlopeOf(shape);
            var pivot = new Vector3((slope.MinX + slope.MaxX) * 0.5f, slope.BaseY, (slope.MinZ + slope.MaxZ) * 0.5f);
            float yaw = shape.Type == BuildPieceType.Ramp ? shape.Rotation * 90f : 0f;
            root.SetPositionAndRotation(pivot, Quaternion.Euler(0f, yaw, 0f));
            body.localRotation = Quaternion.identity;
            body.localPosition = Vector3.zero;
            body.localScale = new Vector3(1f, height, 1f);
        }

        public void Dispose()
        {
            Object.Destroy(Box);
            Object.Destroy(Ramp);
            Object.Destroy(Roof);
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

        // A box corner order (bit 0 x, bit 1 y, bit 2 z) made into six flat quads: works for any hexahedron given that way.
        private static Mesh Hull(string name, Vector3[] c)
        {
            int[][] faces =
            {
                new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },   // -z, +z
                new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 },   // -x, +x
                new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 },   // -y, +y
            };
            var vertices = new Vector3[24];
            var triangles = new int[36];
            for (int f = 0; f < 6; f++)
            {
                for (int k = 0; k < 4; k++) vertices[f * 4 + k] = c[faces[f][k]];
                int v = f * 4;
                int t = f * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

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
