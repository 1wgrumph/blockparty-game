using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// GREYBOX editor entry points. Runs headless via -executeMethod:
//   Unity -batchmode -projectPath . -executeMethod GreyboxSceneBuild.BuildScene -quit
//   DISPLAY=:0 Unity -projectPath . -executeMethod GreyboxSceneBuild.PlayGreybox -logFile <log>
public static class GreyboxSceneBuild
{
    public const string ScenePath = "Assets/Blockparty/Scenes/EightResident.unity";

    // Assembles EightResident.unity from scratch (idempotent): ground, two
    // greybox homes with facing door walls plus a connecting path, eight
    // station markers, eight animated resident roots, camera, sun, and the
    // sim-driven director wired to the roots, stations and camera.
    public static void BuildScene()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Blockparty/Scenes"))
            AssetDatabase.CreateFolder("Assets/Blockparty", "Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("GreyboxWorld");
        SceneManager.MoveGameObjectToScene(world, scene);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.SetParent(world.transform, false);
        ground.transform.position = new Vector3(0f, -0.15f, 0f);
        ground.transform.localScale = new Vector3(60f, 0.3f, 40f);
        Paint(ground, new Color(0.52f, 0.55f, 0.52f));

        var homes = new GameObject("Homes");
        homes.transform.SetParent(world.transform, false);
        BuildHome(homes.transform, "HomeA", -8f);
        BuildHome(homes.transform, "HomeB", 8f);
        var path = GameObject.CreatePrimitive(PrimitiveType.Cube);
        path.name = "Path";
        path.transform.SetParent(homes.transform, false);
        path.transform.position = new Vector3(0f, 0.05f, 0f);
        path.transform.localScale = new Vector3(6.4f, 0.1f, 2f);
        Paint(path, new Color(0.42f, 0.42f, 0.40f));

        var residents = new GameObject("Residents");
        residents.transform.SetParent(world.transform, false);
        var roots = new Transform[8];
        for (int i = 0; i < 8; i++)
        {
            float x = i < 4 ? -11f + i * 2f : 5f + (i - 4) * 2f;
            var root = new GameObject("Resident_" + (i + 1));
            root.transform.SetParent(residents.transform, false);
            root.transform.position = new Vector3(x, 0f, -1f);
            root.AddComponent<Animation>();
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.position = new Vector3(x, 1f, -1f);
            Paint(body, i < 4 ? new Color(0.55f, 0.72f, 0.88f) : new Color(0.58f, 0.85f, 0.62f));
            roots[i] = root.transform;
        }

        var stations = new GameObject("Stations");
        stations.transform.SetParent(world.transform, false);
        var markers = new Transform[8];
        for (int i = 0; i < 8; i++)
        {
            float x = i < 4 ? -10f + i * 1.4f : 6f + (i - 4) * 1.4f;
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = i < 4 ? "StationA" + (i + 1) : "StationB" + (i - 3);
            marker.transform.SetParent(stations.transform, false);
            marker.transform.position = new Vector3(x, 0.25f, -2.5f);
            marker.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            Paint(marker, new Color(0.85f, 0.70f, 0.20f));
            markers[i] = marker.transform;
        }

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(world.transform, false);
        sun.type = LightType.Directional;
        sun.intensity = 1f;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var camera = new GameObject("GreyboxCamera").AddComponent<Camera>();
        camera.transform.SetParent(world.transform, false);
        camera.transform.position = new Vector3(0f, 16f, -16f);
        camera.transform.LookAt(new Vector3(0f, 0f, 2f));
        camera.fieldOfView = 60f;
        camera.tag = "MainCamera";

        var director = world.AddComponent<GreyboxDirector>();
        director.residentRoots = roots;
        director.stations = markers;
        director.greyboxCamera = camera;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("GREYBOX_BUILD saved=" + ScenePath);
    }

    static void BuildHome(Transform homes, string name, float cx)
    {
        var home = new GameObject(name);
        home.transform.SetParent(homes, false);
        var wall = new Color(0.78f, 0.78f, 0.76f);
        Box(home, "Floor", new Vector3(cx, -0.1f, 0f), new Vector3(10.6f, 0.2f, 8.6f), new Color(0.60f, 0.60f, 0.58f));
        Box(home, "WallN", new Vector3(cx, 1.3f, 4.15f), new Vector3(10.6f, 2.6f, 0.3f), wall);
        Box(home, "WallS", new Vector3(cx, 1.3f, -4.15f), new Vector3(10.6f, 2.6f, 0.3f), wall);
        float outer = cx < 0f ? cx - 5.15f : cx + 5.15f;
        float door = cx < 0f ? cx + 5.15f : cx - 5.15f;
        Box(home, "WallSolid", new Vector3(outer, 1.3f, 0f), new Vector3(0.3f, 2.6f, 8.6f), wall);
        Box(home, "WallDoorA", new Vector3(door, 1.3f, 2.55f), new Vector3(0.3f, 2.6f, 3.5f), wall);
        Box(home, "WallDoorB", new Vector3(door, 1.3f, -2.55f), new Vector3(0.3f, 2.6f, 3.5f), wall);
        Box(home, "Lintel", new Vector3(door, 2.3f, 0f), new Vector3(0.3f, 0.6f, 1.6f), wall);
        Box(home, "Table", new Vector3(cx, 0.75f, 2f), new Vector3(1.6f, 0.1f, 1f), new Color(0.42f, 0.28f, 0.16f));
        Box(home, "TableBase", new Vector3(cx, 0.35f, 2f), new Vector3(0.3f, 0.7f, 0.3f), new Color(0.35f, 0.23f, 0.13f));
        Box(home, "Stove", new Vector3(cx - 3f, 0.45f, -2.5f), new Vector3(0.8f, 0.9f, 0.8f), new Color(0.30f, 0.30f, 0.32f));
    }

