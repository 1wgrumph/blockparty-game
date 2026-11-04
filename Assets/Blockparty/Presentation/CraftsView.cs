using System;
using System.Collections.Generic;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Simulation;
using UnityEngine;

// CRAFTS@1 presentation: finished craft pieces as furniture in the Alpha
// homes, placed at runtime in fixed slots measured from each home's Floor
// (no Alpha.unity change). Wall pieces hang framed on the inner walls, the
// quilt lies as a rug, pottery is a self-made lathe-turned vase on a wall
// shelf whose glaze is the piece's image. Sorted sim homes map to the scene's
// HomeA/HomeB in turn. Over capacity the newest piece takes the oldest slot.
// Textures: a generated PNG when the piece has one, else CraftArt's
// deterministic procedural image. Materials clone the home wall material, so
// the player build already carries their shader.
public sealed class CraftsView
{
    static readonly string[] SceneHomes = { "HomeA", "HomeB" };

    // Wall slots: position along the wall and the facing yaw into the room
    // (0 = south wall facing north, 180 = north wall, 270 = east wall facing west).
    // The first four (east and south walls) face the room view's camera.
    static readonly Vector2[] WallSlots =
    {
        new Vector2(-1.4f, 270f), new Vector2(+1.4f, 270f),
        new Vector2(+3.4f, 0f), new Vector2(-2.6f, 0f),
        new Vector2(-2.2f, 180f), new Vector2(+1.0f, 180f),
    };
    static readonly Vector2[] FloorSlots = { new Vector2(0.2f, -1.5f), new Vector2(-2.4f, 0.2f) };
    // Shelf: x, z, yaw of the shelf board; two vases per shelf.
    static readonly Vector4[] Shelves = { new Vector4(+1f, 0f, 270f, 0f), new Vector4(+1.9f, -1f, 0f, 0f) };

    sealed class HomeFrame
    {
        public Vector3 Centre;
        public float InnerX, InnerZ, FloorY;
    }

    readonly Transform root;
    readonly Material baseMaterial;
    readonly Dictionary<string, HomeFrame> homes = new Dictionary<string, HomeFrame>();
    readonly Dictionary<long, GameObject> objects = new Dictionary<long, GameObject>();
    readonly Dictionary<long, Texture2D> textures = new Dictionary<long, Texture2D>();
    readonly Dictionary<Collider, long> hits = new Dictionary<Collider, long>();
    readonly List<Material> owned = new List<Material>();
    readonly List<UnityEngine.Object> shelves = new List<UnityEngine.Object>();
    Mesh vase;
    GUIStyle cardStyle, titleStyle;
    Texture2D cardTexture;

    public CraftsView(Transform parent)
    {
        root = new GameObject("CraftsFurniture").transform;
        root.SetParent(parent, false);
        Material wall = null;
        foreach (string name in SceneHomes)
        {
            GameObject home = GameObject.Find(name);
            if (home == null) continue;
            Transform floor = home.transform.Find("Floor");
            Transform wallN = home.transform.Find("WallN");
            Renderer r = floor == null ? null : floor.GetComponent<Renderer>();
            if (r == null) continue;
            Bounds b = r.bounds;
            homes[name] = new HomeFrame
            {
                Centre = new Vector3(b.center.x, b.max.y, b.center.z),
                InnerX = b.extents.x - 0.3f,
                InnerZ = b.extents.z - 0.3f,
                FloorY = b.max.y,
            };
            if (wall == null && wallN != null && wallN.GetComponent<Renderer>() != null)
                wall = wallN.GetComponent<Renderer>().sharedMaterial;
            if (wall == null) wall = r.sharedMaterial;
        }
        baseMaterial = wall;
    }

    public bool Ready { get { return baseMaterial != null && homes.Count > 0; } }

    public static string SceneHomeOf(WorldState world, HomeId home)
    {
        List<long> ids = new List<long>();
        foreach (HomeRecord row in world.ExportSnapshot().Homes) ids.Add((long)row.Id);
        ids.Sort();
        int index = ids.IndexOf((long)home);
        return SceneHomes[Mathf.Max(0, index) % SceneHomes.Length];
    }

