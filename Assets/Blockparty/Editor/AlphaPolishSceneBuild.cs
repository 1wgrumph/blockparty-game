using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// POLISH-WORLD (#216, owner M5-OD-24): world dressing pass for Alpha.unity.
// AlphaSceneBuild.BuildScene calls Apply after the greybox block, the four
// BLOCK-SITES buildings and the GYM-TRAIL kit exist. It changes only how the
// world looks: warm sun, trilight ambient, haze, a stylised sky, a global
// post volume (tonemapping, gentle bloom, warm grade, vignette), stylised
// materials on the ground, paths and buildings, plus decoration (kerbs and
// road markings, windows, door trims, signs, roofs, trees, bushes, flowers,
// lamps, benches, soft contact shadows, distant hills). Nothing moves, no
// simulation object is touched and no decoration has a collider, so resident
// selection raycasts, navigation and stage cameras behave as before.
// WORLD-FIXES (#216): the open kit buildings get walls and roofs
// (AlphaRoofSceneBuild, with RoofReveal hiding roofs on close zoom), and the
// paths, trail and road become one rounded network (AlphaPathSceneBuild).
// Deterministic: one seeded RNG, fixed placement rules.
public static class AlphaPolishSceneBuild
{
    const string Dir = "Assets/Blockparty/Environment/Polish";
    const int Seed = 216;

    static Shader worldShader;
    static Shader contactShader;
    static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();
    static readonly List<Rect> Occupied = new List<Rect>();
    static readonly List<Rect> Dressed = new List<Rect>();
    static Transform[] roots;
    static System.Random rng;
    static Transform root;
    static Transform block;
    static Mesh blob;
    static Mesh cone;
    static Texture2D grassTex, paverTex, asphaltTex, sidingTex, roofTex, signTex;
    // Row order of polish-signs.png (make_textures.py SIGN_ROWS).
    static readonly string[] SignRows = { "CAFE", "WORKSHOP", "OFFICE", "SCHOOL", "GROCERY", "REC CENTER", "APARTMENTS", "COMMONS" };
    static int placedTrees, placedBushes, placedFlowers;

    static Color H(string hex) { Color c; ColorUtility.TryParseHtmlString(hex, out c); return c; }
    static float R(float a, float b) { return a + (float)rng.NextDouble() * (b - a); }

    public static void Apply(Transform world, Camera camera, Light sun)
    {
        Mats.Clear();
        Occupied.Clear();
        Dressed.Clear();
        rng = new System.Random(Seed);
        placedTrees = placedBushes = placedFlowers = 0;
        worldShader = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/StylizedWorld.shader");
        contactShader = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/SoftContact.shader");
        var skyShader = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/PolishSky.shader");
        if (worldShader == null || contactShader == null || skyShader == null)
            throw new System.Exception("POLISH_BUILD missing shaders under " + Dir);
        grassTex = Tex("polish-grass.png");
        paverTex = Tex("polish-pavers.png");
        asphaltTex = Tex("polish-asphalt.png");
        sidingTex = Tex("polish-siding.png");
        roofTex = Tex("polish-roof.png");
        signTex = Tex("polish-signs.png");
        blob = Icosphere();
        cone = Cone(10);

        root = new GameObject("PolishWorld").transform;
        root.SetParent(world, false);
        // The BLOCK-SITES buildings and the GYM-TRAIL kit sit at the scene root.
        var top = SceneManager.GetActiveScene().GetRootGameObjects();
        roots = System.Array.ConvertAll(top, g => g.transform);
        block = null;
        foreach (var t in roots) if (t.name == "GymTrailExtension") block = t.Find("block");

        // WORLD-FIXES: outer walls and roofs for the open kit buildings first,
        // so restyling, dressing and placement treat them like every other building.
        AlphaRoofSceneBuild.Shells(block);
        Lighting(camera, sun, skyShader);
        RestyleExisting(world);
        Ground(world);
        Roads(world);
        Buildings(world);
        AlphaRoofSceneBuild.Reveal(world, root, block, camera);
        Planting();
        Horizon();

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
        Debug.Log("POLISH_BUILD trees=" + placedTrees + " bushes=" + placedBushes + " flowers=" + placedFlowers
            + " materials=" + Mats.Count + " objects=" + root.GetComponentsInChildren<Renderer>(true).Length);
    }

