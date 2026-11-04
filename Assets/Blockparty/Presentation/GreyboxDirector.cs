using System.Collections.Generic;
using System.IO;
using Blockparty.Activities;
using Blockparty.Automation;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;

// GREYBOX Play-mode driver. Owns the live eight-resident world (the
// GreyboxUnityTests scene check reads this GameStack/WorldState field),
// mirrors sim positions into the eight resident roots, and runs one
// scripted UI-flow pass: BOT food routines through RulePlayer, manual
// takeover through ResidentPanel plus a PlayerHuman Food submit, nav beats
// through WorldNavigation, title beats through TitleFlow, and a mid-routine
// save/reload through SaveV1. Shots plus command/action/effect traces land
// under evidence/production/GREYBOX. Fast Enter Play Mode: all state on the
// instance, Awake rebuilds the sim from the fixed seed on every entry.
public sealed class GreyboxDirector : MonoBehaviour, ISceneContent
{
    public const string CookCode = "ACT-FOOD-01";
    public const string FastFoodCode = "ACT-FOOD-04";
    public const long WorldId = 27L;
    public const long Seed = 42L;
    public const float TimeoutRealSeconds = 240f;

    [Header("Wired by GreyboxSceneBuild.BuildScene")]
    public Transform[] residentRoots;
    public Transform[] stations;
    public Camera greyboxCamera;

    [Header("Run state (read-only at runtime)")]
    public bool done;
    public bool failed;

    GameStack game;
    WorldState world;
    AppearanceStore appearances;
    Clock clock;
    CommandArbitrator commands;
    ActivityExecutor executor;
    Accounts accounts;
    Food food;
    RulePlayer bot;
    ResidentPanel panel;
    WorldNavigation nav;
    ResidentId[] residents;
    ObjectId[] stationIds;
    ActionInstanceId[] actions;
    long ticked;
    float wallTime;
    float phaseTime;
    float autoShotTime;
    int autoShots;
    int phase; // 0 boot, 1 bot-drive, 2 takeover, 3 save, 4 finish, 5 nav+title
    bool phaseBeatDone;
    string shotDir;
    string logDir;
    string liveSaveDir;
    List<string> lines = new List<string>();
    string[] shotQueue = new string[32];
    int shotCount;
    int shotCooldown;
    int finishCountdown;

    static readonly StateWriter Core = StateWriter.SimCore;
    static readonly StateWriter Exec = StateWriter.ActivityExecutor;
    static int playEntryCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetPlayState() { playEntryCount = 0; }

    static string[] Names()
    {
        return new string[] { "Alex", "Alex", "", "  ", "Eli", "Fran", "Gio", "Harper" };
    }

    void Awake()
    {
        try { InitializeGreybox(); }
        catch (IOException e) { Debug.LogError("GREYBOX startup-io " + e.Message); failed = true; done = true; }
        catch (System.UnauthorizedAccessException e) { Debug.LogError("GREYBOX startup-io " + e.Message); failed = true; done = true; }
    }

