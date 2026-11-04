using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// WORLD-FIXES (#216, owner review 2026-11-04 "why is this open?"): every
// Alpha building gets a roof, and RoofReveal hides a building's roof while the
// camera zooms in close over it or the selected resident is inside it.
// AlphaPolishSceneBuild.Apply calls Shells before its path and dressing passes
// (the GYM-TRAIL kit's apartment studios and common room were open-topped
// cutaways: partitions and furniture, no outer walls or roof) and Reveal after
// dressing, when every roof, ceiling and rooftop part exists. Shell walls are
// named "<building>_wall_*" and roofs "<building>_roof"/"_gable", so the
// polish dressing gives them the kit's wall tint, windows, door trim and sign.
// No shell part has a collider: resident picking and the GYM-TRAIL NavMesh are
// unchanged. Interiors keep every studio partition and piece of furniture.
public static class AlphaRoofSceneBuild
{
    const float WallHeight = 2.6f, WallThick = 0.16f, DoorHeight = 2.1f;

    public sealed class Area
    {
        public string id;
        public Transform building;
        public Rect rect;
        public float top;
    }

    // ------------------------------------------------------------ shells
    public static void Shells(Transform block)
    {
        if (block == null) throw new System.Exception("WORLD_FIXES missing GYM-TRAIL block");
        plain = null;
        var apartment = Deep(block, "apartment");
        var common = Deep(block, "common-room");
        if (apartment == null || common == null) throw new System.Exception("WORLD_FIXES missing apartment or common-room");
        // The gym annex (a child of the apartment in the kit prefab, which cannot
        // be restructured) stays its own building: own walls, ceiling and cap.
        if (Deep(block, "gym") == null) throw new System.Exception("WORLD_FIXES missing kit gym");

        // Apartment: studios a-d around a cross hall, footprint from its hall floor.
        Rect a = FloorRect(apartment, "apartment_hall_floor");
        var shell = Group(apartment, "apartment_shell");
        float s = a.yMin - WallThick * 0.5f, n = a.yMax + WallThick * 0.5f;
        float w = a.xMin - WallThick * 0.5f, e = a.xMax + WallThick * 0.5f;
        // South: entry door on the hall (sim entry -30,-2.5).
        WallWithDoor(shell, "apartment_wall_s", true, s, a.xMin - WallThick, a.xMax + WallThick, -30f, 1.2f);
        Wall(shell, "apartment_wall_n", true, n, a.xMin - WallThick, a.xMax + WallThick, 0f, WallHeight);
        Wall(shell, "apartment_wall_e", false, e, a.yMin, a.yMax, 0f, WallHeight);
        // West: the gym's own east wall (z -2..2, door on the hall) closes the middle.
        Wall(shell, "apartment_wall_w_s", false, w, a.yMin, -2.06f, 0f, WallHeight);
        Wall(shell, "apartment_wall_w_n", false, w, 2.06f, a.yMax, 0f, WallHeight);
        Roof(shell, "apartment", a, WallThick);

        // Common room: one open room, door on the south wall (sim entry -30,27.5).
        Rect c = FloorRect(common, "cr_floor");
        shell = Group(common, "common-room_shell");
        s = c.yMin - WallThick * 0.5f; n = c.yMax + WallThick * 0.5f;
        w = c.xMin - WallThick * 0.5f; e = c.xMax + WallThick * 0.5f;
        WallWithDoor(shell, "common-room_wall_s", true, s, c.xMin - WallThick, c.xMax + WallThick, -30f, 1.4f);
        Wall(shell, "common-room_wall_n", true, n, c.xMin - WallThick, c.xMax + WallThick, 0f, WallHeight);
        Wall(shell, "common-room_wall_e", false, e, c.yMin, c.yMax, 0f, WallHeight);
        Wall(shell, "common-room_wall_w", false, w, c.yMin, c.yMax, 0f, WallHeight);
        Roof(shell, "common-room", c, WallThick);
    }

    static Transform Deep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    static Rect FloorRect(Transform building, string floor)
    {
        foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
            if (r.name == floor) return new Rect(r.bounds.min.x, r.bounds.min.z, r.bounds.size.x, r.bounds.size.z);
        throw new System.Exception("WORLD_FIXES missing floor " + floor);
    }

