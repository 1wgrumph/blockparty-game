using System.IO;
using Blockparty.Contracts;
using Blockparty.Simulation;
using UnityEngine;

// PROOF-SCENE Play-mode driver. The simulation (COMMAND/NAV through
// ActivityExecutor) owns every state transition: this script only advances
// the clock, mirrors sim positions into the scene graph, selects animation
// clips from the execution trace, and captures phase screenshots plus the
// event log. Fast Enter Play Mode resets the entry guard at subsystem
// registration; Awake rebuilds the whole sim from the fixed seed.
public sealed class ProofSceneDirector : MonoBehaviour
{
    public const string SitActivity = "ACT-FOOD-02"; // SDoc: slot Chair at table.
    public const long WorldSeed = 42L;
    public const float TimeoutRealSeconds = 240f;

    [Header("Wired by ProofSceneBuild.BuildScene")]
    public Transform residentRoot;
    public Animation residentAnim;
    public AnimationClip idleClip;
    public AnimationClip walkClip;
    public AnimationClip sitEnterClip;
    public AnimationClip sitIdleClip;
    public AnimationClip sitExitClip;
    public Transform chairFocus;
    public Camera proofCamera;
    public float meshForwardOffsetDeg;

    [Header("Run state (read-only at runtime)")]
    public bool done;
    public bool failed;

    WorldState world;
    Clock clock;
    CommandArbitrator arbitrator;
    ActivityExecutor executor;
    ResidentId hero;
    ActionInstanceId travelAction;
    ActionInstanceId sitAction;
    long ticked;
    float wallTime;
    float phaseTime;
    int phase; // 0 boot, 1 walk-leg1, 2 walk-leg2, 3 align, 4 sit-enter, 5 sit-hold, 6 stand, 7 idle
    bool sitHoldShot;
    bool shot1;
    bool shot4;
    bool shot6;
    string shotDir;
    string[] shotQueue = new string[8];
    int shotCount;
    int shotCooldown;
    int finishCountdown;
    Vector3 lastPos;
    float yaw;

    static readonly StateWriter Core = StateWriter.SimCore;
    static readonly StateWriter Exec = StateWriter.ActivityExecutor;

    static int playEntryCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetPlayState() { playEntryCount = 0; }