    void InitializeGreybox()
    {
        playEntryCount++;
        done = false;
        failed = false;
        if (residentRoots == null || residentRoots.Length != 8
            || stations == null || stations.Length != 8 || greyboxCamera == null)
        {
            failed = done = true;
            Debug.LogError("GREYBOX wiring-missing roots=" + (residentRoots == null ? -1 : residentRoots.Length));
            return;
        }
        world = ResidentFactory.CreateNewGame(WorldId, Seed, Names(), out appearances);
        clock = new Clock(world);
        commands = new CommandArbitrator(world, clock);
        executor = new ActivityExecutor(world, commands, clock);
        accounts = new Accounts(world);
        food = new Food(world, commands, executor, accounts);
        bot = new RulePlayer(commands);
        ResidentRecord[] rows = world.ResidentsSnapshot();
        System.Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        residents = new ResidentId[rows.Length];
        HomeRecord second = world.AllocateHome(Core, 8);
        HouseholdRecord household = world.AllocateHousehold(Core, "Second household", second.Id);
        stationIds = new ObjectId[8];
        actions = new ActionInstanceId[8];
        ObjectId station;
        do { station = world.AllocateObject(Core); } while ((long)station <= 8L);
        for (int i = 0; i < rows.Length; i++)
        {
            residents[i] = rows[i].Id;
            if (i >= 4) world.SetHousehold(Core, rows[i].Id, household.Id, second.Id);
            world.SetMoney(Core, rows[i].Id, 50L);
            for (int n = 0; n < StateSchema.NeedCount; n++)
                world.SetNeed(Core, rows[i].Id, (Need)n, 40f);
            Vector3 start = residentRoots[i].position;
            executor.SetResidentPosition(rows[i].Id, new ApproachPose(start.x, 0f, start.z));
            stationIds[i] = i == 0 ? station : world.AllocateObject(Core);
        }
        RegisterObjects(executor);
        foreach (HomeRecord home in world.ExportSnapshot().Homes)
            food.SetHomeStock(Core, home.Id, 8, 0);
        foreach (HouseholdRecord h in world.ExportSnapshot().Households)
            accounts.SetSharedBalance(Core, h.Id, 77L);
        game = new GameStack(world, clock, commands, executor, appearances, accounts, food);
        panel = new ResidentPanel(world, commands);
        nav = new WorldNavigation(world, commands, executor);
        ticked = 0L;
        wallTime = 0f;
        phaseTime = 0f;
        autoShotTime = 0f;
        autoShots = 0;
        phase = 0;
        phaseBeatDone = false;
        shotCount = 0;
        shotCooldown = 0;
        string root = Application.persistentDataPath;
        string env = System.Environment.GetEnvironmentVariable("GREYBOX_SHOT_DIR");
        shotDir = string.IsNullOrEmpty(env) ? Path.Combine(root, "evidence/production/GREYBOX/shots") : env;
        env = System.Environment.GetEnvironmentVariable("GREYBOX_LOG_DIR");
        logDir = string.IsNullOrEmpty(env) ? Path.Combine(root, "evidence/production/GREYBOX") : env;
        liveSaveDir = Path.Combine(logDir, "saves-live");
        Directory.CreateDirectory(shotDir);
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(liveSaveDir);
        Log("boot entry=" + playEntryCount + " unity=" + Application.unityVersion
            + " hash=" + world.ComputeStateHash());
        commands.EnableAutomation();
        for (int i = 0; i < 8; i++)
        {
            RuleDecision decision = bot.Decide(Observe(i, CookCode));
            if (decision == null) { Fail("bot-no-decision resident=" + i); return; }
            CommandResult result = bot.Apply(decision);
            if (!result.Accepted) { Fail("bot-submit-refused resident=" + i + " reason=" + result.Reason); return; }
            ActionEntry current = commands.GetCurrent(residents[i]);
            if (current == null) current = commands.GetPending(residents[i])[0];
            actions[i] = current.Action;
        }
        Log("bot-submitted cook x8 automation=" + commands.AutomationEnabled);
        QueueShot("0-boot");
        FlushLog();
        phase = 1;
        phaseTime = 0f;
    }

    public void RegisterObjects(ActivityExecutor target)
    {
        for (int i = 0; i < stationIds.Length; i++)
        {
            Vector3 p = stations[i].position;
            target.RegisterObject(stationIds[i], new ApproachPose[] { new ApproachPose(p.x, 0f, p.z) });
        }
    }

    PlayerObservation Observe(int i, string code)
    {
        ResidentRecord record;
        if (!world.TryGetResident(residents[i], out record))
            throw new System.Exception("GREYBOX resident lost index=" + i);
        float[] needs = new float[StateSchema.NeedCount];
        for (int n = 0; n < needs.Length; n++) needs[n] = record.GetNeed((Need)n);
        return new PlayerObservation(record.Id, commands.CurrentStamp(record.Id), world.GameSeconds,
            System.Array.AsReadOnly(needs), System.Array.AsReadOnly(new AvailableAction[] {
                new AvailableAction(new ActivityId(code), (long)stationIds[i]) }));
    }

    PlayerObservation[] ObserveAll(string code)
    {
        PlayerObservation[] observations = new PlayerObservation[8];
        for (int i = 0; i < 8; i++) observations[i] = Observe(i, code);
        return observations;
    }

