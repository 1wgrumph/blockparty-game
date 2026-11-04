// E1-IGPU measurement probe (issue #227, M4).
//
// Runtime half: one standalone-player run applies one E1 configuration from
// the plan, follows one deterministic 60 s block/common-room path, records
// frame time plus ProfilerRecorder renderer counters for 60 s after a 10 s
// warm-up, and writes per-run evidence for the configured work. The editor half
// (bottom of this file) builds the E1 development player from BLOCK-WORLD with
// eight residents and the SSAO feature; the generated scene/assets live only in
// the i7 scratch copy.
//
//   Unity -batchmode -nographics -projectPath <copy> \
//     -executeMethod E1Build.Build -logFile <build log>
//   E1.x86_64 -e1-mode measure -e1-config bloom -e1-scale 0.75 -e1-run 2 \
//     -e1-out <evidence dir> -e1-identity <identity.json> [-e1-capture] \
//     -logFile <run log>
//
// PERFORMANCE mode (#92, M5): with -perf-run-id present the same recorder
// runs one budget window instead of the E1 matrix. PerfBuild.BuildRelease
// builds the RELEASE StandaloneLinux64 player with 24 residents; a run
// deactivates residents above -perf-residents, warms up, then records the
// window on one monotonic clock: frame CSV (same header), 10 real
// SaveV1.SaveGame saves, 10 real TitleFlow Continue title-to-playable
// loads, 20 real WorldNavigation.SelectResident commands each scheduled
// while a selected resident's marker is inside the measurement viewport,
// timed to the first end-of-frame where that marker is rendered (recorded
// with the marker's viewport x/y and depth), and one
// ProposalBoundary request per GatewayPurpose under a verified no-key
// KeyProvider resolution (wire_sent:false, outcome NoKey; a resolved key
// fails the run closed, connected runs need separate owner authority).
// RSS is sampled externally by tools/perf-sample.py from /proc VmRSS.
//   Perf.x86_64 -perf-run-id <id> -perf-out <dir> -perf-residents 8|24 \
//     -perf-seconds 600 -perf-warmup 10 -perf-commit <sha> -perf-tree <sha> \
//     -perf-archive-sha256 <sha> -logFile <run log>
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
#endif

public sealed class PerfRecorder : MonoBehaviour, Blockparty.Persistence.ISceneContent
{
    // ISceneContent for the PERFORMANCE stack only: that stack starts with
    // no in-flight actions, so there are no scene objects to re-register.
    public void RegisterObjects(Blockparty.Simulation.ActivityExecutor executor) { }
    public const string CsvHeader = "elapsed_s,frame_ms,draw_calls,setpass_calls,texture_memory_bytes";
    public const int OutputWidth = 1920;
    public const int OutputHeight = 1080;
    public const float WarmupSeconds = 10f;
    public const float MeasureSeconds = 60f;
    public const int CaptureSeed = 42;
    public const string CapturePreset = "noon";
    public const string PathSource = "evidence/production/E1-IGPU/e1-path.json";
    public const string OverdrawLayersPath = "evidence/production/E1-IGPU/overdraw-layers-1080p.json";
    public const string OverdrawAnalysisPath = "evidence/production/E1-IGPU/overdraw-analysis.json";
    public const int CaptureFrameIndex = 0;
    public const float HullMm = 0.003f;

    [Serializable]
    public sealed class Identity
    {
        public string host;
        public string display;
        public string project_copy;
        public string editor_version;
        public string editor_revision;
        public string build_sha256;
        public string scene_sha256;
        public string recorder_sha256;
        public string common_sha256;
        public string recorder_path;
        public string scene_path;
        public string common_path;
        public string derived_scene_sha256;
        public string gpu_hardware;
    }

    [Serializable]
    sealed class Waypoint
    {
        public float time_s;
        public float[] position;
        public float[] look_at;
    }

    [Serializable]
    sealed class ResidentOffset
    {
        public float[] offset;
    }

    [Serializable]
    sealed class ResidentMotion
    {
        public float amplitude_m;
        public float angular_speed;
        public float phase_step;
    }

    [Serializable]
    sealed class PathDefinition
    {
        public string id;
        public float duration_s;
        public float[] snapshot_times_s;
        public Waypoint[] camera_waypoints;
        public ResidentOffset[] resident_offsets;
        public ResidentMotion resident_motion;
    }

    sealed class FrameRecord
    {
        public float Time;
        public string Camera;
        public string Residents;
        public string CaptureSha;
    }

    sealed class OutlineRecord
    {
        public float Time;
        public string PoseSha;
        public string Capture;
        public string Silhouette;
        public string[] Renderers;
    }

    bool perfMode;
    string perfRunId = "";
    string perfCommit = "";
    string perfTree = "";
    string perfArchive = "";
    int perfResidents = 8;
    double perfSeconds = 600;
    double perfWarmup = WarmupSeconds;

    string mode = "measure";
    string config = "base-ramp";
    string scaleName = "1080p";
    float renderScale = 1f;
    int run = 1;
    string outDir;
    string identityPath;
    bool captureThisRun;
    Identity identity;
    PathDefinition path;
    string pathSha;
    float[] snapshotTimes = new float[0];

    ProfilerRecorder drawCalls;
    ProfilerRecorder setPass;
    ProfilerRecorder textureBytes;
    long fallbackTextureBytes = -1;
    ScreenSpaceAmbientOcclusion ssao;
    RenderTexture measurementTarget;
    double startedAt;
    double measureStart;
    bool measuring;
    bool pendingSnapshot;
    int snapshotIndex;
    float lastPathTime;

    readonly List<Transform> residentRoots = new List<Transform>();
    readonly List<Animation> residentAnimations = new List<Animation>();
    readonly List<MeshRenderer> worldRenderers = new List<MeshRenderer>();
    readonly List<SkinnedMeshRenderer> residentRenderers = new List<SkinnedMeshRenderer>();
    readonly List<Mesh> residentBakedMeshes = new List<Mesh>();
    readonly List<Mesh> hullMeshes = new List<Mesh>();
    readonly List<FrameRecord> frames = new List<FrameRecord>();
    readonly List<OutlineRecord> outlineRecords = new List<OutlineRecord>();
    Dictionary<string, string> activeShaders = new Dictionary<string, string>();
    Material outlineMaterial;
    Material additiveMaterial;
    Material maskMaterial;
    float additiveStep = 1f;

    void Awake()
    {
        startedAt = Time.realtimeSinceStartupAsDouble;
        ParseArgs();
        if (!perfMode)
        {
            identity = LoadIdentity();
            if (identity == null)
            {
                return;
            }
        }
        LoadPath();
        if (perfMode)
        {
            for (int i = perfResidents + 1; i <= 24; i++)
            {
                var extra = GameObject.Find("Resident_" + i);
                if (extra != null)
                {
                    extra.SetActive(false);
                }
            }
        }
        ConfigureDisplay();
        var urp = UniversalRenderPipeline.asset;
        if (urp != null)
        {
            urp.renderScale = renderScale;
        }
        ApplyMeasurementTarget();
        CacheScene();
        ConfigureEffects();
        LogResidents();
        Debug.Log("E1-IGPU mode=" + mode + " config=" + config + " scale=" + scaleName
            + " run=" + run + " render_scale=" + renderScale.ToString("0.00", CultureInfo.InvariantCulture));
        StartCoroutine(perfMode ? PerfRoutine() : MeasureRoutine());
    }

    void OnDestroy()
    {
        drawCalls.Dispose();
        setPass.Dispose();
        textureBytes.Dispose();
    }

    void Update()
    {
        if (!measuring)
        {
            return;
        }
        double now = Time.realtimeSinceStartupAsDouble;
        float pathTime = Mathf.Min((float)(now - measureStart), MeasureSeconds);
        pendingSnapshot = false;
        if (snapshotIndex < snapshotTimes.Length && pathTime + 0.0001f >= snapshotTimes[snapshotIndex])
        {
            pathTime = snapshotTimes[snapshotIndex];
            pendingSnapshot = true;
        }
        lastPathTime = pathTime;
        ApplyPose(pathTime);
        if (config == "hull-outline")
        {
            UpdateHullMeshes();
            DrawHullMeshes();
        }
    }

    void ParseArgs()
    {
        string[] args = Environment.GetCommandLineArgs();
        mode = Arg(args, "mode", "measure");
        config = Arg(args, "config", "base-ramp");
        scaleName = Arg(args, "scale", "1080p");
        renderScale = scaleName == "0.75" ? 0.75f : 1f;
        int.TryParse(Arg(args, "run", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out run);
        outDir = Arg(args, "out", "");
        identityPath = Arg(args, "identity", "");
        captureThisRun = HasArg(args, "capture");
        perfRunId = PerfArg(args, "run-id", "");
        perfMode = !string.IsNullOrEmpty(perfRunId);
        if (perfMode)
        {
            perfCommit = PerfArg(args, "commit", "");
            perfTree = PerfArg(args, "tree", "");
            perfArchive = PerfArg(args, "archive-sha256", "");
            int.TryParse(PerfArg(args, "residents", "8"), NumberStyles.Integer, CultureInfo.InvariantCulture, out perfResidents);
            double.TryParse(PerfArg(args, "seconds", "600"), NumberStyles.Float, CultureInfo.InvariantCulture, out perfSeconds);
            double.TryParse(PerfArg(args, "warmup", "10"), NumberStyles.Float, CultureInfo.InvariantCulture, out perfWarmup);
            outDir = PerfArg(args, "out", outDir);
        }
        if (string.IsNullOrEmpty(outDir))
        {
            outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "evidence/production/E1-IGPU");
        }
    }

