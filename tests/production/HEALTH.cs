/* HEALTH@1 TDD contract (product: Assets/Blockparty/Activities/Health.cs).
Blockparty.Activities.Health exposes:
  Health(WorldState world, CommandArbitrator arbitrator, ActivityExecutor executor)
  CommandResult Submit(CommandEnvelope command)
  void Tick(StateWriter writer)
Submit enforces health eligibility before forwarding to the real arbitrator;
Tick drives the real executor and health effects (do not tick both externally).
All mutations use WorldState's authorized writers. No replacement product here.

Oracles: canonical SDoc BP-ACT-09 and section 5 catalog, SEIT-ACT-09-P1/N1.
Catalog numbers are unmeasured candidates, not medical or tuning evidence.
No PopulationScheduler is installed: no unrelated awake need decay is integrated.
The sleep +12/hour rate must therefore produce 20 -> 32 after one game hour.
Flags are seeded through the public WorldState API, not via private fields;
the illness/fatigue fixture values are the SEIT inputs (Health 15, Energy 8).
The injury flag represents the stored storm exposure, not a new injury source.
Save coverage uses the public SAVE-V2 codec, without pinning its schema number.

New suite required by HEALTH's package selector; existing FOOD/NAV suites cover
their own effects/execution, not health restrictions or catalog consequences.
Resource group: isolated-csharp; no editor/display/network/shared ports. Only
the save test creates files, in an owned pid+unique temp directory with cleanup.
SEIT-AC-04 coverage is the health catalog's local completion/interruption claim;
normal-controls demonstration and whole-case acceptance remain at their gates.
negative-fixture replaces one blocked-shift result with Accept: the SAME refusal
assertion must print FAIL and return 1. This is an oracle check, not product code.
*/
using System;
using System.Diagnostics;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class HealthChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;
    private static bool negativeFixture;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Health Health;
        public ResidentId Resident;
        public ObjectId Station;
    }

    private sealed class Scene : ISceneContent
    {
        private readonly ObjectId station;
        public Scene(ObjectId station) { this.station = station; }
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(station, new[] { new ApproachPose(0f, 0f, 0f) });
        }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e.GetType().Name + ": " + e.Message); }
    }

    private static Fixture NewFixture()
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 42L, 0, null);
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Health household", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Resident", LifeStage.Adult, household.Id, home.Id).Id;
        for (int i = 0; i < StateSchema.NeedCount; i++)
            f.World.SetNeed(Core, f.Resident, (Need)i, 40f);
        // Already checked on day zero: no daily illness chance draw is due.
        f.World.SetHealthFlags(Core, f.Resident, 0f, false, false, false, 0);
        f.Clock = new Clock(f.World);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Health = new Health(f.World, f.Arbitrator, f.Executor);
        f.Station = f.World.AllocateObject(Core);
        new Scene(f.Station).RegisterObjects(f.Executor);
        f.Executor.SetResidentPosition(f.Resident, new ApproachPose(0f, 0f, 0f));
        return f;
    }

    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord r;
        if (!f.World.TryGetResident(f.Resident, out r)) throw new Exception("Fixture resident missing");
        return r;
    }

    private static CommandEnvelope Direct(Fixture f, string code)
    {
        return new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, Resident(f).ResidentSequence + 1u,
            CommandKind.DirectActivity, f.Resident, new ActivityId(code), (long)f.Station,
            new CommandParameters.Direct(QueueMode.Immediate));
    }

    private static ActionInstanceId Begin(Fixture f, string code)
    {
        CommandResult result = f.Health.Submit(Direct(f, code));
        if (!result.Accepted) throw new Exception("Fixture activity refused: " + code + " " + result.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Resident).Action;
        f.Health.Tick(Exec);
        f.Health.Tick(Exec);
        if (f.Executor.GetExecution(action).Stage != ExecutionStage.Perform)
            throw new Exception("Fixture did not reach Perform: " + code);
        return action;
    }

    private static void Advance(Fixture f, int seconds)
    {
        for (int elapsed = 0; elapsed < seconds; elapsed += 6)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Health.Tick(Exec);
        }
    }

    private static void Cancel(Fixture f)
    {
        CommandResult result = f.Health.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            Resident(f).ResidentSequence + 1u, CommandKind.CancelCurrent,
            f.Resident, null, null, new CommandParameters.None()));
        if (!result.Accepted) throw new Exception("Fixture cancellation refused: " + result.Reason);
        f.Health.Tick(Exec);
    }

    private static bool Near(float actual, float expected) { return Math.Abs(actual - expected) < 0.02f; }

    private static int Committed(Fixture f, ActionInstanceId action)
    {
        int count = 0;
        foreach (EffectLedgerEntry row in f.World.EffectLedger)
            if (row.Action == action && row.State == LedgerState.Committed) count++;
        return count;
    }

    private static int Fitness(Fixture f)
    {
        foreach (SkillEntry row in Resident(f).Skills)
            if (row.SkillId == "Fitness") return row.Points;
        return 0;
    }

    private static bool CompletedOnce(Fixture f, ActionInstanceId action)
    {
        ExecutionRecord execution = f.Executor.GetExecution(action);
        return execution.IsTerminal && execution.Outcome == TerminalOutcome.Completed
            && Committed(f, action) == 1 && f.Executor.GetHold(f.Station, 0) == null
            && f.Arbitrator.GetCurrent(f.Resident) == null;
    }

    private static void Sleep()
    {
        Fixture f = NewFixture();
        f.World.SetNeed(Core, f.Resident, Need.Energy, 20f);
        ActionInstanceId action = Begin(f, "ACT-HEALTH-01");
        Advance(f, 3600);
        Check(Near(Resident(f).GetNeed(Need.Energy), 32f)
            && Resident(f).GetNeed(Need.Health) == 40f && Committed(f, action) == 0,
            "SEIT-ACT-09-P1 Sleep-hour-rate-replaces-awake-decay-no-early-bonus");
        Advance(f, 6 * 3600);
        Check(Resident(f).GetNeed(Need.Energy) == 100f && Resident(f).GetNeed(Need.Health) == 41f
            && CompletedOnce(f, action), "SEIT-ACT-09-P1 Sleep-seven-hours-capped-plus-one-health");
        string before = f.World.ComputeStateHash().ToString();
        f.Health.Tick(Exec);
        f.Health.Tick(Exec);
        Check(f.World.ComputeStateHash().ToString() == before && Committed(f, action) == 1,
            "SEIT-ACT-09-P1 Sleep-no-duplicate-completion-effects");

        Fixture interrupted = NewFixture();
        interrupted.World.SetNeed(Core, interrupted.Resident, Need.Energy, 20f);
        ActionInstanceId shortSleep = Begin(interrupted, "ACT-HEALTH-01");
        Advance(interrupted, 5 * 3600);
        Cancel(interrupted);
        Advance(interrupted, 2 * 3600);
        Check(Near(Resident(interrupted).GetNeed(Need.Energy), 80f)
            && Resident(interrupted).GetNeed(Need.Health) == 40f && Committed(interrupted, shortSleep) == 0
            && interrupted.Executor.GetExecution(shortSleep).Outcome == TerminalOutcome.Interrupted,
            "SEIT-ACT-09-N1 SEIT-AC-04 Sleep-five-hours-interrupted-no-health-bonus");
    }

    private static void Catalog()
    {
        foreach (string code in new[] { "ACT-SPORT-01", "ACT-HEALTH-02" })
        {
            Fixture f = NewFixture();
            ActionInstanceId action = Begin(f, code);
            int duration = code == "ACT-SPORT-01" ? 2700 : 7200;
            Advance(f, duration - 6);
            Check(Resident(f).GetNeed(Need.Health) == 40f && Resident(f).GetNeed(Need.Energy) == 40f
                && Resident(f).GetNeed(Need.Hygiene) == 40f && Resident(f).GetNeed(Need.Comfort) == 40f
                && Fitness(f) == 0 && Committed(f, action) == 0,
                "SEIT-ACT-09-P1 " + code + "-no-early-completion-effects");
            Advance(f, 6);
            bool exercise = code == "ACT-SPORT-01";
            Check(Resident(f).GetNeed(Need.Health) == (exercise ? 43f : 48f)
                && Resident(f).GetNeed(Need.Energy) == (exercise ? 25f : 50f)
                && Resident(f).GetNeed(Need.Hygiene) == (exercise ? 30f : 40f)
                && Resident(f).GetNeed(Need.Comfort) == (exercise ? 40f : 65f)
                && Fitness(f) == (exercise ? 15 : 0) && CompletedOnce(f, action),
                "SEIT-ACT-09-P1 SEIT-AC-04 " + code + "-catalog-deltas-once");
            string before = f.World.ComputeStateHash().ToString();
            f.Health.Tick(Exec);
            f.Health.Tick(Exec);
            Check(f.World.ComputeStateHash().ToString() == before && Committed(f, action) == 1,
                "SEIT-ACT-09-P1 " + code + "-no-duplicate-effects");

            Fixture cancelled = NewFixture();
            ActionInstanceId stopped = Begin(cancelled, code);
            Advance(cancelled, duration - 6);
            Cancel(cancelled);
            Advance(cancelled, 12);
            Check(Resident(cancelled).GetNeed(Need.Health) == 40f
                && Resident(cancelled).GetNeed(Need.Energy) == 40f
                && Resident(cancelled).GetNeed(Need.Hygiene) == 40f
                && Resident(cancelled).GetNeed(Need.Comfort) == 40f
                && Fitness(cancelled) == 0 && Committed(cancelled, stopped) == 0,
                "SEIT-AC-04 " + code + "-interrupted-no-completion-benefit");
        }
    }

    private static void SetFlag(Fixture f, string flag)
    {
        f.World.SetNeed(Core, f.Resident, Need.Health, flag == "Ill" ? 15f : 35f);
        f.World.SetNeed(Core, f.Resident, Need.Energy, flag == "Fatigued" ? 8f : 40f);
        f.World.SetHealthFlags(Core, f.Resident, 0f, flag == "Ill", flag == "Fatigued", flag == "Injured", 0);
    }

    private static void Restrictions()
    {
        foreach (string code in new[] { "ACT-WORK-01", "ACT-WORK-02", "ACT-SPORT-01" })
        {
            Fixture healthy = NewFixture();
            Check(healthy.Health.Submit(Direct(healthy, code)).Accepted,
                "SEIT-ACT-09-N1 healthy-control-" + code);
            foreach (string flag in new[] { "Ill", "Fatigued", "Injured" })
            {
                Fixture f = NewFixture();
                SetFlag(f, flag);
                CommandEnvelope command = Direct(f, code);
                string before = f.World.ComputeStateHash().ToString();
                CommandResult result = f.Health.Submit(command);
                if (negativeFixture && flag == "Ill" && code == "ACT-WORK-01")
                    result = CommandResult.Accept();
                Check(!result.Accepted && !result.Superseded
                    && Enum.IsDefined(typeof(RejectReason), result.Reason)
                    && !string.IsNullOrWhiteSpace(result.Reason.ToString())
                    && f.Arbitrator.GetCurrent(f.Resident) == null
                    && f.Arbitrator.GetPending(f.Resident).Length == 0
                    && f.World.ComputeStateHash().ToString() == before,
                    "SEIT-ACT-09-N1 " + flag + "-blocks-" + code + "-visible-reason-no-benefit");
            }
        }
        foreach (string flag in new[] { "Ill", "Fatigued", "Injured" })
            foreach (string code in new[] { "ACT-HEALTH-01", "ACT-HEALTH-02", "ACT-HEALTH-03", "ACT-FOOD-02" })
            {
                Fixture f = NewFixture();
                SetFlag(f, flag);
                Check(f.Health.Submit(Direct(f, code)).Accepted,
                    "SEIT-ACT-09-N1 " + flag + "-recovery-remains-available-" + code);
            }
    }

    private static void Recovery()
    {
        Fixture f = NewFixture();
        f.World.SetNeed(Core, f.Resident, Need.Health, 35f);
        f.World.SetNeed(Core, f.Resident, Need.Energy, 15f);
        f.World.SetHealthFlags(Core, f.Resident, 0f, true, true, true, 0);
        ActionInstanceId action = Begin(f, "ACT-HEALTH-02");
        Advance(f, 7200);
        ResidentRecord r = Resident(f);
        Check(r.GetNeed(Need.Health) == 43f && r.GetNeed(Need.Energy) == 25f
            && r.GetNeed(Need.Comfort) == 65f && !r.Ill && !r.Fatigued && !r.Injured
            && CompletedOnce(f, action), "SEIT-ACT-09-P1 RestRecover-clears-flags-at-catalog-thresholds");
        Check(f.Health.Submit(Direct(f, "ACT-WORK-01")).Accepted,
            "SEIT-ACT-09-N1 recovered-resident-can-start-work");
    }

    private static void SaveFlags()
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-health-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture f = NewFixture();
            f.World.SetNeed(Core, f.Resident, Need.Health, 15f);
            f.World.SetNeed(Core, f.Resident, Need.Energy, 8f);
            f.World.SetHealthFlags(Core, f.Resident, 2f, true, true, true, 0);
            GameStack stack = new GameStack(f.World, f.Clock, f.Arbitrator, f.Executor, new AppearanceStore());
            SaveWriteResult saved = SaveV1.SaveGame(stack, directory);
            if (!saved.Ok) throw new Exception("Save failed: " + saved.Detail);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene(f.Station));
            if (!loaded.Ok) throw new Exception("Load failed: " + loaded.Notice);
            f.World = loaded.Game.World;
            f.Clock = loaded.Game.Clock;
            f.Arbitrator = loaded.Game.Arbitrator;
            f.Executor = loaded.Game.Executor;
            f.Health = new Health(f.World, f.Arbitrator, f.Executor);
            ResidentRecord r = Resident(f);
            Check(r.Ill && r.Fatigued && r.Injured && r.LastChanceDrawDay == 0
                && r.StarvationHours == 2f && r.GetNeed(Need.Health) == 15f && r.GetNeed(Need.Energy) == 8f,
                "SEIT-ACT-09-N1 flags-and-daily-marker-public-save-round-trip");
            Check(!f.Health.Submit(Direct(f, "ACT-WORK-01")).Accepted
                && !f.Health.Submit(Direct(f, "ACT-SPORT-01")).Accepted,
                "SEIT-ACT-09-N1 reloaded-flags-still-block-work-and-exercise");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void RepeatedEntry()
    {
        string expected = null;
        for (int run = 0; run < 2; run++)
        {
            Fixture f = NewFixture();
            Check(f.Arbitrator.GetCurrent(f.Resident) == null && f.World.EffectLedger.Count == 0,
                "SEIT-ACT-09-P1 fresh-entry-" + run);
            ActionInstanceId action = Begin(f, "ACT-SPORT-01");
            Advance(f, 2700);
            string observed = f.World.ComputeStateHash().ToString() + ":" + f.World.RngState.ToString();
            Check(CompletedOnce(f, action) && Fitness(f) == 15 && (expected == null || expected == observed),
                "SEIT-ACT-09-P1 repeated-seed-42-deterministic-entry-" + run);
            expected = observed;
        }
    }

    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = NewFixture();
            ActionInstanceId action = Begin(f, "ACT-SPORT-01");
            f.Clock.AdvanceRealSeconds(Core, 45.0, () => f.Health.Tick(writer));
            string actual = f.World.ComputeStateHash().ToString();
            Check(CompletedOnce(f, action) && Fitness(f) == 15
                && (expected == null || actual == expected),
                "HEALTH-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    public static int Main(string[] args)
    {
        negativeFixture = args.Length == 1 && args[0] == "negative-fixture";
        Console.WriteLine("HEALTH fixture world=7 seed=42 game-day=0 daily-draw-already-recorded; no random draws due");
        Run("Sleep", Sleep);
        Run("Catalog", Catalog);
        Run("Restrictions", Restrictions);
        Run("Recovery", Recovery);
        Run("SaveFlags", SaveFlags);
        Run("RepeatedEntry", RepeatedEntry);
        Run("health-tick-writer-contract", AcceptedWriterTick);
        if (failures == 0) Console.WriteLine("HEALTH ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