    static int FoodEvents(WorldState target, ResidentId resident)
    {
        int count = 0;
        foreach (WorldEvent e in target.Events)
            if (e.Kind == "FoodCompleted" && e.Actor.HasValue && e.Actor.Value == resident) count++;
        return count;
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
        autoShotTime += dt;
        clock.AdvanceRealSeconds(Core, dt, delegate { food.Tick(Exec); ticked++; });
        bot.Tick(ObserveAll(CookCode));
        nav.Refresh();
        for (int i = 0; i < 8; i++)
        {
            ApproachPose pose = executor.GetResidentPosition(residents[i]);
            residentRoots[i].position = new Vector3(pose.X, 0f, pose.Z);
        }
        if (autoShotTime >= 5f && autoShots < 24)
        {
            autoShotTime = 0f;
            QueueShot("auto-" + (autoShots < 10 ? "0" : "") + autoShots);
            autoShots++;
        }
        if (phase == 1 && AllAt(ExecutionStage.Perform))
        {
            Log("bot-all-perform gameSeconds=" + world.GameSeconds + " commands=" + commands.CommandLog.Count);
            QueueShot("1-bot-perform");
            DumpCommandLog("commands-bot.log");
            FlushLog();
            phase = 2;
            phaseTime = 0f;
            phaseBeatDone = false;
        }
        else if (phase == 2 && !phaseBeatDone)
        {
            phaseBeatDone = true;
            TakeoverBeat();
        }
        else if (phase == 3 && !phaseBeatDone)
        {
            ExecutionRecord fast = executor.GetExecution(actions[0]);
            if (fast != null && (fast.Stage == ExecutionStage.Perform || fast.IsTerminal))
            {
                phaseBeatDone = true;
                SaveBeat();
            }
        }
        else if (phase == 4 && AllTerminal())
        {
            FinishBeat();
        }
        else if (phase == 5 && phaseTime >= 8f && !phaseBeatDone)
        {
            phaseBeatDone = true;
            NavTitleBeat();
        }
        else if (phase == 6 && shotCount == 0 && --finishCountdown <= 0)
        {
            Log("done failed=" + failed + " wall=" + wallTime);
            FlushLog();
            done = true;
        }
        try { PumpShots(); }
        catch (System.Exception e)
        {
            Fail("capture-failed " + e.GetType().Name);
            return;
        }
        if (wallTime > TimeoutRealSeconds && !done)
            Fail("timeout phase=" + phase);
    }

    bool AllAt(ExecutionStage stage)
    {
        for (int i = 0; i < 8; i++)
        {
            ExecutionRecord e = executor.GetExecution(actions[i]);
            if (e == null || e.IsTerminal || e.Stage != stage) return false;
        }
        return true;
    }

    bool AllTerminal()
    {
        for (int i = 0; i < 8; i++)
        {
            ExecutionRecord e = executor.GetExecution(actions[i]);
            if (e == null || !e.IsTerminal) return false;
        }
        return true;
    }

