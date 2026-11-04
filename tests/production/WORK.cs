/* WORK@1, TDD red contract. New product surface (mirrors FOOD):
   Blockparty.Activities.Work(WorldState, CommandArbitrator, ActivityExecutor,
                             Accounts, BlockSites)
   CommandResult Submit(CommandEnvelope); void Tick(StateWriter);
   CommandResult Complete(ResidentId, ActionInstanceId);
   IEnumerable<string> AvailableJobs(ResidentId, ObjectId? searchComputer);
   null computer means phone. Tick drives the real executor; the existing
   Progression producer is ticked separately, never replaced by this test.
   Reflection isolates only this absent surface, so baseline compiles and
   reports missing WORK capability rather than hiding test syntax errors.

   Oracles: BP-ACT-04 / BP-SEC-05; WORK suite_checks in m3/test-engineer.json.
   AIEngineer numeric values are proposed/unmeasured candidates, not balance
   measurements. Named checks map to SEIT cases below; packaged restart and
   whole AC-04/05 acceptance remain at their integration gates.
   Separate suite required by the WORK package selector. Resource group:
   isolated-csharp; no editor/network/shared resources. Save fixtures own a
   unique temporary directory, removed in finally.
   negative-fixture [name] changes one real input/state or observed result;
   the same oracle must fail. Baseline must pass before qualifying injections.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class WorkChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;
    private static string fault;
    private static string root;
    private static readonly string[] Faults = {
        "missing-office-jobsearch", "missing-ai-engineer", "eligible-denied",
        "underage-accepted", "unqualified-accepted", "missing-office-technician",
        "missing-workshop-technician", "double-pay", "double-progress",
        "interrupted-pay", "off-world-completion", "resume-double-pay"
    };

    private sealed class Scene : ISceneContent
    {
        public readonly List<BlockSiteSlot> Slots = new List<BlockSiteSlot>();
        public readonly List<BlockSite> Sites = new List<BlockSite>();
        public ObjectId HomeComputer;
        public void RegisterObjects(ActivityExecutor executor)
        {
            foreach (BlockSite site in Sites)
                executor.RegisterObject(site.Object, new[] { site.Entry });
            foreach (BlockSiteSlot slot in Slots)
                executor.RegisterObject(slot.Object, new[] { slot.Pose });
            executor.RegisterObject(HomeComputer, new[] { new ApproachPose(15f, 0f, 15f) });
        }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public Progression Progression;
        public AppearanceStore Appearances;
        public BlockSites Sites;
        public WorldNavigation Navigation;
        public ResidentId Resident;
        public Scene Scene;
        public object Work;
        public Type WorkType;
        public float MinX, MaxX, MinZ, MaxZ;
        public bool Inside = true;
        public bool Bounded = true;
        public void Attach()
        {
            WorkType = typeof(WorldState).Assembly.GetType("Blockparty.Activities.Work");
            Require(WorkType != null, "missing WORK capability: Blockparty.Activities.Work shift service");
            Work = Activator.CreateInstance(WorkType, new object[] { World, Commands, Executor, Accounts, Sites });
            Progression = new Progression(World);
            Navigation = new WorldNavigation(World, Commands, Executor, Sites);
        }
        public object Call(string name, params object[] args)
        {
            MethodInfo method = WorkType.GetMethod(name);
            Require(method != null, "missing WORK capability: Work." + name);
            return method.Invoke(Work, args);
        }
        public void Tick(long seconds = 0L)
        {
            ApproachPose before = Executor.GetResidentPosition(Resident);
            Clock.AdvanceRealSeconds(Core, (double)seconds / 60.0);
            Call("Tick", Exec);
            Progression.Tick(Exec);
            Navigation.Refresh();
            ApproachPose after = Executor.GetResidentPosition(Resident);
            Inside &= !float.IsNaN(after.X) && !float.IsNaN(after.Y) && !float.IsNaN(after.Z)
                && after.X >= MinX && after.X <= MaxX && after.Z >= MinZ && after.Z <= MaxZ && after.Y == 0f;
            Bounded &= Distance(before, after) <= ActivityExecutor.TravelMetersPerGameMinute * seconds / 60.0 + 0.0001;
        }
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e.GetBaseException().Message); }
    }
    private static double Distance(ApproachPose a, ApproachPose b)
    {
        double x = (double)a.X-b.X, y = (double)a.Y-b.Y, z = (double)a.Z-b.Z;
        return Math.Sqrt(x*x+y*y+z*z);
    }
    private static Fixture New(string job = "AIEngineer", int level = 1,
        LifeStage stage = LifeStage.YoungAdult, bool computer = true, bool culinary = false, int points = 0)
    {
        var f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 4242L, 0, null);
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "WORK", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Worker", stage, household.Id, home.Id).Id;
        f.World.SetMoney(Core, f.Resident, 1000L);
        f.World.SetCareer(Core, f.Resident, job, level, points);
        foreach (Need need in Enum.GetValues(typeof(Need))) f.World.SetNeed(Core, f.Resident, need, 100f);
        if (computer) f.World.AddCompletedCourse(Core, f.Resident, "ComputerSkills");
        if (culinary) f.World.AddCompletedCourse(Core, f.Resident, "CulinaryCertificate");
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Commands = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Commands, f.Clock);
        f.Sites = new BlockSites(f.World, f.Executor);
        f.Scene = new Scene();
        foreach (BlockSite site in f.Sites.Sites)
        {
            f.Scene.Sites.Add(site);
            f.MinX = Math.Min(f.MinX, site.Entry.X); f.MaxX = Math.Max(f.MaxX, site.Entry.X);
            f.MinZ = Math.Min(f.MinZ, site.Entry.Z); f.MaxZ = Math.Max(f.MaxZ, site.Entry.Z);
            foreach (BlockSiteSlot slot in site.Slots)
            {
                f.Scene.Slots.Add(slot);
                f.MinX = Math.Min(f.MinX, slot.Pose.X); f.MaxX = Math.Max(f.MaxX, slot.Pose.X);
                f.MinZ = Math.Min(f.MinZ, slot.Pose.Z); f.MaxZ = Math.Max(f.MaxZ, slot.Pose.Z);
            }
        }
        f.Scene.HomeComputer = f.World.AllocateObject(Core);
        f.Executor.RegisterObject(f.Scene.HomeComputer, new[] { new ApproachPose(15f, 0f, 15f) });
        f.Executor.SetResidentPosition(f.Resident, new ApproachPose(15f, 0f, 15f));
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Commands, f.Executor, f.Accounts);
        f.Appearances = new AppearanceStore();
        f.Attach();
        return f;
    }
    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord resident;
        Require(f.World.TryGetResident(f.Resident, out resident), "owned resident missing");
        return resident;
    }
    private static BlockSiteSlot Slot(Fixture f, string siteName, string code)
    {
        foreach (BlockSite site in f.Sites.Sites)
            if (site.Id == siteName)
                foreach (BlockSiteSlot slot in site.Slots)
                    if (slot.ActivityCode == code) return slot;
        throw new Exception("missing WORK destination " + siteName + "/" + code);
    }
    private static CommandResult Submit(Fixture f, string code, ObjectId? target)
    {
        ResidentRecord r = Resident(f);
        return (CommandResult)f.Call("Submit", new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId(code),
            target.HasValue ? (long?)((long)target.Value) : null, new CommandParameters.Direct(QueueMode.Immediate)));
    }
    private static ActionInstanceId Begin(Fixture f, string code, ObjectId? target)
    {
        CommandResult result = Submit(f, code, target);
        Require(result.Accepted, "missing WORK capability: eligible " + code + " refused " + result.Reason);
        ActionEntry current = f.Commands.GetCurrent(f.Resident);
        Require(current != null, "accepted work action missing");
        ActionInstanceId action = current.Action;
        for (int i = 0; i < 100; i++)
        {
            f.Tick(i == 0 ? 0L : 60L);
            ExecutionRecord execution = f.Executor.GetExecution(action);
            Require(execution != null && !execution.IsTerminal, "shift failed before Perform: " + code);
            if (execution.Stage == ExecutionStage.Perform) return action;
        }
        throw new Exception("shift never reached Perform: " + code);
    }
    private static int Committed(Fixture f, ActionInstanceId action)
    {
        int count = 0;
        foreach (EffectLedgerEntry entry in f.World.EffectLedger)
            if (entry.Action == action && entry.State == LedgerState.Committed) count++;
        return count;
    }
    private static string Rewards(Fixture f)
    {
        ResidentRecord r = Resident(f);
        return r.Money + ":" + r.CareerLevel + ":" + r.CareerPoints + ":" + r.GetNeed(Need.Energy) + ":" + r.GetNeed(Need.Fun);
    }
    private static void Finish(Fixture f, ActionInstanceId action, long minutes)
    {
        for (long i = 0; i < minutes; i++) f.Tick(60L);
        Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed && Committed(f, action) == 1,
            "SEIT-ACT-04-P1 completed-one-ledger-" + f.Executor.GetExecution(action).Activity.Code);
    }
    private static void Shift(string job, string code, string site, int level, long minutes,
        long pay, int energy, int fun, int gain)
    {
        Fixture f = New(job, level, culinary: true);
        BlockSiteSlot slot = Slot(f, site, code);
        // Exercise actual map TravelTo before work; the shift also has a real slot target.
        ApproachPose start = f.Executor.GetResidentPosition(f.Resident);
        Check(f.Navigation.TravelTo(f.Resident, (long)slot.Object, QueueMode.Immediate, TravelTargetKind.Object).Accepted
            && Distance(start, f.Executor.GetResidentPosition(f.Resident)) == 0,
            "SEIT-AC-04 TravelTo-no-teleport-" + job + "-" + site + "-L" + level);
        ActionEntry travel = f.Commands.GetCurrent(f.Resident);
        Require(travel != null, "travel command missing");
        for (int i = 0; i < 100 && f.Commands.GetCurrent(f.Resident) != null; i++) f.Tick(60L);
        Require(f.Executor.GetExecution(travel.Action).Outcome == TerminalOutcome.Completed, "TravelTo did not arrive");
        // Start away from the workplace again so ACT-WORK travel itself is exercised.
        f.Executor.SetResidentPosition(f.Resident, start);
        ActionInstanceId action = Begin(f, code, slot.Object);
        ExecutionRecord execution = f.Executor.GetExecution(action);
        Check(execution.TargetId == (long)slot.Object && execution.Holds.Length == 1
            && execution.Holds[0].Object == slot.Object
            && Distance(f.Executor.GetResidentPosition(f.Resident), slot.Pose) <= ActivityExecutor.ArrivalToleranceMeters,
            "SEIT-AC-04 single-chosen-site-" + job + "-" + site);
        string before = Rewards(f);
        f.Tick((minutes-1L)*60L);
        Check(!execution.IsTerminal && Committed(f, action) == 0 && Rewards(f) == before,
            "SEIT-ACT-04-P1 no-effects-before-" + minutes + "min-" + job);
        Finish(f, action, 1L);
        if (fault == "double-pay") f.World.SetMoney(Core, f.Resident, Resident(f).Money + pay);
        if (fault == "double-progress") f.World.SetCareer(Core, f.Resident, job, level, Resident(f).CareerPoints + gain);
        if (fault == "off-world-completion") f.Executor.SetResidentPosition(f.Resident, new ApproachPose(float.PositiveInfinity, 0f, 0f));
        f.Tick();
        ResidentRecord r = Resident(f);
        Check(r.Money == 1000L+pay && r.CareerLevel == level && r.CareerPoints == gain
            && r.GetNeed(Need.Energy) == 100f-energy && r.GetNeed(Need.Fun) == 100f-fun,
            "SEIT-ACT-04-P1 catalog-pay-needs-progress-" + job + "-" + site + "-L" + level);
        string once = Rewards(f);
        f.Call("Complete", f.Resident, action);
        f.Tick(); f.Tick();
        Check(Rewards(f) == once && Committed(f, action) == 1 && execution.Holds.Length == 0,
            "SEIT-AC-05 repeated-completion-no-effects-" + job);
        Check(f.Inside && f.Bounded, "SEIT-AC-04 in-block-throughout-" + job + "-" + site);
    }
    private static void Catalog()
    {
        Shift("Barista", "ACT-WORK-01", "cafe", 2, 360L, 96L, 30, 10, 20);
        foreach (string site in new[] { "office", "workshop" })
            Shift("JuniorTechnician", "ACT-WORK-02", site, 1, 480L, 160L, 40, 15, 25);
        long[] pay = { 192L, 220L, 249L, 278L, 307L };
        for (int level = 1; level <= 5; level++)
            Shift("AIEngineer", "ACT-WORK-04", "office", level, 480L, pay[level-1], 40, 15, 25);
    }
    private static void JobSearch()
    {
        foreach (string entry in new[] { "office", "home", "phone" })
        {
            Fixture f = New();
            ObjectId? target = entry == "office" ? Slot(f, "office", "ACT-WORK-03").Object
                : entry == "home" ? (ObjectId?)f.Scene.HomeComputer : null;
            if (fault == "missing-office-jobsearch" && entry == "office") f.Executor.RemoveObject(target.Value);
            var jobs = new HashSet<string>();
            foreach (string job in (IEnumerable)f.Call("AvailableJobs", f.Resident, target)) jobs.Add(job);
            if (fault == "missing-ai-engineer") jobs.Remove("AIEngineer");
            Check(jobs.Contains("Barista") && jobs.Contains("JuniorTechnician") && jobs.Contains("AIEngineer"),
                "SEIT-ACT-04-P1 JobSearch-three-careers-" + entry);
            ActionInstanceId action = Begin(f, "ACT-WORK-03", target);
            f.Tick(59L*60L);
            Check(!f.Executor.GetExecution(action).IsTerminal, "SEIT-ACT-04-P1 JobSearch-60min-" + entry);
            Finish(f, action, 1L);
            Check(f.Inside && f.Bounded, "SEIT-AC-04 JobSearch-in-block-" + entry);
        }
    }
    private static void Eligibility()
    {
        foreach (string job in new[] { "Barista", "JuniorTechnician", "AIEngineer" })
            for (int level = 1; level <= 5; level++)
                foreach (bool qualified in new[] { false, true })
                    foreach (LifeStage stage in new[] { LifeStage.Child, LifeStage.Teen, LifeStage.YoungAdult, LifeStage.Adult, LifeStage.Elder })
                    {
                        Fixture f = New(job, level, stage, qualified, qualified);
                        string code = job == "Barista" ? "ACT-WORK-01" : job == "JuniorTechnician" ? "ACT-WORK-02" : "ACT-WORK-04";
                        string site = job == "Barista" ? "cafe" : "office";
                        bool age = stage >= LifeStage.YoungAdult || (job == "Barista" && stage == LifeStage.Teen && level == 1);
                        bool course = qualified || (job == "Barista" ? level < 3 : job == "JuniorTechnician" && level == 1);
                        bool expected = age && course;
                        string before = Rewards(f);
                        bool accepted = Submit(f, code, Slot(f, site, code).Object).Accepted;
                        if (job == "AIEngineer")
                        {
                            if (fault == "eligible-denied" && expected) accepted = false;
                            if (fault == "underage-accepted" && !age && qualified) accepted = true;
                            if (fault == "unqualified-accepted" && age && !qualified) accepted = true;
                        }
                        Check(accepted == expected && Rewards(f) == before,
                            "SEIT-ACT-04-N1 entry-" + job + "-L" + level + "-" + stage + "-course=" + qualified);
                    }
    }
    private static void MissingDestinations()
    {
        foreach (string removed in new[] { "office", "workshop" })
        {
            Fixture f = New("JuniorTechnician", computer: false);
            BlockSiteSlot missing = Slot(f, removed, "ACT-WORK-02");
            f.Executor.RemoveObject(missing.Object);
            string before = Rewards(f);
            CommandResult result = Submit(f, "ACT-WORK-02", missing.Object);
            f.Tick();
            ActionEntry current = f.Commands.GetCurrent(f.Resident);
            Check((!result.Accepted || current == null || f.Executor.GetExecution(current.Action).Outcome == TerminalOutcome.Failed)
                && Rewards(f) == before, "SEIT-ACT-04-N1 missing-" + removed + "-destination-refused-no-pay");
        }
        // Removing either alternate must also be detected by the normal positive oracle.
        foreach (string site in new[] { "office", "workshop" })
        {
            Fixture f = New("JuniorTechnician", computer: false);
            BlockSiteSlot slot = Slot(f, site, "ACT-WORK-02");
            if (fault == "missing-" + site + "-technician") f.Executor.RemoveObject(slot.Object);
            Begin(f, "ACT-WORK-02", slot.Object);
        }
    }
    private static void Interruptions()
    {
        foreach (string job in new[] { "Barista", "JuniorTechnician", "AIEngineer" })
        {
            Fixture f = New(job);
            string code = job == "Barista" ? "ACT-WORK-01" : job == "JuniorTechnician" ? "ACT-WORK-02" : "ACT-WORK-04";
            ActionInstanceId action = Begin(f, code, Slot(f, job == "Barista" ? "cafe" : "office", code).Object);
            f.Tick(300L*60L);
            string before = Rewards(f);
            ResidentRecord r = Resident(f);
            Require(f.Commands.Submit(new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
                f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence+1u,
                CommandKind.CancelCurrent, r.Id, null, null, CommandParameters.None.Instance)).Accepted, "cancel refused");
            f.Tick(); f.Tick(480L*60L);
            if (fault == "interrupted-pay") f.World.SetMoney(Core, f.Resident, Resident(f).Money+1L);
            ExecutionRecord execution = f.Executor.GetExecution(action);
            Check(execution.Outcome == TerminalOutcome.Interrupted && execution.Holds.Length == 0
                && Committed(f, action) == 0 && Rewards(f) == before && f.Inside,
                "SEIT-ACT-04-N1 SEIT-IF-EXEC-N2 interrupted-no-pay-progress-needs-and-slot-release-" + job);
            f.Call("Complete", f.Resident, action); f.Tick();
            Check(Rewards(f) == before, "SEIT-AC-05 interrupted-completion-notification-no-pay-" + job);
        }
    }
    private static Fixture Reload(Fixture f, string path)
    {
        SaveWriteResult saved = SaveV1.SaveGame(new GameStack(f.World, f.Clock, f.Commands, f.Executor,
            f.Appearances, f.Accounts, f.Food), path);
        Require(saved.Ok, "public save failed: " + saved.Detail);
        return Load(f, path);
    }
    private static Fixture Load(Fixture source, string path)
    {
        SaveLoadResult loaded = SaveV1.LoadGame(path, source.Scene);
        Require(loaded.Ok, "public SAVE-V2 load failed: " + loaded.Notice);
        LoadedGame g = loaded.Game;
        var f = new Fixture { World = g.World, Clock = g.Clock, Commands = g.Arbitrator,
            Executor = g.Executor, Accounts = g.Accounts, Food = g.Food, Appearances = g.Appearances,
            Resident = source.Resident, Sites = source.Sites, Scene = source.Scene,
            MinX = source.MinX, MaxX = source.MaxX, MinZ = source.MinZ, MaxZ = source.MaxZ };
        f.Attach();
        return f;
    }
    private static void SaveResume()
    {
        foreach (string job in new[] { "Barista", "JuniorTechnician", "AIEngineer" })
        {
            Fixture source = New(job);
            string code = job == "Barista" ? "ACT-WORK-01" : job == "JuniorTechnician" ? "ACT-WORK-02" : "ACT-WORK-04";
            long minutes = job == "Barista" ? 360L : 480L;
            long pay = job == "Barista" ? 84L : job == "JuniorTechnician" ? 160L : 192L;
            int gain = job == "Barista" ? 20 : 25;
            ActionInstanceId action = Begin(source, code, Slot(source, job == "Barista" ? "cafe" : "office", code).Object);
            source.Tick(200L*60L);
            string before = Rewards(source);
            string path = Path.Combine(root, job+"-partial");
            Fixture first = Reload(source, path);
            foreach (Fixture f in new[] { first, Load(source, path) })
            {
                ActionEntry resumed = f.Commands.GetCurrent(f.Resident);
                Require(resumed != null && resumed.Activity.Code == code, "saved work action not resumed");
                // SAVE replays commands with a fresh action identity on each timeline.
                ActionInstanceId resumedAction = resumed.Action;
                Check(Rewards(f) == before && Committed(f, resumedAction) == 0,
                    "SEIT-ACT-04-N2 saved-200min-no-early-pay-" + job);
                f.Tick((minutes-201L)*60L);
                Check(!f.Executor.GetExecution(resumedAction).IsTerminal && Rewards(f) == before,
                    "SEIT-ACT-04-N2 resumed-elapsed-not-restarted-" + job);
                Finish(f, resumedAction, 1L);
                if (fault == "resume-double-pay") f.World.SetMoney(Core, f.Resident, Resident(f).Money+pay);
                Check(Resident(f).Money == 1000L+pay && Resident(f).CareerPoints == gain
                    && Resident(f).GetNeed(Need.Energy) == (job == "Barista" ? 70f : 60f)
                    && Resident(f).GetNeed(Need.Fun) == (job == "Barista" ? 90f : 85f) && f.Inside,
                    "SEIT-ACT-04-N2 one-pay-progress-per-loaded-timeline-" + job);
                string once = Rewards(f);
                Fixture done = Reload(f, Path.Combine(root, job+"-done-"+Guid.NewGuid().ToString("N")));
                done.Call("Complete", done.Resident, resumedAction); done.Tick(); done.Tick();
                Check(Rewards(done) == once && Committed(done, resumedAction) == 1,
                    "SEIT-AC-05 completed-save-resume-no-duplicate-" + job);
            }
        }
    }
    private static void Promotion()
    {
        foreach (string job in new[] { "Barista", "JuniorTechnician", "AIEngineer" })
        {
            Fixture f = New(job, points: job == "Barista" ? 80 : 75);
            string code = job == "Barista" ? "ACT-WORK-01" : job == "JuniorTechnician" ? "ACT-WORK-02" : "ACT-WORK-04";
            ActionInstanceId action = Begin(f, code, Slot(f, job == "Barista" ? "cafe" : "office", code).Object);
            Finish(f, action, job == "Barista" ? 360L : 480L);
            Check(Resident(f).CareerLevel == 2 && Resident(f).CareerPoints == 0,
                "SEIT-AC-05 100-points-promotes-via-PROGRESSION-" + job);
            f.Call("Complete", f.Resident, action); f.Tick();
            Check(Resident(f).CareerLevel == 2 && Resident(f).CareerPoints == 0,
                "SEIT-AC-05 promotion-not-replayed-" + job);
        }
    }
    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = New();
            ActionInstanceId action = Begin(f, "ACT-WORK-04", Slot(f, "office", "ACT-WORK-04").Object);
            f.Clock.AdvanceRealSeconds(Core, 480.0, () => f.Call("Tick", writer));
            string actual = f.World.ComputeStateHash() + "|" + Rewards(f);
            Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed
                && Committed(f, action) == 1 && Resident(f).Money == 1192L
                && Resident(f).CareerPoints == 25 && (expected == null || actual == expected),
                "WORK-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    private static void Cases()
    {
        Run("Catalog SEIT-ACT-04-P1 SEIT-AC-04 SEIT-AC-05", Catalog);
        Run("JobSearch SEIT-ACT-04-P1", JobSearch);
        Run("Eligibility SEIT-ACT-04-N1", Eligibility);
        Run("MissingDestinations SEIT-ACT-04-N1", MissingDestinations);
        Run("Interruptions SEIT-ACT-04-N1 SEIT-IF-EXEC-N2", Interruptions);
        Run("SaveResume SEIT-ACT-04-N2 SEIT-AC-05", SaveResume);
        Run("Promotion SEIT-AC-05", Promotion);
        Run("work-tick-writer-contract", AcceptedWriterTick);
    }
    public static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "blockparty-WORK-" + System.Diagnostics.Process.GetCurrentProcess().Id
            + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Cases();
            if (failures != 0) return 1;
            if (args.Length > 0 && args[0] == "negative-fixture")
            {
                string[] selected = args.Length > 1 ? new[] { args[1] } : Faults;
                foreach (string name in selected)
                {
                    if (Array.IndexOf(Faults, name) < 0) { Check(false, "unknown negative fixture " + name); return 1; }
                    fault = name;
                    int before = failures;
                    Console.WriteLine("WORK negative-fixture " + name);
                    Cases();
                    if (failures == before) { Check(false, "NEGATIVE FIXTURE ESCAPED " + name); return 2; }
                    Console.WriteLine("NEGATIVE FIXTURE DETECTED " + name);
                }
                return 1;
            }
            Console.WriteLine("WORK ALL PASS");
            return 0;
        }
        catch (Exception e) { Check(false, "WORK harness " + e.GetBaseException().Message); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