    void Awake()
    {
        if (playEntryCount++ != 0)
        {
            failed = done = true;
            Debug.LogError("PROOF_STATIC_RESET FAIL");
            return;
        }
        AppearanceStore appearances;
        world = ResidentFactory.CreateNewGame(1L, WorldSeed, null, out appearances);
        Debug.Log("PROOF_STATIC_RESET PASS initialHash=" + world.ComputeStateHash());
        Debug.Log("PROOF_RUNTIME editor=" + Application.isEditor + " unity=" + Application.unityVersion);
        clock = new Clock(world);
        arbitrator = new CommandArbitrator(world, clock);
        executor = new ActivityExecutor(world, arbitrator, clock);
        ResidentRecord[] residents = world.ResidentsSnapshot();
        hero = residents[0].Id;
        executor.SetResidentPosition(hero, ProofSceneLayout.Start);
        ObjectId waypoint = world.AllocateObject(Core);
        executor.RegisterObject(waypoint, new ApproachPose[] { ProofSceneLayout.Waypoint });
        ObjectId chair = world.AllocateObject(Core);
        executor.RegisterObject(chair, new ApproachPose[] { ProofSceneLayout.Approach });
        travelAction = Submit(CommandKind.TravelTo, null, (long)waypoint, new CommandParameters.Travel(Blockparty.Contracts.QueueMode.Immediate));
        sitAction = Submit(CommandKind.DirectActivity, new ActivityId(SitActivity), (long)chair, new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Append));
        ticked = 0L;
        wallTime = 0f;
        phaseTime = 0f;
        phase = 0;
        sitHoldShot = false;
        shot1 = shot4 = shot6 = false;
        shotCount = 0;
        shotCooldown = 0;
        done = false;
        failed = false;
        string env = System.Environment.GetEnvironmentVariable("PROOF_SHOT_DIR");
        string root = new DirectoryInfo(Application.dataPath).Parent.FullName;
        shotDir = string.IsNullOrEmpty(env) ? Path.Combine(root, "evidence/production/PROOF-SCENE/shots") : env;
        Directory.CreateDirectory(shotDir);
        lastPos = SimPosition();
        yaw = residentRoot.eulerAngles.y;
        residentAnim.AddClip(idleClip, "idle");
        residentAnim.AddClip(walkClip, "walk");
        residentAnim.AddClip(sitEnterClip, "sit-enter");
        residentAnim.AddClip(sitIdleClip, "sit-idle");
        residentAnim.AddClip(sitExitClip, "sit-exit");
        residentAnim["idle"].wrapMode = WrapMode.Loop;
        residentAnim["walk"].wrapMode = WrapMode.Loop;
        residentAnim["sit-idle"].wrapMode = WrapMode.Loop;
        residentAnim["sit-enter"].wrapMode = WrapMode.ClampForever;
        residentAnim["sit-exit"].wrapMode = WrapMode.ClampForever;
        residentAnim.Play("idle");
        Debug.Log("PROOF boot hero=" + ((long)hero).ToString() + " seed=" + WorldSeed.ToString());
    }

    ActionInstanceId Submit(CommandKind kind, ActivityId? activity, long? targetId, CommandParameters parameters)
    {
        ResidentRecord current;
        world.TryGetResident(hero, out current);
        CommandResult result = arbitrator.Submit(new CommandEnvelope(
            world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            kind, hero, activity, targetId, parameters));
        if (!result.Accepted)
            throw new System.Exception("PROOF command rejected: " + result.Reason.ToString());
        ResidentStatus status = executor.GetResidentStatus(hero);
        if (status.Execution != null)
            return status.Execution.Action;
        ActionEntry head = arbitrator.GetCurrent(hero);
        if (head != null && head.Kind == kind && head.TargetId == targetId)
            return head.Action;
        // Appended while another action runs: read the pending tail.
        ActionEntry[] pending = arbitrator.GetPending(hero);
        return pending[pending.Length - 1].Action;
    }

    void Update()
    {
        if (done)
        {
            if (!Application.isEditor) Application.Quit(failed ? 1 : 0);
            return;
        }
        float dt = Mathf.Min(Time.deltaTime, 0.5f);
        wallTime += dt;
        phaseTime += dt;
        clock.AdvanceRealSeconds(Core, dt);
        while (ticked < clock.TotalTicks)
        {
            executor.Tick(Exec);
            ticked++;
        }
        Vector3 pos = SimPosition();
        residentRoot.position = pos;
        UpdatePhase(pos);
        residentRoot.rotation = Quaternion.Euler(0f, yaw, 0f);
        lastPos = pos;
        try { PumpShots(); }
        catch (System.Exception)
        {
            failed = done = true;
            Debug.LogError("PROOF_CAPTURE_FAILED");
        }
        if (wallTime > TimeoutRealSeconds && !done)
        {
            failed = true;
            done = true;
            Debug.Log("PROOF_TIMEOUT phase=" + phase.ToString());
        }
    }

    Vector3 SimPosition()
    {
        ApproachPose pose = executor.GetResidentPosition(hero);
        return new Vector3(pose.X, 0f, pose.Z);
    }

    static bool TraceHas(ExecutionRecord record, ExecutionStage stage)
    {
        System.Collections.Generic.IReadOnlyList<StageRecord> trace = record.Trace;
        for (int i = 0; i < trace.Count; i++)
        {
            if (trace[i].Stage == stage && !trace[i].Skipped)
                return true;
        }
        return false;
    }

    void UpdatePhase(Vector3 pos)
    {
        ExecutionRecord travel = executor.GetExecution(travelAction);
        ExecutionRecord sit = executor.GetExecution(sitAction);
        if (phase == 0 && travel != null && TraceHas(travel, ExecutionStage.Travel))
        {
            EnterPhase(1, "walk-leg1", "walk");
        }
        else if (phase == 1 && !shot1 && phaseTime >= 1.2f)
        {
            shot1 = true;
            QueueShot("1-walk-leg1");
        }
        else if (phase == 1 && travel != null && travel.IsTerminal)
        {
            EnterPhase(2, "corner", "walk");
            QueueShot("2-corner");
        }
        else if (phase == 2 && sit != null && TraceHas(sit, ExecutionStage.Align))
        {
            EnterPhase(3, "align", "walk");
            FaceAwayFrom(chairFocus.position);
            QueueShot("3-align");
        }
        else if (phase == 3 && sit != null && sit.Stage == ExecutionStage.Perform)
        {
            EnterPhase(4, "sit-enter", "sit-enter");
            FaceAwayFrom(chairFocus.position);
        }
        else if (phase == 4 && !shot4 && phaseTime >= 0.65f)
        {
            shot4 = true;
            QueueShot("4-sit-enter");
        }
        else if (phase == 4 && phaseTime >= sitEnterClip.length)
        {
            EnterPhase(5, "sit-hold", "sit-idle");
        }
        else if (phase == 5)
        {
            long elapsed = world.GameSeconds - sit.PerformStartGameSeconds;
            if (!sitHoldShot && elapsed >= 600L)
            {
                sitHoldShot = true;
                Debug.Log("PROOF milestone=sit-hold gameSeconds=" + world.GameSeconds.ToString());
                QueueShot("5-sit-hold");
            }
            if (sit.IsTerminal)
            {
                EnterPhase(6, "stand", "sit-exit");
            }
        }
        else if (phase == 6 && !shot6 && phaseTime >= 0.5f)
        {
            shot6 = true;
            QueueShot("6-stand");
        }
        else if (phase == 6 && phaseTime >= sitExitClip.length)
        {
            EnterPhase(7, "idle", "idle");
            QueueShot("7-idle");
            finishCountdown = 8; // let the final capture flush before exit
        }
        else if (phase == 7 && !done)
        {
            if (--finishCountdown <= 0)
            {
                ResidentStatus status = executor.GetResidentStatus(hero);
                bool completed = travel.Outcome == TerminalOutcome.Completed
                    && sit.Outcome == TerminalOutcome.Completed
                    && status.IsIdle && status.IdleReason == "Completed " + SitActivity
                    && shotCount == 0;
                failed = !completed;
                Debug.Log((completed ? "PROOF_DONE" : "PROOF_FAILED") + " idleReason=" + status.IdleReason);
                done = true;
            }
        }
        if ((phase == 1 || phase == 2) && (pos - lastPos).sqrMagnitude > 0.00000001f)
        {
            Vector3 d = pos - lastPos;
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + meshForwardOffsetDeg;
        }
    }

    void EnterPhase(int next, string name, string clip)
    {
        phase = next;
        phaseTime = 0f;
        residentAnim.CrossFade(clip, 0.25f);
        Debug.Log("PROOF phase=" + name + " gameSeconds=" + world.GameSeconds.ToString());
    }

    void FaceAwayFrom(Vector3 target)
    {
        Vector3 d = residentRoot.position - target;
        if (d.sqrMagnitude > 0.00000001f)
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + meshForwardOffsetDeg;
    }

    void QueueShot(string name)
    {
        if (shotCount < shotQueue.Length)
            shotQueue[shotCount++] = name;
    }

    RenderTexture shotTarget;
    Texture2D shotTexture;

    // Fixed-resolution captures from the proof camera: independent of the
    // Game-view window size, and synchronous, so the file exists on return.
    void PumpShots()
    {
        if (shotCooldown > 0)
        {
            shotCooldown--;
            return;
        }
        if (shotCount == 0)
            return;
        string name = shotQueue[0];
        for (int i = 1; i < shotCount; i++)
            shotQueue[i - 1] = shotQueue[i];
        shotCount--;
        if (shotTarget == null)
        {
            shotTarget = new RenderTexture(1280, 720, 24);
            shotTexture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        }
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = proofCamera.targetTexture;
        proofCamera.targetTexture = shotTarget;
        proofCamera.Render();
        RenderTexture.active = shotTarget;
        shotTexture.ReadPixels(new Rect(0f, 0f, 1280f, 720f), 0, 0);
        shotTexture.Apply();
        proofCamera.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        string path = Path.Combine(shotDir, name + ".png");
        File.WriteAllBytes(path, shotTexture.EncodeToPNG());
        shotCooldown = 3;
        Debug.Log("PROOF shot=" + path);
    }
}