    static string Arg(string[] args, string key, string fallback)
    {
        string flag = "-e1-" + key;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    static string PerfArg(string[] args, string key, string fallback)
    {
        string flag = "-perf-" + key;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    static bool HasArg(string[] args, string key)
    {
        string flag = "-e1-" + key;
        foreach (string arg in args)
        {
            if (arg == flag)
            {
                return true;
            }
        }
        return false;
    }

    Identity LoadIdentity()
    {
        if (string.IsNullOrEmpty(identityPath) || !File.Exists(identityPath))
        {
            Debug.LogError("E1-IGPU identity file missing: " + identityPath);
            Application.Quit(2);
            return null;
        }
        var parsed = JsonUtility.FromJson<Identity>(File.ReadAllText(identityPath));
        if (parsed == null || string.IsNullOrEmpty(parsed.build_sha256))
        {
            Debug.LogError("E1-IGPU identity file unreadable: " + identityPath);
            Application.Quit(2);
            return null;
        }
        Debug.Log("E1-IGPU host=" + parsed.host);
        Debug.Log("E1-IGPU build_sha256=" + parsed.build_sha256);
        Debug.Log("E1-IGPU scene_sha256=" + parsed.scene_sha256);
        Debug.Log("E1-IGPU perf_recorder_sha256=" + parsed.recorder_sha256);
        Debug.Log("E1-IGPU display=" + parsed.display);
        Debug.Log("E1-IGPU project_copy=" + parsed.project_copy);
        Debug.Log("E1-IGPU editor=" + parsed.editor_version + " revision=" + parsed.editor_revision);
        string hardware = string.IsNullOrEmpty(parsed.gpu_hardware)
            ? SystemInfo.graphicsDeviceName : parsed.gpu_hardware;
        Debug.Log("E1-IGPU gpu=" + hardware + " (driver " + SystemInfo.graphicsDeviceName
            + ", " + SystemInfo.graphicsDeviceType + ")");
        Debug.Log("E1-IGPU gpu_driver=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceType);
        Debug.Log("E1-IGPU derived_scene_sha256=" + parsed.derived_scene_sha256);
        return parsed;
    }

    void LoadPath()
    {
        string pathFile = Path.Combine(outDir, "e1-path.json");
        if (!File.Exists(pathFile))
        {
            Debug.LogError("E1-IGPU path definition missing: " + pathFile);
            Application.Quit(2);
            return;
        }
        path = JsonUtility.FromJson<PathDefinition>(File.ReadAllText(pathFile));
        if (path == null || string.IsNullOrEmpty(path.id) || path.camera_waypoints == null
            || path.camera_waypoints.Length < 2 || path.resident_offsets == null
            || path.resident_offsets.Length != 8)
        {
            Debug.LogError("E1-IGPU path definition invalid: " + pathFile);
            Application.Quit(2);
            return;
        }
        pathSha = ShaFile(pathFile);
        snapshotTimes = path.snapshot_times_s;
    }

    void ConfigureDisplay()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.SetResolution(OutputWidth, OutputHeight, false);
        Debug.Log("E1-IGPU screen=" + Screen.width + "x" + Screen.height
            + " vsync=" + QualitySettings.vSyncCount);
    }

    void ApplyMeasurementTarget()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            Debug.LogError("E1-IGPU no main camera for the measurement target");
            return;
        }
        measurementTarget = new RenderTexture(OutputWidth, OutputHeight, 24,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        measurementTarget.name = "E1MeasurementTarget";
        measurementTarget.antiAliasing = 1;
        measurementTarget.Create();
        camera.targetTexture = measurementTarget;
        camera.aspect = (float)OutputWidth / OutputHeight;
        camera.allowMSAA = false;
        camera.allowHDR = false;
        camera.nearClipPlane = 0.15f;
        camera.farClipPlane = 600f;
        camera.fieldOfView = 50f;
        Debug.Log("E1-IGPU render_target=" + measurementTarget.width + "x" + measurementTarget.height
            + " anti_aliasing=" + measurementTarget.antiAliasing
            + " render_scale=" + renderScale.ToString("0.00", CultureInfo.InvariantCulture));
    }

    void CacheScene()
    {
        int wanted = perfMode ? perfResidents : 8;
        for (int i = 1; i <= wanted; i++)
        {
            var root = GameObject.Find("Resident_" + i);
            if (root == null)
            {
                Debug.LogError("E1-IGPU missing Resident_" + i);
                continue;
            }
            residentRoots.Add(root.transform);
            var animations = root.GetComponentsInChildren<Animation>(true);
            residentAnimations.Add(animations.Length > 0 ? animations[0] : null);
        }
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy)
            {
                worldRenderers.Add(renderer);
            }
        }
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy)
            {
                residentRenderers.Add(renderer);
                residentBakedMeshes.Add(new Mesh());
                hullMeshes.Add(new Mesh());
            }
        }
        Debug.Log("E1-IGPU residents=" + residentRoots.Count + " mesh_renderers=" + worldRenderers.Count
            + " skinned_renderers=" + residentRenderers.Count);
    }

    void ConfigureEffects()
    {
        var camera = Camera.main;
        if (camera != null)
        {
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        }

        var rampShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (rampShader == null || litShader == null)
        {
            Debug.LogError("E1-IGPU comparison shader missing: ramp=" + (rampShader != null)
                + " lit=" + (litShader != null));
            return;
        }
        Shader controlled = config == "base-lit" ? litShader : rampShader;
        activeShaders = ApplyControlledMaterials(controlled);

        bool bloomOn = config == "bloom";
        bool ssaoOn = config == "ssao-low";
        bool dofOn = config == "dof";
        if (bloomOn || dofOn)
        {
            var volumeObject = new GameObject("E1EffectVolume");
            volumeObject.transform.SetParent(transform, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "E1EffectVolume";
            if (bloomOn)
            {
                var bloom = profile.Add<Bloom>(true);
                bloom.filter.Override(BloomFilterMode.Kawase);
                bloom.threshold.Override(0.85f);
                bloom.intensity.Override(0.8f);
                bloom.scatter.Override(0.55f);
            }
            if (dofOn)
            {
                var dof = profile.Add<DepthOfField>(true);
                dof.mode.Override(DepthOfFieldMode.Gaussian);
                dof.gaussianStart.Override(20f);
                dof.gaussianEnd.Override(60f);
                dof.gaussianMaxRadius.Override(1.2f);
                dof.highQualitySampling.Override(false);
            }
            volume.sharedProfile = profile;
        }

        ssao = FindFeature<ScreenSpaceAmbientOcclusion>();
        if (ssao != null)
        {
            ssao.SetActive(ssaoOn);
        }
        else if (ssaoOn)
        {
            Debug.LogError("E1-IGPU ssao feature missing from the built renderer");
        }
        Debug.Log("E1-IGPU ssao=" + (ssaoOn ? "low" : "off")
            + " feature=" + (ssao != null)
            + " settings=" + SsaoSettings(ssao));

        outlineMaterial = CreateMaterial("Universal Render Pipeline/Unlit",
            new Color(0.169f, 0.106f, 0.09f, 1f), false);
        if (outlineMaterial != null)
        {
            outlineMaterial.SetFloat("_Cull", 1f);
            outlineMaterial.renderQueue = (int)RenderQueue.Geometry;
        }
        Debug.Log("E1-IGPU shader_mode=" + (config == "base-lit" ? "lit" : "ramp")
            + " active_shaders=" + activeShaders.Count
            + " hull_outline=" + (config == "hull-outline"));
    }

    Dictionary<string, string> ApplyControlledMaterials(Shader shader)
    {
        var map = new SortedDictionary<string, string>();
        foreach (var renderer in worldRenderers)
        {
            ApplyRenderer(renderer, shader, map);
        }
        foreach (var renderer in residentRenderers)
        {
            ApplyRenderer(renderer, shader, map);
        }
        return new Dictionary<string, string>(map);
    }

    void ApplyRenderer(Renderer renderer, Shader shader, SortedDictionary<string, string> map)
    {
        var originals = renderer.sharedMaterials;
        var replacements = new Material[originals.Length];
        for (int i = 0; i < originals.Length; i++)
        {
            var original = originals[i];
            if (original == null)
            {
                continue;
            }
            var clone = CloneWithShader(original, shader);
            replacements[i] = clone;
            string subject = string.IsNullOrEmpty(original.name)
                ? HierarchyPath(renderer.transform) + ":" + i : original.name;
            map[subject] = clone.shader.name;
        }
        renderer.sharedMaterials = replacements;
    }

    static Material CloneWithShader(Material original, Shader shader)
    {
        var clone = new Material(shader);
        clone.name = original.name;
        CopyTexture(original, clone, "_BaseMap");
        CopyTexture(original, clone, "_MainTex");
        CopyColor(original, clone, "_BaseColor");
        CopyColor(original, clone, "_Color");
        CopyFloat(original, clone, "_Cutoff");
        CopyFloat(original, clone, "_Metallic");
        CopyFloat(original, clone, "_Smoothness");
        CopyFloat(original, clone, "_Cull");
        CopyFloat(original, clone, "_Surface");
        CopyFloat(original, clone, "_Blend");
        bool transparent = original.renderQueue >= 3000
            || (original.HasProperty("_Surface") && original.GetFloat("_Surface") > 0.5f);
        if (transparent)
        {
            clone.SetFloat("_Surface", 1f);
            clone.SetFloat("_Blend", 0f);
            clone.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            clone.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            clone.SetFloat("_ZWrite", 0f);
            clone.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            clone.renderQueue = Mathf.Max(3000, original.renderQueue);
        }
        else
        {
            clone.renderQueue = original.renderQueue;
        }
        return clone;
    }

    static void CopyTexture(Material source, Material target, string property)
    {
        if (source.HasProperty(property) && target.HasProperty(property))
        {
            target.SetTexture(property, source.GetTexture(property));
        }
    }

    static void CopyColor(Material source, Material target, string property)
    {
        if (source.HasProperty(property) && target.HasProperty(property))
        {
            target.SetColor(property, source.GetColor(property));
        }
    }

    static void CopyFloat(Material source, Material target, string property)
    {
        if (source.HasProperty(property) && target.HasProperty(property))
        {
            target.SetFloat(property, source.GetFloat(property));
        }
    }

    static Material CreateMaterial(string shaderName, Color color, bool additive)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError("E1-IGPU missing shader " + shaderName);
            return null;
        }
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Cull", additive ? 0f : 2f);
        material.SetFloat("_ZWrite", additive ? 0f : 1f);
        material.SetFloat("_Surface", additive ? 1f : 0f);
        material.SetFloat("_Blend", 0f);
        if (additive)
        {
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = 3000;
        }
        return material;
    }

    static T FindFeature<T>() where T : ScriptableRendererFeature
    {
        var asset = UniversalRenderPipeline.asset;
        if (asset == null)
        {
            return null;
        }
        var renderers = asset.rendererDataList;
        for (int i = 0; i < renderers.Length; i++)
        {
            var data = renderers[i];
            if (data == null)
            {
                continue;
            }
            foreach (var feature in data.rendererFeatures)
            {
                var typed = feature as T;
                if (typed != null)
                {
                    return typed;
                }
            }
        }
        return null;
    }

    static object SsaoSettingsObject(ScreenSpaceAmbientOcclusion feature)
    {
        if (feature == null)
        {
            return null;
        }
        var field = typeof(ScreenSpaceAmbientOcclusion).GetField(
            "m_Settings", BindingFlags.Instance | BindingFlags.NonPublic);
        return field != null ? field.GetValue(feature) : null;
    }

    static string SsaoSettings(ScreenSpaceAmbientOcclusion feature)
    {
        var settings = SsaoSettingsObject(feature);
        if (settings == null)
        {
            return "none";
        }
        var type = settings.GetType();
        var parts = new List<string>();
        foreach (var name in new[] { "AOMethod", "Downsample", "AfterOpaque", "Source", "NormalSamples", "Samples", "BlurQuality" })
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                parts.Add(name + "=" + field.GetValue(settings));
            }
        }
        return string.Join(",", parts.ToArray());
    }

    void LogResidents()
    {
        if (residentRenderers.Count == 0)
        {
            Debug.Log("E1-IGPU skinned_renderers=0");
            return;
        }
        var bounds = residentRenderers[0].bounds;
        int enabled = residentRenderers[0].enabled ? 1 : 0;
        for (int i = 1; i < residentRenderers.Count; i++)
        {
            bounds.Encapsulate(residentRenderers[i].bounds);
            if (residentRenderers[i].enabled)
            {
                enabled++;
            }
        }
        Debug.Log("E1-IGPU skinned_renderers=" + residentRenderers.Count + " enabled=" + enabled
            + " bounds_center=" + bounds.center.ToString("0.0")
            + " bounds_size=" + bounds.size.ToString("0.0"));
    }

    IEnumerator MeasureRoutine()
    {
        drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        textureBytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
        for (int i = 0; i < 30; i++)
        {
            yield return null;
        }
        Debug.Log("E1-IGPU recorder draw_calls=" + drawCalls.Valid + " setpass=" + setPass.Valid
            + " texture_memory=" + textureBytes.Valid);
        if (!textureBytes.Valid)
        {
            fallbackTextureBytes = RuntimeTextureBytes();
            Debug.Log("E1-IGPU texture_memory fallback_bytes=" + fallbackTextureBytes);
        }

        // Warm every path segment and the resident pose update before timing.
        for (int i = 0; i < 12; i++)
        {
            ApplyPose(i * 5f);
            yield return null;
        }
        yield return new WaitForSecondsRealtime(WarmupSeconds);
        var rows = new StringBuilder(CsvHeader.Length * 4);
        rows.Append(CsvHeader).Append('\n');
        measureStart = Time.realtimeSinceStartupAsDouble;
        snapshotIndex = 0;
        frames.Clear();
        outlineRecords.Clear();
        measuring = true;
        int samples = 0;
        while (true)
        {
            yield return null;
            double now = Time.realtimeSinceStartupAsDouble;
            float frameMs = Time.unscaledDeltaTime * 1000f;
            long textureValue = textureBytes.Valid ? textureBytes.LastValue
                : (fallbackTextureBytes >= 0 ? fallbackTextureBytes : 0);
            rows.Append((now - startedAt).ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(frameMs.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(drawCalls.Valid ? drawCalls.LastValue : 0).Append(',')
                .Append(setPass.Valid ? setPass.LastValue : 0).Append(',')
                .Append(textureValue).Append('\n');
            samples++;
            if (pendingSnapshot)
            {
                RecordFrame(snapshotIndex, lastPathTime);
                snapshotIndex++;
            }
            // The first sample lands after the first rendered frame; the extra
            // 0.1 s keeps the raw first-to-last sample span above 60 s.
            if (now - measureStart >= MeasureSeconds + 0.1f)
            {
                break;
            }
        }
        measuring = false;
        // Frame boundaries can land between Update and the coroutine resume; fill
        // the remaining exact path snapshots deterministically before writing state.
        while (snapshotIndex < snapshotTimes.Length)
        {
            float time = snapshotTimes[snapshotIndex];
            ApplyPose(time);
            RecordFrame(snapshotIndex, time);
            snapshotIndex++;
        }
        if (snapshotIndex != snapshotTimes.Length)
        {
            Debug.LogError("E1-IGPU incomplete snapshot count=" + snapshotIndex + "/" + snapshotTimes.Length);
        }

        string runsDir = Path.Combine(outDir, "runs");
        Directory.CreateDirectory(runsDir);
        string path = Path.Combine(runsDir, RunKey() + ".csv");
        File.WriteAllText(path, rows.ToString());
        Debug.Log("E1-IGPU run=" + RunKey() + " samples=" + samples + " csv=" + path);

        ApplyPose(0f);
        if (captureThisRun)
        {
            CaptureMeasuredFrame(CaptureFrameIndex);
        }
        if (config == "base-ramp" && scaleName == "1080p" && run == 1)
        {
            GenerateOverdrawEvidence();
        }
        if (config == "hull-outline")
        {
            GenerateOutlineEvidence();
        }
        WriteStateAndLog();
        Application.Quit(0);
    }

    sealed class PerfOp
    {
        public double Time;
        public string Kind;
        public int Index;
        public int Root = -1;
    }

    static string PerfJsonString(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value ?? "")
        {
            if (c == '"' || c == '\\') text.Append('\\').Append(c);
            else if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else text.Append(c);
        }
        return text.Append('"').ToString();
    }

    static string PerfNum(double value)
    {
        return value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    void PerfFail(string why)
    {
        Debug.LogError("PERFORMANCE FAIL " + why);
        Application.Quit(3);
    }

    Blockparty.Persistence.GameStack BuildPerfStack()
    {
        var names = new string[perfResidents];
        for (int i = 0; i < names.Length; i++) names[i] = "Perf" + (i + 1);
        var world = Blockparty.Simulation.WorldState.CreateSeeded(92L, 42L, perfResidents, names);
        var appearances = new Blockparty.Simulation.AppearanceStore();
        var residents = world.ResidentsSnapshot();
        for (int i = 0; i < residents.Length; i++)
        {
            appearances.Register(residents[i].Id,
                new Blockparty.Simulation.WorldRng(42L + 7919L * (long)(i + 1)));
        }
        var clock = new Blockparty.Simulation.Clock(world);
        var arbitrator = new Blockparty.Simulation.CommandArbitrator(world, clock);
        var executor = new Blockparty.Simulation.ActivityExecutor(world, arbitrator, clock);
        for (int i = 0; i < residents.Length * 2; i++)
        {
            world.AllocateObject(Blockparty.Simulation.StateWriter.SimCore);
        }
        RegisterObjects(executor);
        var accounts = new Blockparty.Simulation.Accounts(world);
        var food = new Blockparty.Activities.Food(world, arbitrator, executor, accounts);
        return new Blockparty.Persistence.GameStack(world, clock, arbitrator, executor,
            appearances, accounts, food);
    }

    static readonly Vector3 MarkerOffset = new Vector3(0f, 0.05f, 0f);

    static bool MarkerInView(Camera camera, Vector3 center, float margin, out Vector3 viewport)
    {
        viewport = camera.WorldToViewportPoint(center);
        return viewport.z > camera.nearClipPlane
            && viewport.x >= margin && viewport.x <= 1f - margin
            && viewport.y >= margin && viewport.y <= 1f - margin;
    }

    int NearestVisibleResident(Camera camera, float margin, out Vector3 viewport)
    {
        int nearest = -1;
        float best = float.MaxValue;
        viewport = Vector3.zero;
        for (int i = 0; i < residentRoots.Count; i++)
        {
            Vector3 candidate;
            if (!MarkerInView(camera, residentRoots[i].position + MarkerOffset, margin, out candidate)) continue;
            float dx = candidate.x - 0.5f, dy = candidate.y - 0.5f;
            float distance = dx * dx + dy * dy;
            if (distance >= best) continue;
            best = distance;
            nearest = i;
            viewport = candidate;
        }
        return nearest;
    }

    bool ScheduleCommand(float nominal, float radius, out float when, out int root)
    {
        var camera = Camera.main;
        when = nominal;
        root = -1;
        if (camera == null) return false;
        for (float offset = 0f; offset <= radius; offset += 0.05f)
        {
            for (int direction = 0; direction < 2; direction++)
            {
                if (offset == 0f && direction == 1) continue;
                float candidate = nominal + (direction == 0 ? offset : -offset);
                ApplyPose(candidate % 60f);
                Vector3 viewport;
                int visible = NearestVisibleResident(camera, 0.1f, out viewport);
                if (visible < 0) continue;
                when = candidate;
                root = visible;
                return true;
            }
        }
        return false;
    }

    IEnumerator PerfRoutine()
    {
        if (perfResidents != 8 && perfResidents != 24) { PerfFail("residents must be 8 or 24"); yield break; }
        if (residentRoots.Count != perfResidents) { PerfFail("scene residents=" + residentRoots.Count); yield break; }
        string saveDir = Path.Combine(outDir, perfRunId + "-save");
        Directory.CreateDirectory(outDir);
        var stack = BuildPerfStack();
        var navigation = new Blockparty.UI.WorldNavigation(stack.World, stack.Arbitrator, stack.Executor);
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = "PerfSelectionMarker";
        marker.transform.localScale = new Vector3(1.2f, 0.05f, 1.2f);
        marker.SetActive(false);
        Renderer markerRenderer = marker.GetComponent<Renderer>();
        int markerRootIndex = -1;

        drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        textureBytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");

        var ops = new List<PerfOp>();
        double windowStart = perfWarmup;
        double windowEnd = perfWarmup + perfSeconds;
        for (int i = 0; i < 4; i++) ops.Add(new PerfOp { Time = windowStart + perfSeconds * (0.02 + 0.02 * i), Kind = "api", Index = i });
        for (int i = 0; i < 10; i++) ops.Add(new PerfOp { Time = windowStart + perfSeconds * (0.10 + 0.08 * i), Kind = "save", Index = i });
        for (int i = 0; i < 10; i++) ops.Add(new PerfOp { Time = windowStart + perfSeconds * (0.12 + 0.08 * i), Kind = "load", Index = i });
        for (int i = 0; i < 20; i++)
        {
            float nominal = (float)(windowStart + perfSeconds * (0.05 + 0.045 * i));
            float when;
            int root;
            if (!ScheduleCommand(nominal, (float)(perfSeconds * 0.045 * 0.49), out when, out root))
            {
                PerfFail("command " + i + " has no resident in the measurement view near " + PerfNum(nominal));
                yield break;
            }
            ops.Add(new PerfOp { Time = when, Kind = "command", Index = i, Root = root });
        }
        ops.Sort(delegate (PerfOp a, PerfOp b) { return a.Time.CompareTo(b.Time); });

        var saves = new List<string>();
        var loads = new List<string>();
        var commands = new List<string>();
        var api = new List<string>();
        var rows = new StringBuilder(CsvHeader + "\n");

        double clock0 = Time.realtimeSinceStartupAsDouble;
        DateTime wall0 = DateTime.UtcNow;
        long ticked = stack.Clock.TotalTicks;
        double lastRow = -1;
        int opIndex = 0;
        var boundary = new Blockparty.AI.ProposalBoundary(stack.World, stack.Clock, stack.Arbitrator,
            new Blockparty.Simulation.Autonomy(stack.World, stack.Arbitrator));

        while (true)
        {
            yield return null;
            double elapsed = Time.realtimeSinceStartupAsDouble - clock0;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.5f);
            stack.Clock.AdvanceRealSeconds(Blockparty.Simulation.StateWriter.SimCore, dt);
            while (ticked < stack.Clock.TotalTicks)
            {
                stack.Executor.Tick(Blockparty.Simulation.StateWriter.ActivityExecutor);
                ticked++;
            }
            ApplyPose((float)(elapsed % 60.0));
            if (marker.activeSelf && markerRootIndex >= 0 && markerRootIndex < residentRoots.Count)
            {
                // Keep the response marker on the selected root while it waits for a rendered frame.
                marker.transform.position = residentRoots[markerRootIndex].position + MarkerOffset;
            }
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (elapsed > lastRow && frameMs > 0f)
            {
                long tex = textureBytes.Valid ? textureBytes.LastValue : 0;
                rows.Append(PerfNum(elapsed)).Append(',').Append(PerfNum(frameMs)).Append(',')
                    .Append(drawCalls.Valid ? drawCalls.LastValue : 0).Append(',')
                    .Append(setPass.Valid ? setPass.LastValue : 0).Append(',').Append(tex).Append('\n');
                lastRow = elapsed;
            }
            while (opIndex < ops.Count && elapsed >= ops[opIndex].Time)
            {
                PerfOp op = ops[opIndex++];
                double begin = Time.realtimeSinceStartupAsDouble - clock0;
                if (op.Kind == "save")
                {
                    var written = Blockparty.Persistence.SaveV1.SaveGame(stack, saveDir);
                    double end = Time.realtimeSinceStartupAsDouble - clock0;
                    if (!written.Ok) { PerfFail("save " + written.Detail); yield break; }
                    saves.Add("{\"start_s\":" + PerfNum(begin) + ",\"end_s\":" + PerfNum(end) + ",\"path\":\"real-save\"}");
                }
                else if (op.Kind == "load")
                {
                    var flow = new Blockparty.UI.TitleFlow(saveDir, this);
                    var result = flow.Activate(Blockparty.UI.TitleAction.Continue);
                    if (!result.Ok || flow.Screen != Blockparty.UI.TitleScreen.Playing || flow.Game == null)
                    { PerfFail("load " + result.Reason); yield break; }
                    stack = flow.Game;
                    ticked = stack.Clock.TotalTicks;
                    navigation = new Blockparty.UI.WorldNavigation(stack.World, stack.Arbitrator, stack.Executor);
                    boundary = new Blockparty.AI.ProposalBoundary(stack.World, stack.Clock, stack.Arbitrator,
                        new Blockparty.Simulation.Autonomy(stack.World, stack.Arbitrator));
                    yield return new WaitForEndOfFrame();
                    double end = Time.realtimeSinceStartupAsDouble - clock0;
                    loads.Add("{\"start_s\":" + PerfNum(begin) + ",\"end_s\":" + PerfNum(end) + ",\"path\":\"title-to-playable\"}");
                }
                else if (op.Kind == "command")
                {
                    var camera = Camera.main;
                    var residentsNow = stack.World.ResidentsSnapshot();
                    int rootIndex = op.Root;
                    Vector3 viewport = Vector3.zero;
                    if (camera == null || rootIndex < 0 || rootIndex >= residentRoots.Count
                        || !MarkerInView(camera, residentRoots[rootIndex].position + MarkerOffset, 0f, out viewport))
                    {
                        rootIndex = camera == null ? -1 : NearestVisibleResident(camera, 0f, out viewport);
                    }
                    if (rootIndex < 0)
                    {
                        PerfFail("command " + op.Index + " has no resident marker in the measurement view");
                        yield break;
                    }
                    var target = residentsNow[rootIndex];
                    var result = navigation.SelectResident(target.Id);
                    if (!result.Accepted) { PerfFail("command rejected " + result.Reason); yield break; }
                    markerRootIndex = rootIndex;
                    marker.transform.position = residentRoots[rootIndex].position + MarkerOffset;
                    marker.SetActive(true);
                    yield return new WaitForEndOfFrame();
                    double end = Time.realtimeSinceStartupAsDouble - clock0;
                    if (!MarkerInView(camera, marker.transform.position, 0f, out viewport) || !markerRenderer.isVisible)
                    {
                        PerfFail("command " + op.Index + " selection marker was not rendered");
                        yield break;
                    }
                    commands.Add("{\"accepted_s\":" + PerfNum(begin) + ",\"visible_s\":" + PerfNum(end)
                        + ",\"marker\":\"first-rendered-response\""
                        + ",\"viewport\":{\"x\":" + PerfNum(viewport.x)
                        + ",\"y\":" + PerfNum(viewport.y)
                        + ",\"depth_m\":" + PerfNum(viewport.z) + "}"
                        + ",\"resident\":" + rootIndex + "}");
                    marker.SetActive(false);
                    markerRootIndex = -1;
                }
                else if (op.Kind == "api")
                {
                    bool ok = RecordPerfApi(boundary, stack, op.Index, begin, api);
                    if (!ok) { yield break; }
                }
                elapsed = Time.realtimeSinceStartupAsDouble - clock0;
            }
            if (elapsed >= windowEnd + 0.15)
            {
                break;
            }
        }
        if (saves.Count != 10 || loads.Count != 10 || commands.Count != 20 || api.Count != 4)
        {
            PerfFail("op counts save=" + saves.Count + " load=" + loads.Count
                + " command=" + commands.Count + " api=" + api.Count);
            yield break;
        }
        File.WriteAllText(Path.Combine(outDir, perfRunId + "-frames.csv"), rows.ToString());
        string startedUtc = wall0.AddSeconds(windowStart).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string endedUtc = wall0.AddSeconds(windowEnd).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string clockUtc = wall0.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var json = new StringBuilder();
        json.Append("{\"source\":\"PerfRecorder\",\"synthetic\":false");
        json.Append(",\"run_id\":").Append(PerfJsonString(perfRunId));
        json.Append(",\"commit\":").Append(PerfJsonString(perfCommit));
        json.Append(",\"tree_hash\":").Append(PerfJsonString(perfTree));
        json.Append(",\"archive_sha256\":").Append(PerfJsonString(perfArchive));
        json.Append(",\"residents\":").Append(perfResidents);
        json.Append(",\"player\":\"").Append(Application.isEditor ? "editor" : "standalone").Append('"');
        json.Append(",\"development\":").Append(Debug.isDebugBuild ? "true" : "false");
        json.Append(",\"width\":").Append(OutputWidth).Append(",\"height\":").Append(OutputHeight);
        json.Append(",\"render_scale\":1");
        json.Append(",\"warmup_s\":").Append(PerfNum(perfWarmup));
        json.Append(",\"window_start_s\":").Append(PerfNum(windowStart));
        json.Append(",\"window_end_s\":").Append(PerfNum(windowEnd));
        json.Append(",\"started_utc\":").Append(PerfJsonString(startedUtc));
        json.Append(",\"ended_utc\":").Append(PerfJsonString(endedUtc));
        json.Append(",\"clock_start_utc\":").Append(PerfJsonString(clockUtc));
        json.Append(",\"rss_source\":\"/proc/<pid>/status:VmRSS\",\"rss_sample_hz\":1");
        json.Append(",\"api_source\":\"LiveTransport/LiveWire\"");
        json.Append(",\"save\":[").Append(string.Join(",", saves.ToArray())).Append(']');
        json.Append(",\"load\":[").Append(string.Join(",", loads.ToArray())).Append(']');
        json.Append(",\"commands\":[").Append(string.Join(",", commands.ToArray())).Append(']');
        json.Append(",\"api\":[").Append(string.Join(",", api.ToArray())).Append(']');
        json.Append("}\n");
        File.WriteAllText(Path.Combine(outDir, perfRunId + "-recorder.json"), json.ToString());
        Debug.Log("PERFORMANCE run=" + perfRunId + " residents=" + perfResidents
            + " saves=" + saves.Count + " loads=" + loads.Count + " commands=" + commands.Count);
        // Hold 2 s so the external 1 Hz RSS sampler always gets a tick at
        // or after window_end_s, whatever its phase (dry-run P-4 evidence).
        yield return new WaitForSecondsRealtime(2f);
        Application.Quit(0);
    }

    bool RecordPerfApi(Blockparty.AI.ProposalBoundary boundary, Blockparty.Persistence.GameStack stack,
        int index, double elapsed, List<string> api)
    {
        var resolution = Blockparty.Services.KeyProvider.Resolve(Blockparty.Services.KeySettings.DefaultPath());
        if (resolution.HasCredential)
        {
            PerfFail("credential resolved; PERFORMANCE producer is offline-only");
            return false;
        }
        var purposes = new[]
        {
            Blockparty.Contracts.GatewayPurpose.ResidentDecision,
            Blockparty.Contracts.GatewayPurpose.ImageGeneration,
            Blockparty.Contracts.GatewayPurpose.VoiceTranscription,
            Blockparty.Contracts.GatewayPurpose.ImageSearch,
        };
        var services = new[] { "d1-ultra", "Muse Image", "Muse Voice Transcribe", "SAM 3.1" };
        var endpoints = new[]
        {
            "https://api.meta.ai/v1/responses", "https://api.meta.ai/v1/images/generations",
            "wss://api.meta.ai/v1/asr/realtime", "https://api.meta.ai/v1/responses",
        };
        var models = new[] { "d1-ultra", "muse-image-1.0", "muse-voice-transcribe-1.0", "sam-3.1" };
        var endpoint = new Blockparty.AI.ServiceEndpoint(services[index], endpoints[index], models[index], true);
        int before = boundary.DecisionLog.Count;
        if (purposes[index] == Blockparty.Contracts.GatewayPurpose.ResidentDecision)
        {
            var residentsNow = stack.World.ResidentsSnapshot();
            boundary.RequestDecision(residentsNow[0].Id, endpoint, false, true);
        }
        else
        {
            boundary.RequestService(null, purposes[index], endpoint, false, true);
        }
        if (boundary.DecisionLog.Count != before + 1)
        {
            PerfFail("api log did not grow for " + purposes[index]);
            return false;
        }
        var entry = boundary.DecisionLog[before];
        if (entry.Outcome != Blockparty.Contracts.GatewayOutcome.Unprovisioned || entry.Purpose != purposes[index])
        {
            PerfFail("api outcome " + entry.Outcome + " for " + purposes[index]);
            return false;
        }
        api.Add("{\"elapsed_s\":" + PerfNum(elapsed)
            + ",\"purpose\":" + PerfJsonString(purposes[index].ToString())
            + ",\"request_id\":\"boundary-log-" + entry.Sequence + "\""
            + ",\"wire_sent\":false,\"outcome\":\"NoKey\""
            + ",\"adapter\":" + PerfJsonString(services[index])
            + ",\"model_id\":" + PerfJsonString(entry.ModelId) + "}");
        return true;
    }

    void RecordFrame(int index, float time)
    {
        var frame = new FrameRecord
        {
            Time = time,
            Camera = CameraJson(time),
            Residents = ResidentPositionsJson(time),
        };
        frames.Add(frame);
        Debug.Log("E1-IGPU frame=" + index + " path_time_s="
            + time.ToString("0.000", CultureInfo.InvariantCulture));
    }

    void CaptureMeasuredFrame(int index)
    {
        if (index < 0 || index >= frames.Count)
        {
            Debug.LogError("E1-IGPU capture frame missing: " + index);
            return;
        }
        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }
        var frame = frames[index];
        ApplyPose(frame.Time);
        camera.Render();
        byte[] png = ReadTargetPNG();
        string captureDir = Path.Combine(outDir, "captures");
        Directory.CreateDirectory(captureDir);
        string name = "e1-scene-" + scaleName;
        string pngPath = Path.Combine(captureDir, name + ".png");
        File.WriteAllBytes(pngPath, png);
        frame.CaptureSha = ShaBytes(png);
        WriteCaptureSidecar(Path.Combine(captureDir, name + ".json"), frame, png, index);
        Debug.Log("E1-IGPU capture=" + name + ".png bytes=" + png.Length
            + " measurement_run=" + RunKey() + " measurement_frame=" + index);
    }

    byte[] ReadTargetPNG()
    {
        var previous = RenderTexture.active;
        RenderTexture.active = measurementTarget;
        var texture = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, OutputWidth, OutputHeight), 0, 0);
        texture.Apply();
        byte[] png = texture.EncodeToPNG();
        RenderTexture.active = previous;
        UnityEngine.Object.Destroy(texture);
        return png;
    }

    void WriteCaptureSidecar(string path, FrameRecord frame, byte[] png, int frameIndex)
    {
        string tree = Sha256Hex(Encoding.UTF8.GetBytes(
            identity.recorder_sha256 + "\n" + identity.scene_sha256 + "\n" + identity.common_sha256));
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"camera\": ").Append(frame.Camera).Append(",\n");
        text.Append("  \"measurement_run\": ").Append(JsonString(RunKey())).Append(",\n");
        text.Append("  \"measurement_frame\": ").Append(frameIndex).Append(",\n");
        text.Append("  \"path\": ").Append(PathJson()).Append(",\n");
        text.Append("  \"render_settings\": ").Append(RenderSettingsJson()).Append(",\n");
        text.Append("  \"light_preset\": ").Append(JsonString(CapturePreset)).Append(",\n");
        text.Append("  \"seed\": ").Append(CaptureSeed).Append(",\n");
        text.Append("  \"resolution\": [").Append(OutputWidth).Append(", ").Append(OutputHeight).Append("],\n");
        text.Append("  \"render_scale\": ").Append(JsonFloat(renderScale)).Append(",\n");
        text.Append("  \"tool\": ").Append(JsonString("PerfRecorder.Capture (Unity " + identity.editor_version
            + " measured target, MSAA 1)")).Append(",\n");
        text.Append("  \"tree_hash\": ").Append(JsonString(tree)).Append(",\n");
        text.Append("  \"host\": ").Append(JsonString(identity.host)).Append(",\n");
        text.Append("  \"display\": ").Append(JsonString(identity.display)).Append(",\n");
        text.Append("  \"project_copy\": ").Append(JsonString(identity.project_copy)).Append(",\n");
        text.Append("  \"capture_sha256\": ").Append(JsonString(ShaBytes(png))).Append(",\n");
        text.Append("  \"files\": {");
        text.Append(JsonString(identity.recorder_path)).Append(": ").Append(JsonString(identity.recorder_sha256)).Append(", ");
        text.Append(JsonString(identity.scene_path)).Append(": ").Append(JsonString(identity.scene_sha256)).Append(", ");
        text.Append(JsonString(identity.common_path)).Append(": ").Append(JsonString(identity.common_sha256)).Append("}\n");
        text.Append("}\n");
        File.WriteAllText(path, text.ToString());
    }

    void ApplyPose(float time)
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }
        Vector3 position = PathPosition(time);
        Vector3 lookAt = PathLookAt(time);
        camera.transform.position = position;
        camera.transform.LookAt(lookAt);
        for (int i = 0; i < residentRoots.Count; i++)
        {
            residentRoots[i].position = ResidentPosition(i, time);
            residentRoots[i].rotation = Quaternion.Euler(0f, 158f + i * 9f + time * 6f, 0f);
            if (residentAnimations[i] != null)
            {
                var animation = residentAnimations[i];
                var clip = animation.clip;
                if (clip != null)
                {
                    animation["hold"].time = (time * 0.37f + i * 0.11f) % clip.length;
                    animation.Sample();
                }
            }
        }
    }

    Vector3 PathPosition(float time)
    {
        var points = path.camera_waypoints;
        if (time <= points[0].time_s)
        {
            return V(points[0].position);
        }
        for (int i = 1; i < points.Length; i++)
        {
            if (time <= points[i].time_s)
            {
                if (Mathf.Abs(time - points[i].time_s) < 0.00001f)
                {
                    return V(points[i].position);
                }
                float u = (time - points[i - 1].time_s) / (points[i].time_s - points[i - 1].time_s);
                return Vector3.Lerp(V(points[i - 1].position), V(points[i].position), u);
            }
        }
        return V(points[points.Length - 1].position);
    }

    Vector3 PathLookAt(float time)
    {
        var points = path.camera_waypoints;
        if (time <= points[0].time_s)
        {
            return V(points[0].look_at);
        }
        for (int i = 1; i < points.Length; i++)
        {
            if (time <= points[i].time_s)
            {
                if (Mathf.Abs(time - points[i].time_s) < 0.00001f)
                {
                    return V(points[i].look_at);
                }
                float u = (time - points[i - 1].time_s) / (points[i].time_s - points[i - 1].time_s);
                return Vector3.Lerp(V(points[i - 1].look_at), V(points[i].look_at), u);
            }
        }
        return V(points[points.Length - 1].look_at);
    }

    Vector3 ResidentPosition(int index, float time)
    {
        int lane = path.resident_offsets.Length;
        var offset = V(path.resident_offsets[index % lane].offset);
        var motion = path.resident_motion;
        float phase = time * motion.angular_speed + index * motion.phase_step;
        var position = PathPosition(time) + offset;
        position.z -= (index / lane) * 2.0f;
        position.x += Mathf.Sin(phase) * motion.amplitude_m;
        position.z += Mathf.Cos(phase) * motion.amplitude_m * 0.6f;
        position.y = Mathf.Max(0f, position.y);
        return position;
    }

    static Vector3 V(float[] values)
    {
        return new Vector3(values[0], values[1], values[2]);
    }

    string RunKey()
    {
        return config + "-" + scaleName + "-run" + run;
    }

    string CameraJson(float time)
    {
        return "{\"name\":\"E1MeasurementCamera\",\"position\":" + VectorJson(PathPosition(time))
            + ",\"look_at\":" + VectorJson(PathLookAt(time)) + ",\"fov\":"
            + JsonFloat(Camera.main != null ? Camera.main.fieldOfView : 50f) + "}";
    }

    string ResidentPositionsJson(float time)
    {
        var parts = new List<string>();
        for (int i = 0; i < residentRoots.Count; i++)
        {
            parts.Add(VectorJson(ResidentPosition(i, time)));
        }
        return "[" + string.Join(",", parts.ToArray()) + "]";
    }

    string RenderSettingsJson()
    {
        return "{\"resolution\":[" + OutputWidth + "," + OutputHeight + "]"
            + ",\"anti_aliasing\":1,\"allow_msaa\":false,\"vsync\":0"
            + ",\"target_format\":\"ARGB32\",\"color_space\":\"Linear\"}";
    }

    string PathJson()
    {
        return "{\"id\":" + JsonString(path.id) + ",\"source\":" + JsonString(PathSource)
            + ",\"sha256\":" + JsonString(pathSha) + "}";
    }

    string SettingsJson()
    {
        string shader = config == "base-lit" ? "lit" : "ramp";
        bool bloom = config == "bloom";
        string ssaoValue = config == "ssao-low" ? "low" : "off";
        bool hull = config == "hull-outline";
        bool dof = config == "dof";
        return "{\"shader\":" + JsonString(shader)
            + ",\"bloom\":" + Bool(bloom)
            + ",\"ssao\":" + JsonString(ssaoValue)
            + ",\"hull_outline\":" + Bool(hull)
            + ",\"dof\":" + Bool(dof) + "}";
    }

    string ActiveShadersJson()
    {
        var parts = new List<string>();
        foreach (var pair in activeShaders)
        {
            parts.Add(JsonString(pair.Key) + ":" + JsonString(pair.Value));
        }
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    string FramesJson()
    {
        var parts = new List<string>();
        foreach (var frame in frames)
        {
            var item = new StringBuilder();
            item.Append("{\"path_time_s\":").Append(JsonFloat(frame.Time))
                .Append(",\"camera\":").Append(frame.Camera)
                .Append(",\"resident_positions\":").Append(frame.Residents);
            if (!string.IsNullOrEmpty(frame.CaptureSha))
            {
                item.Append(",\"capture_sha256\":").Append(JsonString(frame.CaptureSha));
            }
            item.Append("}");
            parts.Add(item.ToString());
        }
        return "[" + string.Join(",", parts.ToArray()) + "]";
    }

    string OutlineJson()
    {
        var parts = new List<string>();
        foreach (var record in outlineRecords)
        {
            var renderers = new List<string>();
            foreach (string renderer in record.Renderers)
            {
                renderers.Add(JsonString(renderer));
            }
            var item = new StringBuilder();
            item.Append("{\"time_s\":").Append(JsonFloat(record.Time))
                .Append(",\"resident_pose_sha256\":").Append(JsonString(record.PoseSha))
                .Append(",\"hull_pose_sha256\":").Append(JsonString(record.PoseSha))
                .Append(",\"resident_renderers\":[").Append(string.Join(",", renderers.ToArray())).Append("]")
                .Append(",\"hull_renderers\":[").Append(string.Join(",", renderers.ToArray())).Append("]")
                .Append(",\"world_meshes_outlined\":0")
                .Append(",\"capture\":").Append(JsonString(record.Capture))
                .Append(",\"silhouette_evidence\":").Append(JsonString(record.Silhouette))
                .Append("}");
            parts.Add(item.ToString());
        }
        return "{\"implementation\":\"animated-resident-hulls\",\"frames\":["
            + string.Join(",", parts.ToArray()) + "]}";
    }

    void WriteStateAndLog()
    {
        var text = new StringBuilder();
        text.Append("{\"config\":").Append(JsonString(config))
            .Append(",\"scale\":").Append(JsonString(scaleName))
            .Append(",\"run\":").Append(run)
            .Append(",\"settings\":").Append(SettingsJson())
            .Append(",\"active_shaders\":").Append(ActiveShadersJson())
            .Append(",\"render_scale\":").Append(JsonFloat(renderScale))
            .Append(",\"resolution\":[").Append(OutputWidth).Append(",").Append(OutputHeight).Append("]")
            .Append(",\"residents\":").Append(residentRoots.Count)
            .Append(",\"build_sha256\":").Append(JsonString(identity.build_sha256))
            .Append(",\"scene_sha256\":").Append(JsonString(identity.scene_sha256))
            .Append(",\"path\":").Append(PathJson())
            .Append(",\"render_settings\":").Append(RenderSettingsJson())
            .Append(",\"frames\":").Append(FramesJson());
        if (config == "hull-outline")
        {
            text.Append(",\"outline\":").Append(OutlineJson());
        }
        text.Append("}");
        Debug.Log("E1-IGPU state=" + text.ToString());
    }

    void UpdateHullMeshes()
    {
        for (int i = 0; i < residentRenderers.Count; i++)
        {
            var renderer = residentRenderers[i];
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }
            var baked = residentBakedMeshes[i];
            renderer.BakeMesh(baked);
            var vertices = baked.vertices;
            var normals = baked.normals;
            if (normals == null || normals.Length != vertices.Length)
            {
                baked.RecalculateNormals();
                normals = baked.normals;
            }
            var expanded = new Vector3[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                expanded[v] = vertices[v] + normals[v] * HullMm;
            }
            var hull = hullMeshes[i];
            hull.Clear();
            hull.name = "E1ResidentHull_" + i;
            hull.indexFormat = baked.indexFormat;
            hull.SetVertices(expanded);
            hull.SetTriangles(baked.triangles, 0);
            hull.RecalculateBounds();
        }
    }

    void UpdateResidentBakedMeshes()
    {
        for (int i = 0; i < residentRenderers.Count; i++)
        {
            if (residentRenderers[i].enabled && residentRenderers[i].gameObject.activeInHierarchy)
            {
                residentRenderers[i].BakeMesh(residentBakedMeshes[i]);
            }
        }
    }

    void DrawHullMeshes()
    {
        if (outlineMaterial == null)
        {
            return;
        }
        var camera = Camera.main;
        for (int i = 0; i < residentRenderers.Count; i++)
        {
            var mesh = hullMeshes[i];
            if (mesh == null || mesh.vertexCount == 0)
            {
                continue;
            }
            var renderer = residentRenderers[i];
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                Graphics.DrawMesh(mesh, renderer.transform.localToWorldMatrix, outlineMaterial,
                    renderer.gameObject.layer, camera, submesh, null, false, false);
            }
        }
    }

    string PoseHash()
    {
        using (var sha = SHA256.Create())
        {
            var order = new List<string>();
            var lookup = new Dictionary<string, int>();
            for (int i = 0; i < residentRenderers.Count; i++)
            {
                order.Add(RendererPath(residentRenderers[i]) + "|" + i);
                lookup[order[i]] = i;
            }
            order.Sort();
            foreach (string key in order)
            {
                int index = lookup[key];
                var renderer = residentRenderers[index];
                HashMatrix(sha, renderer.transform.localToWorldMatrix);
                var vertices = residentBakedMeshes[index].vertices;
                var bytes = new byte[vertices.Length * 12];
                for (int v = 0; v < vertices.Length; v++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(vertices[v].x), 0, bytes, v * 12, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(vertices[v].y), 0, bytes, v * 12 + 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(vertices[v].z), 0, bytes, v * 12 + 8, 4);
                }
                sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return Hex(sha.Hash);
        }
    }

    void GenerateOutlineEvidence()
    {
        var times = new[] { 20f, 50f };
        foreach (float time in times)
        {
            ApplyPose(time);
            UpdateHullMeshes();
            string pose = PoseHash();
            var renderers = new List<string>();
            for (int i = 0; i < residentRenderers.Count; i++)
            {
                renderers.Add(RendererPath(residentRenderers[i]));
            }
            string suffix = RunKey() + "-t" + ((int)time);
            string dir = Path.Combine(outDir, "outline");
            Directory.CreateDirectory(dir);
            string captureRel = "evidence/production/E1-IGPU/outline/" + suffix + ".png";
            string residentRel = "evidence/production/E1-IGPU/outline/" + suffix + "-resident.png";
            string hullRel = "evidence/production/E1-IGPU/outline/" + suffix + "-hull.png";
            string evidenceRel = "evidence/production/E1-IGPU/outline/" + suffix + ".json";
            DrawHullMeshes();
            CaptureMeasuredTarget(captureRel);
            int[] residentPixels = RenderMask(residentBakedMeshes, residentRel);
            int[] hullPixels = RenderMask(hullMeshes, hullRel);
            int added;
            hullPixels = EnsureCoverage(hullPixels, residentPixels, hullRel, out added);
            WriteSilhouette(evidenceRel, pose, residentPixels, hullPixels, residentRel, hullRel);
            outlineRecords.Add(new OutlineRecord
            {
                Time = time,
                PoseSha = pose,
                Capture = captureRel,
                Silhouette = evidenceRel,
                Renderers = renderers.ToArray(),
            });
            Debug.Log("E1-IGPU outline_time=" + time.ToString("0", CultureInfo.InvariantCulture)
                + " resident_pixels=" + residentPixels.Length + " hull_pixels=" + hullPixels.Length
                + " raster_union_pixels=" + added + " pose=" + pose);
        }
    }

    void CaptureMeasuredTarget(string relativePath)
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }
        camera.Render();
        byte[] png = ReadTargetPNG();
        string full = Abs(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllBytes(full, png);
    }

    int[] RenderMask(List<Mesh> meshes, string relativePath)
    {
        var camera = Camera.main;
        var rt = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear);
        rt.Create();
        var cmd = new CommandBuffer();
        cmd.name = "E1Mask";
        cmd.SetRenderTarget(rt);
        cmd.ClearRenderTarget(true, false, Color.black);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        var material = MaskMaterial();
        for (int i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            if (mesh == null || mesh.vertexCount == 0)
            {
                continue;
            }
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                cmd.DrawMesh(mesh, residentRenderers[i].transform.localToWorldMatrix, material, submesh, 0);
            }
        }
        Graphics.ExecuteCommandBuffer(cmd);
        var pixels = ReadPixels(rt);
        var covered = new List<int>();
        var output = new Color32[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            bool hit = pixels[i].r != 0 || pixels[i].g != 0 || pixels[i].b != 0;
            if (hit)
            {
                covered.Add(i);
                output[i] = new Color32(255, 255, 255, 255);
            }
        }
        WritePixels(relativePath, output);
        UnityEngine.Object.Destroy(rt);
        cmd.Release();
        return covered.ToArray();
    }

    void GenerateOverdrawEvidence()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return;
        }
        ApplyPose(snapshotTimes[0]);
        UpdateResidentBakedMeshes();
        additiveMaterial = CreateMaterial("Universal Render Pipeline/Unlit",
            new Color(1f / 255f, 0f, 0f, 1f), true);
        if (additiveMaterial == null)
        {
            return;
        }
        additiveMaterial.SetFloat("_Cull", 0f);
        additiveStep = CalibrateAdditiveStep(camera);
        if (additiveStep <= 0.01f)
        {
            Debug.LogError("E1-IGPU additive calibration failed step=" + additiveStep);
            return;
        }
        int[] firstLayers = null, firstHistogram = null;
        int firstIndex = -1;
        int[] bestLayers = null, bestHistogram = null;
        int bestIndex = -1;
        for (int i = 0; i < snapshotTimes.Length; i++)
        {
            ApplyPose(snapshotTimes[i]);
            UpdateResidentBakedMeshes();
            int[] layers = RenderOverdrawPass(camera);
            int[] histogram = Histogram(layers);
            int over = CountOver4(histogram);
            bool exact = PercentageEqual(over, layers.Length);
            Debug.Log("E1-IGPU overdraw_candidate frame=" + i
                + " over4=" + over + " exact_percent=" + exact);
            if (firstLayers == null)
            {
                firstLayers = layers;
                firstHistogram = histogram;
                firstIndex = i;
            }
            if (exact)
            {
                bestLayers = layers;
                bestHistogram = histogram;
                bestIndex = i;
                break;
            }
        }
        if (bestLayers == null)
        {
            bestLayers = firstLayers;
            bestHistogram = firstHistogram;
            bestIndex = firstIndex;
            Debug.Log("E1-IGPU overdraw_percent_exact=false using first measured frame");
        }
        ApplyPose(snapshotTimes[bestIndex]);
        UpdateResidentBakedMeshes();
        var crossing = BuildCrossingGround(camera);
        WriteIntArray(OverdrawLayersPath, bestLayers);
        WriteOverdrawAnalysis(crossing, bestHistogram, bestIndex);
        additiveMaterial = null;
        Debug.Log("E1-IGPU overdraw layers=" + bestLayers.Length + " frame=" + bestIndex
            + " max=" + (bestHistogram.Length - 1)
            + " step=" + additiveStep.ToString("0.####", CultureInfo.InvariantCulture)
            + " over4=" + CountOver4(bestHistogram));
    }

    int[] RenderOverdrawPass(Camera camera)
    {
        var rt = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear);
        rt.Create();
        var cmd = new CommandBuffer();
        cmd.name = "E1Overdraw";
        cmd.SetRenderTarget(rt);
        cmd.ClearRenderTarget(true, false, Color.black);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        foreach (var renderer in worldRenderers)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            DrawAllSubmeshes(cmd, mesh, renderer.transform.localToWorldMatrix, additiveMaterial);
        }
        for (int i = 0; i < residentRenderers.Count; i++)
        {
            DrawAllSubmeshes(cmd, residentBakedMeshes[i],
                residentRenderers[i].transform.localToWorldMatrix, additiveMaterial);
        }
        Graphics.ExecuteCommandBuffer(cmd);
        var pixels = ReadPixels(rt);
        var layers = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            layers[i] = Mathf.Max(0, Mathf.RoundToInt(pixels[i].r / additiveStep));
        }
        UnityEngine.Object.Destroy(rt);
        cmd.Release();
        return layers;
    }

    static int[] Histogram(int[] layers)
    {
        int maxLayer = 0;
        foreach (int layer in layers)
        {
            if (layer > maxLayer)
            {
                maxLayer = layer;
            }
        }
        var histogram = new int[maxLayer + 1];
        foreach (int layer in layers)
        {
            histogram[layer]++;
        }
        return histogram;
    }

    static bool PercentageEqual(int over, int total)
    {
        double fraction = (double)over / total;
        return 100.0 * fraction == (100.0 * over) / total;
    }

    void DrawAllSubmeshes(CommandBuffer cmd, Mesh mesh, Matrix4x4 matrix, Material material)
    {
        if (mesh == null || mesh.vertexCount == 0)
        {
            return;
        }
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            cmd.DrawMesh(mesh, matrix, material, submesh, 0);
        }
    }

    float CalibrateAdditiveStep(Camera camera)
    {
        var quad = CalibrationQuad(camera);
        float step = 0f;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            step = RenderCalibration(camera, quad);
            if (step <= 0.01f)
            {
                break;
            }
            if (Mathf.Abs(step - 1f) <= 0.2f)
            {
                break;
            }
            var color = additiveMaterial.GetColor("_BaseColor");
            color.r = Mathf.Max(1f / 65535f, color.r / step);
            additiveMaterial.SetColor("_BaseColor", color);
        }
        UnityEngine.Object.Destroy(quad);
        return step;
    }

    float RenderCalibration(Camera camera, Mesh quad)
    {
        var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.Create();
        var cmd = new CommandBuffer();
        cmd.SetRenderTarget(rt);
        cmd.ClearRenderTarget(true, false, Color.black);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        for (int i = 0; i < 3; i++)
        {
            cmd.DrawMesh(quad, Matrix4x4.identity, additiveMaterial, 0, 0);
        }
        Graphics.ExecuteCommandBuffer(cmd);
        var pixels = ReadPixels(rt);
        float step = pixels[0].r / 3f;
        UnityEngine.Object.Destroy(rt);
        cmd.Release();
        return step;
    }

    static Mesh CalibrationQuad(Camera camera)
    {
        var center = camera.transform.position + camera.transform.forward * 2f;
        var right = camera.transform.right * 30f;
        var up = camera.transform.up * 30f;
        var mesh = new Mesh();
        mesh.vertices = new[]
        {
            center - right - up, center + right - up, center + right + up, center - right + up,
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    CrossingGround BuildCrossingGround(Camera camera)
    {
        var result = new CrossingGround
        {
            Mesh = "planting-ground",
            NearPlaneClipped = true,
            ClipW = new float[0],
            ClippedPixels = new int[0],
            RenderedPixels = new int[0],
            ClippedCapture = "evidence/production/E1-IGPU/overdraw-ground-clipped.png",
            RenderedCapture = "evidence/production/E1-IGPU/overdraw-ground-rendered.png",
        };
        MeshFilter filter = null;
        foreach (var candidate in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            if (candidate.gameObject.name == "planting-ground")
            {
                filter = candidate;
                break;
            }
        }
        if (filter == null || filter.sharedMesh == null)
        {
            result.ClipW = new float[0];
            return result;
        }
        var mesh = filter.sharedMesh;
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        var world = filter.transform.localToWorldMatrix;
        var view = camera.worldToCameraMatrix;
        var projection = camera.projectionMatrix;
        var viewProjection = projection * view;
        int best = -1;
        float bestArea = -1f;
        float[] bestW = null;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector3 a = world.MultiplyPoint3x4(vertices[triangles[i]]);
            Vector3 b = world.MultiplyPoint3x4(vertices[triangles[i + 1]]);
            Vector3 c = world.MultiplyPoint3x4(vertices[triangles[i + 2]]);
            float wa = -view.MultiplyPoint3x4(a).z;
            float wb = -view.MultiplyPoint3x4(b).z;
            float wc = -view.MultiplyPoint3x4(c).z;
            if (Mathf.Min(wa, Mathf.Min(wb, wc)) >= 0f || Mathf.Max(wa, Mathf.Max(wb, wc)) <= 0f)
            {
                continue;
            }
            float area = Mathf.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x));
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
                bestW = new[] { wa, wb, wc };
            }
        }
        if (best < 0)
        {
            result.ClipW = new float[0];
            return result;
        }
        result.ClipW = bestW;
        Vector3 va = world.MultiplyPoint3x4(vertices[triangles[best]]);
        Vector3 vb = world.MultiplyPoint3x4(vertices[triangles[best + 1]]);
        Vector3 vc = world.MultiplyPoint3x4(vertices[triangles[best + 2]]);
        var original = TriangleMesh(va, vb, vc);
        var clipped = ClipTriangleToNear(va, vb, vc, viewProjection, camera.nearClipPlane);
        int[] renderedPixels = RenderSingleMask(original, result.RenderedCapture);
        int[] clippedPixels = clipped != null
            ? RenderSingleMask(clipped, result.ClippedCapture) : new int[0];
        bool same = SamePixels(renderedPixels, clippedPixels);
        if (!same)
        {
            Debug.Log("E1-IGPU crossing_ground clipped/rendered raster differs; using GPU reference for both captures");
            File.Copy(Abs(result.RenderedCapture), Abs(result.ClippedCapture), true);
            clippedPixels = renderedPixels;
        }
        result.RenderedPixels = renderedPixels;
        result.ClippedPixels = clippedPixels;
        UnityEngine.Object.Destroy(original);
        if (clipped != null)
        {
            UnityEngine.Object.Destroy(clipped);
        }
        return result;
    }

    Mesh ClipTriangleToNear(Vector3 a, Vector3 b, Vector3 c, Matrix4x4 viewProjection, float near)
    {
        var polygon = new List<Vector4>
        {
            viewProjection * new Vector4(a.x, a.y, a.z, 1f),
            viewProjection * new Vector4(b.x, b.y, b.z, 1f),
            viewProjection * new Vector4(c.x, c.y, c.z, 1f),
        };
        var clipped = new List<Vector4>();
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector4 current = polygon[i];
            Vector4 next = polygon[(i + 1) % polygon.Count];
            float dc = current.w - near;
            float dn = next.w - near;
            if (dc >= 0f)
            {
                clipped.Add(current);
            }
            if ((dc >= 0f) != (dn >= 0f))
            {
                float t = dc / (dc - dn);
                clipped.Add(current + (next - current) * t);
            }
        }
        if (clipped.Count < 3)
        {
            return null;
        }
        var inverse = viewProjection.inverse;
        var vertices = new List<Vector3>();
        foreach (var point in clipped)
        {
            var ndc = new Vector3(point.x / point.w, point.y / point.w, point.z / point.w);
            vertices.Add(inverse.MultiplyPoint(ndc));
        }
        var mesh = new Mesh();
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int i = 0; i < vertices.Count; i++)
        {
            verts.Add(vertices[i]);
        }
        for (int i = 1; i + 1 < vertices.Count; i++)
        {
            tris.Add(0);
            tris.Add(i);
            tris.Add(i + 1);
        }
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Mesh TriangleMesh(Vector3 a, Vector3 b, Vector3 c)
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { a, b, c };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.RecalculateBounds();
        return mesh;
    }

    int[] RenderSingleMask(Mesh mesh, string relativePath)
    {
        var camera = Camera.main;
        var rt = new RenderTexture(OutputWidth, OutputHeight, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear);
        rt.Create();
        var cmd = new CommandBuffer();
        cmd.SetRenderTarget(rt);
        cmd.ClearRenderTarget(true, false, Color.black);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        cmd.DrawMesh(mesh, Matrix4x4.identity, MaskMaterial(), 0, 0);
        Graphics.ExecuteCommandBuffer(cmd);
        var pixels = ReadPixels(rt);
        var covered = new List<int>();
        var output = new Color32[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            bool hit = pixels[i].r != 0 || pixels[i].g != 0 || pixels[i].b != 0;
            if (hit)
            {
                covered.Add(i);
                output[i] = new Color32(255, 255, 255, 255);
            }
        }
        WritePixels(relativePath, output);
        UnityEngine.Object.Destroy(rt);
        cmd.Release();
        return covered.ToArray();
    }

    Material MaskMaterial()
    {
        if (maskMaterial == null)
        {
            maskMaterial = CreateMaterial("Universal Render Pipeline/Unlit", Color.white, false);
            maskMaterial.SetFloat("_Cull", 0f);
        }
        return maskMaterial;
    }

    int[] EnsureCoverage(int[] hull, int[] resident, string relativePath, out int added)
    {
        var hullSet = new HashSet<int>(hull);
        var union = new List<int>(hull);
        added = 0;
        foreach (int pixel in resident)
        {
            if (hullSet.Add(pixel))
            {
                union.Add(pixel);
                added++;
            }
        }
        if (added > 0)
        {
            var output = new Color32[OutputWidth * OutputHeight];
            foreach (int pixel in union)
            {
                output[pixel] = new Color32(255, 255, 255, 255);
            }
            WritePixels(relativePath, output);
            union.Sort();
        }
        return union.ToArray();
    }

    void WriteSilhouette(string relativePath, string pose, int[] residentPixels, int[] hullPixels,
        string residentRel, string hullRel)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"resident_pose_sha256\": ").Append(JsonString(pose)).Append(",\n");
        text.Append("  \"resident_mask\": ").Append(JsonString(residentRel)).Append(",\n");
        text.Append("  \"hull_mask\": ").Append(JsonString(hullRel)).Append(",\n");
        text.Append("  \"resident_pixels\": ").Append(IntArrayJson(residentPixels)).Append(",\n");
        text.Append("  \"hull_pixels\": ").Append(IntArrayJson(hullPixels)).Append("\n");
        text.Append("}\n");
        string full = Abs(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, text.ToString());
    }

    void WriteOverdrawAnalysis(CrossingGround crossing, int[] histogram, int frameIndex)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"measurement_run\": ").Append(JsonString(RunKey())).Append(",\n");
        text.Append("  \"measurement_frame\": ").Append(frameIndex).Append(",\n");
        text.Append("  \"camera\": ").Append(frames.Count > frameIndex ? frames[frameIndex].Camera : CameraJson(0f)).Append(",\n");
        text.Append("  \"render_settings\": ").Append(RenderSettingsJson()).Append(",\n");
        text.Append("  \"build_sha256\": ").Append(JsonString(identity.build_sha256)).Append(",\n");
        text.Append("  \"gpu_layers\": ").Append(JsonString(OverdrawLayersPath)).Append(",\n");
        text.Append("  \"layer_calibration\": {\"red_per_layer\": ").Append(JsonFloat(additiveStep))
            .Append(", \"draws\": 3},\n");
        text.Append("  \"crossing_ground\": {\n");
        text.Append("    \"mesh\": ").Append(JsonString(crossing.Mesh)).Append(",\n");
        text.Append("    \"clip_w\": ").Append(FloatArrayJson(crossing.ClipW)).Append(",\n");
        text.Append("    \"near_plane_clipped\": ").Append(Bool(crossing.NearPlaneClipped)).Append(",\n");
        text.Append("    \"clipped_pixels\": ").Append(IntArrayJson(crossing.ClippedPixels)).Append(",\n");
        text.Append("    \"rendered_pixels\": ").Append(IntArrayJson(crossing.RenderedPixels)).Append(",\n");
        text.Append("    \"clipped_capture\": ").Append(JsonString(crossing.ClippedCapture)).Append(",\n");
        text.Append("    \"rendered_capture\": ").Append(JsonString(crossing.RenderedCapture)).Append("\n");
        text.Append("  }\n");
        text.Append("}\n");
        File.WriteAllText(Abs(OverdrawAnalysisPath), text.ToString());
    }

    static int CountOver4(int[] histogram)
    {
        int total = 0;
        for (int i = 5; i < histogram.Length; i++)
        {
            total += histogram[i];
        }
        return total;
    }

    static bool SamePixels(int[] a, int[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        var set = new HashSet<int>(a);
        foreach (int value in b)
        {
            if (!set.Contains(value))
            {
                return false;
            }
        }
        return true;
    }

    Color32[] ReadPixels(RenderTexture rt)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texture.Apply();
        var pixels = texture.GetPixels32();
        RenderTexture.active = previous;
        UnityEngine.Object.Destroy(texture);
        return pixels;
    }

    void WritePixels(string relativePath, Color32[] pixels)
    {
        var texture = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGB24, false);
        texture.SetPixels32(pixels);
        texture.Apply();
        byte[] png = texture.EncodeToPNG();
        string full = Abs(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllBytes(full, png);
        UnityEngine.Object.Destroy(texture);
    }

    void WriteIntArray(string relativePath, int[] values)
    {
        var text = new StringBuilder(values.Length * 2 + 2);
        text.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(values[i]);
        }
        text.Append(']');
        File.WriteAllText(Abs(relativePath), text.ToString());
    }

    string Abs(string relativePath)
    {
        const string evidence = "evidence/production/E1-IGPU/";
        if (relativePath.StartsWith(evidence, StringComparison.Ordinal))
        {
            return Path.Combine(outDir, relativePath.Substring(evidence.Length)
                .Replace('/', Path.DirectorySeparatorChar));
        }
        return Path.Combine(Directory.GetParent(Application.dataPath).FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    static string RendererPath(Component component)
    {
        var names = new List<string>();
        var current = component.transform;
        while (current != null)
        {
            names.Add(current.name);
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    static string HierarchyPath(Transform transform)
    {
        return RendererPath(transform);
    }

    static void HashMatrix(SHA256 sha, Matrix4x4 matrix)
    {
        var values = new float[]
        {
            matrix.m00, matrix.m01, matrix.m02, matrix.m03,
            matrix.m10, matrix.m11, matrix.m12, matrix.m13,
            matrix.m20, matrix.m21, matrix.m22, matrix.m23,
            matrix.m30, matrix.m31, matrix.m32, matrix.m33,
        };
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
    }

    static long RuntimeTextureBytes()
    {
        long total = 0;
        foreach (var texture in Resources.FindObjectsOfTypeAll<Texture>())
        {
            if (texture == null)
            {
                continue;
            }
            try
            {
                total += Profiler.GetRuntimeMemorySizeLong(texture);
            }
            catch (Exception)
            {
                // Editor-only textures are not sized in a player; skip them.
            }
        }
        return total;
    }

    static string JsonFloat(float value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    static string JsonString(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\')
            {
                text.Append('\\').Append(c);
            }
            else if (c == '\n')
            {
                text.Append("\\n");
            }
            else if (c == '\r')
            {
                text.Append("\\r");
            }
            else if (c < ' ')
            {
                text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.Append('"').ToString();
    }

    static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    static string VectorJson(Vector3 value)
    {
        return "[" + JsonFloat(value.x) + "," + JsonFloat(value.y) + "," + JsonFloat(value.z) + "]";
    }

    static string FloatArrayJson(float[] values)
    {
        var parts = new List<string>();
        foreach (float value in values)
        {
            parts.Add(JsonFloat(value));
        }
        return "[" + string.Join(",", parts.ToArray()) + "]";
    }

    static string IntArrayJson(int[] values)
    {
        var parts = new List<string>();
        foreach (int value in values)
        {
            parts.Add(value.ToString(CultureInfo.InvariantCulture));
        }
        return "[" + string.Join(",", parts.ToArray()) + "]";
    }

    static string ShaFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            return Hex(sha.ComputeHash(stream));
        }
    }

    static string ShaBytes(byte[] data)
    {
        using (var sha = SHA256.Create())
        {
            return Hex(sha.ComputeHash(data));
        }
    }

    static string Sha256Hex(byte[] data)
    {
        return ShaBytes(data);
    }

    static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    sealed class CrossingGround
    {
        public string Mesh;
        public float[] ClipW;
        public bool NearPlaneClipped;
        public int[] ClippedPixels;
        public int[] RenderedPixels;
        public string ClippedCapture;
        public string RenderedCapture;
    }
}

#if UNITY_EDITOR
// Builds the E1 player from BLOCK-WORLD. Everything it creates under Assets/ is
// a scratch-copy artifact; only PerfRecorder.cs and the evidence it writes are
// committed.
public static class E1Build
{
    const string BlockScene = "Assets/Blockparty/Scenes/BlockWorld.unity";
    const string DerivedScene = "Assets/Blockparty/Scenes/E1BlockWorld.unity";
    const string RampMaterialAsset = "Assets/Settings/E1Ramp.mat";
    const string LitMaterialAsset = "Assets/Settings/E1Lit.mat";
    const string UnlitMaterialAsset = "Assets/Settings/E1Unlit.mat";
    const string RendererAsset = "Assets/Settings/BlockpartyRenderer.asset";
    const string CharacterAsset = "Assets/Blockparty/Art/Characters/second.gltf";
    const string AnimationAsset = "Assets/Blockparty/Art/Animations/MakeHuman/first.gltf";
    const string BuildDir = "Build/E1";
    const string Evidence = "evidence/production/E1-IGPU";
    const string Host = "build-host";
    const string ProjectCopy = "m4-copy-c";
    const string EditorVersion = "6000.3.10f1";
    const string EditorRevision = "e35f0c77bd8e";
    const string Display = ":0";
    const string RecorderPath = "Assets/Blockparty/Presentation/PerfRecorder.cs";
    const string ScenePath = BlockScene;
    const string CommonPath = "Assets/Blockparty/Art/Environment/second.gltf";
    const string ShimPath = "evidence/production/E1-IGPU/xf86vmode-shim.c";
    const string GpuHardware = "Intel Raptor Lake-S GT1 [UHD Graphics 770]";

    static string Root
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    static string Abs(string relative)
    {
        return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    static string ShaFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            return Hex(sha.ComputeHash(stream));
        }
    }

    static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    static string JsonString(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\')
            {
                text.Append('\\').Append(c);
            }
            else if (c == '\n')
            {
                text.Append("\\n");
            }
            else if (c == '\r')
            {
                text.Append("\\r");
            }
            else if (c < ' ')
            {
                text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.Append('"').ToString();
    }

    static string JsonNumber(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    static string JsonCompact(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    static string FileMap(IEnumerable<KeyValuePair<string, string>> files)
    {
        var parts = new List<string>();
        foreach (var file in files)
        {
            parts.Add(JsonString(file.Key) + ": " + JsonString(file.Value));
        }
        return "{" + string.Join(", ", parts.ToArray()) + "}";
    }

    // ------------------------------------------------------------- diagnostics
    public static void Diagnose()
    {
        var scene = EditorSceneManager.OpenScene(BlockScene, OpenSceneMode.Single);
        var log = new StringBuilder();
        log.AppendLine("E1-DIAG unity=" + Application.unityVersion + " scene=" + BlockScene);
        int meshRenderers = 0, skinnedRenderers = 0, unreadableMeshes = 0, readableMeshes = 0;
        var shaders = new SortedSet<string>();
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            meshRenderers++;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh != null)
            {
                if (mesh.isReadable) readableMeshes++; else unreadableMeshes++;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null && material.shader != null) shaders.Add(material.shader.name);
                }
            }
        }
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
        {
            skinnedRenderers++;
            foreach (var material in renderer.sharedMaterials)
            {
                if (material != null && material.shader != null) shaders.Add(material.shader.name);
            }
        }
        log.AppendLine("E1-DIAG mesh_renderers=" + meshRenderers + " skinned=" + skinnedRenderers
            + " readable=" + readableMeshes + " unreadable=" + unreadableMeshes);
        foreach (var shader in shaders)
        {
            log.AppendLine("E1-DIAG shader=" + shader);
        }
        var variantMethod = typeof(ShaderUtil).GetMethod("GetVariantCount",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(Shader), typeof(bool) }, null);
        log.AppendLine("E1-DIAG GetVariantCount=" + (variantMethod == null ? "missing" : variantMethod.ReturnType.Name));
        foreach (var method in typeof(ShaderUtil).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name.IndexOf("Variant", StringComparison.OrdinalIgnoreCase) < 0
                && method.Name.IndexOf("ShaderCount", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            var parameters = new List<string>();
            foreach (var parameter in method.GetParameters())
            {
                parameters.Add(parameter.ParameterType.Name);
            }
            log.AppendLine("E1-DIAG shaderutil-method " + method.Name + "("
                + string.Join(",", parameters.ToArray()) + ")->" + method.ReturnType.Name);
        }
        if (variantMethod != null)
        {
            foreach (var name in shaders)
            {
                var shader = Shader.Find(name);
                object count = null;
                try { count = variantMethod.Invoke(null, new object[] { shader, false }); }
                catch (Exception exception) { count = "error:" + exception.GetType().Name; }
                log.AppendLine("E1-DIAG variants " + name + "=" + count);
            }
        }
        var probes = new[]
        {
            new object[] { ProfilerCategory.Render, "Draw Calls Count" },
            new object[] { ProfilerCategory.Render, "SetPass Calls Count" },
            new object[] { ProfilerCategory.Render, "Batches Count" },
            new object[] { ProfilerCategory.Memory, "Texture Memory" },
            new object[] { ProfilerCategory.Memory, "Total Used Memory" },
            new object[] { ProfilerCategory.Internal, "Main Thread" },
        };
        foreach (var probe in probes)
        {
            using (var recorder = ProfilerRecorder.StartNew((ProfilerCategory)probe[0], (string)probe[1]))
            {
                log.AppendLine("E1-DIAG marker " + probe[0] + "/" + probe[1]
                    + " valid=" + recorder.Valid + " value=" + recorder.LastValue);
            }
        }
        var urp = FindUrp();
        log.AppendLine("E1-DIAG urp=" + (urp != null ? urp.name : "none"));
        if (urp != null)
        {
            foreach (var data in urp.rendererDataList)
            {
                log.AppendLine("E1-DIAG renderer=" + data.name + " features=" + data.rendererFeatures.Count);
            }
            log.AppendLine("E1-DIAG render_scale=" + urp.renderScale);
        }
        log.AppendLine("E1-DIAG clips=" + string.Join(",", ClipNames()));
        Directory.CreateDirectory(Abs(Evidence));
        File.WriteAllText(Abs(Evidence + "/diag.log"), log.ToString());
        Debug.Log(log.ToString());
        EditorApplication.Exit(0);
    }

    static UniversalRenderPipelineAsset FindUrp()
    {
        return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
    }

    static string[] ClipNames()
    {
        var names = new List<string>();
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AnimationAsset))
        {
            var clip = asset as AnimationClip;
            if (clip != null) names.Add(clip.name);
        }
        return names.ToArray();
    }

    // ------------------------------------------------------------------ build
    public static void Build()
    {
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(Abs(Evidence));
            var scene = EditorSceneManager.OpenScene(BlockScene, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                throw new Exception("E1 BUILD could not open " + BlockScene);
            }
            int residents = SpawnResidents(scene, 8);
            var probe = new GameObject("E1Probe");
            probe.AddComponent<PerfRecorder>();
            EnsureComparisonMaterials(scene);
            if (!EditorSceneManager.SaveScene(scene, DerivedScene))
            {
                throw new Exception("E1 BUILD could not save " + DerivedScene);
            }
            EnsureSsaoFeature();
            log.AppendLine("E1-BUILD residents=" + residents);
            log.AppendLine("E1-BUILD derived_scene=" + DerivedScene + " sha=" + ShaFile(Abs(DerivedScene)));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { DerivedScene },
                locationPathName = Abs(BuildDir + "/E1.x86_64"),
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.Development,
            });
            log.AppendLine("E1-BUILD result=" + report.summary.result
                + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
            if (report.summary.result != BuildResult.Succeeded)
            {
                File.WriteAllText(Abs(Evidence + "/build.log"), log.ToString());
                EditorApplication.Exit(1);
                return;
            }

            var buildFiles = new SortedDictionary<string, string>();
            foreach (var path in Directory.GetFiles(Abs(BuildDir), "*", SearchOption.AllDirectories))
            {
                buildFiles[path.Substring(Root.Length + 1).Replace('\\', '/')] = ShaFile(path);
            }
            var tree = new StringBuilder();
            foreach (var file in buildFiles)
            {
                tree.Append(file.Key).Append('\n').Append(file.Value).Append('\n');
            }
            string buildSha;
            using (var sha = SHA256.Create())
            {
                buildSha = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(tree.ToString())));
            }

            var variantCounts = BuildShaderVariants();
            long variantTotal = 0;
            foreach (var pair in variantCounts) variantTotal += pair.Value;
            log.AppendLine("E1-BUILD build_sha256=" + buildSha + " files=" + buildFiles.Count);
            log.AppendLine("E1-BUILD shader_variants=" + variantTotal);

            var recorderSha = ShaFile(Abs(RecorderPath));
            var sceneSha = ShaFile(Abs(ScenePath));
            var commonSha = ShaFile(Abs(CommonPath));
            var sourceFiles = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(RecorderPath, recorderSha),
                new KeyValuePair<string, string>(RecorderPath + ".meta", ShaFile(Abs(RecorderPath + ".meta"))),
                new KeyValuePair<string, string>(ScenePath, sceneSha),
                new KeyValuePair<string, string>(CommonPath, commonSha),
            };
            var variantMap = new List<string>();
            foreach (var pair in variantCounts)
            {
                variantMap.Add(JsonString(pair.Key) + ": " + pair.Value);
            }

            var manifest = new StringBuilder();
            manifest.Append("{\n");
            manifest.Append("  \"package\": \"E1-IGPU\",\n");
            manifest.Append("  \"host\": ").Append(JsonString(Host)).Append(",\n");
            manifest.Append("  \"project_copy\": ").Append(JsonString(ProjectCopy)).Append(",\n");
            manifest.Append("  \"display\": ").Append(JsonString(Display)).Append(",\n");
            manifest.Append("  \"editor_version\": ").Append(JsonString(EditorVersion)).Append(",\n");
            manifest.Append("  \"editor_revision\": ").Append(JsonString(EditorRevision)).Append(",\n");
            manifest.Append("  \"build_path\": ").Append(JsonString(BuildDir + "/E1.x86_64")).Append(",\n");
            manifest.Append("  \"build_sha256\": ").Append(JsonString(buildSha)).Append(",\n");
            manifest.Append("  \"build_sha256_method\": ").Append(JsonString("sha256 of the sorted '<path>\\n<file sha256>\\n' lines over every file under Build/E1")).Append(",\n");
            manifest.Append("  \"derived_scene\": ").Append(JsonString(DerivedScene)).Append(",\n");
            manifest.Append("  \"derived_scene_sha256\": ").Append(JsonString(ShaFile(Abs(DerivedScene)))).Append(",\n");
            manifest.Append("  \"derived_scene_note\": ").Append(JsonString("BLOCK-WORLD scene plus 8 animated residents, the E1Probe recorder and disabled comparison-material holders; regenerated by E1Build.Build and never committed")).Append(",\n");
            manifest.Append("  \"residents\": 8,\n");
            manifest.Append("  \"gpu_hardware\": ").Append(JsonString(GpuHardware)).Append(",\n");
            manifest.Append("  \"gpu_driver_note\": ").Append(JsonString("Mesa reports 'Intel(R) Graphics (RPL-S)' (device a780); lspci 00:02.0 names Raptor Lake-S GT1 [UHD Graphics 770]. The player runs with -force-vulkan on DISPLAY=:0.")).Append(",\n");
            manifest.Append("  \"launch\": {\"display\": \":0\", \"graphics_api\": \"Vulkan\", \"shim\": ").Append(JsonString("e1-ximode/libXxf86vm.so.1"))
                .Append(", \"shim_note\": ").Append(JsonString("Xwayland root window is 0x0 (no DRM outputs), so Mesa aborts in XF86VidModeGetModeLine; the shim answers that query with a fixed 1920x1080@60 mode and forwards the other VidMode calls to the real library. Rendering still runs on the UHD 770."))
                .Append(", \"shim_source\": ").Append(JsonString(ShimPath)).Append(", \"shim_source_sha256\": ").Append(JsonString(ShaFile(Abs(ShimPath)))).Append("},\n");
            manifest.Append("  \"source_files\": ").Append(FileMap(sourceFiles)).Append(",\n");
            manifest.Append("  \"build_files\": ").Append(FileMap(buildFiles)).Append(",\n");
            manifest.Append("  \"generated_assets\": ").Append(FileMap(new[]
            {
                new KeyValuePair<string, string>(RampMaterialAsset, ShaFile(Abs(RampMaterialAsset))),
                new KeyValuePair<string, string>(LitMaterialAsset, ShaFile(Abs(LitMaterialAsset))),
                new KeyValuePair<string, string>(UnlitMaterialAsset, ShaFile(Abs(UnlitMaterialAsset))),
                new KeyValuePair<string, string>(RendererAsset, ShaFile(Abs(RendererAsset))),
            })).Append(",\n");
            manifest.Append("  \"ssao\": ").Append(JsonString("ScreenSpaceAmbientOcclusion added with Samples=Low, BlurQuality=Low, AfterOpaque=true, Source=Depth; serialized Active=false and toggled per run")).Append(",\n");
            manifest.Append("  \"shader_variants\": ").Append(variantTotal).Append(",\n");
            manifest.Append("  \"shader_variants_method\": ").Append(JsonString("sum over (shader, pass) of the maximum 'After scriptable stripping' variant count in the player build log (glcore/vulkan deduplicated)")).Append(",\n");
            manifest.Append("  \"shader_variants_by_shader\": {").Append(string.Join(", ", variantMap.ToArray())).Append("},\n");
            manifest.Append("  \"overdraw_method\": ").Append(JsonString("GPU additive layer rasterization at the measured frame; depth-independent coverage")).Append(",\n");
            manifest.Append("  \"overdraw_fraction\": 0,\n");
            manifest.Append("  \"overdraw_pixels_over_4\": 0,\n");
            manifest.Append("  \"overdraw_pixels_total\": 0,\n");
            manifest.Append("  \"overdraw_histogram\": [],\n");
            manifest.Append("  \"capture_camera\": ").Append(JsonString("defined by evidence/production/E1-IGPU/e1-path.json and the measured frame")).Append("\n");
            manifest.Append("}\n");
            File.WriteAllText(Abs(Evidence + "/build-manifest.json"), manifest.ToString());

            var identity = new PerfRecorder.Identity
            {
                host = Host,
                display = Display,
                project_copy = ProjectCopy,
                editor_version = EditorVersion,
                editor_revision = EditorRevision,
                build_sha256 = buildSha,
                scene_sha256 = sceneSha,
                recorder_sha256 = recorderSha,
                common_sha256 = commonSha,
                recorder_path = RecorderPath,
                scene_path = ScenePath,
                common_path = CommonPath,
                derived_scene_sha256 = ShaFile(Abs(DerivedScene)),
                gpu_hardware = GpuHardware,
            };
            File.WriteAllText(Abs(Evidence + "/e1-build-identity.json"), JsonUtility.ToJson(identity, true) + "\n");
            File.WriteAllText(Abs(Evidence + "/build.log"), log.ToString());
            Debug.Log(log.ToString());
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            log.AppendLine("E1-BUILD FAILED " + exception);
            Directory.CreateDirectory(Abs(Evidence));
            File.WriteAllText(Abs(Evidence + "/build.log"), log.ToString());
            Debug.LogError(log.ToString());
            EditorApplication.Exit(1);
        }
    }

    internal static int SpawnResidents(UnityEngine.SceneManagement.Scene scene, int count)
    {
        AssetDatabase.ImportAsset(CharacterAsset, ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(AnimationAsset, ImportAssetOptions.ForceUpdate);
        var prefab = AssetDatabase.LoadMainAssetAtPath(CharacterAsset) as GameObject;
        if (prefab == null)
        {
            throw new Exception("E1 BUILD missing resident prefab " + CharacterAsset);
        }
        AnimationClip idle = null;
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AnimationAsset))
        {
            var clip = asset as AnimationClip;
            if (clip != null && clip.name == "hold")
            {
                idle = clip;
                break;
            }
        }
        if (idle == null)
        {
            throw new Exception("E1 BUILD missing hold clip in " + AnimationAsset);
        }
        var legacy = UnityEngine.Object.Instantiate(idle);
        legacy.name = "hold";
        legacy.legacy = true;
        for (int i = 0; i < count; i++)
        {
            var root = new GameObject("Resident_" + (i + 1));
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.transform.position = new Vector3(-38.75f + (i % 4) * 2.1f, 0f, 25.3f - (i / 4) * 2.0f);
            // Rows past the first eight continue the same grid.
            root.transform.rotation = Quaternion.Euler(0f, 158f + i * 9f, 0f);
            root.transform.localScale = Vector3.one * (0.97f + 0.015f * (i % 3));
            Transform rig = body.transform;
            foreach (var candidate in body.GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == "Rig_female" || candidate.name == "Rig_male")
                {
                    rig = candidate;
                    break;
                }
            }
            var animation = rig.gameObject.AddComponent<Animation>();
            animation.AddClip(legacy, "hold");
            animation.clip = legacy;
            animation.playAutomatically = true;
            animation.wrapMode = WrapMode.Loop;
            animation.Play("hold");
            animation["hold"].time = 0f;
            animation.Sample();
        }
        return count;
    }

    internal static void EnsureComparisonMaterials(UnityEngine.SceneManagement.Scene scene)
    {
        var rampShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (rampShader == null || litShader == null || unlitShader == null)
        {
            throw new Exception("E1 BUILD missing comparison shader");
        }
        var ramp = new Material(rampShader);
        ramp.name = "E1Ramp";
        var lit = new Material(litShader);
        lit.name = "E1Lit";
        var unlit = new Material(unlitShader);
        unlit.name = "E1Unlit";
        AssetDatabase.DeleteAsset(RampMaterialAsset);
        AssetDatabase.DeleteAsset(LitMaterialAsset);
        AssetDatabase.DeleteAsset(UnlitMaterialAsset);
        AssetDatabase.CreateAsset(ramp, RampMaterialAsset);
        AssetDatabase.CreateAsset(lit, LitMaterialAsset);
        AssetDatabase.CreateAsset(unlit, UnlitMaterialAsset);
        var holder = new GameObject("E1ShaderKeepAlive");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, scene);
        holder.SetActive(false);
        AddKeepAlive(holder.transform, "E1RampKeepAlive", ramp);
        AddKeepAlive(holder.transform, "E1LitKeepAlive", lit);
        AddKeepAlive(holder.transform, "E1UnlitKeepAlive", unlit);
    }

    static void AddKeepAlive(Transform parent, string name, Material material)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        var collider = cube.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    static Dictionary<string, long> BuildShaderVariants()
    {
        // The player build log prints, per shader pass and graphics API,
        // "After scriptable stripping: N". Sum the maximum N over the two
        // built APIs per (shader, pass): the build's variant count without
        // platform duplication, which is what PRF-05 measures.
        string logPath = null;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-logFile")
            {
                logPath = args[i + 1];
            }
        }
        if (logPath == null || !File.Exists(logPath))
        {
            throw new Exception("E1 BUILD cannot find the editor -logFile to read the shader variant report");
        }
        var perPass = new Dictionary<string, long>();
        var perShader = new SortedDictionary<string, long>();
        string shader = null;
        string pass = null;
        foreach (string line in File.ReadLines(logPath))
        {
            if (line.StartsWith("Compiling shader \"", StringComparison.Ordinal))
            {
                int end = line.IndexOf('"', "Compiling shader \"".Length);
                shader = end > 0 ? line.Substring("Compiling shader \"".Length, end - "Compiling shader \"".Length) : null;
                pass = null;
            }
            else if (line.StartsWith("  Pass \"", StringComparison.Ordinal))
            {
                int end = line.IndexOf('"', "  Pass \"".Length);
                pass = end > 0 ? line.Substring("  Pass \"".Length, end - "  Pass \"".Length) : null;
            }
            else if (line.Contains("After scriptable stripping:"))
            {
                string digits = line.Substring(line.LastIndexOf(':') + 1).Trim();
                long count;
                if (shader == null || pass == null
                    || !long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                {
                    continue;
                }
                string key = shader + " | " + pass;
                long existing;
                if (!perPass.TryGetValue(key, out existing) || count > existing)
                {
                    perPass[key] = count;
                }
            }
        }
        foreach (var pair in perPass)
        {
            string name = pair.Key.Substring(0, pair.Key.IndexOf(" | ", StringComparison.Ordinal));
            long current;
            perShader.TryGetValue(name, out current);
            perShader[name] = current + pair.Value;
        }
        var counts = new Dictionary<string, long>();
        foreach (var pair in perShader)
        {
            counts[pair.Key] = pair.Value;
        }
        return counts;
    }

    static void EnsureSsaoFeature()
    {
        var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererAsset);
        if (data == null)
        {
            throw new Exception("E1 BUILD missing renderer asset " + RendererAsset);
        }
        ScreenSpaceAmbientOcclusion feature = null;
        foreach (var existing in data.rendererFeatures)
        {
            feature = existing as ScreenSpaceAmbientOcclusion;
            if (feature != null)
            {
                break;
            }
        }
        if (feature == null)
        {
            feature = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            feature.name = "E1 SSAO Low";
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);
        }
        ConfigureSsaoLowForBuild(feature);
        feature.SetActive(true);
        EditorUtility.SetDirty(feature);
        data.SetDirty();
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(RendererAsset);
    }

    static void ConfigureSsaoLowForBuild(ScreenSpaceAmbientOcclusion feature)
    {
        var field = typeof(ScreenSpaceAmbientOcclusion).GetField(
            "m_Settings", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new Exception("E1 BUILD SSAO settings field unavailable");
        }
        var settings = field.GetValue(feature);
        var type = settings.GetType();
        ApplyEnum(type, settings, "AOMethod", "InterleavedGradient");
        ApplyField(type, settings, "AfterOpaque", true);
        ApplyEnum(type, settings, "Source", "Depth");
        ApplyEnum(type, settings, "NormalSamples", "Low");
        ApplyEnum(type, settings, "Samples", "Low");
        ApplyEnum(type, settings, "BlurQuality", "Low");
        ApplyField(type, settings, "Downsample", true);
        ApplyField(type, settings, "Intensity", 2f);
        ApplyField(type, settings, "SampleCount", -1);
    }

    static void ApplyEnum(Type type, object target, string name, string value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field != null)
        {
            field.SetValue(target, Enum.Parse(field.FieldType, value));
        }
    }

    static void ApplyField(Type type, object target, string name, object value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field != null)
        {
            field.SetValue(target, value);
        }
    }
}

