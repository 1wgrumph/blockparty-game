using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using AudioMixerAsset = UnityEngine.Audio.AudioMixer;
using AudioMixerGroupAsset = UnityEngine.Audio.AudioMixerGroup;
using Object = UnityEngine.Object;

// SLICE editor entry points. Runs headless via -executeMethod:
//   Unity -batchmode -projectPath . -executeMethod SliceSceneBuild.BuildScene -quit
//   DISPLAY=:0 Unity -projectPath . -executeMethod SliceSceneBuild.PlaySlice -logFile <log>
public static class SliceSceneBuild
{
    public const string ScenePath = "Assets/Blockparty/Scenes/VerticalSlice.unity";
    const string Female = "Assets/Blockparty/Art/Characters/MakeHuman/female.gltf";
    const string Male = "Assets/Blockparty/Art/Characters/MakeHuman/male.gltf";
    const string Second = "Assets/Blockparty/Art/Characters/second.gltf";
    const string ClothingFirst = "Assets/Blockparty/Art/Clothing/first.gltf";
    const string AnimFirst = "Assets/Blockparty/Art/Animations/MakeHuman/first.gltf";
    const string AnimSecond = "Assets/Blockparty/Art/Animations/second.gltf";
    const string EnvFirst = "Assets/Blockparty/Art/Environment/apartment-first.gltf";
    const string EnvSecond = "Assets/Blockparty/Art/Environment/second.gltf";
    const string SeatFirst = "Assets/Blockparty/Art/Props/seat-first.gltf";
    const string SeatSecond = "Assets/Blockparty/Art/Props/second.gltf";
    const string SkinDir = "Assets/Blockparty/Art/Characters/MakeHuman/materials/";

    const string LookDir = "Assets/Blockparty/Presentation/Slice";
    const string MixerPath = LookDir + "/SliceMixer.mixer";

    public static void BuildScene()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Blockparty/Scenes"))
            AssetDatabase.CreateFolder("Assets/Blockparty", "Scenes");
        System.IO.Directory.CreateDirectory(LookDir);
        AssetDatabase.Refresh();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("SliceWorld");
        SceneManager.MoveGameObjectToScene(world, scene);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.SetParent(world.transform, false);
        // The landed modules' floor top is exactly y=0; keep the ground 3 cm
        // below it so the two coplanar surfaces do not z-fight.
        ground.transform.position = new Vector3(0f, -0.18f, 0f);
        ground.transform.localScale = new Vector3(40f, 0.3f, 30f);
        Paint(ground, Hex("#D09F64"));

        // Common room (10x7 m, floor y=0, doorway on -z edge) centred in frame;
        // apartment module west of it; seats inside the room.
        Place(EnvFirst, "ApartmentFirst", new Vector3(-8f, 0f, 6f), scene);
        var envSecond = Place(EnvSecond, "EnvironmentSecond", new Vector3(3f, 0f, 6f), scene);
        int posts = RelieveDoorPosts(envSecond);
        if (posts != 2) throw new System.Exception("SLICE_BUILD door posts missing: " + posts);
        Place(SeatFirst, "SeatFirst", new Vector3(1.2f, 0f, 4.6f), scene);
        Place(SeatSecond, "SeatSecond", new Vector3(5.8f, 0f, 8.4f), scene);

        // blockparty-vibrant-v1; material copies and the Volume belong to SLICE.
        ApplyEnvironmentPalette(scene);
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(world.transform, false);
        sun.type = LightType.Directional;
        sun.useColorTemperature = true;
        sun.colorTemperature = 5600f;
        sun.color = Color.white;
        sun.intensity = 1.2f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.8f;
        sun.shadowBias = 0.15f;
        sun.shadowNormalBias = 1.5f;
        sun.shadowNearPlane = 0.2f;
        sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Hex("#9CC7E8").linear;
        RenderSettings.ambientEquatorColor = Hex("#E8DCC8").linear;
        RenderSettings.ambientGroundColor = Hex("#8A7A62").linear;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.sun = sun;
        RenderSettings.fog = false;
        var sky = new Material(Shader.Find("Blockparty/SliceSky"));
        sky.SetColor("_Top", Hex("#8EC3EA"));
        sky.SetColor("_Horizon", Hex("#F3E6CF"));
        RenderSettings.skybox = SaveLook(sky, "Sky.mat");
        AddPractical(world.transform, new Vector3(-0.3f, 2.6f, 4.6f));
        AddPractical(world.transform, new Vector3(3.2f, 2.6f, 6.4f));
        AddPractical(world.transform, new Vector3(6.8f, 2.6f, 8f));
        AddPractical(world.transform, new Vector3(-7f, 2.6f, 6f));
        BuildGrade(world.transform);

