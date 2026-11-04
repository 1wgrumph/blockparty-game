// CONTENT-IMPORT editor entry points (M4). Imports every landed art package and
// assembles Assets/Blockparty/Scenes/Main.unity from it:
//   BLOCK-WORLD block kit + ENV-CATALOG dressed modules (both authored in world
//   space), PROP-CATALOG furnishings as the prop library, and the 16 STAGE-*
//   body/stage variants wired through StageVariants.Resolve into MainDirector,
//   dressed from CLOTH-CATALOG (wardrobe glTF, catalog fits, appearance textures)
//   and animated from ANIM-CATALOG (action clips, flow catalog, facial morphs).
//
//   Unity -batchmode -nographics -projectPath <copy> \
//     -executeMethod MainSceneBuild.BuildScene -logFile <import log>
//   DISPLAY=:0 Unity -projectPath <copy> \
//     -executeMethod MainSceneBuild.Capture -logFile <capture log>
//   DISPLAY=:0 Unity -projectPath <copy> \
//     -executeMethod MainSceneBuild.PlayEntries -logFile <entries log>
//   Unity -batchmode -nographics -projectPath <copy> \
//     -executeMethod MainSceneBuild.BuildPlayer -logFile evidence/production/CONTENT-IMPORT/unity-build.log
//   Unity -batchmode -nographics -projectPath <copy> -executeMethod MainSceneBuild.BuildLodNav
//   Unity -batchmode -nographics -projectPath <copy> -executeMethod MainSceneBuild.BuildLodNavPlayer
//
// BuildScene writes evidence/production/CONTENT-IMPORT/import.json (one row per
// imported asset); Capture writes captures/<name>.png with a <name>.json sidecar.
// JSON is built with a small stdlib writer: the Editor assembly has no System.Text.Json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blockparty.Content;
using Blockparty.Simulation;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static FrameCapture;

public static class MainSceneBuild
{
    public const string ScenePath = "Assets/Blockparty/Scenes/Main.unity";
    const string World = "Assets/Blockparty/Art/Environment/Block/block-world.gltf";
    const string Env = "Assets/Blockparty/Art/Environment/block.gltf";
    const string Trail = "Assets/Blockparty/Art/Environment/Block/bike-trail.gltf";
    const string EnvCatalog = "Assets/Blockparty/Art/Environment/catalog.json";
    const string Props = "Assets/Blockparty/Art/Props/furnishings.gltf";
    const string PropCatalog = "Assets/Blockparty/Art/Props/catalog.json";
    const string Layout = "Assets/Blockparty/Content/block-layout.json";
    const string EnvLayout = "evidence/production/ENV-CATALOG/source/layout.json";
    const string VolumeProfilePath = "Assets/Blockparty/Presentation/Slice/Vibrant.asset";
    const string Builder = "Assets/Blockparty/Editor/MainSceneBuild.cs";
    const string Director = "Assets/Blockparty/Presentation/MainDirector.cs";
    const string Resolver = "Assets/Blockparty/Content/StageVariants.cs";
    const string ClothCatalog = "Assets/Blockparty/Art/Clothing/catalog.json";
    // SKIN-TONES (M5-OD-45): the seven SkinTone presets, lightest first; CLOTH keeps its MST anchors.
    const string SkinTones = "evidence/production/SKIN-TONES/skin-tones.json";
    const string Wardrobe = "Assets/Blockparty/Art/Clothing/wardrobe.gltf";
    // Unity copies of the CLOTH appearance textures (the catalog keeps them under evidence/).
    const string AppearanceDir = "Assets/Blockparty/Presentation/Appearance";
    const string AnimCatalog = "Assets/Blockparty/Art/Animations/catalog.json";
    const string Actions = "Assets/Blockparty/Art/Animations/actions.gltf";
    // ANIM-CATALOG's carrier pose source (render_captures.add_carrier: the landed ANIM-UAL hold take).
    const string HoldSource = "Assets/Blockparty/Art/Animations/MakeHuman/first.gltf";
    const string ActionCatalog = "Assets/Blockparty/Content/action-catalog.json";
    const string StagePresets = "Assets/Blockparty/Art/Rigs/MakeHuman/stage-presets.json";
    const string Feelings = "Assets/Blockparty/Presentation/feelings.bytes";
    const string SeatFacing = "evidence/production/ANIM-CATALOG/seat-facing.json";
    const string MaskShader = "Assets/Blockparty/Presentation/MaskId.shader";
    const string Motion = "Assets/Blockparty/Presentation/ResidentMotion.cs";
    const string Root = "evidence/production/CONTENT-IMPORT";
    const int Seed = 42;
    const int Width = 1280, Height = 720;
    static readonly Vector3 Courtyard = new Vector3(3f, 0f, 25f);
    // Open ground south of the block; stage cameras look away from the buildings.
    static readonly Vector3 Stage = new Vector3(5f, 0f, -60f);

