using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Presentation;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[Serializable]
public sealed class SliceResidentVisual
{
    public string base_body;
    public string source_asset;
    public Mesh source_body_mesh;
    public SkinnedMeshRenderer body_renderer;
    public int bottom_vertex_start;
    public int bottom_vertex_count;
}

// SLICE Play-mode driver: one fixed-seed eight-resident representative corner.
// RunVerification runs the real landed workflow (title/new, BODY-PRESET,
// grounded D1U local-fallback decision, COMMAND/NAV/FOOD physical cook,
// inspect/map/follow/takeover, recorded audio, accessibility, SaveV1
// save/load/continue) synchronously against the live GameStack, mirrors sim
// positions into the rendered resident roots, captures camera frames, and
// exports slice.json under the supplied owned directory. Fast Enter Play
// Mode: static state is reset at subsystem registration; all run state is
// rebuilt from scratch on every RunVerification call.
public sealed class SliceDirector : MonoBehaviour, ISceneContent
{
    [Header("Wired by SliceSceneBuild.BuildScene")]
    public Transform[] residentRoots;
    public Camera sliceCamera;
    public AudioClip foodClip;
    public AudioClip sleepClip;
    public AudioClip ambienceClip;
    public Texture2D[] skins;
    public SliceResidentVisual[] residentVisuals;
    public UnityEngine.Audio.AudioMixer audioMixer;
    public UnityEngine.Audio.AudioMixerGroup cueGroup;
    public UnityEngine.Audio.AudioMixerGroup ambienceGroup;

    [Header("Run state (read-only at runtime)")]
    public bool done;
    public bool failed;
    public string failureReason = "";
    public bool runtimeReady;
    public int InitialStateHash;

    TitleFlow flow;
    string outDir;
    string repoRoot;
    bool visualsReady;
    bool foodPlayed, sleepPlayed, ambiencePlayed;
    string cueGroupName, ambienceGroupName;
    readonly List<string> trace = new List<string>();
    readonly List<Material> ownedMaterials = new List<Material>();
    readonly List<Mesh> ownedMeshes = new List<Mesh>();
    readonly Dictionary<SkinnedMeshRenderer, Mesh> unmaskedBodies = new Dictionary<SkinnedMeshRenderer, Mesh>();
    readonly List<GameObject> ownedObjects = new List<GameObject>();
    readonly List<string> nativeRows = new List<string>();
    ResidentId[] sorted;
    string[] flowFrames = new string[10];
    string[] flowTraces = new string[10];
    static readonly string[] FlowIds = { "title", "new", "inspect", "map", "follow", "takeover", "save", "load", "continue", "accessibility" };
    static readonly StateWriter Core = StateWriter.SimCore;
    bool livePlay;
    Autonomy runtimeAutonomy;
    Accessibility runtimeAccess;
    readonly ExecutionRecord[] runtimeActions = new ExecutionRecord[8];
    readonly int[] runtimeRounds = new int[8];
    readonly string[] runtimeClips = new string[8];
    readonly Transform[] runtimeSeats = new Transform[8];
    readonly Vector3[] runtimeSeatStations = new Vector3[8];
    readonly float[] runtimeNextDecision = new float[8];
    readonly bool[] runtimeSeated = new bool[8];
    readonly bool[] runtimeDeparting = new bool[8];
    readonly Vector3[] runtimeExitFrom = new Vector3[8];
    readonly Vector3[] runtimeExitTo = new Vector3[8];
    readonly float[] runtimeExitStarted = new float[8];
    readonly Vector3[] runtimePresentRaw = new Vector3[8];
    readonly Vector3[] runtimePresentTarget = new Vector3[8];
    readonly Vector3[] runtimePresentResolved = new Vector3[8];
    readonly bool[] runtimePresentAssigned = new bool[8];
    readonly ExecutionRecord[] runtimePresentExecution = new ExecutionRecord[8];
    AudioSource runtimeAmbience, runtimeMealCue;
    float runtimeSeconds, runtimeTick, nextObservation, nextSave = 30f, captionUntil;
    float runtimeSaveNoticeUntil;
    string runtimeCaption = "Birds and neighborhood ambience";
    string runtimeSaveNotice = "";
    bool runtimeSavesEnabled;
    long runtimeTravelFrames, runtimeWalkHoldSwitches;
    static readonly Vector3[] RuntimeStations = {
        new Vector3(4.6f, 0f, 7.2f), new Vector3(0.2f, 0f, 7.6f),
        new Vector3(1.2f, 0f, 4.6f), new Vector3(3.4f, 0f, 4.6f),
        new Vector3(7.5f, 0f, 4.8f), new Vector3(7.4f, 0f, 6.7f),
        new Vector3(2.5f, 0f, 8.8f), new Vector3(5.8f, 0f, 8.4f)
    };
    static readonly Vector3[] RuntimeWaiting = {
        new Vector3(4.6f, 0f, 6.3f), new Vector3(0.9f, 0f, 6.8f),
        new Vector3(1.9f, 0f, 5.5f), new Vector3(3.4f, 0f, 5.5f),
        new Vector3(6.7f, 0f, 5.6f), new Vector3(6.5f, 0f, 6.4f),
        new Vector3(2.5f, 0f, 7.9f), new Vector3(5.8f, 0f, 7.5f)
    };

    static int entriesSeen;
    static readonly List<string> nativeCounts = new List<string>();
    static int nativeEntry;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetPlayState() { entriesSeen = 0; }

    public GameStack Game { get { return flow == null ? null : flow.Game; } }

    public void RegisterObjects(ActivityExecutor executor)
    {
        for (long id = 9; id <= 16; id++)
        {
            var p = livePlay ? RuntimeStations[id - 9] : new Vector3((id - 9) * 2f, 0f, 2f);
            executor.RegisterObject((ObjectId)id, new[] { new ApproachPose(p.x, p.y, p.z) });
        }
    }

    sealed class NoNetwork : IDecisionTransport
    {
        public int Sends;
        public void Send(GatewayRequest request, DecisionPayload payload) { Sends++; }
        public bool TryReceive(out DecisionResponse response) { response = null; return false; }
    }

    void Awake()
    {
        done = false; failed = false; runtimeReady = false; failureReason = "";
        // An unfocused standalone window otherwise stops before Start resumes (Xvfb included).
        if (!Application.isEditor) Application.runInBackground = true;
    }

    // Ordinary Play/player route. The editor verification harness invokes
    // RunVerification on the first update, before this coroutine resumes.
    IEnumerator Start()
    {
        yield return null;
        if (flow != null || done) yield break;
        try { RunRuntime(); }
        catch (Exception e)
        {
            failed = true; done = true;
            failureReason = e.GetType().Name + ": " + e.Message;
            Debug.LogError("SLICE RUNTIME FAIL " + failureReason);
        }
    }

