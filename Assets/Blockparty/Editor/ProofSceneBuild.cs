using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// PROOF-SCENE editor entry points. Runs headless via -executeMethod:
//   Unity -batchmode -projectPath . -executeMethod ProofSceneBuild.BuildScene -quit
//   DISPLAY=:0 Unity -projectPath . -executeMethod ProofSceneBuild.PlayProof -logFile <log>
public static class ProofSceneBuild
{
    const string BaseGltf = "Assets/Blockparty/Art/Characters/Base/base.gltf";
    const string RigGltf = "Assets/Blockparty/Art/Rigs/humanoid.gltf";
    const string ProofGltf = "Assets/Blockparty/Art/Animations/proof.gltf";
    public const string ScenePath = "Assets/Blockparty/Scenes/PhysicalProof.unity";

    // Reports what the pinned importer produced for each package artifact.
    public static void ProbeImports()
    {
        var ok = true;
        ok &= Report(BaseGltf);
        ok &= Report(RigGltf);
        ok &= Report(ProofGltf);
        Debug.Log("PROOF_PROBE result=" + (ok ? "Ok" : "MissingAssets"));
        if (!ok)
            throw new System.Exception("PROOF_PROBE_FAILED");
    }

    static bool Report(string path)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(path);
        var groups = new Dictionary<string, List<string>>();
        foreach (var a in assets)
        {
            if (a == null || a.name == "GltfImporter")
                continue;
            var t = a.GetType().Name;
            if (!groups.TryGetValue(t, out var list))
            {
                list = new List<string>();
                groups[t] = list;
            }
            list.Add(a.name);
        }
        foreach (var kv in groups.OrderBy(kv => kv.Key))
            Debug.Log($"PROOF_PROBE path={path} type={kv.Key} count={kv.Value.Count} names={string.Join(",", kv.Value.Take(12))}");
        var main = AssetDatabase.LoadMainAssetAtPath(path);
        Debug.Log($"PROOF_PROBE path={path} main={(main == null ? "<null>" : main.GetType().Name + ":" + main.name)}");
        // Animation clips must carry no events: callbacks write no state.
        foreach (var a in assets)
        {
            var clip = a as AnimationClip;
            if (clip != null)
                Debug.Log($"PROOF_PROBE path={path} clip={clip.name} length={clip.length} events={clip.events.Length} humanoid={clip.humanMotion}");
        }
        return main != null;
    }

    // Dumps import hierarchies plus one clip's binding paths.
    public static void DumpHierarchies()
    {
        DumpTree(AssetDatabase.LoadMainAssetAtPath(BaseGltf) as GameObject, "base");
        DumpTree(AssetDatabase.LoadMainAssetAtPath(ProofGltf) as GameObject, "proof");
        var clip = Clip("Walk_Loop");
        Debug.Log("PROOF_DUMP clip=" + clip.name + " legacy=" + clip.legacy);
        int shown = 0;
        foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
        {
            if (shown++ < 8)
                Debug.Log("PROOF_DUMP binding path=" + binding.path + " prop=" + binding.propertyName);
        }
    }

    static void DumpTree(GameObject root, string label)
    {
        if (root == null)
        {
            Debug.Log("PROOF_DUMP " + label + " NULL");
            return;
        }
        DumpNode(root.transform, label, 0);
    }

    static void DumpNode(Transform node, string label, int depth)
    {
        if (depth <= 3)
            Debug.Log("PROOF_DUMP " + label + " depth=" + depth + " name=" + node.name);
        if (depth < 3)
        {
            for (int i = 0; i < node.childCount; i++)
                DumpNode(node.GetChild(i), label, depth + 1);
        }
    }

    static int verifyFailures;

    static void VCheck(bool condition, string name)
    {
        Debug.Log("PROOF_VERIFY " + (condition ? "PASS " : "FAIL ") + name);
        if (!condition)
            verifyFailures++;
    }

    // Scene-graph half of the PROOF-SCENE checks (the sim half runs
    // standalone from tests/production/PROOF-SCENE.cs). Throws on failure.
    public static void VerifyScene()
    {
        verifyFailures = 0;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        VCheck(scene.IsValid(), "scene-opens");
        var go = GameObject.Find("ProofDirector");
        VCheck(go != null, "director-present");
        var director = go.GetComponent<ProofSceneDirector>();
        VCheck(director.residentRoot != null, "wire-residentRoot");
        VCheck(director.residentAnim != null, "wire-residentAnim");
        VCheck(director.idleClip != null && director.idleClip.name == "Idle_Loop", "wire-idle");
        VCheck(director.walkClip != null && director.walkClip.name == "Walk_Loop", "wire-walk");
        VCheck(director.sitEnterClip != null && director.sitEnterClip.name == "Sitting_Enter", "wire-sit-enter");
        VCheck(director.sitIdleClip != null && director.sitIdleClip.name == "Sitting_Idle_Loop", "wire-sit-idle");
        VCheck(director.sitExitClip != null && director.sitExitClip.name == "Sitting_Exit", "wire-sit-exit");
        VCheck(director.chairFocus != null, "wire-chairFocus");
        VCheck(director.proofCamera != null, "wire-proofCamera");

        int clips = 0;
        bool noEvents = true;
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ProofGltf))
        {
            var clip = asset as AnimationClip;
            if (clip == null)
                continue;
            clips++;
            noEvents &= clip.events.Length == 0;
        }
        VCheck(clips == 7, "seven-clips");
        VCheck(noEvents, "clips-carry-no-events");

        var skins = director.residentRoot.GetComponentsInChildren<SkinnedMeshRenderer>();
        int materials = 0;
        foreach (var skin in skins)
            materials += skin.sharedMaterials.Length;
        VCheck(skins.Length == 3, "skinned-meshes-present");
        VCheck(materials == 3, "three-material-slots");
        VCheck(director.residentRoot.Find("Armature/root/pelvis") != null, "clip-paths-resolve");

        VCheck(ProofSceneLayout.Start.X == -3f && ProofSceneLayout.Start.Z == -3f, "layout-start");
        VCheck(ProofSceneLayout.Waypoint.X == 3.6f && ProofSceneLayout.Waypoint.Z == -0.4f, "layout-waypoint");
        VCheck(ProofSceneLayout.Approach.X == 0f && ProofSceneLayout.Approach.Z == 2.6f, "layout-approach");
        Vector2 s = new Vector2(ProofSceneLayout.Start.X, ProofSceneLayout.Start.Z);
        Vector2 w = new Vector2(ProofSceneLayout.Waypoint.X, ProofSceneLayout.Waypoint.Z);
        Vector2 a = new Vector2(ProofSceneLayout.Approach.X, ProofSceneLayout.Approach.Z);
        Vector2 half = new Vector2(ProofSceneLayout.ObstacleSize.x / 2f, ProofSceneLayout.ObstacleSize.z / 2f);
        Vector2 c = new Vector2(ProofSceneLayout.ObstacleCenter.x, ProofSceneLayout.ObstacleCenter.z);
        VCheck(Clearance(s, w, c - half, c + half) >= ProofSceneLayout.MinLegClearanceM, "leg1-clears-obstacle");
        VCheck(Clearance(w, a, c - half, c + half) >= ProofSceneLayout.MinLegClearanceM, "leg2-clears-obstacle");
        Vector3 seatMin = ProofSceneLayout.ChairSeatCenter - ProofSceneLayout.ChairSeatSize / 2f;
        Vector3 seatMax = ProofSceneLayout.ChairSeatCenter + ProofSceneLayout.ChairSeatSize / 2f;
        VCheck(ProofSceneLayout.Approach.X >= seatMin.x - 0.1f && ProofSceneLayout.Approach.X <= seatMax.x + 0.1f
            && ProofSceneLayout.Approach.Z >= seatMin.z - 0.2f && ProofSceneLayout.Approach.Z <= seatMax.z + 0.2f,
            "approach-over-seat");
        VCheck(Mathf.Abs(seatMax.y - 0.45f) < 0.001f, "seat-top-height");

        Debug.Log("PROOF_VERIFY failures=" + verifyFailures);
        if (verifyFailures > 0)
            throw new System.Exception("PROOF_VERIFY_FAILED");
    }

    static float Clearance(Vector2 p0, Vector2 p1, Vector2 bmin, Vector2 bmax)
    {
        float best = float.MaxValue;
        for (int i = 0; i <= 400; i++)
        {
            float t = i / 400f;
            float x = p0.x + (p1.x - p0.x) * t;
            float z = p0.y + (p1.y - p0.y) * t;
            float dx = Mathf.Max(bmin.x - x, 0f, x - bmax.x);
            float dz = Mathf.Max(bmin.y - z, 0f, z - bmax.y);
            best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
        }
        return best;
    }

    // Assembles PhysicalProof.unity from scratch (idempotent): ground,
    // obstacle, waypoint marker, chair, light, camera, one resident
    // (MESH-FIRST base + RIG-FIRST-shared skeleton, default MATERIAL-FIRST
    // presets) and the sim-driven director.
    public static void BuildScene()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Blockparty/Scenes"))
            AssetDatabase.CreateFolder("Assets/Blockparty", "Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(2.4f, 1f, 2.4f);
        Paint(ground, new Color(0.62f, 0.62f, 0.60f));

        var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obstacle.name = "Obstacle";
        obstacle.transform.position = ProofSceneLayout.ObstacleCenter;
        obstacle.transform.localScale = ProofSceneLayout.ObstacleSize;
        Paint(obstacle, new Color(0.45f, 0.16f, 0.13f));

        var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = "WaypointMarker";
        marker.transform.position = new Vector3(ProofSceneLayout.Waypoint.X, 0.01f, ProofSceneLayout.Waypoint.Z);
        marker.transform.localScale = new Vector3(0.5f, 0.04f, 0.5f);
        Paint(marker, new Color(0.85f, 0.70f, 0.20f));

        var chair = new GameObject("Chair");
        var seat = Box(chair, "Seat", ProofSceneLayout.ChairSeatCenter, ProofSceneLayout.ChairSeatSize, new Color(0.42f, 0.28f, 0.16f));
        Box(chair, "Back", ProofSceneLayout.ChairBackCenter, ProofSceneLayout.ChairBackSize, new Color(0.42f, 0.28f, 0.16f));
        for (int i = 0; i < 4; i++)
        {
            float lx = (i % 2 == 0 ? -0.22f : 0.22f);
            float lz = 2.75f + (i < 2 ? -0.22f : 0.22f);
            Box(chair, "Leg" + i, new Vector3(lx, 0.19f, lz), new Vector3(0.06f, 0.38f, 0.06f), new Color(0.35f, 0.23f, 0.13f));
        }

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var camera = new GameObject("ProofCamera").AddComponent<Camera>();
        camera.transform.position = ProofSceneLayout.CameraPosition;
        camera.transform.LookAt(ProofSceneLayout.CameraLookAt);
        camera.fieldOfView = 55f;
        camera.tag = "MainCamera";

        var prefab = AssetDatabase.LoadMainAssetAtPath(BaseGltf) as GameObject;
        if (prefab == null)
            throw new System.Exception("PROOF_BUILD missing resident prefab at " + BaseGltf);
        // The importer hoists base.gltf's joints (no Armature node) while the
        // proof.gltf clips bind under Armature/root/..., so the instance is
        // renamed and nested to match the clip paths. No asset is modified.
        var mover = new GameObject("Resident");
        mover.transform.position = new Vector3(ProofSceneLayout.Start.X, 0f, ProofSceneLayout.Start.Z);
        SceneManager.MoveGameObjectToScene(mover, scene);
        var resident = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        resident.name = "Armature";
        resident.transform.SetParent(mover.transform, false);
        var anim = mover.AddComponent<Animation>();

        var directorObject = new GameObject("ProofDirector");
        var director = directorObject.AddComponent<ProofSceneDirector>();
        director.residentRoot = mover.transform;
        director.residentAnim = anim;
        director.idleClip = Clip("Idle_Loop");
        director.walkClip = Clip("Walk_Loop");
        director.sitEnterClip = Clip("Sitting_Enter");
        director.sitIdleClip = Clip("Sitting_Idle_Loop");
        director.sitExitClip = Clip("Sitting_Exit");
        director.chairFocus = seat.transform;
        director.proofCamera = camera;
        director.meshForwardOffsetDeg = 0f;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("PROOF_BUILD saved=" + ScenePath);
    }

    static AnimationClip Clip(string name)
    {
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ProofGltf))
        {
            var clip = asset as AnimationClip;
            if (clip != null && clip.name == name)
                return clip;
        }
        throw new System.Exception("PROOF_BUILD missing clip " + name);
    }

    static void Paint(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        // URP project: props use the URP Lit shader (Standard fallback keeps
        // the method working if the pipeline asset is ever unassigned).
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Legacy Shaders/Diffuse");
        var material = new Material(shader);
        material.color = color;
        renderer.sharedMaterial = material;
    }

    static GameObject Box(GameObject parent, string name, Vector3 center, Vector3 size, Color color)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = center;
        box.transform.localScale = size;
        Paint(box, color);
        return box;
    }

    const string RunKey = "ProofSceneBuild.RunActive";

    [InitializeOnLoadMethod]
    static void ResubscribeAfterReload()
    {
        if (SessionState.GetBool(RunKey, false))
        {
            EditorApplication.update += PollRun;
            EditorApplication.playModeStateChanged += OnRunState;
        }
    }

    // Opens the proof scene, runs it in Play mode until the director
    // reports done, then exits the editor with 0 (pass) or 1 (fail).
    // Needs a display for the Game-view captures; no -quit flag here.
    public static void PlayProof()
    {
        StartPass(1, 1);
    }

    // Same run twice in one editor session: proves repeated Play entry
    // carries no static-state contamination (Fast Enter Play Mode rule).
    // Pass 2 captures go to PROOF_SHOT_DIR_PASS2 when set, else /tmp.
    public static void PlayProofTwice()
    {
        StartPass(1, 2);
    }

    static void StartPass(int pass, int passes)
    {
        bool noDomainReload = EditorSettings.enterPlayModeOptionsEnabled
            && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload;
        if (!noDomainReload) throw new System.Exception("PROOF requires domain reload off and scene reload on");
        Debug.Log("PROOF_MODE domainReload=False sceneReload=True");
        Debug.Log("PROOF_RUN start pass=" + pass + "/" + passes);
        if (pass > 1)
        {
            string dir = System.Environment.GetEnvironmentVariable("PROOF_SHOT_DIR_PASS2");
            System.Environment.SetEnvironmentVariable("PROOF_SHOT_DIR",
                string.IsNullOrEmpty(dir) ? "/tmp/proof-pass2" : dir);
        }
        EditorSceneManager.OpenScene(ScenePath);
        SessionState.SetBool(RunKey, true);
        EditorApplication.update -= PollRun;
        EditorApplication.playModeStateChanged -= OnRunState;
        SessionState.SetInt(RunKey + ".Pass", pass);
        SessionState.SetInt(RunKey + ".Passes", passes);
        SessionState.SetFloat(RunKey + ".Start", (float)EditorApplication.timeSinceStartup);
        EditorApplication.update += PollRun;
        EditorApplication.playModeStateChanged += OnRunState;
        EditorApplication.EnterPlaymode();
    }

    static void PollRun()
    {
        if (!EditorApplication.isPlaying)
            return;
        var go = GameObject.Find("ProofDirector");
        var director = go == null ? null : go.GetComponent<ProofSceneDirector>();
        if (director != null && director.done)
        {
            SessionState.SetBool(RunKey + ".Failed", director.failed);
            EditorApplication.update -= PollRun;
            EditorApplication.ExitPlaymode();
            return;
        }
        if (EditorApplication.timeSinceStartup - SessionState.GetFloat(RunKey + ".Start", 0f) > 300.0)
        {
            Debug.Log("PROOF_RUN runner timeout");
            SessionState.SetBool(RunKey + ".Failed", true);
            EditorApplication.update -= PollRun;
            EditorApplication.ExitPlaymode();
        }
    }

    static void OnRunState(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
            return;
        bool failed = SessionState.GetBool(RunKey + ".Failed", true);
        int pass = SessionState.GetInt(RunKey + ".Pass", 1);
        int passes = SessionState.GetInt(RunKey + ".Passes", 1);
        Debug.Log("PROOF_RUN exited pass=" + pass + " failed=" + failed);
        if (!failed && pass < passes)
        {
            EditorApplication.playModeStateChanged -= OnRunState;
            StartPass(pass + 1, passes);
            return;
        }
        SessionState.EraseBool(RunKey);
        SessionState.EraseBool(RunKey + ".Failed");
        SessionState.EraseInt(RunKey + ".Pass");
        SessionState.EraseInt(RunKey + ".Passes");
        SessionState.EraseFloat(RunKey + ".Start");
        EditorApplication.playModeStateChanged -= OnRunState;
        EditorApplication.Exit(failed ? 1 : 0);
    }
}
