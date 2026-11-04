/* PROGRESSION@1 TDD contract: Blockparty.Simulation.Progression(WorldState),
   void Tick(StateWriter). Tick consumes completed catalog activities once;
   Food remains the real cooking producer, never a second Cooking +10 grant.
   Reconstructing Progression over a loaded world must retain applied actions
   and partial course sessions through SaveV1's public SAVE-V2 codec.

   Oracles: BP-ECO-04 and BP-SEC-05: Cooking +10, Barista +20/100 per level,
   child/teen school +20/+25, eight CulinaryCertificate sessions, Cooking cap
   5 until certified then 10, Barista level 3+ requires that certificate.
   Each named check below maps to the SEIT id in its name. These are local
   portions of SEIT-AC-05/ECO-04-N3; packaged process-restart acceptance remains
   at the integration gate. New suite follows the package runner convention.
   Resource group isolated-csharp; no editor, network or shared resources.
   negative-fixture completes the cancellation fixture instead of cancelling;
   the unchanged no-gain oracle must print FAIL and exit 1.
*/
using System;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class ProgressionChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public AppearanceStore Appearances;
        public Progression Progression;
        public ResidentId Resident;
        public ObjectId Station;
    }

    private sealed class Scene : ISceneContent
    {
        public ObjectId Station;
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(Station, new[] { new ApproachPose(0f, 0f, 0f) });
        }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static Fixture NewFixture(LifeStage stage = LifeStage.Adult)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 4242L, 0, null);
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Progression", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Learner", stage, household.Id, home.Id).Id;
        f.World.SetMoney(Core, f.Resident, 10000L);
        f.World.SetCareer(Core, f.Resident, "Barista", 1, 0);
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, f.Accounts);
        f.Appearances = new AppearanceStore();
        f.Progression = new Progression(f.World);
        f.Station = f.World.AllocateObject(Core);
        new Scene { Station = f.Station }.RegisterObjects(f.Executor);
        f.Executor.SetResidentPosition(f.Resident, new ApproachPose(0f, 0f, 0f));
        f.Food.SetHomeStock(Core, home.Id, 20, 0);
        return f;
    }

    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord resident;
        Require(f.World.TryGetResident(f.Resident, out resident), "owned fixture resident missing");
        return resident;
    }

    private static SkillEntry Cooking(Fixture f)
    {
        foreach (SkillEntry skill in Resident(f).Skills)
            if (skill.SkillId == "Cooking") return skill;
        return new SkillEntry("Cooking", 0, 0);
    }

    private static int Certificates(Fixture f)
    {
        int count = 0;
        foreach (string course in Resident(f).CompletedCourses)
            if (course == "CulinaryCertificate") count++;
        return count;
    }

    private static string Values(Fixture f)
    {
        ResidentRecord r = Resident(f);
        StringBuilder text = new StringBuilder();
        foreach (SkillEntry skill in r.Skills)
            text.Append(skill.SkillId).Append(':').Append(skill.Level).Append(':').Append(skill.Points).Append('|');
        text.Append(r.JobId).Append(':').Append(r.CareerLevel).Append(':').Append(r.CareerPoints)
            .Append(':').Append(r.EducationPoints);
        foreach (string course in r.CompletedCourses) text.Append('|').Append(course);
        return text.ToString();
    }

    private static void Tick(Fixture f)
    {
        f.Food.Tick(Exec); // Food drives the real executor exactly once.
        f.Progression.Tick(Exec);
    }

    private static ActionInstanceId Begin(Fixture f, string code)
    {
        ResidentRecord r = Resident(f);
        CommandEnvelope command = new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId(code), (long)f.Station,
            new CommandParameters.Direct(QueueMode.Immediate));
        CommandResult result = f.Food.Submit(command);
        Require(result.Accepted, "fixture command refused: " + code + " " + result.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Resident).Action;
        Tick(f);
        Tick(f);
        Require(f.Executor.GetExecution(action).Stage == ExecutionStage.Perform,
            "fixture did not reach Perform: " + code);
        return action;
    }

    private static void Advance(Fixture f, long gameSeconds)
    {
        // All fixtures are already performing at their owned station.
        f.Clock.AdvanceRealSeconds(Core, (double)gameSeconds / 60.0);
        Tick(f);
    }

    private static int Committed(Fixture f, ActionInstanceId action)
    {
        int count = 0;
        foreach (EffectLedgerEntry entry in f.World.EffectLedger)
            if (entry.Action == action && entry.State == LedgerState.Committed) count++;
        return count;
    }

    private static ActionInstanceId Complete(Fixture f, string code, long minutes)
    {
        ActionInstanceId action = Begin(f, code);
        Advance(f, minutes * 60L);
        Require(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed,
            "fixture did not complete: " + code);
        Check(Committed(f, action) == 1, "SEIT-AC-05-" + code + "-one-action-ledger-key");
        return action;
    }

    private static void CompletedActivities()
    {
        Fixture f = NewFixture();
        ActionInstanceId cook = Begin(f, "ACT-FOOD-01");
        Advance(f, 2694L);
        Check(Cooking(f).Level == 0 && Cooking(f).Points == 0 && Committed(f, cook) == 0,
            "SEIT-ECO-04-P1-no-cooking-before-completion");
        Advance(f, 6L);
        Check(Cooking(f).Level == 0 && Cooking(f).Points == 10 && Committed(f, cook) == 1,
            "SEIT-ECO-04-P1-cooking-plus-10-once");
        string once = Values(f);
        f.Food.Complete(f.Resident, cook);
        Tick(f);
        Tick(f);
        f.Progression = new Progression(f.World);
        Tick(f);
        Check(Values(f) == once && Committed(f, cook) == 1,
            "SEIT-ECO-04-P1-duplicate-notification-and-service-reentry-no-second-gain");

        Complete(f, "ACT-WORK-01", 360L);
        Check(Resident(f).CareerLevel == 1 && Resident(f).CareerPoints == 20,
            "SEIT-AC-05-barista-plus-20");
        once = Values(f);
        Tick(f);
        Tick(f);
        Check(Values(f) == once, "SEIT-AC-05-career-applies-once");
        f.World.SetCareer(Core, f.Resident, "Barista", 1, 80);
        Complete(f, "ACT-WORK-01", 360L);
        Check(Resident(f).CareerLevel == 2 && Resident(f).CareerPoints == 0,
            "SEIT-AC-05-career-100-points-promotes-once");

        foreach (LifeStage stage in new[] { LifeStage.Child, LifeStage.Teen })
        {
            Fixture school = NewFixture(stage);
            Complete(school, "ACT-EDU-01", 360L);
            int expected = stage == LifeStage.Child ? 20 : 25;
            Check(Resident(school).EducationPoints == expected,
                "SEIT-AC-05-school-catalog-progress-" + stage);
            Tick(school);
            Tick(school);
            Check(Resident(school).EducationPoints == expected,
                "SEIT-AC-05-school-applies-once-" + stage);
        }
    }

    private static void IdleSevenDays()
    {
        Fixture f = NewFixture();
        Complete(f, "ACT-FOOD-01", 45L);
        Complete(f, "ACT-EDU-03", 120L); // Partial course must not advance on time alone.
        f.World.SetEducation(Core, f.Resident, 25);
        f.World.SetCareer(Core, f.Resident, "Barista", 1, 20);
        string before = Values(f);
        long start = f.World.GameSeconds;
        f.Clock.SetSpeed(SimulationSpeed.Ultra);
        for (int day = 1; day <= 7; day++)
        {
            f.Clock.AdvanceRealSeconds(Core, 1440.0 / 7.0);
            Tick(f);
            Check(f.World.GameSeconds == start + day * 86400L && Values(f) == before,
                "SEIT-ECO-04-N1-idle-ultra-day-" + day);
        }
        f.Clock.SetSpeed(SimulationSpeed.Normal);
        for (int i = 0; i < 6; i++) Complete(f, "ACT-EDU-03", 120L);
        Check(Certificates(f) == 0, "SEIT-ECO-04-N1-idle-days-do-not-count-as-course-sessions");
        Complete(f, "ACT-EDU-03", 120L);
        Check(Certificates(f) == 1, "SEIT-ECO-04-N1-eighth-real-session-still-completes");
    }

    private static Fixture Reload(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(new GameStack(f.World, f.Clock, f.Arbitrator,
            f.Executor, f.Appearances, f.Accounts, f.Food), directory);
        Require(saved.Ok, "public save failed: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene { Station = f.Station });
        Require(loaded.Ok, "public load failed: " + loaded.Notice);
        LoadedGame game = loaded.Game;
        return new Fixture { World = game.World, Clock = game.Clock, Arbitrator = game.Arbitrator,
            Executor = game.Executor, Accounts = game.Accounts, Food = game.Food,
            Appearances = game.Appearances, Progression = new Progression(game.World),
            Resident = f.Resident, Station = f.Station };
    }

    private static void CourseGatesAndReload(string root)
    {
        Fixture f = NewFixture();
        // Independent BP-SEC-05 arithmetic: 100+200+300+400+500 + 599.
        f.World.AddSkillPoints(Core, f.Resident, "Cooking", 2099);
        Complete(f, "ACT-FOOD-01", 45L);
        Complete(f, "ACT-FOOD-01", 45L);
        Check(Cooking(f).Level == 5 && Certificates(f) == 0,
            "SEIT-ECO-04-N2-cooking-stays-at-five-without-certificate");
        f.World.SetCareer(Core, f.Resident, "Barista", 2, 80);
        Complete(f, "ACT-WORK-01", 360L);
        Check(Resident(f).CareerLevel == 2, "SEIT-AC-05-barista-level-three-course-gate");
        for (int i = 0; i < 7; i++)
        {
            Complete(f, "ACT-EDU-03", 120L);
            Tick(f);
            Tick(f);
            Check(Certificates(f) == 0, "SEIT-ECO-04-N2-course-incomplete-after-session-" + (i + 1));
        }
        f = Reload(f, Path.Combine(root, "partial-course"));
        Tick(f);
        Check(Certificates(f) == 0, "SEIT-AC-05-partial-course-restores-without-replay");
        ActionInstanceId session = Complete(f, "ACT-EDU-03", 120L);
        Check(Certificates(f) == 1, "SEIT-ECO-04-N2-eight-distinct-sessions-complete-certificate");
        Tick(f);
        Tick(f);
        Check(Certificates(f) == 1 && Committed(f, session) == 1,
            "SEIT-AC-05-course-applies-once");
        Complete(f, "ACT-FOOD-01", 45L);
        Check(Cooking(f).Level == 6, "SEIT-ECO-04-N2-certified-cooking-crosses-cap");
        f.World.SetCareer(Core, f.Resident, "Barista", 2, 80);
        Complete(f, "ACT-WORK-01", 360L);
        Check(Resident(f).CareerLevel == 3 && Resident(f).CareerPoints == 0,
            "SEIT-AC-05-certificate-unlocks-barista-three");
        string before = Values(f);
        f = Reload(f, Path.Combine(root, "completed-course"));
        Tick(f);
        Tick(f);
        Check(Values(f) == before && Certificates(f) == 1,
            "SEIT-AC-05-skill-career-education-and-course-round-trip-once");
    }

    private static void CompletionReload(string root)
    {
        Fixture f = NewFixture();
        Begin(f, "ACT-FOOD-01");
        Advance(f, 2694L);
        f = Reload(f, Path.Combine(root, "pre-completion"));
        Check(Cooking(f).Points == 0, "SEIT-ECO-04-N3-pre-completion-load-no-gain");
        Tick(f);
        Advance(f, 6L);
        Check(Cooking(f).Level == 0 && Cooking(f).Points == 10,
            "SEIT-ECO-04-N3-load-one-tick-before-completion-plus-10");
        string before = Values(f);
        f = Reload(f, Path.Combine(root, "post-completion"));
        Tick(f);
        Tick(f);
        Check(Values(f) == before && Cooking(f).Points == 10,
            "SEIT-ECO-04-N3-complete-save-reload-no-second-gain");
    }

    private static void CancelledActions(bool negative)
    {
        string[] codes = { "ACT-FOOD-01", "ACT-WORK-01", "ACT-EDU-01", "ACT-EDU-03" };
        long[] minutes = { 45L, 360L, 360L, 120L };
        for (int i = 0; i < codes.Length; i++)
        {
            Fixture f = NewFixture(LifeStage.Teen);
            // Seven real sessions make an accidental eighth completion observable.
            if (codes[i] == "ACT-EDU-03")
                for (int n = 0; n < 7; n++) Complete(f, codes[i], minutes[i]);
            string before = Values(f);
            ActionInstanceId action = Begin(f, codes[i]);
            if (negative) Advance(f, minutes[i] * 60L);
            else
            {
                Begin(f, "ACT-HOME-01"); // Real conflicting command interrupts the original.
                Require(f.Executor.GetExecution(action).IsTerminal
                    && f.Executor.GetExecution(action).Outcome != TerminalOutcome.Completed,
                    "fixture did not cancel original action");
                Advance(f, minutes[i] * 60L);
            }
            f.Food.Complete(f.Resident, action);
            Tick(f);
            Check(Values(f) == before, "SEIT-AC-05-cancelled-action-no-progress-" + codes[i]);
        }
    }

    public static int Main(string[] args)
    {
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        string root = Path.Combine(Path.GetTempPath(), "blockparty-progression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            if (negative) Run("cancelled-negative-fixture", () => CancelledActions(true));
            else
            {
                Run("completed-activities", CompletedActivities);
                Run("idle-seven-days", IdleSevenDays);
                Run("course-gates-and-reload", () => CourseGatesAndReload(root));
                Run("completion-reload", () => CompletionReload(root));
                Run("cancelled-actions", () => CancelledActions(false));
            }
        }
        catch (Exception e) { Check(false, "fixture " + e); }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        if (failures == 0) Console.WriteLine("PROGRESSION ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