    public void RunVerification(string evidenceDirectory)
    {
        done = false; failed = false; failureReason = "";
        try
        {
            entriesSeen++;
            outDir = evidenceDirectory;
            repoRoot = Directory.GetParent(Application.dataPath).FullName;
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "frames"));
            Directory.CreateDirectory(Path.Combine(outDir, "traces"));
            Directory.CreateDirectory(Path.Combine(outDir, "saves"));
            Directory.CreateDirectory(Path.Combine(outDir, "entries"));
            trace.Clear();
            RecordNative("start");
            EnsureVisuals();
            Run();
            File.WriteAllText(Path.Combine(outDir, "..", "native-objects.json"),
                "[\n  " + string.Join(",\n  ", nativeCounts.ToArray()) + "\n]\n");
            done = true;
            Debug.Log("SLICE done entriesSeen=" + entriesSeen + " runtime=" + runtimeReady);
        }
        catch (Exception e)
        {
            failed = true; done = true;
            failureReason = e.GetType().Name + ": " + e.Message;
            Debug.LogError("SLICE FAIL " + failureReason);
        }
        finally { RestoreRenderQuality(); }
    }

    void RunRuntime()
    {
        livePlay = true;
        EnsureVisuals();
        string saveDir = Path.Combine(Application.persistentDataPath, "slice");
        var startup = SliceRuntimePolicy.ProbeStartup(saveDir, this);
        runtimeSavesEnabled = startup.SavesEnabled;
        if (!string.IsNullOrEmpty(startup.Notice))
            ShowSaveNotice(startup.Notice, startup.SavesEnabled ? 8f : 1000000f);
        flow = new TitleFlow(saveDir, this);
        bool loaded = startup.ContinueGame;
        if (loaded) Req(flow.Activate(TitleAction.Continue).Ok, "runtime continue");
        else
        {
            Req(flow.Activate(TitleAction.NewGame).Ok, "runtime title/new");
            Req(flow.PrepareNewGame(27L, 42L, null).Ok, "runtime factory");
        }
        Debug.Log("SLICE_RECOVERY " + JsonUtility.ToJson(new RecoveryLog {
            action = startup.Action, saves = runtimeSavesEnabled, loaded = loaded, notice = startup.Notice }));
        var rows = flow.Game.World.ResidentsSnapshot();
        Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        sorted = new ResidentId[rows.Length];
        for (int i = 0; i < rows.Length; i++) sorted[i] = rows[i].Id;
        if (!loaded)
        {
            BindResidentBodies();
            Req(flow.BeginPlay().Ok, "runtime begin play");
            for (int i = 0; i < sorted.Length; i++)
            {
                var p = RuntimeStations[i] + new Vector3(-0.8f, 0f, -0.8f);
                flow.Game.Executor.SetResidentPosition(sorted[i], new ApproachPose(p.x, p.y, p.z));
                flow.Game.Food.SetHomeStock(Core, ResidentHome(flow.Game, sorted[i]), 64, 16);
            }
        }
        runtimeAutonomy = new Autonomy(flow.Game.World, flow.Game.Arbitrator);
        runtimeAccess = new Accessibility(flow.Settings);
        runtimeAccess.SetCaptions(true);
        ApplyAudio(flow.Settings);
        ambiencePlayed = PlayCue(ambienceClip, true, ambienceGroup);
        runtimeAmbience = GetComponent<AudioSource>();
        runtimeMealCue = gameObject.AddComponent<AudioSource>();
        runtimeMealCue.outputAudioMixerGroup = cueGroup;
        runtimeMealCue.clip = foodClip;
        runtimeMealCue.playOnAwake = false;
        MirrorRoots(flow.Game);
        CameraHome();
        done = true;
        runtimeReady = true;
        Debug.Log("SLICE_RUNTIME loaded=" + loaded + " game_seconds=" + flow.Game.World.GameSeconds);
        Debug.Log("SLICE_RUNTIME ready scene=" + gameObject.scene.name + " residents=" + sorted.Length
            + " listeners=" + FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length
            + " mixer=" + (audioMixer == null ? "null" : audioMixer.name) + " ambience_group=" + ambienceGroupName
            + " ambience_playing=" + ambiencePlayed);
        if (!Application.isEditor && HasArg("-slice-smoke"))
        {
            Debug.Log("SLICE_RUNTIME_SMOKE_PASS");
            Invoke(nameof(QuitSmoke), 0.5f);
        }
    }

    static bool HasArg(string value)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++) if (args[i] == value) return true;
        return false;
    }

    void QuitSmoke() { Application.Quit(0); }

    void AdvanceRuntime()
    {
        var g = flow.Game;
        for (int i = 0; i < sorted.Length; i++)
        {
            var previous = runtimeActions[i];
            if (previous != null && previous.IsTerminal)
            {
                if (previous.Outcome == TerminalOutcome.Completed)
                {
                    runtimeRounds[i]++;
                    runtimeMealCue.Play();
                    runtimeCaption = "Meal finished";
                    captionUntil = runtimeSeconds + 4f;
                    Debug.Log("SLICE_MEAL completed resident=" + (long)sorted[i] + " activity=" + previous.Activity.Code);
                    Debug.Log("SLICE_CAPTION Meal finished");
                    Debug.Log("SLICE_AUDIO cue=" + foodClip.name + " group=" + cueGroup.name + " playing=" + runtimeMealCue.isPlaying);
                }
                runtimeActions[i] = null;
            }
            var status = g.Executor.GetResidentStatus(sorted[i]);
            if (status.IsIdle && g.Arbitrator.GetPending(sorted[i]).Length == 0)
            {
                bool eat = (runtimeRounds[i] + (i == 2 || i == 7 ? 0 : 1)) % 2 == 0;
                var home = ResidentHome(g, sorted[i]);
                ResidentRecord resident;
                g.World.TryGetResident(sorted[i], out resident);
                int meals = g.Food.PreparedMeals(home);
                int groceries = g.Food.GroceryUnits(home);
                string activity = SliceRuntimePolicy.FoodCode(eat, meals, groceries, resident.Money);
                if (activity.Length == 0)
                {
                    if (runtimeSeconds >= runtimeNextDecision[i])
                    {
                        Debug.Log("SLICE_DECISION resident=" + (long)sorted[i]
                            + " activity=none result=NoFood meals=" + meals + " groceries=" + groceries);
                        runtimeCaption = "No food available";
                        captionUntil = runtimeSeconds + 4f;
                        runtimeNextDecision[i] = runtimeSeconds + 3f;
                    }
                    continue;
                }
                int station = activity == "ACT-FOOD-02" ? (i % 2 == 0 ? 2 : 7)
                    : activity == "ACT-FOOD-01" ? (i == 2 ? 1 : i == 7 ? 6 : i)
                    : activity == "ACT-FOOD-03" ? (i % 2 == 0 ? 0 : 4) : 5;
                var decision = runtimeAutonomy.Decide(sorted[i], new[] {
                    new AvailableAction(new ActivityId(activity), 9L + station)
                }, null);
                Debug.Log("SLICE_DECISION resident=" + (long)sorted[i] + " activity=" + decision.Chosen.Code
                    + " candidates=" + decision.Candidates.Count + " result=" + decision.CommittedEffect);
            }
        }
        g.Clock.AdvanceRealSeconds(Core, 0.1);
        g.Food.Tick(StateWriter.ActivityExecutor);
        for (int i = 0; i < sorted.Length; i++)
        {
            var execution = g.Executor.GetResidentStatus(sorted[i]).Execution;
            if (execution != null) runtimeActions[i] = execution;
        }
    }

    void PresentRuntime()
    {
        var g = flow.Game;
        for (int i = 0; i < sorted.Length; i++)
        {
            var root = residentRoots[i];
            var p = g.Executor.GetResidentPosition(sorted[i]);
            var raw = new Vector3(p.X, 0f, p.Z);
            var execution = g.Executor.GetResidentStatus(sorted[i]).Execution;
            bool performing = execution != null && execution.Stage == ExecutionStage.Perform;
            bool eating = performing && execution.Activity.Code == "ACT-FOOD-02";
            if (eating)
            {
                runtimeSeats[i] = GameObject.Find(execution.TargetId == 11L ? "SeatFirst" : "SeatSecond").transform;
                runtimeSeatStations[i] = raw;
            }
            bool atSeat = runtimeSeats[i] != null && Vector3.Distance(raw, runtimeSeatStations[i]) < 0.06f;
            if (!eating && runtimeSeats[i] != null && !atSeat && !runtimeDeparting[i])
                BeginChairExit(i, root, runtimeSeats[i]);
            runtimeSeated[i] = atSeat && !runtimeDeparting[i];
            runtimePresentRaw[i] = raw;
            runtimePresentTarget[i] = raw;
            runtimePresentExecution[i] = execution;
            runtimePresentAssigned[i] = false;
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            if (!runtimeDeparting[i]) continue;
            float t = Mathf.Clamp01((runtimeSeconds - runtimeExitStarted[i])
                / SliceRuntimePolicy.ChairExitSeconds);
            Vector3 path = Vector3.Lerp(runtimeExitFrom[i], runtimeExitTo[i],
                Mathf.SmoothStep(0f, 1f, t));
            runtimePresentTarget[i] = path;
            runtimePresentResolved[i] = path;
            runtimePresentAssigned[i] = true;
            if (t >= 1f)
            {
                runtimeDeparting[i] = false;
                Debug.Log("SLICE_CHAIR_EXIT " + JsonUtility.ToJson(new ChairExitLog {
                    resident = (long)sorted[i], phase = "clear", from = runtimeExitFrom[i],
                    to = runtimeExitTo[i], seconds = runtimeSeconds }));
            }
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            if (!runtimeSeated[i]) continue;
            runtimePresentResolved[i] = runtimePresentRaw[i];
            runtimePresentAssigned[i] = true;
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            if (runtimePresentAssigned[i]) continue;
            Vector3 target = runtimePresentTarget[i];
            var execution = runtimePresentExecution[i];
            if (execution == null || execution.Stage == ExecutionStage.Reserve)
            {
                int station = NearestWaitStation(runtimePresentRaw[i]);
                if (station >= 0) target = RuntimeWaiting[station];
            }
            for (int j = 0; j < sorted.Length; j++)
            {
                if (!runtimePresentAssigned[j]) continue;
                Vector3 away = target - runtimePresentResolved[j];
                away.y = 0f;
                float distance = away.magnitude;
                if (distance >= SliceRuntimePolicy.ResidentClearanceMeters) continue;
                if (distance < 0.001f)
                    away = Quaternion.Euler(0f, i * 45f + 15f, 0f) * Vector3.forward;
                else
                    away /= distance;
                target = runtimePresentResolved[j] + away * SliceRuntimePolicy.ResidentClearanceMeters;
            }
            runtimePresentResolved[i] = target;
            runtimePresentAssigned[i] = true;
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            var root = residentRoots[i];
            var execution = runtimePresentExecution[i];
            bool performing = execution != null && execution.Stage == ExecutionStage.Perform;
            bool travelling = execution != null && execution.Stage == ExecutionStage.Travel;
            bool eating = performing && execution.Activity.Code == "ACT-FOOD-02";
            bool seated = runtimeSeated[i];
            bool departing = runtimeDeparting[i];
            string stage = execution == null ? "Idle" : execution.Stage.ToString();
            string clip;
            if (departing)
            {
                clip = "sit";
            }
            else clip = SliceRuntimePolicy.LocomotionClip(eating, seated, travelling, performing);
            if (travelling) runtimeTravelFrames++;
            if (travelling && runtimeClips[i] == "walk" && clip == "hold")
                runtimeWalkHoldSwitches++;

            if (seated) root.position = runtimePresentRaw[i];
            else
            {
                Vector3 target = runtimePresentResolved[i];
                Vector3 direction = target - root.position;
                direction.y = 0f;
                if (!departing && direction.sqrMagnitude > 0.00001f)
                    root.rotation = Quaternion.LookRotation(direction);
                root.position = target;
            }

            if (runtimeClips[i] != clip)
            {
                Debug.Log("SLICE_CLIP " + JsonUtility.ToJson(new ClipLog {
                    resident = (long)sorted[i], from = runtimeClips[i], to = clip,
                    stage = stage, seconds = runtimeSeconds }));
                foreach (var anim in root.GetComponentsInChildren<Animation>())
                {
                    anim.Stop();
                    if (runtimeClips[i] == "eat") anim["eat"].RemoveMixingTransform(Named(root, "spine_01"));
                    anim[clip].layer = 0;
                    anim[clip].speed = clip == "sit" ? 0f : 1f;
                    anim[clip].wrapMode = WrapMode.Loop;
                    if (eating)
                    {
                        anim["sit"].speed = 0f;
                        anim.Play("sit");
                        anim["sit"].normalizedTime = 0.55f;
                        anim["eat"].layer = 1;
                        anim["eat"].AddMixingTransform(Named(root, "spine_01"));
                    }
                    anim.Play(clip, eating ? PlayMode.StopSameLayer : PlayMode.StopAll);
                    if (clip == "sit") anim[clip].normalizedTime = 0.55f;
                    anim.Sample();
                }
                runtimeClips[i] = clip;
            }
            if (seated)
            {
                var chair = runtimeSeats[i];
                SeatResident(root, chair);
                ValidateSeatSupport(root, chair, "runtime meal");
            }
        }
    }

    void BeginChairExit(int i, Transform root, Transform chair)
    {
        Vector3 seat = Named(chair, chair.name == "SeatFirst" ? "seat_cushion" : "seat_cushion_+1")
            .GetComponent<Renderer>().bounds.center;
        Vector3 back = Named(chair, chair.name == "SeatFirst" ? "back_cushion" : "back_cushion_+1")
            .GetComponent<Renderer>().bounds.center;
        Vector3 forward = seat - back;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.00001f) forward = Vector3.forward;
        forward.Normalize();
        runtimeExitFrom[i] = root.position;
        runtimeExitTo[i] = root.position + forward * SliceRuntimePolicy.ChairClearanceMeters;
        runtimeExitStarted[i] = runtimeSeconds;
        runtimeDeparting[i] = true;
        runtimeSeats[i] = null;
        Debug.Log("SLICE_CHAIR_EXIT " + JsonUtility.ToJson(new ChairExitLog {
            resident = (long)sorted[i], phase = "start", from = runtimeExitFrom[i],
            to = runtimeExitTo[i], seconds = runtimeSeconds }));
    }

    static int NearestWaitStation(Vector3 point)
    {
        int best = -1;
        float bestDistance = 0.35f;
        for (int i = 0; i < RuntimeStations.Length; i++)
        {
            float distance = Vector3.Distance(point, RuntimeStations[i]);
            if (distance <= bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    [Serializable] sealed class LiveResident
    {
        public long id;
        public string body, clip, stage;
        public Vector3 position;
        public bool visible, clothed, seated;
    }
    [Serializable] sealed class LiveObservation
    {
        public float seconds;
        public long game_seconds;
        public int listeners;
        public bool ambience_playing;
        public LiveResident[] residents;
    }
    [Serializable] sealed class ClipLog
    {
        public long resident;
        public string from, to, stage;
        public float seconds;
    }
    [Serializable] sealed class ChairExitLog
    {
        public long resident;
        public string phase;
        public Vector3 from, to;
        public float seconds;
    }
    [Serializable] sealed class RecoveryLog
    {
        public string action, notice;
        public bool saves, loaded;
    }

    void ObserveRuntime()
    {
        var row = new LiveObservation { seconds = runtimeSeconds, game_seconds = Game.World.GameSeconds,
            listeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length,
            ambience_playing = runtimeAmbience.isPlaying, residents = new LiveResident[sorted.Length] };
        var planes = GeometryUtility.CalculateFrustumPlanes(sliceCamera);
        for (int i = 0; i < sorted.Length; i++)
        {
            var renderer = residentVisuals[i].body_renderer;
            var execution = Game.Executor.GetResidentStatus(sorted[i]).Execution;
            row.residents[i] = new LiveResident { id = (long)sorted[i], body = residentVisuals[i].base_body,
                clip = runtimeClips[i], stage = execution == null ? "Idle" : execution.Stage.ToString(),
                position = residentRoots[i].position, seated = runtimeClips[i] == "eat" || runtimeClips[i] == "sit",
                visible = renderer.enabled && renderer.gameObject.activeInHierarchy
                    && GeometryUtility.TestPlanesAABB(planes, renderer.bounds),
                clothed = Array.Exists(renderer.sharedMaterials, IsGarmentMaterial) };
        }
        Debug.Log("SLICE_LIVE " + JsonUtility.ToJson(row));
    }

    bool SaveRuntime(string reason)
    {
        var result = SaveV1.SaveGame(Game, Path.Combine(Application.persistentDataPath, "slice"));
        Debug.Log("SLICE_SAVE ok=" + result.Ok + " reason=" + reason + " seconds="
            + runtimeSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (!result.Ok)
        {
            Debug.LogError("SLICE_SAVE_FAILED reason=" + reason + " detail=" + result.Detail);
            ShowSaveNotice("Save failed: " + result.Detail, 8f);
            return false;
        }
        runtimeSaveNotice = "";
        runtimeSaveNoticeUntil = 0f;
        return true;
    }

    void ShowSaveNotice(string notice, float seconds)
    {
        runtimeSaveNotice = notice;
        runtimeSaveNoticeUntil = runtimeSeconds + seconds;
    }

    void OnApplicationQuit()
    {
        Debug.Log("SLICE_MOTION travel_frames=" + runtimeTravelFrames
            + " walk_hold_switches=" + runtimeWalkHoldSwitches);
        if (livePlay && runtimeReady && !failed && runtimeSavesEnabled) SaveRuntime("quit");
    }

    void OnGUI()
    {
        if (!livePlay || !runtimeReady) return;
        string caption = runtimeAccess.CaptionFor(
            runtimeSeconds < captionUntil ? runtimeCaption : "Birds and neighborhood ambience");
        GUI.Box(new Rect(20f, Screen.height - 48f, Screen.width - 40f, 32f), caption);
        if (!string.IsNullOrEmpty(runtimeSaveNotice) && runtimeSeconds < runtimeSaveNoticeUntil)
            GUI.Label(new Rect(20f, 16f, Screen.width - 40f, 28f), runtimeSaveNotice);
    }

    void EnsureVisuals()
    {
        if (visualsReady) return;
        Req(residentRoots != null && residentVisuals != null
            && residentRoots.Length == 8 && residentVisuals.Length == residentRoots.Length,
            "eight rendered residents with source bindings");
        CompensateSkinGrade();
        HideCoveredSkin();
        ApplyRenderQuality();
        visualsReady = true;
    }

    void Req(bool ok, string why) { if (!ok) throw new InvalidDataException(why); }

    void Run()
    {
        seatChecks.Clear();
        string saveDir = Path.Combine(outDir, "saves", "game");
        flow = new TitleFlow(saveDir, this);
        Req(flow.Screen == TitleScreen.Title, "title screen");
        Flow(0, "title screen=" + flow.Screen);

        Req(flow.Activate(TitleAction.NewGame).Ok, "title/new");
        Req(flow.PrepareNewGame(27L, 42L, null).Ok, "factory");
        var g = flow.Game;
        var rows = g.World.ResidentsSnapshot();
        Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        sorted = new ResidentId[rows.Length];
        for (int i = 0; i < rows.Length; i++) sorted[i] = rows[i].Id;
        BindResidentBodies();
        Req(flow.BeginPlay().Ok, "BODY-PRESET");
        g = flow.Game;
        // Visible staging only: a readable row inside the common room while
        // the verified simulation runs its own positions below.
        for (int i = 0; i < sorted.Length; i++)
            g.Executor.SetResidentPosition(sorted[i], new ApproachPose(-1f + i * 1.15f, 0f, 7.4f));
        Flow(1, "new residents=" + rows.Length);
        InitialStateHash = g.World.ComputeStateHash();
        trace.Add("initial hash=" + InitialStateHash);

        var hero = sorted[0];
        g.Executor.SetResidentPosition(hero, new ApproachPose(-2f, 0f, -2f));
        for (int n = 0; n < StateSchema.NeedCount; n++) g.World.SetNeed(Core, hero, (Need)n, 80f);
        g.World.SetNeed(Core, hero, Need.Hunger, 8f);
        g.World.SetTrait(Core, hero, TraitAxis.Active, 50);
        var relations = new Relationships(g.World);
        var history = relations.Complete(Core, new ActivityId("ACT-REL-04"), hero, sorted[1],
            loserDraw: 0, initiatorSentimentDraw: 0.99, recipientSentimentDraw: 0.99);
        Req(history.Applied, "known event");
        var unknown = g.World.AppendEvent(Core, "unobserved", sorted[2], "not resident knowledge").Event;
        var autonomy = new Autonomy(g.World, g.Arbitrator);
        var boundary = new ProposalBoundary(g.World, g.Clock, g.Arbitrator, autonomy);
        var payload = boundary.BuildDecisionPayload(hero);
        var transport = new NoNetwork();
        var adapter = new DecisionAdapter(boundary, transport);
        g.Food.SetHomeStock(Core, ResidentHome(g, hero), 4, 2);
        float hungerBefore = heroNeed(g, hero);
        var req = adapter.RequestDecision(hero, new ServiceEndpoint("local", "https://example.invalid", "d1-ultra", false), false, true);
        Req(req == null && transport.Sends == 0 && autonomy.Decisions.Count == 1, "local fallback");
        var decision = autonomy.Decisions[0];
        var pending = g.Arbitrator.GetPending(hero);
        Req(decision.HasChoice && pending.Length == 1, "decision enqueued chosen=" + (decision.HasChoice ? decision.Chosen.Code : "none"));
        var action = pending[0].Action;
        string activity = decision.Chosen.Code;

        g.Executor.SetResidentPosition(hero, new ApproachPose(-2f, 0f, -2f));
        Tick(g, 500);
        var execution = g.Executor.GetExecution(action);
        float hungerAfter = heroNeed(g, hero);
        int committed = 0;
        foreach (var e in g.World.EffectLedger) if (e.Action == action && e.State == LedgerState.Committed) committed++;
        Req(execution != null && execution.Outcome == TerminalOutcome.Completed && committed == 1,
            "physical cook outcome=" + (execution == null ? "null" : execution.Outcome.ToString()) + " committed=" + committed);
        trace.Add("physical " + activity + " hunger " + hungerBefore + "->" + hungerAfter);

        var panel = new ResidentPanel(g.World, g.Arbitrator);
        string reason;
        Req(panel.TryInspect(hero, out ResidentPanelSnapshot snap, out reason)
            && panel.TryExplain(hero, history.Event, out MemoryEntry expl, out reason), "inspect/explain " + reason);
        Flow(2, "inspect resident=" + ((long)hero) + " name=" + snap.Resident.DisplayName, RootOf(hero));

        var nav = new WorldNavigation(g.World, g.Arbitrator, g.Executor);
        nav.OpenMap();
        Req(nav.IsMapOpen, "map open");
        Flow(3, "map open followed=" + nav.FollowedResident);
        Req(nav.SelectResident(hero).Accepted && nav.FollowResident(hero).Accepted && nav.FollowedResident == hero, "follow");
        Flow(4, "follow resident=" + ((long)hero), RootOf(hero));
        Req(nav.TravelTo(hero, 9L, Blockparty.Contracts.QueueMode.Immediate, TravelTargetKind.Object).Accepted, "takeover travel");
        Tick(g, 100);
        Flow(5, "takeover travel to station 9 pos=" + Pos(g, hero), RootOf(hero));

        // Interactions: one Female and one Male representative, each playing
        // walk/sit/reach/hold at a real NAV arrival.
        var interactions = new StringBuilder();
        var reps = new[] { sorted[0], sorted[1] };
        foreach (var rep in reps)
        {
            bool second = rep == reps[1];
            // Separate stations keep the two representatives from standing in
            // the same NAV approach slot while their clips are captured.
            long station = second ? 11L : 10L;
            float stationX = second ? 4f : 2f;
            g.Appearances.TryGet(rep, out AppearanceRecord look);
            string body = look.BaseBody.ToString();
            Req(nav.TravelTo(rep, station, Blockparty.Contracts.QueueMode.Immediate, TravelTargetKind.Object).Accepted,
                "interaction travel");
            Tick(g, 200);
            var pose = g.Executor.GetResidentPosition(rep);
            double err = Math.Sqrt((pose.X - stationX) * (pose.X - stationX) + (pose.Z - 2f) * (pose.Z - 2f));
            Req(err <= 0.15, "interaction NAV arrival " + err.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            int repIndex = RootIndex(RootOf(rep));
            foreach (string clip in new[] { "walk", "sit", "reach", "hold" })
            {
                var root = RootOf(rep);
                PoseSafe(repIndex, clip != "sit");
                if (clip == "sit")
                {
                    root.position = new Vector3(1.2f, 0f, 4.6f);
                    root.rotation = Quaternion.identity;
                    PlayClip(root, clip);
                    SeatResident(root);
                    CameraAt(new Vector3(3f, 1.35f, 5.6f), new Vector3(1.2f, 1.05f, 4.6f));
                }
                else
                {
                    root.position = new Vector3(stationX, 0f, 2f);
                    root.rotation = Quaternion.Euler(0f, 180f, 0f);
                    PlayClip(root, clip);
                    FrameResident(root, 2.5f, 1.35f,
                        clip == "walk" ? -0.9f : clip == "reach" ? 0.9f : 0.4f);
                }
                ValidateWorkflow("interaction-" + body + "-" + clip, clip == "sit" ? repIndex : 2);
                Debug.Log("SLICE_INTERACTION body=" + body + " clip=" + clip + " resident=" + repIndex + Joints(root));
                string frame = Capture("frames/" + body + "-" + clip + ".png");
                interactions.Append("    {\"resident_id\":").Append((long)rep)
                    .Append(",\"base_body\":\"").Append(body).Append("\",\"clip\":\"").Append(clip)
                    .Append("\",\"outcome\":\"Completed\",\"approach_error_m\":").Append(err.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"support\":\"").Append(clip == "sit" ? "SeatFirst" : "NAV station " + station)
                    .Append("\",\"note\":\"landed UAL clip played on the imported MakeHuman rig after NAV arrival; frame retained for visual inspection\",\"frame\":")
                    .Append(Ref(frame)).Append("},\n");
            }
        }

        var access = new Accessibility(flow.Settings);
        access.SetCaptions(true); access.SetNonColorCues(true);
        Req(access.TrySetTextScale(1.5f) && access.CaptionFor("Meal finished") == "Meal finished"
            && access.NonColorIndicator("Completed") == "[Completed]", "accessibility");
        Flow(9, "accessibility captions=on textScale=1.5 indicator=" + access.NonColorIndicator("Completed"));

        // Recorded audio: real grouped AudioSources on this director.
        ApplyAudio(flow.Settings);
        foodPlayed = PlayCue(foodClip, false, cueGroup);
        sleepPlayed = PlayCue(sleepClip, false, cueGroup);
        ambiencePlayed = PlayCue(ambienceClip, true, ambienceGroup);
        Req(foodPlayed && sleepPlayed && ambiencePlayed, "recorded audio playback");
        string cuesText = File.ReadAllText(Path.Combine(repoRoot, "Assets/Blockparty/Audio/Recorded/cues.json"));
        string foodCaption = Extract(cuesText, "food-completed", "caption"), foodIcon = Extract(cuesText, "food-completed", "icon");
        string sleepCaption = Extract(cuesText, "sleep-completed", "caption"), sleepIcon = Extract(cuesText, "sleep-completed", "icon");
        trace.Add("audio food played=" + foodPlayed + " sleep played=" + sleepPlayed + " ambience played=" + ambiencePlayed);

        // Save / continue / load through the real TitleFlow + SaveV1.
        var before = ResidentRows(g);
        Req(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.Save).Ok, "save");
        string saveFile = Path.Combine(outDir, "saves", "save.json");
        File.Copy(Path.Combine(saveDir, SaveFormat.FileName), saveFile, true);
        Flow(6, "save residents=" + sorted.Length);
        Req(flow.Activate(TitleAction.ReturnToTitle).Ok && flow.ContinueEnabled && flow.Activate(TitleAction.Continue).Ok, "continue");
        Flow(8, "continue reloaded residents=" + flow.Game.World.ResidentsSnapshot().Length);
        Req(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.ReturnToTitle).Ok
            && flow.Activate(TitleAction.Load).Ok && flow.LoadSavedGame().Ok, "load");
        Flow(7, "load residents=" + flow.Game.World.ResidentsSnapshot().Length);
        g = flow.Game;
        Req(SaveV1.SaveGame(g, saveDir).Ok, "resave");
        string reloadedFile = Path.Combine(outDir, "saves", "reloaded.json");
        File.Copy(Path.Combine(saveDir, SaveFormat.FileName), reloadedFile, true);
        var after = ResidentRows(g);
        Req(before == after, "save/reload projection");

        RecordNative("end");
        MirrorRoots(g);
        int finalHash = g.World.ComputeStateHash();
        string entryTrace = WriteTrace("entry");
        AppendEntry(finalHash, entryTrace);
        WriteReceipt(g, before, after, saveFile, reloadedFile, interactions.ToString(),
            hero, activity, hungerBefore, hungerAfter, committed,
            payload, unknown, transport.Sends, foodCaption, foodIcon, sleepCaption, sleepIcon);

        // Beauty pass for the evidence renders: residents in activities
        // inside the furnished common room (transforms only; the verified
        // simulation state above is untouched).
        PoseResidents();
        CameraHome();
        RecordSeat("slice.png", 2);
        Capture("../slice.png");
        // Back three-quarter close-up of a light-skinned resident: the angle
        // where shoulder/back skin showed through before the garment fit.
        sliceCamera.transform.position = new Vector3(2.68f, 1.75f, 6.57f);
        sliceCamera.transform.LookAt(new Vector3(3.4f, 1.05f, 4.6f));
        RecordSeat("slice-room.png", 2);
        Capture("../slice-room.png");
        // Face north-west so the west and north windows and the bathroom door are in frame.
        sliceCamera.transform.position = new Vector3(-8.8f, 2.2f, 3.5f);
        sliceCamera.transform.LookAt(new Vector3(-8.0f, 1.0f, 8.0f));
        Capture("../slice-apartment.png");
        CaptureFitCloseups();
        RestoreRenderQuality();
    }

    // Mask each garment as a closed volume: opening caps keep the head and
    // exposed limbs outside, unlike a distance-only shell around the cloth.
    void HideCoveredSkin()
    {
        if (residentRoots == null) return;
        int totalHidden = 0, totalKept = 0, copies = 0;
        foreach (var root in residentRoots)
        {
            if (root == null) continue;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!IsBodySkin(r) || r.sharedMesh == null) continue;
                var mesh = r.sharedMesh;
                var local = mesh.vertices;
                var covered = new bool[local.Length];
                int garments = 0;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (!IsGarmentMaterial(r.sharedMaterials[submesh])) continue;
                    new GarmentMask(mesh, submesh).MarkCovered(local, covered);
                    garments++;
                }
                Req(garments >= 2, "combined garment submeshes " + root.name);
                var masked = MaskSkinTriangles(r, covered, out int hidden, out int kept);
                masked.name = mesh.name + "_sliceMasked";
                unmaskedBodies.Add(r, mesh);
                r.sharedMesh = masked;
                ownedMeshes.Add(masked);
                totalHidden += hidden; totalKept += kept; copies++;
                Debug.Log("SLICE_MASK resident=" + root.name + " garments=" + garments
                    + " hidden=" + hidden + " kept=" + kept);
            }
        }
        Req(copies == 8, "eight combined body masks");
        Debug.Log("SLICE_MASK total copies=" + copies + " hidden=" + totalHidden + " kept=" + totalKept);
    }

    static bool IsGarmentMaterial(Material material)
    {
        return material != null && HasNamePart(material.name);
    }

    static bool HasNamePart(string name)
    {
        return name.IndexOf("-top__", StringComparison.Ordinal) >= 0
            || name.IndexOf("-bottom__", StringComparison.Ordinal) >= 0
            || name.IndexOf("-footwear__", StringComparison.Ordinal) >= 0;
    }

    static bool IsBodySkin(SkinnedMeshRenderer r)
    {
        foreach (var m in r.sharedMaterials)
            if (m != null && m.name.IndexOf("Skin_", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static bool IsSkinSubmesh(SkinnedMeshRenderer r, int submesh)
    {
        var materials = r.sharedMaterials;
        return submesh < materials.Length && materials[submesh] != null
            && materials[submesh].name.IndexOf("Skin_", StringComparison.Ordinal) >= 0;
    }

    // Filter covered body triangles out of every skin submesh. The eye and hair
    // submeshes are left alone, so only the skin surface is cut.
    Mesh MaskSkinTriangles(SkinnedMeshRenderer r, bool[] covered, out int hidden, out int kept)
    {
        var source = r.sharedMesh;
        var mesh = Instantiate(source);
        mesh.name = source.name + "_sliceMasked";
        hidden = 0; kept = 0;
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            if (!IsSkinSubmesh(r, s)) continue;
            var triangles = source.GetTriangles(s);
            var keep = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                // A triangle straddling an opening has one covered vertex and
                // two exposed ones; two covered vertices mean the triangle is
                // under the cloth and only its edge reaches the opening.
                int inside = (covered[a] ? 1 : 0) + (covered[b] ? 1 : 0) + (covered[c] ? 1 : 0);
                if (inside >= 2) { hidden++; continue; }
                keep.Add(a); keep.Add(b); keep.Add(c);
            }
            kept += keep.Count / 3;
            mesh.SetTriangles(keep, s);
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    sealed class GarmentMask
    {
        readonly List<Vector3> triangles = new List<Vector3>();
        readonly int surfaceVertices;
        readonly Bounds bounds;
        static readonly Vector3 Ray = new Vector3(0.137f, 0.241f, 1f).normalized;

        public GarmentMask(Mesh mesh, int submesh)
        {
            var vertices = mesh.vertices;
            var indices = mesh.GetTriangles(submesh);
            var representatives = new Dictionary<Vector3, int>();
            var canon = new int[vertices.Length];
            foreach (int index in indices)
            {
                Vector3 p = vertices[index];
                p = new Vector3(Mathf.Round(p.x * 10000f), Mathf.Round(p.y * 10000f), Mathf.Round(p.z * 10000f));
                if (!representatives.TryGetValue(p, out int rep)) { rep = index; representatives.Add(p, rep); }
                canon[index] = rep;
            }
            var edges = new Dictionary<long, int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                if (canon[indices[i]] == canon[indices[i + 1]] || canon[indices[i + 1]] == canon[indices[i + 2]]
                    || canon[indices[i]] == canon[indices[i + 2]]) continue;
                for (int j = 0; j < 3; j++)
                {
                    int index = indices[i + j];
                    triangles.Add(vertices[index]);
                    int x = canon[index], y = canon[indices[i + (j + 1) % 3]];
                    long key = ((long)Mathf.Min(x, y) << 32) | (uint)Mathf.Max(x, y);
                    edges.TryGetValue(key, out int count);
                    edges[key] = count + 1;
                }
            }
            surfaceVertices = triangles.Count;
            var links = new Dictionary<int, List<int>>();
            foreach (var edge in edges)
            {
                // Odd incidence closes the landed garments' overlapping crotch flap too.
                if ((edge.Value & 1) == 0) continue;
                int x = (int)(edge.Key >> 32), y = (int)edge.Key;
                if (!links.ContainsKey(x)) links.Add(x, new List<int>());
                if (!links.ContainsKey(y)) links.Add(y, new List<int>());
                links[x].Add(y); links[y].Add(x);
            }
            var seen = new HashSet<int>();
            foreach (int start in links.Keys)
            {
                if (seen.Contains(start)) continue;
                var loop = new List<int>();
                int current = start, previous = -1;
                while (seen.Add(current))
                {
                    loop.Add(current);
                    var neighbours = links[current];
                    if (neighbours.Count != 2) throw new InvalidOperationException("SLICE garment boundary is not a loop");
                    int next = neighbours[0] == previous ? neighbours[1] : neighbours[0];
                    previous = current; current = next;
                }
                Vector3 center = Vector3.zero;
                foreach (int index in loop) center += vertices[index];
                center /= loop.Count;
                for (int i = 0; i < loop.Count; i++)
                {
                    triangles.Add(center);
                    triangles.Add(vertices[loop[i]]);
                    triangles.Add(vertices[loop[(i + 1) % loop.Count]]);
                }
            }
            bounds = new Bounds(triangles[0], Vector3.zero);
            for (int i = 1; i < surfaceVertices; i++) bounds.Encapsulate(triangles[i]);
            bounds.Expand(0.04f);
        }

        public void MarkCovered(Vector3[] body, bool[] covered)
        {
            for (int i = 0; i < body.Length; i++)
            {
                Vector3 p = body[i];
                if (covered[i] || !bounds.Contains(p)) continue;
                int crossings = 0;
                float nearest = float.MaxValue;
                bool nearestIsCloth = false;
                for (int t = 0; t < triangles.Count; t += 3)
                {
                    Vector3 a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    float d = PointTriangleSq(p, a, b, c);
                    if (d < nearest) { nearest = d; nearestIsCloth = t < surfaceVertices; }
                    Vector3 ab = b - a, ac = c - a;
                    Vector3 h = Vector3.Cross(Ray, ac);
                    float determinant = Vector3.Dot(ab, h);
                    if (Mathf.Abs(determinant) < 1e-8f) continue;
                    float inv = 1f / determinant;
                    Vector3 fromA = p - a;
                    float u = inv * Vector3.Dot(fromA, h);
                    if (u < 0f || u > 1f) continue;
                    Vector3 q = Vector3.Cross(fromA, ab);
                    float v = inv * Vector3.Dot(Ray, q);
                    if (v < 0f || u + v > 1f) continue;
                    if (inv * Vector3.Dot(ac, q) > 1e-6f) crossings++;
                }
                // Coarse cloth may lie a few mm inside the body. Extend its
                // side surface by 2 cm, but never extend an opening cap.
                covered[i] = (crossings & 1) != 0 || (nearestIsCloth && nearest < 0.02f * 0.02f);
            }
        }

        // Closest point on a triangle (Ericson, Real-Time Collision Detection).
        static float PointTriangleSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return (p - a).sqrMagnitude;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return (p - b).sqrMagnitude;
            float vc = d1 * d4 - d3 * d2;
            float denom = d1 - d3;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f && denom > 1e-12f)
            {
                float v = d1 / denom;
                return (p - (a + ab * v)).sqrMagnitude;
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return (p - c).sqrMagnitude;
            float vb = d5 * d2 - d1 * d6;
            denom = d2 - d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f && denom > 1e-12f)
            {
                float w = d2 / denom;
                return (p - (a + ac * w)).sqrMagnitude;
            }
            float va = d3 * d6 - d5 * d4;
            denom = (d4 - d3) + (d5 - d6);
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f && denom > 1e-12f)
            {
                float w = (d4 - d3) / denom;
                return (p - (b + (c - b) * w)).sqrMagnitude;
            }
            denom = va + vb + vc;
            if (Mathf.Abs(denom) < 1e-12f) return Mathf.Min((p - a).sqrMagnitude, Mathf.Min((p - b).sqrMagnitude, (p - c).sqrMagnitude));
            float baryV = vb / denom, baryW = vc / denom;
            return (p - (a + ab * baryV + ac * baryW)).sqrMagnitude;
        }
    }

    readonly Dictionary<Material, Color> skinCatalogFactors = new Dictionary<Material, Color>();
    static readonly Color SkinGradeCompensation = new Color(0.88f, 0.90f, 1.02f, 1f);

    void CompensateSkinGrade()
    {
        skinCatalogFactors.Clear();
        for (int resident = 0; resident < residentRoots.Length; resident++)
        {
            var root = residentRoots[resident];
            if (root == null) continue;
            Texture2D tex = skins != null ? skins[(resident % 2 == 1 ? 2 : 0) + ((resident / 2) % 2)] : null;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !source.name.StartsWith("Skin_", StringComparison.Ordinal)) continue;
                    var copy = new Material(source);
                    copy.name = source.name + "_sliceComp";
                    if (source.name.Contains("Skin_CC0") && tex != null)
                    {
                        copy.color = Color.white;
                        if (copy.HasProperty("_BaseMap")) copy.SetTexture("_BaseMap", tex);
                        copy.mainTexture = tex;
                    }
                    ownedMaterials.Add(copy);
                    skinCatalogFactors.Add(copy, copy.color);
                    copy.color *= SkinGradeCompensation;
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
        }
        Req(skinCatalogFactors.Count == 8, "eight skin compensation instances");
        Debug.Log("SLICE_SKIN compensation=" + SkinGradeCompensation + " catalogInstances=" + skinCatalogFactors.Count);
    }

    // Skinned bone matrices are normally rebuilt by the player loop; the
    // captures render mid-frame after Animation.Sample, so force the rebuild.
    // The URP asset's single 50 m cascade makes the sunlit floor edge step at
    // ~2.4 cm per shadow texel; four cascades over 30 m keep it sub-pixel
    // without a heavier atlas on the integrated GPU.
    UniversalRenderPipelineAsset shadowAsset;
    int shadowCascadesBefore;
    float shadowDistanceBefore;
    int shadowResolutionBefore;

    void ApplyRenderQuality()
    {
        if (residentRoots != null)
            foreach (var root in residentRoots)
                if (root != null)
                    foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                        r.forceMatrixRecalculationPerRender = true;
        shadowAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (shadowAsset == null) { Debug.Log("SLICE_SHADOW no URP asset"); return; }
        shadowCascadesBefore = shadowAsset.shadowCascadeCount;
        shadowDistanceBefore = shadowAsset.shadowDistance;
        shadowResolutionBefore = shadowAsset.mainLightShadowmapResolution;
        shadowAsset.shadowCascadeCount = 4;
        shadowAsset.shadowDistance = 30f;
        shadowAsset.mainLightShadowmapResolution = 2048;
        Debug.Log("SLICE_SHADOW cascades=" + shadowCascadesBefore + "->4 distance=" + shadowDistanceBefore
            + "->30 resolution=" + shadowResolutionBefore + "->2048");
    }

    void RestoreRenderQuality()
    {
        if (shadowAsset == null) return;
        shadowAsset.shadowCascadeCount = shadowCascadesBefore;
        shadowAsset.shadowDistance = shadowDistanceBefore;
        shadowAsset.mainLightShadowmapResolution = shadowResolutionBefore;
        shadowAsset = null;
    }

    void PoseResidents() { PoseSafe(-1, true); }

    void PoseSafe(int skip, bool keepSeat)
    {
        // Common room spans x[-2,8], z[2.5,9.5]; seat chair at (1.2,0,4.6).
        for (int i = 0; i < 8; i++)
        {
            if (i == skip) continue;
            switch (i)
            {
                case 0: Pose(i, new Vector3(4.6f, 0f, 7.2f), 200f, "eat"); break;
                case 1: Pose(i, new Vector3(0.2f, 0f, 7.6f), 90f, "hold"); break;
                case 2:
                    if (keepSeat) Pose(i, new Vector3(1.2f, 0f, 4.6f), 0f, "sit");
                    else Pose(i, new Vector3(-1.3f, 0f, 8.6f), 150f, "hold");
                    break;
                case 3: Pose(i, new Vector3(3.4f, 0f, 4.6f), 160f, "walk"); break;
                case 4: Pose(i, new Vector3(7.5f, 0f, 4.8f), -30f, "reach"); break;
                case 5: Pose(i, new Vector3(7.4f, 0f, 6.7f), 210f, "hold"); break;
                case 6: Pose(i, new Vector3(2.5f, 0f, 8.8f), 170f, "hold"); break;
                case 7: Pose(i, new Vector3(7.3f, 0f, 8.9f), 160f, "hold"); break;
            }
        }
    }

    void ValidateWorkflow(string label, int seated)
    {
        for (int i = 0; i < residentRoots.Length; i++)
        {
            var root = residentRoots[i];
            Req(root != null && root.gameObject.activeInHierarchy, label + " inactive resident " + i);
            var anim = root.GetComponentInChildren<Animation>();
            Req(anim != null && anim.isPlaying, label + " unposed resident " + i);
        }
        Transform first = GameObject.Find("SeatFirst").transform;
        Transform second = GameObject.Find("SeatSecond").transform;
        for (int i = 0; i < residentRoots.Length; i++)
        {
            if (i == seated) continue;
            Req(Vector3.Distance(residentRoots[i].position, first.position) > 0.6f, label + " intersects SeatFirst");
            Req(Vector3.Distance(residentRoots[i].position, second.position) > 0.6f, label + " intersects SeatSecond");
        }
        if (seated >= 0) ValidateSeatSupport(residentRoots[seated], first, label);
        Debug.Log("SLICE_WORKFLOW " + label + " posed=8 seated=" + (seated < 0 ? "none" : residentRoots[seated].name));
    }

    void ValidateSeatSupport(Transform root, Transform chair, string label)
    {
        var seat = Named(chair, chair.name == "SeatFirst" ? "seat_cushion" : "seat_cushion_+1").GetComponent<Renderer>().bounds;
        var pelvis = Named(root, "pelvis").position;
        Req(pelvis.y >= seat.max.y - 0.02f && pelvis.y <= seat.max.y + 0.25f, label + " pelvis off cushion");
        Req(Mathf.Abs(pelvis.x - seat.center.x) <= 0.15f && Mathf.Abs(pelvis.z - seat.center.z) <= 0.2f,
            label + " pelvis outside cushion");
    }

    // Fit evidence per outfit at walk, hold and sit, from the back
    // three-quarter angle where the body skin used to show through.
    void CaptureFitCloseups()
    {
        var active = new bool[residentRoots.Length];
        for (int i = 0; i < residentRoots.Length; i++) active[i] = residentRoots[i].gameObject.activeSelf;
        try
        {
            FitShot(2, "everyday", "walk", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(2, "everyday", "hold", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(2, "everyday", "sit", 0.55f, new Vector3(1.2f, 0f, 4.6f), 0f, -1f, 1.35f, -1.8f);
            FitShot(3, "exercise", "walk", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(3, "exercise", "hold", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(3, "exercise", "sit", 0.55f, new Vector3(1.2f, 0f, 4.6f), 0f, -1f, 1.35f, -1.8f);
            FitShot(4, "sleep", "walk", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(4, "sleep", "hold", 0.3f, new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f);
            FitShot(4, "sleep", "sit", 0.55f, new Vector3(1.2f, 0f, 4.6f), 0f, -1f, 1.35f, -1.8f);
            foreach (int index in new[] { 2, 5, 6, 3, 4, 1 })
                foreach (string clip in new[] { "walk", "hold", "reach", "sit" })
                {
                    string outfit = index == 2 || index == 5 ? "everyday" : index == 6 || index == 3 ? "exercise" : "sleep";
                    string body = index % 2 == 1 ? "Male" : "Female";
                    FitShot(index, outfit, clip, clip == "sit" ? 0.55f : 0.3f,
                        new Vector3(6f, 0f, 4f), 180f, -2f, 1.35f, 0.7f,
                        "../r1-fit/" + body + "-" + outfit + "-" + clip + ".png");
                }
        }
        finally
        {
            for (int i = 0; i < residentRoots.Length; i++) residentRoots[i].gameObject.SetActive(active[i]);
        }
    }

    void FitShot(int index, string outfit, string clip, float fraction, Vector3 pos, float rotY,
        float distance, float height, float side, string proofPath = null)
    {
        if (residentRoots == null || index >= residentRoots.Length || residentRoots[index] == null) return;
        for (int i = 0; i < residentRoots.Length; i++)
            if (residentRoots[i] != null) residentRoots[i].gameObject.SetActive(i == index);
        var root = residentRoots[index];
        root.position = clip == "sit" ? new Vector3(1.2f, 0f, 4.6f) : pos;
        root.rotation = Quaternion.Euler(0f, clip == "sit" ? 0f : rotY, 0f);
        PlayClip(root, clip, fraction);
        if (clip == "sit") SeatResident(root);
        if (clip == "sit")
            CameraAt(new Vector3(3f, 1.35f, 5.6f), new Vector3(1.2f, 1.05f, 4.6f));
        else FrameResident(root, distance, height, side);
        if (proofPath == null) Debug.Log("SLICE_FIT outfit=" + outfit + " clip=" + clip + Joints(root));
        if (proofPath == null && outfit == "everyday" && clip == "sit") ExportTrouserDiagnostic(root);
        if (proofPath == null && clip == "sit") RecordSeat("fit-" + outfit + "-sit.png", index);
        string path = clip == "hold" ? "../inspection/fit-" + outfit + "-hold.png" : "../fit-" + outfit + "-" + clip + ".png";
        Capture(proofPath ?? path);
        // Move the previous target out of the chair before the next shot.
        root.position = new Vector3(-1.5f, 0f, 8.5f);
        root.rotation = Quaternion.identity;
    }

    string Joints(Transform root)
    {
        return " head=" + Px(root, "head") + " neck=" + Px(root, "neck_01")
            + " upperarm_l=" + Px(root, "upperarm_l") + " upperarm_r=" + Px(root, "upperarm_r")
            + " lowerarm_l=" + Px(root, "lowerarm_l") + " lowerarm_r=" + Px(root, "lowerarm_r")
            + " hand_l=" + Px(root, "hand_l") + " hand_r=" + Px(root, "hand_r")
            + " calf_l=" + Px(root, "calf_l") + " calf_r=" + Px(root, "calf_r")
            + " foot_l=" + Px(root, "foot_l") + " foot_r=" + Px(root, "foot_r")
            + " thigh_l=" + Px(root, "thigh_l") + " thigh_r=" + Px(root, "thigh_r");
    }

    string Px(Transform root, string bone)
    {
        Vector3 p = sliceCamera.WorldToViewportPoint(Named(root, bone).position);
        return Mathf.RoundToInt(p.x * 960f) + "," + Mathf.RoundToInt((1f - p.y) * 540f);
    }

    [Serializable]
    sealed class SeatObservation
    {
        public string image, resident, chair;
        public float angle_degrees;
        public Vector3 chair_forward, pelvis_forward, pelvis, hip_left, hip_right, knee_left, knee_right;
        public Vector3 seat_center, seat_min, seat_max, backrest_center, backrest_min, backrest_max;
        public Vector3 lower_back;
    }
    [Serializable]
    sealed class SeatObservations { public List<SeatObservation> residents; }
    readonly List<SeatObservation> seatChecks = new List<SeatObservation>();

    static Transform Named(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        throw new InvalidDataException("Missing seat landmark " + name);
    }

    void SeatResident(Transform root)
    {
        SeatResident(root, GameObject.Find("SeatFirst").transform);
    }

    void SeatResident(Transform root, Transform chair)
    {
        var seat = Named(chair, chair.name == "SeatFirst" ? "seat_cushion" : "seat_cushion_+1").GetComponent<Renderer>().bounds;
        var back = Named(chair, chair.name == "SeatFirst" ? "back_cushion" : "back_cushion_+1").GetComponent<Renderer>().bounds;
        Vector3 forward = seat.center - back.center; forward.y = 0f;
        root.rotation = Quaternion.LookRotation(forward, Vector3.up);
        var pelvis = Named(root, "pelvis");
        // The sampled sit clip offsets its hips from the root; anchor the hips to the cushion.
        root.position += new Vector3(seat.center.x - pelvis.position.x,
            seat.max.y + 0.06f - pelvis.position.y, seat.center.z - pelvis.position.z);
    }

    void RecordSeat(string image, int index)
    {
        var root = residentRoots[index];
        var chair = GameObject.Find("SeatFirst").transform;
        var seat = Named(chair, "seat_cushion").GetComponent<Renderer>().bounds;
        var back = Named(chair, "back_cushion").GetComponent<Renderer>().bounds;
        var pelvis = Named(root, "pelvis");
        var left = Named(root, "thigh_l"); var right = Named(root, "thigh_r");
        // Anatomical forward from the hip line, not the rig's arbitrary bone axes.
        Vector3 forward = Vector3.Cross(right.position - left.position, Vector3.up).normalized;
        Vector3 chairForward = seat.center - back.center; chairForward.y = 0f; chairForward.Normalize();
        var row = new SeatObservation { image = image, resident = root.name, chair = chair.name,
            angle_degrees = Vector3.Angle(forward, chairForward), chair_forward = chairForward,
            lower_back = Named(root, "spine_01").position,
            pelvis_forward = forward, pelvis = pelvis.position, hip_left = left.position, hip_right = right.position,
            knee_left = Named(root, "calf_l").position, knee_right = Named(root, "calf_r").position,
            seat_center = seat.center, seat_min = seat.min, seat_max = seat.max,
            backrest_center = back.center, backrest_min = back.min, backrest_max = back.max };
        seatChecks.Add(row);
        File.WriteAllText(Path.Combine(outDir, "..", "seat-check.json"),
            JsonUtility.ToJson(new SeatObservations { residents = seatChecks }, true));
        Debug.Log("SLICE_SEAT " + image + " " + root.name + " angle=" + row.angle_degrees
            + " pelvis=" + row.pelvis + " knees=" + row.knee_left + "," + row.knee_right);
    }

    void ExportTrouserDiagnostic(Transform root)
    {
        int resident = RootIndex(root);
        var visual = residentVisuals[resident];
        var renderer = visual.body_renderer;
        Req(renderer != null && visual.bottom_vertex_count > 0, "combined trouser source binding");
        var previousTarget = sliceCamera.targetTexture;
        sliceCamera.targetTexture = rt;
        var mesh = new Mesh();
        renderer.BakeMesh(mesh);
        var vertices = mesh.vertices;
        var rest = renderer.sharedMesh.vertices;
        var weights = renderer.sharedMesh.boneWeights;
        int start = visual.bottom_vertex_start, count = visual.bottom_vertex_count;
        Req(start >= 0 && start + count <= vertices.Length, "combined trouser vertex range");
        var csv = new StringBuilder("vertex,x,y,z,screen_x,screen_y,depth,rest_x,rest_y,rest_z,bone0,weight0,bone1,weight1,bone2,weight2,bone3,weight3,canonical_x,canonical_y,canonical_z\n");
        for (int local = 0; local < count; local++)
        {
            int i = start + local;
            Vector3 world = renderer.transform.TransformPoint(vertices[i]);
            Vector3 screen = sliceCamera.WorldToViewportPoint(world);
            Vector3 canonical = new Vector3(1.2f, 0f, 4.6f)
                + Quaternion.Euler(0f, 180f, 0f) * root.InverseTransformPoint(world);
            BoneWeight w = weights[i];
            csv.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20}\n",
                local, world.x, world.y, world.z, screen.x * 960f, (1f - screen.y) * 540f, screen.z,
                rest[i].x, rest[i].y, rest[i].z,
                renderer.bones[w.boneIndex0].name, w.weight0, renderer.bones[w.boneIndex1].name, w.weight1,
                renderer.bones[w.boneIndex2].name, w.weight2, renderer.bones[w.boneIndex3].name, w.weight3, canonical.x, canonical.y, canonical.z);
        }
        string dir = Path.Combine(outDir, "..", "measurements");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "trouser-vertices.csv"), csv.ToString());
        int bottom = -1;
        var materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
            if (materials[i] != null && materials[i].name.Contains("everyday-bottom__")) { bottom = i; break; }
        Req(bottom >= 0, "combined everyday-bottom submesh");
        csv.Clear(); csv.Append("triangle,a,b,c\n");
        var triangles = mesh.GetTriangles(bottom);
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i] - start, b = triangles[i + 1] - start, c = triangles[i + 2] - start;
            Req(a >= 0 && a < count && b >= 0 && b < count && c >= 0 && c < count, "combined trouser triangle range");
            csv.Append(i / 3).Append(',').Append(a).Append(',').Append(b).Append(',').Append(c).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, "trouser-triangles.csv"), csv.ToString());
        DestroyImmediate(mesh);
        sliceCamera.targetTexture = previousTarget;
    }

    void CameraHome()
    {
        if (sliceCamera == null) return;
        sliceCamera.transform.position = new Vector3(-1.6f, 2.5f, 3.2f);
        sliceCamera.transform.LookAt(new Vector3(3.5f, 0.9f, 7.8f));
    }

    void CameraAt(Vector3 position, Vector3 target)
    {
        if (sliceCamera == null) return;
        sliceCamera.transform.position = position;
        sliceCamera.transform.LookAt(target);
    }

    // Camera in front of a resident (their local +z is root.forward); side is
    // a lateral offset for a three-quarter view.
    void FrameResident(Transform root, float distance, float height, float side)
    {
        if (root == null || sliceCamera == null) return;
        Vector3 forward = root.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 feet = root.position;
        CameraAt(feet + forward * distance + right * side + Vector3.up * height,
            feet + Vector3.up * 1.05f);
    }

    void FrameFlow(int index, Transform focus)
    {
        switch (index)
        {
            case 0: CameraAt(new Vector3(1.0f, 2.4f, 4.0f), new Vector3(5.5f, 1.0f, 8.8f)); break;
            case 1: CameraAt(new Vector3(2.7f, 2.6f, 3.0f), new Vector3(2.7f, 1.0f, 7.4f)); break;
            case 2: FrameResident(focus, 2.1f, 1.45f, -0.6f); break;
            case 3: CameraAt(new Vector3(3.0f, 2.7f, 2.9f), new Vector3(3.0f, 0.2f, 5.6f)); break;
            case 4: FrameResident(focus, 2.8f, 1.5f, 0.9f); break;
            case 5: FrameResident(focus, 2.3f, 1.35f, 0.7f); break;
            case 6: CameraAt(new Vector3(-1.5f, 2.2f, 3.5f), new Vector3(3.5f, 1.0f, 8.0f)); break;
            case 7: CameraAt(new Vector3(7.4f, 2.3f, 3.5f), new Vector3(2.5f, 1.0f, 8.0f)); break;
            case 8: CameraAt(new Vector3(5.0f, 2.5f, 3.2f), new Vector3(1.0f, 1.0f, 8.2f)); break;
            case 9: CameraAt(new Vector3(2.7f, 2.55f, 2.9f), new Vector3(2.7f, 0.6f, 7.2f)); break;
        }
    }

    void Pose(int index, Vector3 pos, float rotY, string clip)
    {
        if (residentRoots == null || index >= residentRoots.Length || residentRoots[index] == null) return;
        residentRoots[index].position = pos;
        residentRoots[index].rotation = Quaternion.Euler(0f, rotY, 0f);
        PlayClip(residentRoots[index], clip, clip == "sit" ? 0.55f : 0.3f);
        if (clip == "sit") SeatResident(residentRoots[index]);
    }

    HomeId ResidentHome(GameStack g, ResidentId id)
    {
        g.World.TryGetResident(id, out ResidentRecord r);
        return r.Home;
    }

    float heroNeed(GameStack g, ResidentId id)
    {
        g.World.TryGetResident(id, out ResidentRecord r);
        return r.GetNeed(Need.Hunger);
    }

    string Pos(GameStack g, ResidentId id)
    {
        var p = g.Executor.GetResidentPosition(id);
        return p.X.ToString("0.##") + "," + p.Z.ToString("0.##");
    }

    void Tick(GameStack g, int count)
    {
        for (int i = 0; i < count; i++)
        {
            g.Clock.AdvanceRealSeconds(Core, 0.1);
            g.Food.Tick(StateWriter.ActivityExecutor);
        }
        MirrorRoots(g);
    }

    Transform RootOf(ResidentId id)
    {
        for (int i = 0; i < sorted.Length; i++) if (sorted[i] == id) return residentRoots[i];
        return residentRoots[0];
    }

    int RootIndex(Transform root)
    {
        for (int i = 0; i < residentRoots.Length; i++) if (residentRoots[i] == root) return i;
        throw new InvalidDataException("Unknown resident root " + root.name);
    }

    void MirrorRoots(GameStack g)
    {
        if (residentRoots == null || sorted == null) return;
        for (int i = 0; i < sorted.Length && i < residentRoots.Length; i++)
        {
            var p = g.Executor.GetResidentPosition(sorted[i]);
            residentRoots[i].position = new Vector3(p.X, 0f, p.Z);
            residentRoots[i].rotation = Quaternion.Euler(0f, 180f, 0f);
        }
    }

    void Flow(int index, string note, Transform focus = null)
    {
        trace.Add("flow " + FlowIds[index] + " " + note);
        flowTraces[index] = WriteTrace("flow-" + FlowIds[index]);
        MirrorRoots(flow.Game);
        PoseResidents();
        ValidateWorkflow("flow-" + FlowIds[index], 2);
        FrameFlow(index, focus);
        LogFlowJoints("flow-" + FlowIds[index]);
        flowFrames[index] = Capture("frames/flow-" + FlowIds[index] + ".png");
    }

    void LogFlowJoints(string frame)
    {
        for (int i = 0; i < residentRoots.Length; i++)
        {
            var root = residentRoots[i];
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            Debug.Log("SLICE_FLOWJOINT frame=" + frame + " resident=" + i + Joints(root));
        }
    }

    string WriteTrace(string name)
    {
        string path = Path.Combine(outDir, "traces", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
        File.WriteAllText(path, string.Join("\n", trace.ToArray()) + "\nflow-note " + name + "\n");
        return path;
    }

    void PlayClip(Transform root, string clip) { PlayClip(root, clip, 0.35f); }

    void PlayClip(Transform root, string clip, float fraction)
    {
        int played = 0;
        foreach (var anim in root.GetComponentsInChildren<Animation>())
        {
            var state = anim.GetClip(clip);
            if (state == null) throw new InvalidDataException("Missing clip " + clip + " on " + root.name);
            anim.Play(clip);
            anim[clip].time = anim[clip].length * fraction;
            anim.Sample();
            played++;
        }
        Req(played > 0, "No animation rig for clip " + clip + " on " + root.name);
    }

    bool PlayCue(AudioClip clip, bool loop, UnityEngine.Audio.AudioMixerGroup group)
    {
        if (clip == null || group == null) return false;
        var src = gameObject.AddComponent<AudioSource>();
        src.outputAudioMixerGroup = group;
        src.clip = clip; src.loop = loop; src.playOnAwake = false;
        src.Play();
        Debug.Log("SLICE_AUDIO cue=" + clip.name + " group=" + group.name + " playing=" + src.isPlaying);
        return src.isPlaying && src.outputAudioMixerGroup == group;
    }

    RenderTexture rt; Texture2D tex;
    string Capture(string rel, bool measurement = false)
    {
        string path = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (sliceCamera == null) { File.WriteAllBytes(path, new byte[0]); return path; }
        if (rt == null) { rt = new RenderTexture(960, 540, 24, RenderTextureFormat.DefaultHDR); tex = new Texture2D(960, 540, TextureFormat.RGB24, false); }
        var prevT = sliceCamera.targetTexture; var prevA = RenderTexture.active;
        sliceCamera.targetTexture = rt; sliceCamera.Render();
        RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply();
        sliceCamera.targetTexture = prevT; RenderTexture.active = prevA;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        if (!measurement) LogFlowJoints(Path.GetFileNameWithoutExtension(rel));
        if (!measurement && rel.StartsWith("../", StringComparison.Ordinal)) CaptureMeasurements(rel);
        if (!measurement && rel.StartsWith("frames/", StringComparison.Ordinal))
            CaptureIdMasks("../measurements/" + Path.GetFileNameWithoutExtension(rel) + "-ids.png");
        return path;
    }

    // Same camera and pose as the beauty/interaction frame, flat material IDs:
    // skin is magenta and each resident's garment is (0, (resident+1)/10, 1).
    void CaptureIdMasks(string rel)
    {
        var data = sliceCamera.GetUniversalAdditionalCameraData();
        bool post = data.renderPostProcessing;
        var clear = sliceCamera.clearFlags;
        var background = sliceCamera.backgroundColor;
        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        var originals = new Material[renderers.Length][];
        var temporary = new List<Material>();
        try
        {
            data.renderPostProcessing = false;
            sliceCamera.clearFlags = CameraClearFlags.SolidColor;
            sliceCamera.backgroundColor = Color.black;
            for (int i = 0; i < renderers.Length; i++)
            {
                originals[i] = renderers[i].sharedMaterials;
                var replacements = new Material[originals[i].Length];
                int resident = -1;
                for (int j = 0; j < residentRoots.Length; j++)
                    if (renderers[i].transform.IsChildOf(residentRoots[j])) { resident = j; break; }
                for (int j = 0; j < replacements.Length; j++)
                {
                    bool skin = originals[i][j] != null && originals[i][j].name.StartsWith("Skin_", StringComparison.Ordinal);
                    var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    material.SetColor("_BaseColor", resident < 0 ? Color.black : new Color(skin ? 1f : 0f, (resident + 1) / 10f, 1f));
                    replacements[j] = material;
                    temporary.Add(material);
                }
                renderers[i].sharedMaterials = replacements;
            }
            Capture(rel, true);
            var masked = new Dictionary<SkinnedMeshRenderer, Mesh>();
            var idMaterials = new Dictionary<SkinnedMeshRenderer, Material[]>();
            var partMeshes = new List<Mesh>();
            try
            {
                foreach (var pair in unmaskedBodies)
                {
                    var renderer = pair.Key;
                    if (!renderer.gameObject.activeInHierarchy) continue;
                    masked.Add(renderer, renderer.sharedMesh);
                    idMaterials.Add(renderer, renderer.sharedMaterials);
                    var mesh = Instantiate(pair.Value);
                    partMeshes.Add(mesh);
                    var weights = mesh.boneWeights;
                    var parts = new List<int>[9];
                    for (int part = 0; part < parts.Length; part++) parts[part] = new List<int>();
                    for (int submesh = 0; submesh < pair.Value.subMeshCount; submesh++)
                    {
                        int rendererIndex = Array.IndexOf(renderers, renderer);
                        if (originals[rendererIndex][submesh] == null || !originals[rendererIndex][submesh].name.StartsWith("Skin_", StringComparison.Ordinal)) continue;
                        var indices = pair.Value.GetTriangles(submesh);
                        for (int t = 0; t < indices.Length; t += 3)
                        {
                            int part = BodyPart(renderer.bones[weights[indices[t]].boneIndex0].name);
                            for (int v = 0; v < 3; v++) parts[part].Add(indices[t + v]);
                        }
                        mesh.SetTriangles(Array.Empty<int>(), submesh);
                    }
                    var materials = new List<Material>(renderer.sharedMaterials);
                    Color residentColor = materials[0].GetColor("_BaseColor");
                    int first = mesh.subMeshCount;
                    mesh.subMeshCount += parts.Length;
                    for (int part = 0; part < parts.Length; part++)
                    {
                        mesh.SetTriangles(parts[part], first + part);
                        var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                        material.SetColor("_BaseColor", new Color(part / 9f, residentColor.g, 1f));
                        temporary.Add(material); materials.Add(material);
                    }
                    renderer.sharedMesh = mesh;
                    renderer.sharedMaterials = materials.ToArray();
                }
                Capture(rel.Replace("-ids.png", "-exposed-ids.png"), true);
            }
            finally
            {
                foreach (var pair in masked) pair.Key.sharedMesh = pair.Value;
                foreach (var pair in idMaterials) pair.Key.sharedMaterials = pair.Value;
                foreach (var mesh in partMeshes) DestroyImmediate(mesh);
            }
        }
        finally
        {
            for (int i = 0; i < renderers.Length; i++)
                if (originals[i] != null) renderers[i].sharedMaterials = originals[i];
            foreach (var material in temporary) DestroyImmediate(material);
            sliceCamera.clearFlags = clear;
            sliceCamera.backgroundColor = background;
            data.renderPostProcessing = post;
        }
    }

    static int BodyPart(string bone)
    {
        if (bone == "head") return 1;
        if (bone.StartsWith("neck", StringComparison.Ordinal)) return 2;
        if (bone == "lowerarm_l") return 3;
        if (bone == "lowerarm_r") return 4;
        if (bone == "upperarm_l") return 7;
        if (bone == "upperarm_r") return 8;
        if (bone.StartsWith("hand_", StringComparison.Ordinal) || bone.StartsWith("thumb_", StringComparison.Ordinal)
            || bone.StartsWith("index_", StringComparison.Ordinal) || bone.StartsWith("middle_", StringComparison.Ordinal)
            || bone.StartsWith("ring_", StringComparison.Ordinal) || bone.StartsWith("pinky_", StringComparison.Ordinal))
            return bone.EndsWith("_l", StringComparison.Ordinal) ? 5 : 6;
        return 0;
    }

    // Same camera and geometry: ID masks give visible skin and each resident,
    // and neutral flat light gives a reproducible skin reference (no grade).
    void CaptureMeasurements(string rel)
    {
        string name = Path.GetFileNameWithoutExtension(rel);
        var data = sliceCamera.GetUniversalAdditionalCameraData();
        bool post = data.renderPostProcessing;
        try
        {
            data.renderPostProcessing = false;
            Capture("../measurements/" + name + "-ungraded.png", true);
            CaptureIdMasks("../measurements/" + name + "-ids.png");
        }
        finally
        {
            data.renderPostProcessing = post;
        }
        var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        var enabled = new bool[lights.Length];
        var mode = RenderSettings.ambientMode;
        var ambient = RenderSettings.ambientLight;
        float intensity = RenderSettings.ambientIntensity;
        try
        {
            data.renderPostProcessing = false;
            for (int i = 0; i < lights.Length; i++) { enabled[i] = lights[i].enabled; lights[i].enabled = false; }
            foreach (var pair in skinCatalogFactors) pair.Key.color = pair.Value;
            RenderSettings.ambientMode = AmbientMode.Flat;
            // Match the preset's mean hemispherical irradiance; neutralise hue
            // without turning the flat reference into a brighter light preset.
            Color mean = (RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor + RenderSettings.ambientGroundColor) / 3f;
            float neutral = mean.r * 0.2126f + mean.g * 0.7152f + mean.b * 0.0722f;
            RenderSettings.ambientLight = new Color(neutral, neutral, neutral).gamma;
            RenderSettings.ambientIntensity = 1f;
            Capture("../measurements/" + name + "-flat.png", true);
        }
        finally
        {
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = enabled[i];
            foreach (var pair in skinCatalogFactors) pair.Key.color = pair.Value * SkinGradeCompensation;
            RenderSettings.ambientMode = mode;
            RenderSettings.ambientLight = ambient;
            RenderSettings.ambientIntensity = intensity;
            data.renderPostProcessing = post;
        }
    }

    string Rel(string abs)
    {
        string full = Path.GetFullPath(abs);
        string root = Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar;
        return full.StartsWith(root) ? full.Substring(root.Length).Replace('\\', '/') : abs.Replace('\\', '/');
    }

    string HashOf(string abs)
    {
        using (var sha = SHA256.Create()) using (var s = File.OpenRead(abs))
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }

    string Ref(string abs) { return "{\"path\":\"" + Rel(abs) + "\",\"sha256\":\"" + HashOf(abs) + "\"}"; }
    string RefRepo(string rel) { return Ref(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar))); }

    string GuidOf(string rel)
    {
        string meta = Path.Combine(repoRoot, (rel + ".meta").Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(meta)) return null;
        foreach (var line in File.ReadAllLines(meta))
            if (line.StartsWith("guid: ")) return line.Substring(6).Trim();
        return null;
    }

    string Extract(string json, string cue, string key)
    {
        int i = json.IndexOf("\"" + cue + "\"", StringComparison.Ordinal);
        int j = json.IndexOf("\"" + key + "\"", i, StringComparison.Ordinal);
        int a = json.IndexOf('"', j + key.Length + 2) + 1;
        return json.Substring(a, json.IndexOf('"', a) - a);
    }

    void BindResidentBodies()
    {
        Req(residentVisuals.Length == sorted.Length, "rendered resident binding count");
        for (int i = 0; i < sorted.Length; i++)
        {
            var visual = residentVisuals[i];
            Req(visual != null && visual.body_renderer != null && visual.source_body_mesh != null
                && !string.IsNullOrEmpty(visual.source_asset), "rendered resident binding " + i);
            BaseBody body = visual.base_body == "Male" ? BaseBody.Male : BaseBody.Female;
            Req(visual.base_body == "Male" || visual.base_body == "Female", "rendered body value " + i);
            Req(flow.SetBaseBody(sorted[i], body).Ok, "BODY-PRESET " + i);
        }
    }

    string ResidentRows(GameStack g)
    {
        var sb = new StringBuilder();
        var rows = g.World.ResidentsSnapshot();
        Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            Req(g.Appearances.TryGet(r.Id, out AppearanceRecord look), "saved appearance " + r.Id);
            string body = look.BaseBody.ToString();
            var visual = residentVisuals[i];
            Req(visual != null && body == visual.base_body, "saved/rendered body mismatch " + r.Id);
            sb.Append("    {\"id\":").Append((long)r.Id).Append(",\"base_body\":\"").Append(body)
              .Append("\",\"mesh\":").Append(RefRepo(visual.source_asset)).Append("},\n");
        }
        return sb.ToString();
    }

    void AppendEntry(int finalHash, string entryTrace)
    {
        string dir = Path.Combine(outDir, "entries");
        Directory.CreateDirectory(dir);
        string name = "entry-" + InitialStateHash + "-" + finalHash + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
        string row = "{\"unity_version\":\"" + Application.unityVersion + "\",\"domain_reload\":false,\"scene_reload\":true,\"initial_state_hash\":"
            + InitialStateHash + ",\"final_state_hash\":" + finalHash + ",\"trace\":" + Ref(entryTrace) + "}";
        File.WriteAllText(Path.Combine(dir, name), row);
    }

    string PlayEntries()
    {
        string dir = Path.Combine(outDir, "entries");
        var rows = new List<string>();
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "entry-*.json")) rows.Add(File.ReadAllText(f).Trim());
        rows.Sort();
        return string.Join(",\n    ", rows.ToArray());
    }

    void WriteReceipt(GameStack g, string before, string after, string saveFile, string reloadedFile,
        string interactions, ResidentId hero, string activity, float hungerBefore, float hungerAfter,
        int committed, DecisionPayload payload, EventId unknown, int sends,
        string foodCaption, string foodIcon, string sleepCaption, string sleepIcon)
    {
        var assets = new List<string>();
        string[] gltfs = {
            "Assets/Blockparty/Art/Characters/MakeHuman/female.gltf", "Assets/Blockparty/Art/Characters/MakeHuman/male.gltf",
            "Assets/Blockparty/Art/Characters/second.gltf", "Assets/Blockparty/Art/Rigs/MakeHuman/humanoid.gltf",
            "Assets/Blockparty/Art/Clothing/first.gltf", "Assets/Blockparty/Art/Clothing/second.gltf",
            "Assets/Blockparty/Art/Animations/MakeHuman/first.gltf", "Assets/Blockparty/Art/Animations/second.gltf",
            "Assets/Blockparty/Art/Environment/apartment-first.gltf", "Assets/Blockparty/Art/Environment/second.gltf",
            "Assets/Blockparty/Art/Props/seat-first.gltf", "Assets/Blockparty/Art/Props/second.gltf",
            "Assets/Blockparty/Art/Characters/MakeHuman/materials/catalog.json",
            "Assets/Blockparty/Audio/Recorded/cues.json",
            "Assets/Blockparty/Audio/Recorded/second-cue.wav", "Assets/Blockparty/Audio/Recorded/wake.wav",
            "Assets/Blockparty/Audio/Recorded/day-birds.wav" };
        foreach (var a in gltfs) assets.Add(a);
        string catalog = File.ReadAllText(Path.Combine(repoRoot, "Assets/Blockparty/Art/Characters/MakeHuman/materials/catalog.json"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(catalog, "Assets/[^\"]+\\.png"))
            if (!assets.Contains(m.Value)) assets.Add(m.Value);
        var ab = new StringBuilder();
        foreach (var a in assets)
        {
            string guid = GuidOf(a);
            ab.Append("    ").Append(RefRepo(a).TrimEnd('}'));
            if (guid != null) ab.Append(",\"guid\":\"").Append(guid).Append("\"");
            ab.Append("},\n");
        }
        var needs = new StringBuilder();
        for (int i = 0; i < payload.Needs.Length; i++)
        {
            if (i > 0) needs.Append(",");
            needs.Append(payload.Needs[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        var known = new StringBuilder();
        for (int i = 0; i < payload.KnownHistoryEventIds.Length; i++)
        {
            if (i > 0) known.Append(",");
            known.Append(payload.KnownHistoryEventIds[i]);
        }
        var flows = new StringBuilder();
        for (int i = 0; i < FlowIds.Length; i++)
            flows.Append("    {\"id\":\"").Append(FlowIds[i]).Append("\",\"outcome\":\"Completed\",\"frame\":")
                .Append(Ref(flowFrames[i])).Append(",\"trace\":").Append(Ref(flowTraces[i])).Append("},\n");
        string physTrace = WriteTrace("physical");
        string decTrace = WriteTrace("decision");
        var j = new StringBuilder();
        j.Append("{\n  \"schema\": \"blockparty-slice/1\",\n  \"claim_scope\": \"M2 representative corner; local integration only\",\n");
        j.Append("  \"scene\": ").Append(RefRepo("Assets/Blockparty/Scenes/VerticalSlice.unity")).Append(",\n");
        j.Append("  \"director\": ").Append(RefRepo("Assets/Blockparty/Presentation/SliceDirector.cs")).Append(",\n");
        j.Append("  \"builder\": ").Append(RefRepo("Assets/Blockparty/Editor/SliceSceneBuild.cs")).Append(",\n");
        j.Append("  \"assets\": [\n").Append(ab.ToString().TrimEnd(',', '\n')).Append("\n  ],\n");
        j.Append("  \"residents\": [\n").Append(before.TrimEnd(',', '\n')).Append("\n  ],\n");
        j.Append("  \"save_reload\": {\"save\": ").Append(Ref(saveFile)).Append(", \"reloaded_save\": ").Append(Ref(reloadedFile))
            .Append(", \"before\": [\n").Append(before.TrimEnd(',', '\n')).Append("\n    ], \"after\": [\n").Append(after.TrimEnd(',', '\n')).Append("\n    ]},\n");
        j.Append("  \"interactions\": [\n").Append(interactions.TrimEnd(',', '\n')).Append("\n  ],\n");
        j.Append("  \"physical_action\": {\"resident_id\":").Append((long)hero).Append(",\"activity\":\"").Append(activity)
            .Append("\",\"outcome\":\"Completed\",\"committed_effects\":").Append(committed)
            .Append(",\"hunger_before\":").Append(hungerBefore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"hunger_after\":").Append(hungerAfter.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"trace\":").Append(Ref(physTrace)).Append("},\n");
        j.Append("  \"grounded_decision\": {\"resident_id\":").Append((long)hero)
            .Append(",\"mode\":\"local-fallback\",\"outcome\":\"Unprovisioned\",\"transport_sends\":").Append(sends)
            .Append(",\"chosen_activity\":\"").Append(activity).Append("\",\"known_history_ids\":[").Append(known)
            .Append("],\"unknown_event_id\":").Append((long)unknown).Append(",\"needs\":[").Append(needs)
            .Append("],\"trace\":").Append(Ref(decTrace)).Append("},\n");
        j.Append("  \"ui_flows\": [\n").Append(flows.ToString().TrimEnd(',', '\n')).Append("\n  ],\n");
        string audioTrace = WriteTrace("audio");
        float ambDb = 0f, worldDb = 0f, voiceDb = 0f, ifaceDb = 0f;
        Req(audioMixer != null
            && audioMixer.GetFloat("AmbienceVolume", out ambDb)
            && audioMixer.GetFloat("WorldVolume", out worldDb)
            && audioMixer.GetFloat("VoiceVolume", out voiceDb)
            && audioMixer.GetFloat("InterfaceVolume", out ifaceDb), "exposed mixer levels");
        j.Append("  \"audio\": [\n");
        j.Append("    {\"id\":\"food-completed\",\"wav\":").Append(RefRepo("Assets/Blockparty/Audio/Recorded/second-cue.wav"))
            .Append(",\"played\":").Append(foodPlayed ? "true" : "false").Append(",\"mixer_group\":\"").Append(cueGroupName)
            .Append("\",\"caption\":\"").Append(foodCaption).Append("\",\"visual_id\":\"").Append(foodIcon).Append("\",\"trace\":").Append(Ref(audioTrace)).Append("},\n");
        j.Append("    {\"id\":\"sleep-completed\",\"wav\":").Append(RefRepo("Assets/Blockparty/Audio/Recorded/wake.wav"))
            .Append(",\"played\":").Append(sleepPlayed ? "true" : "false").Append(",\"mixer_group\":\"").Append(cueGroupName)
            .Append("\",\"caption\":\"").Append(sleepCaption).Append("\",\"visual_id\":\"").Append(sleepIcon).Append("\",\"trace\":").Append(Ref(audioTrace)).Append("}\n  ],\n");
        j.Append("  \"ambience\": {\"wav\":").Append(RefRepo("Assets/Blockparty/Audio/Recorded/day-birds.wav"))
            .Append(",\"played\":").Append(ambiencePlayed ? "true" : "false").Append(",\"loop\":true,\"mixer_group\":\"").Append(ambienceGroupName)
            .Append("\",\"trace\":").Append(Ref(audioTrace)).Append("},\n");
        j.Append("  \"audio_listeners\": ").Append(FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length)
            .Append(",\n  \"audio_settings\": {\"mixer\":\"").Append(audioMixer.name)
            .Append("\",\"ambience_db\":").Append(ambDb.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"world_db\":").Append(worldDb.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"voice_db\":").Append(voiceDb.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"interface_db\":").Append(ifaceDb.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append("},\n");
        var rendered = new StringBuilder();
        for (int i = 0; i < residentVisuals.Length; i++)
        {
            var visual = residentVisuals[i];
            rendered.Append("    {\"resident_id\":").Append((long)sorted[i])
                .Append(",\"saved_body\":\"").Append(visual.base_body)
                .Append("\",\"rendered_source\":").Append(RefRepo(visual.source_asset))
                .Append(",\"body_renderer\":\"").Append(visual.body_renderer.name).Append("\"},\n");
        }
        j.Append("  \"rendered_bodies\": [\n").Append(rendered.ToString().TrimEnd(',', '\n')).Append("\n  ],\n");
        j.Append("  \"native_objects\": [\n    ").Append(string.Join(",\n    ", nativeCounts.ToArray())).Append("\n  ],\n");
        j.Append("  \"play_entries\": [\n    ").Append(PlayEntries()).Append("\n  ]\n}\n");
        File.WriteAllText(Path.Combine(outDir, "slice.json"), j.ToString());
    }

    void RecordNative(string phase)
    {
        int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
        int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
        int renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
        int textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
        int sliceMaterials = 0, sliceMeshes = 0;
        foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            if (material.name.EndsWith("_sliceComp", StringComparison.Ordinal)) sliceMaterials++;
        foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            if (mesh.name.EndsWith("_sliceMasked", StringComparison.Ordinal)) sliceMeshes++;
        if (phase == "start") nativeEntry++;
        string row = "{\"entry\":" + nativeEntry + ",\"phase\":\"" + phase + "\",\"materials\":" + materials
            + ",\"meshes\":" + meshes + ",\"render_textures\":" + renderTextures + ",\"textures\":" + textures
            + ",\"slice_materials\":" + sliceMaterials + ",\"slice_meshes\":" + sliceMeshes + "}";
        nativeCounts.Add(row);
        Debug.Log("SLICE_NATIVE " + row);
    }

    void ApplyAudio(SettingsRecord settings)
    {
        int listeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length;
        Req(listeners == 1, "exactly one AudioListener, found " + listeners);
        var bridge = GetComponent<AudioMixer>();
        Req(bridge != null && audioMixer != null && cueGroup != null && ambienceGroup != null, "audio mixer wiring");
        bridge.ApplySettings(settings);
        cueGroupName = cueGroup.name;
        ambienceGroupName = ambienceGroup.name;
        float ambience = 0f, world = 0f, voice = 0f, iface = 0f;
        Req(audioMixer.GetFloat("AmbienceVolume", out ambience)
            && audioMixer.GetFloat("WorldVolume", out world)
            && audioMixer.GetFloat("VoiceVolume", out voice)
            && audioMixer.GetFloat("InterfaceVolume", out iface), "exposed audio group settings");
        Debug.Log("SLICE_AUDIO_MIX listeners=" + listeners + " mixer=" + audioMixer.name
            + " ambience=" + ambience.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + " world=" + world.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + " voice=" + voice.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + " interface=" + iface.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    void OnDestroy()
    {
        RestoreRenderQuality();
        if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
        if (tex != null) { Destroy(tex); tex = null; }
        foreach (var material in ownedMaterials) if (material != null) Destroy(material);
        foreach (var mesh in ownedMeshes) if (mesh != null) Destroy(mesh);
        foreach (var go in ownedObjects) if (go != null) Destroy(go);
        ownedMaterials.Clear(); ownedMeshes.Clear(); ownedObjects.Clear();
    }

    void Update()
    {
        if (livePlay && runtimeReady && !failed)
        {
            try
            {
                runtimeSeconds += Time.unscaledDeltaTime;
                runtimeTick += Time.unscaledDeltaTime;
                int steps = SliceRuntimePolicy.StepsThisFrame(
                    runtimeTick, SliceRuntimePolicy.MaxSimulationStepsPerFrame);
                for (int step = 0; step < steps; step++)
                {
                    AdvanceRuntime();
                    runtimeTick -= 0.1f;
                }
                if (runtimeSavesEnabled && runtimeSeconds >= nextSave)
                {
                    if (SaveRuntime("autosave")) nextSave = runtimeSeconds + 30f;
                    else
                    {
                        Debug.Log("SLICE_SAVE_RETRY seconds=" + runtimeSeconds.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture) + " retry_seconds=5");
                        nextSave = runtimeSeconds + 5f;
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                failureReason = e.GetType().Name + ": " + e.Message;
                Debug.LogError("SLICE RUNTIME FAIL " + failureReason);
            }
            return;
        }
        if (flow != null && flow.Game != null && !done) MirrorRoots(flow.Game);
    }

    void LateUpdate()
    {
        if (!livePlay || !runtimeReady || failed) return;
        try
        {
            PresentRuntime();
            if (runtimeSeconds >= nextObservation) { ObserveRuntime(); nextObservation += 2f; }
        }
        catch (Exception e)
        {
            failed = true;
            failureReason = e.GetType().Name + ": " + e.Message;
            Debug.LogError("SLICE RUNTIME FAIL " + failureReason);
        }
    }
}
