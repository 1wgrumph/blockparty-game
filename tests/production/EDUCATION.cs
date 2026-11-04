/* EDUCATION@1 test-first contract; resource group isolated-csharp, no editor.
   New suite required by plan.json and m3/test-engineer.json EDUCATION.
   Oracles: SDoc BP-SEC-05 ACT-EDU-01/03/04 and BP-ACT-05; course prices,
   eight-session thresholds and child +20/500 are candidate catalog values.

   New product seam in Blockparty.Activities.Education (reflection only here,
   so all test bodies compile against the baseline's real dependencies):
     Education(WorldState, CommandArbitrator, ActivityExecutor, Accounts, BlockSites)
     CommandResult Submit(CommandEnvelope)
     void Tick(StateWriter) -- observe completions after Executor/Progression;
                              never tick the executor or duplicate their gains
     int SkillCap(ResidentId, string skill)
     bool CanEnterCareer(ResidentId, string job, int level)
     bool IsGraduated(ResidentId)
   Course completion remains ResidentRecord.CompletedCourses; public SAVE-V2
   SaveV1.SaveGame/LoadGame persists progress, costs and unlocks. No schema pin.
   Scene re-registers the same allocated targets, never allocates replacement ids.

   CourseThresholdsAndReload => suite_checks: eight sessions, costs 320/400,
     exact unlock boundary and persisted partial/completed progress;
     SEIT-ACT-05-P1, SEIT-AC-04/05 education subclaims.
   InterruptedEighth => SEIT-ACT-05-N1: seven sessions plus an interrupted eighth
     grants no progress/cost/unlock; finishing the replacement eighth does.
   SchoolTravelAndReload => SEIT-ACT-05-P1: real in-block school travel,
     480 survives public save/load, next completed child session reaches 500.
   negative-fixture [CulinaryCertificate|ComputerSkills] corrupts the observed
     completion/choices at session seven; the SAME no-early-unlock assertions
     must print FAIL and exit 1. With no course argument both are exercised.
   These local checks do not qualify rendering, normal controls, packaged process
   restart or whole SEIT acceptance. Product-step normal/negative reruns required.
*/
using System;
using System.IO;
using System.Reflection;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class EducationChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private sealed class Scene : ISceneContent
    {
        public BlockSites Sites;
        public ObjectId Classroom, Computer;
        public void RegisterObjects(ActivityExecutor executor)
        {
            foreach (BlockSite site in Sites.Sites)
            {
                executor.RegisterObject(site.Object, new[] { site.Entry });
                foreach (BlockSiteSlot slot in site.Slots)
                    executor.RegisterObject(slot.Object, new[] { slot.Pose });
            }
            executor.RegisterObject(Classroom, new[] { new ApproachPose(0f, 0f, 0f) });
            executor.RegisterObject(Computer, new[] { new ApproachPose(0f, 0f, 0f) });
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
        public AppearanceStore Appearances;
        public Progression Progression;
        public ResidentId Resident;
        public Scene Scene;
        public object Education;

        public void BindEducation()
        {
            Type type = typeof(WorldState).Assembly.GetType("Blockparty.Activities.Education");
            Require(type != null, "missing EDUCATION capability: Blockparty.Activities.Education course/school service");
            Education = Activator.CreateInstance(type,
                new object[] { World, Commands, Executor, Accounts, Scene.Sites });
        }

        public T Call<T>(string method, params object[] args)
        {
            MethodInfo entry = Education.GetType().GetMethod(method);
            Require(entry != null, "missing EDUCATION capability: Education." + method);
            return (T)entry.Invoke(Education, args);
        }

        public void Tick()
        {
            Executor.Tick(Exec);
            Progression.Tick(Exec);
            Call<object>("Tick", Exec);
        }

        public void Advance(long seconds)
        {
            Clock.AdvanceRealSeconds(Core, (double)seconds / 60.0);
            Tick();
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

    private static Fixture NewFixture(LifeStage stage)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 4242L, 0, null);
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Education", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Learner", stage, household.Id, home.Id).Id;
        f.World.SetMoney(Core, f.Resident, 10000L);
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Commands = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Commands, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Commands, f.Executor, f.Accounts);
        f.Appearances = new AppearanceStore();
        f.Progression = new Progression(f.World);
        f.Scene = new Scene { Sites = new BlockSites(f.World, f.Executor),
            Classroom = f.World.AllocateObject(Core), Computer = f.World.AllocateObject(Core) };
        // The two owned course targets model the catalog classroom/Computer;
        // school uses BLOCK-SITES' actual school desk, never this fixture origin.
        f.Executor.RegisterObject(f.Scene.Classroom, new[] { new ApproachPose(0f, 0f, 0f) });
        f.Executor.RegisterObject(f.Scene.Computer, new[] { new ApproachPose(0f, 0f, 0f) });
        f.Executor.SetResidentPosition(f.Resident, new ApproachPose(0f, 0f, 0f));
        f.BindEducation();
        return f;
    }

    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord r;
        Require(f.World.TryGetResident(f.Resident, out r), "owned learner missing");
        return r;
    }

    private static int Completed(Fixture f, string course)
    {
        int count = 0;
        foreach (string value in Resident(f).CompletedCourses) if (value == course) count++;
        return count;
    }

    private static Fixture Reload(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(new GameStack(f.World, f.Clock, f.Commands,
            f.Executor, f.Appearances, f.Accounts, f.Food), directory);
        Require(saved.Ok, "public save failed: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, f.Scene);
        Require(loaded.Ok, "public load failed: " + loaded.Notice);
        LoadedGame g = loaded.Game;
        Fixture next = new Fixture { World = g.World, Clock = g.Clock, Commands = g.Arbitrator,
            Executor = g.Executor, Accounts = g.Accounts, Food = g.Food, Appearances = g.Appearances,
            Progression = new Progression(g.World), Resident = f.Resident, Scene = f.Scene };
        next.BindEducation();
        next.Tick();
        return next;
    }

    private static CommandResult Submit(Fixture f, string code, ObjectId target)
    {
        ResidentRecord r = Resident(f);
        return f.Call<CommandResult>("Submit", new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId(code), (long)target,
            new CommandParameters.Direct(QueueMode.Immediate)));
    }

    private static ActionInstanceId Begin(Fixture f, string code, ObjectId target)
    {
        CommandResult result = Submit(f, code, target);
        Require(result.Accepted, code + " refused: " + result.Reason);
        ActionInstanceId action = f.Commands.GetCurrent(f.Resident).Action;
        f.Tick();
        // Bounded simulation ticks, no wall-clock wait; enough for this block's
        // real NAV path (school is about 45 m from the owned start).
        for (int i = 0; i < 2000; i++)
        {
            ExecutionRecord record = f.Executor.GetExecution(action);
            Require(record != null && !record.IsTerminal, code + " terminated before Perform");
            if (record.Stage == ExecutionStage.Perform) return action;
            f.Advance(6L);
        }
        throw new Exception(code + " did not reach Perform within 200 game minutes");
    }

    private static void Finish(Fixture f, ActionInstanceId action, long seconds)
    {
        f.Advance(seconds);
        ExecutionRecord record = f.Executor.GetExecution(action);
        Require(record.IsTerminal && record.Outcome == TerminalOutcome.Completed,
            "catalog action did not complete: " + record.Activity.Code);
        f.Tick();
        f.Tick();
    }

    private static void Unlocks(Fixture f, string course, bool unlocked, bool corrupt = false)
    {
        bool culinary = course == "CulinaryCertificate";
        string skill = culinary ? "Cooking" : "Logic";
        string job = culinary ? "Barista" : "JuniorTechnician";
        int level = culinary ? 3 : 2;
        // Corrupt observations only: no replacement service or progression fake.
        int completions = corrupt ? 1 : Completed(f, course);
        int cap = corrupt ? 10 : f.Call<int>("SkillCap", f.Resident, skill);
        bool career = corrupt || f.Call<bool>("CanEnterCareer", f.Resident, job, level);
        string id = (unlocked ? "SEIT-ACT-05-P1-" : "SEIT-ACT-05-N1-") + course;
        Check(completions == (unlocked ? 1 : 0), id + "-completion-boundary");
        Check(unlocked ? cap == 10 : culinary ? cap == 5 : cap < 10, id + "-skill-cap-choice");
        Check(career == unlocked, id + "-career-level-choice");
        if (!culinary)
            Check((corrupt || f.Call<bool>("CanEnterCareer", f.Resident, "AIEngineer", 1)) == unlocked,
                id + "-AIEngineer-entry-choice");
    }

    private static ObjectId CourseTarget(Fixture f, string course)
    {
        return course == "CulinaryCertificate" ? f.Scene.Classroom : f.Scene.Computer;
    }

    private static string CourseCode(string course)
    {
        return course == "CulinaryCertificate" ? "ACT-EDU-03" : "ACT-EDU-04";
    }

    private static long CourseCost(Fixture f)
    {
        long total = 0L;
        foreach (AccountTransaction entry in f.Accounts.Ledger)
            if (entry.Source.Equals(AccountRef.Personal(f.Resident))) total += entry.Amount;
        return total;
    }

    private static void CourseThresholdsAndReload(string root, string course, bool negative)
    {
        // Adult keeps unrelated stage gates from masking the course choices.
        Fixture f = NewFixture(LifeStage.Adult);
        long price = course == "CulinaryCertificate" ? 40L : 50L;
        Unlocks(f, course, false);
        Check(f.Call<bool>("CanEnterCareer", f.Resident, "Barista", 1),
            "SEIT-ACT-05-N1-entry-level-Barista-remains-available");
        for (int session = 1; session <= 7; session++)
        {
            ActionInstanceId action = Begin(f, CourseCode(course), CourseTarget(f, course));
            f.Advance(7194L);
            Unlocks(f, course, false);
            Finish(f, action, 6L);
            Unlocks(f, course, false, negative && session == 7);
            Check(Resident(f).Money == 10000L - session * price && CourseCost(f) == session * price,
                "SEIT-AC-05-" + course + "-session-" + session + "-cost-once");
        }
        if (negative) return;
        f = Reload(f, Path.Combine(root, course + "-seven"));
        Unlocks(f, course, false);
        Check(Resident(f).Money == 10000L - 7L * price && CourseCost(f) == 7L * price,
            "SEIT-AC-05-" + course + "-seven-session-cost-restored");
        ActionInstanceId eighth = Begin(f, CourseCode(course), CourseTarget(f, course));
        f.Advance(7194L);
        Unlocks(f, course, false);
        Finish(f, eighth, 6L);
        Unlocks(f, course, true);
        Check(Resident(f).Money == 10000L - 8L * price && CourseCost(f) == 8L * price,
            "SEIT-ACT-05-P1-" + course + "-eight-session-total-" + (8L * price));
        f = Reload(f, Path.Combine(root, course + "-eight"));
        Unlocks(f, course, true);
        Check(Resident(f).Money == 10000L - 8L * price && CourseCost(f) == 8L * price,
            "SEIT-AC-05-" + course + "-completed-cost-restored-no-replay");
    }

    private static void InterruptedEighth(string course)
    {
        Fixture f = NewFixture(LifeStage.Adult);
        long price = course == "CulinaryCertificate" ? 40L : 50L;
        for (int i = 0; i < 7; i++)
            Finish(f, Begin(f, CourseCode(course), CourseTarget(f, course)), 7200L);
        ActionInstanceId interrupted = Begin(f, CourseCode(course), CourseTarget(f, course));
        f.Advance(3600L);
        CommandResult replacement = f.Commands.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            Resident(f).ResidentSequence + 1u, CommandKind.DirectActivity, f.Resident,
            new ActivityId("ACT-HOME-01"), (long)f.Scene.Classroom,
            new CommandParameters.Direct(QueueMode.Immediate)));
        Require(replacement.Accepted, "real interrupting command refused");
        f.Tick();
        ExecutionRecord record = f.Executor.GetExecution(interrupted);
        Require(record.IsTerminal && record.Outcome != TerminalOutcome.Completed,
            "fixture did not interrupt eighth session");
        f.Advance(7200L);
        Unlocks(f, course, false);
        Check(Resident(f).Money == 10000L - 7L * price && CourseCost(f) == 7L * price,
            "SEIT-ACT-05-N1-" + course + "-interruption-no-completion-cost");
        Finish(f, Begin(f, CourseCode(course), CourseTarget(f, course)), 7200L);
        Unlocks(f, course, true);
        Check(Resident(f).Money == 10000L - 8L * price && CourseCost(f) == 8L * price,
            "SEIT-ACT-05-N1-" + course + "-replacement-is-eighth-not-ninth");
    }

    private static bool At(ApproachPose a, ApproachPose b)
    {
        return Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f
            && Math.Abs(a.Z - b.Z) < 0.01f;
    }

    private static void SchoolTravelAndReload(string root)
    {
        Fixture f = NewFixture(LifeStage.Child);
        BlockSite school = null;
        foreach (BlockSite site in f.Scene.Sites.Sites) if (site.Id == "school") school = site;
        Require(school != null && school.Slots.Count > 0, "BLOCK-SITES school fixture missing");
        BlockSiteSlot desk = school.Slots[0];
        Require(desk.ActivityCode == "ACT-EDU-01", "BLOCK-SITES school desk offers wrong activity");
        for (int session = 1; session <= 25; session++)
        {
            // One weekday session per day at 08:00, matching the committed
            // autonomy calendar (day zero Monday). Skip weekends by game time.
            long day = f.World.GameSeconds / 86400L;
            long start = day * 86400L + 28800L;
            if (start < f.World.GameSeconds) { day++; start += 86400L; }
            while (day % 7L > 4L) { day++; start += 86400L; }
            if (start > f.World.GameSeconds) f.Advance(start - f.World.GameSeconds);
            if (session == 1)
            {
                ApproachPose origin = f.Executor.GetResidentPosition(f.Resident);
                Require(!At(origin, desk.Pose), "school route fixture starts at destination");
                CommandResult submitted = Submit(f, "ACT-EDU-01", desk.Object);
                Require(submitted.Accepted, "school command refused: " + submitted.Reason);
                ActionInstanceId action = f.Commands.GetCurrent(f.Resident).Action;
                f.Tick();
                f.Advance(6L);
                ExecutionRecord travelling = f.Executor.GetExecution(action);
                Check(travelling.Stage == ExecutionStage.Travel
                    && !At(f.Executor.GetResidentPosition(f.Resident), desk.Pose),
                    "SEIT-ACT-05-P1-school-real-travel-not-teleport");
                for (int i = 0; i < 2000 && travelling.Stage != ExecutionStage.Perform && !travelling.IsTerminal; i++)
                    f.Advance(6L);
                Require(travelling.Stage == ExecutionStage.Perform, "school did not reach Perform");
                Check(At(f.Executor.GetResidentPosition(f.Resident), desk.Pose)
                    && travelling.TargetId == (long)desk.Object && f.Executor.GetHold(desk.Object, 0).HasValue,
                    "SEIT-AC-04-school-performing-at-registered-in-block-desk");
                Finish(f, action, 21600L);
            }
            else Finish(f, Begin(f, "ACT-EDU-01", desk.Object), 21600L);
            Check(Resident(f).EducationPoints == session * 20,
                "SEIT-ACT-05-P1-school-child-progress-once-session-" + session);
            Check(f.Call<bool>("IsGraduated", f.Resident) == (session == 25),
                "SEIT-ACT-05-P1-school-graduation-at-500-session-" + session);
            if (session == 24)
            {
                f = Reload(f, Path.Combine(root, "school-480"));
                Check(Resident(f).EducationPoints == 480 && !f.Call<bool>("IsGraduated", f.Resident),
                    "SEIT-ACT-05-P1-school-480-restores-before-final-session");
            }
        }
        f = Reload(f, Path.Combine(root, "school-500"));
        Check(Resident(f).EducationPoints == 500 && f.Call<bool>("IsGraduated", f.Resident),
            "SEIT-ACT-05-P1-school-500-graduation-restores-no-replay");
    }

    public static int Main(string[] args)
    {
        failures = 0;
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        string root = Path.Combine(Path.GetTempPath(), "blockparty-education-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (string course in new[] { "CulinaryCertificate", "ComputerSkills" })
            {
                if (negative && args.Length > 1 && Array.IndexOf(args, course) < 0) continue;
                Run("CourseThresholdsAndReload-" + course,
                    () => CourseThresholdsAndReload(root, course, negative));
                if (!negative) Run("InterruptedEighth-" + course, () => InterruptedEighth(course));
            }
            if (!negative) Run("SchoolTravelAndReload", () => SchoolTravelAndReload(root));
        }
        catch (Exception e) { Check(false, "fixture " + e.GetBaseException().Message); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        if (failures == 0) Console.WriteLine("EDUCATION ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