    // ------------------------------------------------------------ lighting
    static void Lighting(Camera camera, Light sun, Shader skyShader)
    {
        // Lit faces land near albedo and shade near half of it: the low
        // lit/shade contrast of a cartoon life sim, without blowing out.
        sun.colorTemperature = 5200f;
        sun.color = H("#fff6ea");
        sun.intensity = 0.45f;
        sun.shadowStrength = 0.62f;
        // From the south-south-east: the fronts the block cameras see are lit
        // and shadows fall behind buildings, as in the approved target.
        sun.transform.rotation = Quaternion.Euler(46f, -20f, 0f);
        // Interior fills were tuned for URP Lit under Neutral tonemapping; the
        // wrap-lit shader without a filmic shoulder needs under half.
        foreach (string fill in new[] { "HomeFill", "CafeFill", "SchoolFill" })
        {
            var light = Child(sun.transform.parent, fill).GetComponent<Light>();
            light.intensity *= 0.45f;
        }
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = H("#d0dbd4");
        RenderSettings.ambientEquatorColor = H("#dcd6c8");
        RenderSettings.ambientGroundColor = H("#9a9674");
        RenderSettings.ambientIntensity = 0.85f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = H("#f1e6cf");
        RenderSettings.fogStartDistance = 100f;
        RenderSettings.fogEndDistance = 560f;
        var sky = new Material(skyShader) { name = "Polish_Sky" };
        RenderSettings.skybox = sky;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.FastApproximateAntialiasing;

        string path = Dir + "/PolishVolumeProfile.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
        }
        // Update components in place so rebuilds keep the asset's sub-object ids.
        var tone = Component<Tonemapping>(profile);
        // No filmic curve: lit faces land on the palette colours themselves.
        tone.mode.Override(TonemappingMode.None);
        var adjust = Component<ColorAdjustments>(profile);
        adjust.postExposure.Override(0f);
        adjust.contrast.Override(6f);
        adjust.saturation.Override(6f);
        adjust.colorFilter.Override(H("#fffaf2"));
        var balance = Component<WhiteBalance>(profile);
        balance.temperature.Override(3f);
        var bloom = Component<Bloom>(profile);
        bloom.threshold.Override(1.0f);
        bloom.intensity.Override(0.32f);
        bloom.scatter.Override(0.65f);
        bloom.tint.Override(H("#ffe9c8"));
        bloom.downscale.Override(BloomDownscaleMode.Quarter);
        bloom.maxIterations.Override(5);
        var vignette = Component<Vignette>(profile);
        vignette.intensity.Override(0.24f);
        vignette.smoothness.Override(0.5f);
        vignette.color.Override(H("#243238"));
        foreach (var component in profile.components) EditorUtility.SetDirty(component);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        // Priority above AlphaDirector's runtime volume (priority 0), so these
        // overrides win where both set a value.
        var volume = new GameObject("PolishVolume").AddComponent<Volume>();
        volume.transform.SetParent(root, false);
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.sharedProfile = profile;
    }

    static T Component<T>(VolumeProfile profile) where T : VolumeComponent
    {
        T component;
        if (profile.TryGet(out component)) return component;
        component = profile.Add<T>(true);
        component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    // ------------------------------------------------------------ materials
    static Texture2D Tex(string name)
    {
        string path = Dir + "/Textures/" + name;
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new System.Exception("POLISH_BUILD missing texture " + path);
        if (importer.anisoLevel != 4 || importer.wrapMode != TextureWrapMode.Repeat)
        {
            importer.anisoLevel = 4;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Material Mat(string key, Color color, Texture2D tex = null, float tile = 0f,
        float varStrength = 0f, Color? varTint = null, float varScale = 6f, float sway = 0f, Color? emission = null)
    {
        Material m;
        if (Mats.TryGetValue(key, out m)) return m;
        m = new Material(worldShader) { name = "Polish_" + key, enableInstancing = true };
        m.SetColor("_BaseColor", color);
        if (tex != null) m.SetTexture("_BaseMap", tex);
        m.SetFloat("_WorldTile", tile);
        m.SetFloat("_VarStrength", varStrength);
        m.SetColor("_VarColor", varTint ?? H("#e6efd8"));
        m.SetFloat("_VarScale", varScale);
        m.SetFloat("_Sway", sway);
        m.SetColor("_EmissionColor", emission ?? Color.black);
        m.SetColor("_ShadeColor", H("#eeeef8"));
        m.SetFloat("_Wrap", 0.5f);
        Mats[key] = m;
        return m;
    }

    static Material Contact(string key, float box, Color shade)
    {
        Material m;
        if (Mats.TryGetValue(key, out m)) return m;
        m = new Material(contactShader) { name = "Polish_" + key, enableInstancing = true };
        m.SetFloat("_Box", box);
        m.SetColor("_ShadowColor", shade);
        m.SetFloat("_Softness", box > 0.5f ? 0.7f : 0.6f);
        Mats[key] = m;
        return m;
    }

    static readonly Color Leaf1 = H("#5d8f4a"), Leaf2 = H("#6ea456"), Leaf3 = H("#7fb562"), Pine = H("#4c7a45");
    static readonly Color Bark = H("#7a5638"), Ivory = H("#f4ebdd"), Charcoal = H("#243238"), Teal = H("#3f777b");
    static readonly Color Rust = H("#ad5737"), Honey = H("#b88a58"), Kerb = H("#e3d8c4"), Cream = H("#fff4d4");
    static readonly Color[] Blooms = { H("#f6a7b0"), H("#f2b24c"), H("#fff4d4"), H("#f08b6e"), H("#c9a7e8") };
    // Curated per-building wall and roof colours from the target palette, so
    // neighbours never repeat; unknown buildings fall back to ivory/rust.
    static readonly Dictionary<string, string[]> Palette = new Dictionary<string, string[]>
    {
        { "Building_cafe", new[] { "#f4ebdd", "#3f777b" } },
        { "Building_workshop", new[] { "#f3d6c8", "#c27a4a" } },
        { "Building_office", new[] { "#dfe6cf", "#7c6a5a" } },
        { "Building_school", new[] { "#f6e3b4", "#c9973a" } },
        { "grocery", new[] { "#cfe1dc", "#4f8a6e" } },
        { "recreation-center", new[] { "#f4ebdd", "#9b4a2e" } },
        { "common-room", new[] { "#e9dccb", "#405a70" } },
        { "apartment", new[] { "#f3e4cf", "#ad5737" } },
        { "oak-house", new[] { "#f6e3b4", "#ad5737" } },
        { "willow-house", new[] { "#cfe1dc", "#405a70" } },
    };

    static Color Jitter(Color c, float amount)
    {
        float k = 1f + R(-amount, amount);
        return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k));
    }

    // ------------------------------------------------------------ primitives
    static GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale,
        Material material, bool shadows = true, Quaternion? rotation = null)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.rotation = rotation ?? Quaternion.identity;
        go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        return go;
    }

    static GameObject MeshPart(Mesh mesh, string name, Transform parent, Vector3 position, Vector3 scale,
        Material material, bool shadows = true, Quaternion? rotation = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.rotation = rotation ?? Quaternion.identity;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        return go;
    }

    static Transform Group(string name, Transform parent = null)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent ?? root, false);
        return t;
    }

    static void ContactShadow(Transform parent, Vector3 center, Vector2 size, bool box, float y = 0.04f)
    {
        var q = Part(PrimitiveType.Quad, "Contact", parent, new Vector3(center.x, y, center.z),
            new Vector3(size.x, size.y, 1f), Contact(box ? "contact-box" : "contact", box ? 1f : 0f,
                box ? H("#c4beb2") : H("#ada697")), false, Quaternion.Euler(90f, 0f, 0f));
        q.GetComponent<MeshRenderer>().receiveShadows = false;
    }

    // Low-poly sphere (icosahedron, one subdivision, smooth normals): 80 tris.
    static Mesh Icosphere()
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var v = new List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        int[] f =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        var mid = new Dictionary<long, int>();
        var tris = new List<int>();
        System.Func<int, int, int> M = (a, b) =>
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int index;
            if (mid.TryGetValue(key, out index)) return index;
            v.Add((v[a] + v[b]) * 0.5f);
            mid[key] = v.Count - 1;
            return v.Count - 1;
        };
        for (int i = 0; i < f.Length; i += 3)
        {
            int a = f[i], b = f[i + 1], c = f[i + 2];
            int ab = M(a, b), bc = M(b, c), ca = M(c, a);
            tris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
        }
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized * 0.5f;
        var mesh = new Mesh { name = "PolishBlob" };
        mesh.SetVertices(v);
        mesh.SetTriangles(tris, 0);
        mesh.SetNormals(v.ConvertAll(p => p.normalized));
        mesh.SetUVs(0, v.ConvertAll(p => new Vector2(p.x + 0.5f, p.z + 0.5f)));
        mesh.RecalculateBounds();
        return mesh;
    }

    // Unit cone: base radius 0.5 at y=0, apex at y=1.
    static Mesh Cone(int sides)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
            var p0 = new Vector3(Mathf.Cos(a0) * 0.5f, 0f, Mathf.Sin(a0) * 0.5f);
            var p1 = new Vector3(Mathf.Cos(a1) * 0.5f, 0f, Mathf.Sin(a1) * 0.5f);
            int b = verts.Count;
            verts.Add(p0); verts.Add(Vector3.up); verts.Add(p1);
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            int c = verts.Count;
            verts.Add(p1); verts.Add(Vector3.zero); verts.Add(p0);
            tris.Add(c); tris.Add(c + 1); tris.Add(c + 2);
        }
        var mesh = new Mesh { name = "PolishCone" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.SetUVs(0, verts.ConvertAll(p => new Vector2(p.x + 0.5f, p.y)));
        mesh.RecalculateBounds();
        return mesh;
    }

    // Gable roof over a w (x) by d (z) footprint, ridge along x, slopes in
    // submesh 0 (metre UVs for the shingle texture), gable ends in submesh 1.
    static Mesh Gable(float w, float d, float h)
    {
        float x = w * 0.5f, z = d * 0.5f;
        float slope = Mathf.Sqrt(z * z + h * h);
        var v = new List<Vector3>
        {
            new Vector3(-x, 0, -z), new Vector3(x, 0, -z), new Vector3(x, h, 0), new Vector3(-x, h, 0),
            new Vector3(x, 0, z), new Vector3(-x, 0, z), new Vector3(-x, h, 0), new Vector3(x, h, 0),
            new Vector3(-x, 0, z), new Vector3(-x, 0, -z), new Vector3(-x, h, 0),
            new Vector3(x, 0, -z), new Vector3(x, 0, z), new Vector3(x, h, 0),
        };
        var uv = new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, slope), new Vector2(0, slope),
            new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, slope), new Vector2(0, slope),
            new Vector2(0, 0), new Vector2(d, 0), new Vector2(d * 0.5f, h),
            new Vector2(0, 0), new Vector2(d, 0), new Vector2(d * 0.5f, h),
        };
        var mesh = new Mesh { name = "PolishGable" };
        mesh.SetVertices(v);
        mesh.SetUVs(0, uv);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(new[] { 0, 3, 2, 0, 2, 1, 4, 7, 6, 4, 6, 5 }, 0);
        mesh.SetTriangles(new[] { 8, 10, 9, 11, 13, 12 }, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Ring of low rolling hills around the block, faded by fog.
    static Mesh HillRing(Vector2 center)
    {
        const int n = 120;
        float[] radii = { 150f, 185f, 215f, 260f };
        var v = new List<Vector3>();
        var tris = new List<int>();
        for (int i = 0; i <= n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            float hgt = 11f + 6f * Mathf.Sin(3f * a + 1f) + 4f * Mathf.Sin(7f * a + 2f) + 2f * Mathf.Sin(13f * a + 0.5f);
            float[] ys = { -0.2f, hgt * 0.75f, hgt, hgt * 0.35f };
            for (int k = 0; k < radii.Length; k++)
                v.Add(new Vector3(center.x + Mathf.Cos(a) * radii[k], ys[k], center.y + Mathf.Sin(a) * radii[k]));
        }
        int rk = radii.Length;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < rk - 1; k++)
            {
                int a = i * rk + k, b = (i + 1) * rk + k;
                tris.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            }
        var mesh = new Mesh { name = "PolishHills" };
        mesh.SetVertices(v);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.SetUVs(0, v.ConvertAll(p => new Vector2(p.x, p.z)));
        mesh.RecalculateBounds();
        return mesh;
    }

    // ------------------------------------------------------------ restyle
    static Transform Child(Transform world, string path)
    {
        var t = world.Find(path);
        if (t == null) throw new System.Exception("POLISH_BUILD missing " + path);
        return t;
    }

    static bool SourceColor(Material m, out Color c)
    {
        c = Color.white;
        if (m == null || m.renderQueue >= 2450) return false;
        foreach (string tex in new[] { "baseColorTexture", "_BaseMap", "_MainTex" })
            if (m.HasProperty(tex) && m.GetTexture(tex) != null) return false;
        if (m.HasProperty("baseColorFactor")) c = m.GetColor("baseColorFactor");
        else if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
        else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
        else return false;
        return c.a > 0.99f;
    }

    static void Restyle(Renderer r, string variant = null, Color? tint = null, Texture2D tex = null, float tile = 0f)
    {
        var mats = r.sharedMaterials;
        bool changed = false;
        for (int i = 0; i < mats.Length; i++)
        {
            Color c;
            if (!SourceColor(mats[i], out c)) continue;
            Color final = tint ?? c;
            string key = (variant ?? "re") + "-" + ColorUtility.ToHtmlStringRGB(final) + (tex != null ? tex.name : "");
            mats[i] = Mat(key, final, tex, tile, tex == null ? 0.25f : 0f, H("#f2ead8"), 3f);
            changed = true;
        }
        if (changed) r.sharedMaterials = mats;
    }

    // Unify the existing greybox props and kit interiors under the stylised
    // wrap-lit shader, keeping each surface's own colour. Residents, the rider
    // and the bicycle (character and prop art) keep their materials.
    static List<T> All<T>() where T : Component
    {
        var list = new List<T>();
        foreach (var t in roots) list.AddRange(t.GetComponentsInChildren<T>(true));
        return list;
    }

    static void RestyleExisting(Transform world)
    {
        foreach (var r in All<MeshRenderer>())
        {
            var t = r.transform;
            if (t.IsChildOf(root)) continue;
            string path = Path(t, null);
            if (path.StartsWith("AlphaWorld/Residents") || path.Contains("female-Adult") || path.Contains("PROP_Bike")
                || path.Contains("HeldDumbbell")) continue;
            Restyle(r);
        }
    }

    static string Path(Transform t, Transform stop)
    {
        string p = t.name;
        while (t.parent != null && t.parent != stop) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    // ------------------------------------------------------------ ground
    static void Ground(Transform world)
    {
        var grass = Mat("grass", Color.white, grassTex, 6f, 0.6f, H("#cfe0b6"), 11f);
        Child(world, "Ground").GetComponent<Renderer>().sharedMaterial = grass;
        var outer = Part(PrimitiveType.Plane, "OuterGround", root, new Vector3(15f, -0.09f, 15f),
            new Vector3(70f, 1f, 70f), grass, false);
        outer.GetComponent<MeshRenderer>().receiveShadows = true;
        var pavers = Mat("pavers", Color.white, paverTex, 2f);
        foreach (string n in new[] { "CrossPath", "HomeAPath", "HomeBPath" })
            Child(world, n).GetComponent<Renderer>().sharedMaterial = pavers;
        foreach (var site in new[] { "cafe", "workshop", "office", "school" })
            Child(world, "Sites/Mat_" + site).GetComponent<Renderer>().sharedMaterial = pavers;
        // Bike trail: soft terracotta pavers instead of flat orange slabs.
        var trail = Mat("trail", H("#f0c9a4"), paverTex, 2f);
        var mark = Mat("trail-mark", Cream);
        foreach (var r in All<MeshRenderer>())
        {
            if (r.name.EndsWith("_pavement")) r.sharedMaterial = trail;
            else if (r.name.Contains("_lane_mark")) r.sharedMaterial = mark;
        }
        // Everything already standing occupies its footprint for placement.
        foreach (var r in All<Renderer>())
        {
            if (r.transform.IsChildOf(root) || r.name == "Ground") continue;
            var b = r.bounds;
            if (b.size.x > 60f || b.size.z > 60f) continue;
            Occupied.Add(new Rect(b.min.x - 0.6f, b.min.z - 0.6f, b.size.x + 1.2f, b.size.z + 1.2f));
        }
    }

    // ------------------------------------------------------------ roads
    static Rect XZ(Bounds b) { return new Rect(b.min.x, b.min.z, b.size.x, b.size.z); }

    // The original strip keeps its object (and any collider); only its drawing stops.
    static Rect Retire(Renderer r) { r.enabled = false; return XZ(r.bounds); }

    // WORLD-FIXES: the square strips become one rounded network
    // (AlphaPathSceneBuild); paint, lamps and benches follow the new road.
    static void Roads(Transform world)
    {
        var group = Group("Street");
        var pavers = new List<Rect>();
        var trails = new List<Rect>();
        var plazas = new List<Vector2[]>();
        foreach (string n in new[] { "CrossPath", "HomeAPath", "HomeBPath" })
            pavers.Add(Retire(Child(world, n).GetComponent<Renderer>()));
        // The site mats sit on the glazed north sides, away from every door and
        // path; drawn alone they read as broken path pieces, so they retire too.
        foreach (string n in new[] { "Sites/Mat_cafe", "Sites/Mat_workshop", "Sites/Mat_office", "Sites/Mat_school" })
            Retire(Child(world, n).GetComponent<Renderer>());
        Retire(Child(world, "Asphalt").GetComponent<Renderer>());
        foreach (var r in All<MeshRenderer>())
        {
            if (r.name.EndsWith("_pavement")) trails.Add(Retire(r));
            else if (r.name == "cy_floor" || r.name == "pk_floor")
            {
                // The plaza's own triangles: the courtyard floor is notched around the office.
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                var v = mesh.vertices;
                var t = mesh.triangles;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var tri = new Vector2[3];
                    for (int c = 0; c < 3; c++)
                    {
                        var w = r.transform.TransformPoint(v[t[k + c]]);
                        tri[c] = new Vector2(w.x, w.z);
                    }
                    plazas.Add(tri);
                }
            }
        }
        var footprints = AlphaRoofSceneBuild.Areas(world, block).ConvertAll(a => a.rect);
        var paths = AlphaPathSceneBuild.Build(Group("Paths", group), pavers, trails, plazas, footprints,
            Mat("pavers", Color.white, paverTex, 2f), Mat("trail", H("#f0c9a4"), paverTex, 2f),
            Mat("asphalt", Color.white, asphaltTex, 4f), Mat("kerb", Kerb));
        // Trail centre dashes stay only where trail surface is drawn, just above it.
        foreach (var r in All<MeshRenderer>())
        {
            if (!r.name.Contains("_lane_mark")) continue;
            var b = r.bounds;
            bool on = AlphaPathSceneBuild.OnTrail(b.min.x, b.min.z) && AlphaPathSceneBuild.OnTrail(b.max.x, b.max.z);
            if (on) r.transform.position += new Vector3(0f, 0.024f - b.max.y, 0f);
            else r.enabled = false;
        }
        var paint = Mat("paint", H("#f7f0e2"));
        float x0 = AlphaPathSceneBuild.RoadX - AlphaPathSceneBuild.RoadHalf, x1 = AlphaPathSceneBuild.RoadX + AlphaPathSceneBuild.RoadHalf;
        float zMin = AlphaPathSceneBuild.RoadZMin, zMax = AlphaPathSceneBuild.RoadZMax;
        System.Func<float, bool> open = z =>
        {
            foreach (var c in paths.Crossings) if (z > c.yMin - 0.15f && z < c.yMax + 0.15f) return false;
            return true;
        };
        System.Func<Vector3, float, bool> built = (p, grow) => AlphaPathSceneBuild.Inside(footprints, p, grow);
        for (float z = zMin + 1.5f; z < zMax - 1f; z += 3f)
            if (open(z - 0.7f) && open(z + 0.7f))
                Part(PrimitiveType.Cube, "CentreDash", group, new Vector3(AlphaPathSceneBuild.RoadX, 0.038f, z), new Vector3(0.14f, 0.02f, 1.4f), paint, false);
        foreach (var c in paths.Crossings)
        {
            float mid = c.center.y, len = Mathf.Min(2.6f, c.height - 0.7f);
            if (mid < zMin || mid > zMax || len < 0.6f) continue;
            for (float x = x0 + 0.35f; x < x1 - 0.2f; x += 0.7f)
            {
                if (built(new Vector3(x, 0f, mid - len * 0.5f), 0.25f) || built(new Vector3(x, 0f, mid + len * 0.5f), 0.25f)) continue;
                Part(PrimitiveType.Cube, "Zebra", group, new Vector3(x, 0.038f, mid), new Vector3(0.38f, 0.02f, len), paint, false);
            }
        }
        // Lamps and street furniture on the sidewalks, clear of crossings and buildings.
        var pole = Mat("lamp-pole", Charcoal);
        var glow = Mat("lamp-glow", H("#ffe9a8"), null, 0f, 0f, null, 6f, 0f, H("#ffd27a") * 1.4f);
        int lamp = 0;
        for (float z = zMin + 3f; z < zMax - 2f; z += 9f, lamp++)
        {
            float side = lamp % 2 == 0 ? x0 - 1.35f : x1 + 1.35f;
            if (!open(z) || built(new Vector3(side, 0f, z), 0.6f)) continue;
            float arm = lamp % 2 == 0 ? 0.35f : -0.35f;
            var p = Group("Lamp", group);
            Part(PrimitiveType.Cylinder, "Pole", p, new Vector3(side, 1.6f, z), new Vector3(0.12f, 1.6f, 0.12f), pole);
            Part(PrimitiveType.Cube, "Arm", p, new Vector3(side + arm * 0.5f, 3.15f, z), new Vector3(0.42f, 0.06f, 0.08f), pole, false);
            MeshPart(blob, "Globe", p, new Vector3(side + arm, 3.0f, z), Vector3.one * 0.36f, glow, false);
            Part(PrimitiveType.Cylinder, "Base", p, new Vector3(side, 0.12f, z), new Vector3(0.24f, 0.12f, 0.24f), pole, false);
            Occupied.Add(new Rect(side - 0.5f, z - 0.5f, 1f, 1f));
        }
        var wood = Mat("bench-wood", Rust);
        for (float z = zMin + 7.5f; z < zMax - 2f; z += 13f)
        {
            if (!open(z) || !open(z + 1f) || !open(z - 1f) || built(new Vector3(x1 + 1.45f, 0f, z), 1f)) continue;
            Bench(group, new Vector3(x1 + 1.45f, 0f, z), 90f, wood, pole);
        }
        // Road, kerbs, sidewalks and the new links occupy their ground too.
        float reach = AlphaPathSceneBuild.RoadHalf + AlphaPathSceneBuild.KerbWidth + AlphaPathSceneBuild.WalkWidth + 0.1f;
        Occupied.Add(Rect.MinMaxRect(AlphaPathSceneBuild.RoadX - reach, zMin - reach, AlphaPathSceneBuild.RoadX + reach, zMax + reach));
        foreach (var l in AlphaPathSceneBuild.Links) Occupied.Add(Rect.MinMaxRect(l.xMin - 0.6f, l.yMin - 0.6f, l.xMax + 0.6f, l.yMax + 0.6f));
    }

    static void Bench(Transform parent, Vector3 at, float yaw, Material wood, Material metal)
    {
        var b = Group("Bench", parent);
        b.position = at;
        b.rotation = Quaternion.Euler(0f, yaw, 0f);
        var q = b.rotation;
        Part(PrimitiveType.Cube, "Seat", b, at + q * new Vector3(0f, 0.45f, 0f), new Vector3(1.5f, 0.08f, 0.45f), wood, true, q);
        Part(PrimitiveType.Cube, "Back", b, at + q * new Vector3(0f, 0.78f, 0.2f), new Vector3(1.5f, 0.32f, 0.06f), wood, true, q);
        foreach (float s in new[] { -0.62f, 0.62f })
            Part(PrimitiveType.Cube, "Leg", b, at + q * new Vector3(s, 0.22f, 0.02f), new Vector3(0.07f, 0.44f, 0.4f), metal, false, q);
        Occupied.Add(new Rect(at.x - 1f, at.z - 1f, 2f, 2f));
    }

    // ------------------------------------------------------------ buildings
    struct Wall { public Renderer r; public Bounds b; public bool alongX; }

    static void Buildings(Transform world)
    {
        var group = Group("Facades");
        int index = 0;
        foreach (string home in new[] { "HomeA", "HomeB" })
        {
            Home(group, Child(world, "Homes/" + home), index);
            index++;
        }
        Transform kit = null;
        foreach (var t in roots) if (t.name == "GymTrailExtension") kit = t;
        var buildings = new List<KeyValuePair<string, Transform>>();
        foreach (var site in new[] { "cafe", "workshop", "office", "school" })
        {
            var b = GameObject.Find("Building_" + site);
            if (b != null) buildings.Add(new KeyValuePair<string, Transform>(site.ToUpperInvariant(), b.transform));
        }
        if (kit != null)
        {
            Transform block = null;
            foreach (Transform c in kit) if (c.name == "block") block = c;
            if (block != null)
                foreach (Transform c in block)
                {
                    string label = c.name == "grocery" ? "GROCERY" : c.name == "recreation-center" ? "REC CENTER"
                        : c.name == "apartment" ? "APARTMENTS" : c.name == "common-room" ? "COMMONS" : null;
                    if (c.name.StartsWith("placed_rack") || c.name == "park" || c.name == "courtyard") continue;
                    buildings.Add(new KeyValuePair<string, Transform>(label, c));
                }
        }
        foreach (var pair in buildings)
        {
            // WORLD-FIXES: the apartment now has its own walls and roof; its gym
            // annex (a child in the kit prefab) is dressed as its own building.
            Transform gym = pair.Value.name == "apartment" ? pair.Value.Find("gym") : null;
            if (gym == null && pair.Value.name == "apartment")
                foreach (var t in pair.Value.GetComponentsInChildren<Transform>(true)) if (t.name == "gym") gym = t;
            Kit(group, pair.Value, pair.Key, index, gym);
            index++;
            if (gym != null) { Kit(group, gym, null, index); index++; }
        }
    }

    static List<Wall> Walls(Transform building, bool requireName, Transform exclude = null)
    {
        var walls = new List<Wall>();
        foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (exclude != null && r.transform.IsChildOf(exclude)) continue;
            if (requireName && !r.name.ToLowerInvariant().Contains("wall")) continue;
            var b = r.bounds;
            float thin = Mathf.Min(b.size.x, b.size.z), len = Mathf.Max(b.size.x, b.size.z);
            if (b.size.y < 0.35f || thin > 0.45f || len < 0.9f || b.max.y < 2.0f) continue;
            walls.Add(new Wall { r = r, b = b, alongX = b.size.x >= b.size.z });
        }
        return walls;
    }

    static Bounds Footprint(List<Wall> walls)
    {
        var f = walls[0].b;
        foreach (var w in walls) f.Encapsulate(w.b);
        return f;
    }

    static void Home(Transform group, Transform home, int index)
    {
        var walls = Walls(home, false);
        if (walls.Count == 0) return;
        var foot = Footprint(walls);
        Dressed.Add(new Rect(foot.min.x, foot.min.z, foot.size.x, foot.size.z));
        Color wallTint = index == 0 ? H("#efe3cf") : H("#cbdfd9");
        var clad = Mat("clad-" + index, wallTint, sidingTex, 1f);
        var g = Group(home.name + "Dressing", group);
        foreach (var w in walls)
        {
            // Exterior cladding plate on the outer face only; interior faces keep their look.
            Vector3 c = w.b.center;
            Vector3 outward = w.alongX ? new Vector3(0f, 0f, Mathf.Sign(c.z - foot.center.z)) : new Vector3(Mathf.Sign(c.x - foot.center.x), 0f, 0f);
            float half = w.alongX ? w.b.extents.z : w.b.extents.x;
            Vector3 size = w.alongX ? new Vector3(w.b.size.x + 0.04f, w.b.size.y, 0.02f) : new Vector3(0.02f, w.b.size.y, w.b.size.z + 0.04f);
            Part(PrimitiveType.Cube, "Clad", g, c + outward * (half + 0.012f), size, clad, false);
        }
        Dress(g, walls, foot, index, null, true);
        // Gable roof above the flat roof box, eaves overhanging by 0.35 m.
        var roofMat = Mat("roof-" + index, index == 0 ? Rust : H("#405a70"), roofTex, 0f);
        var gable = Gable(foot.size.x + 0.7f, foot.size.z + 0.7f, 2.1f);
        var roof = MeshPart(gable, "GableRoof", g, new Vector3(foot.center.x, foot.max.y + 0.2f, foot.center.z), Vector3.one, roofMat);
        roof.GetComponent<MeshRenderer>().sharedMaterials = new[] { roofMat, clad };
        roof.layer = LayerMask.NameToLayer("Ignore Raycast");
        var trim = Mat("trim", H("#fbf6ee"));
        Part(PrimitiveType.Cube, "Fascia", g, new Vector3(foot.center.x, foot.max.y + 0.12f, foot.center.z),
            new Vector3(foot.size.x + 0.5f, 0.16f, foot.size.z + 0.5f), trim, false).layer = LayerMask.NameToLayer("Ignore Raycast");
        Part(PrimitiveType.Cube, "Chimney", g, new Vector3(foot.center.x + foot.size.x * 0.28f, foot.max.y + 2.0f, foot.center.z + 1.0f),
            new Vector3(0.6f, 1.4f, 0.6f), Mat("chimney", H("#c99f72"), paverTex, 0.6f)).layer = LayerMask.NameToLayer("Ignore Raycast");
        ContactShadow(g, foot.center, new Vector2(foot.size.x + 2.2f, foot.size.z + 2.2f), true);
    }

    static void Kit(Transform group, Transform building, string label, int index, Transform exclude = null)
    {
        var walls = Walls(building, building.name.StartsWith("Building_") ? false : true, exclude);
        if (walls.Count < 2) return;
        var foot = Footprint(walls);
        if (foot.size.x < 3f || foot.size.z < 3f) return;
        var area = new Rect(foot.min.x, foot.min.z, foot.size.x, foot.size.z);
        foreach (var d in Dressed)
        {
            // The kit apartment shares the workshop's origin; dress only one.
            float ox = Mathf.Min(area.xMax, d.xMax) - Mathf.Max(area.xMin, d.xMin);
            float oz = Mathf.Min(area.yMax, d.yMax) - Mathf.Max(area.yMin, d.yMin);
            if (ox > 0f && oz > 0f && ox * oz > 0.3f * Mathf.Min(area.width * area.height, d.width * d.height)) return;
        }
        Dressed.Add(area);
        var all = foot;
        foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
            if (exclude == null || !r.transform.IsChildOf(exclude)) all.Encapsulate(r.bounds);
        var g = Group(building.name + "Dressing", group);
        string[] palette;
        if (!Palette.TryGetValue(building.name, out palette)) palette = new[] { "#f4ebdd", "#ad5737" };
        Color tint = H(palette[0]);
        foreach (var w in walls) Restyle(w.r, "wall" + index, tint, null, 0f);
        // Pitched roofs keep their shape with a per-building roof colour;
        // flat roofs get a coloured cap with a parapet.
        Color roofTint = H(palette[1]);
        bool pitched = false;
        foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (exclude != null && r.transform.IsChildOf(exclude)) continue;
            string n = r.name.ToLowerInvariant();
            if (n.Contains("gable")) { pitched = true; Restyle(r, "wall" + index, tint, null, 0f); }
            else if (n.Contains("roof") && r.bounds.size.y > 0.4f) { pitched = true; Restyle(r, "roof" + index, roofTint, null, 0f); }
        }
        if (!pitched)
        {
            var cap = Mat("cap-" + index, roofTint, roofTex, 1.2f);
            var trim = Mat("trim", H("#fbf6ee"));
            float top = all.max.y + 0.07f;
            Part(PrimitiveType.Cube, "RoofCap", g, new Vector3(foot.center.x, top, foot.center.z),
                new Vector3(foot.size.x + 0.3f, 0.14f, foot.size.z + 0.3f), cap);
            foreach (float s in new[] { -1f, 1f })
            {
                Part(PrimitiveType.Cube, "Parapet", g, new Vector3(foot.center.x, top + 0.14f, foot.center.z + s * (foot.extents.z + 0.15f)),
                    new Vector3(foot.size.x + 0.42f, 0.22f, 0.12f), trim, false);
                Part(PrimitiveType.Cube, "Parapet", g, new Vector3(foot.center.x + s * (foot.extents.x + 0.15f), top + 0.14f, foot.center.z),
                    new Vector3(0.12f, 0.22f, foot.size.z + 0.42f), trim, false);
            }
            var unit = Mat("rooftop", H("#c9c3b6"));
            Part(PrimitiveType.Cube, "RoofUnit", g, new Vector3(foot.center.x - foot.extents.x * 0.4f, top + 0.35f, foot.center.z + foot.extents.z * 0.3f),
                new Vector3(0.9f, 0.55f, 0.7f), unit);
        }
        Dress(g, walls, foot, index, label, false);
        ContactShadow(g, foot.center, new Vector2(foot.size.x + 2.0f, foot.size.z + 2.0f), true);
    }

    // Windows, door trim, canopy, sign, wall lamps and foundation planting for
    // one building whose walls and footprint are known.
    static void Dress(Transform g, List<Wall> walls, Bounds foot, int index, string label, bool home)
    {
        var frame = Mat("window-frame", H("#fbf6ee"));
        var glassDay = Mat("glass-day", H("#5f9fb0"), null, 0f, 0f, null, 6f, 0f, H("#12303a") * 0.3f);
        var glassLit = Mat("glass-lit", H("#f3dcaa"), null, 0f, 0f, null, 6f, 0f, H("#ffcf7a") * 0.06f);
        var shutter = Mat("shutter-" + (index % 3), index % 3 == 0 ? Teal : index % 3 == 1 ? H("#405a70") : Rust);
        var doorTrim = Mat("door-trim", H("#fbf6ee"));
        var canopyMat = Mat("canopy-" + (index % 3), index % 3 == 0 ? Teal : index % 3 == 1 ? Rust : H("#405a70"));
        var wallLamp = Mat("lamp-glow", H("#ffe9a8"), null, 0f, 0f, null, 6f, 0f, H("#ffd27a") * 1.4f);
        float baseY = foot.min.y;
        int windowCount = 0;
        foreach (var w in walls)
        {
            Vector3 c = w.b.center;
            float thick = w.alongX ? w.b.size.z : w.b.size.x;
            float outerDist = w.alongX
                ? Mathf.Min(Mathf.Abs(w.b.max.z - foot.max.z), Mathf.Abs(w.b.min.z - foot.min.z))
                : Mathf.Min(Mathf.Abs(w.b.max.x - foot.max.x), Mathf.Abs(w.b.min.x - foot.min.x));
            if (outerDist > 0.5f) continue;
            Vector3 outward = w.alongX ? new Vector3(0f, 0f, Mathf.Sign(c.z - foot.center.z)) : new Vector3(Mathf.Sign(c.x - foot.center.x), 0f, 0f);
            Vector3 along = w.alongX ? Vector3.right : Vector3.forward;
            float len = w.alongX ? w.b.size.x : w.b.size.z;
            Quaternion face = Quaternion.LookRotation(-outward, Vector3.up);
            float surface = thick * 0.5f + (home ? 0.03f : 0.01f);
            // Header over a door: the wall piece starts well above the floor.
            if (w.b.min.y > baseY + 1.6f && len < 3.4f)
            {
                float top = w.b.min.y, head = w.b.max.y;
                Vector3 door = new Vector3(c.x, 0f, c.z) + outward * surface;
                float halfW = len * 0.5f, doorH = top - baseY;
                Part(PrimitiveType.Cube, "DoorTrimTop", g, door + Vector3.up * (top + 0.06f), Abs(along) * (len + 0.3f) + Vector3.up * 0.14f + Abs(outward) * 0.08f, doorTrim, false);
                foreach (float s in new[] { -1f, 1f })
                {
                    Part(PrimitiveType.Cube, "DoorTrimSide", g, door + along * (s * (halfW + 0.07f)) + Vector3.up * (baseY + doorH * 0.5f),
                        Abs(along) * 0.14f + Vector3.up * doorH + Abs(outward) * 0.08f, doorTrim, false);
                    Vector3 lantern = door + along * (s * (halfW + 0.45f)) + Vector3.up * (top - 0.3f) + outward * 0.09f;
                    Part(PrimitiveType.Cube, "LanternCap", g, lantern + Vector3.up * 0.13f, new Vector3(0.18f, 0.05f, 0.18f), Mat("lamp-pole", Charcoal), false);
                    MeshPart(blob, "LanternGlow", g, lantern, new Vector3(0.12f, 0.18f, 0.12f), wallLamp, false);
                }
                MeshPart(blob, "Doormat", g, door + outward * 0.55f + Vector3.up * (baseY + 0.045f),
                    Abs(along) * (len * 0.7f) + Vector3.up * 0.02f + Abs(outward) * 0.6f, Mat("doormat", Honey), false);
                int row = label == null ? -1 : System.Array.IndexOf(SignRows, label);
                if (row >= 0)
                {
                    // Painted sign over the door from the self-made atlas
                    // (polish-signs.png row order = SignRows).
                    float boardH = Mathf.Clamp(head - top - 0.1f, 0.42f, 0.6f);
                    float width = boardH * 4f;
                    Vector3 at = door + Vector3.up * (top + 0.1f + boardH * 0.5f);
                    Part(PrimitiveType.Cube, "SignBacking", g, at + outward * 0.02f, Abs(along) * (width + 0.12f) + Vector3.up * (boardH + 0.12f) + Abs(outward) * 0.05f, Mat("sign-backing", Honey), false);
                    var sign = Mat("sign-" + row, Color.white, signTex, 0f);
                    sign.SetTextureScale("_BaseMap", new Vector2(1f, 1f / SignRows.Length));
                    sign.SetTextureOffset("_BaseMap", new Vector2(0f, 1f - (row + 1f) / SignRows.Length));
                    Part(PrimitiveType.Quad, "Sign_" + label, g, at + outward * 0.05f, new Vector3(width, boardH, 1f), sign, false, face);
                }
                else
                {
                    var canopy = Part(PrimitiveType.Cube, "Canopy", g, door + Vector3.up * (top + 0.2f) + outward * 0.45f,
                        Abs(along) * (len + 0.8f) + Vector3.up * 0.1f + Abs(outward) * 0.95f, canopyMat);
                    canopy.transform.rotation = Quaternion.AngleAxis(8f, Vector3.Cross(Vector3.up, outward));
                }
                FlowerBed(g, door + along * (halfW + 1.25f) + outward * 0.55f + Vector3.up * baseY, along, 1.3f);
                FlowerBed(g, door - along * (halfW + 1.25f) + outward * 0.55f + Vector3.up * baseY, along, 1.3f);
                continue;
            }
            if (w.b.size.y < 2.0f || len < 1.5f) continue;
            int count = Mathf.Max(1, Mathf.FloorToInt((len - 0.6f) / 2.4f));
            float spacing = len / count;
            for (int k = 0; k < count; k++)
            {
                float offset = -len * 0.5f + spacing * (k + 0.5f);
                Vector3 p = new Vector3(c.x, baseY + 1.5f, c.z) + along * offset + outward * surface;
                bool lit = ((index * 7 + k * 3 + windowCount) % 5) == 0;
                Part(PrimitiveType.Cube, "WindowFrame", g, p, Abs(along) * 1.06f + Vector3.up * 1.0f + Abs(outward) * 0.05f, frame, false);
                Part(PrimitiveType.Cube, "WindowGlass", g, p + outward * 0.02f, Abs(along) * 0.86f + Vector3.up * 0.8f + Abs(outward) * 0.04f, lit ? glassLit : glassDay, false);
                Part(PrimitiveType.Cube, "Mullion", g, p + outward * 0.045f, Abs(along) * 0.06f + Vector3.up * 0.8f + Abs(outward) * 0.02f, frame, false);
                Part(PrimitiveType.Cube, "Sill", g, p + Vector3.down * 0.55f + outward * 0.06f, Abs(along) * 1.2f + Vector3.up * 0.08f + Abs(outward) * 0.14f, frame, false);
                if (home)
                    foreach (float s in new[] { -1f, 1f })
                        Part(PrimitiveType.Cube, "Shutter", g, p + along * (s * 0.68f) + outward * 0.02f, Abs(along) * 0.28f + Vector3.up * 1.0f + Abs(outward) * 0.04f, shutter, false);
                windowCount++;
            }
            // Low shrubs along long walls that face the camera side (south/east).
            if (len > 4f && (outward.z < -0.5f || outward.x > 0.5f))
                for (float s = -len * 0.5f + 0.9f; s < len * 0.5f - 0.6f; s += R(1.4f, 2.2f))
                    Bush(g, new Vector3(c.x, baseY, c.z) + along * s + outward * (thick * 0.5f + 0.45f), R(0.45f, 0.7f), false);
        }
        Occupied.Add(new Rect(foot.min.x - 1.4f, foot.min.z - 1.4f, foot.size.x + 2.8f, foot.size.z + 2.8f));
    }

    static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z)); }

    // ------------------------------------------------------------ planting
    static bool Free(Vector3 p, float radius)
    {
        var r = new Rect(p.x - radius, p.z - radius, radius * 2f, radius * 2f);
        foreach (var o in Occupied) if (o.Overlaps(r)) return false;
        return true;
    }

    static void PlantTree(Transform parent, Vector3 at, float scale)
    {
        var t = Group("Tree", parent);
        bool pine = rng.NextDouble() < 0.28;
        var bark = Mat("bark", Bark);
        Part(PrimitiveType.Cylinder, "Trunk", t, at + Vector3.up * (0.9f * scale), new Vector3(0.22f, 0.9f, 0.22f) * scale, bark);
        if (pine)
        {
            var leaf = Mat("pine", Pine, null, 0f, 0.25f, H("#dfeccf"), 2f, 0.012f);
            for (int i = 0; i < 3; i++)
                MeshPart(cone, "Cone", t, at + Vector3.up * ((1.0f + i * 0.85f) * scale),
                    new Vector3(2.3f - i * 0.55f, 1.6f, 2.3f - i * 0.55f) * scale, leaf);
        }
        else
        {
            Color[] greens = { Leaf1, Leaf2, Leaf3 };
            int blobs = 3 + rng.Next(2);
            for (int i = 0; i < blobs; i++)
            {
                var leaf = Mat("leaf" + (i % 3), greens[i % 3], null, 0f, 0.3f, H("#e3efd2"), 2f, 0.015f);
                float s = (i == 0 ? 2.6f : R(1.5f, 2.0f)) * scale;
                Vector3 off = i == 0 ? Vector3.zero : new Vector3(R(-0.8f, 0.8f), R(-0.3f, 0.6f), R(-0.8f, 0.8f)) * scale;
                MeshPart(blob, "Crown", t, at + Vector3.up * (2.5f * scale) + off, new Vector3(s, s * 0.92f, s), leaf);
            }
        }
        ContactShadow(t, at, new Vector2(3.2f, 3.2f) * scale, false, 0.035f);
        Occupied.Add(new Rect(at.x - 1.3f * scale, at.z - 1.3f * scale, 2.6f * scale, 2.6f * scale));
        placedTrees++;
    }

    static void Bush(Transform parent, Vector3 at, float size, bool blooms)
    {
        var b = Group("Bush", parent);
        Color[] greens = { Leaf2, Leaf1, Leaf3 };
        int n = 2 + rng.Next(2);
        for (int i = 0; i < n; i++)
        {
            float s = size * R(0.8f, 1.15f);
            var leaf = Mat("bush" + (i % 3), greens[i % 3], null, 0f, 0.3f, H("#e3efd2"), 2f, 0.02f);
            MeshPart(blob, "Leaves", b, at + new Vector3(R(-0.35f, 0.35f) * size, s * 0.42f, R(-0.35f, 0.35f) * size), new Vector3(s * 1.15f, s, s * 1.15f), leaf, i == 0);
        }
        if (blooms || rng.NextDouble() < 0.35)
        {
            var bloom = Mat("bloom" + rng.Next(Blooms.Length), Blooms[rng.Next(Blooms.Length)]);
            for (int i = 0; i < 4; i++)
                MeshPart(blob, "Bloom", b, at + new Vector3(R(-0.4f, 0.4f) * size, size * R(0.6f, 0.9f), R(-0.4f, 0.4f) * size), Vector3.one * 0.13f, bloom, false);
        }
        placedBushes++;
    }

    static void FlowerBed(Transform parent, Vector3 at, Vector3 along, float length)
    {
        var f = Group("FlowerBed", parent);
        var soil = Mat("soil", H("#8a6a4c"));
        var green = Mat("bed-green", Leaf2, null, 0f, 0.2f, null, 2f, 0.01f);
        Part(PrimitiveType.Cube, "Edge", f, at + Vector3.up * 0.08f, Abs(along) * length + Vector3.up * 0.16f + Abs(Vector3.Cross(along, Vector3.up)) * 0.55f, Mat("bed-edge", H("#d9cdb8")), false);
        Part(PrimitiveType.Cube, "Soil", f, at + Vector3.up * 0.165f, Abs(along) * (length - 0.1f) + Vector3.up * 0.02f + Abs(Vector3.Cross(along, Vector3.up)) * 0.45f, soil, false);
        for (float s = -length * 0.5f + 0.15f; s < length * 0.5f - 0.1f; s += 0.24f)
        {
            Vector3 p = at + along * s + Vector3.Cross(along, Vector3.up) * R(-0.12f, 0.12f);
            MeshPart(blob, "Stem", f, p + Vector3.up * 0.25f, new Vector3(0.22f, 0.2f, 0.22f), green, false);
            var bloom = Mat("bloom" + rng.Next(Blooms.Length), Blooms[rng.Next(Blooms.Length)]);
            MeshPart(blob, "Flower", f, p + Vector3.up * 0.36f, Vector3.one * 0.14f, bloom, false);
        }
        placedFlowers++;
    }

    static void FlowerClump(Transform parent, Vector3 at)
    {
        var f = Group("Flowers", parent);
        var green = Mat("bed-green", Leaf2, null, 0f, 0.2f, null, 2f, 0.01f);
        MeshPart(blob, "Base", f, at + Vector3.up * 0.1f, new Vector3(0.8f, 0.3f, 0.8f), green, false);
        int n = 5 + rng.Next(4);
        var bloom = Mat("bloom" + rng.Next(Blooms.Length), Blooms[rng.Next(Blooms.Length)]);
        for (int i = 0; i < n; i++)
            MeshPart(blob, "Flower", f, at + new Vector3(R(-0.32f, 0.32f), R(0.22f, 0.32f), R(-0.32f, 0.32f)), Vector3.one * 0.13f, bloom, false);
        placedFlowers++;
    }

    static void Planting()
    {
        var group = Group("Planting");
        // Street trees: a row on each verge, 8 m apart, where free.
        for (float z = -4f; z < 36f; z += 8f)
            foreach (float x in new[] { 15.9f, 26.1f })
            {
                var p = new Vector3(x + R(-0.2f, 0.2f), 0f, z + R(-0.5f, 0.5f));
                if (Free(p, 0.6f)) PlantTree(group, p, R(0.85f, 1.05f));
            }
        // Block interior and edges: jittered grid, trees then bushes then flowers.
        for (float x = -30f; x <= 58f; x += 5.5f)
            for (float z = -17f; z <= 47f; z += 5.5f)
            {
                var p = new Vector3(x + R(-2f, 2f), 0f, z + R(-2f, 2f));
                bool edge = x < -24f || x > 50f || z < -12f || z > 41f;
                double roll = rng.NextDouble();
                if (roll < (edge ? 0.65 : 0.3) && Free(p, 1.6f)) PlantTree(group, p, R(0.8f, 1.25f));
                else if (roll < (edge ? 0.82 : 0.55) && Free(p, 0.7f)) { Bush(group, p, R(0.5f, 0.9f), false); Occupied.Add(new Rect(p.x - 0.7f, p.z - 0.7f, 1.4f, 1.4f)); }
                else if (roll < (edge ? 0.92 : 0.72) && Free(p, 0.5f)) { FlowerClump(group, p); Occupied.Add(new Rect(p.x - 0.5f, p.z - 0.5f, 1f, 1f)); }
            }
        // Grass tufts: three slim blades each, on free lawn only.
        var blade = Mat("tuft", H("#6a9a4e"), null, 0f, 0.3f, H("#d9e8c2"), 3f, 0.03f);
        int tufts = 0;
        for (int i = 0; i < 2400 && tufts < 320; i++)
        {
            var p = new Vector3(R(-28f, 58f), 0f, R(-16f, 46f));
            if (!Free(p, 0.25f)) continue;
            var t = Group("Tuft", group);
            for (int k = 0; k < 3; k++)
            {
                float lean = R(-14f, 14f);
                MeshPart(cone, "Blade", t, p + new Vector3(R(-0.08f, 0.08f), -0.07f, R(-0.08f, 0.08f)),
                    new Vector3(0.07f, R(0.22f, 0.34f), 0.07f), blade, false, Quaternion.Euler(lean, R(0f, 360f), R(-14f, 14f)));
            }
            tufts++;
        }
    }

    // ------------------------------------------------------------ horizon
    static void Horizon()
    {
        var group = Group("Horizon");
        var hills = MeshPart(HillRing(new Vector2(15f, 15f)), "Hills", group, Vector3.zero, Vector3.one,
            Mat("hills", H("#98b277"), null, 0f, 0.45f, H("#d6e0b4"), 30f), false);
        hills.GetComponent<MeshRenderer>().receiveShadows = false;
        // A far tree belt on the inner hill slope gives the horizon depth.
        Color[] greens = { H("#5f8c4f"), H("#6c9a58"), H("#577f49") };
        for (int i = 0; i < 56; i++)
        {
            if (rng.NextDouble() < 0.25) continue;
            float a = i * Mathf.PI * 2f / 56f + R(-0.04f, 0.04f);
            float radius = R(112f, 175f);
            var at = new Vector3(15f + Mathf.Cos(a) * radius, R(0f, 2f), 15f + Mathf.Sin(a) * radius);
            float s = R(4.5f, 9f);
            MeshPart(blob, "FarTrees", group, at + Vector3.up * s * 0.3f, new Vector3(s * R(1.2f, 1.8f), s, s * R(1.2f, 1.8f)),
                Mat("far" + (i % 3), greens[i % 3], null, 0f, 0f), false);
        }
    }
}
