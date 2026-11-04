using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// WORLD-FIXES (#216, owner review 2026-11-04: "make sure all places are
// accessible by trail and road", "can we add curves to the corners of the
// trails and roads?"). Replaces the Alpha's square path, trail and road strips
// with one rounded network, drawn as merged meshes:
//   * inputs are the existing flat strips (paver paths, GYM-TRAIL pavements,
//     site approach mats) plus three links that were missing (the minigolf
//     course and the club gazebo had no path), rasterised on a 8 cm grid;
//   * a morphological closing (radius RIn) fillets every inner corner and
//     T-junction, then an opening (radius ROut) rounds every outer corner;
//   * the road is a capsule (round ends) with a sidewalk band and a kerb that
//     follows it, broken only at crossings; it now stops short of the park,
//     which the GYM-TRAIL kit places across its old north end;
//   * buildings and plazas are cut out last, so paths end square on walls and
//     doors and nothing is drawn under a floor.
// Surfaces are contoured with marching squares on the signed distance field at
// 25 cm (one mesh per layer and tile, shared vertices, no T-junctions). The
// trail strips keep their GameObjects (renderers off): GymTrailDirector's
// baked NavMesh and every simulation pose are unchanged. No collider is added.
public static class AlphaPathSceneBuild
{
    public const float RoadX = 21f, RoadHalf = 2f, RoadZMin = -7f, RoadZMax = 28f;
    public const float KerbWidth = 0.24f, KerbHeight = 0.14f, WalkWidth = 1.52f;
    const float RIn = 1.4f, ROut = 0.6f;
    const float GX0 = -44f, GZ0 = -16f, GX1 = 48f, GZ1 = 46f;
    const float Fine = 0.08f, Coarse = 0.25f;
    const int TilesX = 4, TilesZ = 2;

    // Missing links: minigolf (lane east of the workshop rack, plus a pad on the
    // course's north edge) and the club gazebo (pad meeting the trail lane).
    public static readonly Rect[] Links =
    {
        Rect.MinMaxRect(30.0f, -5.7f, 40.5f, -4.1f),
        Rect.MinMaxRect(32.3f, -7.25f, 41.6f, -5.5f),
        Rect.MinMaxRect(8.0f, -9.4f, 12.0f, -5.6f),
    };

    public sealed class Result
    {
        public readonly List<Rect> Crossings = new List<Rect>();
        public int Triangles;
    }

    static int nx, nz, n;
    static bool[] lastTrail;

    // True where the last Build drew trail surface (for the trail's centre dashes).
    public static bool OnTrail(float x, float z)
    {
        int i = Mathf.FloorToInt((x - GX0) / Fine), j = Mathf.FloorToInt((z - GZ0) / Fine);
        return lastTrail != null && i >= 0 && j >= 0 && i < nx && j < nz && lastTrail[j * nx + i];
    }