    public static string Placement(CraftKind kind)
    {
        CraftSlot slot = Crafts.SlotOf(kind);
        return slot == CraftSlot.Floor ? "on the floor" : slot == CraftSlot.Shelf ? "on a shelf" : "on the wall";
    }

    // Destroys and rebuilds every piece object from the crafts state.
    public void Rebuild(Crafts crafts, WorldState world)
    {
        Clear();
        if (!Ready || crafts == null) return;
        Dictionary<string, List<CraftPiece>> byGroup = new Dictionary<string, List<CraftPiece>>();
        foreach (CraftPiece piece in crafts.Pieces)
        {
            string key = SceneHomeOf(world, piece.Home) + "/" + Crafts.SlotOf(piece.Kind);
            List<CraftPiece> list;
            if (!byGroup.TryGetValue(key, out list)) byGroup[key] = list = new List<CraftPiece>();
            list.Add(piece);
        }
        foreach (KeyValuePair<string, List<CraftPiece>> pair in byGroup)
        {
            string sceneHome = pair.Key.Substring(0, pair.Key.IndexOf('/'));
            HomeFrame frame;
            if (!homes.TryGetValue(sceneHome, out frame)) continue;
            List<CraftPiece> list = pair.Value;
            list.Sort((a, b) => ((long)a.Item).CompareTo((long)b.Item));
            CraftSlot slot = Crafts.SlotOf(list[0].Kind);
            int capacity = slot == CraftSlot.Wall ? WallSlots.Length : slot == CraftSlot.Floor ? FloorSlots.Length : Shelves.Length * 2;
            if (slot == CraftSlot.Shelf) BuildShelves(frame, (Mathf.Min(list.Count, capacity) + 1) / 2);
            for (int rank = Mathf.Max(0, list.Count - capacity); rank < list.Count; rank++)
                Build(list[rank], frame, rank % capacity);
        }
    }

    // Swaps the texture after the image path changes (generated arrival).
    public void Refresh(CraftPiece piece)
    {
        GameObject go;
        if (piece == null || !objects.TryGetValue((long)piece.Item, out go)) return;
        Texture2D old;
        if (textures.TryGetValue((long)piece.Item, out old)) UnityEngine.Object.Destroy(old);
        Texture2D texture = TextureOf(piece);
        textures[(long)piece.Item] = texture;
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            if (r.gameObject.name == "Art") SetTexture(r.sharedMaterial, texture);
    }

    public CraftPiece PieceAt(Crafts crafts, Ray ray)
    {
        RaycastHit hit;
        long item;
        if (crafts == null || !Physics.Raycast(ray, out hit, 30f) || !hits.TryGetValue(hit.collider, out item)) return null;
        return crafts.Find((ObjectId)item);
    }

    // Camera pose inside a scene home: 0 = the east wall (portrait and shelf), 1 = the room.
    public static float ViewFieldOfView(int view) { return view == 0 ? 60f : 70f; }

    public bool TryViewPose(WorldState world, HomeId home, int view, out Vector3 position, out Vector3 look)
    {
        position = look = Vector3.zero;
        HomeFrame f;
        if (!homes.TryGetValue(SceneHomeOf(world, home), out f)) return false;
        if (view == 0)
        {
            position = f.Centre + new Vector3(f.InnerX - 2.6f, 1.45f, 0f);
            look = f.Centre + new Vector3(f.InnerX, 1.3f, 0f);
        }
        else
        {
            // Middle of the north wall, above the dining chair, looking south-east (70 degree view):
            // the east and south walls, both shelves and both rugs.
            position = f.Centre + new Vector3(0f, 2.1f, f.InnerZ - 0.4f);
            look = f.Centre + new Vector3(2.5f, 0.9f, -2.8f);
        }
        return true;
    }