        var camera = new GameObject("SliceCamera").AddComponent<Camera>();
        camera.transform.SetParent(world.transform, false);
        camera.transform.position = new Vector3(-1.6f, 2.5f, 3.2f);
        camera.transform.LookAt(new Vector3(3.5f, 0.9f, 7.8f));
        camera.fieldOfView = 55f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.allowHDR = true;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        camera.tag = "MainCamera";
        camera.gameObject.AddComponent<AudioListener>();

        var clips = new List<AnimationClip>();
        foreach (string animPath in new[] { AnimFirst, AnimSecond })
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(animPath))
                if (a is AnimationClip c && !clips.Exists(x => x.name == c.name)) clips.Add(c);
        if (clips.Count == 0) throw new System.Exception("SLICE_BUILD missing clips at " + AnimFirst);

        // Landed clips are Mecanim (legacy=False); share one legacy copy of
        // each so the legacy Animation player can pose every rig. Garments
        // take the body's weights in CombineResident, so every outfit follows
        // the same bends without poking the body through.
        var legacyClips = new List<AnimationClip>();
        foreach (var clip in clips)
        {
            var copy = Object.Instantiate(clip);
            copy.legacy = true;
            copy.name = clip.name;
            legacyClips.Add(copy);
        }

        // Three landed everyday/sleep/exercise sets so the cast is not in one
        // uniform; resident 1 keeps the second character's own outfit.
        var outfits = new[] { "exercise", "sleep", "everyday" };
        var roots = new Transform[8];
        var visuals = new SliceResidentVisual[8];
        for (int i = 0; i < 8; i++)
        {
            bool male = i % 2 == 1;
            string path = i == 0 ? Second : (male ? Male : Female);
            var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (prefab == null) throw new System.Exception("SLICE_BUILD missing prefab at " + path);
            var root = new GameObject("Resident_" + (i + 1));
            root.transform.SetParent(world.transform, false);
            root.transform.position = new Vector3(i * 2f - 7f, 0f, -2f);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            // Resident 1 is the landed second resident (already clothed);
            // the rest wear the landed wardrobe set for their body.
            Transform clothingRig = i > 0
                ? AttachClothing(root.transform, male ? "male" : "female", outfits[i % outfits.Length], scene)
                : null;
            // second.gltf imports under a wrapper; legacy clip paths start at
            // the rig, so animate the rig rather than the wrapper.
            Transform animRoot = body.transform;
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
                if (t.name == "Rig_female" || t.name == "Rig_male") { animRoot = t; break; }
            var anim = animRoot.gameObject.AddComponent<Animation>();
            foreach (var clip in legacyClips) anim.AddClip(clip, clip.name);
            anim.playAutomatically = false;
            visuals[i] = CombineResident(root.transform, path, male ? "Male" : "Female", i, clothingRig);
            ApplyOutfitPalette(root, i);
            roots[i] = root.transform;
        }

        var directorObject = new GameObject("SliceDirector");
        directorObject.transform.SetParent(world.transform, false);
        var director = directorObject.AddComponent<SliceDirector>();
        director.residentRoots = roots;
        director.residentVisuals = visuals;
        director.sliceCamera = camera;
        director.foodClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Blockparty/Audio/Recorded/second-cue.wav");
        director.sleepClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Blockparty/Audio/Recorded/wake.wav");
        director.ambienceClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Blockparty/Audio/Recorded/day-birds.wav");
        director.skins = new[] {
            AssetDatabase.LoadAssetAtPath<Texture2D>(SkinDir + "skin-female-dark-young.png"),
            AssetDatabase.LoadAssetAtPath<Texture2D>(SkinDir + "skin-female-light-young.png"),
            AssetDatabase.LoadAssetAtPath<Texture2D>(SkinDir + "skin-male-dark-young.png"),
            AssetDatabase.LoadAssetAtPath<Texture2D>(SkinDir + "skin-male-light-young.png") };
        AudioMixerAsset mixer = EnsureMixer();
        director.audioMixer = mixer;
        director.cueGroup = Group(mixer, "World");
        director.ambienceGroup = Group(mixer, "Ambience");
        var bridge = directorObject.AddComponent<AudioMixer>();
        var bridgeObject = new SerializedObject(bridge);
        bridgeObject.FindProperty("mixer").objectReferenceValue = mixer;
        bridgeObject.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene, ScenePath);
        CleanAssetText(ScenePath);
        foreach (string path in System.IO.Directory.GetFiles(LookDir, "*", System.IO.SearchOption.AllDirectories))
            CleanAssetText(path);
        Debug.Log("SLICE_BUILD saved=" + ScenePath + " clips=" + clips.Count);
    }

    static GameObject Place(string path, string name, Vector3 pos, Scene scene)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        if (prefab == null) { Debug.LogWarning("SLICE_BUILD optional prefab missing " + path); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        go.name = name;
        go.transform.position = pos;
        return go;
    }

    // The landed door posts share the wall's front and jamb planes exactly, so
    // their faces z-fight along both doorway edges. Bring each post 2 cm into
    // the opening and 1 cm proud of the wall; every post face now crosses the
    // wall volume instead of being coplanar with it.
    static int RelieveDoorPosts(GameObject environment)
    {
        if (environment == null) return 0;
        int posts = 0;
        foreach (var t in environment.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "door_post_w" && t.name != "door_post_e") continue;
            // glTF import mirrors local X, so take the inward sign from the
            // post's own side of the doorway rather than its node name.
            float inward = t.localPosition.x >= 0f ? -0.02f : 0.02f;
            t.localPosition += new Vector3(inward, 0f, 0.01f);
            posts++;
            Debug.Log("SLICE_BUILD doorpost " + t.name + " local=" + t.localPosition);
        }
        return posts;
    }

    // Keeps only the named outfit set from the landed wardrobe. Its rig is
    // combined into the body renderer immediately afterwards, so only the
    // body rig carries the legacy clips.
    static Transform AttachClothing(Transform root, string gender, string outfit, Scene scene)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(ClothingFirst) as GameObject;
        if (prefab == null) throw new System.Exception("SLICE_BUILD missing prefab at " + ClothingFirst);
        var wardrobe = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        PrefabUtility.UnpackPrefabInstance(wardrobe, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        Transform rig = null;
        foreach (var t in wardrobe.GetComponentsInChildren<Transform>(true))
            if (t.name == "Rig_" + gender) rig = t;
        if (rig == null) throw new System.Exception("SLICE_BUILD missing clothing rig " + gender);
        var drop = new List<GameObject>();
        foreach (Transform child in wardrobe.transform)
            if (child != rig) drop.Add(child.gameObject);
        foreach (Transform t in rig)
        {
            if (t.GetComponent<SkinnedMeshRenderer>() == null) continue;
            bool keep = t.name.StartsWith(outfit + "-") && t.name.EndsWith("__" + gender);
            if (!keep) drop.Add(t.gameObject);
        }
        foreach (var go in drop) Object.DestroyImmediate(go);
        rig.SetParent(root, false);
        Object.DestroyImmediate(wardrobe);
        return rig;
    }

    // One skinned renderer per resident: body skin plus every garment submesh,
    // all on the body's 53-joint skeleton. Eyes and the second resident's hair
    // stay separate. The combined mesh is a slice-owned asset.
    static SliceResidentVisual CombineResident(Transform root, string sourceAsset, string baseBody, int resident, Transform clothingRig)
    {
        var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        SkinnedMeshRenderer body = null;
        var garments = new List<SkinnedMeshRenderer>();
        foreach (var renderer in all)
        {
            if (IsBodySkinRenderer(renderer)) { if (body == null) body = renderer; }
            else if (IsGarmentRenderer(renderer)) garments.Add(renderer);
        }
        if (body == null) throw new Exception("SLICE_BUILD missing body skin " + root.name);
        if (garments.Count < 2) throw new Exception("SLICE_BUILD missing garments " + root.name);
        var sourceBodyMesh = body.sharedMesh;
        var bodyBones = body.bones;
        var boneIndex = new Dictionary<string, int>(bodyBones.Length);
        for (int i = 0; i < bodyBones.Length; i++) boneIndex[bodyBones[i].name] = i;
        var bodyPositions = sourceBodyMesh.vertices;
        var bodyWeights = sourceBodyMesh.boneWeights;
        if (bodyWeights == null || bodyWeights.Length != bodyPositions.Length)
            throw new Exception("SLICE_BUILD body weights " + root.name);
        const float bodyCell = 0.05f;
        var bodyGrid = new Dictionary<long, List<int>>();
        for (int i = 0; i < bodyPositions.Length; i++)
        {
            long key = GridKey(Mathf.FloorToInt(bodyPositions[i].x / bodyCell),
                Mathf.FloorToInt(bodyPositions[i].y / bodyCell),
                Mathf.FloorToInt(bodyPositions[i].z / bodyCell));
            List<int> list;
            if (!bodyGrid.TryGetValue(key, out list)) { list = new List<int>(); bodyGrid.Add(key, list); }
            list.Add(i);
        }
        int NearestBodyVertex(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / bodyCell);
            int cy = Mathf.FloorToInt(p.y / bodyCell);
            int cz = Mathf.FloorToInt(p.z / bodyCell);
            int best = -1;
            float bestSq = float.MaxValue;
            const int radius = 3;
            for (int x = cx - radius; x <= cx + radius; x++)
                for (int y = cy - radius; y <= cy + radius; y++)
                    for (int z = cz - radius; z <= cz + radius; z++)
                    {
                        List<int> candidates;
                        if (!bodyGrid.TryGetValue(GridKey(x, y, z), out candidates)) continue;
                        foreach (int i in candidates)
                        {
                            float d = (bodyPositions[i] - p).sqrMagnitude;
                            if (d < bestSq) { bestSq = d; best = i; }
                        }
                    }
            if (best >= 0) return best;
            // Garment vertices sit within 7 cm of the body; this fallback only
            // covers a broken cell map.
            for (int i = 0; i < bodyPositions.Length; i++)
            {
                float d = (bodyPositions[i] - p).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            return best;
        }
        int pelvisBone, thighLBone, thighRBone;
        if (!boneIndex.TryGetValue("pelvis", out pelvisBone) || !boneIndex.TryGetValue("thigh_l", out thighLBone)
            || !boneIndex.TryGetValue("thigh_r", out thighRBone))
            throw new Exception("SLICE_BUILD body pelvis/thigh bones " + root.name);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uv = new List<Vector2>();
        var uv2 = new List<Vector2>();
        var colors = new List<Color32>();
        var weights = new List<BoneWeight>();
        var materials = new List<Material>();
        var triangles = new List<int[]>();
        int bottomStart = -1, bottomCount = 0;

        Action<SkinnedMeshRenderer> add = renderer =>
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null) throw new Exception("SLICE_BUILD empty mesh " + renderer.name);
            var sourceWeights = mesh.boneWeights;
            int crotch = -1;
            float crotchPelvis = 0f;
            if (renderer.name.Contains("everyday-bottom__"))
            {
                int pelvis = -1;
                for (int i = 0; i < renderer.bones.Length; i++)
                    if (renderer.bones[i].name == "pelvis") pelvis = i;
                if (pelvis < 0) throw new Exception("SLICE_BUILD wool bones " + root.name);
                var local = mesh.vertices;
                for (int i = 0; i < local.Length; i++)
                    if (Mathf.Abs(local[i].x) < 0.015f && local[i].z < -0.05f
                        && sourceWeights[i].boneIndex0 == pelvis && sourceWeights[i].weight0 > 0.8f
                        && (crotch < 0 || local[i].y < local[crotch].y)) crotch = i;
                if (crotch < 0) throw new Exception("SLICE_BUILD wool crotch " + root.name);
                crotchPelvis = sourceWeights[crotch].weight0;
                Debug.Log("SLICE_CROTCH resident=" + root.name + " vertex=" + crotch
                    + " pelvis=" + crotchPelvis + "->0.1 thigh_l=0.45 thigh_r=0.45 triangles=" + mesh.triangles.Length / 3);
                bottomStart = vertices.Count;
                bottomCount = mesh.vertexCount;
            }
            var remap = new int[renderer.bones.Length];
            for (int i = 0; i < renderer.bones.Length; i++)
            {
                int mapped;
                if (!boneIndex.TryGetValue(renderer.bones[i].name, out mapped))
                    throw new Exception("SLICE_BUILD bone " + renderer.bones[i].name + " missing " + root.name);
                remap[i] = mapped;
            }
            Matrix4x4 toBody = body.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            int offset = vertices.Count;
            var localPositions = mesh.vertices;
            var localNormals = mesh.normals;
            var localTangents = mesh.tangents;
            var localUv = mesh.uv;
            var localUv2 = mesh.uv2;
            var localColors = mesh.colors32;
            int transferred = 0;
            float maxBodyDistance = 0f;
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                Vector3 bodyLocal = toBody.MultiplyPoint3x4(localPositions[i]);
                vertices.Add(bodyLocal);
                normals.Add(localNormals != null && localNormals.Length == mesh.vertexCount
                    ? toBody.MultiplyVector(localNormals[i]) : Vector3.up);
                if (localTangents != null && localTangents.Length == mesh.vertexCount)
                {
                    Vector3 t = toBody.MultiplyVector(new Vector3(localTangents[i].x, localTangents[i].y, localTangents[i].z));
                    tangents.Add(new Vector4(t.x, t.y, t.z, localTangents[i].w));
                }
                else tangents.Add(new Vector4(1f, 0f, 0f, -1f));
                uv.Add(localUv != null && localUv.Length == mesh.vertexCount ? localUv[i] : Vector2.zero);
                uv2.Add(localUv2 != null && localUv2.Length == mesh.vertexCount ? localUv2[i] : Vector2.zero);
                colors.Add(localColors != null && localColors.Length == mesh.vertexCount ? localColors[i] : new Color32(255, 255, 255, 255));
                BoneWeight w = sourceWeights[i];
                w.boneIndex0 = remap[w.boneIndex0]; w.boneIndex1 = remap[w.boneIndex1];
                w.boneIndex2 = remap[w.boneIndex2]; w.boneIndex3 = remap[w.boneIndex3];
                if (renderer != body)
                {
                    // The landed garment weights are coarser than the body's:
                    // trouser crotch vertices stay on the pelvis and sleeve
                    // seams keep a torso share, so walk bends the body out of
                    // the cloth. Use the nearest body surface vertex's weights
                    // so both surfaces move together.
                    int nearest = NearestBodyVertex(bodyLocal);
                    if (nearest < 0) throw new Exception("SLICE_BUILD body weight match " + renderer.name);
                    w = bodyWeights[nearest];
                    transferred++;
                    maxBodyDistance = Mathf.Max(maxBodyDistance, (bodyPositions[nearest] - bodyLocal).magnitude);
                    if (i == crotch)
                        w = new BoneWeight { boneIndex0 = thighLBone, weight0 = 0.45f,
                            boneIndex1 = thighRBone, weight1 = 0.45f, boneIndex2 = pelvisBone, weight2 = 0.1f };
                }
                weights.Add(w);
            }
            if (renderer != body)
                Debug.Log("SLICE_WEIGHTS resident=" + root.name + " garment=" + renderer.name
                    + " transferred=" + transferred + " max_body_distance="
                    + maxBodyDistance.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    + (crotch >= 0 ? " crotch=" + crotch + " input_pelvis=" + crotchPelvis : ""));
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Material material = s < renderer.sharedMaterials.Length ? renderer.sharedMaterials[s] : null;
                if (renderer == body && material != null && material.name.StartsWith("Eye_", StringComparison.Ordinal)) continue;
                var source = mesh.GetTriangles(s);
                var copy = new int[source.Length];
                for (int i = 0; i < source.Length; i++) copy[i] = source[i] + offset;
                materials.Add(material);
                triangles.Add(copy);
            }
        };

        add(body);
        foreach (var garment in garments.OrderBy(x => x.name, StringComparer.Ordinal)) add(garment);

        var combined = new Mesh();
        combined.name = "Resident_" + (resident + 1) + "_Combined";
        combined.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        combined.SetVertices(vertices);
        combined.SetNormals(normals);
        combined.SetTangents(tangents);
        combined.SetUVs(0, uv);
        combined.SetUVs(1, uv2);
        combined.SetColors(colors);
        combined.boneWeights = weights.ToArray();
        combined.bindposes = sourceBodyMesh.bindposes;
        combined.subMeshCount = triangles.Count;
        for (int i = 0; i < triangles.Count; i++) combined.SetTriangles(triangles[i], i);
        combined.RecalculateBounds();

        string assetPath = LookDir + "/Resident_" + (resident + 1) + "-Combined.asset";
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(combined, assetPath);
        body.sharedMesh = combined;
        body.sharedMaterials = materials.ToArray();
        body.localBounds = combined.bounds;
        foreach (var garment in garments) Object.DestroyImmediate(garment.gameObject);
        if (clothingRig != null) Object.DestroyImmediate(clothingRig.gameObject);
        Debug.Log("SLICE_COMBINE resident=" + root.name + " body=" + sourceAsset + " bone=" + bodyBones.Length
            + " vertices=" + vertices.Count + " submeshes=" + triangles.Count + " bottom=" + bottomStart + "+" + bottomCount);
        return new SliceResidentVisual {
            base_body = baseBody, source_asset = sourceAsset, source_body_mesh = sourceBodyMesh,
            body_renderer = body, bottom_vertex_start = bottomStart, bottom_vertex_count = bottomCount };
    }

    static bool IsBodySkinRenderer(SkinnedMeshRenderer renderer)
    {
        foreach (var material in renderer.sharedMaterials)
            if (material != null && material.name.StartsWith("Skin_", StringComparison.Ordinal)) return true;
        return false;
    }

    static bool IsGarmentRenderer(SkinnedMeshRenderer renderer)
    {
        if (renderer.name.Contains("-top__") || renderer.name.Contains("-bottom__") || renderer.name.Contains("-footwear__")) return true;
        foreach (var material in renderer.sharedMaterials)
            if (material != null && (material.name.Contains("-top__") || material.name.Contains("-bottom__") || material.name.Contains("-footwear__"))) return true;
        return false;
    }

    static long GridKey(int x, int y, int z)
    {
        return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }

    static Type MixerType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name);
            if (type != null) return type;
        }
        var loaded = Type.GetType(name + ", UnityEditor.CoreModule") ?? Type.GetType(name + ", UnityEditor");
        if (loaded != null) return loaded;
        throw new Exception("SLICE_BUILD missing editor audio type " + name);
    }

    static AudioMixerAsset EnsureMixer()
    {
        var mixer = AssetDatabase.LoadAssetAtPath<AudioMixerAsset>(MixerPath);
        if (mixer == null)
        {
            var controllerType = MixerType("UnityEditor.Audio.AudioMixerController");
            var create = controllerType.GetMethod("CreateMixerControllerAtPath", BindingFlags.Public | BindingFlags.Static);
            mixer = (AudioMixerAsset)create.Invoke(null, new object[] { MixerPath });
        }
        var controller = (object)mixer;
        var controllerType2 = controller.GetType();
        var createGroup = controllerType2.GetMethod("CreateNewGroup");
        var addChild = controllerType2.GetMethod("AddChildToParent");
        var master = controllerType2.GetProperty("masterGroup").GetValue(controller);
        foreach (string name in new[] { "Ambience", "World", "Voice", "Interface" })
        {
            if (mixer.FindMatchingGroups(name).Length > 0) continue;
            var group = createGroup.Invoke(controller, new object[] { name, false });
            addChild.Invoke(controller, new[] { group, master });
        }
        ExposeGroupVolumes(controller, mixer);
        AssetDatabase.SaveAssets();
        return mixer;
    }

    static void ExposeGroupVolumes(object controller, AudioMixerAsset mixer)
    {
        var controllerType = controller.GetType();
        var exposedProperty = controllerType.GetProperty("exposedParameters");
        var addExposed = controllerType.GetMethod("AddExposedParameter");
        var contains = controllerType.GetMethod("ContainsExposedParameter");
        var pathType = MixerType("UnityEditor.Audio.AudioGroupParameterPath");
        var getGuid = controllerType.GetProperty("masterGroup").PropertyType.GetMethod("GetGUIDForVolume");
        var names = new[] { "Ambience", "World", "Voice", "Interface" };
        var wanted = new Dictionary<object, string>();
        foreach (string name in names)
        {
            var group = mixer.FindMatchingGroups(name)[0];
            object guid = getGuid.Invoke(group, null);
            wanted[guid] = name + "Volume";
            if (!(bool)contains.Invoke(controller, new[] { guid }))
            {
                var path = Activator.CreateInstance(pathType, new[] { (object)group, guid });
                addExposed.Invoke(controller, new[] { path });
            }
        }
        var exposed = (Array)exposedProperty.GetValue(controller);
        for (int i = 0; i < exposed.Length; i++)
        {
            object item = exposed.GetValue(i);
            var guidField = item.GetType().GetField("guid");
            var nameField = item.GetType().GetField("name");
            object guid = guidField.GetValue(item);
            string desired;
            if (wanted.TryGetValue(guid, out desired)) nameField.SetValue(item, desired);
            exposed.SetValue(item, i);
        }
        exposedProperty.SetValue(controller, exposed);
        controllerType.GetMethod("OnChangedExposedParameter").Invoke(controller, null);
    }

    static AudioMixerGroupAsset Group(AudioMixerAsset mixer, string name)
    {
        var groups = mixer.FindMatchingGroups(name);
        if (groups.Length != 1) throw new Exception("SLICE_BUILD mixer group " + name + " count=" + groups.Length);
        return groups[0];
    }

    static void Paint(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var material = new Material(shader);
        material.color = color;
        material.SetFloat("_Smoothness", 0.3f);
        renderer.sharedMaterial = material;
    }

    static void CleanAssetText(string path)
    {
        // Unity emits blank YAML values with trailing spaces; keep generated diffs clean.
        var lines = System.IO.File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        System.IO.File.WriteAllLines(path, lines);
    }

    static Color Hex(string value)
    {
        if (!ColorUtility.TryParseHtmlString(value, out Color color)) throw new System.ArgumentException(value);
        return color;
    }

    static T SaveLook<T>(T asset, string file) where T : Object
    {
        string path = LookDir + "/" + file;
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(asset, existing);
            Object.DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    static Material PaletteMaterial(Material source, string hex, string file)
    {
        var material = new Material(source);
        material.name = file;
        var color = Hex(hex);
        if (source.color.a < 0.99f) color.a = 0.25f;
        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", color.a < 1f ? 0.9f : 0.3f);
        if (material.HasProperty("roughnessFactor")) material.SetFloat("roughnessFactor", color.a < 1f ? 0.1f : 0.7f);
        if (material.HasProperty("metallicFactor")) material.SetFloat("metallicFactor", 0f);
        return SaveLook(material, file + ".mat");
    }

    static void ApplyEnvironmentPalette(Scene scene)
    {
        // Ivory is lowered within Delta E 2000 <= 5 to keep the daylight walls in range.
        var palette = new Dictionary<string, string> {
            { "Ivory plaster", "#DED6C8" }, { "Muted teal", "#2F9A9C" },
            { "Light timber", "#D09F64" }, { "Rust", "#D2602E" },
            { "Leaf green", "#5DAA3C" }, { "Charcoal", "#2E2523" },
            { "Pale brick", "#E8705A" }, { "seat-teal", "#2F9A9C" },
            { "sofa-teal", "#2F9A9C" }, { "frame-wood", "#D9A86C" },
            { "frame-timber", "#D9A86C" }, { "pillow-ivory", "#DED6C8" },
            { "pillow-rust", "#D2602E" }
        };
        var copies = new Dictionary<Material, Material>();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !palette.TryGetValue(source.name, out string hex)) continue;
                    if (!copies.TryGetValue(source, out Material copy))
                    {
                        bool glass = source.color.a < 0.99f;
                        copy = PaletteMaterial(source, glass ? "#4BA3D9" : hex, "Environment-" + source.name + (glass ? "-glass" : ""));
                        copies[source] = copy;
                    }
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
    }

    static void ApplyOutfitPalette(GameObject root, int resident)
    {
        string[] tops = { "#E8705A", "#2F9A9C", "#C23B5A", "#4BA3D9", "#F2B33D", "#5DAA3C", "#8A4F9E", "#D2602E" };
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var source = materials[i];
                if (source == null) continue;
                string hex = source.name.Contains("-top__") ? tops[resident]
                    : source.name.Contains("-bottom__") ? "#2E2523"
                    : source.name.Contains("-footwear__") ? "#D9A86C" : null;
                if (hex != null) materials[i] = PaletteMaterial(source, hex, "Resident-" + (resident + 1) + "-" + source.name);
            }
            renderer.sharedMaterials = materials;
        }
    }

    static void AddPractical(Transform parent, Vector3 position)
    {
        var light = new GameObject("Practical").AddComponent<Light>();
        light.transform.SetParent(parent, false);
        light.transform.position = position;
        light.type = LightType.Spot;
        light.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        light.spotAngle = 100f;
        light.innerSpotAngle = 55f;
        light.useColorTemperature = true;
        light.colorTemperature = 3200f;
        light.intensity = 0.12f;
        light.range = 4f;
        light.shadows = LightShadows.None;
    }

    static void BuildGrade(Transform parent)
    {
        string path = LookDir + "/Vibrant.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
        }
        foreach (var component in profile.components) Object.DestroyImmediate(component, true);
        profile.components.Clear();
        var tone = profile.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.Neutral);
        var color = profile.Add<ColorAdjustments>(true);
        color.postExposure.Override(0.1f);
        color.contrast.Override(15f);
        color.saturation.Override(20f);
        color.hueShift.Override(0f);
        var white = profile.Add<WhiteBalance>(true);
        white.temperature.Override(5f);
        white.tint.Override(0f);
        var split = profile.Add<SplitToning>(true);
        split.shadows.Override(Hex("#5E7FA8"));
        split.highlights.Override(Hex("#F6D9A8"));
        split.balance.Override(0f);
        var bloom = profile.Add<Bloom>(true);
        bloom.filter.Override(BloomFilterMode.Kawase);
        bloom.threshold.Override(1f);
        bloom.intensity.Override(0.2f);
        bloom.scatter.Override(0.6f);
        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(0.15f);
        vignette.smoothness.Override(0.4f);
        foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
        EditorUtility.SetDirty(profile);
        var volume = new GameObject("VibrantGrade").AddComponent<Volume>();
        volume.transform.SetParent(parent, false);
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.sharedProfile = profile;
    }

    const string RunKey = "SliceSceneBuild.RunActive";

    [InitializeOnLoadMethod]
    static void ResubscribeAfterReload()
    {
        if (SessionState.GetBool(RunKey, false))
        {
            EditorApplication.update += PollRun;
            EditorApplication.playModeStateChanged += OnRunState;
        }
    }

    // Opens the slice scene, runs RunVerification into the repo evidence
    // directory twice (two play entries, domain reload off), then exits.
    public static void PlaySlice()
    {
        bool noDomainReload = EditorSettings.enterPlayModeOptionsEnabled
            && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload;
        if (!noDomainReload) throw new System.Exception("SLICE requires domain reload off and scene reload on");
        Debug.Log("SLICE_MODE domainReload=False sceneReload=True");
        string run = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName,
            "evidence", "production", "SLICE", "run");
        foreach (string owned in new[] { "entries", "traces" })
        {
            string path = System.IO.Path.Combine(run, owned);
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
        }
        StartPass(1, 2);
    }

    static void StartPass(int pass, int passes)
    {
        EditorSceneManager.OpenScene(ScenePath);
        SessionState.SetBool(RunKey, true);
        SessionState.SetInt(RunKey + ".Pass", pass);
        SessionState.SetInt(RunKey + ".Passes", passes);
        SessionState.SetFloat(RunKey + ".Start", (float)EditorApplication.timeSinceStartup);
        EditorApplication.update -= PollRun;
        EditorApplication.playModeStateChanged -= OnRunState;
        EditorApplication.update += PollRun;
        EditorApplication.playModeStateChanged += OnRunState;
        EditorApplication.EnterPlaymode();
    }

    static void PollRun()
    {
        if (!EditorApplication.isPlaying) return;
        var go = GameObject.Find("SliceDirector");
        var director = go == null ? null : go.GetComponent<SliceDirector>();
        if (director != null && !director.done && SessionState.GetBool(RunKey + ".Started", false) == false)
        {
            SessionState.SetBool(RunKey + ".Started", true);
            string root = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string dir = System.IO.Path.Combine(root, "evidence", "production", "SLICE", "run");
            director.RunVerification(dir);
        }
        if (director != null && director.done)
        {
            SessionState.SetBool(RunKey + ".Failed", director.failed);
            SessionState.SetBool(RunKey + ".Started", false);
            EditorApplication.update -= PollRun;
            EditorApplication.ExitPlaymode();
            return;
        }
        if (EditorApplication.timeSinceStartup - SessionState.GetFloat(RunKey + ".Start", 0f) > 300.0)
        {
            Debug.Log("SLICE_RUN runner timeout");
            SessionState.SetBool(RunKey + ".Failed", true);
            EditorApplication.update -= PollRun;
            EditorApplication.ExitPlaymode();
        }
    }

    static void OnRunState(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        bool failed = SessionState.GetBool(RunKey + ".Failed", true);
        int pass = SessionState.GetInt(RunKey + ".Pass", 1);
        int passes = SessionState.GetInt(RunKey + ".Passes", 1);
        Debug.Log("SLICE_RUN exited pass=" + pass + " failed=" + failed);
        if (!failed && pass < passes)
        {
            EditorApplication.playModeStateChanged -= OnRunState;
            StartPass(pass + 1, passes);
            return;
        }
        SessionState.EraseBool(RunKey);
        SessionState.EraseBool(RunKey + ".Failed");
        SessionState.EraseBool(RunKey + ".Started");
        SessionState.EraseInt(RunKey + ".Pass");
        SessionState.EraseInt(RunKey + ".Passes");
        SessionState.EraseFloat(RunKey + ".Start");
        EditorApplication.playModeStateChanged -= OnRunState;
        EditorApplication.Exit(failed ? 1 : 0);
    }
}