    static void Paint(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Legacy Shaders/Diffuse");
        var material = new Material(shader);
        material.color = color;
        renderer.sharedMaterial = material;
    }

    static void Box(GameObject parent, string name, Vector3 center, Vector3 size, Color color)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent.transform, false);
        box.transform.position = center;
        box.transform.localScale = size;
        Paint(box, color);
    }

    static int verifyFailures;

    static void VCheck(bool condition, string name)
    {
        Debug.Log("GREYBOX_VERIFY " + (condition ? "PASS " : "FAIL ") + name);
        if (!condition)
            verifyFailures++;
    }

    // Scene-graph half of the GREYBOX checks (the sim half runs standalone
    // from tests/production/GREYBOX.cs). Throws on failure.
    public static void VerifyScene()
    {
        verifyFailures = 0;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        VCheck(scene.IsValid(), "scene-opens");
        var go = GameObject.Find("GreyboxWorld");
        VCheck(go != null, "world-present");
        var director = go == null ? null : go.GetComponent<GreyboxDirector>();
        VCheck(director != null, "director-present");
        VCheck(director != null && director.residentRoots != null && director.residentRoots.Length == 8, "wire-roots");
        VCheck(director != null && director.stations != null && director.stations.Length == 8, "wire-stations");
        VCheck(director != null && director.greyboxCamera != null, "wire-camera");
        var animatedRoots = new HashSet<Transform>();
        if (go != null)
        {
            foreach (Animation animation in go.GetComponentsInChildren<Animation>())
                if (animation.GetComponentInChildren<Renderer>() != null) animatedRoots.Add(animation.transform);
            foreach (Animator animator in go.GetComponentsInChildren<Animator>())
                if (animator.GetComponentInChildren<Renderer>() != null) animatedRoots.Add(animator.transform);
        }
        VCheck(animatedRoots.Count == 8, "eight-animated-rendered-roots");
        VCheck(GameObject.Find("HomeA") != null && GameObject.Find("HomeB") != null, "both-homes");
        VCheck(GameObject.Find("Path") != null, "connecting-path");
        int stations = 0;
        for (int i = 0; i < 8; i++)
        {
            string name = i < 4 ? "StationA" + (i + 1) : "StationB" + (i - 3);
            if (GameObject.Find(name) != null) stations++;
        }
        VCheck(stations == 8, "eight-stations");
        Debug.Log("GREYBOX_VERIFY failures=" + verifyFailures);
        if (verifyFailures > 0)
            throw new System.Exception("GREYBOX_VERIFY_FAILED");
    }

    const string RunKey = "GreyboxSceneBuild.RunActive";

    [InitializeOnLoadMethod]
    static void ResubscribeAfterReload()
    {
        if (SessionState.GetBool(RunKey, false))
        {
            EditorApplication.update += PollRun;
            EditorApplication.playModeStateChanged += OnRunState;
        }
    }

    // Opens the greybox scene, runs it in Play mode until the director
    // reports done, then exits the editor with 0 (pass) or 1 (fail).
    // Needs a display for the Game-view captures; no -quit flag here.
    public static void PlayGreybox()
    {
        bool noDomainReload = EditorSettings.enterPlayModeOptionsEnabled
            && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload;
        if (!noDomainReload) throw new System.Exception("GREYBOX requires domain reload off and scene reload on");
        Debug.Log("GREYBOX_MODE domainReload=False sceneReload=True");
        EditorSceneManager.OpenScene(ScenePath);
        SessionState.SetBool(RunKey, true);
        EditorApplication.update -= PollRun;
        EditorApplication.playModeStateChanged -= OnRunState;
        SessionState.SetFloat(RunKey + ".Start", (float)EditorApplication.timeSinceStartup);
        EditorApplication.update += PollRun;
        EditorApplication.playModeStateChanged += OnRunState;
        EditorApplication.EnterPlaymode();
    }

    static void PollRun()
    {
        if (!EditorApplication.isPlaying)
            return;
        var go = GameObject.Find("GreyboxWorld");
        var director = go == null ? null : go.GetComponent<GreyboxDirector>();
        if (director != null && director.done)
        {
            SessionState.SetBool(RunKey + ".Failed", director.failed);
            EditorApplication.update -= PollRun;
            EditorApplication.ExitPlaymode();
            return;
        }
        if (EditorApplication.timeSinceStartup - SessionState.GetFloat(RunKey + ".Start", 0f) > 300.0)
        {
            Debug.Log("GREYBOX_RUN runner timeout");
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
        Debug.Log("GREYBOX_RUN exited failed=" + failed);
        SessionState.EraseBool(RunKey);
        SessionState.EraseBool(RunKey + ".Failed");
        SessionState.EraseFloat(RunKey + ".Start");
        EditorApplication.playModeStateChanged -= OnRunState;
        EditorApplication.Exit(failed ? 1 : 0);
    }
}