    // Wrapped info card: title, how the image was made and the exact prompt.
    public void DrawInfo(CraftPiece piece, string title)
    {
        if (piece == null) return;
        float k = Screen.height / 1080f;
        if (cardStyle == null)
        {
            cardTexture = new Texture2D(1, 1);
            cardTexture.SetPixel(0, 0, new Color(0.141f, 0.196f, 0.220f, 0.94f));
            cardTexture.Apply();
            cardStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            cardStyle.normal.textColor = new Color(0.957f, 0.922f, 0.867f);
            titleStyle = new GUIStyle(cardStyle) { fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.949f, 0.698f, 0.298f);
        }
        cardStyle.fontSize = Mathf.RoundToInt(17f * k);
        titleStyle.fontSize = Mathf.RoundToInt(22f * k);
        float w = 520f * k, pad = 18f * k;
        string body = piece.Disclosure + "\n\nPrompt: " + piece.Prompt;
        float th = titleStyle.CalcHeight(new GUIContent(title), w - 2f * pad);
        float bh = cardStyle.CalcHeight(new GUIContent(body), w - 2f * pad);
        // Top right, under the HUD corner buttons; the Controls help owns the top left.
        Rect r = new Rect(Screen.width - 26f * k - w, 150f * k, w, th + bh + 3f * pad);
        int depth = GUI.depth;
        GUI.depth = -20;
        GUI.DrawTexture(r, cardTexture);
        GUI.Label(new Rect(r.x + pad, r.y + pad, w - 2f * pad, th), title, titleStyle);
        GUI.Label(new Rect(r.x + pad, r.y + pad * 2f + th, w - 2f * pad, bh), body, cardStyle);
        GUI.depth = depth;
    }

    public void Clear()
    {
        foreach (GameObject go in objects.Values) UnityEngine.Object.Destroy(go);
        foreach (Texture2D t in textures.Values) UnityEngine.Object.Destroy(t);
        foreach (Material m in owned) UnityEngine.Object.Destroy(m);
        foreach (UnityEngine.Object o in shelves) UnityEngine.Object.Destroy(o);
        objects.Clear(); textures.Clear(); hits.Clear(); owned.Clear(); shelves.Clear();
    }

    // ---------- building ----------

    void Build(CraftPiece piece, HomeFrame f, int slot)
    {
        GameObject go = new GameObject("Craft_" + (long)piece.Item + "_" + piece.Kind);
        go.transform.SetParent(root, false);
        Texture2D texture = TextureOf(piece);
        textures[(long)piece.Item] = texture;
        CraftSlot kind = Crafts.SlotOf(piece.Kind);
        if (kind == CraftSlot.Wall) BuildWall(go, piece, f, WallSlots[slot], texture);
        else if (kind == CraftSlot.Floor) BuildRug(go, f, FloorSlots[slot], texture);
        else BuildVase(go, f, slot, texture);
        foreach (Collider c in go.GetComponentsInChildren<Collider>()) hits[c] = (long)piece.Item;
        objects[(long)piece.Item] = go;
    }

    void BuildWall(GameObject go, CraftPiece piece, HomeFrame f, Vector2 s, Texture2D texture)
    {
        Vector3 at = s.y == 270f ? new Vector3(f.InnerX, 0f, s.x)
            : s.y == 180f ? new Vector3(s.x, 0f, f.InnerZ) : new Vector3(s.x, 0f, -f.InnerZ);
        go.transform.position = f.Centre + at + Vector3.up * 1.55f;
        go.transform.rotation = Quaternion.Euler(0f, s.y, 0f);
        float aspect = (float)texture.width / texture.height;
        float h = piece.Kind == CraftKind.Painting ? 0.62f : 0.72f;
        float w = h * aspect;
        // Local +Z faces into the room; the quad's front is its -Z, so it turns 180 degrees.
        GameObject art = GameObject.CreatePrimitive(PrimitiveType.Quad);
        art.name = "Art";
        art.transform.SetParent(go.transform, false);
        art.transform.localPosition = new Vector3(0f, 0f, piece.Kind == CraftKind.Poster ? 0.016f : 0.035f);
        art.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        art.transform.localScale = new Vector3(w, h, 1f);
        art.GetComponent<Renderer>().sharedMaterial = MaterialFor(texture, Color.white,
            piece.Kind == CraftKind.StainedGlass ? 0.7f : 0.15f);
        if (piece.Kind == CraftKind.Poster)
        {
            Bar(go, new Vector3(0f, 0f, 0.006f), new Vector3(w + 0.03f, h + 0.03f, 0.012f), Hex(0xF4EBDD), false);
            return;
        }
        Color frame = piece.Kind == CraftKind.Portrait ? Hex(0xF2B24C) : piece.Kind == CraftKind.StainedGlass ? Hex(0x243238) : Hex(0x5A3A26);
        float t = piece.Kind == CraftKind.StainedGlass ? 0.05f : 0.06f;
        Bar(go, new Vector3(0f, h / 2f + t / 2f, 0.03f), new Vector3(w + 2f * t, t, 0.05f), frame, true);
        Bar(go, new Vector3(0f, -h / 2f - t / 2f, 0.03f), new Vector3(w + 2f * t, t, 0.05f), frame, true);
        Bar(go, new Vector3(-w / 2f - t / 2f, 0f, 0.03f), new Vector3(t, h, 0.05f), frame, true);
        Bar(go, new Vector3(w / 2f + t / 2f, 0f, 0.03f), new Vector3(t, h, 0.05f), frame, true);
        Bar(go, new Vector3(0f, 0f, 0.006f), new Vector3(w, h, 0.012f), Hex(0x2C5A5D), false);
    }

