using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// ALPHA@1 running-build director. One scene owns the whole M3 slice:
// a fresh seeded world plus the four landed BLOCK-SITES modules, two homes
// and twenty resident roots. A normal launch mirrors the live sim every frame
// (residents and buildings visible) and never writes evidence.
//
// Evidence modes are opt-in through the player command line:
//   --alpha-run=first  --alpha-out=<dir> --alpha-tree=<git tree> --alpha-binary-sha=<sha>
//       runs every class, the life/estate/care/birth/storm gates and the AI
//       local fallbacks through the real command path, captures one PNG per
//       class and per site, and saves two household contexts.
//   --alpha-run=second --alpha-out=<dir> --alpha-tree=... --alpha-binary-sha=...
//       reloads both contexts in a new process, proves normalized continuity,
//       continues one completed action per household and re-saves.
//   --alpha-run=smoke --alpha-smoke=<png>
//       renders the normal household launch for the developer's visual check.
// The logs are normalized observations of the real run; the receipt assembler
// hashes the artifacts and binds every log to the source tree and binary.
public sealed partial class AlphaDirector : MonoBehaviour, ISceneContent
{
    public const long WorldId = 27L;
    public const long Seed = 42L;
    public const long Day = 86400L;
    const long StepSeconds = 60L;

    static readonly StateWriter Core = StateWriter.SimCore;
    static readonly StateWriter Exec = StateWriter.ActivityExecutor;

    [Header("Wired by AlphaSceneBuild.BuildScene")]
    public Transform[] residentRoots;
    public Camera alphaCamera;
    public Transform[] siteMarkers;
    public ClassStage[] classStages;

    // One class frame's real place: the actor stand point, the capture camera
    // and its look target. All three are scene markers built with the props.
    [Serializable]
    public sealed class ClassStage
    {
        public string id;
        public Transform actor;
        public Transform camera;
        public Transform look;
    }

    [Header("Run state (read-only at runtime)")]
    public bool done;
    public bool failed;
    public string failureReason = "";

