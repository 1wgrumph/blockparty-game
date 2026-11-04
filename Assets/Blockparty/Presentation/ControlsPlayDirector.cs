using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;

// CONTROLS-PLAY@1 running-build director. Two phases on the i7 player:
//   --controls-play=prepare : write a fresh test settings file under the
//       build's own evidence tree and snapshot it before the restart.
//   --controls-play=run     : reload that file in a second process, drive the
//       ten flows with real X11 keyboard/mouse events, capture 1920x1080
//       frames, and write controls.json and its bound artifacts.
// Every event is driven through the real CAMERA/HUD/INPUT-ACCESS/
// SELECT-ACTION controllers; the external driver only injects X events.
public sealed class ControlsPlayDirector : MonoBehaviour
{
    static readonly string[] Modes = { "keyboard_only", "mouse_only", "remapped" };
    static readonly string[] PauseStates =
    {
        "Camera", "Follow", "Selection", "Map", "ActionMenu", "HudFocus", "MinigolfAim"
    };
    static readonly double[] Scales = { 1.0, 1.5 };
    static readonly string[] RemapKeys =
    {
        "J", "K", "L", "T", "G", "Y", "Z", "V", "C", "P",
        "Alpha1", "Alpha2", "Alpha3", "Alpha4", "Alpha5", "Alpha6",
        "Alpha7", "Alpha8", "Alpha9", "Alpha0", "Comma", "Period"
    };
    static readonly KeyCode[] PolledKeys =
    {
        KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G,
        KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N,
        KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U,
        KeyCode.V, KeyCode.W, KeyCode.X, KeyCode.Y, KeyCode.Z,
        KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
        KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        KeyCode.Comma, KeyCode.Period, KeyCode.Minus, KeyCode.Equals,
        KeyCode.Return, KeyCode.Space, KeyCode.Escape, KeyCode.Backspace,
        KeyCode.PageUp, KeyCode.PageDown, KeyCode.Tab,
        KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
    };

    sealed class Request
    {
        public string Mode;
        public string Flow;
        public double TextScale;
        public string Device;
        public string Op;
        public string Action;
        public string ExpectedKey;
        public string Binding;
        public string StateName;
        public string TargetId;
        public bool Record;
        public bool Persist;
    }

    sealed class InputRecord
    {
        public string Device;
        public string Key;
        public string MouseButton;
        public Vector2 Point;
    }

    sealed class RunEvent
    {
        public string Id;
        public string Flow;
        public string Device;
        public string Input;
        public string Action;
        public string Outcome = "Completed";
        public double TextScale;
        public string FramePath;
        public string NarrationKind;
        public string Label;
        public bool MovingResident;
        public string State;
        public bool Paused;
        public long ProcessId;
        public float CursorSize;
        public float SettingsTextScale;
        public string Binding;
    }

    string phase;
    string outDir;
    string settingsDir;
    string tree;
    string binary;
    long beforePid;
    bool done;
    int exitCode;
    bool failed;
    ControlsBridge bridge;
    ControlsView view;
    MeasureProbe probe;
    NarrationBridge narration;
    Narration narrationEngine;
    Accessibility narrationAccessibility;
    NarrationAudioOutput narrationAudio;
    string narrationAudioDir;
    string narrationAudioSource;
    string oracleTree;
    string oracleBinary;
    Accessibility persistedAccessibility;
    SettingsRecord persistedSettings;
    Accessibility modeAccessibility;
    string mode;
    double scale;
    string currentFlow = string.Empty;
    readonly Dictionary<string, List<RunEvent>> runs = new Dictionary<string, List<RunEvent>>();

    Vector3 cameraBasePosition;
    Quaternion cameraBaseRotation;
    float cameraBaseFieldOfView;
    readonly float[] residentBaseX = new float[8];
    readonly float[] residentBaseZ = new float[8];
    readonly float[] residentPhase = new float[8];
    readonly Vector3[] residentLast = new Vector3[8];
    float motionTime;
    bool motionEnabled;
    bool measuring;
    float lastMoveTime;

    static void Log(string message)
    {
        UnityEngine.Debug.Log("CONTROLS_PLAY " + message);
    }

    static string Arg(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                return args[i].Substring(key.Length + 1);
        return string.Empty;
    }

    void Start()
    {
        Application.runInBackground = true;
        string[] args = Environment.GetCommandLineArgs();
        phase = Arg(args, "--controls-play");
        if (phase.Length == 0)
        {
            Log("idle (no --controls-play phase)");
            enabled = false;
            return;
        }
        outDir = Arg(args, "--controls-out");
        settingsDir = Arg(args, "--controls-settings");
        tree = Arg(args, "--controls-tree");
        binary = Arg(args, "--controls-binary-sha");
        narrationAudioDir = Arg(args, "--controls-audio");
        narrationAudioSource = Arg(args, "--controls-audio-source");
        oracleTree = Arg(args, "--controls-oracle-tree");
        oracleBinary = Arg(args, "--controls-oracle-binary");
        if (narrationAudioSource.Length == 0) narrationAudioSource = "na-sink";
        if (narrationAudioDir.Length > 0 && (oracleTree.Length == 0 || oracleBinary.Length == 0))
        {
            Fail("narration audio requires --controls-oracle-tree/--controls-oracle-binary");
            return;
        }
        if (outDir.Length == 0 || settingsDir.Length == 0 || tree.Length == 0 || binary.Length == 0)
        {
            Fail("bad-args phase=" + phase);
            return;
        }
        Directory.CreateDirectory(outDir);
        try
        {
            if (phase == "prepare") Prepare();
            else if (phase == "run") StartCoroutine(RunGuarded());
            else Fail("unknown phase " + phase);
        }
        catch (Exception e) { Fail(e.ToString()); }
    }

    static SettingsRecord MakeSettings(IReadOnlyDictionary<string, string> remaps)
    {
        return new SettingsRecord(SaveV1.SettingsSchemaVersion,
            1f, 1.5f, true,
            1f, 1f, 1f, 1f,
            true, true, remaps,
            2f, 2f, 2f, 2f,
            true, false, false, false, true, true, true);
    }

    static Dictionary<string, string> ComposeRemaps()
    {
        Dictionary<string, string> remaps = new Dictionary<string, string>();
        for (int i = 0; i < InputActions.Names.Length; i++)
            remaps.Add(InputActions.Names[i], RemapKeys[i]);
        return remaps;
    }

