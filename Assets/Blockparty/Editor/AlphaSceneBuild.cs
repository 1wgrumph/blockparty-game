using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// ALPHA scene entry points. Runs headless via -executeMethod:
//   Unity -batchmode -nographics -projectPath . -executeMethod AlphaSceneBuild.BuildScene -quit
//   DISPLAY exists: Unity -projectPath . -executeMethod AlphaSceneBuild.PlayAlpha -logFile <log>
// Assembles Assets/Blockparty/Scenes/Alpha.unity: the EightResident greybox
// block (ground, two homes, path), the four landed BLOCK-SITES buildings at
// their sim origins (cafe 0,0 / workshop 30,0 / office 0,30 / school 30,30),
// twenty resident roots (dressed, animated characters at play: POLISH-PEOPLE),
// per-class activity stages with their greybox props,
// the sun, the wide ALPHA camera and the post-processing volume (created at
// play by AlphaDirector so no extra profile asset is needed) and the
// GYM-TRAIL extension (gym annex kit, bike trail, rider, bicycle, held
// dumbbells) bound to the Alpha sim. The written
// Alpha.unity owns the run wiring; AlphaDirector builds the sim at play.
public static class AlphaSceneBuild
{
    public const string ScenePath = "Assets/Blockparty/Scenes/Alpha.unity";

    static readonly string[] SiteIds = { "cafe", "workshop", "office", "school" };
    static readonly string[] SiteAssets =
    {
        "Assets/Blockparty/Art/Environment/Workplaces/cafe.gltf",
        "Assets/Blockparty/Art/Environment/Workplaces/workshop.gltf",
        "Assets/Blockparty/Art/Environment/Civic/office.gltf",
        "Assets/Blockparty/Art/Environment/Civic/school.gltf",
    };
    static readonly Vector3[] SiteOrigins =
    {
        new Vector3(0f, 0f, 0f),
        new Vector3(30f, 0f, 0f),
        new Vector3(0f, 0f, 30f),
        new Vector3(30f, 0f, 30f),
    };

    static readonly Color Ground = C(0.44f, 0.50f, 0.38f);
    static readonly Color Asphalt = C(0.29f, 0.30f, 0.32f);
    static readonly Color Wall = C(0.78f, 0.73f, 0.64f);
    static readonly Color Floor = C(0.50f, 0.45f, 0.38f);
    static readonly Color Roof = C(0.38f, 0.26f, 0.20f);
    static readonly Color Wood = C(0.46f, 0.30f, 0.18f);
    static readonly Color DarkWood = C(0.30f, 0.19f, 0.12f);
    static readonly Color Stone = C(0.53f, 0.53f, 0.50f);
    static readonly Color Charcoal = C(0.18f, 0.19f, 0.20f);
    static readonly Color Teal = C(0.18f, 0.42f, 0.44f);
    static readonly Color Rust = C(0.62f, 0.29f, 0.17f);
    static readonly Color Green = C(0.28f, 0.45f, 0.24f);
    static readonly Color LightGreen = C(0.43f, 0.61f, 0.32f);
    static readonly Color Cream = C(0.93f, 0.89f, 0.79f);
    static readonly Color Red = C(0.70f, 0.17f, 0.15f);
    static readonly Color Yellow = C(0.87f, 0.71f, 0.24f);
    static readonly Color White = C(0.93f, 0.93f, 0.90f);
    static readonly Color Orange = C(0.76f, 0.44f, 0.17f);

    // Actor/camera/look spots shared with AlphaDirector's ClassStage array.
    static readonly Vector3 FoodActor = new Vector3(9.9f, 0f, 5.9f);
    static readonly Vector3 WorkActor = new Vector3(2.0f, 0f, -0.3f);
    static readonly Vector3 EducationActor = new Vector3(27.0f, 0f, 30.53f);
    static readonly Vector3 SocialActor = new Vector3(15.7f, 0f, 13.9f);
    static readonly Vector3 PartyActor = new Vector3(12f, 0f, 8.6f);
    static readonly Vector3 MinigolfActor = new Vector3(39.3f, 0f, -6.9f);
    static readonly Vector3 HealthActor = new Vector3(15.2f, 1.05f, 9.5f);
    static readonly Vector3 ClubActor = new Vector3(9.2f, 0f, -7.5f);
    static readonly Vector3 HomeActor = new Vector3(14.0f, 0f, 6.0f);
    static readonly Vector3 CreativeActor = new Vector3(9.7f, 0f, 9.9f);

    static readonly Dictionary<Color32, Material> Materials = new Dictionary<Color32, Material>();

    static Color C(float r, float g, float b) { return new Color(r, g, b); }

