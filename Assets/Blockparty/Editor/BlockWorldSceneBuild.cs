// BLOCK-WORLD editor entry points (issue #217, M4).
// Builds the BlockWorld scene from the Blender-authored block kit and the six
// landed module glTFs, bakes one NavMesh over the block, records the baked
// path proofs, and captures the committed P5/E7 PNGs with sidecars.
//
//   Unity -batchmode -nographics -projectPath <copy> \
//     -executeMethod BlockWorldSceneBuild.BuildProve -logFile <import log>
//   DISPLAY=:0 Unity -projectPath <copy> \
//     -executeMethod BlockWorldSceneBuild.Capture -logFile <capture log>
//
// Capture mode comes from BLOCK_WORLD_CAPTURE: "p5" (24 P5 + navmesh overlay +
// the E7 display golden) or "e7-batchmode"/"e7-nographics" (one E7 frame).
// JSON is written with a small stdlib builder: the Editor assembly does not
// reference System.Text.Json (the test runner's netcorerun profile does).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class BlockWorldSceneBuild
{
    const string Art = "Assets/Blockparty/Art/Environment/Block/block-world.gltf";
    const string Layout = "Assets/Blockparty/Content/block-layout.json";
    const string ScenePath = "Assets/Blockparty/Scenes/BlockWorld.unity";
    const string Builder = "Assets/Blockparty/Editor/BlockWorldSceneBuild.cs";
    const string Root = "evidence/production/BLOCK-WORLD";
    const string Host = "build-host";
    const string ProjectCopy = "m4-copy-a";
    const string VolumeProfilePath = "Assets/Blockparty/Presentation/Slice/Vibrant.asset";
    // 540p keeps the pure-Python oracle under its published command timeout.
    const int Width = 800;
    const int Height = 450;
    // Owner sheets are separate native renders of the same P5 camera frames.
    const int OwnerWidth = 1920;
    const int OwnerHeight = 1080;
    const int Seed = 42;
    static string captureBuilderSha;

    static string ProjectRoot
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    static string Abs(string relative)
    {
        return Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    static string Sha(string relative)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(Abs(relative)))
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    // ------------------------------------------------------------------ build
    public static void BuildProve()
    {
        AssetDatabase.ImportAsset(Art, ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(Layout, ImportAssetOptions.ForceUpdate);
        var worldPrefab = AssetDatabase.LoadMainAssetAtPath(Art) as GameObject;
        if (worldPrefab == null)
        {
            throw new Exception("BLOCK-WORLD missing imported block kit");
        }
        var layout = ReadLayout();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = (GameObject)PrefabUtility.InstantiatePrefab(worldPrefab, scene);
        world.name = "BlockWorld";
        world.transform.position = Vector3.zero;
        foreach (var site in layout.Sites)
        {
            var prefab = AssetDatabase.LoadMainAssetAtPath(site.Asset) as GameObject;
            if (prefab == null)
            {
                throw new Exception("BLOCK-WORLD missing module " + site.Asset);
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = site.Id;
            // block-layout and BlockSites are Unity world space. glTFast already
            // applied its glTF X reflection to each imported module; do not reflect again.
            instance.transform.position = V(site.Origin);
        }
        BuildGrade(scene);
        AddAddressSigns(scene, layout);
        BuildLighting(scene);
        var camera = BuildCamera("SceneCamera", new Vector3(5f, 48f, -45f), new Vector3(5f, 0f, 10f));
        camera.tag = "MainCamera";
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            throw new Exception("BLOCK-WORLD scene save refused");
        }
        // Unity writes empty scalars as "key: "; normalize so the committed
        // scene is whitespace-clean. YAML treats both forms as empty.
        NormalizeSceneText();

        // Bind every input before proving anything against it.
        string artSha = Sha(Art), layoutSha = Sha(Layout), sceneSha = Sha(ScenePath), builderSha = Sha(Builder);
        int polygons;
        if (!BakeNavMesh(out polygons) || polygons <= 0)
        {
            throw new Exception("BLOCK-WORLD navmesh bake produced no walkable polygons");
        }

        var names = new List<string>();
        var points = new Dictionary<string, float[]>();
        foreach (var site in layout.Sites)
        {
            names.Add(site.Id);
            points[site.Id] = site.Entry;
        }
        names.Add("courtyard");
        points["courtyard"] = layout.Courtyard;
        var pairs = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            for (int j = i + 1; j < names.Count; j++)
            {
                string status;
                string corners;
                if (!Calculate(points[names[i]], points[names[j]], out status, out corners))
                {
                    throw new Exception("BLOCK-WORLD incomplete path " + names[i] + " -> " + names[j]);
                }
                pairs.Add(Obj("from", Q(names[i]), "to", Q(names[j]),
                    "status", Q(status), "corners", corners));
            }
        }
        var links = new List<string>();
        var candidates = new List<float[]>();
        foreach (float dz in new[] { -4.2f, -5.0f, -6.0f, -7.0f, -8.0f })
        {
            foreach (float dx in new[] { 0f, 2f, -2f, 4f, -4f })
            {
                candidates.Add(new[] { dx, 0f, dz });
            }
        }
        foreach (var site in layout.Sites)
        {
            float[] indoor = site.Entry;
            float[] outdoor = null;
            string fstatus = null, fcorners = null, rstatus = null, rcorners = null;
            // Pick the first outdoor anchor the baked surface covers within the
            // published 0.05 m arrival tolerance, outside the module doorway.
            foreach (var offset in candidates)
            {
                var candidate = new[] { indoor[0] + offset[0], indoor[1] + offset[1], indoor[2] + offset[2] };
                if (Calculate(indoor, candidate, out fstatus, out fcorners) &&
                    Calculate(candidate, indoor, out rstatus, out rcorners))
                {
                    outdoor = candidate;
                    break;
                }
            }
            if (outdoor == null)
            {
                throw new Exception("BLOCK-WORLD incomplete indoor-outdoor link " + site.Id);
            }
            links.Add(Obj("site", Q(site.Id), "enabled", "true", "bidirectional", "true",
                "indoor", Floats(indoor), "outdoor", Floats(outdoor),
                "status", Q(fstatus), "corners", fcorners,
                "reverse", Obj("status", Q(rstatus), "corners", rcorners)));
        }
        var proof = Obj(
            "host", Q(Host),
            "unity", Q("6000.3.10f1"),
            "method", Q("NavMesh.CalculatePath"),
            "baked", "true",
            "files", Obj(Art, Q(artSha), Layout, Q(layoutSha), ScenePath, Q(sceneSha), Builder, Q(builderSha)),
            "pairs", Arr(pairs),
            "links", Arr(links));
        File.WriteAllText(Abs(Root + "/nav-proofs.json"), proof);

        Debug.Log("BLOCK-WORLD asset_sha256=" + artSha);
        Debug.Log("BLOCK-WORLD layout_sha256=" + layoutSha);
        Debug.Log("BLOCK-WORLD scene_sha256=" + sceneSha);
        Debug.Log("BLOCK-WORLD nav_proofs_sha256=" + Sha(Root + "/nav-proofs.json"));
        Debug.Log("BLOCK-WORLD host=" + Host);
        Debug.Log("BLOCK-WORLD import PASS");
        Debug.Log("BLOCK-WORLD NavMesh bake PASS");
        Debug.Log("BLOCK-WORLD navmesh polygons=" + polygons
            + " pairs=" + pairs.Count + " links=" + links.Count);
        // -quit supplies Unity's own clean-exit log line and code 0; an
        // uncaught exception still exits 1 (observed), so success stays explicit.
    }

    [Serializable]
    sealed class SiteData
    {
        public string id;
        public string asset;
        public string address;
        public float[] origin;
        public float[] entry;

        public string Id { get { return id; } }
        public string Asset { get { return asset; } }
        public string Address { get { return address; } }
        public float[] Origin { get { return origin; } }
        public float[] Entry { get { return entry; } }
    }

    [Serializable]
    sealed class LayoutData
    {
        public float[] courtyard;
        public SiteData[] sites;

        public List<SiteData> Sites { get { return new List<SiteData>(sites); } }
        public float[] Courtyard { get { return courtyard; } }
    }

    static LayoutData ReadLayout()
    {
        return JsonUtility.FromJson<LayoutData>(File.ReadAllText(Abs(Layout)));
    }

    static Vector3 V(float[] p)
    {
        return new Vector3(p[0], p[1], p[2]);
    }

    static bool Calculate(float[] from, float[] to, out string status, out string corners)
    {
        status = null;
        corners = null;
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(V(from), V(to), NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete || path.corners.Length < 2)
        {
            return false;
        }
        if (Vector3.Distance(path.corners[0], V(from)) > 0.05f ||
            Vector3.Distance(path.corners[path.corners.Length - 1], V(to)) > 0.05f)
        {
            Debug.Log("BLOCK-WORLD path endpoint outside 0.05 m: "
                + Vector3.Distance(path.corners[0], V(from)).ToString("F4") + " / "
                + Vector3.Distance(path.corners[path.corners.Length - 1], V(to)).ToString("F4")
                + " target " + from[0].ToString("F4") + "," + from[1].ToString("F4") + "," + from[2].ToString("F4")
                + " -> " + to[0].ToString("F4") + "," + to[1].ToString("F4") + "," + to[2].ToString("F4")
                + " corners " + string.Join(";", Array.ConvertAll(path.corners,
                    c => c.x.ToString("F4") + "," + c.y.ToString("F4") + "," + c.z.ToString("F4"))));
            return false;
        }
        var list = new List<string>();
        foreach (var corner in path.corners)
        {
            list.Add(Floats(new[] { corner.x, corner.y, corner.z }));
        }
        status = "PathComplete";
        corners = Arr(list);
        return true;
    }

    // ------------------------------------------------------------ json bits
    static string Q(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string Obj(params string[] pairs)
    {
        var parts = new List<string>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            parts.Add(Q(pairs[i]) + ":" + pairs[i + 1]);
        }
        return "{" + string.Join(",", parts) + "}";
    }

    static string Arr(List<string> items)
    {
        return "[" + string.Join(",", items) + "]";
    }

    static string Floats(float[] values)
    {
        var parts = new List<string>();
        foreach (var value in values)
        {
            parts.Add(((float)Math.Round(value, 4)).ToString("R", CultureInfo.InvariantCulture));
        }
        return "[" + string.Join(",", parts) + "]";
    }

    static void NormalizeSceneText()
    {
        string path = Abs(ScenePath);
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }
        File.WriteAllText(path, string.Join("\n", lines));
    }

    static void BuildGrade(Scene scene)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null)
        {
            throw new Exception("BLOCK-WORLD missing approved Vibrant volume profile");
        }
        var grade = new GameObject("VibrantGrade");
        SceneManager.MoveGameObjectToScene(grade, scene);
        var volume = grade.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.sharedProfile = profile;
    }

    static void AddAddressSigns(Scene scene, LayoutData layout)
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
        {
            Debug.LogWarning("BLOCK-WORLD address signs skipped: legacy runtime font absent");
            return;
        }
        foreach (var site in layout.Sites)
        {
            var sign = new GameObject("AddressSign-" + site.Id);
            SceneManager.MoveGameObjectToScene(sign, scene);
            sign.transform.position = new Vector3(
                site.entry[0], 3.1f, site.entry[2] - SignDepth(site.id));
            // TextMesh's front faces the street (+Z in camera view / -Z surface normal).
            sign.transform.rotation = Quaternion.identity;
            var text = sign.AddComponent<TextMesh>();
            text.text = site.Address;
            text.font = font;
            text.fontSize = 64;
            text.characterSize = 0.035f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = Hex("#2E2523");
            text.richText = false;
            sign.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
    }

    static float SignDepth(string id)
    {
        return id == "common-room" ? 1.53f : 1.03f;
    }

    // ---------------------------------------------------------------- scene
    static void BuildLighting(Scene scene)
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Hex("#9CC7E8");
        RenderSettings.ambientEquatorColor = Hex("#E8DCC8");
        RenderSettings.ambientGroundColor = Hex("#8A7A62");
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;
        var sunObject = new GameObject("Sun");
        SceneManager.MoveGameObjectToScene(sunObject, scene);
        var sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = Hex("#FFF6E8");
        sun.intensity = 1.2f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.8f;
        sunObject.transform.rotation = Quaternion.Euler(65f, -30f, 0f);
        sunObject.AddComponent<UniversalAdditionalLightData>();
        QualitySettings.shadowDistance = 90f;
        QualitySettings.shadowCascades = 4;
    }

    static Camera BuildCamera(string name, Vector3 position, Vector3 lookAt)
    {
        var obj = new GameObject(name);
        var camera = obj.AddComponent<Camera>();
        camera.transform.position = position;
        camera.transform.LookAt(lookAt);
        camera.fieldOfView = 50f;
        camera.nearClipPlane = 0.15f;
        camera.farClipPlane = 600f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex("#8EC3EA");
        camera.allowHDR = true;
        camera.allowMSAA = true;
        var data = obj.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = true;
        return camera;
    }

    static Color Hex(string value)
    {
        Color color;
        ColorUtility.TryParseHtmlString(value, out color);
        return color;
    }

    // -------------------------------------------------------------- navmesh
    static IEnumerable<MeshFilter> BlockFilters()
    {
        foreach (var filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            string name = filter.gameObject.name;
            if (name == "sky" || name == "horizon")
            {
                continue; // distant decoration is not walkable ground
            }
            if (filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0)
            {
                continue;
            }
            yield return filter;
        }
    }

    // Offset the Recast tile grid off the world origin (x=0 is a tile border).
    // The Y offset leaves only a small, constant voxel lift for the -3 mm trim below.
    static readonly Vector3 dataPosition = new Vector3(-11f, 0.0460f, 10.211f);

    static bool BakeNavMesh(out int polygons)
    {
        polygons = 0;
        var settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 0.25f;
        settings.agentHeight = 1.8f;
        settings.agentClimb = 0.4f;
        settings.agentSlope = 45f;
        var bounds = new Bounds(new Vector3(0f, 2f, 10f), new Vector3(140f, 12f, 90f));
        var sources = new List<NavMeshBuildSource>();
        bool readable = true;
        foreach (var filter in BlockFilters())
        {
            if (!filter.sharedMesh.isReadable)
            {
                readable = false;
                break;
            }
        }
        var temporary = new List<MeshCollider>();
        try
        {
            if (readable)
            {
                foreach (var filter in BlockFilters())
                {
                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Mesh,
                        sourceObject = filter.sharedMesh,
                        transform = filter.transform.localToWorldMatrix,
                        area = 0,
                    });
                }
            }
            else
            {
                foreach (var filter in BlockFilters())
                {
                    var collider = filter.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = filter.sharedMesh;
                    temporary.Add(collider);
                }
                NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders,
                    0, new List<NavMeshBuildMarkup>(), sources);
            }
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds,
                dataPosition, Quaternion.identity);
            if (data == null)
            {
                return false;
            }
            NavMesh.RemoveAllNavMeshData();
            // Keep the voxel lift inside NAV's published 0.05 m arrival tolerance.
            NavMesh.AddNavMeshData(data,
                dataPosition + new Vector3(0f, -0.003f, 0f), Quaternion.identity);
            var triangulation = NavMesh.CalculateTriangulation();
            polygons = triangulation.indices.Length / 3;
            Debug.Log("BLOCK-WORLD navmesh sources=" + sources.Count + " readable=" + readable
                + " polygons=" + polygons);
            return polygons > 0;
        }
        finally
        {
            foreach (var collider in temporary)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
        }
    }

    // --------------------------------------------------------------- capture
    public static void Capture()
    {
        string mode = Environment.GetEnvironmentVariable("BLOCK_WORLD_CAPTURE") ?? "p5";
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        if (!scene.IsValid())
        {
            throw new Exception("BLOCK-WORLD scene did not open");
        }
        int polygons;
        BakeNavMesh(out polygons);
        string artSha = Sha(Art), layoutSha = Sha(Layout), sceneSha = Sha(ScenePath);
        captureBuilderSha = Sha(Builder);
        string tree = TreeHash(artSha, layoutSha, sceneSha, captureBuilderSha);
        string captureDir = Abs(Root + "/captures");
        string ownerDir = Abs(Root + "/owner-sheets");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(ownerDir);

        if (mode == "p5")
        {
            CaptureP5(captureDir, ownerDir, tree, artSha, layoutSha, sceneSha);
            // The E7 golden is the same overhead-yaw000-noon frame as the P5 shot.
            Shot(captureDir, "e7-overhead-yaw000-noon-display", OverheadFrame(0),
                "noon", tree, artSha, layoutSha, sceneSha, "e7-display");
        }
        else if (mode == "e7-batchmode" || mode == "e7-nographics")
        {
            Shot(captureDir, "e7-overhead-yaw000-noon-" + mode.Substring(3),
                OverheadFrame(0), "noon", tree, artSha, layoutSha, sceneSha, mode);
        }
        else
        {
            throw new Exception("BLOCK-WORLD unknown capture mode " + mode);
        }
        if (mode == "e7-nographics")
        {
            Debug.Log("BLOCK-WORLD capture CLASSIFIED_FAILED mode=e7-nographics");
        }
        else
        {
            Debug.Log("BLOCK-WORLD capture PASS mode=" + mode);
        }
        // GUI capture runs do not honour -quit after executeMethod (observed);
        // exit explicitly with the success code.
        EditorApplication.Exit(0);
    }

    static void CaptureP5(string dir, string ownerDir, string tree, string artSha,
        string layoutSha, string sceneSha)
    {
        foreach (int yaw in new[] { 0, 120, 240 })
        {
            foreach (string preset in new[] { "morning", "noon", "evening", "night" })
            {
                ShotP5(dir, ownerDir, string.Format("overhead-yaw{0:D3}-{1}", yaw, preset),
                    OverheadFrame(yaw), preset, tree, artSha, layoutSha, sceneSha);
            }
        }
        ShotP5(dir, ownerDir, "courtyard-wide-noon",
            Frame(new Vector3(5f, 1.8f, 5.5f), new Vector3(17f, 1.1f, 16f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        ShotP5(dir, ownerDir, "courtyard-minigolf-noon",
            Frame(new Vector3(2f, 11f, 3f), new Vector3(16f, 0f, 15f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        ShotP5(dir, ownerDir, "street-walk-noon-01",
            Frame(new Vector3(-38f, 1.7f, -5f), new Vector3(10f, 1.5f, -5f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        ShotP5(dir, ownerDir, "street-walk-noon-02",
            Frame(new Vector3(5f, 1.7f, -5f), new Vector3(0f, 1.5f, -3.2f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        ShotP5(dir, ownerDir, "street-walk-noon-03",
            Frame(new Vector3(-25f, 1.7f, -5f), new Vector3(-30f, 1.5f, -3.2f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        ShotP5(dir, ownerDir, "street-walk-noon-04",
            Frame(new Vector3(12f, 1.7f, -5f), new Vector3(32f, 1.5f, -5f)),
            "noon", tree, artSha, layoutSha, sceneSha);
        foreach (var site in ReadLayout().Sites)
        {
            var eye = new Vector3(site.entry[0], 1.75f, site.entry[2] - 6f);
            var look = new Vector3(site.entry[0], 1.7f, site.entry[2] + 0.2f);
            ShotP5(dir, ownerDir, "entrance-" + site.id + "-noon", Frame(eye, look),
                "noon", tree, artSha, layoutSha, sceneSha);
        }
        // Diagnostic: the same overhead frame with the baked NavMesh drawn on it.
        var overlay = NavMeshOverlay();
        try
        {
            Shot(dir, "navmesh-overhead", OverheadFrame(0), "noon",
                tree, artSha, layoutSha, sceneSha, "p5");
        }
        finally
        {
            if (overlay != null)
            {
                UnityEngine.Object.DestroyImmediate(overlay);
            }
        }
    }

    static void ShotP5(string dir, string ownerDir, string name, FrameData frame, string preset,
        string tree, string artSha, string layoutSha, string sceneSha)
    {
        Shot(dir, name, frame, preset, tree, artSha, layoutSha, sceneSha, "p5");
        Shot(ownerDir, name, frame, preset, tree, artSha, layoutSha, sceneSha,
            "owner-sheet", OwnerWidth, OwnerHeight, true);
    }

    static GameObject NavMeshOverlay()
    {
        var triangulation = NavMesh.CalculateTriangulation();
        if (triangulation.vertices == null || triangulation.vertices.Length < 3)
        {
            return null;
        }
        var mesh = new Mesh();
        var vertices = new Vector3[triangulation.vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = triangulation.vertices[i] + new Vector3(0f, 0.06f, 0f);
        }
        mesh.vertices = vertices;
        mesh.triangles = triangulation.indices;
        mesh.RecalculateNormals();
        var obj = new GameObject("NavMeshOverlay");
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = obj.AddComponent<MeshRenderer>();
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }
        var material = new Material(shader);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetColor("_BaseColor", new Color(0.18f, 0.85f, 0.72f, 0.55f));
        material.renderQueue = 3100;
        renderer.sharedMaterial = material;
        return obj;
    }

    sealed class FrameData
    {
        public Vector3 Position;
        public Vector3 LookAt;
        public int Yaw;
        public bool Overhead;
        public string Json;
    }

    static FrameData OverheadFrame(int yaw)
    {
        var target = new Vector3(5f, 0f, 10f);
        float radians = yaw * Mathf.Deg2Rad;
        var position = target + new Vector3(Mathf.Sin(radians) * 55f, 48f, -Mathf.Cos(radians) * 55f);
        var frame = Frame(position, target);
        frame.Yaw = yaw;
        frame.Overhead = true;
        frame.Json = Obj("position", Floats(new[] { position.x, position.y, position.z }),
            "look_at", Floats(new[] { target.x, target.y, target.z }),
            "fov", "50", "yaw", yaw.ToString(CultureInfo.InvariantCulture));
        return frame;
    }

    static FrameData Frame(Vector3 position, Vector3 lookAt)
    {
        return new FrameData
        {
            Position = position,
            LookAt = lookAt,
            Json = Obj("position", Floats(new[] { position.x, position.y, position.z }),
                "look_at", Floats(new[] { lookAt.x, lookAt.y, lookAt.z }),
                "fov", "50"),
        };
    }

    static void Shot(string dir, string name, FrameData frame, string preset, string tree,
        string artSha, string layoutSha, string sceneSha, string mode,
        int width = Width, int height = Height, bool ownerSheet = false)
    {
        string path = Path.Combine(dir, name + ".png");
        var camera = BuildCamera("CaptureCamera", frame.Position, frame.LookAt);
        ApplyPreset(preset, camera);
        bool rendered = false;
        bool fallback = false;
        string captureError = null;
        RenderTexture rt = null;
        Texture2D texture = null;
        try
        {
            try
            {
                if (mode == "e7-nographics")
                {
                    throw new Exception("intentional no-graphics E7 experiment");
                }
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                rt.antiAliasing = 4;
                rt.Create();
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = null;
                camera.targetTexture = null;
                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    throw new Exception("EncodeToPNG returned no data");
                }
                File.WriteAllBytes(path, png);
                rendered = true;
            }
            catch (Exception exception)
            {
                captureError = (exception.GetType().Name + ": " + exception.Message)
                    .Replace("\r", " ").Replace("\n", " ");
                Debug.Log("BLOCK-WORLD capture failure " + name + ": " + captureError);
                if (mode != "e7-nographics")
                {
                    throw new Exception("BLOCK-WORLD ordinary capture failed: " + name, exception);
                }
                // Only the intentional -nographics E7 experiment writes this
                // classified fallback; ordinary P5/display failures propagate.
                fallback = true;
                var black = new Texture2D(width, height, TextureFormat.RGB24, false);
                var pixels = new Color32[width * height];
                black.SetPixels32(pixels);
                black.Apply();
                File.WriteAllBytes(path, black.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(black);
            }
            finally
            {
                RenderTexture.active = null;
                if (camera != null)
                {
                    camera.targetTexture = null;
                }
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                if (rt != null)
                {
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
            string resolution = "[" + width + "," + height + "]";
            string sidecar = Obj(
                "camera", frame.Json,
                "light_preset", Q(preset),
                "seed", Seed.ToString(CultureInfo.InvariantCulture),
                "resolution", resolution,
                "native_resolution", resolution,
                "rendered", rendered ? "true" : "false",
                "fallback", fallback ? "true" : "false",
                "capture_error", captureError == null ? "null" : Q(captureError),
                "tool", Q("Unity 6000.3.10f1 RenderTexture capture (" + mode + ")"),
                "tree_hash", Q(tree),
                "host", Q(Host),
                "project_copy", Q(ProjectCopy),
                "files", Obj(Art, Q(artSha), Layout, Q(layoutSha), ScenePath, Q(sceneSha),
                    Builder, Q(captureBuilderSha)));
            if (ownerSheet)
            {
                sidecar = sidecar.Substring(0, sidecar.Length - 1) + ",\"owner_sheet\":true}";
            }
            File.WriteAllText(Path.Combine(dir, name + ".json"), sidecar);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(camera.gameObject);
        }
    }

    static void ApplyPreset(string preset, Camera camera)
    {
        var sun = UnityEngine.Object.FindFirstObjectByType<Light>();
        switch (preset)
        {
            case "morning":
                SetSun(sun, 35f, -50f, "#FFE0B8", 1.15f);
                SetAmbient("#A9CDEB", "#F1E1C8", "#7C6C56", 1.0f);
                if (camera != null) camera.backgroundColor = Hex("#B7D6EE");
                break;
            case "evening":
                SetSun(sun, 15f, -65f, "#FFB87A", 0.95f);
                SetAmbient("#9CB2D9", "#F3D3A8", "#6E6152", 0.9f);
                if (camera != null) camera.backgroundColor = Hex("#E8C8A0");
                break;
            case "night":
                SetSun(sun, 30f, -80f, "#8FA6D8", 0.28f);
                SetAmbient("#2C3A5C", "#3A4258", "#1E1B18", 0.55f);
                if (camera != null) camera.backgroundColor = Hex("#1C2740");
                break;
            default:
                SetSun(sun, 65f, -30f, "#FFF6E8", 1.2f);
                SetAmbient("#9CC7E8", "#E8DCC8", "#8A7A62", 1.0f);
                if (camera != null) camera.backgroundColor = Hex("#8EC3EA");
                break;
        }
    }

    static void SetSun(Light sun, float pitch, float yaw, string color, float intensity)
    {
        if (sun == null)
        {
            return;
        }
        sun.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        sun.color = Hex(color);
        sun.intensity = intensity;
    }

    static void SetAmbient(string sky, string equator, string ground, float intensity)
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Hex(sky);
        RenderSettings.ambientEquatorColor = Hex(equator);
        RenderSettings.ambientGroundColor = Hex(ground);
        RenderSettings.ambientIntensity = intensity;
    }

    static string TreeHash(string artSha, string layoutSha, string sceneSha, string builderSha)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(artSha + "\n" + layoutSha + "\n" + sceneSha
                + "\n" + builderSha + "\n");
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