    void Prepare()
    {
        Directory.CreateDirectory(settingsDir);
        SettingsRecord settings = MakeSettings(ComposeRemaps());
        SaveWriteResult written = SaveV1.SaveSettings(settings, settingsDir);
        if (!written.Ok) { Fail("prepare-save " + written.Detail); return; }
        string path = Path.Combine(settingsDir, SaveV1.SettingsFileName);
        File.Copy(path, Path.Combine(outDir, "settings-before.json"), true);
        long pid = Process.GetCurrentProcess().Id;
        File.WriteAllText(Path.Combine(outDir, "before-pid.txt"),
            pid.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(outDir, "prepare.json"),
            "{\"pid\":" + pid.ToString(CultureInfo.InvariantCulture)
            + ",\"settings_sha256\":\"" + MeasureProbe.Sha256File(path) + "\"}");
        Log("prepare pid=" + pid + " settings=" + path);
        exitCode = 0;
        done = true;
    }

    IEnumerator RunGuarded()
    {
        IEnumerator inner = RunEvidence();
        while (true)
        {
            bool moved;
            try { moved = inner.MoveNext(); }
            catch (Exception e) { Fail(e.ToString()); yield break; }
            if (!moved) yield break;
            yield return inner.Current;
        }
    }

    IEnumerator RunEvidence()
    {
        if (Screen.width != 1920 || Screen.height != 1080)
            throw new InvalidOperationException("display is " + Screen.width + "x" + Screen.height);

        SettingsLoadResult loaded = SaveV1.LoadSettings(settingsDir);
        if (!loaded.Ok) throw new InvalidOperationException("settings-load " + loaded.Detail);
        persistedSettings = loaded.Settings;
        persistedAccessibility = new Accessibility(persistedSettings);
        string pidText = File.ReadAllText(Path.Combine(outDir, "before-pid.txt")).Trim();
        beforePid = long.Parse(pidText, CultureInfo.InvariantCulture);
        probe = new MeasureProbe();
        MeasureProbe.SelfCheck();
        if (narrationAudioDir.Length > 0)
        {
            narrationAudio = new NarrationAudioOutput(narrationAudioDir, narrationAudioSource);
            narrationAccessibility = persistedAccessibility;
            narrationEngine = new Narration(narrationAccessibility, narrationAudio);
        }
        else
        {
            narrationAccessibility = persistedAccessibility;
            narrationEngine = new Narration(narrationAccessibility);
        }
        narration = new NarrationBridge(narrationEngine);
        for (int i = 0; i < Modes.Length; i++) runs[Modes[i]] = new List<RunEvent>();
        string frames = Path.Combine(outDir, "frames");
        if (narrationAudioDir.Length == 0)
        {
            if (Directory.Exists(frames)) Directory.Delete(frames, true);
            Directory.CreateDirectory(frames);
        }
        Log("run pid=" + Process.GetCurrentProcess().Id + " display=" + Screen.width + "x" + Screen.height
            + (narrationAudioDir.Length > 0 ? " audio=" + narrationAudioDir : string.Empty));

        yield return null;
        yield return null;
        foreach (string nextMode in Modes)
        {
            foreach (double nextScale in Scales)
            {
                mode = nextMode;
                scale = nextScale;
                BeginMode(nextMode, nextScale);
                List<Request> requests = BuildRequests(nextMode, nextScale);
                for (int i = 0; i < requests.Count; i++)
                {
                    Request request = requests[i];
                    PrepareRequest(request);
                    if (!WriteReady(request)) yield break;
                    InputRecord input = null;
                    float deadline = Time.realtimeSinceStartup + 30f;
                    while (input == null)
                    {
                        input = PollInput();
                        if (input != null) break;
                        if (Time.realtimeSinceStartup > deadline)
                            throw new InvalidOperationException("input timeout " + request.Flow + " " + request.Op);
                        yield return null;
                    }
                    Validate(request, input);
                    Apply(request, input);
                    if (request.Record)
                    {
                        string id = EventId(request);
                        yield return null;
                        if (narrationAudioDir.Length == 0)
                        {
                            string path = Path.Combine(outDir, "frames", id + ".png");
                            yield return Capture(path);
                            RecordEvent(request, input, id, path);
                            WriteRun(nextMode);
                        }
                        else
                        {
                            RecordEvent(request, input, id, null);
                            if (!narrationEngine.SpeechAvailable)
                                throw new InvalidOperationException("speech unavailable " + id);
                        }
                    }
                    else
                    {
                        // One frame boundary so a held key/button cannot be
                        // re-read as a new press by the next request.
                        yield return null;
                    }
                    UpdateViewState();
                }
            }
        }
        if (narrationAudioDir.Length == 0)
        {
            yield return CaptureMeasurements();
            FinishRun();
        }
        else
        {
            narrationAccessibility.SetNarrationEnabled(false);
            narrationEngine.Focus("Narration disabled silence control");
            narrationAudio.CaptureSilence();
            FinishAudio();
        }
    }

    void BeginMode(string nextMode, double nextScale)
    {
        SettingsRecord settings = nextMode == "remapped" ? persistedSettings : MakeSettings(new Dictionary<string, string>());
        modeAccessibility = new Accessibility(settings);
        bridge = new ControlsBridge(settings);
        lastMoveTime = -10f;
        motionEnabled = true;
        motionTime = 0f;
        for (int i = 0; i < 8 && i < bridge.Residents.Length; i++)
        {
            residentBaseX[i] = -10f + i * 2.5f;
            residentBaseZ[i] = 1f;
            residentPhase[i] = i * 0.7f;
        }
        EnsureView();
        ResetCameraTransform();
        view.TextScale = (float)nextScale;
        view.Greyscale = false;
        view.MeasureMode = false;
        view.MenuOpen = false;
        view.Paused = false;
        view.State = "Camera";
        view.Notice = nextMode + " @ " + nextScale.ToString("0.0", CultureInfo.InvariantCulture);
        bridge.Input.EnterState("Camera");
        view.CursorKind = CursorKind.Default;
        view.CursorPx = MeasureProbe.PixelsFor(1.0);
        UpdateViewState();
        Log("mode=" + nextMode + " scale=" + nextScale.ToString("0.0", CultureInfo.InvariantCulture));
    }