    static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }
    static string Abs(string relative) { return Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar)); }

    static string Sha(string relative)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(Abs(relative)))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    static IEnumerable<KeyValuePair<BaseBody, LifeStage>> Pairs()
    {
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody)))
            foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
                yield return new KeyValuePair<BaseBody, LifeStage>(body, stage);
    }

    static void Run(string name, Action action)
    {
        int code = 0;
        try { action(); Debug.Log("CONTENT-IMPORT " + name + " PASS"); }
        catch (Exception e) { code = 1; Debug.LogError("CONTENT-IMPORT " + name + " FAIL " + e); }
        EditorApplication.Exit(code);
    }

    // ----------------------------------------------------------------- import
    public static void BuildScene() { Run("build", BuildSceneNow); }

    static void BuildSceneNow()
    {
        AssetDatabase.Refresh();
        var report = Import();
        var anim = ImportAnim(report);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Place(scene, World, "BlockWorld");
        var env = Place(scene, Env, "EnvCatalog");
        Place(scene, Trail, "BikeTrail");  // GYM-TRAIL's park bike trail, authored in block world space
        var appearance = ImportAppearance();
        var library = Place(scene, Props, "PropLibrary");
        library.transform.position = new Vector3(0f, 0f, -70f);
        library.SetActive(false);
        Light deepFill;
        var fill = BuildLighting(scene, env, out deepFill);

        var residents = new GameObject("Residents");
        SceneManager.MoveGameObjectToScene(residents, scene);
        var directorObject = new GameObject("MainDirector");
        SceneManager.MoveGameObjectToScene(directorObject, scene);
        var director = directorObject.AddComponent<MainDirector>();
        var paths = new List<string>();
        var prefabs = new List<GameObject>();
        foreach (var pair in Pairs())
        {
            string path = StageVariants.Resolve(pair.Key, pair.Value);
            paths.Add(path);
            prefabs.Add(AssetDatabase.LoadMainAssetAtPath(path) as GameObject);
        }
        director.variantPaths = paths.ToArray();
        director.variantPrefabs = prefabs.ToArray();
        director.residentRoot = residents.transform;
        director.propLibrary = library.transform;
        director.residentFill = fill;
        director.deepSkinFill = deepFill;
        director.spawn = Spawn(scene, env, out director.courtyard);
        director.clothCatalog = AssetDatabase.LoadAssetAtPath<TextAsset>(ClothCatalog);
        director.wardrobe = AssetDatabase.LoadMainAssetAtPath(Wardrobe) as GameObject;
        director.skinTones = appearance.skin;
        director.faces = appearance.face;
        director.eyeColors = appearance.eye;
        director.hairColors = appearance.hair;
        director.animCatalog = AssetDatabase.LoadAssetAtPath<TextAsset>(AnimCatalog);
        director.actionCatalog = AssetDatabase.LoadAssetAtPath<TextAsset>(ActionCatalog);
        director.stagePresets = AssetDatabase.LoadAssetAtPath<TextAsset>(StagePresets);
        director.feelings = AssetDatabase.LoadAssetAtPath<TextAsset>(Feelings);
        director.actionRig = AssetDatabase.LoadMainAssetAtPath(Actions) as GameObject;
        director.clips = anim.clips;
        director.holdClip = anim.hold;
        director.maskShader = AssetDatabase.LoadAssetAtPath<Shader>(MaskShader);
        if (director.maskShader == null) throw new Exception("missing " + MaskShader);
        directorObject.AddComponent<MainPlay>().director = director;
        foreach (var bound in new UnityEngine.Object[] { director.animCatalog, director.actionCatalog, director.stagePresets,
                     director.feelings, director.actionRig, director.holdClip })
            if (bound == null) throw new Exception("ANIM-CATALOG input not imported");

        var camera = BuildCamera("MainCamera", new Vector3(5f, 48f, -45f), new Vector3(5f, 0f, 10f));
        camera.tag = "MainCamera";
        SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
        ApplyLodNav(scene);
        if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new Exception("could not save " + ScenePath);
        AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceUpdate);
        report.Add(Obj("path", Q(ScenePath), "sha256", Q(Sha(ScenePath)), "kind", Q("scene")));
        Directory.CreateDirectory(Abs(Root));
        File.WriteAllText(Abs(Root + "/import.json"), Obj(
            "package", Q("CONTENT-IMPORT"), "host", Q(Host), "project_copy", Q(ProjectCopy),
            "unity", Q(Application.unityVersion), "builder", Q(Sha(Builder)),
            "assets", "[" + string.Join(",", report) + "]",
            "appearance_catalog", Obj("path", Q(ClothCatalog), "sha256", Q(Sha(ClothCatalog))),
            "household_centre", Floats(new[] { director.courtyard.x, director.courtyard.y, director.courtyard.z }),
            "household_slots", "[" + string.Join(",", Array.ConvertAll(director.spawn, p => Floats(new[] { p.x, p.y, p.z }))) + "]",
            "household_rows", Rows.Length.ToString(CultureInfo.InvariantCulture),
            "appearance", appearance.rows) + "\n");
        Debug.Log("CONTENT-IMPORT scene_sha256=" + Sha(ScenePath));
    }

    // Force-imports every landed package and measures each asset in a scratch
    // scene: an asset with no renderers, a missing or error-shader material, or a
    // non-positive height is an import defect and fails the build.
    static List<string> Import()
    {
        var rows = new List<string>();
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        foreach (string data in new[] { EnvCatalog, PropCatalog, Layout, ClothCatalog })
        {
            AssetDatabase.ImportAsset(data, ImportAssetOptions.ForceUpdate);
            rows.Add(Obj("path", Q(data), "sha256", Q(Sha(data)), "kind", Q("catalog")));
        }
        var assets = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(World, "BLOCK-WORLD"),
            new KeyValuePair<string, string>(Env, "ENV-CATALOG"),
            new KeyValuePair<string, string>(Props, "PROP-CATALOG"),
            new KeyValuePair<string, string>(Wardrobe, "CLOTH-CATALOG"),
        };
        var variantOf = new Dictionary<string, KeyValuePair<BaseBody, LifeStage>>();
        foreach (var pair in Pairs())
        {
            string path = StageVariants.Resolve(pair.Key, pair.Value);
            variantOf[path] = pair;
            assets.Add(new KeyValuePair<string, string>(path, "STAGE-" + pair.Value.ToString().ToUpperInvariant()));
        }
        foreach (var asset in assets)
        {
            AssetDatabase.ImportAsset(asset.Key, ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadMainAssetAtPath(asset.Key) as GameObject;
            if (prefab == null) throw new Exception("not imported as a model: " + asset.Key);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scratch);
            int renderers = 0, skinned = 0, materials = 0, missing = 0;
            var shaders = new SortedSet<string>();
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                if (renderer is SkinnedMeshRenderer) skinned++;
                if (!any) { bounds = renderer.bounds; any = true; } else bounds.Encapsulate(renderer.bounds);
                foreach (var material in renderer.sharedMaterials)
                {
                    materials++;
                    if (material == null || material.shader == null || !material.shader.isSupported
                        || material.shader.name.StartsWith("Hidden/InternalErrorShader", StringComparison.Ordinal))
                        missing++;
                    else shaders.Add(material.shader.name);
                }
            }
            UnityEngine.Object.DestroyImmediate(instance);
            if (renderers == 0 || missing > 0 || bounds.size.y <= 0f)
                throw new Exception(asset.Key + " import defect renderers=" + renderers + " missing_materials=" + missing
                    + " height=" + bounds.size.y);
            var fields = new List<string>
            {
                "path", Q(asset.Key), "sha256", Q(Sha(asset.Key)), "kind", Q("model"), "package", Q(asset.Value),
                "renderers", renderers.ToString(CultureInfo.InvariantCulture),
                "skinned", skinned.ToString(CultureInfo.InvariantCulture),
                "materials", materials.ToString(CultureInfo.InvariantCulture),
                "missing_materials", missing.ToString(CultureInfo.InvariantCulture),
                "shaders", Strings(new List<string>(shaders).ToArray()),
                "size_m", Floats(new[] { bounds.size.x, bounds.size.y, bounds.size.z }),
            };
            KeyValuePair<BaseBody, LifeStage> variant;
            if (variantOf.TryGetValue(asset.Key, out variant))
            {
                fields.AddRange(new[] { "body", Q(variant.Key.ToString()), "stage", Q(variant.Value.ToString()),
                    "presentation", Q(MainDirector.Presentation(variant.Value)) });
            }
            rows.Add(Obj(fields.ToArray()));
            Debug.Log("CONTENT-IMPORT import " + asset.Value + " " + asset.Key + " renderers=" + renderers
                + " materials=" + materials + " missing=0 height=" + bounds.size.y.ToString("F3", CultureInfo.InvariantCulture));
        }
        return rows;
    }

    sealed class Anim { public AnimationClip[] clips; public AnimationClip hold; }

    // Imports the ANIM-CATALOG action pack and checks every glTF animation came in as a
    // clip with curves on the action rig; one import.json "animation" row lists them.
    static Anim ImportAnim(List<string> rows)
    {
        foreach (string data in new[] { AnimCatalog, ActionCatalog, StagePresets, Feelings })
        {
            AssetDatabase.ImportAsset(data, ImportAssetOptions.ForceUpdate);
            rows.Add(Obj("path", Q(data), "sha256", Q(Sha(data)), "kind", Q("catalog")));
        }
        var result = new Anim();
        foreach (string path in new[] { Actions, HoldSource })
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var wanted = new List<string>();
            foreach (var a in JObject.Parse(File.ReadAllText(Abs(path)))["animations"]) wanted.Add((string)a["name"]);
            var found = new Dictionary<string, AnimationClip>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var clip = asset as AnimationClip;
                if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.Ordinal)) found[clip.name] = clip;
            }
            var missing = wanted.FindAll(n => !found.ContainsKey(n) || AnimationUtility.GetCurveBindings(found[n]).Length == 0);
            if (missing.Count > 0)
                throw new Exception(path + " clips not imported: " + string.Join(",", missing) + " (imported: "
                    + string.Join(",", new List<string>(found.Keys)) + ")");
            if (path == Actions) result.clips = wanted.ConvertAll(n => found[n]).ToArray();
            else result.hold = found["hold"];
            var clips = new List<string>();
            foreach (string n in wanted)
                clips.Add(Obj("name", Q(n), "length_s", found[n].length.ToString("R", CultureInfo.InvariantCulture),
                    "curves", AnimationUtility.GetCurveBindings(found[n]).Length.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Obj("path", Q(path), "sha256", Q(Sha(path)), "kind", Q("animation"),
                "package", Q(path == Actions ? "ANIM-CATALOG" : "ANIM-UAL"), "clips", "[" + string.Join(",", clips) + "]"));
            Debug.Log("CONTENT-IMPORT import animation " + path + " clips=" + wanted.Count);
        }
        return result;
    }

    sealed class Appearance { public Texture2D[] skin, face, eye; public Color[] hair; public string rows; }

    // Imports every creator preset of both bodies from CLOTH-CATALOG (skin tones from
    // SKIN-TONES) and checks it
    // resolves on every stage variant: texture dimensions are copied into Unity and
    // bound per (body, preset); hair colour is the resource's mean sRGB colour; each
    // wardrobe item exists in every body/stage cell with the stage body's joint
    // count, and its culled faces index that body's triangles. One import.json
    // "appearance" row per (body, dimension, preset index).
    static Appearance ImportAppearance()
    {
        var doc = JObject.Parse(File.ReadAllText(Abs(ClothCatalog)));
        var dims = doc["dimensions"];
        var wardrobe = AssetDatabase.LoadMainAssetAtPath(Wardrobe) as GameObject;
        var nodes = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var renderer in wardrobe.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            nodes[renderer.transform.name] = renderer;
        var fits = new Dictionary<string, JToken>();
        foreach (var fit in doc["fits"]) fits[(string)fit["body"] + "-" + (string)fit["stage"]] = fit;
        var result = new Appearance
        {
            skin = new Texture2D[2 * ResidentFactory.SkinToneCount], face = new Texture2D[2 * ResidentFactory.FaceCount],
            eye = new Texture2D[2 * ResidentFactory.EyeColorCount], hair = new Color[2 * ResidentFactory.HairColorCount],
        };
        var rows = new List<string>();
        string wardrobeRef = Obj("path", Q(Wardrobe), "sha256", Q(Sha(Wardrobe)));
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody)))
        {
            string name = body.ToString().ToLowerInvariant();
            int b = (int)body;
            // Every wardrobe item of this body must dress every stage variant.
            foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
            {
                string cell = name + "-" + stage;
                var variant = AssetDatabase.LoadMainAssetAtPath(StageVariants.Resolve(body, stage)) as GameObject;
                SkinnedMeshRenderer skin = null;
                foreach (var r in variant.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (skin == null || r.sharedMesh.vertexCount > skin.sharedMesh.vertexCount) skin = r;
                int faces = (int)skin.sharedMesh.GetIndexCount(0) / 3;
                foreach (var item in (JObject)fits[cell]["items"])
                {
                    SkinnedMeshRenderer node;
                    if (!nodes.TryGetValue(item.Key + "__" + cell, out node))
                        throw new Exception("wardrobe has no " + item.Key + "__" + cell);
                    if (node.bones.Length != skin.bones.Length)
                        throw new Exception(item.Key + "__" + cell + " joints " + node.bones.Length + " != stage body " + skin.bones.Length);
                    foreach (int face in item.Value["culled_faces"].ToObject<int[]>())
                        if (face >= faces) throw new Exception(item.Key + "__" + cell + " culls face " + face + " of " + faces);
                }
            }
            foreach (var spec in new[] { "skin_tone|SkinTone", "face|Face", "eye_color|EyeColor", "hair_color|HairColor" })
            {
                string key = spec.Split('|')[0];
                var dimension = (AppearanceDimension)Enum.Parse(typeof(AppearanceDimension), spec.Split('|')[1]);
                var catalog = key == "skin_tone" ? (JArray)JObject.Parse(File.ReadAllText(Abs(SkinTones)))["tones"] : (JArray)dims[key];
                int count = ResidentFactory.OptionCount(dimension);
                for (int index = 0; index < count; index++)
                {
                    // Creator presets span the catalog evenly (every catalog now has one row per preset).
                    int entry = count == catalog.Count ? index : Mathf.RoundToInt(index * (catalog.Count - 1f) / (count - 1f));
                    var row = catalog[entry];
                    string source = (string)row["resources"][name]["path"];
                    if (Sha(source) != (string)row["resources"][name]["sha256"]) throw new Exception("changed CLOTH resource " + source);
                    var fields = new List<string> { "body", Q(body.ToString()), "dimension", Q(dimension.ToString()),
                        "index", index.ToString(CultureInfo.InvariantCulture), "catalog_id", Q((string)row["id"]),
                        "asset", Obj("path", Q(source), "sha256", Q(Sha(source))) };
                    if (key == "hair_color")
                    {
                        var probe = new Texture2D(2, 2);
                        probe.LoadImage(File.ReadAllBytes(Abs(source)));
                        var pixels = probe.GetPixels32();
                        long r = 0, g = 0, bl = 0;
                        foreach (var px in pixels) { r += px.r; g += px.g; bl += px.b; }
                        UnityEngine.Object.DestroyImmediate(probe);
                        var mean = new Color32((byte)(r / pixels.Length), (byte)(g / pixels.Length), (byte)(bl / pixels.Length), 255);
                        result.hair[b * count + index] = mean;
                        fields.AddRange(new[] { "srgb", Q("#" + ColorUtility.ToHtmlStringRGB(mean)) });
                    }
                    else
                    {
                        string copy = AppearanceDir + "/" + key + "/" + Path.GetFileName(source);
                        Texture2D texture = CopyTexture(source, copy);
                        (key == "skin_tone" ? result.skin : key == "face" ? result.face : result.eye)[b * count + index] = texture;
                        fields.AddRange(new[] { "unity_asset", Obj("path", Q(copy), "sha256", Q(Sha(copy))) });
                    }
                    rows.Add(Obj(fields.ToArray()));
                }
            }
            string[] styles = Ids(dims["hair_style"]), tops = Ids(dims["top"]), bottoms = Ids(dims["bottom"]),
                shoes = Ids(dims["footwear"]), accessories = Ids(dims["accessory"]);
            for (int type = 0; type < ResidentFactory.HairTypeCount; type++)
            {
                var items = new List<string>();
                foreach (int style in ResidentFactory.ValidHairStyles(type)) items.Add(styles[style]);
                rows.Add(WardrobeRow(body, AppearanceDimension.HairType, type, items.ToArray(), wardrobeRef));
            }
            for (int style = 0; style < ResidentFactory.HairStyleCount; style++)
                rows.Add(WardrobeRow(body, AppearanceDimension.HairStyle, style, new[] { styles[style] }, wardrobeRef));
            for (int clothing = 0; clothing < ResidentFactory.ClothingComboCount; clothing++)
            {
                int top, bottom, footwear;
                ResidentFactory.UnpackClothing(clothing, out top, out bottom, out footwear);
                rows.Add(WardrobeRow(body, AppearanceDimension.Clothing, clothing,
                    new[] { tops[top], bottoms[bottom], shoes[footwear] }, wardrobeRef));
            }
            for (int accessory = 0; accessory < ResidentFactory.AccessoryCount; accessory++)
                rows.Add(WardrobeRow(body, AppearanceDimension.Accessory, accessory, new[] { accessories[accessory] }, wardrobeRef));
        }
        result.rows = "[" + string.Join(",", rows) + "]";
        Debug.Log("CONTENT-IMPORT appearance rows=" + rows.Count);
        return result;
    }

    static string WardrobeRow(BaseBody body, AppearanceDimension dimension, int index, string[] items, string wardrobeRef)
    {
        return Obj("body", Q(body.ToString()), "dimension", Q(dimension.ToString()),
            "index", index.ToString(CultureInfo.InvariantCulture), "items", Strings(items), "asset", wardrobeRef);
    }

    static string[] Ids(JToken rows)
    {
        var ids = new List<string>();
        foreach (var row in rows)
        {
            if ((int)row["index"] != ids.Count) throw new Exception("CLOTH catalog index order " + row["id"]);
            ids.Add((string)row["id"]);
        }
        return ids.ToArray();
    }

    // Byte-identical copy, imported readable and uncompressed for the runtime skin+face composite.
    static Texture2D CopyTexture(string source, string copy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(copy)));
        var bytes = File.ReadAllBytes(Abs(source));
        if (!File.Exists(Abs(copy)) || Sha(copy) != Sha(source)) File.WriteAllBytes(Abs(copy), bytes);
        AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(copy);
        importer.isReadable = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.sRGBTexture = true;
        importer.SaveAndReimport();
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(copy);
        if (texture == null) throw new Exception("not imported as a texture: " + copy);
        return texture;
    }

    static GameObject Place(Scene scene, string path, string name)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        if (prefab == null) throw new Exception("missing imported " + path);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.name = name;
        // Both block glTFs are authored in world space; glTFast already reflected X.
        instance.transform.position = Vector3.zero;
        return instance;
    }

    // ---------------------------------------------------------------- LOD-NAV
    // Every top-level node of the placed block exports becomes a LODGroup: LOD0 is
    // the imported source subtree, LOD1/LOD2 the make_lods.py meshes (glTF world
    // space, one primitive per LOD0 material). The LOD0 meshes are the collision
    // proxies; BlockWorldNavigation bakes its walkable grid from them.
    const string LodDir = "evidence/production/LOD-NAV/lod";
    const string LodPlayer = "Build/LOD-NAV/LodNav.x86_64";
    // { manifest id prefix, source glTF, scene instance, LOD file stem }
    static readonly string[][] LodSources =
    {
        new[] { "world", World, "BlockWorld", "block-world" },
        new[] { "env", Env, "EnvCatalog", "block" },
        new[] { "trail", Trail, "BikeTrail", "bike-trail" },
    };
    // Screen heights before QualitySettings.lodBias (2 on the default tier); LOD2 is never culled.
    static readonly float[] LodHeights = { 0.3f, 0.1f, 0f };

    public static void BuildLodNav()
    {
        Run("lod-nav", () =>
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            ApplyLodNav(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new Exception("could not save " + ScenePath);
            Debug.Log("LOD-NAV scene_sha256=" + Sha(ScenePath));
        });
    }

    public static void BuildLodNavPlayer()
    {
        Run("lod-nav-player", () =>
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Abs(LodPlayer),
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.Development,
            });
            Debug.Log("LOD-NAV player result=" + report.summary.result + " errors=" + report.summary.totalErrors);
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("LOD-NAV player build " + report.summary.result);
        });
    }

    static void ApplyLodNav(Scene scene)
    {
        var old = GameObject.Find("BlockWorldNavigation");
        if (old != null) UnityEngine.Object.DestroyImmediate(old);
        var ids = new List<string>();
        var assets = new List<string>();
        var nodes = new List<int>();
        var groups = new List<LODGroup>();
        Dictionary<int, Transform> envNodes = null;
        JObject envDoc = null;
        foreach (var source in LodSources)
        {
            var doc = JObject.Parse(File.ReadAllText(Abs(source[1])));
            var instance = GameObject.Find(source[2]);
            if (instance == null) throw new Exception("LOD-NAV: Main.unity has no " + source[2]);
            var map = NodeMap(doc, instance.transform);
            var lods = new[] { LoadLod(source[3], 1), LoadLod(source[3], 2) };
            foreach (int node in doc["scenes"][(int?)doc["scene"] ?? 0]["nodes"])
            {
                string name = (string)doc["nodes"][node]["name"];
                var root = map[node];
                foreach (var stale in root.GetComponentsInChildren<Transform>(true))
                    if (stale != null && stale.name.StartsWith(name + "__LOD", StringComparison.Ordinal))
                        UnityEngine.Object.DestroyImmediate(stale.gameObject);
                foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true)) UnityEngine.Object.DestroyImmediate(collider);
                var existing = root.GetComponent<LODGroup>();
                if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
                var lod0 = root.GetComponentsInChildren<Renderer>(true);
                var materials = new Dictionary<string, Material>();
                foreach (var renderer in lod0)
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null) materials[LodMaterialKey(material)] = material;
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                var levels = new List<LOD> { new LOD(LodHeights[0], lod0) };
                for (int k = 1; k <= 2; k++)
                {
                    var go = LodObject(lods[k - 1], name + "__LOD" + k, materials);
                    go.transform.SetParent(root, true);
                    levels.Add(new LOD(LodHeights[k], new Renderer[] { go.GetComponent<MeshRenderer>() }));
                }
                var group = root.gameObject.AddComponent<LODGroup>();
                group.SetLODs(levels.ToArray());
                group.RecalculateBounds();
                ids.Add(source[0] + "/" + name);
                assets.Add(source[1]);
                nodes.Add(node);
                groups.Add(group);
            }
            if (source[1] == Env) { envNodes = map; envDoc = doc; }
        }
        var holder = new GameObject("BlockWorldNavigation");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var nav = holder.AddComponent<BlockWorldNavigation>();
        nav.objectIds = ids.ToArray();
        nav.objectAssets = assets.ToArray();
        nav.objectNodes = nodes.ToArray();
        nav.objectGroups = groups.ToArray();
        var slotIds = new List<string>();
        var approach = new List<Vector3>();
        var targets = new List<Transform>();
        foreach (var slot in envDoc["asset"]["extras"]["blockparty"]["interaction_slots"])
        {
            slotIds.Add((string)slot["id"]);
            approach.Add(envNodes[(int)slot["node"]].position);
            targets.Add(envNodes[(int)slot["target_node"]]);
        }
        nav.slotIds = slotIds.ToArray();
        nav.slotApproach = approach.ToArray();
        nav.slotTargets = targets.ToArray();
        var entranceIds = new List<string>();
        var entrances = new List<Vector3>();
        foreach (var module in JObject.Parse(File.ReadAllText(Abs(EnvCatalog)))["modules"])
        {
            entranceIds.Add((string)module["id"]);
            entrances.Add(envNodes[(int)module["entry_node"]].position);
        }
        nav.entranceIds = entranceIds.ToArray();
        nav.entrancePositions = entrances.ToArray();
        var overlay = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "LodNavWalkable" };
        overlay.SetColor("_BaseColor", new Color(0.1f, 0.75f, 1f));
        nav.overlayMaterial = overlay;
        Debug.Log("LOD-NAV objects=" + ids.Count + " slots=" + slotIds.Count + " entrances=" + entranceIds.Count);
    }

    // glTF node index -> imported Transform, following glTFast's node-named hierarchy.
    // ENV's block.gltf reuses palette names for trim-textured and coloured-factor materials (GYM-TRAIL
    // kit); make_lods.py names LOD materials the same way, so the pair stays distinct.
    static string LodMaterialKey(Material material)
    {
        if (!material.HasProperty("baseColorFactor")) return material.name;
        var c = material.GetColor("baseColorFactor");
        return Mathf.Abs(c.r - 1f) > 1e-3f || Mathf.Abs(c.g - 1f) > 1e-3f || Mathf.Abs(c.b - 1f) > 1e-3f ? material.name + "~c" : material.name;
    }

    static Dictionary<int, Transform> NodeMap(JObject doc, Transform instance)
    {
        var map = new Dictionary<int, Transform>();
        foreach (int node in doc["scenes"][(int?)doc["scene"] ?? 0]["nodes"])
        {
            string name = (string)doc["nodes"][node]["name"];
            Transform found = instance.Find(name);
            if (found == null)
                foreach (Transform child in instance) if (found == null) found = child.Find(name);
            if (found == null) throw new Exception("LOD-NAV: no imported root " + name + " under " + instance.name);
            MapChildren(doc, node, found, map);
        }
        return map;
    }

    static void MapChildren(JObject doc, int node, Transform t, Dictionary<int, Transform> map)
    {
        map[node] = t;
        var children = doc["nodes"][node]["children"];
        if (children == null) return;
        foreach (int child in children)
        {
            string name = (string)doc["nodes"][child]["name"];
            var found = t.Find(name);
            if (found == null) throw new Exception("LOD-NAV: no imported node " + name + " under " + t.name);
            MapChildren(doc, child, found, map);
        }
    }

    sealed class LodFile { public JObject doc; public byte[] data; }

    static LodFile LoadLod(string stem, int level)
    {
        var doc = JObject.Parse(File.ReadAllText(Abs(LodDir + "/" + stem + "-lod" + level + ".gltf")));
        string uri = (string)doc["buffers"][0]["uri"];
        return new LodFile { doc = doc, data = Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1)) };
    }

    static float[] LodFloats(LodFile f, int accessor, int width)
    {
        var a = f.doc["accessors"][accessor];
        var view = f.doc["bufferViews"][(int)a["bufferView"]];
        int count = (int)a["count"], offset = ((int?)view["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0);
        int stride = (int?)view["byteStride"] ?? 4 * width;
        var values = new float[count * width];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < width; c++) values[i * width + c] = BitConverter.ToSingle(f.data, offset + i * stride + 4 * c);
        return values;
    }

    static int[] Indices(LodFile f, int accessor)
    {
        var a = f.doc["accessors"][accessor];
        var view = f.doc["bufferViews"][(int)a["bufferView"]];
        int count = (int)a["count"], type = (int)a["componentType"];
        int offset = ((int?)view["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0);
        var values = new int[count];
        for (int i = 0; i < count; i++)
            values[i] = type == 5125 ? (int)BitConverter.ToUInt32(f.data, offset + 4 * i)
                : type == 5123 ? BitConverter.ToUInt16(f.data, offset + 2 * i) : f.data[offset + i];
        return values;
    }

    // One Unity mesh per LOD node; glTF -> Unity as glTFast does it (x reflected, winding and V flipped).
    static GameObject LodObject(LodFile f, string nodeName, Dictionary<string, Material> materials)
    {
        JToken node = null;
        foreach (var n in f.doc["nodes"]) if ((string)n["name"] == nodeName) node = n;
        if (node == null || node["mesh"] == null) throw new Exception("LOD-NAV: missing LOD node " + nodeName);
        if (node["matrix"] != null || node["translation"] != null || node["rotation"] != null || node["scale"] != null)
            throw new Exception("LOD-NAV: LOD node " + nodeName + " is not in world space");
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var submeshes = new List<int[]>();
        var shared = new List<Material>();
        foreach (var primitive in f.doc["meshes"][(int)node["mesh"]]["primitives"])
        {
            var attributes = primitive["attributes"];
            int baseVertex = vertices.Count;
            var p = LodFloats(f, (int)attributes["POSITION"], 3);
            var nrm = attributes["NORMAL"] != null ? LodFloats(f, (int)attributes["NORMAL"], 3) : null;
            var uv = attributes["TEXCOORD_0"] != null ? LodFloats(f, (int)attributes["TEXCOORD_0"], 2) : null;
            for (int i = 0; i < p.Length / 3; i++)
            {
                vertices.Add(new Vector3(-p[3 * i], p[3 * i + 1], p[3 * i + 2]));
                normals.Add(nrm != null ? new Vector3(-nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]) : Vector3.up);
                uvs.Add(uv != null ? new Vector2(uv[2 * i], 1f - uv[2 * i + 1]) : Vector2.zero);
            }
            var ids = Indices(f, (int)primitive["indices"]);
            var tris = new int[ids.Length];
            for (int i = 0; i < ids.Length; i += 3)
            {
                tris[i] = baseVertex + ids[i];
                tris[i + 1] = baseVertex + ids[i + 2];
                tris[i + 2] = baseVertex + ids[i + 1];
            }
            submeshes.Add(tris);
            string material = (string)f.doc["materials"][(int)primitive["material"]]["name"];
            Material found;
            if (!materials.TryGetValue(material, out found) && !(material.EndsWith("~c", StringComparison.Ordinal)
                && materials.TryGetValue(material.Substring(0, material.Length - 2), out found)))
                throw new Exception("LOD-NAV: " + nodeName + " material " + material + " not on LOD0");
            shared.Add(found);
        }
        var mesh = new Mesh { name = nodeName, indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = submeshes.Count;
        for (int i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i);
        mesh.RecalculateBounds();
        var go = new GameObject(nodeName);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = shared.ToArray();
        return go;
    }

    // ------------------------------------------------------------- household
    // Two rows facing the default camera (-Z): Newborn..Child in front, Teen..Elder behind.
    static readonly Vector3[] Rows =
    {
        new Vector3(-1.5f, 0f, -0.8f), new Vector3(-0.5f, 0f, -0.8f), new Vector3(0.5f, 0f, -0.8f), new Vector3(1.5f, 0f, -0.8f),
        new Vector3(-2.55f, 0f, 0.8f), new Vector3(-0.85f, 0f, 0.8f), new Vector3(0.85f, 0f, 0.8f), new Vector3(2.55f, 0f, 0.8f),
    };
    // Courtyard capture cameras, relative to the household centre.
    static readonly Vector3 WideCamera = new Vector3(-5f, 2.6f, -7f), CloseCamera = new Vector3(-2.2f, 1.7f, -4.6f);

    // The household centre nearest the block's courtyard where every slot stands on
    // walkable ground (first hit from above is near-level ground under 0.2 m), lies
    // outside every building footprint (BLOCK-WORLD shells/facades, ENV interiors) by
    // 0.5 m, has a clear 0.3 m body capsule, and is in clear view of both courtyard
    // cameras. Uses temporary mesh colliders, removed before the scene is saved.
    static Vector3[] Spawn(Scene scene, GameObject env, out Vector3 centre)
    {
        var temporary = new List<MeshCollider>();
        var buildings = new List<Bounds>();
        try
        {
            foreach (var filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null || filter.gameObject.scene != scene) continue;
                string name = filter.gameObject.name;
                if (name == "sky" || name == "horizon") continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                temporary.Add(collider);
                if (name.StartsWith("shell-", StringComparison.Ordinal) || name.StartsWith("facade-", StringComparison.Ordinal))
                    buildings.Add(filter.GetComponent<Renderer>().bounds);
            }
            foreach (string id in Interiors)
            {
                var module = FindDeep(env, id);
                if (module == null) continue;
                foreach (var renderer in module.GetComponentsInChildren<Renderer>(true)) buildings.Add(renderer.bounds);
            }
            Physics.SyncTransforms();
            for (float ring = 0f; ring <= 12f; ring += 0.5f)
                for (float x = -ring; x <= ring; x += 0.5f)
                    for (float z = -ring; z <= ring; z += 0.5f)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != ring) continue;
                        var candidate = Courtyard + new Vector3(x, 0f, z);
                        var slots = Slots(candidate, buildings);
                        if (slots == null) continue;
                        centre = candidate;
                        var all = new List<Vector3>(slots);
                        all.AddRange(Extra(candidate, buildings, all));
                        Debug.Log("CONTENT-IMPORT household centre=" + candidate + " slots=" + slots.Length + " extra=" + (all.Count - slots.Length));
                        return all.ToArray();
                    }
            throw new Exception("no household centre within 12 m of " + Courtyard + " has every slot on open walkable ground");
        }
        finally
        {
            foreach (var collider in temporary) UnityEngine.Object.DestroyImmediate(collider);
        }
    }

    static Vector3[] Slots(Vector3 centre, List<Bounds> buildings)
    {
        foreach (var camera in new[] { centre + WideCamera, centre + CloseCamera })
            if (Inside(camera, buildings, 0.5f) || Physics.CheckSphere(camera, 0.3f)) return null;
        var slots = new Vector3[Rows.Length];
        for (int i = 0; i < Rows.Length; i++)
        {
            var p = centre + Rows[i];
            RaycastHit hit;
            if (!Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out hit, 40f) || hit.point.y > 0.2f || hit.normal.y < 0.9f)
                return null;
            p.y = hit.point.y;
            if (Inside(p, buildings, 0.5f)) return null;
            if (Physics.CheckCapsule(p + Vector3.up * 0.65f, p + Vector3.up * 1.9f, 0.3f)) return null;
            foreach (var camera in new[] { centre + WideCamera, centre + CloseCamera })
                if (Physics.Linecast(camera, p + Vector3.up * 0.9f)) return null;
            slots[i] = p;
        }
        return slots;
    }

    // Slots for residents born later: nearest open ground around the household, each
    // on level walkable ground, 0.5 m outside every building, with a clear body capsule
    // and 0.9 m from every other slot.
    const int ExtraSlots = 16;
    static List<Vector3> Extra(Vector3 centre, List<Bounds> buildings, List<Vector3> taken)
    {
        var candidates = new List<Vector3>();
        for (float x = -8f; x <= 8f; x += 1f)
            for (float z = -8f; z <= 8f; z += 1f) candidates.Add(centre + new Vector3(x, 0f, z));
        candidates.Sort((a, b) => (a - centre).sqrMagnitude.CompareTo((b - centre).sqrMagnitude));
        var extra = new List<Vector3>();
        foreach (var c in candidates)
        {
            if (extra.Count == ExtraSlots) break;
            var p = c;
            RaycastHit hit;
            if (!Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out hit, 40f) || hit.point.y > 0.2f || hit.normal.y < 0.9f)
                continue;
            p.y = hit.point.y;
            if (Inside(p, buildings, 0.5f) || Physics.CheckCapsule(p + Vector3.up * 0.65f, p + Vector3.up * 1.9f, 0.3f)) continue;
            bool near = false;
            foreach (var q in taken) if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(q.x, 0f, q.z)) < 0.9f) near = true;
            foreach (var q in extra) if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(q.x, 0f, q.z)) < 0.9f) near = true;
            if (!near) extra.Add(p);
        }
        if (extra.Count < ExtraSlots) throw new Exception("only " + extra.Count + " later-born slots on open ground near " + centre);
        return extra;
    }

    static bool Inside(Vector3 p, List<Bounds> boxes, float margin)
    {
        foreach (var b in boxes)
            if (p.x > b.min.x - margin && p.x < b.max.x + margin && p.z > b.min.z - margin && p.z < b.max.z + margin) return true;
        return false;
    }

    // ---------------------------------------------------------------- capture
    public static void Capture() { Run("capture", CaptureNow); }

    [Serializable] sealed class CameraData { public float[] position; public float[] look_at; }
    [Serializable] sealed class LayoutModule { public string id; public string root; public CameraData camera; }
    [Serializable] sealed class LayoutData { public List<LayoutModule> modules; }

    // Captured household time: residents are mid-flow (start played, loop running).
    const float HouseholdSeconds = 2.5f;
    // ANIM-CATALOG author_activity.TASKS posture of each ACT take (seated ones sit on a seat prop).
    static readonly HashSet<string> SeatedActs = new HashSet<string> { "ACT-CLUB-01", "ACT-CREA-02", "ACT-EDU-01",
        "ACT-EDU-02", "ACT-EDU-03", "ACT-EDU-04", "ACT-FOOD-02", "ACT-HEALTH-02", "ACT-HEALTH-04", "ACT-REL-02",
        "ACT-WORK-03", "ACT-WORK-04" };
    // ANIM-CATALOG capture_context.PARTNERS: partner flow, distance (m), loop fraction, seated, body.
    static readonly Dictionary<string, object[]> Partners = new Dictionary<string, object[]>
    {
        { "ACT-CLUB-01", new object[] { "ACT-CLUB-01", 1.60f, 0.25f, true, BaseBody.Male } },
        { "ACT-REL-02", new object[] { "ACT-REL-02", 1.60f, 0.25f, true, BaseBody.Male } },
        { "ACT-REL-01", new object[] { "ACT-REL-01", 0.95f, 0f, false, BaseBody.Male } },
        { "ACT-REL-03", new object[] { "ACT-REL-03", 0.85f, 0.35f, false, BaseBody.Male } },
        { "ACT-REL-04", new object[] { "ACT-REL-01", 1.05f, 0.5f, false, BaseBody.Male } },
        { "ACT-REL-05", new object[] { "ACT-REL-05", 0.75f, 0.5f, false, BaseBody.Female } },
        { "ACT-REL-06", new object[] { "ACT-REL-06", 0.95f, 0.35f, false, BaseBody.Male } },
        { "ACT-PARTY-01", new object[] { "ACT-PARTY-02", 0.95f, 0.5f, false, BaseBody.Male } },
        { "ACT-PARTY-02", new object[] { "ACT-PARTY-01", 0.95f, 0.5f, false, BaseBody.Male } },
    };

    static void CaptureNow()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var director = UnityEngine.Object.FindFirstObjectByType<MainDirector>();
        if (director == null) throw new Exception("Main.unity has no MainDirector");
        for (int i = 0; i < director.variantPrefabs.Length; i++)
            if (director.variantPrefabs[i] == null) throw new Exception("unbound variant " + director.variantPaths[i]);
        if (director.actionRig == null || director.clips.Length == 0 || director.holdClip == null)
            throw new Exception("Main.unity has no ANIM-CATALOG binding");
        director.Populate();
        for (int k = 0; k < Mathf.RoundToInt(HouseholdSeconds * 30f); k++) director.Tick(1f / 30f);
        string tree = TreeHash();
        string dir = Abs(Root + "/captures");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "masks"));
        foreach (var stale in Directory.GetFiles(Path.Combine(dir, "masks"))) File.Delete(stale);
        var argv = Environment.GetCommandLineArgs();
        var household = HouseholdSubjects(director);
        foreach (var s in household)
            Debug.Log("CONTENT-IMPORT household " + s.id + " flow=" + s.flow + " phase=" + s.phase + " clip=" + s.clip
                + " t=" + s.time.ToString("F2", CultureInfo.InvariantCulture));

        foreach (int yaw in new[] { 0, 120, 240 })
            foreach (string preset in new[] { "morning", "noon", "evening", "night" })
            {
                var target = new Vector3(5f, 0f, 10f);
                float r = yaw * Mathf.Deg2Rad;
                Shot(dir, string.Format("overhead-yaw{0:D3}-{1}", yaw, preset),
                    target + new Vector3(Mathf.Sin(r) * 62f, 55f, -Mathf.Cos(r) * 62f), target, 50f, preset, tree, argv, household);
            }
        var centre = director.courtyard;
        Shot(dir, "courtyard-wide-noon", centre + WideCamera, centre + new Vector3(0f, 0.7f, 0f),
            50f, "noon", tree, argv, household);
        Shot(dir, "courtyard-residents-noon", centre + CloseCamera, centre + new Vector3(0f, 0.75f, 0f),
            45f, "noon", tree, argv, household);

        var layout = JsonUtility.FromJson<LayoutData>(File.ReadAllText(Abs(EnvLayout)));
        var env = GameObject.Find("EnvCatalog");
        foreach (string id in new[] { "apartment", "common-room" })
        {
            var module = layout.modules.Find(m => m.id == id);
            var root = FindDeep(env, module.root);
            if (root == null) throw new Exception("missing ENV module root " + module.root);
            // ENV layout cameras are raw glTF metres in module space; reflect X once.
            Shot(dir, "interior-" + id + "-noon", root.transform.TransformPoint(GlTF(V(module.camera.position))),
                root.transform.TransformPoint(GlTF(V(module.camera.look_at))), 50f, "noon", tree, argv, new List<Subject>());
        }

        // Stage frames, lineups, activities and feelings render alone on open ground.
        director.residentRoot.gameObject.SetActive(false);
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody)))
        {
            var lineup = new List<Subject>();
            float x = 0f;
            foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
            {
                var portrait = StageSubjects(director, body, stage, Stage, 1L + (int)body * 8 + (int)stage);
                var b = Extent(portrait);
                Shot(dir, "stage-" + body.ToString().ToLowerInvariant() + "-" + stage.ToString().ToLowerInvariant(),
                    b.center + new Vector3(stage <= LifeStage.Infant ? 0.6f : 0.35f, 0.1f, -1f).normalized * Fit(b, 26f), b.center, 26f,
                    "noon", tree, argv, portrait);
                // Lineup runs Newborn to Elder left to right for a camera looking along +Z; dependents in an adult's arms.
                var shift = new Vector3(Stage.x + x + b.size.x * 0.5f - b.center.x, 0f, Stage.z - b.center.z);
                foreach (var s in portrait) { s.body.transform.position += shift; s.body.SetActive(false); }
                x += b.size.x + 0.25f;
                lineup.AddRange(portrait);
            }
            foreach (var s in lineup) s.body.SetActive(true);
            var all = Extent(lineup);
            Shot(dir, "stages-" + body.ToString().ToLowerInvariant() + "-lineup",
                all.center + new Vector3(0f, 0.1f, -1f).normalized * Fit(all, 30f) * 0.7f, all.center, 30f, "noon", tree, argv, lineup);
            foreach (var s in lineup) UnityEngine.Object.DestroyImmediate(s.body);
        }

        var props = JObject.Parse(File.ReadAllText(Abs(PropCatalog)));
        var seats = JObject.Parse(File.ReadAllText(Abs(SeatFacing)));
        var seatSlots = new List<string>();
        foreach (JObject act in JObject.Parse(File.ReadAllText(Abs(ActionCatalog)))["activities"])
        {
            string code = (string)act["id"];
            var staged = new List<GameObject>();
            var subjects = StageAct(director, act, props, seats, staged);
            var b = Extent(subjects);
            foreach (var go in staged) b.Encapsulate(Extent(go));
            var front = subjects[0].body.transform.forward;
            // The GYM-TRAIL ride clip turns the rider half a turn about its root: face the rider, not the root.
            if (code == "ACT-SPORT-03") front = -front;
            var look = b.center;
            // ANIM capture_context.VIEWS: partners and props in front of the resident are seen from the side.
            bool side = Partners.ContainsKey(code) || staged.Exists(go => !go.name.StartsWith("Staged-seat", StringComparison.Ordinal)
                && go.name != "Staged-Toilet" && go.name != "Staged-second" && go.name != "Staged-ComputerDesk" && go.name != "Staged-Bike");
            var view = Quaternion.AngleAxis(side ? -65f : -20f, Vector3.up) * front;
            Shot(dir, "act-" + code + "-loop", look + (view + Vector3.up * 0.35f).normalized * Fit(b, 30f) * 0.95f, look,
                30f, "noon", tree, argv, subjects);
            if (SeatedActs.Contains(code))
            {
                string slot = (string)act["interaction_point"]["slot_id"];
                seatSlots.Add(slot);
                var profile = Quaternion.AngleAxis(-70f, Vector3.up) * front;
                Shot(dir, "seated-" + slot, look + (profile + Vector3.up * 0.3f).normalized * Fit(b, 30f) * 0.95f, look,
                    30f, "noon", tree, argv, subjects);
            }
            foreach (var go in staged) UnityEngine.Object.DestroyImmediate(go);
            foreach (var s in subjects) UnityEngine.Object.DestroyImmediate(s.body);
        }
        File.WriteAllText(Path.Combine(dir, "seat-slots.json"), Strings(seatSlots.ToArray()) + "\n");

        for (int round = 1; round <= 4; round++)
            foreach (var stage in new[] { LifeStage.Adult, LifeStage.Child })
                EmotionSheet(director, dir, round, stage, tree, argv);
        director.residentRoot.gameObject.SetActive(true);

        var library = director.propLibrary.gameObject;
        library.SetActive(true);
        // The library is a long grid of small props: three overlapping shots along its long axis.
        var lb = Extent(library);
        bool alongZ = lb.size.z >= lb.size.x;
        Vector3 axis = alongZ ? Vector3.forward : Vector3.right;
        float length = alongZ ? lb.size.z : lb.size.x;
        for (int part = 0; part < 3; part++)
        {
            var piece = new Bounds(lb.min + Vector3.Scale(lb.size, Vector3.one - axis) * 0.5f
                + axis * length * (part + 0.5f) / 3f,
                Vector3.Scale(lb.size, Vector3.one - axis) + axis * length / 3f);
            Vector3 side = alongZ ? Vector3.right : Vector3.back;
            Shot(dir, "import-props-library-" + (part + 1) + "-noon",
                piece.center + (side + Vector3.up).normalized * Fit(piece, 40f) * 0.8f, piece.center, 40f, "noon", tree, argv,
                new List<Subject>());
        }
        library.SetActive(false);
        Debug.Log("CONTENT-IMPORT tree_hash=" + tree);
    }

    // ------------------------------------------------------------ play entries
    // Two Fast Enter Play Mode entries of Main.unity (domain reload off, scene reload on). Each
    // records the world state hash once the director has populated and again after EntrySeconds
    // of play, with a per-second trace of what the household plays: play-entries/entry-<n>.json.
    const string EntryKey = "MainSceneBuild.Entry";
    const int Entries = 2;
    const float EntrySeconds = 8f, EntryTimeout = 300f;
    static float entryStart = -1f;
    static int entryInitial, entrySecond;
    static readonly List<string> entryTrace = new List<string>();

    public static void PlayEntries()
    {
        if (!EditorSettings.enterPlayModeOptionsEnabled || EditorSettings.enterPlayModeOptions != EnterPlayModeOptions.DisableDomainReload)
        {
            Debug.LogError("CONTENT-IMPORT entries FAIL Fast Enter Play Mode must disable domain reload only");
            EditorApplication.Exit(1);
            return;
        }
        string dir = Abs(Root + "/play-entries");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        StartEntry(1);
    }

    static void StartEntry(int entry)
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        SessionState.SetInt(EntryKey, entry);
        SessionState.SetBool(EntryKey + ".Failed", true);
        SessionState.SetFloat(EntryKey + ".Start", (float)EditorApplication.timeSinceStartup);
        entryStart = -1f;
        entryTrace.Clear();
        EditorApplication.update -= PollEntry;
        EditorApplication.playModeStateChanged -= EntryState;
        EditorApplication.update += PollEntry;
        EditorApplication.playModeStateChanged += EntryState;
        EditorApplication.EnterPlaymode();
    }

    static void PollEntry()
    {
        if (EditorApplication.timeSinceStartup - SessionState.GetFloat(EntryKey + ".Start", 0f) > EntryTimeout)
        {
            Debug.LogError("CONTENT-IMPORT entries FAIL entry " + SessionState.GetInt(EntryKey, 0) + " timed out");
            EditorApplication.update -= PollEntry;
            EditorApplication.ExitPlaymode();
            return;
        }
        if (!EditorApplication.isPlaying) return;
        var director = UnityEngine.Object.FindFirstObjectByType<MainDirector>();
        if (director == null || director.World == null) return;
        if (entryStart < 0f)
        {
            entryStart = Time.time;
            entrySecond = -1;
            entryInitial = director.World.ComputeStateHash();
        }
        float t = Time.time - entryStart;
        if ((int)t != entrySecond)
        {
            entrySecond = (int)t;
            foreach (var s in HouseholdSubjects(director))
                entryTrace.Add(Obj("seconds", t.ToString("F2", CultureInfo.InvariantCulture), "resident", Q(s.id), "flow", Q(s.flow),
                    "phase", Q(s.phase), "clip", Q(s.clip)));
        }
        if (t < EntrySeconds) return;
        int entry = SessionState.GetInt(EntryKey, 0);
        int final = director.World.ComputeStateHash();
        var options = EditorSettings.enterPlayModeOptions;
        string trace = Root + "/play-entries/trace-" + entry + ".jsonl";
        File.WriteAllText(Abs(trace), string.Join("\n", entryTrace) + "\n");
        File.WriteAllText(Abs(Root + "/play-entries/entry-" + entry + ".json"), Obj("entry", entry.ToString(CultureInfo.InvariantCulture),
            "unity_version", Q(Application.unityVersion),
            "domain_reload", (options & EnterPlayModeOptions.DisableDomainReload) == 0 ? "true" : "false",
            "scene_reload", (options & EnterPlayModeOptions.DisableSceneReload) == 0 ? "true" : "false",
            "initial_state_hash", entryInitial.ToString(CultureInfo.InvariantCulture),
            "final_state_hash", final.ToString(CultureInfo.InvariantCulture),
            "seconds", t.ToString("F2", CultureInfo.InvariantCulture),
            "residents", director.residentRoot.childCount.ToString(CultureInfo.InvariantCulture),
            "trace", Q(trace)) + "\n");
        Debug.Log("CONTENT-IMPORT entry " + entry + " initial=" + entryInitial + " final=" + final + " trace_rows=" + entryTrace.Count);
        SessionState.SetBool(EntryKey + ".Failed", false);
        EditorApplication.update -= PollEntry;
        EditorApplication.ExitPlaymode();
    }

    static void EntryState(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        int entry = SessionState.GetInt(EntryKey, 0);
        bool failed = SessionState.GetBool(EntryKey + ".Failed", true);
        EditorApplication.playModeStateChanged -= EntryState;
        if (!failed && entry < Entries) { StartEntry(entry + 1); return; }
        SessionState.EraseInt(EntryKey);
        SessionState.EraseBool(EntryKey + ".Failed");
        SessionState.EraseFloat(EntryKey + ".Start");
        Debug.Log("CONTENT-IMPORT entries " + (failed ? "FAIL" : "PASS"));
        EditorApplication.Exit(failed ? 1 : 0);
    }

    // ----------------------------------------------------------------- player
    // Linux player of the committed Main.unity; build-manifest.json binds the scene, the
    // scene input hash (TreeHash) and every player file.
    public const string PlayerPath = "Build/Main/Main.x86_64";
    public static void BuildPlayer() { Run("player", BuildPlayerNow); }

    static void BuildPlayerNow()
    {
        string dir = Abs(Path.GetDirectoryName(PlayerPath));
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var summary = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = PlayerPath,
            target = BuildTarget.StandaloneLinux64, options = BuildOptions.None }).summary;
        if (summary.result != BuildResult.Succeeded)
            throw new Exception("player build " + summary.result + " errors=" + summary.totalErrors);
        var files = new List<string>();
        foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(ProjectRoot, file).Replace('\\', '/');
            files.Add(Q(rel) + ":" + Q(Sha(rel)));
        }
        File.WriteAllText(Abs(Root + "/build-manifest.json"), Obj("package", Q("CONTENT-IMPORT"), "host", Q(Host),
            "project_copy", Q(ProjectCopy), "unity", Q(Application.unityVersion), "target", Q("StandaloneLinux64"),
            "command", Strings(Environment.GetCommandLineArgs()), "scene", Q(ScenePath), "scene_sha256", Q(Sha(ScenePath)),
            "tree_hash", Q(TreeHash()), "build_path", Q(PlayerPath), "result", Q(summary.result.ToString()),
            "errors", summary.totalErrors.ToString(CultureInfo.InvariantCulture),
            "warnings", summary.totalWarnings.ToString(CultureInfo.InvariantCulture),
            "size", summary.totalSize.ToString(CultureInfo.InvariantCulture),
            "built_utc", Q(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            "files", "{" + string.Join(",", files) + "}") + "\n");
        Debug.Log("CONTENT-IMPORT player " + PlayerPath + " files=" + files.Count + " size=" + summary.totalSize);
    }

    // ------------------------------------------------------------- subjects
    // A seeded sample resident holding one clip pose (absolute time = fraction of the clip).
    static Subject Posed(MainDirector director, BaseBody body, LifeStage stage, long seed, string flow, string phase,
        AnimationClip clip, float fraction, Vector3 position, Quaternion rotation)
    {
        if (clip == null) throw new Exception("no clip for " + flow + "/" + phase);
        var instance = director.WearSample(body, stage, seed, null);
        instance.transform.SetPositionAndRotation(position, rotation);
        var motion = director.Motion(instance, body, stage);
        float time = clip.length * fraction;
        motion.Hold(flow, phase, clip, time);
        instance.name = "Sample-" + body + "-" + stage + "-" + flow;
        return new Subject { body = instance, id = instance.name, baseBody = body, stage = stage, flow = flow, phase = phase,
            clip = clip.name, time = time };
    }

    // Stage frame: the resolved variant in its stage flow; newborns and infants are
    // carried / held (ANIM aliases held to carried) by an adult of the same body.
    static List<Subject> StageSubjects(MainDirector director, BaseBody body, LifeStage stage, Vector3 at, long seed)
    {
        var facing = Quaternion.LookRotation(Vector3.back, Vector3.up);
        var list = new List<Subject>();
        if (stage <= LifeStage.Infant)
        {
            var carrier = Posed(director, body, LifeStage.Adult, seed + 100, "carry", "hold", director.holdClip,
                MainDirector.HoldTime / director.holdClip.length, at, facing);
            var held = Posed(director, body, stage, seed, stage.ToString(), "approach", director.FlowClip(stage.ToString(), "approach"),
                0.5f, at, facing);
            MainDirector.Carry(held.body, carrier.body);
            list.Add(held);
            list.Add(carrier);
            return list;
        }
        list.Add(Posed(director, body, stage, seed, stage.ToString(), "start", director.FlowClip(stage.ToString(), "start"),
            0.5f, at, facing));
        return list;
    }

    static GameObject CloneProp(MainDirector director, string id)
    {
        Transform source = null;
        foreach (var t in director.propLibrary.GetComponentsInChildren<Transform>(true)) if (t.name == "PROP_" + id) source = t;
        if (source == null) throw new Exception("PROP-CATALOG node PROP_" + id + " not in the prop library");
        var copy = UnityEngine.Object.Instantiate(source.gameObject);
        copy.name = "Staged-" + id;
        copy.transform.localScale = Vector3.one;
        copy.SetActive(true);
        return copy;
    }

    static JObject PropFor(JObject props, string slot)
    {
        foreach (JObject prop in props["props"])
            foreach (var socket in prop["sockets"] ?? new JArray())
                if ((string)socket["slot_id"] == slot) return prop;
        return null;
    }

    static JToken Socket(JObject prop, string slot)
    {
        foreach (var socket in prop["sockets"]) if ((string)socket["slot_id"] == slot) return socket;
        throw new Exception((string)prop["id"] + " has no socket for " + slot);
    }

    static Transform Child(GameObject root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        throw new Exception(root.name + " has no " + name);
    }

    // ANIM-CATALOG seat placement (seat-facing.json stage-sit rows): seat scale and the
    // seat point behind the resident root, per body and stage.
    static void SeatRule(JObject seats, BaseBody body, LifeStage stage, out float scale, out float behind)
    {
        foreach (var row in seats["seats"])
            if ((string)row["entry"] == "stage-sit" && (string)row["body"] == body.ToString().ToLowerInvariant()
                && (string)row["stage"] == stage.ToString())
            {
                scale = (float)row["seat_slot"]["placement"][0][0];
                behind = -(float)row["seat_slot"]["placement"][2][3];
                return;
            }
        throw new Exception("no ANIM seat rule for " + body + "/" + stage);
    }

    // One activity loop staged as ANIM-CATALOG staged it: the resident at its PROP-CATALOG
    // slot (seated on a seat prop scaled and placed by the ANIM seat rule; standing at the
    // slot prop's approach node), with its partner where the activity has one.
    static List<Subject> StageAct(MainDirector director, JObject act, JObject props, JObject seats, List<GameObject> staged)
    {
        string code = (string)act["id"];
        var min = (LifeStage)Enum.Parse(typeof(LifeStage), (string)act["min_stage"]);
        var max = (LifeStage)Enum.Parse(typeof(LifeStage), (string)act["max_stage"]);
        var stage = LifeStage.Adult < min ? min : LifeStage.Adult > max ? max : LifeStage.Adult;
        var at = Stage;
        var facing = Quaternion.LookRotation(Vector3.back, Vector3.up);
        string slot = (string)act["interaction_point"]["slot_id"];
        long seed = 501L + Math.Abs(code.GetHashCode()) % 97;
        var subject = Posed(director, BaseBody.Female, stage, seed, code, "loop", director.FlowClip(code, "loop"), 0.5f, at, facing);
        var list = new List<Subject> { subject };
        var prop = PropFor(props, slot);
        var motion = subject.body.GetComponent<ResidentMotion>();
        if (SeatedActs.Contains(code))
        {
            string id = prop == null ? null : (string)prop["id"];
            Seat(director, subject, seats, slot, id == "Toilet" || id == "second" || id == "ComputerDesk" ? id : "seat-first", staged);
            if (id == "Instrument" || id == "Easel")
            {
                var context = CloneProp(director, id);
                context.transform.SetPositionAndRotation(at + subject.body.transform.forward * 0.55f,
                    Quaternion.LookRotation(-subject.body.transform.forward, Vector3.up));
                staged.Add(context);
            }
        }
        else if (code == "ACT-SPORT-03")
        {
            // GYM-TRAIL keys the ride on a bicycle whose bottom centre sits at the rider's root, unrotated
            // relative to it (GymTrailSceneBuild captures); the rack slot is GYM-TRAIL's, not the ride pose's.
            var bike = CloneProp(director, "Bike");
            staged.Add(bike);
            bike.transform.SetPositionAndRotation(subject.body.transform.position, subject.body.transform.rotation);
        }
        else if (prop != null && CounterProps.Contains((string)prop["id"]))
            OnCounter(director, subject, (string)prop["id"], staged);
        else if (prop != null && (string)prop["id"] == "ExerciseMachine")
        {
            // 'standing-on-machine': the machine under the resident, its socket at the resident's
            // root and the resident standing on its base deck.
            var copy = CloneProp(director, (string)prop["id"]);
            staged.Add(copy);
            copy.transform.rotation = facing * Quaternion.Euler(0f, 180f, 0f);
            var socket = Child(copy, (string)Socket(prop, slot)["node"]);
            var shift = at - socket.position;
            copy.transform.position += new Vector3(shift.x, 0f, shift.z);
            subject.body.transform.position += Vector3.up * Extent(Child(copy, "exercise-base").gameObject).max.y;
            motion.Apply();
        }
        else if (prop != null && code != "ACT-REL-06")
        {
            // Standing: the resident on the slot's approach node, the prop's slot side toward it.
            var copy = CloneProp(director, (string)prop["id"]);
            staged.Add(copy);
            copy.transform.rotation = facing * Quaternion.Euler(0f, 180f, 0f);
            var approach = Child(copy, (string)Socket(prop, slot)["approach_node"]);
            var shift = at - approach.position;
            copy.transform.position += new Vector3(shift.x, 0f, shift.z);
            if (code == "ACT-HEALTH-01")
            {
                // Lying asleep: the bed's sleeping slot under the lying pelvis, eyes closed.
                var socket = Child(copy, (string)Socket(prop, slot)["node"]);
                var pelvis = motion.BodyBone("pelvis").position;
                copy.transform.position += new Vector3(pelvis.x - socket.position.x, 0f, pelvis.z - socket.position.z);
                LieOn(subject, socket.position.y, 0.09f);
                subject.blink = true;
                motion.Blink(true);
            }
        }
        object[] partner;
        if (Partners.TryGetValue(code, out partner))
        {
            float distance = (float)partner[1];
            var other = Posed(director, (BaseBody)partner[4], LifeStage.Adult, 777L, (string)partner[0], "loop",
                director.FlowClip((string)partner[0], "loop"), (float)partner[2],
                at + subject.body.transform.forward * distance, Quaternion.LookRotation(-subject.body.transform.forward, Vector3.up));
            if ((bool)partner[3]) Seat(director, other, seats, slot, "seat-first", staged);
            list.Add(other);
        }
        if (code == "ACT-HOME-05")
        {
            // Bedside care: a newborn dependent lies asleep on the bed beside the carer.
            var bed = staged.Find(go => go.name == "Staged-Bed");
            var socket = Child(bed, "socket_Bed_Bed");
            var baby = Posed(director, BaseBody.Female, LifeStage.Newborn, 901L, "Newborn", "loop",
                director.FlowClip("Newborn", "loop"), 0.5f, new Vector3(socket.position.x, 0f, socket.position.z), bed.transform.rotation);
            var babyMotion = baby.body.GetComponent<ResidentMotion>();
            var pelvis = babyMotion.BodyBone("pelvis").position;
            baby.body.transform.position += new Vector3(socket.position.x - pelvis.x, 0f, socket.position.z - pelvis.z);
            LieOn(baby, socket.position.y, 0.15f);
            baby.blink = true;
            babyMotion.Blink(true);
            list.Add(baby);
        }
        return list;
    }

    // PROP-CATALOG props used at a counter ('standing-at-counter', '-behind-counter',
    // '-at-workstation') stand on one: a PROP Table, as ANIM capture_context staged ACT-WORK-01
    // (table near edge 0.20 m short of the palms, the prop on its top 0.02 m short of them).
    static readonly HashSet<string> CounterProps = new HashSet<string> { "FastFood", "EspressoMachine", "Toolkit" };

    static void OnCounter(MainDirector director, Subject subject, string propId, List<GameObject> staged)
    {
        var motion = subject.body.GetComponent<ResidentMotion>();
        var root = subject.body.transform;
        var forward = root.forward;
        float reach = Vector3.Dot((motion.BodyBone("hand_l").position + motion.BodyBone("hand_r").position) * 0.5f - root.position, forward);
        var table = CloneProp(director, "Table");
        staged.Add(table);
        table.transform.rotation = root.rotation;
        Place(table, root.position + forward * (reach - 0.20f), forward, 0f);
        var item = CloneProp(director, propId);
        staged.Add(item);
        item.transform.rotation = root.rotation * Quaternion.Euler(0f, 180f, 0f);
        Place(item, root.position + forward * (reach - 0.02f), forward, Extent(table).max.y);
    }

    // Moves a staged prop so its near face (toward -forward) is at nearFace and its bottom at floor.
    static void Place(GameObject go, Vector3 nearFace, Vector3 forward, float floor)
    {
        var b = Extent(go);
        float half = Mathf.Abs(forward.x) * b.extents.x + Mathf.Abs(forward.z) * b.extents.z;
        var target = nearFace + forward * half;
        go.transform.position += new Vector3(target.x - b.center.x, floor - b.min.y, target.z - b.center.z);
    }

    // A lying body rests on a surface: its lowest trunk/limb joint sits one body half-thickness above it.
    static void LieOn(Subject subject, float surface, float halfThickness)
    {
        var motion = subject.body.GetComponent<ResidentMotion>();
        float lowest = float.MaxValue;
        foreach (string bone in new[] { "pelvis", "spine_01", "spine_03", "neck_01", "head", "thigh_l", "thigh_r", "calf_l",
                     "calf_r", "foot_l", "foot_r", "upperarm_l", "upperarm_r" })
        {
            var t = motion.BodyBone(bone);
            if (t != null) lowest = Mathf.Min(lowest, t.position.y);
        }
        subject.body.transform.position += Vector3.up * (surface + halfThickness - lowest);
        motion.Apply();
    }

    // Seat prop by the ANIM seat rule: scaled per stage, its slot socket on the seat point behind the root.
    static void Seat(MainDirector director, Subject subject, JObject seats, string slot, string propId, List<GameObject> staged)
    {
        float scale, behind;
        SeatRule(seats, subject.baseBody, subject.stage, out scale, out behind);
        var copy = CloneProp(director, propId);
        staged.Add(copy);
        var root = subject.body.transform;
        copy.transform.rotation = root.rotation;
        copy.transform.localScale = Vector3.one * scale;
        Transform socket = null;
        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("socket_" + propId + "_", StringComparison.Ordinal)
                && (socket == null || t.name == "socket_" + propId + "_" + slot)) socket = t;
        if (socket == null) throw new Exception(propId + " has no seat socket");
        var target = root.position - root.forward * behind;
        var offset = socket.position - copy.transform.position;
        copy.transform.position = new Vector3(target.x - offset.x, 0f, target.z - offset.z);
        subject.seat = socket;
        subject.seatForward = copy.transform.forward;
    }

    // ------------------------------------------------------------- feelings sheet
    // emotions-r<round>-<stage>: the seven feelings in a 3x3 sheet (as ANIM-CATALOG's),
    // each cell its own render of one resident: idle loop + feeling render channels + glyph.
    const int Cell = 384;

    static void EmotionSheet(MainDirector director, string dir, int round, LifeStage stage, string tree, string[] argv)
    {
        string name = "emotions-r" + round + "-" + stage.ToString().ToLowerInvariant();
        int w = Cell * 3, h = Cell * 3;
        var sheet = new Color32[w * h];
        var ids = new Color32[w * h];
        var cells = new List<string>();
        var subjects = new List<Subject>();
        var rows = new List<string>();
        var idle = director.Clip("Idle_Loop");
        for (int i = 0; i < MainDirector.Feelings.Length; i++)
        {
            string feeling = MainDirector.Feelings[i];
            var s = Posed(director, BaseBody.Female, stage, 300L + round, feeling, "loop", idle,
                ((round * 3) % 20) / 20f, Stage, Quaternion.LookRotation(Vector3.back, Vector3.up));
            s.id = feeling;
            var motion = s.body.GetComponent<ResidentMotion>();
            var channels = director.FlowEntry(feeling)["render_channels"];
            var weights = new Dictionary<string, float>();
            var targets = (JArray)channels["face"]["target_paths"];
            for (int k = 0; k < targets.Count; k++) weights[(string)targets[k]] = (float)channels["face"]["values"][k];
            motion.Feel(weights, channels["posture"]["values"].ToObject<float[]>(), channels["gaze"]["values"].ToObject<float[]>());
            var b = Extent(s.body);
            var head = motion.BodyBone("head").position;
            var look = new Vector3(b.center.x, (b.center.y + head.y) * 0.5f, b.center.z);
            float azimuth = -8f + 4f * round;
            var direction = Quaternion.AngleAxis(azimuth, Vector3.up) * new Vector3(0f, 0.12f, -1f).normalized;
            var camera = BuildCamera("CaptureCamera", look + direction * Fit(b, 30f) * 0.8f, look);
            camera.fieldOfView = 30f;
            camera.aspect = 1f;
            var icon = director.Icon(s.body, feeling);
            icon.transform.rotation = camera.transform.rotation;
            ApplyPreset("noon", camera);
            MainDirector.AimFill(director.residentFill, camera.transform);
            MainDirector.AimFill(director.deepSkinFill, camera.transform);
            try
            {
                int ox = (i % 3) * Cell, oy = (2 - i / 3) * Cell;
                Blit(Beauty(camera, Cell, Cell), Cell, sheet, w, ox, oy);
                Blit(IdMask(camera, Cell, Cell, new List<Subject> { s }, subjects.Count), Cell, ids, w, ox, oy);
                s.coverage = Coverage(camera, Cell, Cell, s, dir, name + "__" + feeling, ox, oy);
                rows.Add(SubjectJson(s, subjects.Count, camera));
                cells.Add(Obj("feeling", Q(feeling), "origin", "[" + ox + "," + oy + "]", "size", Cell.ToString(CultureInfo.InvariantCulture),
                    "camera", CameraJson(camera)));
                subjects.Add(s);
            }
            finally { UnityEngine.Object.DestroyImmediate(camera.gameObject); }
            s.body.SetActive(false);
        }
        // Unused cells take the first cell's backdrop colour (as ANIM's sheet).
        var backdrop = sheet[(2 * Cell + 2) * w + 2];
        for (int i = MainDirector.Feelings.Length; i < 9; i++)
        {
            int ox = (i % 3) * Cell, oy = (2 - i / 3) * Cell;
            for (int y = 0; y < Cell; y++) for (int x = 0; x < Cell; x++) sheet[(oy + y) * w + ox + x] = backdrop;
        }
        WritePng(Path.Combine(dir, name + ".png"), sheet, w, h);
        WritePng(Path.Combine(dir, "masks", name + ".ids.png"), ids, w, h);
        // A sheet has one camera per cell.
        WriteSidecar(dir, name, "\"camera\":{\"cells\":[" + string.Join(",", cells) + "]}", "noon", w, h, tree, argv, rows,
            "masks/" + name + ".ids.png");
        foreach (var s in subjects) UnityEngine.Object.DestroyImmediate(s.body);
    }

    static void Blit(Color32[] src, int size, Color32[] dst, int w, int ox, int oy)
    {
        for (int y = 0; y < size; y++) Array.Copy(src, y * size, dst, (oy + y) * w + ox, size);
    }

    static Bounds Extent(List<Subject> subjects)
    {
        var b = Extent(subjects[0].body);
        foreach (var s in subjects) b.Encapsulate(Extent(s.body));
        return b;
    }

    static Bounds Extent(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        var b = renderers[0].bounds;
        foreach (var renderer in renderers) b.Encapsulate(renderer.bounds);
        return b;
    }

    // Distance that keeps the bounding sphere inside a 16:9 frame at this vertical fov.
    static float Fit(Bounds b, float fov)
    {
        float half = fov * 0.5f * Mathf.Deg2Rad;
        float horizontal = Mathf.Atan(Mathf.Tan(half) * Width / Height);
        return b.extents.magnitude / Mathf.Sin(Mathf.Min(half, horizontal)) * 1.05f;
    }

    static GameObject FindDeep(GameObject root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t.gameObject;
        return null;
    }

    const string CaptureTool = "MainSceneBuild.Capture";

    static void Shot(string dir, string name, Vector3 position, Vector3 lookAt, float fov, string preset,
        string tree, string[] argv, List<Subject> subjects)
    {
        var camera = BuildCamera("CaptureCamera", position, lookAt);
        camera.fieldOfView = fov;
        camera.aspect = (float)Width / Height;
        ApplyPreset(preset, camera);
        var director = UnityEngine.Object.FindFirstObjectByType<MainDirector>();
        if (director != null)
        {
            MainDirector.AimFill(director.residentFill, camera.transform);
            MainDirector.AimFill(director.deepSkinFill, camera.transform);
        }
        foreach (var s in subjects)
        {
            var icon = s.body.transform.Find("EmoteIcon");
            if (icon != null) icon.rotation = camera.transform.rotation;
        }
        try
        {
            WritePng(Path.Combine(dir, name + ".png"), Beauty(camera, Width, Height), Width, Height);
            Measure(dir, name, camera, Width, Height, subjects, Q("camera") + ":" + Obj("position", Floats(new[] { position.x, position.y, position.z }),
                    "look_at", Floats(new[] { lookAt.x, lookAt.y, lookAt.z }),
                    "fov", fov.ToString("R", CultureInfo.InvariantCulture)),
                preset, tree, argv, CaptureTool);
        }
        finally { UnityEngine.Object.DestroyImmediate(camera.gameObject); }
    }

    // Hash of every input the Main scene is built from, in a fixed order.
    static string TreeHash()
    {
        var parts = new List<string>();
        foreach (string path in new[] { ScenePath, Builder, Director, Resolver, World, Env, EnvCatalog, Props, PropCatalog, Layout,
                     ClothCatalog, Wardrobe, AnimCatalog, Actions, HoldSource, ActionCatalog, StagePresets, Feelings, Motion, MaskShader })
            parts.Add(path + "=" + Sha(path));
        foreach (var pair in Pairs())
        {
            string path = StageVariants.Resolve(pair.Key, pair.Value);
            parts.Add(path + "=" + Sha(path));
        }
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts) + "\n")))
                .Replace("-", string.Empty).ToLowerInvariant();
    }

    // ------------------------------------------------- scene lighting (ENV-CATALOG look)
    static readonly string[] Interiors = { "apartment", "common-room", "cafe", "workshop", "office", "school",
        "grocery", "recreation-center", "oak-house", "willow-house" };
    // Warm fills (CLOTH's studio fill colour) aimed from the camera, scaled with the sun:
    // ResidentFill lights residents only; DeepSkinFill adds the sheen that lets the
    // darkest skin keep its facial planes, on that skin only.
    const float ResidentFillNoon = 0.5f;
    const float DeepSkinFillNoon = 1.2f;
    const float SunNoon = 1.2f;

    static Light BuildLighting(Scene scene, GameObject env, out Light deepFill)
    {
        RenderSettings.fog = false;
        SetAmbient("#9CC7E8", "#E8DCC8", "#8A7A62", 0.85f);
        var sunObject = new GameObject("Sun");
        SceneManager.MoveGameObjectToScene(sunObject, scene);
        var sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.8f;
        sunObject.AddComponent<UniversalAdditionalLightData>();
        SetSun(sun, 65f, -30f, "#FFF6E8", SunNoon);
        RenderSettings.sun = sun;
        var fill = Fill(scene, "ResidentFill", ResidentFillNoon, (1 << MainDirector.ResidentLayer) | (1 << MainDirector.DeepSkinLayer));
        deepFill = Fill(scene, "DeepSkinFill", DeepSkinFillNoon, 1 << MainDirector.DeepSkinLayer);
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null) throw new Exception("missing " + VolumeProfilePath);
        var holder = new GameObject("ColorProfileVolume");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var volume = holder.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;
        foreach (string id in Interiors)
        {
            var module = FindDeep(env, id);
            if (module != null) AddInteriorPracticals(scene, module);
        }
        return fill;
    }

    static Light Fill(Scene scene, string name, float intensity, int mask)
    {
        var holder = new GameObject(name);
        SceneManager.MoveGameObjectToScene(holder, scene);
        var light = holder.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.86f, 0.72f);
        light.intensity = intensity;
        light.shadows = LightShadows.None;
        light.cullingMask = mask;
        holder.AddComponent<UniversalAdditionalLightData>();
        return light;
    }

    // Fixture-shaped 3200 K pools over each interior floor, as ENV-CATALOG lit them.
    static void AddInteriorPracticals(Scene scene, GameObject module)
    {
        var floors = new List<Renderer>();
        foreach (var renderer in module.GetComponentsInChildren<Renderer>(true))
            if (renderer.name.Contains("_floor")) floors.Add(renderer);
        if (floors.Count == 0) return;
        Bounds bounds = floors[0].bounds;
        foreach (var floor in floors) bounds.Encapsulate(floor.bounds);
        float width = bounds.size.x, depth = bounds.size.z;
        Vector2[] fractions = width > 7f || depth > 5f
            ? new[] { new Vector2(0.25f, 0.3f), new Vector2(0.75f, 0.3f), new Vector2(0.25f, 0.7f), new Vector2(0.75f, 0.7f) }
            : new[] { new Vector2(0.3f, 0.5f), new Vector2(0.7f, 0.5f) };
        Color warm = Mathf.CorrelatedColorTemperatureToRGB(3200f);
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        foreach (var fraction in fractions)
        {
            var position = new Vector3(bounds.min.x + width * fraction.x, bounds.max.y + 2.3f, bounds.min.z + depth * fraction.y);
            var lightHolder = new GameObject("InteriorPractical");
            SceneManager.MoveGameObjectToScene(lightHolder, scene);
            lightHolder.transform.position = position;
            var light = lightHolder.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = warm;
            light.intensity = 1.8f;
            light.range = Mathf.Max(4.5f, Mathf.Max(width, depth) * 0.8f);
            light.shadows = LightShadows.None;
            lightHolder.AddComponent<UniversalAdditionalLightData>();
            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            fixture.name = "InteriorFixture";
            UnityEngine.Object.DestroyImmediate(fixture.GetComponent<Collider>());
            SceneManager.MoveGameObjectToScene(fixture, scene);
            fixture.transform.position = position + Vector3.up * 0.08f;
            fixture.transform.localScale = new Vector3(0.22f, 0.02f, 0.22f);
            var material = new Material(shader) { color = warm };
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", warm * 2.2f);
            fixture.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
    }

    static Camera BuildCamera(string name, Vector3 position, Vector3 lookAt)
    {
        var obj = new GameObject(name);
        var camera = obj.AddComponent<Camera>();
        camera.transform.position = position;
        camera.transform.LookAt(lookAt);
        camera.fieldOfView = 50f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 600f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Hex("#8EC3EA");
        camera.allowHDR = true;
        camera.allowMSAA = true;
        obj.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
        return camera;
    }

    static void ApplyPreset(string preset, Camera camera)
    {
        var sunObject = GameObject.Find("Sun");
        var sun = sunObject != null ? sunObject.GetComponent<Light>() : null;
        switch (preset)
        {
            case "morning":
                SetSun(sun, 35f, -50f, "#FFE0B8", 1.0f); SetAmbient("#A9CDEB", "#F1E1C8", "#7C6C56", 0.8f);
                camera.backgroundColor = Hex("#B7D6EE"); break;
            case "evening":
                SetSun(sun, 15f, -65f, "#FFB87A", 0.85f); SetAmbient("#9CB2D9", "#F3D3A8", "#6E6152", 0.7f);
                camera.backgroundColor = Hex("#E8C8A0"); break;
            case "night":
                SetSun(sun, 30f, -80f, "#8FA6D8", 0.25f); SetAmbient("#2C3A5C", "#3A4258", "#1E1B18", 0.5f);
                camera.backgroundColor = Hex("#1C2740"); break;
            default:
                SetSun(sun, 65f, -30f, "#FFF6E8", SunNoon); SetAmbient("#9CC7E8", "#E8DCC8", "#8A7A62", 0.85f);
                camera.backgroundColor = Hex("#8EC3EA"); break;
        }
        if (sun == null) return;
        foreach (var fill in new[] { new KeyValuePair<string, float>("ResidentFill", ResidentFillNoon),
                     new KeyValuePair<string, float>("DeepSkinFill", DeepSkinFillNoon) })
        {
            var holder = GameObject.Find(fill.Key);
            if (holder != null) holder.GetComponent<Light>().intensity = fill.Value * sun.intensity / SunNoon;
        }
    }

    static void SetSun(Light sun, float pitch, float yaw, string color, float intensity)
    {
        if (sun == null) return;
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

    static Vector3 V(float[] p) { return new Vector3(p[0], p[1], p[2]); }
    static Vector3 GlTF(Vector3 p) { return new Vector3(-p.x, p.y, p.z); }

    static Color Hex(string value)
    {
        Color color;
        ColorUtility.TryParseHtmlString(value, out color);
        return color;
    }
}
