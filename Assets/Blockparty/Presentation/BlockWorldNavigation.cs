// LOD-NAV (M4): block navigation over the placed LOD objects' collision proxies.
// The walkable grid is baked from physics at runtime: a cell is walkable when a
// floor (upward face at most MaxFloor high) lies under it and an agent capsule
// standing on it touches no collider. Paths are octile Dijkstra routes over the
// grid, string-pulled to corners; a CharacterController then walks each route
// through the real colliders and its positions are what the nav export records.
// Seated and lying slots put their approach node on the target furniture, so when
// no walkable cell lies within 0.15 m the route ends with one straight leg of at
// most FinalLeg metres that may pass only through the slot's own target object and
// low furniture (at most SeatHeight tall) standing on the approach stance itself,
// i.e. the seat the resident sits into. The leg also ends routes whose goal cell is
// walkable but cut off from the entrance.
//
// Development-player evidence modes (MainSceneBuild.BuildLodNavPlayer):
//   LodNav.x86_64 -lodnav-mode nav|capture|perf -lodnav-out <dir> -lodnav-tree <hash>
//     [-lodnav-scale 1080p|0.75 -lodnav-run <n>]
// nav writes nav-observed.json, capture writes captures/<name>.png + .json, perf
// writes runs/perf-<scale>-run<n>.csv (elapsed_s,frame_ms after a 10 s warm-up).
// Exported positions are glTF world metres (Unity x negated), as the manifest is.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public sealed class BlockWorldNavigation : MonoBehaviour
{
    public string[] objectIds;
    public string[] objectAssets;
    public int[] objectNodes;
    public LODGroup[] objectGroups;
    public string[] slotIds;
    public Vector3[] slotApproach;
    public Transform[] slotTargets;
    public string[] entranceIds;
    public Vector3[] entrancePositions;
    public Material overlayMaterial;
    public Vector2 gridMin = new Vector2(-47f, -13f);
    public Vector2 gridMax = new Vector2(47f, 45f);
    public float cell = 0.1f;
    public float radius = 0.15f;
    public float agentHeight = 1.8f;
    public float step = 0.3f;

    const float MaxFloor = 0.35f, FloorProbe = 3f, Snap = 0.15f, MoveStep = 0.05f, FinalLeg = 1f, SeatHeight = 1.2f, Curb = 0.155f, Skin = 0.01f;
    const float WarmupSeconds = 10f, MeasureSeconds = 60f;
    const int Width = 1920, Height = 1080;
    const int IgnoreRaycast = 2;

    int nx, nz;
    float[] floor;
    bool[] walk;
    string mode, outDir, tree, scaleName = "1080p";
    int run = 1;

    public bool Walkable(int i) { return walk != null && walk[i]; }

    void Start()
    {
        mode = Arg("mode", null);
        if (mode == null) return;
        outDir = Arg("out", ".");
        tree = Arg("tree", "");
        scaleName = Arg("scale", "1080p");
        run = int.Parse(Arg("run", "1"), CultureInfo.InvariantCulture);
        Debug.Log("LOD-NAV mode=" + mode + " development=" + Debug.isDebugBuild + " gpu=" + SystemInfo.graphicsDeviceName);
        StartCoroutine(mode == "nav" ? NavRoutine() : mode == "capture" ? CaptureRoutine() : PerfRoutine());
    }

    static string Arg(string key, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-lodnav-" + key) return args[i + 1];
        return fallback;
    }

    // ------------------------------------------------------------------ grid
    Vector3 Center(int i) { return new Vector3(gridMin.x + (i % nx + 0.5f) * cell, floor[i], gridMin.y + (i / nx + 0.5f) * cell); }

    public void BuildGrid()
    {
        Physics.SyncTransforms();
        nx = Mathf.CeilToInt((gridMax.x - gridMin.x) / cell);
        nz = Mathf.CeilToInt((gridMax.y - gridMin.y) / cell);
        floor = new float[nx * nz];
        walk = new bool[nx * nz];
        var hits = new RaycastHit[32];
        var top = new float[nx * nz];
        int mask = ~(1 << IgnoreRaycast), count = 0;
        for (int i = 0; i < floor.Length; i++)
        {
            float x = gridMin.x + (i % nx + 0.5f) * cell, z = gridMin.y + (i / nx + 0.5f) * cell;
            int n = Physics.RaycastNonAlloc(new Vector3(x, FloorProbe, z), Vector3.down, hits, FloorProbe + 2f, mask, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            top[i] = float.NegativeInfinity;
            for (int h = 0; h < n; h++)
            {
                if (hits[h].normal.y <= 0.7f) continue;
                if (hits[h].point.y <= MaxFloor && hits[h].point.y > best) best = hits[h].point.y;
                if (hits[h].point.y <= MaxFloor + step && hits[h].point.y > top[i]) top[i] = hits[h].point.y;
            }
            if (float.IsNegativeInfinity(best)) continue;
            floor[i] = best;
            walk[i] = !Physics.CheckCapsule(new Vector3(x, best + step + radius, z), new Vector3(x, best + agentHeight - radius, z),
                radius, mask, QueryTriggerInteraction.Ignore);
        }
        // The walker steps onto any ledge in reach (0.2 m of the cell, its radius plus the
        // offset within a cell) that rises more than Curb but within its step; such cells
        // are not walkable, or the walker would leave the floor the route is planned on.
        for (int i = 0; i < walk.Length; i++)
        {
            if (!walk[i]) continue;
            int x = i % nx, z = i / nx;
            for (int dz = -2; dz <= 2 && walk[i]; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int X = x + dx, Z = z + dz;
                    if (X < 0 || Z < 0 || X >= nx || Z >= nz) continue;
                    float rise = top[Z * nx + X] - floor[i];
                    if (rise > Curb && rise <= step + Skin) { walk[i] = false; break; }
                }
            if (walk[i]) count++;
        }
        Debug.Log("LOD-NAV grid " + nx + "x" + nz + " walkable=" + count);
    }

    int Nearest(Vector3 p)
    {
        int cx = Mathf.FloorToInt((p.x - gridMin.x) / cell), cz = Mathf.FloorToInt((p.z - gridMin.y) / cell), best = -1;
        float bestD = Snap;
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (x < 0 || z < 0 || x >= nx || z >= nz || !walk[z * nx + x]) continue;
                float d = Vector3.Distance(Center(z * nx + x), p);
                if (d <= bestD) { bestD = d; best = z * nx + x; }
            }
        return best;
    }

    bool Joined(int a, int b) { return walk[a] && walk[b] && Mathf.Abs(floor[a] - floor[b]) <= step; }

    // Octile Dijkstra from one cell over 8-connected walkable cells; diagonals need both sides open.
    int[] Tree(int start)
    {
        var dist = new float[walk.Length];
        var prev = new int[walk.Length];
        for (int i = 0; i < dist.Length; i++) { dist[i] = float.PositiveInfinity; prev[i] = -1; }
        var heap = new MinHeap();
        dist[start] = 0f;
        heap.Push(start, 0f);
        while (heap.Count > 0)
        {
            float d;
            int i = heap.Pop(out d);
            if (d > dist[i]) continue;
            int x = i % nx, z = i / nx;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int X = x + dx, Z = z + dz;
                    if (X < 0 || Z < 0 || X >= nx || Z >= nz) continue;
                    int j = Z * nx + X;
                    if (!Joined(i, j)) continue;
                    if (dx != 0 && dz != 0 && (!Joined(i, z * nx + X) || !Joined(i, Z * nx + x))) continue;
                    float nd = d + (dx != 0 && dz != 0 ? 1.41421356f : 1f);
                    if (nd < dist[j]) { dist[j] = nd; prev[j] = i; heap.Push(j, nd); }
                }
        }
        return prev;
    }

    // Straight segment stays on walkable, height-joined cells (sampled at quarter cells).
    bool Sight(int a, int b)
    {
        Vector3 p = Center(a), q = Center(b);
        int samples = Mathf.CeilToInt(Vector2.Distance(new Vector2(p.x, p.z), new Vector2(q.x, q.z)) / (cell * 0.25f));
        int last = a;
        for (int s = 1; s <= samples; s++)
        {
            Vector3 m = Vector3.Lerp(p, q, (float)s / samples);
            int x = Mathf.FloorToInt((m.x - gridMin.x) / cell), z = Mathf.FloorToInt((m.z - gridMin.y) / cell);
            if (x < 0 || z < 0 || x >= nx || z >= nz) return false;
            int c = z * nx + x;
            if (c != last && !Joined(last, c)) return false;
            last = c;
        }
        return true;
    }

    public List<Vector3> Route(int[] prev, int start, int goal)
    {
        var cells = new List<int>();
        for (int c = goal; c != -1; c = prev[c]) cells.Add(c);
        if (cells[cells.Count - 1] != start) return null;
        cells.Reverse();
        var corners = new List<Vector3> { Center(start) };
        int anchor = 0;
        for (int k = 1; k < cells.Count; k++)
            if (!Sight(cells[anchor], cells[k])) { anchor = k - 1; corners.Add(Center(cells[anchor])); }
        if (cells.Count > 1) corners.Add(Center(goal));
        return corners;
    }

    // Walks the corners with a CharacterController through the real colliders; null when blocked.
    static List<Vector3> Walk(CharacterController body, List<Vector3> corners, Collider[] passable)
    {
        foreach (var c in passable) Physics.IgnoreCollision(body, c, true);
        try { return WalkCorners(body, corners); }
        finally { foreach (var c in passable) Physics.IgnoreCollision(body, c, false); }
    }

    static List<Vector3> WalkCorners(CharacterController body, List<Vector3> corners)
    {
        body.enabled = false;
        body.transform.position = corners[0];
        body.enabled = true;
        var seen = new List<Vector3> { body.transform.position };
        for (int k = 1; k < corners.Count; k++)
        {
            int stalled = 0;
            for (int guard = 0; ; guard++)
            {
                Vector3 p = body.transform.position, d = corners[k] - p;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist <= 0.005f) break;
                if (guard > 20000 || stalled > 40) return null;
                float s = Mathf.Min(MoveStep, dist);
                body.Move(d / dist * s + Vector3.down * 0.05f);
                Vector3 moved = body.transform.position - p;
                moved.y = 0f;
                stalled = moved.magnitude < s * 0.25f ? stalled + 1 : 0;
            }
            seen.Add(body.transform.position);
        }
        return seen;
    }

    static bool Within(Collider c, Collider[] set) { return Array.IndexOf(set, c) >= 0; }

    // Highest floor under p (upward face at most MaxFloor and Curb above p), or NaN.
    float FloorAt(Vector3 p)
    {
        var hits = Physics.RaycastAll(new Vector3(p.x, FloorProbe, p.z), Vector3.down, FloorProbe + 2f, ~(1 << IgnoreRaycast), QueryTriggerInteraction.Ignore);
        float best = float.NaN;
        foreach (var h in hits)
            if (h.normal.y > 0.7f && h.point.y <= Mathf.Min(MaxFloor, p.y + Curb) && (float.IsNaN(best) || h.point.y > best)) best = h.point.y;
        return best;
    }

    // Walkable cells within FinalLeg of the goal whose straight capsule leg to it, and
    // the goal stance itself, touch only the slot's target colliders; nearest first.
    List<int> FinalLegCells(Vector3 goal, ref Collider[] target)
    {
        var found = new List<KeyValuePair<float, int>>();
        if (target.Length == 0) return new List<int>();
        int mask = ~(1 << IgnoreRaycast);
        Vector3 up = Vector3.up * (agentHeight - 2f * radius - step);
        Vector3 g = goal + Vector3.up * (step + radius);
        var stance = new List<Collider>(target);
        foreach (var c in Physics.OverlapCapsule(g, g + up, radius, mask, QueryTriggerInteraction.Ignore))
            if (!Within(c, target))
            {
                if (c.bounds.max.y - goal.y > SeatHeight) return new List<int>();
                stance.Add(c);
            }
        target = stance.ToArray();
        int span = Mathf.CeilToInt(FinalLeg / cell);
        int cx = Mathf.FloorToInt((goal.x - gridMin.x) / cell), cz = Mathf.FloorToInt((goal.z - gridMin.y) / cell);
        for (int z = cz - span; z <= cz + span; z++)
            for (int x = cx - span; x <= cx + span; x++)
            {
                if (x < 0 || z < 0 || x >= nx || z >= nz || !walk[z * nx + x]) continue;
                Vector3 a = Center(z * nx + x);
                Vector3 d = goal - a;
                d.y = 0f;
                float length = d.magnitude;
                if (length > FinalLeg || length < 1e-4f || Mathf.Abs(a.y - goal.y) > step) continue;
                Vector3 bottom = a + Vector3.up * (step + radius);
                bool clear = true;
                foreach (var h in Physics.CapsuleCastAll(bottom, bottom + up, radius, d / length, length, mask, QueryTriggerInteraction.Ignore))
                    if (!Within(h.collider, target)) { clear = false; break; }
                if (clear) found.Add(new KeyValuePair<float, int>(length, z * nx + x));
            }
        found.Sort((p, q) => p.Key.CompareTo(q.Key));
        return found.ConvertAll(p => p.Value);
    }

    IEnumerator NavRoutine()
    {
        yield return null;
        BuildGrid();
        var walker = new GameObject("NavWalker") { layer = IgnoreRaycast };
        var body = walker.AddComponent<CharacterController>();
        body.radius = radius * 0.9f;
        body.height = agentHeight;
        body.center = Vector3.up * (agentHeight * 0.5f);
        body.stepOffset = step;
        body.slopeLimit = 45f;
        body.skinWidth = 0.01f;
        body.minMoveDistance = 0f;
        var json = new StringBuilder();
        json.Append("{\"tree_hash\":").Append(Q(tree)).Append(",\"development_player\":").Append(Debug.isDebugBuild ? "true" : "false")
            .Append(",\"unity\":").Append(Q(Application.unityVersion)).Append(",\"gpu_device\":").Append(Q(SystemInfo.graphicsDeviceName))
            .Append(",\"grid\":{\"cell_m\":").Append(F(cell)).Append(",\"agent_radius_m\":").Append(F(radius))
            .Append(",\"agent_height_m\":").Append(F(agentHeight)).Append(",\"step_m\":").Append(F(step))
            .Append(",\"min\":[").Append(F(-gridMax.x)).Append(',').Append(F(gridMin.y)).Append("],\"max\":[")
            .Append(F(-gridMin.x)).Append(',').Append(F(gridMax.y)).Append("],\"cells\":[").Append(nx).Append(',').Append(nz).Append("]}")
            .Append(",\"objects\":[");
        for (int i = 0; i < objectIds.Length; i++)
            json.Append(i > 0 ? "," : "").Append("{\"id\":").Append(Q(objectIds[i])).Append(",\"asset\":").Append(Q(objectAssets[i]))
                .Append(",\"node\":").Append(objectNodes[i]).Append(",\"lod_count\":").Append(objectGroups[i].lodCount).Append('}');
        json.Append("],\"paths\":[");
        var goals = new int[slotIds.Length];
        var legs = new List<int>[slotIds.Length];
        var targets = new Collider[slotIds.Length][];
        var goalPoints = new Vector3[slotIds.Length];
        for (int s = 0; s < slotIds.Length; s++)
        {
            goals[s] = Nearest(slotApproach[s]);
            targets[s] = slotTargets[s].GetComponentsInChildren<Collider>(true);
            goalPoints[s] = slotApproach[s];
            float y = FloorAt(slotApproach[s]);
            if (!float.IsNaN(y)) goalPoints[s].y = y;
            legs[s] = !float.IsNaN(y) ? FinalLegCells(goalPoints[s], ref targets[s]) : new List<int>();
        }
        int reached = 0, total = 0;
        for (int e = 0; e < entranceIds.Length; e++)
        {
            int start = Nearest(entrancePositions[e]);
            int[] prev = start >= 0 ? Tree(start) : null;
            for (int s = 0; s < slotIds.Length; s++)
            {
                int goal = goals[s];
                bool leg = false;
                if (goal >= 0 && prev != null && goal != start && prev[goal] == -1) goal = -1;
                if (prev != null && (goal < 0 || goal != start && prev[goal] == -1))
                    foreach (int c in legs[s])
                        if (c == start || prev[c] != -1) { goal = c; leg = true; break; }
                List<Vector3> corners = prev != null && goal >= 0 ? Route(prev, start, goal) : null;
                if (corners != null && leg) corners.Add(goalPoints[s]);
                List<Vector3> seen = corners != null ? Walk(body, corners, leg ? targets[s] : new Collider[0]) : null;
                bool ok = seen != null;
                if (ok) reached++;
                json.Append(total++ > 0 ? "," : "").Append("{\"entrance\":").Append(Q(entranceIds[e])).Append(",\"slot\":").Append(Q(slotIds[s]))
                    .Append(",\"reached\":").Append(ok ? "true" : "false").Append(",\"final_leg\":").Append(leg ? "true" : "false")
                    .Append(",\"start_cell\":").Append(start).Append(",\"goal_cell\":").Append(goal)
                    .Append(",\"corners\":").Append(Points(corners)).Append(",\"positions\":").Append(Points(seen)).Append('}');
            }
            Debug.Log("LOD-NAV entrance " + entranceIds[e] + " start=" + start + " reached_total=" + reached + "/" + total);
            yield return null;
        }
        json.Append("]}\n");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "nav-observed.json"), json.ToString());
        Debug.Log("LOD-NAV nav reached=" + reached + "/" + total);
        Application.Quit(0);
    }

    // ---------------------------------------------------------------- capture
    IEnumerator CaptureRoutine()
    {
        for (int i = 0; i < 30; i++) yield return null;
        var camera = Camera.main;
        var env = Group("env/apartment");
        Vector3 near = env != null ? Bounds(env).center : new Vector3(-30f, 0f, 0f);
        Vector3 block = new Vector3(0f, 0f, 16f);
        string dir = Path.Combine(outDir, "captures");
        Directory.CreateDirectory(dir);
        Shot(camera, dir, "lod-near-overhead-noon", near + new Vector3(0f, 13f, -9f), near, 50f);
        Shot(camera, dir, "lod-mid-overhead-noon", block + new Vector3(0f, 42f, -40f), block, 50f);
        Shot(camera, dir, "lod-far-overhead-noon", block + new Vector3(0f, 105f, -100f), block, 50f);
        foreach (float t in new[] { 0f, 30f, 60f })
        {
            Vector3 position, look;
            PathPose(WarmupSeconds + t, out position, out look);
            Shot(camera, dir, "perf-path-frame-" + ((int)t).ToString("00", CultureInfo.InvariantCulture), position, look, 50f);
        }
        BuildGrid();
        var overlay = Overlay();
        Shot(camera, dir, "navmesh-overhead", block + new Vector3(0f, 78f, -14f), block, 50f);
        Destroy(overlay);
        Application.Quit(0);
    }

    LODGroup Group(string id)
    {
        for (int i = 0; i < objectIds.Length; i++) if (objectIds[i] == id) return objectGroups[i];
        return null;
    }

    static Bounds Bounds(LODGroup group)
    {
        var renderers = group.GetComponentsInChildren<Renderer>();
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    // Walkable cells as merged row strips 3 cm above their floor.
    GameObject Overlay()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x;
                if (!walk[i]) continue;
                int end = x;
                while (end + 1 < nx && walk[i + end + 1 - x] && Mathf.Abs(floor[i + end + 1 - x] - floor[i]) < 0.01f) end++;
                float x0 = gridMin.x + x * cell, x1 = gridMin.x + (end + 1) * cell, z0 = gridMin.y + z * cell, z1 = z0 + cell, y = floor[i] + 0.03f;
                int v = vertices.Count;
                vertices.Add(new Vector3(x0, y, z0)); vertices.Add(new Vector3(x0, y, z1));
                vertices.Add(new Vector3(x1, y, z1)); vertices.Add(new Vector3(x1, y, z0));
                triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
                x = end;
            }
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        var holder = new GameObject("NavOverlay");
        holder.AddComponent<MeshFilter>().sharedMesh = mesh;
        holder.AddComponent<MeshRenderer>().sharedMaterial = overlayMaterial;
        return holder;
    }

    void Shot(Camera camera, string dir, string name, Vector3 position, Vector3 look, float fov)
    {
        camera.transform.position = position;
        camera.transform.LookAt(look);
        camera.fieldOfView = fov;
        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        rt.Create();
        var previous = camera.targetTexture;
        camera.targetTexture = rt;
        camera.aspect = (float)Width / Height;
        camera.Render();
        RenderTexture.active = rt;
        var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        texture.Apply();
        RenderTexture.active = null;
        camera.targetTexture = previous;
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), texture.EncodeToPNG());
        Destroy(texture);
        rt.Release();
        var levels = new StringBuilder();
        int[] perLevel = new int[3];
        for (int i = 0; i < objectGroups.Length; i++)
        {
            int level = EstimatedLevel(camera, objectGroups[i]);
            if (level >= 0 && level < perLevel.Length) perLevel[level]++;
            levels.Append(i > 0 ? "," : "").Append(Q(objectIds[i])).Append(':').Append(level);
        }
        File.WriteAllText(Path.Combine(dir, name + ".json"), "{\"camera\":{\"space\":\"unity_world\",\"position\":" + V(position)
            + ",\"look_at\":" + V(look) + ",\"fov\":" + F(fov) + "},\"light_preset\":\"noon\",\"seed\":" + MainDirector.Seed
            + ",\"resolution\":[" + Width + "," + Height + "],\"tool\":" + Q("Unity " + Application.unityVersion
            + " development player RenderTexture capture, BlockWorldNavigation capture mode")
            + ",\"tree_hash\":" + Q(tree) + ",\"development_player\":" + (Debug.isDebugBuild ? "true" : "false")
            + ",\"gpu_device\":" + Q(SystemInfo.graphicsDeviceName) + ",\"lod_bias\":" + F(QualitySettings.lodBias)
            + ",\"estimated_lod_counts\":[" + perLevel[0] + "," + perLevel[1] + "," + perLevel[2] + "]"
            + ",\"estimated_lod_levels\":{" + levels + "}}\n");
        Debug.Log("LOD-NAV capture " + name + " lod_counts=" + perLevel[0] + "," + perLevel[1] + "," + perLevel[2]);
    }

    // LODGroup selection estimate: world size over the frustum height at the reference point, times lodBias.
    static int EstimatedLevel(Camera camera, LODGroup group)
    {
        Vector3 s = group.transform.lossyScale;
        float size = group.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        float distance = Vector3.Distance(camera.transform.position, group.transform.TransformPoint(group.localReferencePoint));
        float relative = size / (2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad)) * QualitySettings.lodBias;
        var lods = group.GetLODs();
        for (int i = 0; i < lods.Length; i++) if (relative >= lods[i].screenRelativeTransitionHeight) return i;
        return lods.Length;
    }

    // ------------------------------------------------------------- performance
    // 70 s loop over the block: one slow orbit with the height easing between 18 m and 62 m.
    static void PathPose(float t, out Vector3 position, out Vector3 look)
    {
        float a = t / (WarmupSeconds + MeasureSeconds) * Mathf.PI * 2f;
        look = new Vector3(Mathf.Sin(a) * 12f, 0f, 16f + Mathf.Cos(a) * 8f);
        float height = 40f + 22f * Mathf.Sin(a * 2f);
        position = new Vector3(Mathf.Sin(a + 0.6f) * 48f, height, 16f - Mathf.Cos(a + 0.6f) * 48f);
    }

    IEnumerator PerfRoutine()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.SetResolution(Width, Height, false);
        float scale = scaleName == "0.75" ? 0.75f : 1f;
        var urp = UniversalRenderPipeline.asset;
        if (urp != null) urp.renderScale = scale;
        var camera = Camera.main;
        // Exact 1920x1080 target as E1 measured it, independent of the desktop window size.
        var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        target.Create();
        camera.targetTexture = target;
        camera.aspect = (float)Width / Height;
        camera.fieldOfView = 50f;
        var rows = new StringBuilder("elapsed_s,frame_ms\n");
        double start = Time.realtimeSinceStartupAsDouble;
        int samples = 0;
        while (true)
        {
            double t = Time.realtimeSinceStartupAsDouble - start;
            Vector3 position, look;
            PathPose((float)t, out position, out look);
            camera.transform.position = position;
            camera.transform.LookAt(look);
            yield return null;
            t = Time.realtimeSinceStartupAsDouble - start;
            if (t < WarmupSeconds) continue;
            rows.Append(t.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append((Time.unscaledDeltaTime * 1000f).ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            samples++;
            if (t >= WarmupSeconds + MeasureSeconds + 0.5) break;
        }
        string dir = Path.Combine(outDir, "runs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "perf-" + scaleName + "-run" + run + ".csv"), rows.ToString());
        Debug.Log("LOD-NAV perf scale=" + scaleName + " run=" + run + " samples=" + samples + " render_scale="
            + (urp != null ? urp.renderScale : -1f) + " screen=" + Screen.width + "x" + Screen.height);
        Application.Quit(0);
    }

    // ------------------------------------------------------------------- json
    static string F(float v) { return ((double)v).ToString("0.####", CultureInfo.InvariantCulture); }
    static string V(Vector3 v) { return "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]"; }
    static string G(Vector3 v) { return "[" + F(-v.x) + "," + F(v.y) + "," + F(v.z) + "]"; }
    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    static string Points(List<Vector3> points)
    {
        if (points == null) return "[]";
        var parts = new List<string>();
        foreach (var p in points) parts.Add(G(p));
        return "[" + string.Join(",", parts) + "]";
    }

    sealed class MinHeap
    {
        readonly List<int> items = new List<int>();
        readonly List<float> keys = new List<float>();
        public int Count { get { return items.Count; } }

        public void Push(int item, float key)
        {
            items.Add(item); keys.Add(key);
            for (int i = items.Count - 1; i > 0;)
            {
                int parent = (i - 1) / 2;
                if (keys[parent] <= keys[i]) break;
                Swap(i, parent); i = parent;
            }
        }

        public int Pop(out float key)
        {
            int top = items[0];
            key = keys[0];
            int last = items.Count - 1;
            Swap(0, last);
            items.RemoveAt(last); keys.RemoveAt(last);
            for (int i = 0; ;)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < items.Count && keys[l] < keys[m]) m = l;
                if (r < items.Count && keys[r] < keys[m]) m = r;
                if (m == i) break;
                Swap(i, m); i = m;
            }
            return top;
        }

        void Swap(int a, int b)
        {
            int t = items[a]; items[a] = items[b]; items[b] = t;
            float k = keys[a]; keys[a] = keys[b]; keys[b] = k;
        }
    }
}