    sealed class Sim
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public AppearanceStore Appearances;
        public Accounts Accounts;
        public Food Food;
        public Expenses Expenses;
        public Ownership Ownership;
        public Home Home;
        public Club Club;
        public Housing Housing;
        public Creative Creative;
        public FamilyCreation Family;
        public Party Party;
        public Work Work;
        public Education Education;
        public Blockparty.Activities.Social Social;
        public Minigolf Minigolf;
        public Health Health;
        public Progression Progression;
        public SevereStorm Storm;
        public EstateCare Estate;
        public Aging Aging;
        public BlockSites Sites;
        public Autonomy Autonomy;
        public ProposalBoundary Boundary;
        public ResidentId[] Residents;
        public HomeId[] HomeIds;
        public HouseholdId[] HouseholdIds;
        public ObjectId Bed;
        public ObjectId[] CommonObjects;
        public ObjectId Roof;
        public ObjectId Electric;
        public ObjectId Outdoor;
        public ObjectId Shelter;
        public ObjectId[] SiteObjects;
        public long AgingStartGameSeconds;
    }

    sealed class ClassRow
    {
        public string Id;
        public ResidentId Resident;
        public string Activity;
        public string Site;
        public string Effects;
    }

    sealed class WalkSample
    {
        public double GameSeconds;
        public ApproachPose Position;
        public ResidentId Followed;
        public Vector3 Camera;
        public Quaternion Rotation;
    }

    sealed class WalkRow
    {
        public string Site;
        public ResidentId Resident;
        public readonly List<WalkSample> Samples = new List<WalkSample>();
        public readonly string[] Frames = new string[3];
    }

    readonly List<ClassRow> classes = new List<ClassRow>();
    readonly List<string> events = new List<string>();
    readonly List<string> aiEntries = new List<string>();
    readonly Dictionary<string, string> frames = new Dictionary<string, string>();
    string mode;
    string outDir;
    string tree;
    string binarySha;
    bool evidence;
    bool live;
    bool quitting;
    bool walking;
    BlockSite followSite;
    float followShift;
    Sim sim;
    LiveSimulation liveSim;
    float liveTick;
    const string LiveTuning = "Assets/Blockparty/Content/tuning.json";
    VolumeProfile toneProfile;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { }

    static string Arg(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                return args[i].Substring(key.Length + 1);
        }
        return string.Empty;
    }

    static string Q(string value)
    {
        string text = value ?? string.Empty;
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    static string N(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    static string N(long value) { return value.ToString(CultureInfo.InvariantCulture); }
    static string B(bool value) { return value ? "true" : "false"; }

    void Awake()
    {
        if (!Application.isEditor) Application.runInBackground = true;
        // Runtime profile keeps the post-processing settings out of the write
        // set; the renderer already enables URP post-processing project-wide.
        Volume volume = gameObject.AddComponent<Volume>();
        volume.isGlobal = true;
        toneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        Tonemapping tonemapping = toneProfile.Add<Tonemapping>(true);
        tonemapping.mode.Override(TonemappingMode.Neutral);
        ColorAdjustments adjustments = toneProfile.Add<ColorAdjustments>(true);
        adjustments.postExposure.Override(-0.35f);
        adjustments.contrast.Override(6f);
        adjustments.saturation.Override(8f);
        volume.sharedProfile = toneProfile;
    }

    IEnumerator Start()
    {
        string[] args = Environment.GetCommandLineArgs();
        mode = Arg(args, "--alpha-run");
        outDir = Arg(args, "--alpha-out");
        tree = Arg(args, "--alpha-tree");
        binarySha = Arg(args, "--alpha-binary-sha");
        evidence = mode.Length > 0;
        yield return null;
        if (mode == "smoke")
        {
            yield return RunSmoke();
            yield break;
        }
        if (mode == "walk")
        {
            yield return RunWalkGuarded();
            done = true;
            yield break;
        }
        // The splash honours the saved text size and motion settings before the world (and access) exist.
        if (!evidence)
        {
            string dir = Arg(args, "--alpha-save");
            SettingsLoadResult saved = SaveV1.LoadSettings(dir.Length > 0 ? dir : Path.Combine(Application.persistentDataPath, "alpha-save"));
            if (saved.Ok) { splashTextScale = saved.Settings.TextScale; splashReducedMotion = saved.Settings.ReducedMotion; }
        }
        // The world build blocks for seconds; put the splash card on screen first.
        while (!evidence && !splash.OnScreen && Time.realtimeSinceStartup < 20f) yield return null;
        try
        {
            if (mode == "first") { RunFirst(); done = true; }
            else if (mode == "second") { RunSecond(); done = true; }
            else StartLive();
        }
        catch (Exception e)
        {
            Fail(e.GetType().Name + ": " + e.Message);
            if (evidence)
            {
                done = true;
            }
        }
    }

    void Update()
    {
        if (evidence)
        {
            if (walking) ApplyCamera();
            if (done && !quitting)
            {
                quitting = true;
                Log("quit requested failed=" + B(failed));
                Application.Quit(failed ? 1 : 0);
            }
            return;
        }
        if (live)
        {
            if (controls != null && controls.Open)
            {
                // VOICE-LIVE: the Controls page talk row captures through the Voice settings.
                if (voice != null && voice.Capturing)
                {
                    voice.HandlePageKeys();
                    if (!voice.Capturing) controls.Tell(voice.PageMessage, voice.PageMessageIsError);
                }
                else AlphaControlsInput.Handle(controls, access);
            }
            else if (voice != null && voice.PageOpen) voice.HandlePageKeys();
            else if (voice != null && voice.Capturing) voice.CancelCapture();
            else if (!titleOpen && !settingsOpen && SamLookKeys()) { }   // SAM-LOOK: the typed Look box owns the keyboard
            else if (!titleOpen && GazetteKeys()) { }
            else
            {
                bool surface = HandleSurfaceKeys();
                if (titleOpen) { if (!surface && splash.Done) HandleTitleKeys(); }
                else HandleInput(surface);
            }
            if (!titleOpen) { LiveTick(); CraftsUpdate(); GazetteTick(); }
            // VOICE-LIVE (M5-OD-38): talk key and Hey Meta only in play, never under the title or a page.
            if (voice != null) voice.Tick(Time.unscaledDeltaTime, !titleOpen && !settingsOpen && !voice.PageOpen && !(controls != null && controls.Open) && !LookOpen && !failed);
            if (access != null) AudioListener.volume = muted ? 0f : access.Settings.MixerLevel;
        }
    }

    Accessibility access;
    InputMap input;
    WorldNavigation navigation;
    Selection selection;
    ActionMenu menu;
    Hud hud;
    CameraControl cameraControl;
    CursorState cursors;
    Vector3 cameraBase;
    Quaternion cameraRotation;
    ObjectId[] activityObjects;
    readonly HashSet<ActionInstanceId> liveRewards = new HashSet<ActionInstanceId>();
    const string ControlsHelp = "N/B: select resident | Space: action menu | J: conceive | K: move | L: rest | Escape: pause | F5: save | F9: reload";
    string noticeText = ControlsHelp;
    int noticeSeq;
    // Each assignment is a notice event; the HUD renews its toast on the sequence, not on text change.
    string notice { get { return noticeText; } set { noticeText = value; noticeSeq++; } }
    readonly AlphaHud alphaHud = new AlphaHud();
    // CONTROLS-LOGO (M5-OD-36/37): Settings > Controls rebind page and the startup splash.
    ControlsRebind controls;
    readonly AlphaSplash splash = new AlphaSplash();
    float splashTextScale = 1f;
    bool splashReducedMotion;
    AlphaVoice voice;   // VOICE-LIVE (M5-OD-38)
    ResidentPanel residentPanel;
    AlphaHud.Frame hudFrame;
    string saveDirectory;
    // #243 MusicPlayer's read-only view of live play; null until the live controls bind.
    public (bool Paused, SettingsRecord Settings, string SaveDirectory)? MusicView =>
        live && access != null ? (sim.Clock.Paused, access.Settings, saveDirectory) : ((bool, SettingsRecord, string)?)null;
    bool settingsOpen;
    // MISSING-UI (M5-OD-30): title gate, colour filter and mute.
    // Mute persists as the saved master mixer level 0 (LiveSettings); access keeps the audible level.
    bool titleOpen, continueOk, muted;
    string continueNote = "";
    int titleFocus, colorFilter, settingsFocus;
    // PARTY-LIVE plan dialog (M5-OD-50/51): host, start choice and invite toggles.
    bool planOpen;
    ResidentId planHost;
    int planTime = 1;
    readonly List<ResidentId> planGuests = new List<ResidentId>();
    // Live pings: slowdown explanations first, so one frame's payoff is not hidden by the next ping.
    LiveSimulation.PingBoard pings = new LiveSimulation.PingBoard();
    int activityIndex;
    ActionInstanceId? golfAction;
    ResidentId? menuResident;
    // M5-OD-49: SOCIAL partners for the selected resident (closest friend first, then nearest) and the chosen one.
    readonly List<ResidentId> socialPartners = new List<ResidentId>();
    ResidentId socialPartner;
    // JOBS & EDUCATION: sub-choices and job state
    readonly List<string> eligibleEduCodes = new List<string>();
    readonly List<string> eligibleEduTitles = new List<string>();
    string selectedEduCode;
    readonly Dictionary<(long, long), float> friendshipBefore = new Dictionary<(long, long), float>();
    long needsLogHour = -1L;
    static readonly string[] ClassNames = { "FOOD", "WORK", "EDUCATION", "SOCIAL", "PARTY", "MINIGOLF", "HEALTH", "CLUB", "HOME", "CREATIVE" };
    static readonly string[] ActivityCodes = { "ACT-FOOD-01", "ACT-WORK-01", "ACT-EDU-01", "ACT-REL-01", "ACT-PARTY-01", "ACT-SPORT-02", "ACT-HEALTH-02", "ACT-CLUB-01", "ACT-HOME-01", "ACT-CREA-02" };

    public GymTrailDirector gymTrail;
    public AlphaResidentLooks residentLooks;
    void BindControls()
    {
        if (access == null)
        {
            SettingsRecord defaults = new SettingsRecord(SaveV1.SettingsSchemaVersion, 1f, 1f, true,
                1f, 1f, 1f, 1f, true, true, new Dictionary<string, string>());
            access = string.IsNullOrEmpty(saveDirectory) ? new Accessibility(defaults) : LiveSettings.Load(saveDirectory, defaults, out muted);
            Log("settings loaded muted=" + B(muted) + " mixer=" + N(access.Settings.MixerLevel) + " textScale=" + N(access.Settings.TextScale)
                + " colorFilter=" + ColorFilter.Names[colorFilter]);
        }
        input = new InputMap(access, sim.Clock);
        navigation = new WorldNavigation(sim.World, sim.Commands, sim.Executor, sim.Sites);
        selection = new Selection(sim.World, navigation, access);
        menu = new ActionMenu(sim.World, sim.Commands, access, input);
        hud = new Hud(sim.World, sim.Commands, access, input);
        residentPanel = new ResidentPanel(sim.World, sim.Commands);
        cameraControl = new CameraControl(access, input, navigation, sim.Executor);
        cursors = new CursorState(access);
        controls = new ControlsRebind(access, SaveSettings, m => Log("input controls " + m));
        if (!evidence)
        {
            if (voice == null)
            {
                voice = new AlphaVoice(this, saveDirectory, Arg(Environment.GetCommandLineArgs(), "--alpha-voice-fake"), Log);
                // INTEGRATE-W5: a spoken "look for ..." phrase queues a SAM look (once, voice outlives rebinds).
                voice.LookRequested += phrase => Log("voice look queued=" + Blockparty.Presentation.SamLook.Find(phrase).ToString().ToLowerInvariant());
                voice.MinigolfRequested += VoiceMinigolf;
            }
            voice.Bind(sim.World, sim.Clock, sim.Commands, sim.Autonomy, sim.Sites, sim.Executor, access);
            voice.Present = PresentVoice;
            voice.Selected = () => navigation.SelectedResident;   // VOICE-SOCIAL (M5-OD-53)
            voice.Live = liveSim;
            voice.PartyVenue = () => (long)activityObjects[4];
            controls.TalkRow = voice.BeginCapture;
        }
        if(gymTrail!=null){gymTrail.hudOwnsPanel=true;gymTrail.Bind(sim.World,sim.Clock,sim.Commands,sim.Executor,sim.Residents[0]);}
        liveRewards.Clear();
        golfAction = null;
    }

    // The one camera application for live input and walk evidence. The follow
    // target comes from CameraControl (WorldNavigation + NAV), never a private pose.
    void ApplyCamera()
    {
        if (alphaCamera == null || cameraControl == null || CraftsCamera()) return;
        float panX = (float)cameraControl.PanX;
        float panZ = (float)cameraControl.PanZ;
        float yaw = (float)cameraControl.Rotation * 6f;
        if (cameraControl.FollowedResident.HasValue)
        {
            ApproachPose pose = cameraControl.FollowPosition;
            BlockSite site = followSite ?? FollowSiteOf(cameraControl.FollowedResident.Value);
            Vector3 camera, look;
            if (site != null)
            {
                FollowCamera(site, pose, followShift, out camera, out look);
            }
            else
            {
                look = new Vector3(pose.X, pose.Y + 1.0f, pose.Z);
                camera = look + new Vector3(0f, 3.2f, -6.5f);
            }
            alphaCamera.transform.position = camera + new Vector3(panX, 0f, panZ);
            alphaCamera.transform.LookAt(look);
            alphaCamera.transform.rotation *= Quaternion.Euler(0f, yaw, 0f);
        }
        else
        {
            alphaCamera.transform.position = cameraBase + new Vector3(panX, 0f, panZ);
            alphaCamera.transform.rotation = cameraRotation * Quaternion.Euler(0f, yaw, 0f);
        }
        alphaCamera.fieldOfView = Mathf.Clamp(55f * 10f / (float)cameraControl.Zoom, 30f, 80f);
    }

    BlockSite FollowSiteOf(ResidentId resident)
    {
        if (sim == null) return null;
        ActionEntry entry = sim.Commands.GetCurrent(resident);
        if (entry == null || !entry.TargetId.HasValue) return null;
        long target = entry.TargetId.Value;
        for (int i = 0; i < sim.Sites.Sites.Count; i++)
        {
            BlockSite site = sim.Sites.Sites[i];
            if ((long)site.Object == target) return site;
            for (int j = 0; j < site.Slots.Count; j++)
                if ((long)site.Slots[j].Object == target) return site;
        }
        return null;
    }

    void Select(ResidentId resident)
    {
        ResidentRecord r;
        if (sim.World.TryGetResident(resident, out r) && !r.IsAlive)
        {
            notice = r.DisplayName + " has passed away.";
            return;
        }
        ResidentId? prev = navigation.SelectedResident;
        if (!selection.FocusResident(resident)) return;
        hud.Select(resident);
        notice = "Selected " + selection.Label;
        Log("input select resident=" + (long)resident);
        if (input != null && input.MenuOpen && (!prev.HasValue || prev.Value != resident))
        {
            OpenActivity(activityIndex);
        }
    }

    List<SelectionTarget> LivingTargets()
    {
        if (sim == null || sim.World == null) return null;
        ResidentId[] ids = sim.Residents ?? Roster(sim.World);
        List<SelectionTarget> targets = new List<SelectionTarget>();
        for (int i = 0; i < ids.Length; i++)
        {
            ResidentRecord r;
            if (sim.World.TryGetResident(ids[i], out r) && r.IsAlive)
                targets.Add(new SelectionTarget(ids[i], 0f, 0f));
        }
        return targets;
    }

    void OpenActivity(int index)
    {
        if (!navigation.SelectedResident.HasValue) { notice = "Select a resident first (N)."; return; }
        ResidentRecord actor;
        if (sim.World.TryGetResident(navigation.SelectedResident.Value, out actor) && !actor.IsAlive)
        {
            notice = actor.DisplayName + " has passed away.";
            return;
        }
        if (index == 4 && liveSim != null) { OpenPartyPlan(navigation.SelectedResident.Value); return; }
        planOpen = false;
        activityIndex = index;
        ObjectId target = activityObjects[index];
        if (index == 1)
        {
            ResidentRecord res = ResidentOf(sim, navigation.SelectedResident.Value);
            if (string.IsNullOrEmpty(res.JobId))
            {
                menuResident = navigation.SelectedResident.Value;   // INTEGRATE-W5: refusal-text repair reads the menu actor
                menu.Open(navigation.SelectedResident.Value, target, ClassNames[1], new AvailableAction[0]);
                input.TryActivate(access.BindingFor("OpenActionMenu"));
                input.EnterState("ActionMenu");
                notice = "Find a job (select a career)";
                return;
            }
            string shiftCode = JobShiftCode(res.JobId);
            if (shiftCode == "ACT-WORK-01") target = SlotOf(sim, "cafe", "ACT-WORK-01").Object;
            else if (shiftCode == "ACT-WORK-02") target = SlotOf(sim, "office", "ACT-WORK-02").Object;
            else if (shiftCode == "ACT-WORK-04") target = SlotOf(sim, "office", "ACT-WORK-04").Object;

            menuResident = navigation.SelectedResident.Value;   // INTEGRATE-W5: refusal-text repair reads the menu actor
            menu.Open(navigation.SelectedResident.Value, target, ClassNames[1],
                new[] { new AvailableAction(new ActivityId(shiftCode), (long)target) });
            input.TryActivate(access.BindingFor("OpenActionMenu"));
            input.EnterState("ActionMenu");
            notice = "Work shift: " + res.JobId + " | N/B: Immediate or Append | Enter: choose | X: close";
            return;
        }
        if (index == 2)
        {
            ResidentRecord res = ResidentOf(sim, navigation.SelectedResident.Value);
            RefreshEducation(res);
            if (eligibleEduCodes.Count == 0) { notice = "No education activities available."; return; }
            if (string.IsNullOrEmpty(selectedEduCode) || !eligibleEduCodes.Contains(selectedEduCode))
            {
                selectedEduCode = eligibleEduCodes[0];
            }
            target = selectedEduCode == "ACT-EDU-01" ? SlotOf(sim, "school", "ACT-EDU-01").Object : activityObjects[2];
            menuResident = navigation.SelectedResident.Value;   // INTEGRATE-W5: refusal-text repair reads the menu actor
            menu.Open(navigation.SelectedResident.Value, target, ClassNames[2],
                new[] { new AvailableAction(new ActivityId(selectedEduCode), (long)target) });
            input.TryActivate(access.BindingFor("OpenActionMenu"));
            input.EnterState("ActionMenu");
            notice = "Education: " + EduTitleOf(selectedEduCode) + " | Left/Right: change course | N/B: choose | X: close";
            return;
        }
        if (index == 3)
        {
            RefreshPartners(navigation.SelectedResident.Value);
            if (socialPartners.Count == 0) { notice = "Nobody else is around to talk to."; return; }
            if (!socialPartners.Contains(socialPartner)) socialPartner = socialPartners[0];
            target = (ObjectId)(long)socialPartner;
        }
        menuResident = navigation.SelectedResident.Value;
        menu.Open(navigation.SelectedResident.Value, target, ClassNames[index],
            new[] { new AvailableAction(new ActivityId(ActivityCodes[index]), (long)target) });
        input.TryActivate(access.BindingFor("OpenActionMenu"));
        input.EnterState("ActionMenu");
        notice = index == 3 ? "Chat with " + ResidentOf(sim, socialPartner).DisplayName + " | Left/Right or click: change partner"
            : "N/B: Immediate or Append | Enter: choose | X: close";
    }

    void RefreshEducation(ResidentRecord res)
    {
        eligibleEduCodes.Clear();
        eligibleEduTitles.Clear();
        if (res == null) return;
        if (res.Stage <= LifeStage.Teen)
        {
            eligibleEduCodes.Add("ACT-EDU-01");
            eligibleEduTitles.Add("Attend School");
        }
        eligibleEduCodes.Add("ACT-EDU-02");
        eligibleEduTitles.Add("Study");
        if (res.Stage >= LifeStage.Teen)
        {
            eligibleEduCodes.Add("ACT-EDU-03");
            eligibleEduTitles.Add("Culinary Certificate");
            eligibleEduCodes.Add("ACT-EDU-04");
            eligibleEduTitles.Add("Computer Skills");
        }
    }

    static string EduTitleOf(string code)
    {
        switch (code)
        {
            case "ACT-EDU-01": return "Attend School";
            case "ACT-EDU-02": return "Study";
            case "ACT-EDU-03": return "Culinary Certificate";
            case "ACT-EDU-04": return "Computer Skills";
            default: return code;
        }
    }

    void PickEducation(string code)
    {
        if (activityIndex != 2 || !input.MenuOpen || !navigation.SelectedResident.HasValue
            || !eligibleEduCodes.Contains(code)) return;
        selectedEduCode = code;
        ObjectId target = selectedEduCode == "ACT-EDU-01" ? SlotOf(sim, "school", "ACT-EDU-01").Object : activityObjects[2];
        menuResident = navigation.SelectedResident.Value;   // INTEGRATE-W5: refusal-text repair reads the menu actor
        menu.Open(navigation.SelectedResident.Value, target, ClassNames[2],
            new[] { new AvailableAction(new ActivityId(selectedEduCode), (long)target) });
        notice = "Education: " + EduTitleOf(selectedEduCode);
        Log("input education pick=" + selectedEduCode);
    }

    static string JobShiftCode(string jobId)
    {
        if (string.Equals(jobId, "Barista", StringComparison.Ordinal)) return "ACT-WORK-01";
        if (string.Equals(jobId, "JuniorTechnician", StringComparison.Ordinal)
            || string.Equals(jobId, "Technician", StringComparison.Ordinal)) return "ACT-WORK-02";
        if (string.Equals(jobId, "AIEngineer", StringComparison.Ordinal)) return "ACT-WORK-04";
        return "ACT-WORK-01";
    }

    void HireJob(string jobId)
    {
        if (!navigation.SelectedResident.HasValue) return;
        ResidentId resId = navigation.SelectedResident.Value;
        bool ok = sim.Work.Hire(resId, jobId);
        Log("input work hire resident=" + (long)resId + " job=" + jobId + " ok=" + B(ok));
        if (ok)
        {
            notice = "Hired as " + jobId + "!";
            OpenActivity(1);
        }
        else
        {
            notice = "Could not hire as " + jobId;
        }
    }

    void QuitJob()
    {
        if (!navigation.SelectedResident.HasValue) return;
        ResidentId resId = navigation.SelectedResident.Value;
        bool ok = sim.Work.Quit(resId);
        Log("input work quit resident=" + (long)resId + " ok=" + B(ok));
        if (ok)
        {
            notice = "Quit job.";
            OpenActivity(1);
        }
    }

    void RefreshPartners(ResidentId self)
    {
        ResidentRecord me = ResidentOf(sim, self);
        ApproachPose here = sim.Executor.GetResidentPosition(self);
        socialPartners.Clear();
        foreach (ResidentId other in sim.Residents)
            if (other != self && ResidentOf(sim, other).IsAlive) socialPartners.Add(other);
        socialPartners.Sort((a, b) =>
        {
            int byFriend = FriendshipOf(me, b).CompareTo(FriendshipOf(me, a));
            return byFriend != 0 ? byFriend : DistanceSq(here, a).CompareTo(DistanceSq(here, b));
        });
    }

    float DistanceSq(ApproachPose here, ResidentId other)
    {
        ApproachPose there = sim.Executor.GetResidentPosition(other);
        float dx = there.X - here.X, dz = there.Z - here.Z;
        return dx * dx + dz * dz;
    }

    static float FriendshipOf(ResidentRecord record, ResidentId other)
    {
        foreach (PairBond bond in record.Bonds) if (bond.Other == other) return bond.Friendship;
        return 0f;
    }

    // The SOCIAL card's partner rows, the world click and Left/Right all land here.
    void PickPartner(ResidentId partner)
    {
        if (activityIndex != 3 || !input.MenuOpen || !navigation.SelectedResident.HasValue
            || partner == navigation.SelectedResident.Value || !socialPartners.Contains(partner)) return;
        socialPartner = partner;
        menuResident = navigation.SelectedResident.Value;
        menu.Open(navigation.SelectedResident.Value, (ObjectId)(long)partner, ClassNames[3],
            new[] { new AvailableAction(new ActivityId(ActivityCodes[3]), (long)partner) });
        notice = "Chat with " + ResidentOf(sim, partner).DisplayName;
        Log("input social partner=" + (long)partner);
    }

    // PARTY opens "Plan party": a start time and the host's top bonds as default guests.
    void OpenPartyPlan(ResidentId host)
    {
        ResidentRecord hostRecord;
        if (sim.World.TryGetResident(host, out hostRecord) && !hostRecord.IsAlive)
        {
            notice = hostRecord.DisplayName + " has passed away.";
            return;
        }
        if (input.MenuOpen) { input.TryActivate(access.BindingFor("CloseActionMenu")); input.EnterState("Camera"); menuResident = null; }
        activityIndex = 4;
        planHost = host;
        planTime = liveSim.PartyStartFor(1) >= 0L ? 1 : 2;
        planGuests.Clear();
        PartyPlan open = sim.Party.OpenPlan(host);
        if (open != null) { foreach (PartyInvite invite in open.Invites) planGuests.Add((ResidentId)invite.Guest); }
        else planGuests.AddRange(liveSim.TopBonds(host, 2));
        planOpen = true;
        Log("input party plan open host=" + (long)host + " guests=" + planGuests.Count);
    }

    void ConfirmPartyPlan()
    {
        string summary;
        PartyPlan plan = liveSim.PlanParty(planHost, liveSim.PartyStartFor(planTime), planGuests,
            (long)activityObjects[4], out summary);
        Log("input party plan confirm host=" + (long)planHost + " time=" + LiveSimulation.PartyTimes[planTime]
            + " ok=" + B(plan != null) + " " + summary);
        if (plan == null) { notice = summary; return; }
        planOpen = false;
    }

    // Voice "<name> play minigolf": the same path as the activity menu's minigolf entry.
    void VoiceMinigolf(ResidentId resident)
    {
        Select(resident);
        if (navigation.SelectedResident != resident) return;
        menuResident = resident;
        activityIndex = 5;
        ChooseActivity(Blockparty.Contracts.QueueMode.Immediate);
    }

    void ChooseActivity(Blockparty.Contracts.QueueMode mode)
    {
        if (!navigation.SelectedResident.HasValue) return;
        ResidentRecord actor;
        if (sim.World.TryGetResident(navigation.SelectedResident.Value, out actor) && !actor.IsAlive)
        {
            notice = actor.DisplayName + " has passed away.";
            return;
        }
        // INTEGRATE-W5: act for the resident the menu was opened for (refusal-text repair); jobs-education reads its job.
        ResidentId resId = menuResident.HasValue ? menuResident.Value : navigation.SelectedResident.Value;
        ResidentRecord resident = ResidentOf(sim, resId);
        if (activityIndex == 5)
        {
            try
            {
                golfAction = sim.Minigolf.Start(activityObjects[5], new[] { resId }, new[] { true });
                notice = "Minigolf started. P: putt. Backspace: cancel.";
            }
            catch (Exception e) { notice = e.Message; }
            return;
        }
        string code = ActivityCodes[activityIndex];
        if (activityIndex == 1)
        {
            if (resident == null || string.IsNullOrEmpty(resident.JobId))
            {
                notice = "Find a job first before going to work.";
                return;
            }
            code = JobShiftCode(resident.JobId);
        }
        else if (activityIndex == 2)
        {
            if (!string.IsNullOrEmpty(selectedEduCode)) code = selectedEduCode;
        }
        CommandResult result = menu.Choose(new ActivityId(code), mode);
        hud.ActionResult(resId, new ActivityId(code), result);
        MenuNotice();
    }

    // REFUSAL-TEXT: a refusal shows a plain sentence, never the raw RejectReason the menu stores.
    string MenuCode() { return menu.Activities.Count > 0 ? menu.Activities[0].Activity.Code : ActivityCodes[activityIndex]; }

    string MenuText()
    {
        RejectReason reason;
        if (!Enum.TryParse(menu.Message, out reason)) return menu.Message;
        ResidentRecord record = null;
        ResidentId? actor = menuResident.HasValue ? menuResident : navigation.SelectedResident;
        if (actor.HasValue) sim.World.TryGetResident(actor.Value, out record);
        return RefusalText.For(record, MenuCode(), reason);
    }

    void MenuNotice()
    {
        RejectReason reason;
        notice = MenuText();
        ResidentId? actor = menuResident.HasValue ? menuResident : navigation.SelectedResident;
        Log("input action " + notice + " | code=" + MenuCode()
            + " resident=" + (actor.HasValue ? ((long)actor.Value).ToString() : "none")
            + " reason=" + (Enum.TryParse(menu.Message, out reason) ? reason.ToString() : "none"));
    }

    void CancelSelected()
    {
        if (!navigation.SelectedResident.HasValue) return;
        if (liveSim != null && liveSim.CancelMinigolf(navigation.SelectedResident.Value))
        {
            notice = "Minigolf match cancelled.";
            Log("input minigolf cancel resident=" + (long)navigation.SelectedResident.Value);
            return;
        }
        foreach (HudElement element in hud.Snapshot(1280, 720).Elements)
            if (element.Id.StartsWith("cancel:", StringComparison.Ordinal))
            { hud.TryClick(element.Id); notice = element.Label; Log("input " + notice); break; }
    }

    // SETTINGS-AI: Free will persists in PlayerPrefs (local, beside the settings record), default On.
    const string FreeWillPref = "blockparty.freeWill";
    bool freeWill = true;

    void ToggleFreeWill()
    {
        freeWill = !freeWill;
        if (liveSim != null) liveSim.FreeWill = freeWill;
        PlayerPrefs.SetInt(FreeWillPref, freeWill ? 1 : 0);
        PlayerPrefs.Save();
        Log("input setting Free will on=" + B(freeWill));
    }

    void SaveSettings()
    {
        if (string.IsNullOrEmpty(saveDirectory)) return;
        SaveWriteResult saved = LiveSettings.Save(access, saveDirectory, muted);
        if (!saved.Ok) notice = "Settings not saved: " + saved.Detail;
        Log("input settings saved=" + B(saved.Ok) + " textScale=" + N(access.Settings.TextScale)
            + " reducedMotion=" + B(access.Settings.ReducedMotion) + " captions=" + B(access.Settings.Captions) + " muted=" + B(muted)
            + " remapped=" + (controls != null ? controls.ChangedCount() : 0));
    }

    bool Pressed(string action)
    {
        KeyCode key;
        return Enum.TryParse(access.BindingFor(action), out key) && Input.GetKeyDown(key);
    }

    // Open Settings and history take the remappable navigation, confirm and close keys
    // before the title or the world; true while Settings owns those keys this frame.
    bool HandleSurfaceKeys()
    {
        if (failed || access == null || hudFrame == null) return false;
        if (!titleOpen && !settingsOpen && Pressed("Inspect")) { alphaHud.ToggleHistory(hudFrame); return false; }
        if (!settingsOpen)
        {
            if (alphaHud.HistoryOpen && Pressed("CloseActionMenu")) alphaHud.ToggleHistory(hudFrame);
            return false;
        }
        // SETTINGS-AI: the key field owns typing; only Close leaves the page.
        if (alphaHud.KeyPageOpen) { if (Input.GetKeyDown(KeyCode.Escape)) alphaHud.CloseKeyPage(); return true; }
        int rows = AlphaHud.SettingsRowCount;
        if (Pressed("CloseActionMenu") || Pressed("OpenSettings")) { settingsOpen = false; Log("input settings open=false"); }
        else if (Pressed("SelectNext")) { settingsFocus = (settingsFocus + 1) % rows; Log("input settings focus=" + settingsFocus); }
        else if (Pressed("SelectPrevious")) { settingsFocus = (settingsFocus + rows - 1) % rows; Log("input settings focus=" + settingsFocus); }
        else if (Pressed("Confirm")) alphaHud.ActivateSetting(hudFrame, settingsFocus);
        return true;
    }

    void HandleInput(bool surface)
    {
        if (input == null || failed) return;
        foreach (string action in InputActions.Names)
        {
            if (surface) break;
            KeyCode key;
            if (!Enum.TryParse(access.BindingFor(action), out key) || !Input.GetKeyDown(key)) continue;
            if (action == "Pause") { input.TryActivate(access.BindingFor(action)); Log("input paused=" + B(sim.Clock.Paused)); }
            else if (action == "SelectNext" || action == "SelectPrevious")
            {
                if (input.MenuOpen) menu.TryInput(access.BindingFor(action));
                else if (selection.TryInput(access.BindingFor(action), LivingTargets()) && navigation.SelectedResident.HasValue)
                    Select(navigation.SelectedResident.Value);
            }
            else if (action == "OpenActionMenu") OpenActivity(activityIndex);
            else if (action == "CloseActionMenu") { planOpen = false; input.TryActivate(access.BindingFor(action)); input.EnterState("Camera"); menuResident = null; }
            else if (action == "ImmediateCommand") ChooseActivity(Blockparty.Contracts.QueueMode.Immediate);
            else if (action == "AppendCommand") ChooseActivity(Blockparty.Contracts.QueueMode.Append);
            else if (action == "Confirm" && input.MenuOpen)
            {
                if (activityIndex == 5) ChooseActivity(Blockparty.Contracts.QueueMode.Immediate);
                else { menu.TryInput(access.BindingFor(action)); MenuNotice(); }
            }
            else if (action == "Cancel") CancelSelected();
            else if (action == "SpeedUp" || action == "SpeedDown")
            {
                ChangeSpeed(action == "SpeedUp" ? 1 : -1);
            }
            else if (action == "OpenSettings") { settingsOpen = !settingsOpen; Log("input settings open=" + B(settingsOpen)); }
        }
        foreach (string action in InputActions.Names)
        {
            if (!action.StartsWith("Camera", StringComparison.Ordinal)) continue;
            KeyCode key;
            if (Enum.TryParse(access.BindingFor(action), out key) && Input.GetKey(key))
                cameraControl.TryActivate(access.BindingFor(action), Time.deltaTime * 8.0);
        }
        HandleMouse();
        ApplyCamera();
        if (Input.GetKeyDown(KeyCode.J)) LiveConceive();
        if (Input.GetKeyDown(KeyCode.K)) LiveMoveHousehold();
        if (Input.GetKeyDown(KeyCode.L)) LiveRest();
        SamLookHotkey();
        if (Input.GetKeyDown(KeyCode.F5)) SaveLive();
        if (Input.GetKeyDown(KeyCode.F9)) ReloadLive();
        if (Input.GetKeyDown(KeyCode.P) && golfAction.HasValue)
        {
            Minigolf.MatchSnapshot match = sim.Minigolf.GetMatch(golfAction.Value);
            if (match != null && !match.Completed)
                sim.Minigolf.SubmitPutt(golfAction.Value, match.HoleIndex, match.TurnIndex, match.ActiveResident, 0, 30);
        }
        if (Input.GetKeyDown(KeyCode.F10)) Application.Quit(0);
        bool pickingEdu = input.MenuOpen && activityIndex == 2;
        if (pickingEdu && eligibleEduCodes.Count > 0 && (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow)))
        {
            int n = eligibleEduCodes.Count, step = Input.GetKeyDown(KeyCode.RightArrow) ? 1 : -1;
            int cur = eligibleEduCodes.IndexOf(selectedEduCode);
            if (cur < 0) cur = 0;
            PickEducation(eligibleEduCodes[((cur + step) % n + n) % n]);
        }
        bool picking = input.MenuOpen && activityIndex == 3;
        if (picking && socialPartners.Count > 0 && (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow)))
        {
            int n = socialPartners.Count, step = Input.GetKeyDown(KeyCode.RightArrow) ? 1 : -1;
            PickPartner(socialPartners[((socialPartners.IndexOf(socialPartner) + step) % n + n) % n]);
        }
        // SOCIAL-LIVE (M5-OD-49) with M5-OD-54: HandleMouse is off while the action menu is open, so a world
        // click with the SOCIAL card open picks the partner here instead of selecting.
        if (picking && Input.GetMouseButtonDown(0) && !alphaHud.Blocks(Input.mousePosition))
        {
            ResidentId? partner = ResidentAt(Input.mousePosition);
            if (partner.HasValue) PickPartner(partner.Value);
        }
    }

    int dragButton = -1;
    Vector3 lastMouse;

    // M5-OD-54: left drag past the threshold, or a middle/right drag, pans on the
    // ground plane; a left click without drag selects; the wheel zooms. Off while
    // the action menu or Settings is open, and never starts on a HUD panel.
    void HandleMouse()
    {
        if (alphaCamera == null) return;
        Vector3 mouse = Input.mousePosition;
        if (input.MenuOpen || settingsOpen) { cameraControl.CancelPointer(); dragButton = -1; return; }
        if (Input.mouseScrollDelta.y != 0f && !alphaHud.Blocks(mouse))
        {
            cameraControl.ZoomByWheel(Input.mouseScrollDelta.y);
            Log("input wheel zoom=" + N(cameraControl.Zoom));
        }
        if (dragButton < 0)
        {
            for (int b = 0; b < 3; b++)
                if (Input.GetMouseButtonDown(b) && !alphaHud.Blocks(mouse))
                { dragButton = b; lastMouse = mouse; cameraControl.PointerDown(mouse.x, mouse.y); break; }
            if (dragButton < 0) return;
        }
        if (cameraControl.PointerMove(mouse.x, mouse.y))
        {
            Vector3 from, to;
            if (GroundPoint(lastMouse, out from) && GroundPoint(mouse, out to))
                cameraControl.PanByGround(from.x - to.x, from.z - to.z);
            lastMouse = mouse;
        }
        // Button state, not ButtonUp, so a release lost to focus change still ends the press.
        if (!Input.GetMouseButton(dragButton))
        {
            bool dragged = cameraControl.Dragging;
            if (cameraControl.PointerUp() && dragButton == 0) SelectAt(mouse);
            if (dragged) Log("input drag-pan button=" + dragButton + " panX=" + N(cameraControl.PanX) + " panZ=" + N(cameraControl.PanZ));
            dragButton = -1;
        }
    }

    bool GroundPoint(Vector3 screen, out Vector3 point)
    {
        Ray ray = alphaCamera.ScreenPointToRay(screen);
        float t;
        point = default(Vector3);
        // Rays near the horizon would throw the camera far away; skip those frames.
        if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out t) || t > 400f) return false;
        point = ray.GetPoint(t);
        return true;
    }

    void SelectAt(Vector3 mouse)
    {
        ResidentId? resident = ResidentAt(mouse);
        if (resident.HasValue) Select(resident.Value);
    }

    // PORTRAITS (owner playtest 2026-11-04): at play zoom a resident is a few pixels wide, so a
    // click that misses the 0.3 m body collider still takes the resident whose drawn body (the
    // collider's axis, projected) passes within PickRadius reference pixels; a direct collider hit
    // wins. Roots map to residents in id order, as Mirror places them.
    const float PickRadius = 26f;

    ResidentId? ResidentAt(Vector3 mouse)
    {
        if (alphaCamera == null || residentRoots == null) return null;
        ResidentRecord[] rows = sim.World.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        int n = Mathf.Min(rows.Length, residentRoots.Length);
        RaycastHit hit;
        if (Physics.Raycast(alphaCamera.ScreenPointToRay(mouse), out hit))
            for (int i = 0; i < n; i++)
                if (residentRoots[i] != null && hit.transform.IsChildOf(residentRoots[i])) return rows[i].Id;
        float best = PickRadius * Screen.height / 1080f;
        ResidentId? found = null;
        Vector2 m = mouse;
        for (int i = 0; i < n; i++)
        {
            if (residentRoots[i] == null || !residentRoots[i].gameObject.activeInHierarchy) continue;
            CapsuleCollider body = residentRoots[i].GetComponentInChildren<CapsuleCollider>();
            if (body == null || !body.enabled) continue;   // an unused slot or a resident not placed yet
            Vector3 foot = alphaCamera.WorldToScreenPoint(body.transform.TransformPoint(body.center - Vector3.up * body.height * 0.5f));
            Vector3 top = alphaCamera.WorldToScreenPoint(body.transform.TransformPoint(body.center + Vector3.up * body.height * 0.5f));
            if (foot.z <= 0f || top.z <= 0f) continue;
            Vector2 a = foot, b = top, ab = b - a;
            float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(m - a, ab) / ab.sqrMagnitude) : 0f;
            float d = Vector2.Distance(m, a + t * ab);
            if (d >= best) continue;
            // PORTRAITS-R1: a near miss counts only for a body the camera can see there; a wall or
            // header in front still blocks it, as a direct click would be blocked. Aim at the
            // capsule's inner axis so the ground under the feet never counts as the blocker.
            float inner = Mathf.Max(0f, body.height * 0.5f - body.radius);
            Vector3 aim = body.transform.TransformPoint(body.center + Vector3.up * Mathf.Lerp(-inner, inner, t));
            RaycastHit block;
            if (Physics.Linecast(alphaCamera.transform.position, aim, out block) && !block.transform.IsChildOf(residentRoots[i])) continue;
            best = d; found = rows[i].Id;
        }
        return found;
    }

    void ChangeSpeed(int direction)
    {
        SimulationSpeed[] speeds = { SimulationSpeed.Normal, SimulationSpeed.Fast, SimulationSpeed.Ultra };
        int current = Array.IndexOf(speeds, sim.Clock.Speed);
        sim.Clock.SetSpeed(speeds[Mathf.Clamp(current + direction, 0, speeds.Length - 1)]);
        Log("input speed=" + sim.Clock.Speed);
    }

    // HUD speed buttons: pick a speed directly; a paused clock resumes through the Pause action.
    void SetSpeed(SimulationSpeed speed)
    {
        if (sim.Clock.Paused) { input.TryActivate(access.BindingFor("Pause")); Log("input paused=" + B(sim.Clock.Paused)); }
        sim.Clock.SetSpeed(speed);
        Log("input speed=" + sim.Clock.Speed);
    }

    void LiveConceive()
    {
        if (!navigation.SelectedResident.HasValue) { notice = "Select a resident first (N)."; return; }
        ResidentId actor = navigation.SelectedResident.Value;
        ResidentId partner;
        if (!liveSim.ConsentPartner(actor, out partner)) { notice = "No consenting partner."; return; }
        CommandResult result = liveSim.Submit("live.conceive", actor, (long)partner);
        notice = result.Accepted ? "Conception command accepted." : "Conception refused: " + result.Reason;
        Log("input live.conceive actor=" + (long)actor + " partner=" + (long)partner
            + " accepted=" + B(result.Accepted) + (result.Accepted ? "" : " reason=" + result.Reason));
    }

    void LiveMoveHousehold()
    {
        if (!navigation.SelectedResident.HasValue) { notice = "Select a resident first (N)."; return; }
        ResidentId actor = navigation.SelectedResident.Value;
        HomeId destination;
        if (!liveSim.VacantHomeFor(actor, out destination)) { notice = "No vacant home fits."; return; }
        CommandResult result = liveSim.Submit("live.move-household", actor, (long)destination);
        notice = result.Accepted ? "Household move accepted." : "Move refused: " + result.Reason;
        Log("input live.move-household actor=" + (long)actor + " home=" + (long)destination
            + " accepted=" + B(result.Accepted) + (result.Accepted ? "" : " reason=" + result.Reason));
    }

    void LiveRest()
    {
        if (!navigation.SelectedResident.HasValue) { notice = "Select a resident first (N)."; return; }
        ResidentId actor = navigation.SelectedResident.Value;
        CommandResult result = liveSim.Submit("ACT-HEALTH-02", actor, 0L);
        notice = result.Accepted ? "Rest command accepted." : "Rest refused: " + result.Reason;
        Log("input live.rest actor=" + (long)actor + " accepted=" + B(result.Accepted)
            + (result.Accepted ? "" : " reason=" + result.Reason));
    }

    // VOICE-LIVE (M5-OD-38): an accepted voice command's player-facing effect,
    // through the same selection, navigation/camera, Outing card and save the
    // ordinary controls use. The command was already validated and submitted
    // once by VOICE@1; this only applies it. Returns the status to show, or null.
    string PresentVoice(CommandEnvelope c)
    {
        string shown = null;
        switch (c.Kind)
        {
            case CommandKind.SelectResident: Select(c.Resident.Value); break;
            case CommandKind.FollowResident: Select(c.Resident.Value); navigation.FollowResident(c.Resident.Value); break;
            case CommandKind.StopFollowing: navigation.StopFollowing(); break;
            case CommandKind.OpenMap:
                if (input.MenuOpen) { input.TryActivate(access.BindingFor("CloseActionMenu")); input.EnterState("Camera"); menuResident = null; }
                navigation.OpenMap();
                alphaHud.OpenOuting();
                break;
            case CommandKind.SaveGame: shown = SaveLive() ? "Saved the game (F9 reload)." : notice; break;
            default: return null;
        }
        Log("voice present kind=" + c.Kind + " selected=" + (navigation.SelectedResident.HasValue ? N((long)navigation.SelectedResident.Value) : "none")
            + " followed=" + (navigation.FollowedResident.HasValue ? N((long)navigation.FollowedResident.Value) : "none")
            + " map=" + B(navigation.IsMapOpen) + " outing=" + B(alphaHud.OutingOpen));
        return shown;
    }

    bool SaveLive()
    {
        SaveWriteResult saved = CraftsSave.Save(saveDirectory, crafts, sim.World, () => liveSim.Save(saveDirectory));
        notice = saved.Ok ? "Household saved (F9 reload)." : "Save failed: " + saved.Detail;
        GazetteSave(saveDirectory, saved.Ok);
        Log("input save ok=" + B(saved.Ok));
        return saved.Ok;
    }

    void ReloadLive()
    {
        SaveLoadResult loaded = SaveV1.LoadGame(saveDirectory, this);
        if (!loaded.Ok) { notice = "Reload failed: " + loaded.Notice; return; }
        LoadedGame g = loaded.Game;
        liveSim.Rebind(g);
        planOpen = false;
        sim = BindLive(liveSim);
        BindControls();
        Select(sim.Residents[0]);
        Mirror(sim);
        notice = "Household reloaded; durable Home, Club and Family restored.";
        CraftsBind(true);
        SamLookBind();
        gazetteRestore = true;
        GazetteBind();   // restore before the next LiveTick advances the reloaded clock
        Log("input reload ok=true epoch=" + (uint)g.World.Epoch
            + " entry=" + "Blockparty.Simulation.LiveSimulation.AdvanceRealSeconds");
    }

    // Title: New Game plays the default live world already built behind it;
    // Continue loads the save slot through the F9 reload path.
    void OpenTitle()
    {
        TitleFlow flow = new TitleFlow(saveDirectory, this);
        continueOk = flow.ContinueEnabled;
        SaveHeader? header = continueOk ? flow.SavedHeader : null;
        string reason = flow.ContinueReason;
        // An empty slot is the "No saved game" subtitle; this notice and the read error name a private path.
        bool backup = reason.EndsWith("backup is available.", StringComparison.Ordinal);
        if (reason.StartsWith("No save file at ", StringComparison.Ordinal))
            reason = backup ? "The save is missing; the previous backup is available." : "";
        else if (reason.StartsWith("The save could not be read", StringComparison.Ordinal))
            reason = "The save could not be read." + (backup ? " The previous backup is available." : "");
        continueNote = header.HasValue ? "Day " + (header.Value.SavedAtGameSeconds / Day + 1).ToString(CultureInfo.InvariantCulture) : reason;
        titleFocus = continueOk ? 1 : 0;
        titleOpen = true;
        Log("title open continue=" + B(continueOk) + " note=" + Q(continueNote));
    }

    void HandleTitleKeys()
    {
        if (failed) return;
        if (Input.GetKeyDown(KeyCode.F10)) Application.Quit(0);
        KeyCode key;
        int step = Enum.TryParse(access.BindingFor("SelectNext"), out key) && Input.GetKeyDown(key) ? 1
            : Enum.TryParse(access.BindingFor("SelectPrevious"), out key) && Input.GetKeyDown(key) ? 2 : 0;
        if (step > 0)
        {
            titleFocus = (titleFocus + step) % 3;
            if (titleFocus == 1 && !continueOk) titleFocus = (titleFocus + step) % 3;
        }
        if (Enum.TryParse(access.BindingFor("OpenSettings"), out key) && Input.GetKeyDown(key)) TitleChoose(TitleAction.Settings);
        else if (Enum.TryParse(access.BindingFor("Confirm"), out key) && Input.GetKeyDown(key))
            TitleChoose(titleFocus == 0 ? TitleAction.NewGame : titleFocus == 1 ? TitleAction.Continue : TitleAction.Settings);
    }

    void TitleChoose(TitleAction action)
    {
        if (action == TitleAction.Settings)
        {
            settingsOpen = !settingsOpen;
            Log("input title settings open=" + B(settingsOpen));
        }
        else if (action == TitleAction.NewGame)
        {
            titleOpen = false;
            settingsOpen = false;
            notice = "New game started.";
            Log("input title new-game residents=" + sim.Residents.Length + " seconds=" + sim.World.GameSeconds);
        }
        else if (action == TitleAction.Continue)
        {
            if (!continueOk) { Log("input title continue refused reason=" + Q(continueNote)); return; }
            settingsOpen = false;
            ReloadLive();
            titleOpen = !notice.StartsWith("Household reloaded", StringComparison.Ordinal);
            if (titleOpen) { continueOk = false; continueNote = notice; }
            Log("input title continue ok=" + B(!titleOpen) + " seconds=" + sim.World.GameSeconds);
        }
    }

    void ToggleMute()
    {
        muted = !muted;
        AudioListener.volume = muted ? 0f : access.Settings.MixerLevel;
        Log("input setting mute=" + B(muted) + " mixer=" + N(access.Settings.MixerLevel) + " listener=" + N(AudioListener.volume));
    }

    void CycleColorFilter()
    {
        colorFilter = (colorFilter + 1) % ColorFilter.Names.Length;
        ColorFilter.Apply(toneProfile, colorFilter);
        ColorAdjustments adjust;
        toneProfile.TryGet(out adjust);
        Log("input setting color-filter=" + ColorFilter.Names[colorFilter] + " saturation=" + N(adjust.saturation.value));
    }

    void CycleAging()
    {
        AgingPreset[] order = { AgingPreset.Off, AgingPreset.Slow, AgingPreset.Moderate, AgingPreset.Accelerated };
        sim.Clock.SetPreset(order[(Array.IndexOf(order, sim.Clock.Preset) + 1) % order.Length]);
        bool advances = Clockwork.AgingAdvances(sim.Clock.Preset);
        Log("input setting aging=" + sim.Clock.Preset + " advances=" + B(advances)
            + (advances ? " multiplier=" + N(Clockwork.PresetMultiplier(sim.Clock.Preset)) : ""));
    }

    void OnGUI()
    {
        if (evidence) return;
        // The splash card covers the title while it holds, then fades over it.
        if (input != null && !splash.Holding) DrawHud();
        if (!splash.Done)
            splash.Draw(input != null || failed, access != null ? access.Settings.ReducedMotion : splashReducedMotion,
                access != null ? access.Settings.TextScale : splashTextScale);
    }

    // Help names the live bindings, so a remap shows at once.
    string ControlsHelpText()
    {
        Func<string, string> key = a => ControlsRebind.Display(access.BindingFor(a));
        return key("SelectNext") + "/" + key("SelectPrevious") + ": select resident | " + key("OpenActionMenu")
            + ": action menu | J: conceive | K: move | L: rest | " + key("Pause") + ": pause | F5: save | F9: reload";
    }

    void DrawHud()
    {
        if (hudFrame == null)
        {
            hudFrame = new AlphaHud.Frame
            {
                Categories = ClassNames,
                ControlsHelp = ControlsHelp,
                Pause = () => { input.TryActivate(access.BindingFor("Pause")); Log("input paused=" + B(sim.Clock.Paused)); },
                SetSpeed = SetSpeed,
                Save = () => SaveLive(),
                Reload = ReloadLive,
                ToggleSettings = () => settingsOpen = !settingsOpen,
                SettingsChanged = SaveSettings,
                Cancel = CancelSelected,
                CloseMenu = () => { input.TryActivate(access.BindingFor("CloseActionMenu")); input.EnterState("Camera"); menuResident = null; },
                Select = Select,
                OpenCategory = OpenActivity,
                Choose = ChooseActivity,
                RootOf = id => RootOf(sim, id),
                Title = TitleChoose,
                ToggleMute = ToggleMute,
                CycleColorFilter = CycleColorFilter,
                CycleAging = CycleAging,
                ToggleFreeWill = ToggleFreeWill,
                Craft = CraftChoice,
                PlanSetTime = i => { planTime = i; Log("input party plan time=" + LiveSimulation.PartyTimes[i]); },
                PlanToggle = id => { if (!planGuests.Remove(id)) planGuests.Add(id); Log("input party plan toggle resident=" + (long)id + " invited=" + B(planGuests.Contains(id))); },
                PlanConfirm = ConfirmPartyPlan,
                PlanClose = () => planOpen = false,
                PickPartner = PickPartner,
                OpenLook = OpenLook,
            };
        }
        AlphaHud.Frame f = hudFrame;
        f.World = sim.World; f.Clock = sim.Clock; f.Executor = sim.Executor; f.Appearances = sim.Appearances;
        f.Residents = sim.Residents; f.Access = access; f.Input = input; f.Navigation = navigation;
        f.Hud = hud; f.Menu = menu; f.Cursors = cursors; f.Trail = gymTrail; f.Camera = alphaCamera;
        f.ActiveCategory = activityIndex; f.MenuMessage = MenuText(); f.Notice = notice; f.NoticeSeq = noticeSeq; f.SettingsOpen = settingsOpen;
        f.Failed = failed; f.FailureReason = failureReason; f.CraftRows = CraftRows();
        f.TitleOpen = titleOpen; f.ContinueOk = continueOk; f.ContinueNote = continueNote; f.TitleFocus = titleFocus; f.SettingsFocus = settingsFocus; f.FreeWill = freeWill;
        f.ColorFilter = ColorFilter.Names[colorFilter]; f.Muted = muted; f.Voice = voice;
        f.Controls = controls; f.ControlsHelp = ControlsHelpText();
        f.Live = liveSim; f.PlanOpen = planOpen; f.PlanHost = planHost; f.PlanTime = planTime; f.PlanGuests = planGuests;
        f.Partners = input.MenuOpen && activityIndex == 3 ? socialPartners : null;
        f.Partner = socialPartner;
        f.EducationOptions = (input.MenuOpen && activityIndex == 2) ? eligibleEduCodes.ToArray() : null;
        f.EducationTitles = (input.MenuOpen && activityIndex == 2) ? eligibleEduTitles.ToArray() : null;
        f.SelectedEduCode = selectedEduCode;
        f.PickEducation = PickEducation;
        ResidentRecord selRec = navigation.SelectedResident.HasValue ? ResidentOf(sim, navigation.SelectedResident.Value) : null;
        f.WorkHasJob = selRec != null && !string.IsNullOrEmpty(selRec.JobId);
        f.WorkJobId = selRec != null ? selRec.JobId : "";
        f.WorkCareerLevel = selRec != null ? selRec.CareerLevel : 1;
        f.HireJob = HireJob;
        f.QuitJob = QuitJob;
        f.Panel = residentPanel;
        f.Look = samLook; f.LookFresh = LookOutlinesFresh();
        alphaHud.Draw(f);
        CraftsOnGUI();
        GazetteOnGUI();
    }

    void Fail(string message)
    {
        failed = true;
        failureReason = message;
        UnityEngine.Debug.LogError("ALPHA FAIL " + message);
    }

    static void Log(string message) { UnityEngine.Debug.Log("ALPHA " + message); }

    // ---------- world construction ----------

    Sim BuildWorld(long worldId, long seed, bool full)
    {
        Sim s = new Sim();
        string[] names = full
            ? new string[] { "Alex", "Bea", "Cara", "Dev", "Eli", "Fran", "Gio", "Harper" }
            : new string[] { "Ivy", "Jules", "Kai", "Lena", "Milo", "Nia", "Omar", "Pia" };
        s.World = ResidentFactory.CreateNewGame(worldId, seed, names, out s.Appearances);
        s.Clock = new Clock(s.World);
        s.Clock.SetSpeed(SimulationSpeed.Ultra);
        s.Clock.SetPreset(full ? AgingPreset.Moderate : AgingPreset.Off);
        s.Commands = new CommandArbitrator(s.World, s.Clock);
        s.Executor = new ActivityExecutor(s.World, s.Commands, s.Clock);
        s.Accounts = new Accounts(s.World);
        s.Food = new Food(s.World, s.Commands, s.Executor, s.Accounts);
        s.Expenses = new Expenses(s.World, s.Accounts);
        s.Ownership = new Ownership(s.World, s.Accounts);
        s.Housing = new Housing(s.World, s.Accounts, s.Ownership, s.Executor, s.Appearances);
        s.Home = new Home(s.World, s.Commands, s.Executor, s.Accounts);
        s.Creative = new Creative(s.World);
        s.Party = new Party(s.World, s.Commands, s.Executor, s.Accounts);
        s.Family = new FamilyCreation(s.World, s.Commands, s.Executor, s.Appearances, s.Housing);
        s.Social = new Blockparty.Activities.Social(s.World, s.Commands, s.Executor);
        s.Minigolf = new Minigolf(s.World, s.Commands, s.Executor, s.Clock);
        s.Health = new Health(s.World, s.Commands, s.Executor);
        s.Progression = new Progression(s.World);
        s.Autonomy = new Autonomy(s.World, s.Commands);
        s.Boundary = new ProposalBoundary(s.World, s.Clock, s.Commands, s.Autonomy);
        s.Club = new Club(s.World);

        List<HomeRecord> homes = new List<HomeRecord>();
        homes.Add(s.World.ExportSnapshot().Homes[0]);
        for (int i = 1; i < 4; i++) homes.Add(s.World.AllocateHome(Core, 8));
        List<HouseholdRecord> households = new List<HouseholdRecord>();
        households.Add(s.World.ExportSnapshot().Households[0]);
        for (int i = 1; i < 4; i++)
            households.Add(s.World.AllocateHousehold(Core, "Alpha household " + (i + 1), homes[i].Id));

        ResidentRecord[] firstEight = s.World.ResidentsSnapshot();
        Array.Sort(firstEight, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        s.Residents = new ResidentId[full ? 20 : 8];
        for (int i = 0; i < 8; i++) s.Residents[i] = firstEight[i].Id;
        if (full)
        {
            LifeStage[] stages = new LifeStage[12];
            stages[0] = LifeStage.Adult;  // resident 9
            stages[1] = LifeStage.Adult;  // 10
            stages[2] = LifeStage.Child;  // 11 birthday + education
            stages[3] = LifeStage.Adult;  // 12 creative
            stages[4] = LifeStage.Adult;  // 13 course sessions
            stages[5] = LifeStage.Adult;  // 14 birth carrier
            stages[6] = LifeStage.Adult;  // 15 other parent
            stages[7] = LifeStage.Adult;  // 16 spare
            stages[8] = LifeStage.Elder;  // 17 death
            stages[9] = LifeStage.Child;  // 18 care dependent
            stages[10] = LifeStage.Adult; // 19 spouse
            stages[11] = LifeStage.Adult; // 20 storm exposure
            for (int i = 0; i < stages.Length; i++)
            {
                ResidentRecord record = s.World.AllocateResident(Core, null, stages[i],
                    households[0].Id, homes[0].Id);
                s.Residents[8 + i] = record.Id;
                // Delivery needs both parents' appearance records, so every
                // allocated resident gets the same seeded appearance row the
                // factory gives the first eight.
                s.Appearances.Register(record.Id, new WorldRng(seed + 7919L * (long)record.Id));
            }
        }
        s.HomeIds = new HomeId[full ? 4 : 1];
        s.HouseholdIds = new HouseholdId[full ? 4 : 1];
        for (int i = 0; i < s.HomeIds.Length; i++) s.HomeIds[i] = homes[i].Id;
        for (int i = 0; i < s.HouseholdIds.Length; i++) s.HouseholdIds[i] = households[i].Id;

        // Household assignment: 1-6, 7-12, 13-16+19+20, 17-18.
        if (full)
        {
            for (int i = 0; i < 6; i++) s.World.SetHousehold(Core, s.Residents[i], households[0].Id, homes[0].Id);
            for (int i = 6; i < 12; i++) s.World.SetHousehold(Core, s.Residents[i], households[1].Id, homes[1].Id);
            for (int i = 12; i < 16; i++) s.World.SetHousehold(Core, s.Residents[i], households[2].Id, homes[2].Id);
            s.World.SetHousehold(Core, s.Residents[16], households[3].Id, homes[3].Id);
            s.World.SetHousehold(Core, s.Residents[17], households[3].Id, homes[3].Id);
            s.World.SetHousehold(Core, s.Residents[18], households[2].Id, homes[2].Id);
            s.World.SetHousehold(Core, s.Residents[19], households[2].Id, homes[2].Id);
        }
        else
        {
            for (int i = 4; i < 8; i++) s.World.SetHousehold(Core, s.Residents[i], households[1].Id, homes[1].Id);
        }

        // Deterministic needs/money for every resident.
        for (int i = 0; i < s.Residents.Length; i++)
        {
            s.World.SetMoney(Core, s.Residents[i], 10000L);
            for (int n = 0; n < StateSchema.NeedCount; n++)
                s.World.SetNeed(Core, s.Residents[i], (Need)n, 70f);
        }
        if (full) s.World.SetMoney(Core, s.Residents[16], 100L);

        for (int i = 0; i < s.HomeIds.Length; i++)
        {
            s.Food.SetHomeStock(Core, s.HomeIds[i], 16, 4);
            s.Accounts.SetSharedBalance(Core, s.HouseholdIds[i], 1000L);
            s.Home.SetHomeState(Core, s.HomeIds[i], 40f, 8);
        }

        // Object ids must stay clear of resident ids (NAV resolves both).
        ObjectId next;
        do { next = s.World.AllocateObject(Core); } while ((long)next <= s.Residents.Length + 40L);

        // BLOCK-SITES allocates its own object ids, so it must come after the
        // resident-range guard above.
        s.Sites = new BlockSites(s.World, s.Executor);
        s.Work = new Work(s.World, s.Commands, s.Executor, s.Accounts, s.Sites);
        s.Education = new Education(s.World, s.Commands, s.Executor, s.Accounts, s.Sites);
        s.SiteObjects = new ObjectId[s.Sites.Sites.Count];
        for (int i = 0; i < s.Sites.Sites.Count; i++) s.SiteObjects[i] = s.Sites.Sites[i].Object;

        // Spouse/care/sibling identity for the estate and care gates.
        if (full)
        {
            ResidentId guardian = s.Residents[16];
            ResidentId dependent = s.Residents[17];
            ResidentId spouse = s.Residents[18];
            s.World.AddFamilyLink(Core, guardian, spouse, "Spouse");
            s.World.AddFamilyLink(Core, spouse, guardian, "Spouse");
            s.World.AddFamilyLink(Core, dependent, spouse, "Sibling");
            s.World.AddFamilyLink(Core, spouse, dependent, "Sibling");
            s.World.SetCareRequired(Core, dependent, true);
            s.World.AssignCare(Core, new CareAssignment(dependent, guardian,
                new CarePlacement(households[3].Id, -1), default(EventId)));
            s.World.SetStage(Core, guardian, LifeStage.Elder, 31.5f);
            s.World.SetAging(Core, guardian, 31.5, 31.5, false);
            s.World.SetStage(Core, s.Residents[10], LifeStage.Child, 31.5f);
            s.World.SetAging(Core, s.Residents[10], 31.5, 0.0, false);
            // Caregivers are durable Housing state, never inferred from stage.
            for (int i = 0; i < s.Residents.Length; i++)
            {
                if (i == 16 || i == 17) continue;
                s.Housing.SetCaregiver(Core, s.Residents[i], true);
            }
        }

        for (int i = 0; i < s.Residents.Length; i++)
        {
            ApproachPose pose = new ApproachPose(SimX(i), 0f, SimZ(i));
            s.Executor.SetResidentPosition(s.Residents[i], pose);
        }
        return s;
    }

    static float SimX(int index)
    {
        int row = index % 10;
        return 4f + row * 1.2f;
    }

    static float SimZ(int index) { return index < 10 ? 1.5f : 14.5f; }

    void Mirror(Sim s)
    {
        if (residentRoots == null) return;
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        for (int i = 0; i < rows.Length && i < residentRoots.Length; i++)
        {
            if (residentRoots[i] == null) continue;
            if (!rows[i].IsAlive)
            {
                if (residentRoots[i].gameObject.activeSelf) residentRoots[i].gameObject.SetActive(false);
                continue;
            }
            if (!residentRoots[i].gameObject.activeSelf) residentRoots[i].gameObject.SetActive(true);
            if (gymTrail != null && residentRoots[i] == gymTrail.residentRoot) continue;
            ApproachPose pose = s.Executor.GetResidentPosition(rows[i].Id);
            if (float.IsNaN(pose.X) || float.IsNaN(pose.Z) || float.IsInfinity(pose.X) || float.IsInfinity(pose.Z))
                continue;
            residentRoots[i].position = new Vector3(pose.X, 0f, pose.Z);
            residentRoots[i].rotation = Quaternion.identity;
        }
        if (residentLooks != null)
            residentLooks.Sync(residentRoots, rows, s.Appearances, navigation != null ? navigation.SelectedResident : null,
                gymTrail != null ? gymTrail.residentRoot : null, live);
    }

    // ---------- clock / service stepping ----------

    void Advance(Sim s, long gameSeconds)
    {
        long multiplier = s.Clock.Speed == SimulationSpeed.Ultra ? 7L
            : s.Clock.Speed == SimulationSpeed.Fast ? 3L : 1L;
        if (s.Clock.Paused) return;
        s.Clock.AdvanceRealSeconds(Core, (double)gameSeconds / (60.0 * (double)multiplier));
    }

    void TickAll(Sim s, bool life)
    {
        s.Food.Tick(Exec);
        s.Work.Tick(Exec);
        s.Social.Tick(Exec);
        s.Health.Tick(Exec);
        s.Home.Tick(Exec);
        s.Minigolf.Tick(Exec);
        s.Party.Tick(Exec);
        s.Family.Tick(Exec);
        s.Education.Tick(Exec);
        s.Progression.Tick(Exec);
        if (life)
        {
            if (s.Aging != null) s.Aging.Tick(Core);
            if (s.Estate != null) s.Estate.Tick(Exec);
            if (s.Storm != null) s.Storm.Tick(Core);
        }
    }

    bool Step(Sim s, long gameSeconds, bool life)
    {
        Advance(s, gameSeconds);
        TickAll(s, life);
        return true;
    }

    CommandEnvelope Envelope(Sim s, ResidentId resident, string code, long? target)
    {
        return new CommandEnvelope(s.World.AllocateCommand(Core), s.World.Epoch,
            s.World.GameSeconds, CommandSource.PlayerHuman,
            s.Commands.CurrentStamp(resident).Sequence + 1u,
            CommandKind.DirectActivity, resident, new ActivityId(code), target,
            new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate));
    }

    ActionInstanceId CurrentAction(Sim s, ResidentId resident, out bool found)
    {
        ActionEntry entry = s.Commands.GetCurrent(resident);
        if (entry == null)
        {
            ActionEntry[] pending = s.Commands.GetPending(resident);
            if (pending.Length > 0) entry = pending[0];
        }
        found = entry != null;
        return found ? entry.Action : default(ActionInstanceId);
    }

    bool WaitTerminal(Sim s, ActionInstanceId action, long maxGameSeconds, bool life)
    {
        long elapsed = 0L;
        while (elapsed < maxGameSeconds)
        {
            ExecutionRecord record = s.Executor.GetExecution(action);
            if (record != null && record.IsTerminal) return true;
            Step(s, StepSeconds, life);
            elapsed += StepSeconds;
        }
        return false;
    }

    bool Completed(Sim s, ActionInstanceId action)
    {
        ExecutionRecord record = s.Executor.GetExecution(action);
        return record != null && record.Outcome == TerminalOutcome.Completed;
    }

    int SkillPoints(ResidentRecord record, string skill)
    {
        for (int i = 0; i < record.Skills.Count; i++)
            if (string.Equals(record.Skills[i].SkillId, skill, StringComparison.Ordinal))
                return record.Skills[i].Points;
        return 0;
    }

    int SkillLevel(ResidentRecord record, string skill)
    {
        for (int i = 0; i < record.Skills.Count; i++)
            if (string.Equals(record.Skills[i].SkillId, skill, StringComparison.Ordinal))
                return record.Skills[i].Level;
        return 0;
    }

    float NeedOf(Sim s, ResidentId id, Need need)
    {
        ResidentRecord record;
        if (!s.World.TryGetResident(id, out record)) return 0f;
        return record.GetNeed(need);
    }

    long MoneyOf(Sim s, ResidentId id)
    {
        ResidentRecord record;
        if (!s.World.TryGetResident(id, out record)) return 0L;
        return record.Money;
    }

    float Friendship(Sim s, ResidentId from, ResidentId toward)
    {
        ResidentRecord record;
        if (!s.World.TryGetResident(from, out record)) return 0f;
        for (int i = 0; i < record.Bonds.Count; i++)
            if (record.Bonds[i].Other == toward) return record.Bonds[i].Friendship;
        return 0f;
    }

    double Cleanliness(Sim s, HomeId home)
    {
        Home.HomeSnapshot snapshot = s.Home.Export();
        for (int i = 0; i < snapshot.States.Count; i++)
            if (snapshot.States[i].Home == (long)home) return snapshot.States[i].Cleanliness;
        return 0.0;
    }

    // ---------- first process ----------

    void RunFirst()
    {
        if (outDir.Length == 0 || tree.Length != 40 || binarySha.Length != 64)
        {
            Fail("bad-args out=" + outDir + " tree=" + tree + " binary=" + binarySha);
            return;
        }
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "frames"));
        sim = BuildWorld(WorldId, Seed, true);
        Mirror(sim);
        Log("first start residents=" + sim.Residents.Length + " tree=" + tree);
        CaptureSiteFrames(sim);
        RunClasses(sim);
        RunProgressionGate(sim);
        RunAiFallbacks(sim);
        RunLifeGates(sim);
        WriteFirstReceipt(sim);
        Log("first done");
    }

    // ---------- site walks ----------

    IEnumerator RunWalkGuarded()
    {
        IEnumerator core = RunWalk();
        while (true)
        {
            bool moved;
            try { moved = core.MoveNext(); }
            catch (Exception e)
            {
                walking = false;
                Fail(e.GetType().Name + ": " + e.Message);
                yield break;
            }
            if (!moved) yield break;
            yield return core.Current;
        }
    }

    IEnumerator RunWalk()
    {
        if (outDir.Length == 0 || tree.Length != 40 || binarySha.Length != 64)
        {
            Fail("bad-args out=" + outDir + " tree=" + tree + " binary=" + binarySha);
            yield break;
        }
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "frames"));
        sim = BuildWorld(WorldId, Seed, true);
        sim.Clock.SetSpeed(SimulationSpeed.Normal);
        Mirror(sim);
        cameraBase = alphaCamera.transform.position;
        cameraRotation = alphaCamera.transform.rotation;
        BindControls();
        Log("walk start sites=" + sim.Sites.Sites.Count + " tree=" + tree);
        // Workshop starts east of the cafe so its follow camera is not behind
        // the cafe building; the other sites keep the first four residents.
        int[] walkers = { 0, 8, 2, 3 };
        List<WalkRow> rows = new List<WalkRow>();
        walking = true;
        for (int i = 0; i < sim.Sites.Sites.Count && i < walkers.Length; i++)
        {
            WalkRow row = new WalkRow();
            IEnumerator site = WalkSite(sim, sim.Sites.Sites[i], sim.Residents[walkers[i]], row);
            while (true)
            {
                bool moved;
                try { moved = site.MoveNext(); }
                catch (Exception e) { Fail(e.GetType().Name + ": " + e.Message); moved = false; }
                if (!moved) break;
                yield return site.Current;
            }
            if (failed) { walking = false; yield break; }
            rows.Add(row);
        }
        walking = false;
        WriteWalkRaw(rows);
        WriteAppliedCameras(rows);
        Log("walk done walks=" + rows.Count);
    }

    IEnumerator WalkSite(Sim s, BlockSite site, ResidentId walker, WalkRow row)
    {
        row.Site = site.Id;
        row.Resident = walker;
        ApproachPose start = s.Executor.GetResidentPosition(walker);
        // A walker starting inside the east-west resident row would line the
        // camera up with its neighbours; shift that lens south of the row,
        // decaying over the first 6 m so the later frames use the radial view.
        float cameraShift = Mathf.Abs(start.Z - site.Origin.Z) < 2f
            && Mathf.Abs(start.X - site.Origin.X) > 8f
            ? -4.5f : 0f;
        double dx = (double)start.X - (double)site.Entry.X;
        double dz = (double)start.Z - (double)site.Entry.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        // NAV walks a straight 1 m/game-minute line, so a 3-tick (0.3 m)
        // sample interval bounds the receipt checks; the margin covers
        // whole-clock-tick rounding before the arrival snap.
        int count = (int)Math.Ceiling(
            Math.Max(0.0, distance - ActivityExecutor.ArrivalToleranceMeters) / 0.3) + 14;
        if (count < 10) count = 10;
        int mid = count / 2;
        followSite = site;
        followShift = cameraShift;
        CommandResult follow = navigation.FollowResident(walker);
        if (!follow.Accepted)
        {
            Fail("walk follow refused " + site.Id + " " + follow.Reason);
            yield break;
        }
        // One real frame per sample: Update applies CameraControl.FollowPosition
        // through ApplyCamera before the sample and capture are read.
        yield return null;
        if (!WalkSampleAt(s, site, walker, row, 0, 0)) yield break;
        CommandResult command = navigation.TravelTo(walker, (long)site.Object, Blockparty.Contracts.QueueMode.Immediate);
        if (!command.Accepted)
        {
            Fail("walk command refused " + site.Id + " " + command.Reason);
            yield break;
        }
        Log("walk command site=" + site.Id + " resident=" + (long)walker
            + " accepted=true route=WorldNavigation.TravelTo");
        ActionEntry entry = s.Commands.GetCurrent(walker);
        if (entry == null) { Fail("walk action missing " + site.Id); yield break; }
        ActionInstanceId action = entry.Action;
        bool arrived = false;
        for (int index = 1; index < count; index++)
        {
            long beforeTicks = s.Clock.TotalTicks;
            s.Clock.AdvanceRealSeconds(Core, 0.3);
            long ticks = s.Clock.TotalTicks - beforeTicks;
            for (long tick = 0L; tick < ticks; tick++) s.Executor.Tick(Exec);
            Mirror(s);
            followShift = cameraShift * Mathf.Clamp01(1f - index * 0.3f / 6f);
            yield return null;
            int stage = index == mid ? 1 : index == count - 1 ? 2 : -1;
            if (!WalkSampleAt(s, site, walker, row, index, stage)) yield break;
            ExecutionRecord record = s.Executor.GetExecution(action);
            if (record != null && record.IsTerminal) arrived = record.Outcome == TerminalOutcome.Completed;
            if (index == count - 1 && !arrived)
            {
                Fail("walk did not arrive site=" + site.Id + " samples=" + count + " distance=" + N(distance));
                yield break;
            }
        }
        Log("walk " + site.Id + " resident=" + (long)walker + " samples=" + count + " distance=" + N(distance));
    }

    bool WalkSampleAt(Sim s, BlockSite site, ResidentId walker, WalkRow row, int index, int stage)
    {
        navigation.Refresh();
        if (!navigation.FollowedResident.HasValue || navigation.FollowedResident.Value != walker)
        {
            Fail("walk follow lost " + site.Id + " sample " + index);
            return false;
        }
        if (cameraControl == null || !cameraControl.FollowedResident.HasValue)
        {
            Fail("walk camera not bound " + site.Id + " sample " + index);
            return false;
        }
        if (alphaCamera == null) { Fail("no camera"); return false; }
        ApproachPose pose = s.Executor.GetResidentPosition(walker);
        ApproachPose followed = cameraControl.FollowPosition;
        if (Math.Abs(followed.X - pose.X) > 1e-4f || Math.Abs(followed.Y - pose.Y) > 1e-4f
            || Math.Abs(followed.Z - pose.Z) > 1e-4f)
        {
            Fail("walk camera follow position mismatch " + site.Id + " sample " + index);
            return false;
        }
        WalkSample sample = new WalkSample();
        sample.GameSeconds = s.World.GameSeconds;
        sample.Position = pose;
        sample.Followed = navigation.FollowedResident.Value;
        sample.Camera = alphaCamera.transform.position;
        sample.Rotation = alphaCamera.transform.rotation;
        if (stage >= 0)
        {
            string[] names = { "start", "mid", "arrival" };
            row.Frames[stage] = "frames/walk-" + site.Id + "-" + names[stage] + ".png";
            CaptureCurrent("walk:" + site.Id + ":" + names[stage]);
            if ((alphaCamera.transform.position - sample.Camera).sqrMagnitude > 1e-8f)
            {
                Fail("walk capture moved camera " + site.Id + " sample " + index);
                return false;
            }
        }
        row.Samples.Add(sample);
        return true;
    }

    // Presentation-only follow camera: orbit outward from the site origin so
    // the followed resident stays between the lens and the building. The pose
    // is CameraControl.FollowPosition, not a separately calculated resident pose.
    static void FollowCamera(BlockSite site, ApproachPose pose, float shift, out Vector3 camera, out Vector3 look)
    {
        float dx = pose.X - site.Origin.X;
        float dz = pose.Z - site.Origin.Z;
        float length = Mathf.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f) { dx = 0f; dz = 1f; length = 1f; }
        dx /= length;
        dz /= length;
        camera = new Vector3(pose.X + dx * 6.5f, pose.Y + 4.2f, pose.Z + dz * 6.5f + shift);
        look = Mathf.Abs(shift) > 0.01f
            ? new Vector3(pose.X, pose.Y + 1.0f, pose.Z)
            : new Vector3(pose.X - dx * 1.5f, pose.Y + 1.1f, pose.Z - dz * 1.5f);
    }

    void RunClasses(Sim s)
    {
        // FOOD: cook at home, then capture the acting resident.
        ResidentId food = s.Residents[0];
        PlaceForClass(s, "FOOD", food);
        long money0 = MoneyOf(s, food);
        float hunger0 = NeedOf(s, food, Need.Hunger);
        int cook0 = SkillPoints(ResidentOf(s, food), "Cooking");
        SubmitDirect(s, food, "ACT-FOOD-01", null, s.Food);
        ActionInstanceId action = WaitAccepted(s, food, "ACT-FOOD-01");
        if (!WaitTerminal(s, action, 3L * 3600L, false) || !Completed(s, action)) { Fail("FOOD not completed"); return; }
        Step(s, StepSeconds, false);
        ResidentRecord foodRecord = ResidentOf(s, food);
        string foodEffects = "{\"hunger\":" + N((double)(foodRecord.GetNeed(Need.Hunger) - hunger0))
            + ",\"cooking_points\":" + N((long)(SkillPoints(foodRecord, "Cooking") - cook0))
            + ",\"money\":" + N(foodRecord.Money - money0) + "}";
        CookingDelta = SkillPoints(foodRecord, "Cooking") - cook0;
        RecordClass("FOOD", food, "ACT-FOOD-01", null, foodEffects);
        CaptureClass("FOOD", s, food);

        // WORK: barista shift at the cafe slot.
        ResidentId worker = s.Residents[1];
        PlaceForClass(s, "WORK", worker);
        s.World.SetCareer(Core, worker, "Barista", 1, 0);
        BlockSiteSlot cafeSlot = SlotOf(s, "cafe", "ACT-WORK-01");
        long workMoney0 = MoneyOf(s, worker);
        int workCareer0 = ResidentOf(s, worker).CareerPoints;
        CommandResult workResult = s.Work.Submit(Envelope(s, worker, "ACT-WORK-01", (long)cafeSlot.Object));
        if (!workResult.Accepted) { Fail("WORK refused " + workResult.Reason); return; }
        ActionInstanceId workAction = WaitAccepted(s, worker, "ACT-WORK-01");
        if (!WaitTerminal(s, workAction, 10L * 3600L, false) || !Completed(s, workAction)) { Fail("WORK not completed"); return; }
        Step(s, StepSeconds, false);
        ResidentRecord workerRecord = ResidentOf(s, worker);
        string workEffects = "{\"money\":" + N(workerRecord.Money - workMoney0)
            + ",\"career_points\":" + N((long)(workerRecord.CareerPoints - workCareer0)) + "}";
        CareerDelta = workerRecord.CareerPoints - workCareer0;
        RecordClass("WORK", worker, "ACT-WORK-01", "cafe", workEffects);
        CaptureClass("WORK", s, worker);

        // EDUCATION: child attends the in-block school desk.
        ResidentId learner = s.Residents[10];
        PlaceForClass(s, "EDUCATION", learner);
        BlockSiteSlot schoolSlot = SlotOf(s, "school", "ACT-EDU-01");
        int edu0 = ResidentOf(s, learner).EducationPoints;
        CommandResult eduResult = s.Education.Submit(Envelope(s, learner, "ACT-EDU-01", (long)schoolSlot.Object));
        if (!eduResult.Accepted) { Fail("EDUCATION refused " + eduResult.Reason); return; }
        ActionInstanceId eduAction = WaitAccepted(s, learner, "ACT-EDU-01");
        if (!WaitTerminal(s, eduAction, 10L * 3600L, false) || !Completed(s, eduAction)) { Fail("EDUCATION not completed"); return; }
        Step(s, StepSeconds, false);
        string eduEffects = "{\"education_points\":" + N((long)(ResidentOf(s, learner).EducationPoints - edu0)) + "}";
        RecordClass("EDUCATION", learner, "ACT-EDU-01", "school", eduEffects);
        CaptureClass("EDUCATION", s, learner);

        // SOCIAL: chat with a nearby resident.
        ResidentId social = s.Residents[2];
        ResidentId partner = s.Residents[3];
        PlaceForClass(s, "SOCIAL", social);
        ClassStage socialStage = StageOf("SOCIAL");
        if (socialStage != null)
            s.Executor.SetResidentPosition(partner,
                new ApproachPose(socialStage.actor.position.x, 0f, socialStage.actor.position.z + 1.2f));
        float friendship0 = Friendship(s, social, partner);
        CommandResult socialResult = s.Commands.Submit(Envelope(s, social, "ACT-REL-01", (long)partner));
        if (!socialResult.Accepted) { Fail("SOCIAL refused " + socialResult.Reason); return; }
        ActionInstanceId socialAction = WaitAccepted(s, social, "ACT-REL-01");
        if (!WaitTerminal(s, socialAction, 2L * 3600L, false) || !Completed(s, socialAction)) { Fail("SOCIAL not completed"); return; }
        Step(s, StepSeconds, false);
        string socialEffects = "{\"friendship\":" + N((double)(Friendship(s, social, partner) - friendship0)) + "}";
        RecordClass("SOCIAL", social, "ACT-REL-01", null, socialEffects);
        CaptureClassWithCompanions("SOCIAL", s, social, new[] { partner },
            new[] { new Vector3(0f, 0f, 1.2f) }, true);

        // PARTY: host a party; Party.Tick charges at perform entry.
        ResidentId host = s.Residents[4];
        PlaceForClass(s, "PARTY", host);
        long hostMoney0 = MoneyOf(s, host);
        CommandResult partyResult = s.Commands.Submit(Envelope(s, host, "ACT-PARTY-01", null));
        if (!partyResult.Accepted) { Fail("PARTY refused " + partyResult.Reason); return; }
        ActionInstanceId partyAction = WaitAccepted(s, host, "ACT-PARTY-01");
        if (!WaitTerminal(s, partyAction, 5L * 3600L, false) || !Completed(s, partyAction)) { Fail("PARTY not completed"); return; }
        Step(s, StepSeconds, false);
        string partyEffects = "{\"money\":" + N(MoneyOf(s, host) - hostMoney0) + "}";
        RecordClass("PARTY", host, "ACT-PARTY-01", null, partyEffects);
        ResidentId[] guests = { s.Residents[7], s.Residents[8], s.Residents[9] };
        Vector3[] guestOffsets =
        {
            new Vector3(-1.8f, 0f, 1.4f), new Vector3(1.8f, 0f, 1.4f), new Vector3(0f, 0f, 2.8f),
        };
        ClassStage partyStage = StageOf("PARTY");
        if (partyStage != null)
        {
            for (int i = 0; i < guests.Length; i++)
                s.Executor.SetResidentPosition(guests[i], new ApproachPose(
                    partyStage.actor.position.x + guestOffsets[i].x, 0f,
                    partyStage.actor.position.z + guestOffsets[i].z));
        }
        CaptureClassWithCompanions("PARTY", s, host, guests, guestOffsets);

        // MINIGOLF: one human participant finishes three holes.
        ResidentId golfer = s.Residents[5];
        PlaceForClass(s, "MINIGOLF", golfer);
        RunMinigolf(s, golfer);
        CaptureClass("MINIGOLF", s, golfer);

        // HEALTH: rest recovery.
        ResidentId patient = s.Residents[6];
        PlaceForClass(s, "HEALTH", patient);
        float health0 = NeedOf(s, patient, Need.Health);
        CommandResult healthResult = s.Health.Submit(Envelope(s, patient, "ACT-HEALTH-02", null));
        if (!healthResult.Accepted) { Fail("HEALTH refused " + healthResult.Reason); return; }
        ActionInstanceId healthAction = WaitAccepted(s, patient, "ACT-HEALTH-02");
        if (!WaitTerminal(s, healthAction, 5L * 3600L, false) || !Completed(s, healthAction)) { Fail("HEALTH not completed"); return; }
        Step(s, StepSeconds, false);
        string healthEffects = "{\"health\":" + N((double)(NeedOf(s, patient, Need.Health) - health0)) + "}";
        RecordClass("HEALTH", patient, "ACT-HEALTH-02", null, healthEffects);
        CaptureClass("HEALTH", s, patient);

        // CLUB: attend, then hold the real meeting boundary.
        ResidentId member = s.Residents[7];
        ResidentId clubmate = s.Residents[8];
        PlaceForClass(s, "CLUB", member);
        ClassStage clubStage = StageOf("CLUB");
        if (clubStage != null)
            s.Executor.SetResidentPosition(clubmate,
                new ApproachPose(clubStage.actor.position.x + 1.6f, 0f, clubStage.actor.position.z));
        s.Club.SetMembership(Core, "GardenClub", member, true);
        s.Club.SetMembership(Core, "GardenClub", clubmate, true);
        CommandResult clubResult = s.Commands.Submit(Envelope(s, member, "ACT-CLUB-01", null));
        if (!clubResult.Accepted) { Fail("CLUB refused " + clubResult.Reason); return; }
        ActionInstanceId clubAction = WaitAccepted(s, member, "ACT-CLUB-01");
        if (!WaitTerminal(s, clubAction, 5L * 3600L, false) || !Completed(s, clubAction)) { Fail("CLUB not completed"); return; }
        float clubSocial0 = NeedOf(s, member, Need.Social);
        RelationshipResult meeting = s.Club.ClubMeeting(Core, "GardenClub", new[] { member, clubmate });
        if (!meeting.Applied) { Fail("CLUB meeting refused " + meeting.Reason); return; }
        string clubEffects = "{\"social\":" + N((double)(NeedOf(s, member, Need.Social) - clubSocial0)) + "}";
        RecordClass("CLUB", member, "ACT-CLUB-01", null, clubEffects);
        CaptureClass("CLUB", s, member);

        // HOME: clean the acting resident's household.
        ResidentId cleaner = s.Residents[9];
        PlaceForClass(s, "HOME", cleaner);
        HomeId cleanerHome = ResidentOf(s, cleaner).Home;
        double clean0 = Cleanliness(s, cleanerHome);
        CommandResult homeResult = s.Home.Submit(Envelope(s, cleaner, "ACT-HOME-01", null));
        if (!homeResult.Accepted) { Fail("HOME refused " + homeResult.Reason); return; }
        ActionInstanceId homeAction = WaitAccepted(s, cleaner, "ACT-HOME-01");
        if (!WaitTerminal(s, homeAction, 3L * 3600L, false) || !Completed(s, homeAction)) { Fail("HOME not completed"); return; }
        Step(s, StepSeconds, false);
        string homeEffects = "{\"cleanliness\":" + N(Cleanliness(s, cleanerHome) - clean0) + "}";
        RecordClass("HOME", cleaner, "ACT-HOME-01", null, homeEffects);
        CaptureClass("HOME", s, cleaner);

        // CREATIVE: make wall art from a recorded factual memory.
        ResidentId author = s.Residents[11];
        PlaceForClass(s, "CREATIVE", author);
        long creativeMoney0 = MoneyOf(s, author);
        int creativeSkill0 = SkillPoints(ResidentOf(s, author), "Creativity");
        List<ResidentId> participants = new List<ResidentId>();
        participants.Add(author);
        WorldEvent memoryEvent = s.World.AppendEvent(Core, "Met", author, "alpha creative memory");
        s.World.AddMemory(Core, author, new MemoryEntry(memoryEvent.Event, "Met",
            s.World.GameSeconds, s.HomeIds[2], participants.AsReadOnly(), "OK", "participant"));
        CommandResult creativeResult = s.Commands.Submit(Envelope(s, author, "ACT-CREA-01", null));
        if (!creativeResult.Accepted) { Fail("CREATIVE refused " + creativeResult.Reason); return; }
        ActionInstanceId creativeAction = WaitAccepted(s, author, "ACT-CREA-01");
        if (!WaitTerminal(s, creativeAction, 5L * 3600L, false) || !Completed(s, creativeAction)) { Fail("CREATIVE not completed"); return; }
        if (!s.Creative.Complete(Exec, creativeAction, new ActivityId("ACT-CREA-01"), author, memoryEvent.Event))
        {
            Fail("CREATIVE completion boundary refused");
            return;
        }
        ResidentRecord authorRecord = ResidentOf(s, author);
        string creativeEffects = "{\"money\":" + N(authorRecord.Money - creativeMoney0)
            + ",\"creativity_points\":" + N((long)(SkillPoints(authorRecord, "Creativity") - creativeSkill0)) + "}";
        RecordClass("CREATIVE", author, "ACT-CREA-01", null, creativeEffects);
        CaptureClass("CREATIVE", s, author);
    }

    void RegisterGolfCourse(Sim s, ObjectId course)
    {
        Minigolf.Hole[] holes = new Minigolf.Hole[3];
        for (int i = 0; i < holes.Length; i++)
        {
            float tee = 34f + i * 2.6f;
            holes[i] = new Minigolf.Hole(new ApproachPose(tee, 0f, -8f),
                new ApproachPose(tee + 1.8f, 0f, -8f), tee - 1.0, tee + 4.0, -9.0, -7.0, 0.75);
        }
        s.Minigolf.RegisterCourse(course, holes, 1.0, 0.1);
    }

    void RunMinigolf(Sim s, ResidentId golfer)
    {
        // The course sits at the visual greens built by AlphaSceneBuild.
        ObjectId course = s.World.AllocateObject(Core);
        s.Executor.RegisterObject(course, new[] { new ApproachPose(34f, 0f, -8f) });
        RegisterGolfCourse(s, course);
        s.Executor.SetResidentPosition(golfer, new ApproachPose(34f, 0f, -8f));
        float fun0 = NeedOf(s, golfer, Need.Fun);
        ActionInstanceId action = s.Minigolf.Start(course, new[] { golfer }, new[] { true });
        for (int guard = 0; guard < 200; guard++)
        {
            Step(s, StepSeconds, false);
            Minigolf.MatchSnapshot match = s.Minigolf.GetMatch(action);
            if (match == null) { Fail("MINIGOLF match missing"); return; }
            if (match.Completed) break;
            if (!match.Rolling)
            {
                if (!s.Minigolf.SubmitPutt(action, match.HoleIndex, match.TurnIndex,
                    match.ActiveResident, 0, 30))
                {
                    Fail("MINIGOLF putt refused hole=" + match.HoleIndex);
                    return;
                }
            }
        }
        Minigolf.MatchSnapshot final = s.Minigolf.GetMatch(action);
        if (final == null || !final.Completed || !Completed(s, action)) { Fail("MINIGOLF not completed"); return; }
        ClassStage minigolfStage = StageOf("MINIGOLF");
        if (minigolfStage != null)
            s.Executor.SetResidentPosition(golfer, new ApproachPose(
                minigolfStage.actor.position.x, 0f, minigolfStage.actor.position.z));
        string effects = "{\"fun\":" + N((double)(NeedOf(s, golfer, Need.Fun) - fun0)) + "}";
        RecordClass("MINIGOLF", golfer, "ACT-SPORT-02", null, effects);
    }

    void RunProgressionGate(Sim s)
    {
        ResidentId learner = s.Residents[12];
        s.World.SetMoney(Core, learner, 10000L);
        for (int session = 0; session < 8; session++)
        {
            CommandResult result = s.Education.Submit(Envelope(s, learner, "ACT-EDU-03", null));
            if (!result.Accepted) { Fail("course refused " + result.Reason + " session=" + session); return; }
            ActionInstanceId action = WaitAccepted(s, learner, "ACT-EDU-03");
            if (!WaitTerminal(s, action, 5L * 3600L, false) || !Completed(s, action))
            {
                Fail("course not completed session=" + session);
                return;
            }
            Step(s, StepSeconds, false);
        }
        ResidentRecord record = ResidentOf(s, learner);
        int sessions = 0;
        for (int i = 0; i < s.World.EffectLedger.Count; i++)
        {
            EffectLedgerEntry entry = s.World.EffectLedger[i];
            if (entry.Recipient.First == learner && entry.EffectKind == "ACT-EDU-03"
                && entry.State == LedgerState.Committed) sessions++;
        }
        bool certificate = false;
        for (int i = 0; i < record.CompletedCourses.Count; i++)
            if (record.CompletedCourses[i] == "CulinaryCertificate") certificate = true;
        string gate = "{\"kind\":\"ProgressionApplied\",\"observation\":\"normalized gate roll-up\","
            + "\"gate_values\":{\"cooking_delta\":" + N((double)CookingDelta) + ","
            + "\"career_delta\":" + N((double)CareerDelta) + ","
            + "\"course_sessions\":" + N((long)sessions) + ","
            + "\"certificate_unlocked\":" + B(certificate) + "},"
            + "\"observed_from\":[\"ACT-FOOD-01 Cooking skill\",\"ACT-WORK-01 Progression career points\","
            + "\"8 x ACT-EDU-03 committed sessions\"]}";
        events.Add(gate);
        if (!certificate || sessions < 8 || CookingDelta != 10 || CareerDelta != 20)
        {
            Fail("progression gate incomplete cooking=" + CookingDelta + " career=" + CareerDelta
                + " sessions=" + sessions + " certificate=" + certificate);
        }
    }

    int CookingDelta;
    int CareerDelta;

    void InitializeLife(Sim s)
    {
        s.AgingStartGameSeconds = s.World.GameSeconds;
        s.Aging = new Aging(s.World, s.Clock);
        s.Storm = new SevereStorm(s.World, s.Executor, s.Home, s.Club);
        s.Estate = new EstateCare(StackOf(s));

        s.CommonObjects = new ObjectId[3];
        for (int i = 0; i < s.CommonObjects.Length; i++)
        {
            s.CommonObjects[i] = s.World.AllocateObject(Core);
            s.Executor.RegisterObject(s.CommonObjects[i], new[] { new ApproachPose(4f + i, 0f, 4f) });
            s.Storm.RegisterObject(Core, s.CommonObjects[i], true, false, true, null);
        }
        s.Roof = s.World.AllocateObject(Core);
        s.Executor.RegisterObject(s.Roof, new[] { new ApproachPose(-16f, 0f, 2f) });
        s.Storm.RegisterObject(Core, s.Roof, false, false, false, s.HomeIds[0]);
        s.Electric = s.World.AllocateObject(Core);
        s.Executor.RegisterObject(s.Electric, new[] { new ApproachPose(8f, 0f, 8f) });
        s.Storm.RegisterObject(Core, s.Electric, false, true, false, null);
        s.Outdoor = s.World.AllocateObject(Core);
        s.Executor.RegisterObject(s.Outdoor, new[] { new ApproachPose(10f, 0f, 10f) });
        s.Storm.RegisterObject(Core, s.Outdoor, true, false, false, null);
        s.Shelter = s.World.AllocateObject(Core);
        s.Executor.RegisterObject(s.Shelter, new[] { new ApproachPose(-16f, 0f, 4f) });
        s.Storm.RegisterObject(Core, s.Shelter, false, false, false, null);
    }

    void RunLifeGates(Sim s)
    {
        // Accelerated-clock seed: the storm gate opens at world age 30 days.
        s.World.SetGameSeconds(Core, 30L * Day);
        InitializeLife(s);
        s.Storm.SetOutdoors(Core, s.Residents[19], true, s.Shelter);
        s.World.SetNeed(Core, s.Residents[19], Need.Health, 80f);

        // Conception through the real TryForBaby command; the unified loop
        // below drives it together with the estate, care, aging and storm.
        ResidentId carrier = s.Residents[13];
        ResidentId other = s.Residents[14];
        s.Bed = s.World.AllocateObject(Core);
        ApproachPose bedPose = new ApproachPose(-16f, 0f, 22f);
        s.Executor.RegisterObject(s.Bed, new[] { bedPose, bedPose });
        s.Family.RegisterBed(Core, s.Bed, s.HomeIds[2]);
        s.Family.SetReproductiveCapabilities(Core, carrier, true, false);
        s.Family.SetReproductiveCapabilities(Core, other, false, true);
        s.Family.SetPartner(Core, carrier, other, true);
        s.Executor.SetResidentPosition(carrier, bedPose);
        CommandResult baby = s.Family.Submit(Envelope(s, carrier, "ACT-REL-06", (long)s.Bed));
        if (!baby.Accepted) { Fail("birth command refused " + baby.Reason); return; }
        ActionInstanceId babyAction = WaitAccepted(s, carrier, "ACT-REL-06");

        long estateBefore = 0L;
        long estateAfter = 0L;
        long estateTotal = 0L;
        bool deathSeen = false;
        WorldEvent careEvent = null;
        long carePlacesAtTransfer = 0L;
        WorldEvent warningEvent = null;
        WorldEvent stormEvent = null;
        WorldEvent birthdayEvent = null;
        WorldEvent birthEvent = null;
        long stormStart = 0L;
        long warningPopulation = 0L;
        long warningLead = 0L;
        long exposureDelta = 0L;
        bool unsafeWeatherAtStart = false;
        long due = 0L;
        long gestation = 0L;
        PregnancyRecord pregnancy = default(PregnancyRecord);
        long guard = 0L;
        while (guard < 5L * 24L * 3600L)
        {
            long stepStart = s.World.GameSeconds;
            long estateBalanceStart = s.Accounts.Balance(AccountRef.Shared(s.HouseholdIds[2]));
            long guardianMoneyStart = MoneyOf(s, s.Residents[16]);
            long exposedHealthStart = (long)NeedOf(s, s.Residents[19], Need.Health);
            Step(s, StepSeconds, true);
            guard += StepSeconds;

            if (!deathSeen)
            {
                WorldEvent death = FindEventAfter(s, "Death", stepStart, s.Residents[16], true);
                if (death != null)
                {
                    deathSeen = true;
                    estateTotal = guardianMoneyStart;
                    estateBefore = estateBalanceStart;
                    estateAfter = s.Accounts.Balance(AccountRef.Shared(s.HouseholdIds[2]));
                }
            }
            if (warningEvent == null)
            {
                WorldEvent warning = FindEventAfter(s, "SevereStormWarning", stepStart, null, false);
                if (warning != null)
                {
                    warningEvent = warning;
                    warningPopulation = LivingPopulation(s);
                    warningLead = (s.Storm.ScheduledStartGameSeconds ?? 0L) - warning.GameSeconds;
                }
            }
            if (warningEvent != null && stormEvent == null)
            {
                WorldEvent storm = FindEventAfter(s, "SevereStorm", stepStart, null, false);
                if (storm != null)
                {
                    stormEvent = storm;
                    stormStart = storm.GameSeconds;
                    exposureDelta = (long)(NeedOf(s, s.Residents[19], Need.Health) - exposedHealthStart);
                    unsafeWeatherAtStart = !s.Storm.IsElectricEnabled(s.Electric);
                }
            }
            if (due == 0L && s.Family.TryGetPregnancy(carrier, out pregnancy))
            {
                due = pregnancy.DueGameTime;
                gestation = due - EventTime(s, pregnancy.ConceptionEvent);
            }
            if (birthdayEvent == null)
                birthdayEvent = FindEventAfter(s, "Birthday", stepStart, s.Residents[10], true);
            if (birthEvent == null)
                birthEvent = FindEventAfter(s, "Birth", stepStart, carrier, true);
            if (careEvent == null)
            {
                careEvent = FindEventAfter(s, "CareTransfer", stepStart, s.Residents[17], true);
                // Capacity at the transfer boundary, before the later birth
                // consumes the same household's remaining bed.
                if (careEvent != null)
                    carePlacesAtTransfer = s.World.ExportSnapshot().Homes[2].BedCount
                        - MembersOf(s, s.HouseholdIds[2]);
            }
            if (birthEvent != null && stormEvent != null && due != 0L
                && s.World.GameSeconds >= due + 1800L)
                break;
        }
        if (!deathSeen) { Fail("no death observed"); return; }
        if (careEvent == null) { Fail("no care transfer observed"); return; }
        if (warningEvent == null) { Fail("no storm warning"); return; }
        if (stormEvent == null) { Fail("storm did not start"); return; }
        if (birthdayEvent == null) { Fail("no birthday observed"); return; }
        if (birthEvent == null) { Fail("no birth observed"); return; }
        if (!s.Family.TryGetPregnancy(carrier, out pregnancy) && due == 0L)
        {
            Fail("conception never started");
            return;
        }

        long cooldownSeconds = s.Storm.CooldownUntilGameSeconds - (stormStart + 21600L);
        long availableCommon = s.CommonObjects.Length;
        long occupiedRoofs = 1L;
        long damagedCommon = 0L;
        for (int i = 0; i < s.CommonObjects.Length; i++)
            if (!string.IsNullOrEmpty(s.Storm.DamageState(s.CommonObjects[i]))) damagedCommon++;
        long damagedRoofs = string.IsNullOrEmpty(s.Storm.DamageState(s.Roof)) ? 0L : 1L;

        string childText = birthEvent.Detail.Replace("child ", "").Trim();
        long childId = 0L;
        long.TryParse(childText, NumberStyles.Integer, CultureInfo.InvariantCulture, out childId);
        BirthRecord birthRecord = s.Family.GetBirth((ResidentId)childId);
        if (birthRecord == null) { Fail("birth record missing child=" + childId); return; }
        ResidentId guardianId = s.Residents[18];
        for (int i = s.World.CareAssignments.Count - 1; i >= 0; i--)
        {
            if (s.World.CareAssignments[i].Dependent == s.Residents[17])
            {
                guardianId = s.World.CareAssignments[i].Guardian;
                break;
            }
        }
        ResidentRecord dependentRow;
        s.World.TryGetResident(s.Residents[17], out dependentRow);
        if (dependentRow == null || dependentRow.Household != s.HouseholdIds[2])
            Fail("care transfer did not move the dependent into household 3");
        long availablePlaces = carePlacesAtTransfer;
        ResidentRecord birthdayRecord;
        s.World.TryGetResident(s.Residents[10], out birthdayRecord);
        if (birthdayRecord == null || birthdayRecord.Stage != LifeStage.Teen)
            Fail("birthday transition did not reach Teen");
        double stageDaysBefore = 31.5 + (double)(birthdayEvent.GameSeconds - s.AgingStartGameSeconds) / 86400.0;

        events.Add("{\"kind\":\"Birthday\",\"gate_values\":{\"before\":\"Child\",\"after\":\"Teen\","
            + "\"stage_duration_days\":" + N(31.5) + ",\"stage_days_before\":"
            + N(stageDaysBefore) + "}}");
        events.Add("{\"kind\":\"Birth\",\"gate_values\":{\"gestation_seconds\":" + N(gestation)
            + ",\"birth_count\":1,\"child_id\":" + Q(N(childId))
            + ",\"carrier_id\":" + Q(N((long)carrier))
            + ",\"other_parent_id\":" + Q(N((long)other)) + "}}");
        events.Add("{\"kind\":\"Death\",\"gate_values\":{\"estate_total\":" + N(estateTotal)
            + ",\"distributed_total\":" + N(estateAfter - estateBefore)
            + ",\"settlement_count\":1}}");
        events.Add("{\"kind\":\"CareTransfer\",\"gate_values\":{\"dependent_id\":" + Q(N((long)s.Residents[17]))
            + ",\"guardian_id\":" + Q(N((long)guardianId))
            + ",\"available_places\":" + N(availablePlaces)
            + ",\"transfer_count\":1}}");
        events.Add("{\"kind\":\"SevereStormWarning\",\"gate_values\":{\"population\":" + N(warningPopulation)
            + ",\"world_age_days\":" + N(warningEvent.GameSeconds / Day)
            + ",\"cooldown_remaining\":0,\"warning_lead_seconds\":" + N(warningLead) + "}}");
        events.Add("{\"kind\":\"SevereStorm\",\"gate_values\":{\"duration_seconds\":" + N(21600L)
            + ",\"cooldown_seconds\":" + N(cooldownSeconds)
            + ",\"exposure_health_delta\":" + N(exposureDelta)
            + ",\"unsafe_weather\":" + B(unsafeWeatherAtStart)
            + ",\"available_common\":" + N(availableCommon)
            + ",\"damaged_common\":" + N(damagedCommon)
            + ",\"occupied_roofs\":" + N(occupiedRoofs)
            + ",\"damaged_roofs\":" + N(damagedRoofs) + "}}");

        if (estateAfter - estateBefore != estateTotal) Fail("estate totals differ before=" + estateBefore + " after=" + estateAfter);
        if (gestation < 259200L) Fail("gestation too short " + gestation);
        if (availablePlaces < 1L) Fail("care capacity gate " + availablePlaces);
        if (warningPopulation < 16L) Fail("storm population gate " + warningPopulation);
        if (warningEvent.GameSeconds / Day < 30L) Fail("storm world age gate");
        if (warningLead != 86400L) Fail("warning lead " + warningLead);
        if (cooldownSeconds != 1728000L) Fail("storm cooldown " + cooldownSeconds);
        if (exposureDelta != -20L) Fail("storm exposure delta " + exposureDelta);
        if (damagedCommon != Math.Min(2L, availableCommon)) Fail("common damage gate");
        if (damagedRoofs != Math.Min(1L, occupiedRoofs)) Fail("roof damage gate");
        if (!unsafeWeatherAtStart) Fail("storm unsafe weather not observed");
        Log("life done death=" + estateTotal + "/" + (estateAfter - estateBefore)
            + " birth=" + childId + " warning=" + warningPopulation + " stormDamage=" + damagedCommon + "/" + damagedRoofs);
    }

    long MembersOf(Sim s, HouseholdId household)
    {
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        long count = 0L;
        for (int i = 0; i < rows.Length; i++) if (rows[i].IsAlive && rows[i].Household == household) count++;
        return count;
    }

    long LivingPopulation(Sim s)
    {
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        long count = 0L;
        for (int i = 0; i < rows.Length; i++) if (rows[i].IsAlive) count++;
        return count;
    }

    long EventTime(Sim s, EventId id)
    {
        IReadOnlyList<WorldEvent> events = s.World.Events;
        for (int i = 0; i < events.Count; i++) if (events[i].Event == id) return events[i].GameSeconds;
        return 0L;
    }

    static WorldEvent FindEventAfter(Sim s, string kind, long after, ResidentId? actor, bool actorOnly)
    {
        IReadOnlyList<WorldEvent> all = s.World.Events;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].GameSeconds < after || all[i].Kind != kind) continue;
            if (actorOnly && (!all[i].Actor.HasValue || all[i].Actor.Value != actor)) continue;
            if (actor.HasValue && all[i].Actor.HasValue && all[i].Actor.Value != actor.Value && !actorOnly) continue;
            return all[i];
        }
        return null;
    }

    void RunAiFallbacks(Sim s)
    {
        ServiceEndpoint image = new ServiceEndpoint("IMAGE", "https://image.invalid/v1", "muse-image-1.0", true);
        ServiceEndpoint voice = new ServiceEndpoint("VOICE", "https://voice.invalid/v1", "muse-audio-1.0", true);
        ServiceEndpoint sam = new ServiceEndpoint("SAM", "https://sam.invalid/v1", "sam-3", true);
        ServiceEndpoint d1u = new ServiceEndpoint("D1U", "https://decision.invalid/v1", "d1u-1", true);

        // Reuse the live work item created by the CREATIVE class.
        WorkItem liveWork = s.Creative.WorkItems.Count > 0 ? s.Creative.WorkItems[0] : null;
        if (liveWork == null) { Fail("no creative work item for IMAGE fallback"); return; }
        ImageAdapter adapter = new ImageAdapter(s.World, s.Boundary, s.Creative);
        int before = s.Boundary.DecisionLog.Count;
        adapter.Request(liveWork.Reference.Item, image, false, true);
        string imageReason = ReasonOf(s.Boundary, before);
        ImageContent content = adapter.GetContent(liveWork.Reference.Item);
        aiEntries.Add("{\"service\":\"IMAGE\",\"outcome\":\"local-fallback\",\"reason\":" + Q(imageReason)
            + ",\"connected_success\":false,\"content_path\":" + Q(content == null ? "missing" : content.Path) + "}");

        before = s.Boundary.DecisionLog.Count;
        VoiceAdapter voiceAdapter = new VoiceAdapter(s.World, s.Boundary, s.Commands, s.Sites, s.Executor);
        // The adapter short-circuits a missing credential itself; a missing
        // first-send disclosure reaches the boundary and records the fixed
        // Denied reason, so this exercises the boundary's own fallback gate.
        voiceAdapter.Request(voice, true, false, true);
        aiEntries.Add("{\"service\":\"VOICE\",\"outcome\":\"local-fallback\",\"reason\":" + Q(ReasonOf(s.Boundary, before))
            + ",\"connected_success\":false,\"prompt\":" + Q(voiceAdapter.Prompt) + "}");

        before = s.Boundary.DecisionLog.Count;
        CaptureSearch search = new CaptureSearch(s.Boundary);
        search.SetCapture((CaptureId)41L, new byte[64 * 64 * 4], 64, 64);
        search.Request("find the cafe", sam, false, true);
        aiEntries.Add("{\"service\":\"SAM\",\"outcome\":\"local-fallback\",\"reason\":" + Q(ReasonOf(s.Boundary, before))
            + ",\"connected_success\":false,\"message\":" + Q(search.Message) + "}");

        before = s.Boundary.DecisionLog.Count;
        s.Commands.EnableAutomation();
        DecisionAdapter decision = new DecisionAdapter(s.Boundary, new NoTransport());
        decision.RequestDecision(s.Residents[12], d1u, false, true);
        ProposalLogEntry last = s.Boundary.DecisionLog[s.Boundary.DecisionLog.Count - 1];
        string detail = last.Detail;
        int semi = detail.IndexOf(';');
        string d1uReason = semi < 0 ? detail : detail.Substring(0, semi);
        aiEntries.Add("{\"service\":\"D1U\",\"outcome\":\"local-fallback\",\"reason\":" + Q(d1uReason)
            + ",\"connected_success\":false,\"fallback\":" + Q(semi < 0 ? "" : detail.Substring(semi + 1)) + "}");
        // The local autonomy fallback is a real enqueue; clear it so both
        // household saves quit with no in-progress or pending action.
        ClearQueue(s, s.Residents[12]);
        Log("ai fallbacks " + aiEntries.Count);
    }

    void ClearQueue(Sim s, ResidentId resident)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            ActionEntry current = s.Commands.GetCurrent(resident);
            ActionEntry[] pending = s.Commands.GetPending(resident);
            if (current == null && pending.Length == 0) return;
            bool head = current != null;
            long? target = head ? (long?)null : (long?)pending[0].Command;
            CommandEnvelope envelope = new CommandEnvelope(s.World.AllocateCommand(Core), s.World.Epoch,
                s.World.GameSeconds, CommandSource.PlayerHuman,
                s.Commands.CurrentStamp(resident).Sequence + 1u,
                head ? CommandKind.CancelCurrent : CommandKind.CancelQueued,
                resident, null, target, CommandParameters.None.Instance);
            s.Commands.Submit(envelope);
            Step(s, StepSeconds, false);
        }
        Fail("continuation queue did not clear");
    }

    sealed class NoTransport : IDecisionTransport
    {
        public void Send(GatewayRequest request, DecisionPayload payload) { }
        public bool TryReceive(out DecisionResponse response) { response = null; return false; }
    }

    string ReasonOf(ProposalBoundary boundary, int from)
    {
        if (boundary.DecisionLog.Count <= from) return "missing";
        string detail = boundary.DecisionLog[boundary.DecisionLog.Count - 1].Detail;
        int semi = detail.IndexOf(';');
        return semi < 0 ? detail : detail.Substring(0, semi);
    }

    void WriteWalkRaw(List<WalkRow> rows)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\"schema\":\"blockparty-site-walk/1\",\"source\":\"player-command\",")
            .Append("\"platform\":\"StandaloneLinux64\",\"host\":\"i7\",\"tree_hash\":").Append(Q(tree))
            .Append(",\"binary_sha256\":").Append(Q(binarySha)).Append(",\"walks\":[");
        string[] names = { "start", "mid", "arrival" };
        for (int w = 0; w < rows.Count; w++)
        {
            if (w > 0) json.Append(',');
            WalkRow row = rows[w];
            json.Append("{\"site_id\":").Append(Q(row.Site))
                .Append(",\"resident_id\":").Append(Q(N((long)row.Resident)))
                .Append(",\"command\":{\"kind\":\"TravelTo\",\"route\":\"WorldNavigation.TravelTo\",\"resident_id\":")
                .Append(Q(N((long)row.Resident))).Append(",\"site_id\":").Append(Q(row.Site))
                .Append(",\"accepted\":true},\"samples\":[");
            for (int i = 0; i < row.Samples.Count; i++)
            {
                if (i > 0) json.Append(',');
                WalkSample sample = row.Samples[i];
                json.Append("{\"game_seconds\":").Append(N(sample.GameSeconds))
                    .Append(",\"position\":[").Append(N((double)sample.Position.X)).Append(',')
                    .Append(N((double)sample.Position.Y)).Append(',').Append(N((double)sample.Position.Z))
                    .Append("],\"followed_resident_id\":").Append(Q(N((long)sample.Followed)))
                    .Append(",\"camera_position\":[").Append(N((double)sample.Camera.x)).Append(',')
                    .Append(N((double)sample.Camera.y)).Append(',').Append(N((double)sample.Camera.z))
                    .Append("]}");
            }
            json.Append("],\"frames\":[");
            int[] index = { 0, row.Samples.Count / 2, row.Samples.Count - 1 };
            for (int f = 0; f < 3; f++)
            {
                if (f > 0) json.Append(',');
                json.Append("{\"id\":").Append(Q(names[f])).Append(",\"sample_index\":")
                    .Append(N((long)index[f])).Append(",\"file\":").Append(Q(row.Frames[f])).Append('}');
            }
            json.Append("]}");
        }
        json.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "walk-raw.json"), json.ToString());
    }

    void WriteAppliedCameras(List<WalkRow> rows)
    {
        StringBuilder json = new StringBuilder();
        json.Append("{\"schema\":\"blockparty-site-walk-applied/1\",\"tree_hash\":").Append(Q(tree))
            .Append(",\"binary_sha256\":").Append(Q(binarySha)).Append(",\"walks\":[");
        for (int w = 0; w < rows.Count; w++)
        {
            if (w > 0) json.Append(',');
            WalkRow row = rows[w];
            json.Append("{\"site_id\":").Append(Q(row.Site)).Append(",\"samples\":[");
            for (int i = 0; i < row.Samples.Count; i++)
            {
                if (i > 0) json.Append(',');
                WalkSample sample = row.Samples[i];
                json.Append("{\"game_seconds\":").Append(N(sample.GameSeconds))
                    .Append(",\"followed_resident_id\":").Append(Q(N((long)sample.Followed)))
                    .Append(",\"applied_camera_position\":[").Append(N((double)sample.Camera.x)).Append(',')
                    .Append(N((double)sample.Camera.y)).Append(',').Append(N((double)sample.Camera.z))
                    .Append("],\"applied_camera_rotation\":[").Append(N((double)sample.Rotation.x)).Append(',')
                    .Append(N((double)sample.Rotation.y)).Append(',').Append(N((double)sample.Rotation.z))
                    .Append(',').Append(N((double)sample.Rotation.w)).Append("]}");
            }
            json.Append("]}");
        }
        json.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "applied-cameras.json"), json.ToString());
    }

    void WriteFirstReceipt(Sim s)
    {
        StringBuilder classesJson = new StringBuilder();
        classesJson.Append("{\"source\":\"player-command\",\"completions\":[");
        for (int i = 0; i < classes.Count; i++)
        {
            if (i > 0) classesJson.Append(',');
            ClassRow row = classes[i];
            classesJson.Append("{\"resident_id\":").Append(Q(N((long)row.Resident)))
                .Append(",\"activity_id\":").Append(Q(row.Activity))
                .Append(",\"outcome\":\"Completed\",\"effects\":").Append(row.Effects);
            if (!string.IsNullOrEmpty(row.Site)) classesJson.Append(",\"site_id\":").Append(Q(row.Site));
            classesJson.Append('}');
        }
        classesJson.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "classes-raw.json"), classesJson.ToString());

        StringBuilder eventsJson = new StringBuilder();
        eventsJson.Append("{\"events\":[");
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0) eventsJson.Append(',');
            eventsJson.Append(events[i]);
        }
        eventsJson.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "events-raw.json"), eventsJson.ToString());

        StringBuilder aiJson = new StringBuilder();
        aiJson.Append("{\"entries\":[");
        for (int i = 0; i < aiEntries.Count; i++)
        {
            if (i > 0) aiJson.Append(',');
            aiJson.Append(aiEntries[i]);
        }
        aiJson.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "ai-raw.json"), aiJson.ToString());

        StringBuilder frameJson = new StringBuilder();
        frameJson.Append("{\"frames\":[");
        bool first = true;
        foreach (KeyValuePair<string, string> row in frames)
        {
            if (!first) frameJson.Append(',');
            first = false;
            frameJson.Append("{\"id\":").Append(Q(row.Key)).Append(",\"file\":").Append(Q(row.Value)).Append('}');
        }
        frameJson.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "frames-raw.json"), frameJson.ToString());

        // Household context A: the integrated world.
        string beforeA = Normalized(StackOf(s));
        File.WriteAllText(Path.Combine(outDir, "h1-before-state.json"), beforeA);
        GameStack stackA = StackOf(s);
        SaveWriteResult saveA = SaveV1.SaveGame(stackA, Path.Combine(outDir, "h1-save"));
        if (!saveA.Ok) { Fail("context A save failed: " + saveA.Detail); return; }
        File.Copy(Path.Combine(outDir, "h1-save", "save.json"), Path.Combine(outDir, "h1-save.json"), true);

        // Household context B: an independent neighborhood, saved and quit.
        Sim b = BuildWorld(WorldId + 1L, 7L, false);
        string beforeB = Normalized(StackOf(b));
        File.WriteAllText(Path.Combine(outDir, "h2-before-state.json"), beforeB);
        GameStack stackB = StackOf(b);
        SaveWriteResult saveB = SaveV1.SaveGame(stackB, Path.Combine(outDir, "h2-save"));
        if (!saveB.Ok) { Fail("context B save failed: " + saveB.Detail); return; }
        File.Copy(Path.Combine(outDir, "h2-save", "save.json"), Path.Combine(outDir, "h2-save.json"), true);

        StringBuilder firstJson = new StringBuilder();
        firstJson.Append("{\"pid\":").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
            .Append(",\"tree\":").Append(Q(tree)).Append(",\"binary_sha256\":").Append(Q(binarySha))
            .Append(",\"unity\":").Append(Q(Application.unityVersion))
            .Append(",\"contexts\":[\"h1\",\"h2\"],\"frames\":").Append(N((long)frames.Count)).Append('}');
        File.WriteAllText(Path.Combine(outDir, "first.json"), firstJson.ToString());
        Log("first receipt written classes=" + classes.Count + " events=" + events.Count);
    }

    GameStack StackOf(Sim s)
    {
        return new GameStack(s.World, s.Clock, s.Commands, s.Executor, s.Appearances,
            s.Accounts, s.Food, s.Expenses, s.Ownership, s.Home, s.Club, s.Housing, s.Creative, s.Family);
    }

    ResidentRecord ResidentOf(Sim s, ResidentId id)
    {
        ResidentRecord record;
        if (!s.World.TryGetResident(id, out record)) throw new InvalidOperationException("missing resident " + (long)id);
        return record;
    }

    BlockSiteSlot SlotOf(Sim s, string siteId, string code)
    {
        for (int i = 0; i < s.Sites.Sites.Count; i++)
        {
            BlockSite site = s.Sites.Sites[i];
            if (site.Id != siteId) continue;
            for (int j = 0; j < site.Slots.Count; j++)
                if (site.Slots[j].ActivityCode == code) return site.Slots[j];
        }
        throw new InvalidOperationException("missing site slot " + siteId + "/" + code);
    }

    void SubmitDirect(Sim s, ResidentId resident, string code, long? target, Food food)
    {
        CommandResult result = food.Submit(Envelope(s, resident, code, target));
        if (!result.Accepted) throw new InvalidOperationException(code + " refused " + result.Reason);
    }

    ActionInstanceId WaitAccepted(Sim s, ResidentId resident, string code)
    {
        bool found;
        ActionInstanceId action = CurrentAction(s, resident, out found);
        if (!found) throw new InvalidOperationException("accepted action missing " + code);
        return action;
    }

    void RecordClass(string id, ResidentId resident, string activity, string site, string effects)
    {
        ClassRow row = new ClassRow();
        row.Id = id;
        row.Resident = resident;
        row.Activity = activity;
        row.Site = site;
        row.Effects = effects;
        classes.Add(row);
        Log("class " + id + " resident=" + (long)resident + " effects=" + effects);
    }

    // ---------- frames ----------

    void CaptureSiteFrames(Sim s)
    {
        // One frame per registered site: the ALPHA receipt requires every BlockSites address.
        for (int i = 0; i < s.Sites.Sites.Count; i++)
        {
            BlockSite site = s.Sites.Sites[i];
            // The glTF modules glaze the north wall; view them from the north
            // on the side away from the homes so the whole facade reads.
            float side = (i % 2 == 0) ? -1f : 1f;
            ResidentId actor = s.Residents[i % s.Residents.Length];
            ResidentId companion = s.Residents[(i + 1) % s.Residents.Length];
            ApproachPose front = new ApproachPose(
                site.Origin.X + side * 1.4f, 0f, site.Origin.Z + 4.4f);
            s.Executor.SetResidentPosition(actor, front);
            s.Executor.SetResidentPosition(companion,
                new ApproachPose(front.X - side * 0.9f, 0f, front.Z - 0.5f));
            Mirror(s);
            CaptureFrom("site:" + site.Id,
                new Vector3(site.Origin.X + side * 7.5f, 5.2f, site.Origin.Z + 10.5f),
                new Vector3(site.Origin.X, 1.4f, site.Origin.Z + 0.6f));
        }
    }

    void CaptureClass(string id, Sim s, ResidentId resident)
    {
        CaptureClassWithCompanions(id, s, resident, null, null);
    }

    void CaptureClassWithCompanions(string id, Sim s, ResidentId resident,
        ResidentId[] companions, Vector3[] offsets, bool clearLens = false)
    {
        ClassStage stage = StageOf(id);
        Mirror(s);
        if (stage == null)
        {
            ApproachPose pose = s.Executor.GetResidentPosition(resident);
            CaptureFrom(id, new Vector3(pose.X + 2.2f, 1.9f, pose.Z - 2.4f),
                new Vector3(pose.X, 1.1f, pose.Z));
            return;
        }
        PlaceVisual(s, resident, stage.actor.position, stage.actor.rotation);
        if (companions != null && offsets != null)
        {
            Vector3 facePoint = id == "PARTY"
                ? stage.actor.position + new Vector3(0f, 0f, 1.4f)
                : stage.actor.position;
            for (int i = 0; i < companions.Length && i < offsets.Length; i++)
            {
                Vector3 position = stage.actor.position + offsets[i];
                Vector3 direction = facePoint - position;
                float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                PlaceVisual(s, companions[i], position, Quaternion.Euler(0f, yaw, 0f));
            }
        }
        List<GameObject> hidden = clearLens
            ? HideNearLens(s, stage.camera.position, resident, companions) : null;
        try { CaptureFrom(id, stage.camera.position, stage.look.position); }
        finally
        {
            if (hidden != null) for (int i = 0; i < hidden.Count; i++) hidden[i].SetActive(true);
        }
    }

    // Bystanders standing in the class lens read as a wall of colour (SOCIAL's
    // orange arc), so the SOCIAL shot hides the ones within 4.5 m of it.
    List<GameObject> HideNearLens(Sim s, Vector3 cameraPosition,
        ResidentId resident, ResidentId[] companions)
    {
        var hidden = new List<GameObject>();
        if (residentRoots == null) return hidden;
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        for (int i = 0; i < rows.Length && i < residentRoots.Length; i++)
        {
            if (residentRoots[i] == null || rows[i].Id == resident) continue;
            bool participant = false;
            if (companions != null)
                for (int j = 0; j < companions.Length; j++) participant |= companions[j] == rows[i].Id;
            if (participant) continue;
            if ((residentRoots[i].position - cameraPosition).sqrMagnitude < 20.25f)
            {
                residentRoots[i].gameObject.SetActive(false);
                hidden.Add(residentRoots[i].gameObject);
            }
        }
        return hidden;
    }

    void PlaceVisual(Sim s, ResidentId resident, Vector3 position, Quaternion rotation)
    {
        Transform root = RootOf(s, resident);
        if (root == null) return;
        root.position = position;
        root.rotation = rotation;
    }

    Transform RootOf(Sim s, ResidentId resident)
    {
        if (residentRoots == null) return null;
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        for (int i = 0; i < rows.Length && i < residentRoots.Length; i++)
            if (rows[i].Id == resident) return residentRoots[i];
        return null;
    }

    ClassStage StageOf(string id)
    {
        if (classStages == null) return null;
        for (int i = 0; i < classStages.Length; i++)
            if (classStages[i] != null && string.Equals(classStages[i].id, id, StringComparison.Ordinal))
                return classStages[i];
        return null;
    }

    void PlaceForClass(Sim s, string id, ResidentId resident)
    {
        ClassStage stage = StageOf(id);
        if (stage == null) return;
        s.Executor.SetResidentPosition(resident,
            new ApproachPose(stage.actor.position.x, 0f, stage.actor.position.z));
    }

    void CaptureFrom(string id, Vector3 cameraPosition, Vector3 lookAt)
    {
        if (alphaCamera == null) { Fail("no camera"); return; }
        alphaCamera.transform.position = cameraPosition;
        alphaCamera.transform.LookAt(lookAt);
        CaptureCurrent(id);
    }

    // Capture the camera exactly where the shared application left it.
    void CaptureCurrent(string id)
    {
        if (alphaCamera == null) { Fail("no camera"); return; }
        const int width = 1280, heightPixels = 720;
        alphaCamera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        RenderTexture target = new RenderTexture(width, heightPixels, 24);
        Texture2D texture = new Texture2D(width, heightPixels, TextureFormat.RGB24, false);
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = alphaCamera.targetTexture;
        alphaCamera.targetTexture = target;
        alphaCamera.Render();
        RenderTexture.active = target;
        texture.ReadPixels(new Rect(0f, 0f, (float)width, (float)heightPixels), 0, 0);
        texture.Apply();
        alphaCamera.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        string file = "frames/" + id.Replace(':', '-') + ".png";
        Directory.CreateDirectory(Path.Combine(outDir, "frames"));
        File.WriteAllBytes(Path.Combine(outDir, file), texture.EncodeToPNG());
        frames[id] = file;
        UnityEngine.Object.Destroy(target);
        UnityEngine.Object.Destroy(texture);
        Log("frame " + id + " -> " + file);
    }

    // ---------- second process ----------

    void RunSecond()
    {
        if (outDir.Length == 0) { Fail("bad-args out"); return; }
        string pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        RunSecondContext("h1", pid);
        if (failed) return;
        RunSecondContext("h2", pid);
        if (failed) return;
        StringBuilder second = new StringBuilder();
        second.Append("{\"pid\":").Append(pid).Append(",\"tree\":").Append(Q(tree))
            .Append(",\"binary_sha256\":").Append(Q(binarySha))
            .Append(",\"unity\":").Append(Q(Application.unityVersion)).Append('}');
        File.WriteAllText(Path.Combine(outDir, "second.json"), second.ToString());
        Log("second done");
    }

    void RunSecondContext(string key, string pid)
    {
        string saveDir = Path.Combine(outDir, key + "-save");
        SaveLoadResult loaded = SaveV1.LoadGame(saveDir, this);
        if (!loaded.Ok) { Fail(key + " load failed: " + loaded.Notice); return; }
        LoadedGame game = loaded.Game;
        string after = Normalized(new GameStack(game.World, game.Clock, game.Arbitrator, game.Executor,
            game.Appearances, game.Accounts, game.Food, game.Expenses, game.Ownership,
            game.Home, game.Club, game.Housing, game.Creative, game.Family));
        string beforePath = Path.Combine(outDir, key + "-before-state.json");
        string before = File.Exists(beforePath) ? File.ReadAllText(beforePath) : "";
        File.WriteAllText(Path.Combine(outDir, key + "-after-state.json"), after);
        if (before != after)
        {
            Fail(key + " normalized continuity differs");
            return;
        }
        // Continue in the loaded world through the real command path.
        game.Clock.SetSpeed(SimulationSpeed.Ultra);
        ResidentRecord[] rows = game.World.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        ResidentId actor = rows[0].Id;
        ContinueLoaded(game, actor, key);
        if (failed) return;
        GameStack stack = new GameStack(game.World, game.Clock, game.Arbitrator, game.Executor,
            game.Appearances, game.Accounts, game.Food, game.Expenses, game.Ownership,
            game.Home, game.Club, game.Housing, game.Creative, game.Family);
        SaveWriteResult resave = SaveV1.SaveGame(stack, Path.Combine(outDir, key + "-resave"));
        if (!resave.Ok) { Fail(key + " resave failed: " + resave.Detail); return; }
        File.Copy(Path.Combine(outDir, key + "-resave", "save.json"),
            Path.Combine(outDir, key + "-reloaded-save.json"), true);
        Log(key + " second context complete");
    }

    void ContinueLoaded(LoadedGame game, ResidentId actor, string key)
    {
        CommandEnvelope envelope = new CommandEnvelope(game.World.AllocateCommand(Core), game.World.Epoch,
            game.World.GameSeconds, CommandSource.PlayerHuman,
            game.Arbitrator.CurrentStamp(actor).Sequence + 1u,
            CommandKind.DirectActivity, actor, new ActivityId("ACT-HOME-01"), null,
            new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate));
        CommandResult result = game.Arbitrator.Submit(envelope);
        if (!result.Accepted) { Fail(key + " continuation refused " + result.Reason); return; }
        ActionEntry entry = game.Arbitrator.GetCurrent(actor);
        if (entry == null) { Fail(key + " continuation action missing"); return; }
        ResidentRecord resident;
        if (!game.World.TryGetResident(actor, out resident)) { Fail(key + " resident missing"); return; }
        float cleanlinessBefore = game.Home.Cleanliness(resident.Home);
        long elapsed = 0L;
        while (elapsed < 3L * 3600L)
        {
            ExecutionRecord record = game.Executor.GetExecution(entry.Action);
            if (record != null && record.IsTerminal) break;
            game.Clock.AdvanceRealSeconds(Core, 60.0 / 420.0);
            game.Home.Tick(Exec);
            elapsed += 60L;
        }
        ExecutionRecord final = game.Executor.GetExecution(entry.Action);
        if (final == null || final.Outcome != TerminalOutcome.Completed)
        {
            Fail(key + " continuation not completed");
            return;
        }
        StringBuilder continued = new StringBuilder();
        continued.Append("{\"resident_id\":").Append(Q(N((long)actor)))
            .Append(",\"activity_id\":\"ACT-HOME-01\",\"outcome\":\"Completed\",\"pid\":")
            .Append(Q(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)))
            .Append(",\"cleanliness_before\":").Append(N((double)cleanlinessBefore))
            .Append(",\"cleanliness_after\":").Append(N((double)game.Home.Cleanliness(resident.Home))).Append('}');
        File.WriteAllText(Path.Combine(outDir, key + "-continued.json"), continued.ToString());
    }

    // ---------- normalized state export ----------

    string Normalized(GameStack stack)
    {
        WorldState world = stack.World;
        Accounts accounts = stack.Accounts;
        Food food = stack.Food;
        StringBuilder sb = new StringBuilder();
        ResidentRecord[] residents = world.ResidentsSnapshot();
        Array.Sort(residents, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        sb.Append("{\"residents\":[");
        for (int i = 0; i < residents.Length; i++)
        {
            ResidentRecord r = residents[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(N((long)r.Id))
              .Append(",\"alive\":").Append(B(r.IsAlive))
              .Append(",\"stage\":").Append(Q(r.Stage.ToString()))
              .Append(",\"household\":").Append(N((long)r.Household))
              .Append(",\"home\":").Append(N((long)r.Home))
              .Append(",\"money\":").Append(N(r.Money))
              .Append(",\"career\":[").Append(Q(r.JobId)).Append(',').Append(N((long)r.CareerLevel))
              .Append(',').Append(N((long)r.CareerPoints)).Append(']')
              .Append(",\"education\":").Append(N((long)r.EducationPoints))
              .Append(",\"needs\":[");
            for (int n = 0; n < StateSchema.NeedCount; n++)
            {
                if (n > 0) sb.Append(',');
                sb.Append(N((double)r.GetNeed((Need)n)));
            }
            sb.Append("],\"skills\":[");
            List<SkillEntry> skills = new List<SkillEntry>(r.Skills);
            skills.Sort(delegate (SkillEntry a, SkillEntry b)
            {
                int byId = string.CompareOrdinal(a.SkillId, b.SkillId);
                return byId != 0 ? byId : a.Level.CompareTo(b.Level);
            });
            for (int k = 0; k < skills.Count; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('[').Append(Q(skills[k].SkillId)).Append(',').Append(N((long)skills[k].Level))
                  .Append(',').Append(N((long)skills[k].Points)).Append(']');
            }
            sb.Append("],\"courses\":[");
            List<string> courses = new List<string>(r.CompletedCourses);
            courses.Sort(StringComparer.Ordinal);
            for (int c = 0; c < courses.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(Q(courses[c]));
            }
            sb.Append("]}");
        }
        sb.Append("],\"homes\":[");
        List<HomeRecord> homes = world.ExportSnapshot().Homes;
        homes.Sort(delegate (HomeRecord a, HomeRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        for (int i = 0; i < homes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(N((long)homes[i].Id))
              .Append(",\"beds\":").Append(N((long)homes[i].BedCount))
              .Append(",\"stock\":[").Append(N((long)food.GroceryUnits(homes[i].Id)))
              .Append(',').Append(N((long)food.PreparedMeals(homes[i].Id))).Append("]}");
        }
        sb.Append("],\"households\":[");
        List<HouseholdRecord> households = world.ExportSnapshot().Households;
        households.Sort(delegate (HouseholdRecord a, HouseholdRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        for (int i = 0; i < households.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(N((long)households[i].Id))
              .Append(",\"name\":").Append(Q(households[i].Name))
              .Append(",\"home\":").Append(N((long)households[i].Home))
              .Append(",\"balance\":").Append(N(accounts.Balance(AccountRef.Shared(households[i].Id)))).Append('}');
        }
        sb.Append("],\"ledger\":[");
        List<long> amounts = new List<long>();
        for (int i = 0; i < accounts.Ledger.Count; i++) amounts.Add(accounts.Ledger[i].Amount);
        amounts.Sort();
        for (int i = 0; i < amounts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(N(amounts[i]));
        }
        sb.Append("],\"town_fund\":").Append(N(world.TownFund));
        JsonObject payload = SaveV1.SerializePayload(stack, world.ExportSnapshot());
        foreach (string key in new[] { "home", "club", "family" })
        {
            sb.Append(',').Append(Q(key)).Append(':');
            if (!payload.Has(key)) { sb.Append("null"); continue; }
            JsonObject section = payload.GetObject(key, "payload");
            if (key == "home")
            {
                // Load regenerates action identities; retain every other durable field.
                JsonArray tracked = section.GetArray("tracked", "home");
                for (int i = 0; i < tracked.Count; i++)
                    tracked.GetObject(i, "home.tracked").Set("action", new JsonNumber("0"));
            }
            sb.Append(SaveV1.RenderJson(section));
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ---------- live / smoke ----------

    string LiveTuningPath()
    {
        // Editor and player data folders first; repository runs fall back to cwd.
        string besideData = Path.Combine(Application.dataPath, "Blockparty/Content/tuning.json");
        return File.Exists(besideData) ? besideData : LiveTuning;
    }

    void StartLive()
    {
        live = true;
        try
        {
            liveSim = LiveSimulation.Create(Seed, LiveTuningPath());
            freeWill = PlayerPrefs.GetInt(FreeWillPref, 1) != 0;
            liveSim.FreeWill = freeWill;
            Log("free will on=" + B(freeWill));
            sim = BindLive(liveSim);
            activityObjects = new ObjectId[ClassNames.Length];
            for (int i = 0; i < activityObjects.Length; i++)
            {
                activityObjects[i] = sim.World.AllocateObject(Core);
                ClassStage stage = StageOf(ClassNames[i]);
                Vector3 position = stage == null ? Vector3.zero : stage.actor.position;
                sim.Executor.RegisterObject(activityObjects[i], new[] { new ApproachPose(position.x, 0f, position.z) });
            }
            RegisterGolfCourse(sim, activityObjects[5]);
            sim.World.SetCareer(Core, sim.Residents[0], "Barista", 1, 0);
            saveDirectory = Arg(Environment.GetCommandLineArgs(), "--alpha-save");
            if (saveDirectory.Length == 0) saveDirectory = Path.Combine(Application.persistentDataPath, "alpha-save");
            cameraBase = alphaCamera.transform.position;
            cameraRotation = alphaCamera.transform.rotation;
            BindControls();
            Mirror(sim);
            CraftsBind(false);
            SamLookBind();
            if (!evidence) OpenTitle();
            Log("live start residents=" + sim.Residents.Length
                + " entry=" + "Blockparty.Simulation.LiveSimulation.AdvanceRealSeconds");
        }
        catch (Exception e) { Fail("live start " + e); }
    }

    Sim BindLive(LiveSimulation source)
    {
        GameStack g = source.Game;
        Sim s = new Sim();
        s.World = g.World; s.Clock = g.Clock; s.Commands = g.Arbitrator; s.Executor = g.Executor;
        s.Appearances = g.Appearances; s.Accounts = g.Accounts; s.Food = g.Food; s.Expenses = g.Expenses;
        s.Ownership = g.Ownership; s.Home = g.Home; s.Club = g.Club; s.Housing = g.Housing;
        s.Creative = g.Creative; s.Family = g.Family; s.Party = g.Party;
        s.Work = source.Work; s.Education = source.Education; s.Social = source.Social; s.Health = source.Health;
        s.Minigolf = source.Minigolf; s.Progression = source.Progression; s.Aging = source.Aging;
        s.Estate = source.Estate; s.Storm = source.Storm; s.Autonomy = source.Autonomy;
        s.Sites = source.Sites; s.Bed = source.Bed;
        s.CommonObjects = source.CommonObjects; s.Roof = source.Roof; s.Electric = source.Electric;
        s.Outdoor = source.Outdoor; s.Shelter = source.Shelter;
        s.Residents = Roster(g.World);
        HomeRecord[] homes = g.World.ExportSnapshot().Homes.ToArray();
        s.HomeIds = Array.ConvertAll(homes, h => h.Id);
        HouseholdRecord[] households = g.World.ExportSnapshot().Households.ToArray();
        s.HouseholdIds = Array.ConvertAll(households, h => h.Id);
        s.AgingStartGameSeconds = g.World.GameSeconds;
        return s;
    }

    static ResidentId[] Roster(WorldState world)
    {
        ResidentRecord[] rows = world.ResidentsSnapshot();
        Array.Sort(rows, delegate (ResidentRecord a, ResidentRecord b) { return ((long)a.Id).CompareTo((long)b.Id); });
        return Array.ConvertAll(rows, r => r.Id);
    }

    void LiveTick()
    {
        if (sim == null || failed) return;
        try
        {
            liveTick += Time.deltaTime;
            if (liveTick < 0.25f) return;
            double seconds = Math.Min(liveTick, 0.5f);
            liveTick = 0f;
            friendshipBefore.Clear();
            foreach (ResidentRecord r in sim.World.ResidentsSnapshot())
                foreach (PairBond bond in r.Bonds) friendshipBefore[((long)r.Id, (long)bond.Other)] = bond.Friendship;
            liveSim.AdvanceRealSeconds(seconds);
            // Party answers, arrivals, payoff and auto-slowdown pings (the sim already set Normal speed).
            foreach (LiveSimulation.LiveNotice ping in liveSim.DrainNotices())
            {
                pings.Add(ping);
                GazetteNotice(ping.Text);
                Log("live notice slowdown=" + B(ping.Slowdown) + " speed=" + sim.Clock.Speed + " " + ping.Text);
            }
            // Arrivals and births join the roster live; selection is by ResidentId, so it is kept.
            if (sim.World.ResidentCount != sim.Residents.Length) sim.Residents = Roster(sim.World);
            if (navigation != null && navigation.SelectedResident.HasValue)
            {
                ResidentRecord selRec;
                if (sim.World.TryGetResident(navigation.SelectedResident.Value, out selRec) && !selRec.IsAlive)
                {
                    ResidentId firstAlive = default(ResidentId);
                    bool found = false;
                    foreach (ResidentId id in sim.Residents)
                    {
                        ResidentRecord r;
                        if (sim.World.TryGetResident(id, out r) && r.IsAlive)
                        {
                            firstAlive = id;
                            found = true;
                            break;
                        }
                    }
                    if (found) Select(firstAlive);
                }
            }
            string shown = pings.Next(Time.unscaledTime);
            if (shown != null)
            {
                notice = shown;
                Log("live ping shown " + notice);
            }
            foreach (ResidentId[] attendees in liveSim.PaidMeetings) ClubMet(attendees);
            liveSim.PaidMeetings.Clear();
            LogNeeds();
            foreach (ExecutionRecord execution in sim.Executor.Executions)
            {
                if (execution.Outcome != TerminalOutcome.Completed || !liveRewards.Add(execution.Action)) continue;
                ResidentId resident = execution.Resident;
                // ACT-CLUB-01 co-attendance is paid by LiveSimulation (M5-OD-52).
                if (execution.Activity.Code.StartsWith("ACT-REL-", StringComparison.Ordinal) && execution.TargetId.HasValue)
                    SocialCompleted(resident, (ResidentId)execution.TargetId.Value, execution.Activity.Code);
                if (execution.Activity.Code.StartsWith("ACT-CREA-", StringComparison.Ordinal))
                {
                    // Wall Art needs a factual memory its author holds; the newest is the deterministic pick.
                    ResidentRecord author;
                    EventId memory = sim.World.TryGetResident(resident, out author) && author.Memories.Count > 0
                        ? author.Memories[author.Memories.Count - 1].Event : default(EventId);
                    sim.Creative.Complete(Exec, execution.Action, execution.Activity, resident, memory);
                }
                CraftsCompleted(execution);
                hud.ActionCompleted(resident, execution.Activity);
                Log("live completed " + execution.Activity.Code + " resident=" + (long)resident + " action=" + (long)execution.Action);
            }
            Mirror(sim);
        }
        catch (Exception e) { Fail("live tick " + e); }
    }

    // M5-OD-59 session evidence: once per game hour, the clock and each need's lowest value among living residents.
    void LogNeeds()
    {
        long hour = sim.World.GameSeconds / 3600L;
        if (hour == needsLogHour) return;
        needsLogHour = hour;
        StringBuilder line = new StringBuilder("live needs t=").Append(sim.World.GameSeconds);
        for (int i = 0; i < StateSchema.NeedCount; i++)
        {
            float low = float.MaxValue;
            foreach (ResidentRecord r in sim.World.ResidentsSnapshot()) if (r.IsAlive) low = Mathf.Min(low, r.GetNeed((Need)i));
            line.Append(' ').Append((Need)i).Append('=').Append(low.ToString("0.0", CultureInfo.InvariantCulture));
        }
        Log(line.ToString());
    }

    // M5-OD-49: every completed social action logs its pair and Friendship change; a change shows as a toast.
    void SocialCompleted(ResidentId actor, ResidentId partner, string code)
    {
        ResidentRecord a, b;
        if (!sim.World.TryGetResident(actor, out a) || !sim.World.TryGetResident(partner, out b)) return;
        float before;
        friendshipBefore.TryGetValue(((long)actor, (long)partner), out before);
        float after = FriendshipOf(a, partner), delta = after - before;
        Log("live social " + code + " actor=" + a.DisplayName + " partner=" + b.DisplayName
            + " friendship=" + N(before) + "->" + N(after));
        GazetteSocial(a, b, before, after);
        if (delta != 0f)
            notice = a.DisplayName + ": " + (delta > 0f ? "+" : "") + delta.ToString("0.#", CultureInfo.InvariantCulture)
                + " Friendship with " + b.DisplayName;
    }

    // M5-OD-52: one paid club meeting; co-attending members gain Friendship with each other.
    void ClubMet(ResidentId[] attendees)
    {
        List<string> names = new List<string>();
        foreach (ResidentId id in attendees) names.Add(ResidentOf(sim, id).DisplayName);
        Log("live club meeting attendees=" + string.Join(",", names));
        for (int i = 0; i < attendees.Length; i++)
            for (int j = 0; j < attendees.Length; j++)
                if (i != j) SocialCompleted(attendees[i], attendees[j], "ACT-CLUB-01");
    }

    IEnumerator RunSmoke()
    {
        string path = Arg(Environment.GetCommandLineArgs(), "--alpha-smoke");
        if (string.IsNullOrEmpty(path)) path = Path.Combine(Application.persistentDataPath, "alpha-smoke.png");
        try { StartLive(); }
        catch (Exception e) { Fail("smoke start " + e.Message); }
        if (failed || sim == null) { failed = true; done = true; yield break; }
        yield return new WaitForSeconds(0.5f);
        yield return new WaitForEndOfFrame();
        Texture2D texture = null;
        try
        {
            Mirror(sim);
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null) throw new IOException("smoke capture returned no texture");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Log("smoke frame " + path + " residents=" + sim.Residents.Length);
        }
        catch (Exception e) { Fail("smoke capture " + e.GetType().Name + ": " + e.Message); }
        finally { if (texture != null) UnityEngine.Object.Destroy(texture); done = true; }
    }

    // Evidence saves are terminal; live reloads restore the same scene slots
    // before SaveV1 resumes in-progress commands.
    public void RegisterObjects(ActivityExecutor executor)
    {
        if (!live || sim == null) return;
        if(gymTrail!=null)gymTrail.RegisterObjects(executor);
        foreach (BlockSite site in sim.Sites.Sites)
        {
            executor.RegisterObject(site.Object, new[] { site.Entry });
            foreach (BlockSiteSlot slot in site.Slots) executor.RegisterObject(slot.Object, new[] { slot.Pose });
        }
        for (int i = 0; i < activityObjects.Length; i++)
        {
            ClassStage stage = StageOf(ClassNames[i]);
            Vector3 pose = stage == null ? Vector3.zero : stage.actor.position;
            executor.RegisterObject(activityObjects[i], new[] { new ApproachPose(pose.x, 0f, pose.z) });
        }
        CraftsRegister(executor);
        if (liveSim != null) liveSim.RegisterSceneObjects(executor);
    }
}