    void BuildRug(GameObject go, HomeFrame f, Vector2 s, Texture2D texture)
    {
        go.transform.position = f.Centre + new Vector3(s.x, 0f, s.y);
        GameObject art = GameObject.CreatePrimitive(PrimitiveType.Quad);
        art.name = "Art";
        art.transform.SetParent(go.transform, false);
        art.transform.localPosition = new Vector3(0f, 0.012f, 0f);
        art.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        float h = 1.2f;
        art.transform.localScale = new Vector3(h * texture.width / texture.height, h, 1f);
        art.GetComponent<Renderer>().sharedMaterial = MaterialFor(texture, Color.white, 0.05f);
    }

    void BuildShelves(HomeFrame f, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Vector4 s = Shelves[i];
            GameObject board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "CraftShelf" + i;
            board.transform.SetParent(root, false);
            board.transform.position = ShelfCentre(f, s) + Vector3.up * 1.0f;
            board.transform.rotation = Quaternion.Euler(0f, s.z, 0f);
            board.transform.localScale = new Vector3(0.9f, 0.04f, 0.26f);
            board.GetComponent<Renderer>().sharedMaterial = MaterialFor(null, Hex(0x8D5A3B), 0.3f);
            UnityEngine.Object.Destroy(board.GetComponent<Collider>());
            shelves.Add(board);
        }
    }

    static Vector3 ShelfCentre(HomeFrame f, Vector4 s)
    {
        if (s.z == 270f) return f.Centre + new Vector3(f.InnerX - 0.14f, 0f, s.y);
        return f.Centre + new Vector3(s.x, 0f, -f.InnerZ + 0.14f);
    }

    void BuildVase(GameObject go, HomeFrame f, int slot, Texture2D texture)
    {
        Vector4 s = Shelves[slot / 2];
        Quaternion yaw = Quaternion.Euler(0f, s.z, 0f);
        go.transform.position = ShelfCentre(f, s) + Vector3.up * 1.02f + yaw * new Vector3(slot % 2 == 0 ? -0.22f : 0.22f, 0f, 0f);
        // The mesh seam (local +X) turns to the wall.
        go.transform.rotation = yaw * Quaternion.Euler(0f, 90f, 0f);
        GameObject art = new GameObject("Art");
        art.transform.SetParent(go.transform, false);
        art.AddComponent<MeshFilter>().sharedMesh = Vase();
        art.AddComponent<MeshRenderer>().sharedMaterial = MaterialFor(texture, Color.white, 0.55f);
        CapsuleCollider capsule = art.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, 0.16f, 0f);
        capsule.radius = 0.11f;
        capsule.height = 0.32f;
        GameObject opening = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        opening.name = "Opening";
        opening.transform.SetParent(go.transform, false);
        opening.transform.localPosition = new Vector3(0f, 0.305f, 0f);
        opening.transform.localScale = new Vector3(0.1f, 0.004f, 0.1f);
        opening.GetComponent<Renderer>().sharedMaterial = MaterialFor(null, Hex(0x5A3A26), 0.2f);
        UnityEngine.Object.Destroy(opening.GetComponent<Collider>());
    }

    // Lathe-turned vase: one profile revolved in 32 steps, analytic normals,
    // u around the body (seam at the back) and v up the profile.
    Mesh Vase()
    {
        if (vase != null) return vase;
        Vector2[] profile =
        {
            new Vector2(0.000f, 0.000f), new Vector2(0.070f, 0.000f), new Vector2(0.092f, 0.030f),
            new Vector2(0.108f, 0.090f), new Vector2(0.110f, 0.150f), new Vector2(0.095f, 0.210f),
            new Vector2(0.062f, 0.255f), new Vector2(0.050f, 0.285f), new Vector2(0.060f, 0.310f),
            new Vector2(0.066f, 0.320f),
        };
        const int steps = 32;
        int rows = profile.Length;
        Vector3[] v = new Vector3[(steps + 1) * rows];
        Vector3[] n = new Vector3[v.Length];
        Vector2[] uv = new Vector2[v.Length];
        float[] length = new float[rows];
        for (int j = 1; j < rows; j++) length[j] = length[j - 1] + Vector2.Distance(profile[j], profile[j - 1]);
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.PI * 2f * i / steps;
            float c = Mathf.Cos(a), sn = Mathf.Sin(a);
            for (int j = 0; j < rows; j++)
            {
                Vector2 p = profile[j];
                Vector2 t = profile[Mathf.Min(rows - 1, j + 1)] - profile[Mathf.Max(0, j - 1)];
                Vector2 pn = new Vector2(t.y, -t.x).normalized;
                int k = i * rows + j;
                v[k] = new Vector3(p.x * c, p.y, p.x * sn);
                n[k] = new Vector3(pn.x * c, pn.y, pn.x * sn);
                uv[k] = new Vector2((float)i / steps, length[j] / length[rows - 1]);
            }
        }
        List<int> tris = new List<int>();
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < rows - 1; j++)
            {
                int a0 = i * rows + j, a1 = a0 + 1, b0 = (i + 1) * rows + j, b1 = b0 + 1;
                tris.Add(a0); tris.Add(a1); tris.Add(b0);
                tris.Add(b0); tris.Add(a1); tris.Add(b1);
            }
        vase = new Mesh { name = "CraftVase" };
        vase.vertices = v;
        vase.normals = n;
        vase.uv = uv;
        vase.SetTriangles(tris, 0);
        vase.RecalculateBounds();
        return vase;
    }

    void Bar(GameObject parent, Vector3 local, Vector3 size, Color color, bool keepCollider)
    {
        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "Frame";
        bar.transform.SetParent(parent.transform, false);
        bar.transform.localPosition = local;
        bar.transform.localScale = size;
        bar.GetComponent<Renderer>().sharedMaterial = MaterialFor(null, color, 0.35f);
        if (!keepCollider) UnityEngine.Object.Destroy(bar.GetComponent<Collider>());
    }

    Material MaterialFor(Texture2D texture, Color color, float smoothness)
    {
        Material m = new Material(baseMaterial) { name = "Craft" + (texture == null ? "Solid" : "Art") };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        m.color = color;
        SetTexture(m, texture);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        owned.Add(m);
        return m;
    }

    static void SetTexture(Material m, Texture2D texture)
    {
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
        m.mainTexture = texture;
        if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", Vector2.one);
    }

    // True when Unity decodes the generated PNG; checked before a piece takes it (Crafts.SetGenerated).
    public static bool Decodes(string pngBase64)
    {
        Texture2D png = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        bool ok = png.LoadImage(Convert.FromBase64String(pngBase64));
        UnityEngine.Object.Destroy(png);
        return ok;
    }

    static Texture2D TextureOf(CraftPiece piece)
    {
        if (piece.ImagePath == Crafts.GeneratedPath)
        {
            Texture2D png = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (png.LoadImage(Convert.FromBase64String(piece.ImageBase64)))
            {
                png.wrapMode = TextureWrapMode.Clamp;
                png.name = "CraftGenerated" + (long)piece.Item;
                return png;
            }
            UnityEngine.Object.Destroy(png);
        }
        int w, h;
        CraftArt.SizeOf(piece.Kind, out w, out h);
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.LoadRawTextureData(CraftArt.Render(piece.Kind, piece.Seed, piece.SkinTone, piece.HairColor));
        t.filterMode = FilterMode.Point;
        t.wrapMode = piece.Kind == CraftKind.Pottery ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        t.name = "CraftProcedural" + (long)piece.Item;
        t.Apply(false, false);
        return t;
    }

    static Color Hex(uint rgb)
    {
        return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }
}