    // plazas: world-space (x, z) triangles of each plaza floor (the courtyard
    // floor is an L notched around the office, so its bounds would not do).
    public static Result Build(Transform parent, List<Rect> pavers, List<Rect> trails, List<Vector2[]> plazas, List<Rect> footprints,
        Material paverMat, Material trailMat, Material asphaltMat, Material kerbMat)
    {
        nx = Mathf.RoundToInt((GX1 - GX0) / Fine);
        nz = Mathf.RoundToInt((GZ1 - GZ0) / Fine);
        n = nx * nz;
        var result = new Result();
        var all = new List<Rect>(pavers);
        all.AddRange(trails);
        all.AddRange(Links);
        foreach (var r in all)
            if (Crosses(r)) result.Crossings.Add(Rect.MinMaxRect(r.xMin, r.yMin - 0.15f, r.xMax, r.yMax + 0.15f));

        var road = Capsule(0f, RoadHalf);
        var kerbBand = Capsule(RoadHalf, RoadHalf + KerbWidth);
        var walk = Capsule(RoadHalf + KerbWidth, RoadHalf + KerbWidth + WalkWidth);
        var gaps = Raster(result.Crossings);
        var paverRaw = Or(walk, Raster(pavers), Raster(Links), And(gaps, kerbBand));
        var trailRaw = Raster(trails);
        var plaza = RasterTriangles(plazas);
        var built = Raster(footprints);
        var roadside = Or(road, kerbBand);

        var paverRound = Open(Close(Or(paverRaw, plaza, roadside), RIn), ROut);
        var allRound = Open(Close(Or(paverRaw, trailRaw, plaza, roadside), RIn), ROut);
        var kerbSolid = AndNot(kerbBand, gaps);
        var paverFinal = AndNot(paverRound, Or(road, kerbSolid, plaza, built));
        var trailFinal = AndNot(allRound, Or(paverRound, roadside, plaza, built));
        var asphaltFinal = AndNot(road, built);
        lastTrail = trailFinal;

        result.Triangles += Surface(parent, "Pavers", paverFinal, 0.02f, paverMat);
        result.Triangles += Surface(parent, "Trail", trailFinal, 0.012f, trailMat);
        result.Triangles += Surface(parent, "Asphalt", asphaltFinal, 0.03f, asphaltMat);
        result.Triangles += Kerb(parent, kerbMat, result.Crossings, footprints);
        Debug.Log("WORLD_FIXES paths crossings=" + result.Crossings.Count + " triangles=" + result.Triangles);
        return result;
    }

    // A flat strip crossing the road: narrow along z, reaching past a road edge.
    static bool Crosses(Rect r)
    {
        if (r.height > 4.5f || r.center.y < RoadZMin - RoadHalf || r.center.y > RoadZMax + RoadHalf) return false;
        float w = RoadX - RoadHalf, e = RoadX + RoadHalf;
        return (r.xMin < w - 0.3f && r.xMax > w) || (r.xMin < e && r.xMax > e + 0.3f);
    }

    public static float RoadDistance(float x, float z)
    {
        float dz = z - Mathf.Clamp(z, RoadZMin, RoadZMax);
        return Mathf.Sqrt((x - RoadX) * (x - RoadX) + dz * dz);
    }

    // ------------------------------------------------------------ rasters
    static float CX(int i) { return GX0 + (i + 0.5f) * Fine; }
    static float CZ(int j) { return GZ0 + (j + 0.5f) * Fine; }