    void TakeoverBeat()
    {
        for (int i = 0; i < 8; i++)
        {
            ResidentPanelSnapshot snapshot;
            string reason;
            if (!panel.TryInspect(residents[i], out snapshot, out reason))
            { Fail("panel-inspect-refused resident=" + i + " " + reason); return; }
            Log("inspect resident=" + ((long)residents[i]) + " name=" + snapshot.Resident.DisplayName
                + " hunger=" + snapshot.Resident.GetNeed(Need.Hunger)
                + " current=" + (snapshot.Current == null ? 0L : (long)snapshot.Current.Action));
        }
        ActionInstanceId interrupted = actions[0];
        CommandResult cancelled = panel.Cancel(residents[0], interrupted);
        if (!cancelled.Accepted) { Fail("panel-cancel-refused reason=" + cancelled.Reason); return; }
        CommandResult manual = food.Submit(new CommandEnvelope(
            world.AllocateCommand(Core), world.Epoch, world.GameSeconds,
            CommandSource.PlayerHuman, commands.CurrentStamp(residents[0]).Sequence + 1u,
            CommandKind.DirectActivity, residents[0], new ActivityId(FastFoodCode),
            (long)stationIds[0], new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate)));
        if (!manual.Accepted) { Fail("manual-fastfood-refused reason=" + manual.Reason); return; }
        ActionEntry manualHead = commands.GetCurrent(residents[0]);
        if (manualHead == null)
        {
            ActionEntry[] pend = commands.GetPending(residents[0]);
            if (pend.Length == 0) { Fail("manual-action-nowhere"); return; }
            manualHead = pend[pend.Length - 1];
        }
        actions[0] = manualHead.Action;
        ExecutionRecord old = executor.GetExecution(interrupted);
        Log("takeover automation=" + commands.AutomationEnabled
            + " interrupted=" + (old == null ? "null" : old.Outcome.ToString())
            + " manual=" + ((long)actions[0]));
        if (commands.AutomationEnabled) { Fail("takeover-left-automation-on"); return; }
        QueueShot("2-takeover");
        FlushLog();
        phase = 3;
        phaseTime = 0f;
        phaseBeatDone = false;
    }

    void SaveBeat()
    {
        int before = world.ComputeStateHash();
        SaveWriteResult written = SaveV1.SaveGame(game, liveSaveDir);
        if (!written.Ok) { Fail("save-failed " + written.Detail); return; }
        File.Copy(Path.Combine(liveSaveDir, SaveFormat.FileName), Path.Combine(logDir, "save-live.json"), true);
        SaveLoadResult loaded = SaveV1.LoadGame(liveSaveDir, this);
        if (!loaded.Ok) { Fail("reload-failed " + loaded.Notice); return; }
        LoadedGame g = loaded.Game;
        world = g.World;
        appearances = g.Appearances;
        clock = g.Clock;
        commands = g.Arbitrator;
        executor = g.Executor;
        accounts = g.Accounts;
        food = g.Food;
        bot = new RulePlayer(commands);
        panel = new ResidentPanel(world, commands);
        nav = new WorldNavigation(world, commands, executor);
        game = new GameStack(world, clock, commands, executor, appearances, accounts, food);
        ticked = clock.TotalTicks;
        for (int i = 0; i < 8; i++)
        {
            ActionEntry current = commands.GetCurrent(residents[i]);
            if (current == null) { Fail("reload-lost-action resident=" + i); return; }
            actions[i] = current.Action;
        }
        Log("save-reload before=" + before + " after=" + world.ComputeStateHash()
            + " note0=" + (g.Notes.Length == 0 ? "none" : g.Notes[0]));
        for (int i = 1; i < g.Notes.Length; i++) Log("reload-note " + g.Notes[i]);
        Log("reload-restored services accounts+food");
        QueueShot("3-reloaded");
        FlushLog();
        phase = 4;
        phaseTime = 0f;
    }

    void FinishBeat()
    {
        for (int i = 0; i < 8; i++)
        {
            ResidentRecord record;
            world.TryGetResident(residents[i], out record);
            ExecutionRecord e = executor.GetExecution(actions[i]);
            Log("complete resident=" + ((long)residents[i]) + " outcome=" + e.Outcome
                + " money=" + record.Money + " hunger=" + record.GetNeed(Need.Hunger)
                + " foodEvents=" + FoodEvents(world, residents[i]));
        }
        foreach (HomeRecord home in world.ExportSnapshot().Homes)
            Log("stock home=" + ((long)home.Id) + " groceries=" + food.GroceryUnits(home.Id)
                + " meals=" + food.PreparedMeals(home.Id));
        QueueShot("4-complete");
        DumpCommandLog("commands-final.log");
        DumpTraces();
        FlushLog();
        nav.OpenMap();
        IReadOnlyList<NavigationResident> rows = nav.SearchByName("Alex");
        Log("nav-search Alex rows=" + rows.Count);
        nav.SelectResident(residents[0]);
        nav.FollowResident(residents[0]);
        CommandResult travel = nav.TravelTo(residents[0], (long)stationIds[1], Blockparty.Contracts.QueueMode.Immediate, TravelTargetKind.Object);
        if (!travel.Accepted) { Fail("nav-travel-refused reason=" + travel.Reason); return; }
        ActionEntry head = commands.GetCurrent(residents[0]);
        actions[0] = head == null ? actions[0] : head.Action;
        Log("nav-travel submitted target=station1");
        phase = 5;
        phaseTime = 0f;
        phaseBeatDone = false;
    }

    void NavTitleBeat()
    {
        ApproachPose pose = executor.GetResidentPosition(residents[0]);
        Vector3 target = stations[1].position;
        float gap = Vector2.Distance(new Vector2(pose.X, pose.Z), new Vector2(target.x, target.z));
        Log("nav-arrival gap=" + gap + " follow=" + nav.FollowedResident.HasValue);
        nav.StopFollowing();
        nav.CloseMap();
        QueueShot("5-nav-travel");
        string titleDir = Path.Combine(logDir, "saves-titleflow");
        Directory.CreateDirectory(titleDir);
        TitleFlow flow = new TitleFlow(titleDir, this);
        TitleBeat(flow, TitleAction.Settings);
        TitleBeat(flow, TitleAction.Back);
        TitleBeat(flow, TitleAction.Credits);
        TitleBeat(flow, TitleAction.Back);
        TitleBeat(flow, TitleAction.NewGame);
        TitleFlowResult prep = flow.PrepareNewGame(WorldId, Seed, Names());
        Log("title PrepareNewGame ok=" + prep.Ok + " " + prep.Reason);
        TitleFlowResult begin = flow.BeginPlay();
        Log("title BeginPlay ok=" + begin.Ok + " " + begin.Reason);
        TitleBeat(flow, TitleAction.Pause);
        TitleBeat(flow, TitleAction.Save);
        TitleBeat(flow, TitleAction.Resume);
        TitleBeat(flow, TitleAction.Pause);
        TitleBeat(flow, TitleAction.ReturnToTitle);
        Log("title end screen=" + flow.Screen);
        QueueShot("6-title-flow");
        FlushLog();
        phase = 6;
        finishCountdown = 8;
    }

    void TitleBeat(TitleFlow flow, TitleAction action)
    {
        TitleFlowResult result = flow.Activate(action);
        Log("title " + action + " ok=" + result.Ok + " screen=" + flow.Screen + " " + result.Reason);
    }

    void Fail(string message)
    {
        Log("FAIL " + message);
        FlushLog();
        failed = true;
        done = true;
        Debug.LogError("GREYBOX FAIL " + message);
    }

    void Log(string message)
    {
        lines.Add(message);
        Debug.Log("GREYBOX " + message);
    }

    void FlushLog()
    {
        File.AppendAllText(Path.Combine(logDir, "greybox-run.log"), string.Join("\n", lines.ToArray()) + "\n");
        lines.Clear();
    }

    void DumpCommandLog(string file)
    {
        List<string> rows = new List<string>();
        foreach (CommandLogEntry entry in commands.CommandLog)
            rows.Add(((long)entry.Command) + " " + entry.Kind
                + " resident=" + (entry.Resident.HasValue ? ((long)entry.Resident.Value).ToString() : "-")
                + " accepted=" + entry.Accepted + " reason=" + entry.Reason);
        File.WriteAllText(Path.Combine(logDir, file), string.Join("\n", rows.ToArray()) + "\n");
        Log("dump " + file + " rows=" + rows.Count);
    }

    void DumpTraces()
    {
        List<string> rows = new List<string>();
        foreach (SlotEvent slot in executor.ReservationLog)
            rows.Add("slot t=" + slot.GameSeconds + " action=" + ((long)slot.Hold.Action)
                + " acquired=" + slot.Acquired);
        foreach (EffectLedgerEntry entry in world.EffectLedger)
            rows.Add("ledger action=" + ((long)entry.Action) + " state=" + entry.State);
        foreach (WorldEvent e in world.Events)
            rows.Add("event t=" + e.GameSeconds + " kind=" + e.Kind
                + " actor=" + (e.Actor.HasValue ? ((long)e.Actor.Value).ToString() : "-"));
        File.WriteAllText(Path.Combine(logDir, "traces-final.log"), string.Join("\n", rows.ToArray()) + "\n");
        Log("dump traces-final.log rows=" + rows.Count);
    }

    void QueueShot(string name)
    {
        if (shotCount < shotQueue.Length)
            shotQueue[shotCount++] = name;
    }

    RenderTexture shotTarget;
    Texture2D shotTexture;

    // Fixed-resolution captures from the greybox camera: independent of the
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
        RenderTexture previousTarget = greyboxCamera.targetTexture;
        greyboxCamera.targetTexture = shotTarget;
        greyboxCamera.Render();
        RenderTexture.active = shotTarget;
        shotTexture.ReadPixels(new Rect(0f, 0f, 1280f, 720f), 0, 0);
        shotTexture.Apply();
        greyboxCamera.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        string path = Path.Combine(shotDir, name + ".png");
        File.WriteAllBytes(path, shotTexture.EncodeToPNG());
        shotCooldown = 3;
        Debug.Log("GREYBOX shot=" + path);
    }
}