    void EnsureView()
    {
        if (view != null) return;
        Camera cam = Camera.main;
        if (cam == null)
        {
            GameObject found = GameObject.Find("GreyboxCamera");
            if (found != null) cam = found.GetComponent<Camera>();
        }
        if (cam == null) throw new InvalidOperationException("scene has no camera");
        List<Transform> residents = new List<Transform>();
        for (int i = 1; i <= 8; i++)
        {
            GameObject go = GameObject.Find("Resident_" + i);
            if (go != null) residents.Add(go.transform);
        }
        List<Transform> stations = new List<Transform>();
        for (int i = 1; i <= 8; i++)
        {
            GameObject go = GameObject.Find(i <= 4 ? "StationA" + i : "StationB" + (i - 4));
            if (go != null) stations.Add(go.transform);
        }
        if (residents.Count != 8) throw new InvalidOperationException("scene resident roots " + residents.Count);
        if (stations.Count != 8) throw new InvalidOperationException("scene station roots " + stations.Count);
        view = gameObject.AddComponent<ControlsView>();
        view.WorldCamera = cam;
        view.ResidentRoots = residents.ToArray();
        view.StationRoots = stations.ToArray();
        cameraBasePosition = cam.transform.position;
        cameraBaseRotation = cam.transform.rotation;
        cameraBaseFieldOfView = cam.fieldOfView;
        for (int i = 0; i < 8; i++) residentLast[i] = view.ResidentRoots[i].position;
    }

    List<Request> BuildRequests(string nextMode, double nextScale)
    {
        string device = nextMode == "mouse_only" ? "mouse" : "keyboard";
        List<Request> list = new List<Request>();
        Add(list, nextMode, nextScale, device, "camera", "camera", "CameraPanRight",
            device == "mouse" ? "camera-right" : null, true, false);
        Add(list, nextMode, nextScale, device, "select-click", "select-click", "SelectNext",
            device == "mouse" ? "world-resident:0" : null, true, false);
        Add(list, nextMode, nextScale, device, "select-snap", "select-snap", "SelectNext",
            device == "mouse" ? "snap-target" : null, true, false);
        Add(list, nextMode, nextScale, device, "select-keyboard", "select-keyboard", "SelectNext",
            device == "mouse" ? "world-resident:2" : null, true, false);
        if (device == "mouse")
        {
            Add(list, nextMode, nextScale, device, "action-menu-immediate", "menu-open", "OpenActionMenu",
                "world-object:0", false, false);
            Add(list, nextMode, nextScale, device, "action-menu-immediate", "menu-immediate", "ImmediateCommand",
                "action:ACT-FOOD-01:Immediate", true, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-open", "OpenActionMenu",
                "world-object:0", false, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-append", "AppendCommand",
                "action:ACT-FOOD-01:Append", true, false);
        }
        else
        {
            Add(list, nextMode, nextScale, device, "action-menu-immediate", "menu-open", "OpenActionMenu", null, false, false);
            Add(list, nextMode, nextScale, device, "action-menu-immediate", "menu-focus", "SelectNext", null, false, false);
            Add(list, nextMode, nextScale, device, "action-menu-immediate", "menu-immediate", "Confirm", null, true, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-open", "OpenActionMenu", null, false, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-focus", "SelectNext", null, false, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-focus", "SelectNext", null, false, false);
            Add(list, nextMode, nextScale, device, "action-menu-append", "menu-append", "Confirm", null, true, false);
        }
        Add(list, nextMode, nextScale, device, "hud-read", "hud", "SelectNext",
            device == "mouse" ? "hud:portrait:0" : null, true, false);
        Add(list, nextMode, nextScale, device, "cursor-states", "cursor", "SelectNext",
            device == "mouse" ? "cursor-target:Blocked" : null, true, false);
        foreach (string state in PauseStates)
        {
            Add(list, nextMode, nextScale, device, "pause-anytime", "pause-record", "Pause",
                device == "mouse" ? "on-screen" : null, true, false, state);
            Add(list, nextMode, nextScale, device, "pause-anytime", "pause-resume", "Pause",
                device == "mouse" ? "on-screen" : null, false, false, null);
        }
        Add(list, nextMode, nextScale, device, "settings-persist-after-restart", "persist", "Inspect",
            device == "mouse" ? "persist-button" : null, true, true);
        return list;
    }

    void Add(List<Request> list, string nextMode, double nextScale, string device,
        string flow, string op, string action, string target, bool record, bool persist,
        string state = null)
    {
        Request request = new Request
        {
            Mode = nextMode,
            Flow = flow,
            TextScale = nextScale,
            Device = device,
            Op = op,
            Action = action,
            TargetId = target,
            Record = record,
            Persist = persist,
            StateName = state,
        };
        if (device == "keyboard")
        {
            request.ExpectedKey = persist
                ? persistedAccessibility.BindingFor(action)
                : modeAccessibility.BindingFor(action);
        }
        request.Binding = persist
            ? persistedAccessibility.BindingFor(action)
            : modeAccessibility.BindingFor(action);
        list.Add(request);
    }

    void PrepareRequest(Request request)
    {
        currentFlow = request.Flow;
        if (request.StateName != null)
        {
            bridge.Input.EnterState(request.StateName);
            view.State = request.StateName;
        }
        UpdateViewState();
    }

    bool WriteReady(Request request)
    {
        string target = request.TargetId;
        Vector2 point = new Vector2(0f, 0f);
        Rect rect = new Rect(0f, 0f, 0f, 0f);
        if (request.Device == "mouse")
        {
            rect = view.RectFor(target);
            if (rect.width <= 0f || rect.height <= 0f)
            {
                Fail("missing click target " + target + " for " + request.Op);
                return false;
            }
            point = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            view.Pointer = point;
            view.FocusId = target;
        }
        else
        {
            view.FocusId = FocusFor(request);
            Rect focus = view.RectFor(view.FocusId);
            if (focus.width > 0f) view.Pointer = new Vector2(focus.x + focus.width * 0.5f, focus.y + focus.height * 0.5f);
        }
        view.Notice = request.Mode + " " + request.Op + " " + request.Action
            + (request.Device == "keyboard" ? " key=" + request.ExpectedKey : " click=" + target);
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"phase\":\"input\",\"mode\":").Append(MeasureProbe.Quote(request.Mode))
          .Append(",\"flow\":").Append(MeasureProbe.Quote(request.Flow))
          .Append(",\"op\":").Append(MeasureProbe.Quote(request.Op))
          .Append(",\"text_scale\":").Append(request.TextScale.ToString("0.0", CultureInfo.InvariantCulture))
          .Append(",\"device\":").Append(MeasureProbe.Quote(request.Device))
          .Append(",\"record\":").Append(request.Record ? "true" : "false")
          .Append(",\"awaiting_input\":true,\"seq\":").Append(++readySeq);
        if (request.Device == "keyboard")
            sb.Append(",\"key\":").Append(MeasureProbe.Quote(request.ExpectedKey));
        else
            sb.Append(",\"button\":1,\"target\":").Append(MeasureProbe.Quote(target))
              .Append(",\"x\":").Append(MeasureProbe.Number(point.x))
              .Append(",\"y\":").Append(MeasureProbe.Number(point.y))
              .Append(",\"w\":").Append(MeasureProbe.Number(rect.width))
              .Append(",\"h\":").Append(MeasureProbe.Number(rect.height));
        sb.Append('}');
        WriteAtomic(Path.Combine(outDir, "controls-ready.json"), sb.ToString());
        return true;
    }

