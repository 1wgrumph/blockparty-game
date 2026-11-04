using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.UI;
using UnityEngine;

// CONTENT-IMPORT@1 play view of Main.unity (player and editor play mode). Frames the
// household from the courtyard, shows each started flow's ANIM cue caption through
// Accessibility.CaptionFor, and drifts the camera slowly around the household unless
// reduced motion is on (ACC-03). C toggles captions, M toggles reduced motion.
// Player arguments: -captions on|off, -reduced-motion on|off, -play-seconds N (quit
// after N s), -capture-dir D with -capture name@seconds[,name@seconds...] (measured
// captures through FrameCapture: screen beauty frame + render masks + sidecar), and
// -tree-hash H (the build's input hash for the sidecars), -window WxH (windowed size) and
// -record-dir D (the shown frames as D/<n>.jpg at RecordFps, encoded off the main thread;
// Xwayland cannot read the player window from outside). Each second it logs a
// CONTENT_IMPORT_LIVE row (every resident's flow, phase, clip and position).
// No statics: a Fast Enter Play Mode re-entry starts from the serialized scene.
public sealed class MainPlay : MonoBehaviour
{
    public MainDirector director;
    // The courtyard view (MainSceneBuild CloseCamera) and its drift.
    public Vector3 viewOffset = new Vector3(-2.2f, 1.7f, -4.6f);
    public float viewHeight = 0.75f, viewFov = 45f, driftDegrees = 12f, driftPeriod = 30f;

    Accessibility access;
    float started = -1f, nextLive;
    float playSeconds = -1f;
    string captureDir, recordDir, treeHash = "";
    const float RecordFps = 10f;
    int recorded, writing, liveFrame, windowWidth, windowHeight;
    readonly List<KeyValuePair<string, float>> captures = new List<KeyValuePair<string, float>>();
    readonly Dictionary<GameObject, FrameCapture.Subject> subjects = new Dictionary<GameObject, FrameCapture.Subject>();
    GUIStyle captionStyle, statusStyle;

    public Accessibility Access { get { return access; } }