    static bool[] Capsule(float inner, float outer)
    {
        var m = new bool[n];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                float d = RoadDistance(CX(i), CZ(j));
                m[j * nx + i] = d <= outer && (inner <= 0f || d > inner);
            }
        return m;
    }

    static bool[] Raster(IEnumerable<Rect> rects)
    {
        var m = new bool[n];
        foreach (var r in rects)
        {
            int i0 = Mathf.Max(0, Mathf.CeilToInt((r.xMin - GX0) / Fine - 0.5f)), i1 = Mathf.Min(nx - 1, Mathf.FloorToInt((r.xMax - GX0) / Fine - 0.5f));
            int j0 = Mathf.Max(0, Mathf.CeilToInt((r.yMin - GZ0) / Fine - 0.5f)), j1 = Mathf.Min(nz - 1, Mathf.FloorToInt((r.yMax - GZ0) / Fine - 0.5f));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++) m[j * nx + i] = true;
        }
        return m;
    }

    static bool[] RasterTriangles(List<Vector2[]> tris)
    {
        var m = new bool[n];
        foreach (var t in tris)
        {
            float x0 = Mathf.Min(t[0].x, Mathf.Min(t[1].x, t[2].x)), x1 = Mathf.Max(t[0].x, Mathf.Max(t[1].x, t[2].x));
            float z0 = Mathf.Min(t[0].y, Mathf.Min(t[1].y, t[2].y)), z1 = Mathf.Max(t[0].y, Mathf.Max(t[1].y, t[2].y));
            int i0 = Mathf.Max(0, Mathf.FloorToInt((x0 - GX0) / Fine)), i1 = Mathf.Min(nx - 1, Mathf.CeilToInt((x1 - GX0) / Fine));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((z0 - GZ0) / Fine)), j1 = Mathf.Min(nz - 1, Mathf.CeilToInt((z1 - GZ0) / Fine));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var p = new Vector2(CX(i), CZ(j));
                    float d1 = Edge(t[0], t[1], p), d2 = Edge(t[1], t[2], p), d3 = Edge(t[2], t[0], p);
                    bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
                    if (!(neg && pos)) m[j * nx + i] = true;
                }
        }
        return m;
    }

    static float Edge(Vector2 a, Vector2 b, Vector2 p) { return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x); }

    static bool[] Or(params bool[][] ms)
    {
        var o = new bool[n];
        foreach (var m in ms) for (int k = 0; k < n; k++) o[k] |= m[k];
        return o;
    }

    static bool[] And(bool[] a, bool[] b) { var o = new bool[n]; for (int k = 0; k < n; k++) o[k] = a[k] && b[k]; return o; }
    static bool[] AndNot(bool[] a, bool[] b) { var o = new bool[n]; for (int k = 0; k < n; k++) o[k] = a[k] && !b[k]; return o; }
    static bool[] Not(bool[] a) { var o = new bool[n]; for (int k = 0; k < n; k++) o[k] = !a[k]; return o; }

    static bool[] Dilate(bool[] m, float r) { var d = Distance(m); var o = new bool[n]; for (int k = 0; k < n; k++) o[k] = d[k] <= r; return o; }
    static bool[] Erode(bool[] m, float r) { var d = Distance(Not(m)); var o = new bool[n]; for (int k = 0; k < n; k++) o[k] = d[k] > r; return o; }
    static bool[] Close(bool[] m, float r) { return Erode(Dilate(m, r), r); }
    static bool[] Open(bool[] m, float r) { return Dilate(Erode(m, r), r); }

    // Exact Euclidean distance (m) from each cell centre to the nearest set
    // cell centre (Felzenszwalb-Huttenlocher, separable squared transform).
    static float[] Distance(bool[] m)
    {
        const double Inf = 1e20;
        int len = Mathf.Max(nx, nz);
        var f = new double[n];
        for (int k = 0; k < n; k++) f[k] = m[k] ? 0.0 : Inf;
        var line = new double[len]; var d = new double[len]; var v = new int[len]; var z = new double[len + 1];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++) line[j] = f[j * nx + i];
            Transform1D(line, nz, d, v, z);
            for (int j = 0; j < nz; j++) f[j * nx + i] = d[j];
        }
        var o = new float[n];
        for (int j = 0; j < nz; j++)
        {
            for (int i = 0; i < nx; i++) line[i] = f[j * nx + i];
            Transform1D(line, nx, d, v, z);
            for (int i = 0; i < nx; i++) o[j * nx + i] = (float)(System.Math.Sqrt(d[i]) * Fine);
        }
        return o;
    }

    static void Transform1D(double[] f, int len, double[] d, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < len; q++)
        {
            double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < len; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    // ------------------------------------------------------------ surfaces
    // Signed distance (m, negative inside), sampled bilinearly from the fine grid.
    static float[] Signed(bool[] m)
    {
        var outside = Distance(m);
        var inside = Distance(Not(m));
        var s = new float[n];
        for (int k = 0; k < n; k++) s[k] = m[k] ? -(inside[k] - Fine * 0.5f) : outside[k] - Fine * 0.5f;
        return s;
    }

    static float Sample(float[] s, float x, float z)
    {
        float fx = (x - GX0) / Fine - 0.5f, fz = (z - GZ0) / Fine - 0.5f;
        int i = Mathf.Clamp(Mathf.FloorToInt(fx), 0, nx - 2), j = Mathf.Clamp(Mathf.FloorToInt(fz), 0, nz - 2);
        float tx = Mathf.Clamp01(fx - i), tz = Mathf.Clamp01(fz - j);
        float a = Mathf.Lerp(s[j * nx + i], s[j * nx + i + 1], tx);
        float b = Mathf.Lerp(s[(j + 1) * nx + i], s[(j + 1) * nx + i + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    sealed class Builder
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<int> t = new List<int>();
        public readonly Dictionary<long, int> map = new Dictionary<long, int>();
        public int Add(long key, Vector3 p)
        {
            int index;
            if (map.TryGetValue(key, out index)) return index;
            v.Add(p); map[key] = v.Count - 1;
            return v.Count - 1;
        }
    }

    static int Surface(Transform parent, string name, bool[] mask, float y, Material material)
    {
        var s = Signed(mask);
        int NX = Mathf.RoundToInt((GX1 - GX0) / Coarse), NZ = Mathf.RoundToInt((GZ1 - GZ0) / Coarse);
        var val = new float[(NX + 1) * (NZ + 1)];
        for (int J = 0; J <= NZ; J++)
            for (int I = 0; I <= NX; I++) val[J * (NX + 1) + I] = Sample(s, GX0 + I * Coarse, GZ0 + J * Coarse);
        var group = new GameObject(name).transform;
        group.SetParent(parent, false);
        int tris = 0;
        int tw = Mathf.CeilToInt(NX / (float)TilesX), th = Mathf.CeilToInt(NZ / (float)TilesZ);
        for (int ty = 0; ty < TilesZ; ty++)
            for (int tx = 0; tx < TilesX; tx++)
            {
                var b = new Builder();
                for (int J = ty * th; J < Mathf.Min(NZ, (ty + 1) * th); J++)
                    for (int I = tx * tw; I < Mathf.Min(NX, (tx + 1) * tw); I++)
                        Cell(b, val, NX, I, J, y);
                if (b.t.Count == 0) continue;
                var mesh = new Mesh { name = name + "_" + tx + "_" + ty, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(b.v);
                mesh.SetTriangles(b.t, 0);
                mesh.SetNormals(b.v.ConvertAll(_ => Vector3.up));
                mesh.SetUVs(0, b.v.ConvertAll(p => new Vector2(p.x, p.z)));
                mesh.RecalculateBounds();
                var go = new GameObject(mesh.name);
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = ShadowCastingMode.Off;
                tris += b.t.Count / 3;
            }
        return tris;
    }

    // Marching squares on one lattice cell; the inside polygon is walked
    // clockwise seen from above (bl, tl, tr, br) so faces point up.
    static readonly int[] CornerI = { 0, 0, 1, 1 }, CornerJ = { 0, 1, 1, 0 };
    static readonly List<int> Poly = new List<int>(8);

    static void Cell(Builder b, float[] val, int NX, int I, int J, float y)
    {
        int stride = NX + 1;
        Poly.Clear();
        for (int c = 0; c < 4; c++)
        {
            int i0 = I + CornerI[c], j0 = J + CornerJ[c];
            int i1 = I + CornerI[(c + 1) % 4], j1 = J + CornerJ[(c + 1) % 4];
            float s0 = val[j0 * stride + i0], s1 = val[j1 * stride + i1];
            if (s0 < 0f) Poly.Add(b.Add(((long)j0 * stride + i0) * 3, new Vector3(GX0 + i0 * Coarse, y, GZ0 + j0 * Coarse)));
            if ((s0 < 0f) != (s1 < 0f))
            {
                // Canonical edge (lower lattice index first) so both cells share the vertex.
                bool forward = (j0 * stride + i0) < (j1 * stride + i1);
                int ia = forward ? i0 : i1, ja = forward ? j0 : j1, ib = forward ? i1 : i0, jb = forward ? j1 : j0;
                float sa = forward ? s0 : s1, sb = forward ? s1 : s0;
                float t = sa / (sa - sb);
                long key = ((long)ja * stride + ia) * 3 + (ja == jb ? 1 : 2);
                Poly.Add(b.Add(key, new Vector3(GX0 + Mathf.Lerp(ia, ib, t) * Coarse, y, GZ0 + Mathf.Lerp(ja, jb, t) * Coarse)));
            }
        }
        for (int k = 1; k + 1 < Poly.Count; k++) { b.t.Add(Poly[0]); b.t.Add(Poly[k]); b.t.Add(Poly[k + 1]); }
    }

    // ------------------------------------------------------------ kerb
    // A raised strip on the road capsule's edge (straights and round ends),
    // broken at crossings and where a building stands on the road edge.
    static int Kerb(Transform parent, Material material, List<Rect> crossings, List<Rect> footprints)
    {
        float rc = RoadHalf + KerbWidth * 0.5f;
        var pts = new List<Vector3>();   // centreline (x, 0, z)
        var nrm = new List<Vector3>();   // outward normal
        System.Action<float, float, float, float> line = (x, z0, z1, nx0) =>
        {
            int steps = Mathf.CeilToInt(Mathf.Abs(z1 - z0) / 0.25f);
            for (int k = 0; k < steps; k++) { pts.Add(new Vector3(x, 0f, Mathf.Lerp(z0, z1, k / (float)steps))); nrm.Add(new Vector3(nx0, 0f, 0f)); }
        };
        System.Action<float, float, float> arc = (cz, a0, a1) =>
        {
            const int steps = 24;
            for (int k = 0; k < steps; k++)
            {
                float a = Mathf.Lerp(a0, a1, k / (float)steps);
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                pts.Add(new Vector3(RoadX, 0f, cz) + d * rc); nrm.Add(d);
            }
        };
        line(RoadX - rc, RoadZMax, RoadZMin, -1f);
        arc(RoadZMin, Mathf.PI, 2f * Mathf.PI);
        line(RoadX + rc, RoadZMin, RoadZMax, 1f);
        arc(RoadZMax, 0f, Mathf.PI);
        var v = new List<Vector3>(); var nm = new List<Vector3>(); var t = new List<int>();
        System.Action<Vector3, Vector3, Vector3, Vector3, Vector3> quad = (a, b, c, d, normal) =>
        {
            int o = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int k = 0; k < 4; k++) nm.Add(normal);
            bool flip = Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f;
            if (flip) t.AddRange(new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
            else t.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
        };
        float h = KerbHeight, half = KerbWidth * 0.5f;
        bool prevOn = false;
        int count = pts.Count;
        for (int k = 0; k < count; k++)
        {
            Vector3 p0 = pts[k], p1 = pts[(k + 1) % count];
            Vector3 n0 = nrm[k], n1 = nrm[(k + 1) % count];
            Vector3 mid = (p0 + p1) * 0.5f;
            bool on = !Inside(crossings, mid, 0f) && !Inside(footprints, mid, 0.05f);
            if (on)
            {
                Vector3 i0 = p0 - n0 * half, o0 = p0 + n0 * half, i1 = p1 - n1 * half, o1 = p1 + n1 * half;
                Vector3 up = Vector3.up * h;
                quad(i0 + up, o0 + up, o1 + up, i1 + up, Vector3.up);
                quad(o0, o1, o1 + up, o0 + up, ((n0 + n1) * 0.5f).normalized);
                quad(i0, i1, i1 + up, i0 + up, -((n0 + n1) * 0.5f).normalized);
                Vector3 along = (p1 - p0).normalized;
                if (!prevOn) quad(i0, o0, o0 + up, i0 + up, -along);
                bool nextOn = true;
                {
                    Vector3 q1 = pts[(k + 2) % count], m2 = (p1 + q1) * 0.5f;
                    nextOn = !Inside(crossings, m2, 0f) && !Inside(footprints, m2, 0.05f);
                }
                if (!nextOn) quad(i1, o1, o1 + up, i1 + up, along);
            }
            prevOn = on;
        }
        var mesh = new Mesh { name = "Kerb", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(v);
        mesh.SetNormals(nm);
        mesh.SetTriangles(t, 0);
        mesh.SetUVs(0, v.ConvertAll(p => new Vector2(p.x + p.y, p.z)));
        mesh.RecalculateBounds();
        var go = new GameObject("Kerb");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        return t.Count / 3;
    }

    public static bool Inside(List<Rect> rects, Vector3 p, float grow)
    {
        foreach (var r in rects)
            if (p.x >= r.xMin - grow && p.x <= r.xMax + grow && p.z >= r.yMin - grow && p.z <= r.yMax + grow) return true;
        return false;
    }
}