    public static void BuildScene()
    {
        Materials.Clear();
        if (!AssetDatabase.IsValidFolder("Assets/Blockparty/Scenes"))
            AssetDatabase.CreateFolder("Assets/Blockparty", "Scenes");
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject world = new GameObject("AlphaWorld");
        SceneManager.MoveGameObjectToScene(world, scene);

        // Sits below the GYM-TRAIL kit grade, as in the qualified GymTrail scene.
        Box(world.transform, "Ground", new Vector3(15f, -0.225f, 15f), new Vector3(90f, 0.3f, 70f), Ground);
        Box(world.transform, "Asphalt", new Vector3(21f, -0.02f, 15f), new Vector3(4f, 0.1f, 44f), Asphalt);
        Box(world.transform, "CrossPath", new Vector3(15f, -0.02f, 15f), new Vector3(30f, 0.1f, 3f), Asphalt);
        Box(world.transform, "HomeAPath", new Vector3(12f, -0.02f, 2.6f), new Vector3(2f, 0.1f, 3.2f), Asphalt);
        Box(world.transform, "HomeBPath", new Vector3(12f, -0.02f, 17.4f), new Vector3(2f, 0.1f, 3.2f), Asphalt);

        var homes = new GameObject("Homes");
        homes.transform.SetParent(world.transform, false);
        BuildHome(homes.transform, "HomeA", 12f, 8f);
        BuildHome(homes.transform, "HomeB", 12f, 22f);

        var props = new GameObject("ClassProps");
        props.transform.SetParent(world.transform, false);
        BuildHomeProps(props.transform);
        BuildSocialGreen(props.transform);
        BuildClubGazebo(props.transform);
        BuildMinigolfCourse(props.transform);

        var sites = new GameObject("Sites");
        sites.transform.SetParent(world.transform, false);
        Transform[] siteMarkers = new Transform[SiteIds.Length];
        for (int i = 0; i < SiteIds.Length; i++)
        {
            GameObject marker = new GameObject("Site_" + SiteIds[i]);
            marker.transform.SetParent(sites.transform, false);
            marker.transform.position = SiteOrigins[i];
            siteMarkers[i] = marker.transform;
            Place(SiteAssets[i], "Building_" + SiteIds[i], SiteOrigins[i], scene);
            // The glTF modules put their glazing on the north wall and their
            // entry on the south, so the readable approach mat sits north.
            Box(sites.transform, "Mat_" + SiteIds[i],
                SiteOrigins[i] + new Vector3(0f, 0.005f, 4.6f),
                new Vector3(4.2f, 0.02f, 2.2f), C(0.72f, 0.66f, 0.52f));
        }

        var residents = new GameObject("Residents");
        residents.transform.SetParent(world.transform, false);
        var looks = ResidentLooks(residents.transform);
        var contact = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ContactShader)) { name = "ResidentContact" };
        contact.SetColor("_ShadowColor", C(0.50f, 0.52f, 0.46f));
        contact.SetFloat("_Softness", 0.6f);
        Transform[] roots = new Transform[20];
        for (int i = 0; i < roots.Length; i++)
        {
            float x = 4f + (i % 10) * 1.2f;
            float z = i < 10 ? 1.5f : 14.5f;
            var root = new GameObject("Resident_" + (i + 1));
            root.transform.SetParent(residents.transform, false);
            root.transform.position = new Vector3(x, 0f, z);
            // The dressed character arrives under Body at play, from the resident's stored look.
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            // AlphaDirector's click raycast target, on Body so it follows the drawn (gliding)
            // resident; AlphaResidentLooks enables it and fits it to the stage once a roster
            // resident (live play starts with 8) takes the root.
            var hit = body.AddComponent<CapsuleCollider>();
            hit.center = new Vector3(0f, 0.875f, 0f);
            hit.height = 1.75f;
            hit.radius = 0.3f;
            hit.enabled = false;
            var shadow = GameObject.CreatePrimitive(PrimitiveType.Quad);
            shadow.name = "Contact";
            Object.DestroyImmediate(shadow.GetComponent<Collider>());
            shadow.transform.SetParent(body.transform, false);
            shadow.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(0.9f, 0.9f, 1f);
            var shade = shadow.GetComponent<MeshRenderer>();
            shade.sharedMaterial = contact;
            shade.shadowCastingMode = ShadowCastingMode.Off;
            shade.receiveShadows = false;
            shadow.SetActive(false);
            roots[i] = root.transform;
        }

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(world.transform, false);
        sun.type = LightType.Directional;
        sun.useColorTemperature = true;
        sun.colorTemperature = 5600f;
        sun.color = Color.white;
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.65f;
        sun.transform.rotation = Quaternion.Euler(40f, 150f, 0f);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = C(0.55f, 0.70f, 0.85f);
        RenderSettings.ambientEquatorColor = C(0.72f, 0.67f, 0.59f);
        RenderSettings.ambientGroundColor = C(0.34f, 0.31f, 0.26f);
        RenderSettings.ambientIntensity = 0.55f;
        RenderSettings.sun = sun;
        RenderSettings.fog = false;

        FillLight(world.transform, "HomeFill", new Vector3(11f, 1.4f, 8.5f), 11f, 5f)
            .cullingMask &= ~(1 << LayerMask.NameToLayer("Ignore Raycast"));
        FillLight(world.transform, "CafeFill", new Vector3(0f, 2.25f, 0f), 7f);
        FillLight(world.transform, "SchoolFill", new Vector3(30f, 2.25f, 30f), 8f);

        var camera = new GameObject("AlphaCamera").AddComponent<Camera>();
        camera.transform.SetParent(world.transform, false);
        camera.transform.position = new Vector3(36f, 17f, -18f);
        camera.transform.LookAt(new Vector3(14f, 0.8f, 15f));
        camera.fieldOfView = 55f;
        camera.farClipPlane = 300f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = C(0.52f, 0.70f, 0.85f);
        camera.allowHDR = true;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        camera.tag = "MainCamera";
        camera.gameObject.AddComponent<AudioListener>();

        var stageRoot = new GameObject("ClassStages");
        stageRoot.transform.SetParent(world.transform, false);
        List<AlphaDirector.ClassStage> classStages = new List<AlphaDirector.ClassStage>();
        AddStage(stageRoot.transform, classStages, "FOOD", FoodActor, 270f,
            new Vector3(13.4f, 1.9f, 4.3f), new Vector3(9.6f, 1.0f, 5.5f));
        AddStage(stageRoot.transform, classStages, "WORK", WorkActor, 270f,
            new Vector3(-0.2f, 2.0f, -2.5f), new Vector3(2.9f, 1.05f, 0.5f));
        AddStage(stageRoot.transform, classStages, "EDUCATION", EducationActor, 0f,
            new Vector3(30.2f, 1.8f, 28.4f), new Vector3(27.4f, 1.1f, 30.8f));
        AddStage(stageRoot.transform, classStages, "SOCIAL", SocialActor, 0f,
            new Vector3(11.8f, 1.9f, 14.5f), new Vector3(15.8f, 1.05f, 14.5f));
        AddStage(stageRoot.transform, classStages, "PARTY", PartyActor, 0f,
            new Vector3(15.2f, 1.9f, 7.4f), new Vector3(12f, 1.0f, 10f));
        AddStage(stageRoot.transform, classStages, "MINIGOLF", MinigolfActor, 90f,
            new Vector3(36.9f, 1.7f, -4.9f), new Vector3(40.4f, 0.7f, -7.7f));
        AddStage(stageRoot.transform, classStages, "HEALTH", HealthActor, 0f,
            new Vector3(16.4f, 2.15f, 7.8f), new Vector3(15.1f, 0.95f, 10.3f), true);
        AddStage(stageRoot.transform, classStages, "CLUB", ClubActor, 90f,
            new Vector3(10f, 1.0f, -11.5f), new Vector3(10f, 1.0f, -7.5f));
        AddStage(stageRoot.transform, classStages, "HOME", HomeActor, 180f,
            new Vector3(12.4f, 2.0f, 4.3f), new Vector3(14.4f, 0.85f, 5.5f));
        AddStage(stageRoot.transform, classStages, "CREATIVE", CreativeActor, 270f,
            new Vector3(10.7f, 2.0f, 11.5f), new Vector3(8.9f, 1.15f, 10.4f));

        var directorObject = new GameObject("AlphaDirector");
        directorObject.transform.SetParent(world.transform, false);
        var director = directorObject.AddComponent<AlphaDirector>();
        director.residentRoots = roots;
        director.alphaCamera = camera;
        director.siteMarkers = siteMarkers;
        director.classStages = classStages.ToArray();
        director.residentLooks = looks;
        BuildGymTrail(director);
        AlphaPolishSceneBuild.Apply(world.transform, camera, sun);

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("ALPHA_BUILD saved=" + ScenePath + " residents=" + roots.Length
            + " sites=" + SiteIds.Length + " stages=" + classStages.Count);
    }

    const string MainScene = "Assets/Blockparty/Scenes/Main.unity";
    const string ContactShader = "Assets/Blockparty/Environment/Polish/SoftContact.shader";

    // POLISH-PEOPLE: a disabled MainDirector carrying Main.unity's CONTENT-IMPORT
    // bindings (stage variants, wardrobe, appearance textures, ANIM catalog and
    // clips) dresses and animates the residents through AlphaResidentLooks.
    static AlphaResidentLooks ResidentLooks(Transform parent)
    {
        var holder = new GameObject("ResidentLooks");
        holder.transform.SetParent(parent, false);
        var wardrobe = holder.AddComponent<MainDirector>();
        Scene main = EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Additive);
        try
        {
            MainDirector source = null;
            foreach (var root in main.GetRootGameObjects())
                if (source == null) source = root.GetComponentInChildren<MainDirector>(true);
            if (source == null) throw new System.Exception("ALPHA_BUILD no MainDirector in " + MainScene);
            EditorUtility.CopySerialized(source, wardrobe);
        }
        finally { EditorSceneManager.CloseScene(main, true); }
        // Main.unity's own scene objects stay there; the residents get Main's two
        // camera-aimed fills (MainSceneBuild.Fill: ResidentFill 0.5, DeepSkinFill 1.2).
        wardrobe.residentRoot = null;
        wardrobe.propLibrary = null;
        wardrobe.residentFill = ResidentFill(holder.transform, "ResidentFill", 0.5f,
            (1 << MainDirector.ResidentLayer) | (1 << MainDirector.DeepSkinLayer));
        wardrobe.deepSkinFill = ResidentFill(holder.transform, "DeepSkinFill", 1.2f, 1 << MainDirector.DeepSkinLayer);
        wardrobe.spawn = new Vector3[0];
        wardrobe.enabled = false;
        if (wardrobe.variantPrefabs.Length == 0 || wardrobe.wardrobe == null || wardrobe.actionRig == null)
            throw new System.Exception("ALPHA_BUILD Main.unity director is not bound");
        var looks = holder.AddComponent<AlphaResidentLooks>();
        looks.wardrobe = wardrobe;
        looks.ringMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "SelectionRing" };
        looks.ringMaterial.SetColor("_BaseColor", C(0.95f, 0.70f, 0.30f));
        return looks;
    }

    static Light ResidentFill(Transform parent, string name, float intensity, int mask)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.transform.SetParent(parent, false);
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.86f, 0.72f);
        light.intensity = intensity;
        light.shadows = LightShadows.None;
        light.cullingMask = mask;
        light.gameObject.AddComponent<UniversalAdditionalLightData>();
        return light;
    }

    // The rider's clips, in evidence/production/GYM-TRAIL/source/motion-input.json
    // take order for the female Adult cell.
    const string RiderAsset = "Assets/Blockparty/Art/Animations/GymTrail/female-Adult.gltf";
    static readonly string[] RiderClips =
    {
        "cycling-approach", "cycling-start", "cycling-loop", "cycling-finish", "cycling-interruption",
        "gym-approach", "gym-start", "gym-loop", "gym-finish", "gym-interruption",
    };

    // GYM-TRAIL on the Alpha block: the rider takes resident slot 0 and the
    // GymTrailDirector binds to the Alpha sim's World/Clock/Executor at play.
    static void BuildGymTrail(AlphaDirector alpha)
    {
        var extension = new GameObject("GymTrailExtension");
        var kit = Spawn("Assets/Blockparty/Art/Environment/block.gltf");
        kit.transform.SetParent(extension.transform);
        // The Alpha already owns these four identical modules and their simulation markers.
        foreach (var name in SiteIds) Object.DestroyImmediate(Find(kit, name).gameObject);
        Spawn("Assets/Blockparty/Art/Environment/Block/bike-trail.gltf").transform.SetParent(extension.transform);
        var actor = Spawn(RiderAsset);
        actor.transform.SetParent(extension.transform);
        var furniture = Spawn("Assets/Blockparty/Art/Props/furnishings.gltf");
        var bike = Object.Instantiate(Find(furniture, "PROP_Bike").gameObject);
        bike.transform.SetParent(null);
        bike.transform.position = Vector3.zero;
        bike.transform.rotation = Quaternion.identity;
        Object.DestroyImmediate(furniture);
        bike.transform.SetParent(extension.transform);

        var gym = extension.AddComponent<GymTrailDirector>();
        gym.bindToAlpha = true;
        gym.configuration = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Blockparty/Content/gym-trail-runtime.json");
        gym.navigation = AssetDatabase.LoadAssetAtPath<NavMeshData>("Assets/Blockparty/Scenes/GymTrailNav.asset");
        gym.residentRoot = actor.transform;
        gym.bicycleRoot = bike.transform;
        var clips = new List<AnimationClip>();
        foreach (var clip in RiderClips) clips.Add(Clip(RiderAsset, clip));
        gym.clips = clips.ToArray();
        gym.handWeights = HeldWeights(actor, kit);

        alpha.residentRoots[0].gameObject.SetActive(false);
        alpha.residentRoots[0] = actor.transform;
        alpha.gymTrail = gym;
    }

    // Copies each floor dumbbell (rod and two plates) into a HeldDumbbell
    // group in the rider's hand and hides the floor originals.
    static Transform[] HeldWeights(GameObject actor, GameObject kit)
    {
        var result = new List<Transform>();
        for (int j = 0; j < 2; j++)
        {
            string rodName = "gym_dumbbell" + (j == 0 ? "" : ".001");
            var group = new GameObject("HeldDumbbell");
            group.transform.position = Find(kit, rodName).position;
            foreach (var name in new[] { rodName, "gym_weight" + (j == 0 ? "" : ".002"), "gym_weight" + (j == 0 ? ".001" : ".003") })
            {
                var source = Find(kit, name);
                var copy = Object.Instantiate(source.gameObject);
                copy.transform.SetPositionAndRotation(source.position, source.rotation);
                copy.transform.localScale = source.lossyScale;
                copy.transform.SetParent(group.transform, true);
                source.gameObject.SetActive(false);
            }
            var hand = Find(actor, j == 0 ? "hand_l" : "hand_r");
            group.transform.SetParent(hand, true);
            group.transform.position = (hand.position + Find(actor, j == 0 ? "middle_01_l" : "middle_01_r").position) * 0.5f;
            group.transform.localScale *= 0.7f;
            result.Add(group.transform);
        }
        return result.ToArray();
    }

    static GameObject Spawn(string path)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        if (prefab == null) throw new System.Exception("ALPHA_BUILD missing prefab " + path);
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
    }

    static Transform Find(GameObject root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        throw new System.Exception("ALPHA_BUILD missing transform " + name);
    }

    static AnimationClip Clip(string asset, string name)
    {
        foreach (var x in AssetDatabase.LoadAllAssetsAtPath(asset))
        {
            var clip = x as AnimationClip;
            if (clip != null && clip.name == name) return clip;
        }
        throw new System.Exception("ALPHA_BUILD missing clip " + asset + " " + name);
    }

    static void AddStage(Transform parent, List<AlphaDirector.ClassStage> stages, string id,
        Vector3 actor, float yaw, Vector3 camera, Vector3 look, bool lying = false)
    {
        var stage = new AlphaDirector.ClassStage();
        stage.id = id;
        stage.actor = Marker(parent, "Actor_" + id, actor,
            lying ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(0f, yaw, 0f)).transform;
        stage.camera = Marker(parent, "Camera_" + id, camera, Quaternion.identity).transform;
        stage.look = Marker(parent, "Look_" + id, look, Quaternion.identity).transform;
        stages.Add(stage);
    }

    static GameObject Marker(Transform parent, string name, Vector3 position, Quaternion rotation)
    {
        var marker = new GameObject(name);
        marker.transform.SetParent(parent, false);
        marker.transform.position = position;
        marker.transform.rotation = rotation;
        return marker;
    }

    static Light FillLight(Transform parent, string name, Vector3 position, float range, float intensity = 3f)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.transform.SetParent(parent, false);
        light.transform.position = position;
        light.type = LightType.Point;
        light.range = range;
        light.intensity = intensity;
        light.color = C(1f, 0.91f, 0.80f);
        light.shadows = LightShadows.None;
        return light;
    }

    static void BuildHome(Transform homes, string name, float cx, float cz)
    {
        var home = new GameObject(name);
        home.transform.SetParent(homes, false);
        Box(home.transform, "Floor", new Vector3(cx, -0.05f, cz), new Vector3(10.6f, 0.2f, 8.6f), Floor);
        Box(home.transform, "WallN", new Vector3(cx, 1.3f, cz + 4.15f), new Vector3(10.6f, 2.6f, 0.3f), Wall);
        Box(home.transform, "WallS_w", new Vector3(cx - 3.1f, 1.3f, cz - 4.15f), new Vector3(4.4f, 2.6f, 0.3f), Wall);
        Box(home.transform, "WallS_e", new Vector3(cx + 3.1f, 1.3f, cz - 4.15f), new Vector3(4.4f, 2.6f, 0.3f), Wall);
        Box(home.transform, "DoorHeader", new Vector3(cx, 2.35f, cz - 4.15f), new Vector3(1.8f, 0.5f, 0.3f), Wall);
        Box(home.transform, "WallW", new Vector3(cx - 5.15f, 1.3f, cz), new Vector3(0.3f, 2.6f, 8.6f), Wall);
        Box(home.transform, "WallE", new Vector3(cx + 5.15f, 1.3f, cz), new Vector3(0.3f, 2.6f, 8.6f), Wall);
        // Keep the room fill off the low ceiling; sunlight and ambient still light it.
        Box(home.transform, "Roof", new Vector3(cx, 2.7f, cz), new Vector3(10.8f, 0.2f, 8.8f), Roof)
            .layer = LayerMask.NameToLayer("Ignore Raycast");
    }

    static void BuildHomeProps(Transform parent)
    {
        // Kitchen (FOOD) on the west wall; stove and steaming pot.
        Box(parent, "KitchenCounter", new Vector3(7.1f, 0.45f, 8.6f), new Vector3(0.6f, 0.9f, 3.4f), Wood);
        Box(parent, "KitchenTop", new Vector3(7.1f, 0.93f, 8.6f), new Vector3(0.7f, 0.08f, 3.5f), Stone);
        Box(parent, "Stove", new Vector3(9f, 0.45f, 5.5f), new Vector3(0.9f, 0.9f, 0.9f), Charcoal);
        Box(parent, "StoveTop", new Vector3(9f, 0.91f, 5.5f), new Vector3(0.94f, 0.04f, 0.94f), Stone);
        Cylinder(parent, "Pot", new Vector3(9f, 1.03f, 5.5f), 0.28f, 0.22f, Charcoal);
        Cylinder(parent, "PotLid", new Vector3(9f, 1.16f, 5.5f), 0.30f, 0.03f, Stone);
        Sphere(parent, "Steam1", new Vector3(8.93f, 1.42f, 5.5f), 0.20f, White);
        Sphere(parent, "Steam2", new Vector3(9.08f, 1.62f, 5.46f), 0.15f, White);
        Sphere(parent, "Steam3", new Vector3(8.97f, 1.80f, 5.54f), 0.11f, White);
        Box(parent, "PrepTable", new Vector3(9.9f, 0.45f, 4.35f), new Vector3(1.3f, 0.9f, 0.65f), Wood);
        Box(parent, "PrepTop", new Vector3(9.9f, 0.93f, 4.35f), new Vector3(1.4f, 0.08f, 0.75f), Stone);
        Box(parent, "Carrot", new Vector3(9.55f, 1.02f, 4.35f), new Vector3(0.22f, 0.08f, 0.08f), Orange);
        Box(parent, "Herb", new Vector3(10.1f, 1.02f, 4.30f), new Vector3(0.10f, 0.10f, 0.16f), LightGreen);
        Box(parent, "Board", new Vector3(10.3f, 1.02f, 4.42f), new Vector3(0.36f, 0.06f, 0.26f), Cream);

        // Dining set (PARTY) and the household table.
        Box(parent, "Table", new Vector3(12f, 0.75f, 10f), new Vector3(1.8f, 0.10f, 1.2f), Wood);
        Box(parent, "TableBase", new Vector3(12f, 0.35f, 10f), new Vector3(0.35f, 0.7f, 0.35f), DarkWood);
        ChairOf(parent, "ChairS", 12f, 8.9f, 0f);
        ChairOf(parent, "ChairN", 12f, 11.1f, 180f);
        ChairOf(parent, "ChairW", 10.8f, 10f, 90f);
        ChairOf(parent, "ChairE", 13.2f, 10f, 270f);
        for (int i = 0; i < 4; i++)
        {
            Cylinder(parent, "Plate" + i, new Vector3(11.5f + (i % 2) * 1f, 0.83f, 9.75f + (i / 2) * 0.5f), 0.16f, 0.02f, White);
            Cylinder(parent, "Cup" + i, new Vector3(11.55f + (i % 2) * 1f, 0.90f, 9.70f + (i / 2) * 0.5f), 0.05f, 0.12f, i % 2 == 0 ? Teal : Rust);
        }
        Cylinder(parent, "Cake", new Vector3(12f, 0.86f, 10.05f), 0.22f, 0.14f, Cream);
        for (int i = 0; i < 3; i++)
        {
            Cylinder(parent, "Candle" + i, new Vector3(11.86f + i * 0.14f, 0.98f, 10.05f), 0.015f, 0.18f, i == 1 ? Teal : Rust);
            Sphere(parent, "Flame" + i, new Vector3(11.86f + i * 0.14f, 1.09f, 10.05f), 0.05f, Yellow);
        }
        for (int i = 0; i < 7; i++)
            Box(parent, "Bunting" + i, new Vector3(10.2f + i * 0.6f, 2.30f, 12.08f),
                new Vector3(0.26f, 0.26f, 0.03f), i % 3 == 0 ? Teal : i % 3 == 1 ? Rust : Yellow);

        // Bed and health cross (HEALTH).
        Box(parent, "BedFrame", new Vector3(15.2f, 0.28f, 10.4f), new Vector3(1.55f, 0.36f, 2.30f), DarkWood);
        Box(parent, "Mattress", new Vector3(15.2f, 0.55f, 10.4f), new Vector3(1.42f, 0.18f, 2.12f), Cream);
        Box(parent, "Pillow", new Vector3(15.2f, 0.68f, 11.25f), new Vector3(0.70f, 0.14f, 0.40f), White);
        Box(parent, "Headboard", new Vector3(15.2f, 0.95f, 11.45f), new Vector3(1.6f, 1.1f, 0.10f), Wood);
        Box(parent, "HealthPlaque", new Vector3(15.2f, 1.95f, 12.08f), new Vector3(0.62f, 0.62f, 0.06f), White);
        Box(parent, "CrossH", new Vector3(15.2f, 1.95f, 12.03f), new Vector3(0.42f, 0.12f, 0.04f), Red);
        Box(parent, "CrossV", new Vector3(15.2f, 1.95f, 12.03f), new Vector3(0.12f, 0.42f, 0.04f), Red);

        // Easel and canvas (CREATIVE).
        Box(parent, "EaselLegA", new Vector3(8.22f, 0.75f, 10.15f), new Vector3(0.07f, 1.5f, 0.07f), DarkWood);
        Box(parent, "EaselLegB", new Vector3(8.22f, 0.75f, 11.05f), new Vector3(0.07f, 1.5f, 0.07f), DarkWood);
        Box(parent, "EaselLegC", new Vector3(8.92f, 0.75f, 10.60f), new Vector3(0.07f, 1.5f, 0.07f), DarkWood);
        Box(parent, "Canvas", new Vector3(8.45f, 1.42f, 10.60f), new Vector3(0.05f, 1.05f, 0.85f), Cream);
        Box(parent, "PaintA", new Vector3(8.49f, 1.62f, 10.38f), new Vector3(0.02f, 0.28f, 0.30f), Teal);
        Box(parent, "PaintB", new Vector3(8.49f, 1.62f, 10.82f), new Vector3(0.02f, 0.28f, 0.30f), Rust);
        Box(parent, "PaintC", new Vector3(8.49f, 1.28f, 10.60f), new Vector3(0.02f, 0.24f, 0.62f), Yellow);
        Cylinder(parent, "PaintPotA", new Vector3(9.05f, 0.10f, 9.95f), 0.09f, 0.20f, Teal);
        Cylinder(parent, "PaintPotB", new Vector3(9.22f, 0.10f, 10.18f), 0.09f, 0.20f, Rust);
        Cylinder(parent, "PaintPotC", new Vector3(9.05f, 0.10f, 10.42f), 0.09f, 0.20f, Yellow);

        // Bucket and mop (HOME).
        Cylinder(parent, "Bucket", new Vector3(14.8f, 0.22f, 5.15f), 0.23f, 0.44f, Teal);
        Box(parent, "MopHandle", new Vector3(14.92f, 0.95f, 5.05f), new Vector3(0.05f, 1.5f, 0.05f), DarkWood);
        Box(parent, "MopHead", new Vector3(14.92f, 0.12f, 5.05f), new Vector3(0.30f, 0.10f, 0.42f), Cream);
        Box(parent, "WetSign", new Vector3(13.55f, 0.42f, 4.85f), new Vector3(0.38f, 0.75f, 0.06f), Yellow);
    }

    static void BuildSocialGreen(Transform parent)
    {
        // The bench sits north of the chat pair: from the capture camera it
        // reads beside them instead of hiding behind their capsules.
        Box(parent, "BenchSeat", new Vector3(16.55f, 0.45f, 16.6f), new Vector3(0.55f, 0.10f, 1.9f), Wood);
        Box(parent, "BenchBack", new Vector3(16.88f, 0.82f, 16.6f), new Vector3(0.08f, 0.65f, 1.9f), Wood);
        Box(parent, "BenchLegA", new Vector3(16.55f, 0.22f, 15.8f), new Vector3(0.45f, 0.44f, 0.08f), DarkWood);
        Box(parent, "BenchLegB", new Vector3(16.55f, 0.22f, 17.4f), new Vector3(0.45f, 0.44f, 0.08f), DarkWood);
        Box(parent, "BenchTable", new Vector3(16.6f, 0.50f, 15.35f), new Vector3(0.7f, 0.08f, 0.7f), Wood);
        Cylinder(parent, "BenchCupA", new Vector3(16.45f, 0.62f, 15.25f), 0.06f, 0.14f, Teal);
        Cylinder(parent, "BenchCupB", new Vector3(16.78f, 0.62f, 15.48f), 0.06f, 0.14f, Rust);
        Cylinder(parent, "TreeTrunk", new Vector3(10.2f, 1.10f, 16.6f), 0.20f, 2.2f, DarkWood);
        Sphere(parent, "TreeCrownA", new Vector3(10.2f, 2.85f, 16.6f), 2.6f, Green);
        Sphere(parent, "TreeCrownB", new Vector3(9.4f, 2.55f, 16.1f), 1.7f, LightGreen);
        Box(parent, "FlowerBed", new Vector3(17.9f, 0.12f, 16.6f), new Vector3(2.2f, 0.24f, 0.9f), DarkWood);
        for (int i = 0; i < 4; i++)
            Sphere(parent, "Bloom" + i, new Vector3(17.1f + i * 0.5f, 0.35f, 16.6f), 0.20f, i % 2 == 0 ? Rust : Yellow);
    }

    static void BuildClubGazebo(Transform parent)
    {
        Vector3[] posts =
        {
            new Vector3(8.6f, 1.2f, -8.9f), new Vector3(11.4f, 1.2f, -8.9f),
            new Vector3(8.6f, 1.2f, -6.1f), new Vector3(11.4f, 1.2f, -6.1f),
        };
        for (int i = 0; i < posts.Length; i++)
            Box(parent, "GazeboPost" + i, posts[i], new Vector3(0.14f, 2.4f, 0.14f), DarkWood);
        Box(parent, "GazeboRoof", new Vector3(10f, 2.48f, -7.5f), new Vector3(3.5f, 0.16f, 3.4f), Green);
        Box(parent, "GazeboBeamS", new Vector3(10f, 2.28f, -8.9f), new Vector3(3.0f, 0.16f, 0.14f), DarkWood);
        Box(parent, "GazeboTable", new Vector3(10f, 0.75f, -7.5f), new Vector3(1.4f, 0.09f, 1.0f), Wood);
        Box(parent, "GazeboTableBase", new Vector3(10f, 0.36f, -7.5f), new Vector3(0.3f, 0.72f, 0.3f), DarkWood);
        Box(parent, "GazeboBenchW", new Vector3(8.95f, 0.45f, -7.5f), new Vector3(0.35f, 0.08f, 1.2f), Wood);
        Box(parent, "GazeboBenchE", new Vector3(11.05f, 0.45f, -7.5f), new Vector3(0.35f, 0.08f, 1.2f), Wood);
        Box(parent, "ClubSign", new Vector3(10f, 1.95f, -9.02f), new Vector3(1.9f, 0.55f, 0.08f), Green);
        Box(parent, "ClubSignFace", new Vector3(10f, 1.95f, -8.96f), new Vector3(1.55f, 0.30f, 0.03f), Cream);
        for (int i = 0; i < 4; i++)
        {
            Cylinder(parent, "Planter" + i, posts[i] + new Vector3(0f, -1.0f, 0f), 0.24f, 0.40f, Rust);
            Sphere(parent, "PlanterBloom" + i, posts[i] + new Vector3(0f, -0.55f, 0f), 0.42f, i % 2 == 0 ? Green : LightGreen);
        }
    }

    static void BuildMinigolfCourse(Transform parent)
    {
        Color green = C(0.30f, 0.58f, 0.28f);
        for (int i = 0; i < 3; i++)
        {
            float tee = 34.0f + i * 2.6f;
            float cup = tee + 1.8f;
            Box(parent, "Green" + i, new Vector3(tee + 1.0f, 0.06f, -8f), new Vector3(3.0f, 0.12f, 1.5f), green);
            Cylinder(parent, "Cup" + i, new Vector3(cup, 0.13f, -8f), 0.12f, 0.02f, Charcoal);
            Cylinder(parent, "Pin" + i, new Vector3(cup, 0.75f, -8f), 0.02f, 1.2f, White);
            Box(parent, "Flag" + i, new Vector3(cup + 0.18f, 1.25f, -8f), new Vector3(0.36f, 0.22f, 0.02f), Red);
            Cylinder(parent, "Tee" + i, new Vector3(tee, 0.13f, -8f), 0.10f, 0.02f, White);
        }
        Sphere(parent, "GolfBall", new Vector3(40.6f, 0.10f, -7.4f), 0.10f, White);
        Box(parent, "GolfClub", new Vector3(40.0f, 0.42f, -7.4f), new Vector3(0.05f, 0.85f, 0.05f), Charcoal);
        Box(parent, "GolfClubHead", new Vector3(40.3f, 0.06f, -7.4f), new Vector3(0.22f, 0.06f, 0.08f), Stone);
        Box(parent, "CourseBench", new Vector3(33.2f, 0.40f, -9.8f), new Vector3(1.6f, 0.10f, 0.45f), Wood);
        Box(parent, "CourseBenchBack", new Vector3(33.2f, 0.70f, -10.0f), new Vector3(1.6f, 0.55f, 0.07f), Wood);
    }

    static void ChairOf(Transform parent, string name, float x, float z, float yaw)
    {
        var chair = new GameObject(name);
        chair.transform.SetParent(parent, false);
        chair.transform.position = new Vector3(x, 0f, z);
        chair.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        BoxLocal(chair.transform, "Seat", new Vector3(0f, 0.45f, 0f), new Vector3(0.50f, 0.06f, 0.50f), Wood);
        BoxLocal(chair.transform, "Back", new Vector3(0f, 0.82f, -0.23f), new Vector3(0.50f, 0.70f, 0.06f), Wood);
        BoxLocal(chair.transform, "Legs", new Vector3(0f, 0.22f, 0f), new Vector3(0.08f, 0.44f, 0.08f), DarkWood);
    }

    static GameObject Place(string path, string name, Vector3 position, Scene scene)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        if (prefab == null)
        {
            throw new System.Exception("ALPHA_BUILD missing prefab " + path);
        }
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.name = name;
        instance.transform.position = position;
        return instance;
    }

    static Material MaterialFor(Color color)
    {
        Color32 key = color;
        Material material;
        if (Materials.TryGetValue(key, out material)) return material;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
        material = new Material(shader);
        material.color = color;
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.2f);
        Materials.Add(key, material);
        return material;
    }

    static void Paint(GameObject go, Color color)
    {
        Renderer renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = MaterialFor(color);
    }

    static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Color color)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.position = center;
        box.transform.localScale = size;
        Paint(box, color);
        return box;
    }

    static void BoxLocal(Transform parent, string name, Vector3 center, Vector3 size, Color color)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.localPosition = center;
        box.transform.localScale = size;
        Paint(box, color);
    }

    static void Cylinder(Transform parent, string name, Vector3 center, float radius, float height, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
        Paint(go, color);
    }

    static void Sphere(Transform parent, string name, Vector3 center, float diameter, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = new Vector3(diameter, diameter, diameter);
        Paint(go, color);
    }

    // Display-side start used by the i7 smoke/evidence run; the player's own
    // scene load starts AlphaDirector normally, so this only exists for an
    // editor-driven check.
    public static void PlayAlpha()
    {
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.EnterPlaymode();
    }
}
