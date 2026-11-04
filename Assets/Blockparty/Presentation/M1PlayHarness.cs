using System.Collections.Generic;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.Automation;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

// GATE-M1 standalone play harness. Runs only when the player is launched
// with --m1-mode=command|ui (plus --m1-outdir, --m1-tree, --m1-binary-sha).
// Without those args the normal scene directors run untouched.
// Input method: player's own UI event injection (no xdotool on i7).
// Each UI flow drives its real controller (TitleFlow/ResidentPanel/
// WorldNavigation/Food) then captures the greybox camera; the input log
// records the injected mouse/keyboard event per flow.
public sealed class M1PlayHarness : MonoBehaviour, ISceneContent
{
    public const string CookCode = "ACT-FOOD-01";
    public const string FastFoodCode = "ACT-FOOD-04";
    public const long WorldId = 27L;
    public const long Seed = 42L;

    static readonly StateWriter Core = StateWriter.SimCore;
    static readonly StateWriter Exec = StateWriter.ActivityExecutor;

    string mode;
    string outDir;
    string tree;
    string binarySha;
    string framePrefix;
    bool done;
    int exitCode;
    string failReason;

    Transform[] roots;
    Transform[] stationTransforms;
    Camera cam;
    ObjectId[] stationIds;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        bool m1 = false;
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--m1-mode=")) m1 = true;
        if (!m1) return;
        GameObject go = new GameObject("M1PlayHarness");
        go.AddComponent<M1PlayHarness>();
        Debug.Log("M1 harness boot");
    }

    public void RegisterObjects(ActivityExecutor target)
    {
        for (int i = 0; i < stationIds.Length; i++)
        {
            Vector3 p = stationTransforms[i].position;
            target.RegisterObject(stationIds[i], new ApproachPose[] { new ApproachPose(p.x, 0f, p.z) });
        }
    }

    sealed class NullScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor target) { }
    }

    static string Arg(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith(key + "=")) return args[i].Substring(key.Length + 1);
        return string.Empty;
    }

    static string[] Names()
    {
        return new string[] { "Alex", "Alex", "", "  ", "Eli", "Fran", "Gio", "Harper" };
    }

    void Start()
    {
        try { StartPlayer(); }
        catch (IOException e) { Fail("startup-io " + e.Message); }
        catch (System.UnauthorizedAccessException e) { Fail("startup-io " + e.Message); }
    }

    void StartPlayer()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        mode = Arg(args, "--m1-mode");
        outDir = Arg(args, "--m1-outdir");
        tree = Arg(args, "--m1-tree");
        binarySha = Arg(args, "--m1-binary-sha");
        framePrefix = Arg(args, "--m1-frame-prefix");
        if ((mode != "command" && mode != "ui") || outDir.Length == 0
            || tree.Length == 0 || binarySha.Length == 0)
        {
            Fail("bad-args mode=" + mode);
            return;
        }
        GameObject world = GameObject.Find("GreyboxWorld");
        GreyboxDirector grey = world == null ? null : world.GetComponent<GreyboxDirector>();
        if (grey == null || grey.residentRoots == null || grey.residentRoots.Length != 8
            || grey.stations == null || grey.stations.Length != 8 || grey.greyboxCamera == null)
        {
            Fail("m1-requires-EightResident-scene");
            return;
        }
        roots = grey.residentRoots;
        stationTransforms = grey.stations;
        cam = grey.greyboxCamera;
        grey.enabled = false;
        Directory.CreateDirectory(outDir);
        Debug.Log("M1 start mode=" + mode + " out=" + outDir);
        if (mode == "command") { RunCommand(); done = true; }
        else RunUi();
    }

    void Update()
    {
        if (!done) { if (mode == "ui") UpdateUi(); return; }
        if (Application.isEditor) { enabled = false; return; }
        Application.Quit(exitCode);
    }

    void Fail(string message)
    {
        failReason = message;
        exitCode = 1;
        done = true;
        Debug.LogError("M1 FAIL " + message);
    }

    sealed class Sim
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public AppearanceStore Appearances;
        public Accounts Accounts;
        public Food Food;
        public RulePlayer Bot;
        public ResidentPanel Panel;
        public WorldNavigation Nav;
        public ResidentId[] Residents;
        public ActionInstanceId[] Actions;
    }

    Sim NewSim()
    {
        Sim s = new Sim();
        s.World = ResidentFactory.CreateNewGame(WorldId, Seed, Names(), out s.Appearances);
        s.Clock = new Clock(s.World);
        s.Commands = new CommandArbitrator(s.World, s.Clock);
        s.Executor = new ActivityExecutor(s.World, s.Commands, s.Clock);
        s.Accounts = new Accounts(s.World);
        s.Food = new Food(s.World, s.Commands, s.Executor, s.Accounts);
        s.Bot = new RulePlayer(s.Commands);
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        System.Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        s.Residents = new ResidentId[rows.Length];
        s.Actions = new ActionInstanceId[rows.Length];
        HomeRecord second = s.World.AllocateHome(Core, 8);
        HouseholdRecord household = s.World.AllocateHousehold(Core, "Second household", second.Id);
        stationIds = new ObjectId[8];
        ObjectId station;
        do { station = s.World.AllocateObject(Core); } while ((long)station <= 8L);
        for (int i = 0; i < rows.Length; i++)
        {
            s.Residents[i] = rows[i].Id;
            if (i >= 4) s.World.SetHousehold(Core, rows[i].Id, household.Id, second.Id);
            s.World.SetMoney(Core, rows[i].Id, 50L);
            for (int n = 0; n < StateSchema.NeedCount; n++)
                s.World.SetNeed(Core, rows[i].Id, (Need)n, 40f);
            Vector3 start = roots[i].position;
            s.Executor.SetResidentPosition(rows[i].Id, new ApproachPose(start.x, 0f, start.z));
            stationIds[i] = i == 0 ? station : s.World.AllocateObject(Core);
        }
        RegisterObjects(s.Executor);
        foreach (HomeRecord home in s.World.ExportSnapshot().Homes)
            s.Food.SetHomeStock(Core, home.Id, 8, 0);
        foreach (HouseholdRecord h in s.World.ExportSnapshot().Households)
            s.Accounts.SetSharedBalance(Core, h.Id, 77L);
        s.Panel = new ResidentPanel(s.World, s.Commands);
        s.Nav = new WorldNavigation(s.World, s.Commands, s.Executor);
        return s;
    }

    PlayerObservation Observe(Sim s, int i, string code)
    {
        ResidentRecord record;
        s.World.TryGetResident(s.Residents[i], out record);
        float[] needs = new float[StateSchema.NeedCount];
        for (int n = 0; n < needs.Length; n++) needs[n] = record.GetNeed((Need)n);
        return new PlayerObservation(record.Id, s.Commands.CurrentStamp(record.Id), s.World.GameSeconds,
            System.Array.AsReadOnly(needs), System.Array.AsReadOnly(new AvailableAction[] {
                new AvailableAction(new ActivityId(code), (long)stationIds[i]) }));
    }

    CommandEnvelope Direct(Sim s, int i, string code)
    {
        return new CommandEnvelope(s.World.AllocateCommand(Core), s.World.Epoch,
            s.World.GameSeconds, CommandSource.PlayerHuman,
            s.Commands.CurrentStamp(s.Residents[i]).Sequence + 1u,
            CommandKind.DirectActivity, s.Residents[i], new ActivityId(code),
            (long)stationIds[i], new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate));
    }

    static void Step(Sim s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            s.Clock.AdvanceRealSeconds(Core, 0.1);
            s.Food.Tick(Exec);
        }
    }

    bool AllTerminal(Sim s)
    {
        for (int i = 0; i < 8; i++)
        {
            ExecutionRecord e = s.Executor.GetExecution(s.Actions[i]);
            if (e == null || !e.IsTerminal) return false;
        }
        return true;
    }

    void Mirror(Sim s)
    {
        for (int i = 0; i < 8; i++)
        {
            ApproachPose pose = s.Executor.GetResidentPosition(s.Residents[i]);
            roots[i].position = new Vector3(pose.X, 0f, pose.Z);
        }
    }

    void Capture(string path)
    {
        RenderTexture target = new RenderTexture(1280, 720, 24);
        Texture2D texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = cam.targetTexture;
        cam.targetTexture = target;
        cam.Render();
        RenderTexture.active = target;
        texture.ReadPixels(new Rect(0f, 0f, 1280f, 720f), 0, 0);
        texture.Apply();
        cam.targetTexture = previousTarget;
        RenderTexture.active = previousActive;
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.Destroy(target);
        Object.Destroy(texture);
        Debug.Log("M1 shot=" + path);
    }

    static string JsonEscape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    // Normalized logical export: money, needs, cooking, stocks, shared
    // balances, ledger amounts. Excludes epoch, action ids, sequences,
    // generations and clocks (SAVE remaps those on reload).
    static string LogicalState(Sim s)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"residents\":[");
        ResidentRecord[] rows = s.World.ResidentsSnapshot();
        System.Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(((long)rows[i].Id).ToString());
            sb.Append(",\"money\":").Append(rows[i].Money.ToString());
            sb.Append(",\"hunger\":").Append(rows[i].GetNeed(Need.Hunger).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"health\":").Append(rows[i].GetNeed(Need.Health).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"fun\":").Append(rows[i].GetNeed(Need.Fun).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            int cooking = 0;
            foreach (SkillEntry skill in rows[i].Skills)
                if (skill.SkillId == "Cooking") cooking = skill.Points;
            sb.Append(",\"cooking\":").Append(cooking.ToString());
            sb.Append('}');
        }
        sb.Append("],\"stocks\":[");
        List<HomeRecord> homes = s.World.ExportSnapshot().Homes;
        homes.Sort((a, b) => ((long)a.Id).CompareTo((long)b.Id));
        for (int i = 0; i < homes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"home\":").Append(((long)homes[i].Id).ToString());
            sb.Append(",\"groceries\":").Append(s.Food.GroceryUnits(homes[i].Id).ToString());
            sb.Append(",\"meals\":").Append(s.Food.PreparedMeals(homes[i].Id).ToString());
            sb.Append('}');
        }
        sb.Append("],\"shared\":[");
        List<HouseholdRecord> households = s.World.ExportSnapshot().Households;
        households.Sort((a, b) => ((long)a.Id).CompareTo((long)b.Id));
        for (int i = 0; i < households.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"household\":").Append(((long)households[i].Id).ToString());
            sb.Append(",\"balance\":").Append(s.Accounts.Balance(AccountRef.Shared(households[i].Id)).ToString());
            sb.Append('}');
        }
        sb.Append("],\"ledger\":[");
        List<long> amounts = new List<long>();
        foreach (AccountTransaction tx in s.Accounts.Ledger) amounts.Add(tx.Amount);
        amounts.Sort();
        for (int i = 0; i < amounts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(amounts[i].ToString());
        }
        sb.Append("]}");
        return sb.ToString();
    }

    void RunCommand()
    {
        Sim s = NewSim();
        long[] moneyBefore = new long[8];
        float[] hungerBefore = new float[8];
        for (int i = 0; i < 8; i++)
        {
            ResidentRecord r;
            s.World.TryGetResident(s.Residents[i], out r);
            moneyBefore[i] = r.Money;
            hungerBefore[i] = r.GetNeed(Need.Hunger);
        }
        s.Commands.EnableAutomation();
        for (int i = 0; i < 4; i++)
        {
            RuleDecision decision = s.Bot.Decide(Observe(s, i, CookCode));
            if (decision == null) { Fail("bot-no-decision " + i); return; }
            CommandResult result = s.Bot.Apply(decision);
            if (!result.Accepted) { Fail("bot-refused " + i + " " + result.Reason); return; }
            ActionEntry current = s.Commands.GetCurrent(s.Residents[i]);
            if (current == null) current = s.Commands.GetPending(s.Residents[i])[0];
            s.Actions[i] = current.Action;
        }
        for (int i = 4; i < 8; i++)
        {
            CommandResult result = s.Food.Submit(Direct(s, i, FastFoodCode));
            if (!result.Accepted) { Fail("manual-refused " + i + " " + result.Reason); return; }
            ActionEntry current = s.Commands.GetCurrent(s.Residents[i]);
            if (current == null) current = s.Commands.GetPending(s.Residents[i])[0];
            s.Actions[i] = current.Action;
        }
        for (int tick = 0; tick < 900 && !AllTerminal(s); tick++) Step(s, 1);
        if (!AllTerminal(s)) { Fail("command-incomplete"); return; }
        for (int i = 0; i < 8; i++)
        {
            ExecutionRecord e = s.Executor.GetExecution(s.Actions[i]);
            if (e.Outcome != TerminalOutcome.Completed) { Fail("not-completed " + i + " " + e.Outcome); return; }
        }
        Mirror(s);
        StringBuilder bot = new StringBuilder();
        bot.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"binary_sha256\":\"").Append(binarySha);
        bot.Append("\",\"source\":\"BOT\",\"completions\":[");
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) bot.Append(',');
            ResidentRecord r;
            s.World.TryGetResident(s.Residents[i], out r);
            bot.Append("{\"resident_id\":\"").Append(((long)s.Residents[i]).ToString());
            bot.Append("\",\"activity_id\":\"").Append(CookCode);
            bot.Append("\",\"outcome\":\"Completed\",\"money_before\":").Append(moneyBefore[i].ToString());
            bot.Append(",\"money_after\":").Append(r.Money.ToString());
            bot.Append(",\"hunger_before\":").Append(hungerBefore[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            bot.Append(",\"hunger_after\":").Append(r.GetNeed(Need.Hunger).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            bot.Append('}');
        }
        bot.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "bot.json"), bot.ToString());
        StringBuilder manual = new StringBuilder();
        manual.Append("{\"tree_hash\":\"").Append(tree).Append("\",\"binary_sha256\":\"").Append(binarySha);
        manual.Append("\",\"source\":\"manual\",\"completions\":[");
        for (int i = 4; i < 8; i++)
        {
            if (i > 4) manual.Append(',');
            ResidentRecord r;
            s.World.TryGetResident(s.Residents[i], out r);
            manual.Append("{\"resident_id\":\"").Append(((long)s.Residents[i]).ToString());
            manual.Append("\",\"activity_id\":\"").Append(FastFoodCode);
            manual.Append("\",\"outcome\":\"Completed\",\"money_before\":").Append(moneyBefore[i].ToString());
            manual.Append(",\"money_after\":").Append(r.Money.ToString());
            manual.Append(",\"hunger_before\":").Append(hungerBefore[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            manual.Append(",\"hunger_after\":").Append(r.GetNeed(Need.Hunger).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            manual.Append('}');
        }
        manual.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "manual.json"), manual.ToString());
        string before = LogicalState(s);
        File.WriteAllText(Path.Combine(outDir, "before-state.json"), before);
        GameStack stack = new GameStack(s.World, s.Clock, s.Commands, s.Executor, s.Appearances, s.Accounts, s.Food);
        string saveDir = Path.Combine(outDir, "save1");
        SaveWriteResult written = SaveV1.SaveGame(stack, saveDir);
        if (!written.Ok) { Fail("save-failed " + written.Detail); return; }
        File.Copy(Path.Combine(saveDir, "save.json"), Path.Combine(outDir, "save.json"), true);
        SaveLoadResult loaded = SaveV1.LoadGame(saveDir, this);
        if (!loaded.Ok) { Fail("reload-failed " + loaded.Notice); return; }
        Sim r2 = new Sim();
        r2.World = loaded.Game.World;
        r2.Accounts = loaded.Game.Accounts;
        r2.Food = loaded.Game.Food;
        string after = LogicalState(r2);
        File.WriteAllText(Path.Combine(outDir, "after-state.json"), after);
        if (!string.Equals(before, after)) { Fail("continuity-differs"); return; }
        GameStack stack2 = new GameStack(loaded.Game.World, loaded.Game.Clock, loaded.Game.Arbitrator,
            loaded.Game.Executor, loaded.Game.Appearances, loaded.Game.Accounts, loaded.Game.Food);
        string saveDir2 = Path.Combine(outDir, "save2");
        SaveWriteResult written2 = SaveV1.SaveGame(stack2, saveDir2);
        if (!written2.Ok) { Fail("resave-failed " + written2.Detail); return; }
        File.Copy(Path.Combine(saveDir2, "save.json"), Path.Combine(outDir, "reloaded-save.json"), true);
        StringBuilder traces = new StringBuilder();
        foreach (CommandLogEntry entry in s.Commands.CommandLog)
        {
            traces.Append(((long)entry.Command).ToString()).Append(' ').Append(entry.Kind.ToString());
            traces.Append(" resident=").Append(entry.Resident.HasValue ? ((long)entry.Resident.Value).ToString() : "-");
            traces.Append(" accepted=").Append(entry.Accepted.ToString()).Append(' ').Append(entry.Reason.ToString()).Append('\n');
        }
        foreach (WorldEvent e in s.World.Events)
        {
            traces.Append("event t=").Append(e.GameSeconds.ToString()).Append(' ').Append(e.Kind);
            traces.Append(' ').Append(JsonEscape(e.Detail)).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "command-traces.log"), traces.ToString());
        Debug.Log("M1 command done");
    }

    TitleFlow uiFlow;
    Sim ui;
    int uiStep;
    string uiView = "Title";
    string uiNotice = "Welcome to Block Party";
    bool capturing;
    bool takeoverWaiting;
    ActionInstanceId takeoverAction;
    readonly List<string> uiEvents = new List<string>();

    void RunUi()
    {
        stationIds = new ObjectId[8];
        for (int i = 0; i < 8; i++) stationIds[i] = (ObjectId)(9L + i);
        uiFlow = new TitleFlow(Path.Combine(outDir, "saves-ui"), this);
        WriteUiReady();
    }

    void BindUiGame(bool initialize)
    {
        GameStack g = uiFlow.Game;
        ui = new Sim { World = g.World, Clock = g.Clock, Commands = g.Arbitrator,
            Executor = g.Executor, Appearances = g.Appearances, Accounts = g.Accounts, Food = g.Food };
        ResidentRecord[] rows = g.World.ResidentsSnapshot();
        System.Array.Sort(rows, (a,b) => ((long)a.Id).CompareTo((long)b.Id));
        ui.Residents = new ResidentId[rows.Length];
        ui.Actions = new ActionInstanceId[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            ui.Residents[i] = rows[i].Id;
            if (!initialize) continue;
            g.World.SetMoney(Core, rows[i].Id, 50L);
            for (int n = 0; n < StateSchema.NeedCount; n++) g.World.SetNeed(Core, rows[i].Id, (Need)n, 40f);
            Vector3 p = roots[i].position;
            g.Executor.SetResidentPosition(rows[i].Id, new ApproachPose(p.x, 0f, p.z));
        }
        if (initialize)
            foreach (HomeRecord home in g.World.ExportSnapshot().Homes) g.Food.SetHomeStock(Core, home.Id, 8, 0);
        ui.Panel = new ResidentPanel(ui.World, ui.Commands);
        ui.Nav = new WorldNavigation(ui.World, ui.Commands, ui.Executor);
        ui.Bot = new RulePlayer(ui.Commands);
        Mirror(ui);
    }

    void WriteUiReady()
    {
        File.WriteAllText(Path.Combine(outDir, "ui-ready.json"),
            "{\"step\":" + uiStep + ",\"view\":\"" + uiView + "\",\"capturing\":" + (capturing ? "true" : "false") + "}");
    }

    void RequireUi(TitleFlowResult result)
    {
        if (!result.Ok) throw new System.InvalidOperationException(result.Reason);
    }

    void OnGUI()
    {
        if (mode != "ui" || uiFlow == null || done) return;
        GUI.skin.label.fontSize = 20;
        GUI.skin.button.fontSize = 18;
        GUI.Box(new Rect(8, 8, 420, 700), "Block Party — " + uiView);
        GUI.Label(new Rect(24, 38, 380, 50), uiNotice);
        string[] labels = { "Show title", "New game", "Begin play", "Save / open Load", "Load saved neighborhood",
            "Inspect Alex", "Open map", "Follow Alex", "Take over / Fast food" };
        GUI.enabled = !capturing && !takeoverWaiting;
        for (int i = 0; i < labels.Length; i++)
        {
            GUI.enabled = !capturing && !takeoverWaiting && uiStep == i;
            if (GUI.Button(new Rect(24, 100 + i * 48, 380, 42), labels[i])) UiClick(i);
        }
        GUI.enabled = true;
        if (ui == null) return;
        GUI.Label(new Rect(450, 20, 790, 40), "Live neighborhood: " + ui.World.GameSeconds + " game seconds | " + uiFlow.Screen);
        if (uiView == "Inspect")
        {
            ResidentPanelSnapshot snapshot; string reason;
            if (ui.Panel.TryInspect(ui.Residents[0], out snapshot, out reason))
            {
                ResidentRecord r; ui.World.TryGetResident(ui.Residents[0], out r);
                GUI.Box(new Rect(450, 80, 600, 200), "Resident inspection");
                GUI.Label(new Rect(470, 120, 560, 100), r.DisplayName + "  ID " + (long)r.Id +
                    "\nMoney: " + r.Money + "\nHunger: " + r.GetNeed(Need.Hunger).ToString("F2"));
            }
        }
        else if (uiView == "Map")
        {
            GUI.Box(new Rect(450, 80, 600, 280), "Neighborhood map — search Alex");
            int y = 120;
            foreach (NavigationResident row in ui.Nav.SearchByName("Alex"))
            {
                GUI.Label(new Rect(470, y, 560, 40), row.DisplayName + " / " + (long)row.Id + " / home " + (long)row.Home +
                    " / x=" + row.Position.X.ToString("F1")); y += 45;
            }
        }
        else if (uiView == "Load") GUI.Box(new Rect(450, 80, 600, 180), "Saved neighborhood — eight residents\nSelect Load saved neighborhood to continue");
        else if (uiView == "Creator") GUI.Box(new Rect(450, 80, 600, 180), "Create household\nAlex, Alex, randomized names, Eli, Fran, Gio, Harper");
        else if (uiView == "Takeover") GUI.Box(new Rect(450, 80, 600, 180), "Player control\nAutomation OFF — Fast food " + (takeoverWaiting ? "in progress" : "completed"));
    }

    void UiClick(int step)
    {
        string before = uiFlow.Screen.ToString();
        string flow = null;
        switch (step)
        {
            case 0: flow = "title"; break;
            case 1: RequireUi(uiFlow.Activate(TitleAction.NewGame)); uiView = "Creator"; break;
            case 2:
                RequireUi(uiFlow.PrepareNewGame(WorldId, Seed, Names())); RequireUi(uiFlow.BeginPlay());
                BindUiGame(true); uiView = "Playing"; flow = "new"; break;
            case 3:
                RequireUi(uiFlow.Activate(TitleAction.Pause)); RequireUi(uiFlow.Activate(TitleAction.Save));
                RequireUi(uiFlow.Activate(TitleAction.ReturnToTitle)); RequireUi(uiFlow.Activate(TitleAction.Load));
                uiView = "Load"; break;
            case 4: RequireUi(uiFlow.LoadSavedGame()); BindUiGame(false); uiView = "Playing"; flow = "load"; break;
            case 5: uiView = "Inspect"; flow = "inspect"; break;
            case 6: ui.Nav.OpenMap(); uiView = "Map"; flow = "map"; break;
            case 7:
                ui.Nav.SelectResident(ui.Residents[0]); ui.Nav.FollowResident(ui.Residents[0]);
                if (!ui.Nav.TravelTo(ui.Residents[0], (long)stationIds[0], Blockparty.Contracts.QueueMode.Immediate, TravelTargetKind.Object).Accepted)
                    throw new System.InvalidOperationException("Follow travel refused");
                uiView = "Follow"; flow = "follow"; break;
            case 8:
                ui.Commands.EnableAutomation();
                RuleDecision decision = ui.Bot.Decide(Observe(ui, 0, CookCode));
                if (!ui.Bot.Apply(decision).Accepted) throw new System.InvalidOperationException("BOT setup refused");
                ActionEntry pending = ui.Commands.GetPending(ui.Residents[0])[0];
                if (!ui.Panel.Cancel(ui.Residents[0], pending.Action).Accepted || !ui.Food.Submit(Direct(ui, 0, FastFoodCode)).Accepted)
                    throw new System.InvalidOperationException("Takeover refused");
                ActionEntry head = ui.Commands.GetCurrent(ui.Residents[0]);
                if (head == null) head = ui.Commands.GetPending(ui.Residents[0])[0];
                takeoverAction = head.Action; takeoverWaiting = true; uiView = "Takeover"; break;
        }
        uiNotice = "Mouse click: " + before + " → " + uiFlow.Screen;
        File.AppendAllText(Path.Combine(outDir, "transitions.log"), "mouse step=" + step + " before=" + before + " after=" + uiFlow.Screen + " view=" + uiView + "\n");
        uiStep++;
        WriteUiReady();
        if (flow != null) StartCoroutine(CaptureUi(flow, "left click button step " + step));
    }

    System.Collections.IEnumerator CaptureUi(string flow, string input)
    {
        capturing = true; WriteUiReady();
        yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
        try { File.WriteAllBytes(Path.Combine(outDir, flow + ".png"), frame.EncodeToPNG()); }
        finally { Object.Destroy(frame); }
        uiEvents.Add("{\"flow\":\"" + flow + "\",\"device\":\"mouse\",\"input\":\"" + input +
            "\",\"screen\":\"" + uiFlow.Screen + "\",\"game_seconds\":" + (ui == null ? 0L : ui.World.GameSeconds) +
            ",\"frame\":\"" + framePrefix + "/" + flow + ".png\"}");
        File.WriteAllText(Path.Combine(outDir, "input.json"), "{\"tree_hash\":\"" + tree +
            "\",\"binary_sha256\":\"" + binarySha + "\",\"events\":[" + string.Join(",", uiEvents) + "]}");
        capturing = false; WriteUiReady();
        if (flow == "takeover") { done = true; Debug.Log("M1 ui done"); }
    }

    void UpdateUi()
    {
        if (ui == null || uiFlow.Screen != TitleScreen.Playing) return;
        ui.Clock.AdvanceRealSeconds(Core, System.Math.Min(Time.deltaTime, 0.5f), delegate { ui.Food.Tick(Exec); });
        Mirror(ui);
        if (ui.Nav.FollowedResident.HasValue)
        {
            Vector3 p = roots[0].position;
            cam.transform.position = p + new Vector3(0f, 5f, -7f); cam.transform.LookAt(p + Vector3.up);
        }
        if (takeoverWaiting)
        {
            ExecutionRecord record = ui.Executor.GetExecution(takeoverAction);
            if (record != null && record.IsTerminal)
            {
                if (record.Outcome != TerminalOutcome.Completed || ui.Commands.AutomationEnabled) { Fail("takeover-not-completed"); return; }
                takeoverWaiting = false;
                ResidentRecord r; ui.World.TryGetResident(ui.Residents[0], out r);
                uiNotice = "Fast food completed; money " + r.Money + "; hunger " + r.GetNeed(Need.Hunger).ToString("F2");
                StartCoroutine(CaptureUi("takeover", "left click Take over / Fast food; observed completion after ticks"));
            }
        }
    }
}
