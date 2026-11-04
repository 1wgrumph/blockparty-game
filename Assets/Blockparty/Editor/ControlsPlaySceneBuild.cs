using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// CONTROLS-PLAY editor entry points, invoked headless on the i7:
//   Unity -batchmode -projectPath . -executeMethod ControlsPlaySceneBuild.BuildScene -quit
//   Unity -batchmode -projectPath . -executeMethod ControlsPlaySceneBuild.VerifyScene -quit
//   Unity -batchmode -projectPath . -executeMethod ControlsPlaySceneBuild.BuildLinux -quit
// The scene is the landed GREYBOX EightResident layout with its sim director
// removed and the controls-play director added, so the package runs on the
// same greybox content without editing EightResident.unity.
public static class ControlsPlaySceneBuild
{
    public const string SourceScene = "Assets/Blockparty/Scenes/EightResident.unity";
    public const string ScenePath = "Assets/Blockparty/Scenes/ControlsPlay.unity";
    public const string BuildPath = "Build/ControlsPlay/ControlsPlay.x86_64";
    public const string EvidenceDir = "evidence/production/CONTROLS-PLAY";

    public static void BuildScene()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        if (!scene.IsValid()) throw new Exception("CONTROLS_PLAY_SCENE source missing " + SourceScene);
        GameObject world = GameObject.Find("GreyboxWorld");
        if (world == null) throw new Exception("CONTROLS_PLAY_SCENE GreyboxWorld missing");
        GreyboxDirector grey = world.GetComponent<GreyboxDirector>();
        if (grey != null) UnityEngine.Object.DestroyImmediate(grey);
        GameObject previous = GameObject.Find("ControlsPlayDirector");
        if (previous != null) UnityEngine.Object.DestroyImmediate(previous);
        GameObject director = new GameObject("ControlsPlayDirector");
        director.AddComponent<ControlsPlayDirector>();
        SceneManager.MoveGameObjectToScene(director, scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("CONTROLS_PLAY_SCENE saved=" + ScenePath);
    }

    public static void VerifyScene()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int failures = 0;
        Check(scene.IsValid(), "scene-opens", ref failures);
        Check(GameObject.Find("GreyboxWorld") != null, "greybox-world", ref failures);
        Check(GameObject.Find("GreyboxCamera") != null, "camera", ref failures);
        Check(GameObject.Find("ControlsPlayDirector") != null, "director", ref failures);
        GameObject world = GameObject.Find("GreyboxWorld");
        Check(world == null || world.GetComponent<GreyboxDirector>() == null, "greybox-director-removed", ref failures);
        for (int i = 1; i <= 8; i++)
            Check(GameObject.Find("Resident_" + i) != null, "resident-" + i, ref failures);
        for (int i = 1; i <= 8; i++)
            Check(GameObject.Find(i <= 4 ? "StationA" + i : "StationB" + (i - 4)) != null,
                "station-" + i, ref failures);
        Debug.Log("CONTROLS_PLAY_VERIFY failures=" + failures);
        if (failures > 0) throw new Exception("CONTROLS_PLAY_VERIFY_FAILED");
    }

    static void Check(bool ok, string name, ref int failures)
    {
        Debug.Log("CONTROLS_PLAY_VERIFY " + (ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    public static void BuildLinux()
    {
        string tree = Environment.GetEnvironmentVariable("BLOCKPARTY_SOURCE_TREE");
        if (string.IsNullOrEmpty(tree) || tree.Length != 40)
            throw new Exception("Build manifest requires BLOCKPARTY_SOURCE_TREE (40-hex source tree)");
        Directory.CreateDirectory(EvidenceDir);
        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = BuildPath,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        };
        BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
        bool ok = summary.result == BuildResult.Succeeded;
        string binarySha = ok && File.Exists(BuildPath) ? Sha256(BuildPath) : string.Empty;
        string payload = ok ? PayloadJson(Path.GetDirectoryName(BuildPath)) : "[]";
        string logJson = "{\"tree_hash\":\"" + tree + "\",\"binary_sha256\":\"" + binarySha
            + "\",\"exit_code\":" + (ok ? 0 : 1)
            + ",\"method\":\"BuildLinux\",\"target\":\"StandaloneLinux64\""
            + ",\"built_utc\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            + "\",\"errors\":" + summary.totalErrors
            + ",\"size\":" + summary.totalSize
            + ",\"files\":" + payload + "}";
        string logPath = EvidenceDir + "/build-log.json";
        File.WriteAllText(logPath, logJson);
        Debug.Log("CONTROLS_PLAY_BUILD result=" + summary.result + " errors=" + summary.totalErrors
            + " size=" + summary.totalSize + " sha256=" + binarySha);
        if (!ok) throw new Exception("CONTROLS_PLAY_BUILD_FAILED result=" + summary.result);
        string buildJson = "{\"tree_hash\":\"" + tree + "\",\"binary_sha256\":\"" + binarySha
            + "\",\"method\":\"BuildLinux\",\"target\":\"StandaloneLinux64\",\"exit_code\":0"
            + ",\"log\":{\"path\":\"" + logPath + "\",\"sha256\":\"" + Sha256(logPath) + "\"}"
            + ",\"built_utc\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            + "\"}";
        File.WriteAllText(EvidenceDir + "/build.json", buildJson);
    }

    static string PayloadJson(string root)
    {
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) sb.Append(',');
            string relative = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            sb.Append("{\"relative_path\":\"").Append(relative)
              .Append("\",\"sha256\":\"").Append(Sha256(files[i])).Append("\"}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    static string Sha256(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