    static Transform Group(Transform parent, string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, true);
        return t;
    }

    static Material plain;
    static Material Plain()
    {
        // Untextured base colour: the polish restyle replaces it with the kit tint.
        if (plain == null)
        {
            plain = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "WorldFixesShell" };
            plain.SetColor("_BaseColor", new Color(0.93f, 0.89f, 0.80f));
            plain.SetFloat("_Smoothness", 0.2f);
        }
        return plain;
    }

    // A wall along x (alongX, fixed z = at) or along z (fixed x = at) from lo to hi, y0..y1.
    static void Wall(Transform parent, string name, bool alongX, float at, float lo, float hi, float y0, float y1)
    {
        if (hi - lo < 0.05f) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        float mid = (lo + hi) * 0.5f, len = hi - lo;
        go.transform.position = alongX ? new Vector3(mid, (y0 + y1) * 0.5f, at) : new Vector3(at, (y0 + y1) * 0.5f, mid);
        go.transform.localScale = alongX ? new Vector3(len, y1 - y0, WallThick) : new Vector3(WallThick, y1 - y0, len);
        go.transform.SetParent(parent, true);
        go.GetComponent<MeshRenderer>().sharedMaterial = Plain();
    }

    // Two wall pieces and a header: the polish dressing reads the header as a door.
    static void WallWithDoor(Transform parent, string name, bool alongX, float at, float lo, float hi, float door, float width)
    {
        Wall(parent, name + "_a", alongX, at, lo, door - width * 0.5f, 0f, WallHeight);
        Wall(parent, name + "_b", alongX, at, door + width * 0.5f, hi, 0f, WallHeight);
        Wall(parent, name + "_h", alongX, at, door - width * 0.5f, door + width * 0.5f, DoorHeight, WallHeight);
    }

    // Gable roof over the walls (eaves 0.35 m), ridge along the longer side.
    // Slopes are "<id>_roof" (roof tint), the two end triangles "<id>_gable" (wall tint).
    static void Roof(Transform parent, string id, Rect floor, float wall)
    {
        bool alongX = floor.width >= floor.height;
        float len = (alongX ? floor.width : floor.height) + 2f * wall + 0.7f;
        float span = (alongX ? floor.height : floor.width) + 2f * wall + 0.7f;
        float rise = Mathf.Min(2.0f, span * 0.3f);
        var at = new Vector3(floor.center.x, WallHeight, floor.center.y);
        var rot = alongX ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
        float x = len * 0.5f, z = span * 0.5f;
        float slope = Mathf.Sqrt(z * z + rise * rise);
        var slopes = new Mesh { name = id + "_roof" };
        slopes.SetVertices(new List<Vector3>
        {
            new Vector3(-x, 0, -z), new Vector3(x, 0, -z), new Vector3(x, rise, 0), new Vector3(-x, rise, 0),
            new Vector3(x, 0, z), new Vector3(-x, 0, z), new Vector3(-x, rise, 0), new Vector3(x, rise, 0),
        });
        slopes.SetUVs(0, new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(len, 0), new Vector2(len, slope), new Vector2(0, slope),
            new Vector2(0, 0), new Vector2(len, 0), new Vector2(len, slope), new Vector2(0, slope),
        });
        slopes.SetTriangles(new[] { 0, 3, 2, 0, 2, 1, 4, 7, 6, 4, 6, 5 }, 0);
        slopes.RecalculateNormals();
        slopes.RecalculateBounds();
        // Gable ends sit on the outer wall faces and close the attic up to the
        // slope (a pentagon: the slope is eave-height yb above the wall line).
        float gx = x - 0.35f, gz = z - 0.35f, yb = rise * 0.35f / z;
        var ends = new Mesh { name = id + "_gable" };
        var ev = new List<Vector3>();
        foreach (float sx in new[] { -1f, 1f })
        {
            float sz = -sx; // west end runs +z -> -z, east end -z -> +z: both face outward
            ev.Add(new Vector3(sx * gx, 0, sz * gz)); ev.Add(new Vector3(sx * gx, yb, sz * gz)); ev.Add(new Vector3(sx * gx, rise, 0));
            ev.Add(new Vector3(sx * gx, yb, -sz * gz)); ev.Add(new Vector3(sx * gx, 0, -sz * gz));
        }
        ends.SetVertices(ev);
        ends.SetUVs(0, ev.ConvertAll(p => new Vector2(p.z, p.y)));
        ends.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 5, 6, 7, 5, 7, 8, 5, 8, 9 }, 0);
        ends.RecalculateNormals();
        ends.RecalculateBounds();
        foreach (var mesh in new[] { slopes, ends })
        {
            var go = new GameObject(mesh.name);
            go.transform.SetPositionAndRotation(at, rot);
            go.transform.SetParent(parent, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Plain();
            r.shadowCastingMode = ShadowCastingMode.TwoSided;
            go.layer = LayerMask.NameToLayer("Ignore Raycast");
        }
    }

    // ------------------------------------------------------------ registry
    // Footprint (outer wall faces) and eave height of every Alpha building.
    public static List<Area> Areas(Transform world, Transform block)
    {
        var areas = new List<Area>();
        foreach (string home in new[] { "HomeA", "HomeB" })
        {
            var t = world.Find("Homes/" + home);
            if (t == null) throw new System.Exception("WORLD_FIXES missing " + home);
            areas.Add(Footprint(home, t, n => n.StartsWith("Wall") || n == "DoorHeader"));
        }
        foreach (string site in new[] { "cafe", "workshop", "office", "school" })
        {
            var t = GameObject.Find("Building_" + site);
            if (t == null) throw new System.Exception("WORLD_FIXES missing Building_" + site);
            areas.Add(Footprint(site, t.transform, n => n.StartsWith("wall_")));
        }
        foreach (string kit in new[] { "apartment", "gym", "common-room", "grocery", "oak-house", "willow-house", "recreation-center" })
        {
            var t = Deep(block, kit);
            if (t == null) throw new System.Exception("WORLD_FIXES missing kit " + kit);
            areas.Add(Footprint(kit, t, n => n.Contains("wall")));
        }
        return areas;
    }

    static Area Footprint(string id, Transform building, System.Func<string, bool> isWall)
    {
        Bounds b = new Bounds();
        bool any = false;
        foreach (var r in building.GetComponentsInChildren<MeshRenderer>(true))
        {
            string n = r.name.ToLowerInvariant();
            if (!isWall(r.name) && !isWall(n)) continue;
            // The gym annex is a separate building inside the apartment's hierarchy.
            if (id == "apartment" && r.name.StartsWith("gym_")) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        if (!any) throw new System.Exception("WORLD_FIXES no walls for " + id);
        return new Area { id = id, building = building, rect = new Rect(b.min.x, b.min.z, b.size.x, b.size.z), top = b.max.y };
    }

    // ------------------------------------------------------------ reveal
    public static RoofReveal Reveal(Transform world, Transform polishRoot, Transform block, Camera camera)
    {
        var areas = Areas(world, block);
        var facades = polishRoot.Find("Facades");
        var list = new List<RoofReveal.Building>();
        foreach (var area in areas)
        {
            var roof = new List<Renderer>();
            foreach (var r in area.building.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.name.ToLowerInvariant();
                if (area.id == "apartment" && r.name.StartsWith("gym_")) continue;
                bool part = n == "roof" || n.Contains("_roof") || n.EndsWith("roof") || n.Contains("gable") || n.StartsWith("ceiling") || n.Contains("_ceiling");
                if (part) roof.Add(r);
            }
            Transform dressing = facades != null ? facades.Find(area.building.name + "Dressing") : null;
            if (dressing != null)
                foreach (var r in dressing.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.name == "GableRoof" || r.name == "Fascia" || r.name == "Chimney" || r.name == "RoofCap"
                        || r.name == "Parapet" || r.name == "RoofUnit") roof.Add(r);
            if (roof.Count == 0) throw new System.Exception("WORLD_FIXES no roof parts for " + area.id);
            list.Add(new RoofReveal.Building { id = area.id, area = area.rect, top = area.top, roof = roof.ToArray() });
        }
        var holder = new GameObject("RoofReveal");
        holder.transform.SetParent(world, false);
        var reveal = holder.AddComponent<RoofReveal>();
        reveal.buildings = list.ToArray();
        reveal.viewCamera = camera;
        reveal.selection = world.Find("Residents/ResidentLooks");
        if (reveal.selection == null) throw new System.Exception("WORLD_FIXES missing Residents/ResidentLooks");
        var log = new System.Text.StringBuilder("WORLD_FIXES roofs");
        foreach (var b in list) log.Append(' ').Append(b.id).Append('=').Append(b.roof.Length);
        Debug.Log(log.ToString());
        return reveal;
    }
}