    void Awake()
    {
        if (director == null) director = GetComponent<MainDirector>();
        // Defaults as a new save's settings record: captions on, reduced motion off.
        access = new Accessibility(new SettingsRecord(SaveV1.SettingsSchemaVersion, 1f, 1f, false));
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "-captions": access.SetCaptions(value == "on"); break;
                case "-reduced-motion": access.SetReducedMotion(value == "on"); break;
                case "-play-seconds": playSeconds = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "-capture-dir": captureDir = value; break;
                case "-tree-hash": treeHash = value; break;
                case "-record-dir": recordDir = value; Directory.CreateDirectory(value); break;
                case "-window":
                    var size = value.Split('x');
                    windowWidth = int.Parse(size[0], CultureInfo.InvariantCulture);
                    windowHeight = int.Parse(size[1], CultureInfo.InvariantCulture);
                    Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
                    break;
                case "-capture":
                    foreach (string item in value.Split(','))
                    {
                        var bits = item.Split('@');
                        captures.Add(new KeyValuePair<string, float>(bits[0], float.Parse(bits[1], CultureInfo.InvariantCulture)));
                    }
                    break;
            }
        }
        // A timed run keeps playing while its window is not focused, and does not wait on vsync: the
        // i7 desktop session throttles presentation to about 3 fps (E1-IGPU also ran with vsync off).
        if (playSeconds > 0f)
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
        }
        Debug.Log("CONTENT_IMPORT_PLAY settings captions=" + access.Settings.Captions + " reduced_motion=" + access.Settings.ReducedMotion
            + " play_seconds=" + playSeconds + " captures=" + captures.Count);
    }

    void Update()
    {
        if (director == null || director.World == null) return;
        // Play time starts once a requested window size has taken effect (it lands a few frames late).
        if (started < 0f && windowWidth > 0 && (Screen.width != windowWidth || Screen.height != windowHeight)) return;
        if (started < 0f) { started = Time.time; nextLive = 0f; Debug.Log("CONTENT_IMPORT_PLAY ready residents=" + director.residentRoot.childCount); }
        float t = Time.time - started;
        if (Input.GetKeyDown(KeyCode.C)) access.SetCaptions(!access.Settings.Captions);
        if (Input.GetKeyDown(KeyCode.M)) access.SetReducedMotion(!access.Settings.ReducedMotion);
        Frame(t);
        if (t >= nextLive) { Live(t); nextLive += 1f; }
        if (recordDir != null && (playSeconds < 0f || t <= playSeconds) && t >= recorded / RecordFps) StartCoroutine(Record(recorded++, t));
        for (int i = captures.Count - 1; i >= 0; i--)
            if (t >= captures[i].Value)
            {
                StartCoroutine(Shoot(captures[i].Key, t));
                captures.RemoveAt(i);
            }
        if (playSeconds > 0f && t >= playSeconds && captures.Count == 0 && Volatile.Read(ref writing) == 0)
        {
            Debug.Log("CONTENT_IMPORT_PLAY done seconds=" + t.ToString("F2", CultureInfo.InvariantCulture));
            playSeconds = -1f;
            Application.Quit();
        }
    }

    // Reduced motion holds the view still (no drift); otherwise it sways driftDegrees around the household.
    void Frame(float t)
    {
        var camera = Camera.main;
        if (camera == null) return;
        float yaw = access.Settings.ReducedMotion ? 0f : driftDegrees * Mathf.Sin(2f * Mathf.PI * t / driftPeriod);
        var centre = director.courtyard;
        camera.transform.position = centre + Quaternion.AngleAxis(yaw, Vector3.up) * viewOffset;
        camera.transform.LookAt(centre + Vector3.up * viewHeight);
        camera.fieldOfView = viewFov;
    }

    void Live(float t)
    {
        var rows = new List<string>();
        foreach (Transform child in director.residentRoot)
        {
            var motion = child.GetComponent<ResidentMotion>();
            if (motion == null) continue;
            var p = child.position;
            rows.Add(FrameCapture.Obj("id", FrameCapture.Q(child.name), "flow", FrameCapture.Q(motion.Flow ?? ""),
                "phase", FrameCapture.Q(motion.Phase ?? ""), "clip", FrameCapture.Q(motion.Current != null ? motion.Current.name : ""),
                "time", motion.Time.ToString("F2", CultureInfo.InvariantCulture),
                "position", FrameCapture.Floats(new[] { p.x, p.y, p.z }), "active", child.gameObject.activeInHierarchy ? "true" : "false"));
        }
        int frames = Time.frameCount - liveFrame;
        liveFrame = Time.frameCount;
        Debug.Log("CONTENT_IMPORT_LIVE " + FrameCapture.Obj("seconds", t.ToString("F2", CultureInfo.InvariantCulture),
            "frames", frames.ToString(CultureInfo.InvariantCulture),
            "captions", access.Settings.Captions ? "true" : "false", "reduced_motion", access.Settings.ReducedMotion ? "true" : "false",
            "cues", FrameCapture.Strings(director.Cues.ToArray()), "residents", "[" + string.Join(",", rows) + "]"));
    }

    // One measured capture of what the player shows (captions included): the screen at the end of
    // the frame, then the same frame's render masks from the main camera.
    IEnumerator Shoot(string name, float t)
    {
        yield return new WaitForEndOfFrame();
        if (string.IsNullOrEmpty(captureDir)) yield break;
        Directory.CreateDirectory(Path.Combine(captureDir, "masks"));
        var shot = ScreenCapture.CaptureScreenshotAsTexture();
        int w = shot.width, h = shot.height;
        FrameCapture.WritePng(Path.Combine(captureDir, name + ".png"), shot.GetPixels32(), w, h);
        Destroy(shot);
        var list = FrameCapture.HouseholdSubjects(director);
        foreach (var s in list)
        {
            FrameCapture.Subject old;
            if (subjects.TryGetValue(s.body, out old)) s.parts = old.parts;
            subjects[s.body] = s;
        }
        var camera = Camera.main;
        FrameCapture.Measure(captureDir, name, camera, w, h, list, FrameCapture.Q("camera") + ":" + FrameCapture.CameraJson(camera),
            "noon", treeHash, Environment.GetCommandLineArgs(), "MainPlay player capture at " + t.ToString("F2", CultureInfo.InvariantCulture)
            + " s (ScreenCapture beauty)");
        Debug.Log("CONTENT_IMPORT_CAPTURE " + name + " seconds=" + t.ToString("F2", CultureInfo.InvariantCulture) + " size=" + w + "x" + h);
    }

    IEnumerator Record(int n, float t)
    {
        yield return new WaitForEndOfFrame();
        var shot = ScreenCapture.CaptureScreenshotAsTexture();
        byte[] raw = shot.GetRawTextureData();
        var format = shot.graphicsFormat;
        uint w = (uint)shot.width, h = (uint)shot.height;
        Destroy(shot);
        string path = Path.Combine(recordDir, n.ToString("D4", CultureInfo.InvariantCulture) + ".jpg");
        Interlocked.Increment(ref writing);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { File.WriteAllBytes(path, ImageConversion.EncodeArrayToJPG(raw, format, w, h, 0, 92)); }
            finally { Interlocked.Decrement(ref writing); }
        });
        Debug.Log("CONTENT_IMPORT_FRAME " + n + " " + t.ToString("F3", CultureInfo.InvariantCulture));
    }

    void OnGUI()
    {
        if (director == null || director.World == null) return;
        if (captionStyle == null)
        {
            // White on a 90% near-black panel (ACC-02: well above 4.5:1 over any background).
            var panel = new Texture2D(1, 1);
            panel.SetPixel(0, 0, new Color(0.04f, 0.04f, 0.05f, 0.9f));
            panel.Apply();
            captionStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            captionStyle.normal.background = panel;
            captionStyle.normal.textColor = Color.white;
            captionStyle.padding = new RectOffset(16, 16, 8, 8);
            statusStyle = new GUIStyle(captionStyle) { alignment = TextAnchor.MiddleLeft };
        }
        captionStyle.fontSize = Mathf.RoundToInt(access.ScaledTextSize(22f));
        statusStyle.fontSize = Mathf.RoundToInt(access.ScaledTextSize(16f));
        GUI.Label(new Rect(12, 12, 360, 30), "Captions " + (access.Settings.Captions ? "on" : "off") + " (C)   Reduced motion "
            + (access.Settings.ReducedMotion ? "on" : "off") + " (M)", statusStyle);
        var text = new StringBuilder();
        foreach (string cue in director.Cues)
        {
            string caption = access.CaptionFor(cue);
            if (caption.Length > 0) text.Append(text.Length > 0 ? "\n" : "").Append(caption);
        }
        if (text.Length == 0) return;
        int lines = director.Cues.Count;
        float height = 16 + lines * (captionStyle.fontSize + 8);
        GUI.Label(new Rect(Screen.width * 0.2f, Screen.height - height - 24, Screen.width * 0.6f, height), text.ToString(), captionStyle);
    }
}