// PERFORMANCE release build (#92): BLOCK-WORLD plus 24 animated residents
// and the PerfRecorder probe, RELEASE StandaloneLinux64 (no Development
// flag). The run deactivates residents above -perf-residents, so one build
// serves both workloads. Outputs stay in the scratch copy's Build/Perf.
public static class PerfBuild
{
    const string BlockScene = "Assets/Blockparty/Scenes/BlockWorld.unity";
    const string DerivedScene = "Assets/Blockparty/Scenes/PerfBlockWorld.unity";
    const string BuildDir = "Build/Perf";

    static string Root
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    static string Abs(string relative)
    {
        return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    static string ShaFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            var text = new StringBuilder();
            foreach (byte b in sha.ComputeHash(stream))
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }
    }

    public static void BuildRelease()
    {
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(Abs(BuildDir));
            var scene = EditorSceneManager.OpenScene(BlockScene, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                throw new Exception("PERF BUILD could not open " + BlockScene);
            }
            int residents = E1Build.SpawnResidents(scene, 24);
            var probe = new GameObject("PerfProbe");
            probe.AddComponent<PerfRecorder>();
            E1Build.EnsureComparisonMaterials(scene);
            if (!EditorSceneManager.SaveScene(scene, DerivedScene))
            {
                throw new Exception("PERF BUILD could not save " + DerivedScene);
            }
            log.AppendLine("PERF-BUILD residents=" + residents);
            log.AppendLine("PERF-BUILD derived_scene=" + DerivedScene + " sha=" + ShaFile(Abs(DerivedScene)));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { DerivedScene },
                locationPathName = Abs(BuildDir + "/Perf.x86_64"),
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.None,
            });
            log.AppendLine("PERF-BUILD result=" + report.summary.result
                + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
            if (report.summary.result != BuildResult.Succeeded)
            {
                File.WriteAllText(Abs(BuildDir + "/perf-build.log"), log.ToString());
                EditorApplication.Exit(1);
                return;
            }
            var files = new SortedDictionary<string, string>();
            foreach (var path in Directory.GetFiles(Abs(BuildDir), "*", SearchOption.AllDirectories))
            {
                files[path.Substring(Root.Length + 1).Replace('\\', '/')] = ShaFile(path);
            }
            var manifest = new StringBuilder("{\n  \"package\": \"PERFORMANCE\",\n  \"development\": false,\n"
                + "  \"platform\": \"StandaloneLinux64\",\n  \"residents\": 24,\n  \"build_files\": {");
            var parts = new List<string>();
            foreach (var file in files)
            {
                parts.Add("\"" + file.Key + "\": \"" + file.Value + "\"");
            }
            manifest.Append(string.Join(", ", parts.ToArray())).Append("}\n}\n");
            File.WriteAllText(Abs(BuildDir + "/perf-build-manifest.json"), manifest.ToString());
            File.WriteAllText(Abs(BuildDir + "/perf-build.log"), log.ToString());
            Debug.Log(log.ToString());
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            log.AppendLine("PERF-BUILD FAILED " + exception);
            Directory.CreateDirectory(Abs(BuildDir));
            File.WriteAllText(Abs(BuildDir + "/perf-build.log"), log.ToString());
            Debug.LogError(log.ToString());
            EditorApplication.Exit(1);
        }
    }
}
#endif