    int readySeq;

    string FocusFor(Request request)
    {
        if (request.Flow.StartsWith("action-menu", StringComparison.Ordinal))
            return "menu";
        if (request.Flow == "hud-read") return "hud:portrait:0";
        if (request.Flow == "cursor-states") return "cursor-target:" + (request.Device == "mouse" ? "Blocked" : "Default");
        if (request.Flow == "pause-anytime") return "on-screen";
        if (request.Flow == "settings-persist-after-restart") return "overlay";
        if (request.Flow.StartsWith("select-", StringComparison.Ordinal)) return "world-resident:0";
        return "overlay";
    }

    InputRecord PollInput()
    {
        for (int i = 0; i < PolledKeys.Length; i++)
        {
            if (Input.GetKeyDown(PolledKeys[i]))
                return new InputRecord { Device = "keyboard", Key = PolledKeys[i].ToString() };
        }
        if (Input.GetMouseButtonDown(0))
            return new InputRecord
            {
                Device = "mouse",
                MouseButton = "Mouse0",
                Point = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y),
            };
        return null;
    }

    void Validate(Request request, InputRecord input)
    {
        if (input.Device != request.Device)
            throw new InvalidOperationException("device mismatch expected=" + request.Device + " got=" + input.Device);
        if (request.Device == "keyboard")
        {
            if (input.Key != request.ExpectedKey)
                throw new InvalidOperationException("binding mismatch expected=" + request.ExpectedKey + " got=" + input.Key);
            return;
        }
        string hit = view.HitTest(input.Point);
        if (hit != request.TargetId)
            throw new InvalidOperationException("click target expected=" + request.TargetId + " hit=" + hit
                + " at " + input.Point.x.ToString("0", CultureInfo.InvariantCulture) + ","
                + input.Point.y.ToString("0", CultureInfo.InvariantCulture));
    }

    void Apply(Request request, InputRecord input)
    {
        switch (request.Op)
        {
            case "camera":
                bridge.CameraActivate(modeAccessibility.BindingFor("CameraPanRight"), 0.5);
                view.Notice = "camera pan right";
                break;
            case "select-click":
                DoSelect(request, input, SelectMode.Click);
                break;
            case "select-snap":
                DoSelect(request, input, SelectMode.Snap);
                break;
            case "select-keyboard":
                DoSelect(request, input, SelectMode.Keyboard);
                break;
            case "menu-open":
                bridge.OpenMenu(bridge.Residents[0]);
                view.MenuOpen = true;
                view.MenuLabel = "Station " + ControlsBridge.ActivityCode;
                view.FocusId = "menu";
                break;
            case "menu-focus":
                if (!bridge.MenuFocus(modeAccessibility.BindingFor("SelectNext")))
                    throw new InvalidOperationException("menu focus refused");
                view.MenuFocusedId = bridge.Menu.FocusedElementId;
                break;
            case "menu-immediate":
            case "menu-append":
                if (request.Device == "mouse")
                {
                    string id = request.Op == "menu-immediate"
                        ? "action:ACT-FOOD-01:Immediate" : "action:ACT-FOOD-01:Append";
                    if (!bridge.MenuClick(id)) throw new InvalidOperationException("menu click refused " + id);
                }
                else if (!bridge.MenuConfirm(modeAccessibility.BindingFor("Confirm")))
                    throw new InvalidOperationException("menu confirm refused");
                view.MenuMessage = bridge.LastMenuMessage;
                view.FocusId = "menu";
                break;
            case "hud":
                if (request.Device == "mouse")
                {
                    string id = view.HudRows.Count > 0 ? view.HudRows[0].Id : "clock";
                    if (!bridge.HudClick(id)) throw new InvalidOperationException("hud click refused " + id);
                }
                else if (!bridge.HudNext(modeAccessibility.BindingFor("SelectNext")))
                    throw new InvalidOperationException("hud focus refused");
                view.FocusId = "hud:portrait:0";
                break;
            case "cursor":
                if (request.Device == "mouse")
                {
                    bridge.LastCue = bridge.CursorFor(CursorKind.Blocked);
                    view.CursorKind = CursorKind.Blocked;
                }
                else
                {
                    if (!bridge.CursorDemoNext()) throw new InvalidOperationException("cursor resolve failed");
                    view.CursorKind = bridge.LastCue.Kind;
                }
                view.Notice = "cursor " + bridge.LastCue.Kind + ": " + bridge.LastCue.Label;
                view.FocusId = "cursor-target:" + bridge.LastCue.Kind;
                break;
            case "pause-record":
                if (!bridge.TogglePause(modeAccessibility.BindingFor("Pause")))
                    throw new InvalidOperationException("pause refused");
                if (!bridge.Clock.Paused) throw new InvalidOperationException("pause did not pause");
                view.Paused = true;
                view.Notice = "paused in " + bridge.Input.ControlState;
                break;
            case "pause-resume":
                if (!bridge.TogglePause(modeAccessibility.BindingFor("Pause")))
                    throw new InvalidOperationException("resume refused");
                if (bridge.Clock.Paused) throw new InvalidOperationException("resume did not resume");
                view.Paused = false;
                break;
            case "persist":
                SettingsLoadResult reloaded = SaveV1.LoadSettings(settingsDir);
                if (!reloaded.Ok) throw new InvalidOperationException("persist reload " + reloaded.Detail);
                persistedSettings = reloaded.Settings;
                persistedAccessibility = new Accessibility(persistedSettings);
                view.CursorPx = MeasureProbe.PixelsFor(persistedSettings.CursorSize);
                view.Notice = "persisted text=" + persistedSettings.TextScale.ToString("0.00", CultureInfo.InvariantCulture)
                    + " cursor=" + persistedSettings.CursorSize.ToString("0.00", CultureInfo.InvariantCulture)
                    + " inspect=" + persistedAccessibility.BindingFor("Inspect");
                view.FocusId = "persist-button";
                break;
            default:
                throw new InvalidOperationException("unknown op " + request.Op);
        }
        UpdateViewState();
    }

    enum SelectMode { Click, Snap, Keyboard }

    void DoSelect(Request request, InputRecord input, SelectMode selectMode)
    {
        List<SelectionTarget> targets = SelectionTargets();
        if (request.Device == "mouse")
        {
            int index = selectMode == SelectMode.Keyboard ? 2 : 0;
            ResidentId? hit = selectMode == SelectMode.Snap ? (ResidentId?)null : bridge.Residents[index];
            if (!bridge.PointerSelect(hit, input.Point.x, input.Point.y, targets))
                throw new InvalidOperationException("pointer select refused " + selectMode);
        }
        else if (!bridge.SelectionNext(modeAccessibility.BindingFor("SelectNext"), targets))
            throw new InvalidOperationException("keyboard select refused");
        int selected = 0;
        if (bridge.LastSelected.HasValue)
            for (int i = 0; i < bridge.Residents.Length; i++)
                if (bridge.Residents[i] == bridge.LastSelected.Value) selected = i;
        view.FocusId = "world-resident:" + selected;
        view.Notice = "selected " + bridge.ResidentName(selected);
    }

    List<SelectionTarget> SelectionTargets()
    {
        List<SelectionTarget> targets = new List<SelectionTarget>();
        for (int i = 0; i < bridge.Residents.Length; i++)
        {
            Rect rect = view.RectFor("world-resident:" + i);
            targets.Add(new SelectionTarget(bridge.Residents[i], rect.center.x, rect.center.y));
        }
        return targets;
    }

    void UpdateViewState()
    {
        if (bridge == null || view == null) return;
        view.State = bridge.Input.ControlState;
        view.Paused = bridge.Clock.Paused;
        view.MenuOpen = bridge.MenuOpen;
        view.MenuFocusedId = bridge.Menu.FocusedElementId;
        view.TextScale = (float)scale;
        view.HudRows.Clear();
        HudSnapshot snapshot = bridge.Hud.Snapshot(1920, 1080);
        int row = 0;
        foreach (object item in snapshot.Elements)
        {
            HudElement element = item as HudElement;
            if (element == null) continue;
            if (!element.Focusable) continue;
            view.HudRows.Add(new ControlsView.ViewRow(
                element.Id,
                element.Label,
                bridge.Hud.FocusedElementId == element.Id));
            row++;
            if (row >= 8) break;
        }
        view.MenuRows.Clear();
        IReadOnlyList<HudElement> choices = bridge.Menu.Elements(1920, 1080);
        for (int i = 0; i < choices.Count; i++)
            view.MenuRows.Add(new ControlsView.ViewRow(choices[i].Id, choices[i].Label,
                bridge.Menu.FocusedElementId == choices[i].Id));
        view.MenuLabel = bridge.Menu.Label;
        view.MenuMessage = bridge.Menu.Message;
    }

    string EventId(Request request)
    {
        string mode = request.Mode == "keyboard_only" ? "kb" : request.Mode == "mouse_only" ? "mouse" : "remap";
        string suffix = request.TextScale == 1.0 ? "s100" : "s150";
        string id = "cp-" + mode + "-" + request.Flow + "-" + suffix;
        if (request.StateName != null) id += "-" + request.StateName.ToLowerInvariant();
        return id;
    }

    void RecordEvent(Request request, InputRecord input, string id, string framePath)
    {
        RunEvent e = new RunEvent
        {
            Id = id,
            Flow = request.Flow,
            Device = request.Device,
            Input = request.Device == "mouse" ? "Mouse0" : input.Key,
            Action = request.Action,
            TextScale = request.TextScale,
            FramePath = framePath,
            State = bridge.Input.ControlState,
            Paused = bridge.Clock.Paused,
            ProcessId = Process.GetCurrentProcess().Id,
            CursorSize = persistedSettings.CursorSize,
            SettingsTextScale = persistedSettings.TextScale,
            Binding = request.Binding,
            MovingResident = Time.time - lastMoveTime < 0.25f,
        };
        string kind = request.Flow.StartsWith("select-", StringComparison.Ordinal) ? "selection"
            : request.Flow.StartsWith("action-", StringComparison.Ordinal) ? "action-result" : "focus";
        e.NarrationKind = kind;
        if (narrationAudio != null)
        {
            narrationAudio.EventId = id;
            narrationAudio.Flow = request.Flow;
            narrationAudio.Kind = kind;
        }
        if (kind == "selection")
        {
            ResidentRecord resident = SelectedRecord();
            e.Label = resident == null ? "resident" : resident.DisplayName;
            narration.Selection(id, request.Flow, resident);
        }
        else if (kind == "action-result")
        {
            ResidentRecord resident = bridge.Resident(0);
            CommandResult result = bridge.LastMenuMessage.StartsWith("Accepted", StringComparison.Ordinal)
                ? CommandResult.Accept() : CommandResult.Reject(RejectReason.InvalidParameters);
            e.Label = ControlsBridge.ActivityCode;
            narration.ActionResult(id, request.Flow, resident, bridge.Activity, result);
        }
        else
        {
            e.Label = LabelFor(request);
            narration.Focus(id, request.Flow, e.Label);
        }
        if (string.IsNullOrWhiteSpace(e.Label))
            throw new InvalidOperationException("blank label " + id);
        runs[request.Mode].Add(e);
    }

    ResidentRecord SelectedRecord()
    {
        if (bridge.LastSelected.HasValue)
        {
            ResidentRecord record;
            if (bridge.World.TryGetResident(bridge.LastSelected.Value, out record)) return record;
        }
        return bridge.Resident(0);
    }

    string LabelFor(Request request)
    {
        switch (request.Op)
        {
            case "camera": return "Camera pan right";
            case "hud": return string.IsNullOrWhiteSpace(bridge.LastHudLabel)
                ? "HUD focus " + bridge.Hud.FocusedElementId : bridge.LastHudLabel;
            case "cursor": return bridge.LastCue == null ? "Cursor" : bridge.LastCue.Label;
            case "pause-record": return "Paused in " + bridge.Input.ControlState;
            case "persist": return "Persisted settings applied";
            case "menu-immediate": return "Immediate action menu";
            case "menu-append": return "Append action menu";
            default: return request.Flow + " completed";
        }
    }

    IEnumerator Capture(string path)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            if (texture.width != 1920 || texture.height != 1080)
                throw new InvalidOperationException("frame " + texture.width + "x" + texture.height + " " + path);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally { Destroy(texture); }
    }

    void WriteRun(string runMode)
    {
        List<RunEvent> events = runs[runMode];
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"binary_sha256\":\"").Append(binary)
          .Append("\",\"mode\":").Append(MeasureProbe.Quote(runMode)).Append(",\"events\":[");
        for (int i = 0; i < events.Count; i++)
        {
            RunEvent e = events[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(MeasureProbe.Quote(e.Id))
              .Append(",\"flow\":").Append(MeasureProbe.Quote(e.Flow))
              .Append(",\"device\":").Append(MeasureProbe.Quote(e.Device))
              .Append(",\"input\":").Append(MeasureProbe.Quote(e.Input))
              .Append(",\"action\":").Append(MeasureProbe.Quote(e.Action))
              .Append(",\"outcome\":").Append(MeasureProbe.Quote(e.Outcome))
              .Append(",\"text_scale\":").Append(e.TextScale.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"frame\":").Append(MeasureProbe.Ref(e.FramePath))
              .Append(",\"narration_kind\":").Append(MeasureProbe.Quote(e.NarrationKind))
              .Append(",\"label\":").Append(MeasureProbe.Quote(e.Label))
              .Append(",\"moving_resident\":").Append(e.MovingResident ? "true" : "false")
              .Append(",\"state\":").Append(MeasureProbe.Quote(e.State))
              .Append(",\"paused\":").Append(e.Paused ? "true" : "false")
              .Append(",\"process_id\":").Append(e.ProcessId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"cursor_size\":").Append(e.CursorSize.ToString("0.0###", CultureInfo.InvariantCulture))
              .Append(",\"settings_text_scale\":").Append(e.SettingsTextScale.ToString("0.0###", CultureInfo.InvariantCulture))
              .Append(",\"binding\":").Append(MeasureProbe.Quote(e.Binding))
              .Append('}');
        }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(outDir, runMode + ".json"), sb.ToString());
    }

    IEnumerator CaptureMeasurements()
    {
        measuring = true;
        motionEnabled = false;
        ResetCameraTransform();
        view.TextScale = 1f;
        view.MenuOpen = true;
        view.MeasureMode = true;
        view.CursorPx = MeasureProbe.PixelsFor(1.0);
        view.CursorKind = CursorKind.Default;
        view.HighContrastCursor = false;
        for (int i = 0; i < bridge.Residents.Length; i++)
            residentLast[i] = view.ResidentRoots[i].position;
        foreach (MeasureProbe.ElementSpec element in MeasureProbe.Elements)
        {
            foreach (double sampleScale in MeasureProbe.Scales)
            {
                view.TextScale = (float)sampleScale;
                view.MeasureId = element.Id;
                view.MeasureScale = (float)sampleScale;
                view.FocusId = element.Id;
                view.Pointer = new Vector2(960f, 540f);
                view.Notice = "measurement " + element.Id;
                yield return null;
                string name = "m-element-" + Safe(element.Id) + "-s" + (sampleScale == 1.0 ? "100" : "150") + ".png";
                string path = Path.Combine(outDir, "frames", name);
                yield return Capture(path);
                ControlsView.ViewElement drawn = view.Find(element.Id);
                if (drawn == null) throw new InvalidOperationException("measured element missing " + element.Id);
                probe.AddSample(element.Id, sampleScale, drawn.Rect, drawn.Foreground, drawn.Background,
                    ControlsView.FocusInk, drawn.Background, path);
            }
        }
        view.MeasureMode = false;
        view.MenuOpen = false;
        view.FocusId = string.Empty;
        view.TextScale = 1f;
        view.Greyscale = true;
        foreach (string status in MeasureProbe.GreyscaleStates)
        {
            view.GreyscaleStatus = status;
            yield return null;
            string name = "m-grey-" + status + ".png";
            string path = Path.Combine(outDir, "frames", name);
            yield return Capture(path);
            probe.AddGrey(status, path);
        }
        view.Greyscale = false;
        view.Pointer = new Vector2(1150f, 620f);
        foreach (string state in Enum.GetNames(typeof(CursorKind)))
        {
            foreach (double size in MeasureProbe.CursorSizes)
            {
                foreach (bool high in new[] { false, true })
                {
                    view.CursorKind = (CursorKind)Enum.Parse(typeof(CursorKind), state);
                    view.CursorPx = MeasureProbe.PixelsFor(size);
                    view.HighContrastCursor = high;
                    view.Notice = "cursor " + state + " " + size.ToString("0.00", CultureInfo.InvariantCulture)
                        + (high ? " high-contrast" : " standard");
                    yield return null;
                    string name = "m-cursor-" + state + "-s" + SizeTag(size) + (high ? "-hc" : "-std") + ".png";
                    string path = Path.Combine(outDir, "frames", name);
                    yield return Capture(path);
                    probe.AddCursor(state, size, high, path);
                }
            }
        }
        view.HighContrastCursor = false;
        view.CursorPx = MeasureProbe.PixelsFor(1.0);
        view.CursorKind = CursorKind.Default;
        measuring = false;
        File.WriteAllText(Path.Combine(outDir, "measurements.json"),
            probe.BuildJson(tree, binary, MeasureProbe.Ref));
        File.WriteAllText(Path.Combine(outDir, "narration.json"), narration.BuildJson(tree, binary));
        Log("measurements samples=" + probe.Samples.Count + " cursors=" + probe.Cursors.Count);
    }

    static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    static string SizeTag(double size)
    {
        return size.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", string.Empty);
    }

    static string Safe(string value)
    {
        return value.Replace(':', '-').Replace('/', '-');
    }

    void FinishAudio()
    {
        narrationAudio.Close();
        File.WriteAllText(Path.Combine(outDir, "narration.json"), narration.BuildJson(tree, binary));
        string controls = "evidence/production/CONTROLS-PLAY/controls.json";
        narrationAudio.WriteReceipt(Path.Combine(narrationAudioDir, "audio.json"),
            oracleTree, oracleBinary, MeasureProbe.Sha256File(controls),
            tree, binary, Process.GetCurrentProcess().Id);
        WriteAtomic(Path.Combine(outDir, "controls-ready.json"),
            "{\"phase\":\"done\",\"seq\":" + (++readySeq).ToString(CultureInfo.InvariantCulture) + "}");
        Log("audio complete events=" + narrationAudio.Events.Count + " pid=" + Process.GetCurrentProcess().Id);
        exitCode = 0;
        done = true;
    }

    void FinishRun()
    {
        SaveWriteResult resaved = SaveV1.SaveSettings(persistedSettings, settingsDir);
        if (!resaved.Ok) throw new InvalidOperationException("settings resave " + resaved.Detail);
        string currentPath = Path.Combine(settingsDir, SaveV1.SettingsFileName);
        string beforePath = Path.Combine(outDir, "settings-before.json");
        string afterPath = Path.Combine(outDir, "settings-after.json");
        if (!File.Exists(beforePath)) throw new InvalidOperationException("missing settings-before");
        string beforeBytes = File.ReadAllText(beforePath);
        string afterBytes = File.ReadAllText(currentPath);
        if (!string.Equals(beforeBytes, afterBytes, StringComparison.Ordinal))
            throw new InvalidOperationException("settings content changed across restart");
        File.WriteAllText(afterPath, afterBytes);
        WriteControls();
        WriteAtomic(Path.Combine(outDir, "controls-ready.json"),
            "{\"phase\":\"done\",\"seq\":" + (++readySeq).ToString(CultureInfo.InvariantCulture) + "}");
        Log("run complete before_pid=" + beforePid + " after_pid=" + Process.GetCurrentProcess().Id
            + " events=" + (runs["keyboard_only"].Count + runs["mouse_only"].Count + runs["remapped"].Count));
        exitCode = 0;
        done = true;
    }

    void WriteControls()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"binary_sha256\":\"").Append(binary).Append('"');
        sb.Append(",\"build\":").Append(MeasureProbe.Ref(Path.Combine(outDir, "build.json")));
        sb.Append(",\"runs\":{");
        for (int i = 0; i < Modes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(MeasureProbe.Quote(Modes[i])).Append(':')
              .Append(MeasureProbe.Ref(Path.Combine(outDir, Modes[i] + ".json")));
        }
        sb.Append("},\"settings_before\":").Append(MeasureProbe.Ref(Path.Combine(outDir, "settings-before.json")));
        sb.Append(",\"settings_after\":").Append(MeasureProbe.Ref(Path.Combine(outDir, "settings-after.json")));
        sb.Append(",\"restart\":{\"before_pid\":").Append(beforePid.ToString(CultureInfo.InvariantCulture))
          .Append(",\"after_pid\":").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)).Append('}');
        sb.Append(",\"measurements\":").Append(MeasureProbe.Ref(Path.Combine(outDir, "measurements.json")));
        sb.Append(",\"narration\":").Append(MeasureProbe.Ref(Path.Combine(outDir, "narration.json")));
        sb.Append(",\"narration_audio\":{\"status\":\"GAP\",\"id\":\"G-7\",\"reason\":")
          .Append(MeasureProbe.Quote("No Linux speech backend is configured in this headless Xvfb player; "
              + "utterance text is recorded as narration intent, not spoken-audio proof.")).Append('}');
        sb.Append(",\"generated_utc\":").Append(MeasureProbe.Quote(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        sb.Append('}');
        File.WriteAllText(Path.Combine(outDir, "controls.json"), sb.ToString());
    }

    void ResetCameraTransform()
    {
        if (view == null || view.WorldCamera == null) return;
        view.WorldCamera.transform.position = cameraBasePosition;
        view.WorldCamera.transform.rotation = cameraBaseRotation;
        view.WorldCamera.fieldOfView = cameraBaseFieldOfView;
    }

    void Update()
    {
        if (failed)
        {
            if (!Application.isEditor) Application.Quit(1);
            return;
        }
        if (done)
        {
            if (!Application.isEditor) Application.Quit(exitCode);
            return;
        }
        Animate();
        if (!measuring) ApplyCamera();
    }

    void Animate()
    {
        if (view == null || !motionEnabled || bridge == null) return;
        motionTime += Time.deltaTime;
        float maxDelta = 0f;
        for (int i = 0; i < 8 && i < view.ResidentRoots.Length; i++)
        {
            float x = residentBaseX[i] + Mathf.Sin(motionTime * 0.9f + residentPhase[i]) * 0.45f;
            float z = residentBaseZ[i] + Mathf.Cos(motionTime * 0.7f + residentPhase[i]) * 0.30f;
            Vector3 next = new Vector3(x, 0f, z);
            maxDelta = Mathf.Max(maxDelta, (next - residentLast[i]).magnitude);
            residentLast[i] = next;
            view.ResidentRoots[i].position = next;
            bridge.SetPose(i, x, z);
        }
        if (maxDelta > 0.0001f) lastMoveTime = Time.time;
    }

    void ApplyCamera()
    {
        if (view == null || view.WorldCamera == null || bridge == null) return;
        Camera cam = view.WorldCamera;
        cam.transform.position = cameraBasePosition
            + new Vector3((float)bridge.Camera.PanX * 0.35f, 0f, (float)bridge.Camera.PanZ * 0.35f);
        cam.transform.rotation = cameraBaseRotation
            * Quaternion.Euler(0f, (float)bridge.Camera.Rotation * 6f, 0f);
        cam.fieldOfView = Mathf.Clamp(60f * 10f / (float)bridge.Camera.Zoom, 30f, 80f);
    }

    // NARRATION-AUDIO evidence mode: each utterance gets its own WAV from the
    // i7 sink monitor. A digital-silence keepalive keeps the monitor driven so
    // idle captures are zeros instead of uninitialised sink buffers. The
    // product narration text still travels only through SpeechDispatcherOutput.
    sealed class NarrationAudioOutput : ISpeechOutput
    {
        public sealed class Entry
        {
            public string EventId, Flow, Kind, Text, Wav;
        }

        readonly string root;
        readonly string source;
        readonly List<Entry> events = new List<Entry>();
        readonly SpeechDispatcherOutput speech;
        readonly string logPath;
        Process keepalive;
        Process recorder;
        string silenceWav;

        public string EventId, Flow, Kind;

        public List<Entry> Events { get { return events; } }

        public NarrationAudioOutput(string root, string source)
        {
            this.root = root;
            this.source = source;
            Directory.CreateDirectory(Path.Combine(root, "wav"));
            logPath = Path.Combine(root, "capture.log");
            File.WriteAllText(logPath, "NARRATION-AUDIO capture session\n");
            speech = new SpeechDispatcherOutput(BlockingStart);
            keepalive = Launch("/usr/bin/pw-cat", "--playback", "--target", source, "--raw",
                "--format", "s16", "--rate", "48000", "--channels", "1", "/dev/zero");
            LogLine("keepalive pid=" + keepalive.Id + " target=" + source);
            Thread.Sleep(300);
        }

        static bool BlockingStart(ProcessStartInfo start)
        {
            if (!start.ArgumentList.Contains("--wait")) start.ArgumentList.Insert(0, "--wait");
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }

        public bool Speak(string text)
        {
            string id = string.IsNullOrWhiteSpace(EventId) ? "utterance-" + events.Count : EventId;
            Entry entry = new Entry
            {
                EventId = id,
                Flow = Flow ?? string.Empty,
                Kind = Kind ?? string.Empty,
                Text = text ?? string.Empty,
                Wav = Path.Combine(root, "wav", id + ".wav"),
            };
            EventId = null;
            Flow = null;
            Kind = null;
            if (!Capture(entry, true)) return false;
            events.Add(entry);
            return true;
        }

        public void CaptureSilence()
        {
            Entry entry = new Entry { EventId = "silence-control", Wav = Path.Combine(root, "wav", "silence-control.wav") };
            if (!Capture(entry, false)) throw new InvalidOperationException("silence control capture failed");
            silenceWav = entry.Wav;
            LogLine("OK silence-control bytes=" + new FileInfo(entry.Wav).Length);
        }

        bool Capture(Entry entry, bool speak)
        {
            Process recorder = Launch("/usr/bin/pw-record", "--target", "0", "--rate", "48000",
                "--channels", "1", "--format", "s16", entry.Wav);
            if (recorder == null)
            {
                LogLine("FAIL " + entry.EventId + " pw-record missing");
                return false;
            }
            bool ok = false;
            this.recorder = recorder;
            NarrationAudioCapture.Run(
                () =>
                {
                    if (!Link(recorder))
                    {
                        LogLine("FAIL " + entry.EventId + " monitor link");
                        return;
                    }
                    Thread.Sleep(150);
                    if (Environment.GetEnvironmentVariable("BLOCKPARTY_NARRATION_AUDIO_INJECT_FAILURE") == entry.EventId)
                        throw new InvalidOperationException("injected narration capture failure " + entry.EventId);
                    ok = !speak || speech.Speak(entry.Text);
                    Thread.Sleep(300);
                },
                () =>
                {
                    Stop(recorder);
                    this.recorder = null;
                });
            long bytes = File.Exists(entry.Wav) ? new FileInfo(entry.Wav).Length : 0;
            if (bytes <= 44) ok = false;
            LogLine((ok ? "OK " : "FAIL ") + entry.EventId + " bytes=" + bytes
                + " flow=" + entry.Flow + " kind=" + entry.Kind + " text=" + MeasureProbe.Quote(entry.Text));
            return ok;
        }

        bool Link(Process recorder)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                ProcessStartInfo start = new ProcessStartInfo("/usr/bin/pw-link");
                start.UseShellExecute = false;
                start.ArgumentList.Add(source + ":monitor_FL");
                start.ArgumentList.Add("pw-record:input_MONO");
                using (Process link = Process.Start(start))
                {
                    link.WaitForExit();
                    if (link.ExitCode == 0) return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        static Process Launch(string file, params string[] args)
        {
            ProcessStartInfo start = new ProcessStartInfo(file);
            start.UseShellExecute = false;
            foreach (string arg in args) start.ArgumentList.Add(arg);
            return Process.Start(start);
        }

        void Stop(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    ProcessStartInfo kill = new ProcessStartInfo("/usr/bin/kill");
                    kill.UseShellExecute = false;
                    kill.ArgumentList.Add("-INT");
                    kill.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
                    using (Process signal = Process.Start(kill)) signal.WaitForExit();
                    process.WaitForExit(3000);
                }
                if (!process.HasExited) process.Kill();
            }
            catch (Exception e)
            {
                LogLine("stop error " + e.GetType().Name);
            }
        }

        void LogLine(string line)
        {
            File.AppendAllText(logPath, line + "\n");
        }

        public void Close()
        {
            Stop(recorder);
            recorder = null;
            Stop(keepalive);
            keepalive = null;
        }

        public void WriteReceipt(string path, string oracleTree, string oracleBinary,
            string controlsHash, string tree, string binary, int playerPid)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"schema\":\"blockparty-narration-audio/1\"");
            sb.Append(",\"tree_hash\":").Append(MeasureProbe.Quote(oracleTree));
            sb.Append(",\"binary_sha256\":").Append(MeasureProbe.Quote(oracleBinary));
            sb.Append(",\"controls\":").Append(MeasureProbe.Ref("evidence/production/CONTROLS-PLAY/controls.json"));
            sb.Append(",\"capture\":{");
            sb.Append("\"host\":\"i7\",\"platform\":\"Linux\",\"backend\":\"speech-dispatcher\"");
            sb.Append(",\"method\":\"PipeWire-output\"");
            sb.Append(",\"source\":").Append(MeasureProbe.Quote(source + ".monitor_FL (support.null-audio-sink, 48 kHz mono)"));
            sb.Append(",\"player_pid\":").Append(playerPid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"command\":[\"/usr/bin/pw-record\",\"--target\",\"0\",\"--rate\",\"48000\",\"--channels\",\"1\",\"--format\",\"s16\"]");
            sb.Append(",\"log\":{\"path\":").Append(MeasureProbe.Quote(logPath))
              .Append(",\"sha256\":").Append(MeasureProbe.Quote(MeasureProbe.Sha256File(logPath))).Append('}');
            sb.Append(",\"narration_build\":{\"tree_hash\":").Append(MeasureProbe.Quote(tree))
              .Append(",\"binary_sha256\":").Append(MeasureProbe.Quote(binary))
              .Append(",\"build\":").Append(MeasureProbe.Ref(Path.Combine(root, "build.json"))).Append('}');
            sb.Append(",\"keepalive\":[\"/usr/bin/pw-cat\",\"--playback\",\"--target\",").Append(MeasureProbe.Quote(source))
              .Append(",\"--raw\",\"--format\",\"s16\",\"--rate\",\"48000\",\"--channels\",\"1\",\"/dev/zero\"]");
            sb.Append(",\"link\":[\"/usr/bin/pw-link\",").Append(MeasureProbe.Quote(source + ":monitor_FL"))
              .Append(",\"pw-record:input_MONO\"]");
            sb.Append(",\"speech\":[\"spd-say\",\"--wait\",\"-r\",\"-20\",\"--\"]");
            sb.Append('}');
            sb.Append(",\"rms_threshold\":0.001");
            sb.Append(",\"utterances\":[");
            for (int i = 0; i < events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Entry e = events[i];
                sb.Append("{\"event_id\":").Append(MeasureProbe.Quote(e.EventId))
                  .Append(",\"flow\":").Append(MeasureProbe.Quote(e.Flow))
                  .Append(",\"kind\":").Append(MeasureProbe.Quote(e.Kind))
                  .Append(",\"text\":").Append(MeasureProbe.Quote(e.Text))
                  .Append(",\"narration_enabled\":true")
                  .Append(",\"wav\":").Append(MeasureProbe.Ref(e.Wav)).Append('}');
            }
            sb.Append(']');
            sb.Append(",\"silence_control\":{\"narration_enabled\":false,\"wav\":")
              .Append(MeasureProbe.Ref(silenceWav)).Append('}');
            sb.Append('}');
            File.WriteAllText(path, sb.ToString());
        }
    }

    void OnApplicationQuit()
    {
        CloseAudio();
    }

    void CloseAudio()
    {
        if (narrationAudio != null) narrationAudio.Close();
    }

    void Fail(string message)
    {
        CloseAudio();
        failed = true;
        done = true;
        exitCode = 1;
        UnityEngine.Debug.LogError("CONTROLS_PLAY FAIL " + message);
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "controls-fail.json"),
                "{\"error\":" + MeasureProbe.Quote(message) + "}");
        }
        catch (Exception) { }
        if (!Application.isEditor) Application.Quit(1);
    }
}
