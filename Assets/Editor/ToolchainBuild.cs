using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// TOOLCHAIN evidence entry points, invoked headless once the editor is licensed:
//   Unity -batchmode -projectPath . -executeMethod ToolchainBuild.BuildLinux -quit -logFile <log>
//   Unity -projectPath . -executeMethod ToolchainBuild.PlaySmoke -logFile <log>   (needs a display for render)
public static class ToolchainBuild
{
    const string Scene = "Assets/Scenes/ToolchainProof.unity";

    public static void BuildLinux()
    {
        // ALPHA reuses this evidence entry point (the frozen receipt binds
        // "-executeMethod ToolchainBuild.BuildLinux"); the env switch selects
        // the Alpha-only scene set and its own manifest. Unset means the
        // unchanged M1 build list and manifest.
        bool alpha = Environment.GetEnvironmentVariable("BLOCKPARTY_BUILD_ALPHA") == "1";
        var options = new BuildPlayerOptions
        {
            scenes = alpha
                ? new[] { "Assets/Blockparty/Scenes/Alpha.unity" }
                : new[] { "Assets/Blockparty/Scenes/EightResident.unity", "Assets/Blockparty/Scenes/PhysicalProof.unity", "Assets/Blockparty/Scenes/VerticalSlice.unity", Scene, "Assets/Blockparty/Scenes/Alpha.unity" },
            locationPathName = alpha ? "Build/Alpha/Alpha.x86_64" : "Build/ToolchainProof/ToolchainProof.x86_64",
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        };
        BuildSummary s = BuildPipeline.BuildPlayer(options).summary;
        Debug.Log($"TOOLCHAIN_BUILD result={s.result} size={s.totalSize} errors={s.totalErrors} output={s.outputPath} duration={s.totalTime} alpha={alpha}");
        if (s.result != BuildResult.Succeeded)
            throw new Exception($"TOOLCHAIN_BUILD_FAILED result={s.result} errors={s.totalErrors}");
        // Live play reads tuning.json at Application.dataPath/Blockparty/Content (the player's _Data folder).
        string content = System.IO.Path.Combine(System.IO.Path.ChangeExtension(options.locationPathName, null) + "_Data", "Blockparty/Content");
        System.IO.Directory.CreateDirectory(content);
        System.IO.File.Copy("Assets/Blockparty/Content/tuning.json", System.IO.Path.Combine(content, "tuning.json"), true);
        string tree = Environment.GetEnvironmentVariable("BLOCKPARTY_SOURCE_TREE");
        string command = Environment.GetEnvironmentVariable("BLOCKPARTY_BUILD_COMMAND");
        if (string.IsNullOrEmpty(tree) || string.IsNullOrEmpty(command))
            throw new Exception("Build manifest requires BLOCKPARTY_SOURCE_TREE and BLOCKPARTY_BUILD_COMMAND");
        string root = System.IO.Path.GetDirectoryName(options.locationPathName);
        string[] files = System.IO.Directory.GetFiles(root, "*", System.IO.SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var json = new System.Text.StringBuilder();
        json.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"command\":\"")
            .Append(command.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\"files\":[");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) json.Append(',');
            string path = System.IO.Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = System.IO.File.OpenRead(files[i]))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            json.Append("{\"relative_path\":\"").Append(path).Append("\",\"sha256\":\"").Append(hash).Append("\"}");
        }
        json.Append("]}\n");
        string evidenceDir = alpha ? "evidence/production/ALPHA" : "evidence/production/GATE-M1";
        System.IO.Directory.CreateDirectory(evidenceDir);
        System.IO.File.WriteAllText(evidenceDir + "/build-manifest.json", json.ToString());
    }

    // Additive Alpha entry for callers that prefer a named method. The i7
    // evidence run invokes BuildLinux with BLOCKPARTY_BUILD_ALPHA=1 so the
    // tracked manifest command names the frozen receipt's entry point.
    public static void BuildAlphaLinux()
    {
        Environment.SetEnvironmentVariable("BLOCKPARTY_BUILD_ALPHA", "1");
        BuildLinux();
    }

    // Separate player used to prove the slice bootstraps without the editor
    // verification harness; BuildLinux keeps its M1 scene order.
    public static void BuildSliceLinux()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Blockparty/Scenes/VerticalSlice.unity" },
            locationPathName = "Build/Slice/Slice.x86_64",
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        };
        BuildSummary s = BuildPipeline.BuildPlayer(options).summary;
        Debug.Log($"TOOLCHAIN_SLICE_BUILD result={s.result} size={s.totalSize} errors={s.totalErrors} output={s.outputPath}");
        if (s.result != BuildResult.Succeeded)
            throw new Exception($"TOOLCHAIN_SLICE_BUILD_FAILED result={s.result} errors={s.totalErrors}");
    }

    // Distribution players (Alpha scene only), one per desktop target:
    //   Unity -batchmode -projectPath . -executeMethod ToolchainBuild.BuildDistWindows -quit -logFile <log>
    // Windows and macOS need their Mono build support modules in the editor.
    public static void BuildDistLinux() => BuildDist(BuildTarget.StandaloneLinux64, "Build/Dist/Linux/Blockparty.x86_64", "Build/Dist/Linux/Blockparty_Data");
    public static void BuildDistWindows() => BuildDist(BuildTarget.StandaloneWindows64, "Build/Dist/Windows/Blockparty.exe", "Build/Dist/Windows/Blockparty_Data");

    public static void BuildDistMac()
    {
        // Universal binary so the player runs natively on Intel and Apple silicon.
        EditorUserBuildSettings.SetPlatformSettings("OSXUniversal", "Architecture", "x64ARM64");
        // Application.dataPath on macOS is <app>/Contents.
        BuildDist(BuildTarget.StandaloneOSX, "Build/Dist/Mac/Blockparty.app", "Build/Dist/Mac/Blockparty.app/Contents");
    }

    static void BuildDist(BuildTarget target, string location, string dataPath)
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Blockparty/Scenes/Alpha.unity" },
            locationPathName = location,
            target = target,
            options = BuildOptions.None,
        };
        BuildSummary s = BuildPipeline.BuildPlayer(options).summary;
        Debug.Log($"DIST_BUILD target={target} result={s.result} size={s.totalSize} errors={s.totalErrors} output={s.outputPath} duration={s.totalTime}");
        if (s.result != BuildResult.Succeeded)
            throw new Exception($"DIST_BUILD_FAILED target={target} result={s.result} errors={s.totalErrors}");
        // Live play reads tuning.json at Application.dataPath/Blockparty/Content.
        string content = System.IO.Path.Combine(dataPath, "Blockparty/Content");
        System.IO.Directory.CreateDirectory(content);
        System.IO.File.Copy("Assets/Blockparty/Content/tuning.json", System.IO.Path.Combine(content, "tuning.json"), true);
    }

    static string _smokePng;
    const string SmokeKey = "ToolchainBuild.SmokeActive";

    // Re-subscribes after the domain reload that play-mode entry performs.
    [InitializeOnLoadMethod]
    static void ResubscribeAfterReload()
    {
        if (SessionState.GetBool(SmokeKey, false))
        {
            _smokePng = Environment.GetEnvironmentVariable("TOOLCHAIN_SMOKE_PNG")
                ?? "evidence/production/TOOLCHAIN/render.png";
            EditorApplication.playModeStateChanged += OnPlayState;
        }
    }

    public static void PlaySmoke()
    {
        Debug.Log("TOOLCHAIN_PLAY start");
        _smokePng = Environment.GetEnvironmentVariable("TOOLCHAIN_SMOKE_PNG")
            ?? "evidence/production/TOOLCHAIN/render.png";
        EditorSceneManager.OpenScene(Scene);
        SessionState.SetBool(SmokeKey, true);
        EditorApplication.playModeStateChanged += OnPlayState;
        EditorApplication.EnterPlaymode();
    }

    static void OnPlayState(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            Debug.Log($"TOOLCHAIN_PLAY entered scene={SceneManager.GetActiveScene().path}");
            ScreenCapture.CaptureScreenshot(_smokePng);
            EditorApplication.ExitPlaymode();
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            Debug.Log("TOOLCHAIN_PLAY exited");
            SessionState.EraseBool(SmokeKey);
            EditorApplication.playModeStateChanged -= OnPlayState;
            EditorApplication.Exit(0);
        }
    }

    // One-shot: silence the MCP package's first-run setup wizard (utility window,
    // harmless but noisy) by recording it dismissed. Safe to run headless.
    public static void DismissMcpSetup()
    {
        EditorPrefs.SetBool("MCPForUnity.SetupDismissed", true);
        Debug.Log("TOOLCHAIN_MCP setup wizard dismissed");
    }
}
