/*
TIERS@1 test-first API contract; product file (TIERS writes):
Assets/Blockparty/Simulation/PopulationScheduler.cs, namespace Blockparty.Simulation.

enum SimulationTier { Detailed, Reduced }
sealed class TierContext:
  TierContext(ResidentId[] visible, ResidentId? selected, ResidentId? followed,
              ResidentId[][] sharedGroups)
  Arrays describe the camera context and shared action/reservation participants.
sealed class TierCostSnapshot (read-only values):
  int Population, DetailedCount, ReducedCount;
  long DetailedWorkUnits, ReducedWorkUnits;
  Work units count actual authoritative resident integrations in the LAST fixed
  tick (one per living resident), not time estimates or claimed CPU savings.
sealed class PopulationScheduler:
  PopulationScheduler(WorldState, Clock, CommandArbitrator, ActivityExecutor, Autonomy)
  void Recompute(TierContext) -- membership only; no authoritative state mutation.
  SimulationTier GetTier(ResidentId)
  TierCostSnapshot Costs { get; }
  void AdvanceRealSeconds(StateWriter, double realSeconds, TierContext)
    Validates before mutation; runs every due six-game-second tick via the real
    Clock, integrates needs and due effects once, and drives the real executor
    and autonomy. Recomputes membership after accepted commands, before each
    integration. Idle Reduced planning waits at most 60 game seconds and wakes
    on urgent needs/availability/events. No frame or wall-clock based decisions.
    Invalid writer/time/context/unknown resident: ArgumentException (including
    subclasses) with a reason, preserving world, clock, queues and scheduler.

Existing APIs: Blockparty.Contracts boundary types; Blockparty.Simulation
WorldState/StateWriter, Clock, ResidentFactory, AppearanceStore, CommandArbitrator,
ActivityExecutor/ExecutionRecord and Autonomy; Blockparty.Persistence
GameStack, SaveV1, SaveWriteResult, SaveLoadResult, LoadedGame, ISceneContent.
No test-side simulation adapter or fake executor. Fixtures use real producers.
Resource group: isolated-csharp; no Unity editor, display, service or fixed port.

Local portions of SIM-03-P1/N1, IF-TIME-P3/N6, PERF-03-P1/P2/N1/N2/N3,
IF-BUILD-P2/N4 are exercised below. Full SEIT completion is NOT claimed:
PERF-03-P2/IF-BUILD-P2 need CPU time on named hardware/packaged build, not these
work counters; AC-21/AC-21-A1 need second-person launch and all five budgets,
API usage and sustained-session evidence; AC-02 needs the late-provider/follow
view scenario. Camera/shared-group inputs here do not qualify presentation or
the real shared-action producer. STATE@1 exposes no GroceryUnit stock API, so
the cook fixture proves timing/needs/ledger only, not FOOD stock reservation.
Urgent planning is covered; availability,
target-invalidation and social/event wake integration remain full-gate duties.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class TiersChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Autonomy Autonomy;
        public AppearanceStore Appearances;
        public PopulationScheduler Scheduler;
        public ResidentRecord[] Residents;
    }

    private static Stack Bind(WorldState world, Clock clock, CommandArbitrator arbitrator,
        ActivityExecutor executor, AppearanceStore appearances)
    {
        Autonomy autonomy = new Autonomy(world, arbitrator);
        return new Stack { World = world, Clock = clock, Arbitrator = arbitrator,
            Executor = executor, Autonomy = autonomy, Appearances = appearances,
            Scheduler = new PopulationScheduler(world, clock, arbitrator, executor, autonomy),
            Residents = world.ResidentsSnapshot() };
    }

    private static Stack NewStack(int population)
    {
        WorldState world = WorldState.CreateSeeded(7L, 42L, population, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        return Bind(world, clock, arbitrator, new ActivityExecutor(world, arbitrator, clock),
            new AppearanceStore());
    }

    private static TierContext Context(ResidentId[] visible, ResidentId? selected = null,
        ResidentId? followed = null, ResidentId[][] shared = null)
    {
        return new TierContext(visible, selected, followed, shared ?? new ResidentId[0][]);
    }

    private static TierContext Hidden()
    {
        return Context(new ResidentId[0]);
    }

    private static ResidentId[] Ids(Stack s)
    {
        ResidentId[] ids = new ResidentId[s.Residents.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = s.Residents[i].Id;
        return ids;
    }

    private static void StartAutonomous(Stack s, ResidentId id, string code, long? target = null)
    {
        if (!s.Arbitrator.EnqueueAutonomous(id, new ActivityId(code), target)
            || !s.Arbitrator.PromoteNext(id))
            throw new InvalidOperationException("Real autonomous fixture refused " + code);
        s.Executor.Tick(Exec);
    }

    private static CommandResult PlayerAction(Stack s, ResidentId id)
    {
        ResidentRecord resident;
        s.World.TryGetResident(id, out resident);
        return s.Arbitrator.Submit(new CommandEnvelope(s.World.AllocateCommand(Core),
            s.World.Epoch, s.World.GameSeconds, CommandSource.PlayerHuman,
            resident.ResidentSequence + 1u, CommandKind.DirectActivity, id,
            new ActivityId("ACT-PARTY-01"), null, new CommandParameters.Direct(QueueMode.Immediate)));
    }

    private static void Membership()
    {
        Stack s = NewStack(12);
        ResidentId[] r = Ids(s);
        TierContext context = Context(new ResidentId[] { r[0] }, r[1], r[2],
            new ResidentId[][] { new ResidentId[] { r[4], r[5] },
                new ResidentId[] { r[3], r[4] }, new ResidentId[] { r[0], r[3] } });
        int before = s.World.ComputeStateHash();
        s.Scheduler.Recompute(context);
        bool assigned = true;
        for (int i = 0; i < r.Length; i++)
            assigned &= s.Scheduler.GetTier(r[i]) == (i < 6 ? SimulationTier.Detailed : SimulationTier.Reduced);
        Check(assigned && s.Scheduler.Costs.Population == 12
            && s.Scheduler.Costs.DetailedCount == 6 && s.Scheduler.Costs.ReducedCount == 6
            && before == s.World.ComputeStateHash(),
            "SEIT-PERF-03-P1-visible-selected-followed-transitive-sharing");

        s.Scheduler.Recompute(Context(new ResidentId[] { r[6] }));
        bool moved = true;
        for (int i = 0; i < r.Length; i++)
            moved &= s.Scheduler.GetTier(r[i]) == (i == 6 ? SimulationTier.Detailed : SimulationTier.Reduced);
        Check(moved && before == s.World.ComputeStateHash(),
            "SEIT-PERF-03-N2-camera-change-releases-old-membership-without-effects");

        Check(PlayerAction(s, r[9]).Accepted, "SEIT-IF-TIME-P3-real-player-command-accepted");
        s.Scheduler.AdvanceRealSeconds(Core, 0.1, Hidden());
        ExecutionRecord execution = s.Executor.GetResidentStatus(r[9]).Execution;
        Check(s.Scheduler.GetTier(r[9]) == SimulationTier.Detailed
            && s.Scheduler.Costs.DetailedCount == 1 && execution != null
            && execution.Trace[0].GameSeconds <= 6L && s.World.GameSeconds == 6L,
            "SEIT-IF-TIME-P3-accepting-tick-integrates-player-as-detailed");
    }

    // Catalog oracle: awake hourly decay from BP-SEC-05, not product outputs.
    private static double Decay(int need)
    {
        switch ((Need)need)
        {
            case Need.Hunger: return 4.0;
            case Need.Energy: return 3.0;
            case Need.Bladder: return 6.0;
            case Need.Hygiene: return 2.0;
            case Need.Social: return 2.0;
            case Need.Fun: return 2.0;
            case Need.Comfort: return 1.0;
            default: return 0.0;
        }
    }

    private static bool NeedsMatch(ResidentRecord resident, long seconds, bool cook, long cookStart,
        float? hungerAnchor = null, long anchorSeconds = 0L)
    {
        for (int n = 0; n < StateSchema.NeedCount; n++)
        {
            bool anchored = cook && n == (int)Need.Hunger && hungerAnchor.HasValue;
            double initial = anchored ? (double)hungerAnchor.Value
                : cook && n == (int)Need.Hunger ? 30.0 : 80.0;
            double gain = 0.0;
            if (cook && seconds >= cookStart + 2700L)
            {
                if (n == (int)Need.Hunger) gain = 45.0;
                if (n == (int)Need.Health) gain = 2.0;
            }
            long elapsed = anchored ? seconds - anchorSeconds : seconds;
            float expected = (float)(initial + gain - Decay(n) * (double)elapsed / 3600.0);
            if (Math.Abs((double)resident.GetNeed((Need)n) - (double)expected) > 1e-6) return false;
        }
        return true;
    }

    private sealed class StoveScene : ISceneContent
    {
        public ObjectId Stove;
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(Stove, new ApproachPose[] { new ApproachPose(0.0f, 0.0f, 0.0f) });
        }
    }

    private static ExecutionRecord PrepareCook(Stack s)
    {
        ResidentId cook = s.Residents[7].Id;
        // Also supports the SEIT tick-10 planning witness: its independently
        // prepared start state includes the first minute's elapsed decay.
        for (int i = 0; i < s.Residents.Length; i++)
            for (int n = 0; n < StateSchema.NeedCount; n++)
            {
                double initial = i == 7 && n == (int)Need.Hunger ? 30.0 : 80.0;
                s.World.SetNeed(Core, s.Residents[i].Id, (Need)n,
                    (float)(initial - Decay(n) * (double)s.World.GameSeconds / 3600.0));
            }
        ObjectId stove = s.World.AllocateObject(Core);
        new StoveScene { Stove = stove }.RegisterObjects(s.Executor);
        StartAutonomous(s, cook, "ACT-FOOD-01", (long)stove);
        ExecutionRecord record = s.Executor.GetResidentStatus(cook).Execution;
        if (record == null || record.Stage != ExecutionStage.Perform)
            throw new InvalidOperationException("Cook fixture did not enter Perform at its recorded start");
        // Keep the next activity identical in both schedules, avoiding new idle
        // choices whose permitted latency would invalidate exact comparisons.
        if (!s.Arbitrator.EnqueueAutonomous(cook, new ActivityId("ACT-PARTY-01"), null))
            throw new InvalidOperationException("Cook continuation refused");
        for (int i = 0; i < s.Residents.Length; i++)
            if (i != 7) StartAutonomous(s, s.Residents[i].Id, "ACT-PARTY-01");
        return record;
    }

    private static long StageTime(ExecutionRecord record, ExecutionStage stage)
    {
        for (int i = 0; i < record.Trace.Count; i++)
            if (record.Trace[i].Stage == stage && !record.Trace[i].Skipped) return record.Trace[i].GameSeconds;
        return -1L;
    }

    private static string SeededRun(int population)
    {
        Stack detailed = NewStack(population);
        Stack reduced = NewStack(population);
        ExecutionRecord a = PrepareCook(detailed);
        ExecutionRecord b = PrepareCook(reduced);
        long startA = StageTime(a, ExecutionStage.Perform);
        long startB = StageTime(b, ExecutionStage.Perform);
        TierContext all = Context(Ids(detailed));
        StringBuilder trace = new StringBuilder();
        bool oracle = startA == 0L && startB == 0L;
        bool equal = true;
        bool counter = true;
        bool offscreen = true;
        bool age = true;
        for (int tick = 1; tick <= 1200; tick++)
        {
            detailed.Scheduler.AdvanceRealSeconds(Core, 0.1, all);
            reduced.Scheduler.AdvanceRealSeconds(Core, 0.1, Hidden());
            long seconds = (long)tick * 6L;
            oracle &= detailed.World.GameSeconds == seconds && reduced.World.GameSeconds == seconds
                && detailed.Clock.TotalTicks == (long)tick && reduced.Clock.TotalTicks == (long)tick;
            for (int i = 0; i < population; i++)
            {
                ResidentRecord ra;
                ResidentRecord rb;
                detailed.World.TryGetResident(detailed.Residents[i].Id, out ra);
                reduced.World.TryGetResident(reduced.Residents[i].Id, out rb);
                oracle &= NeedsMatch(ra, seconds, i == 7, startA)
                    && NeedsMatch(rb, seconds, i == 7, startB);
                age &= Math.Abs((double)ra.StageProgressDays - (double)seconds / 86400.0) <= 1e-6
                    && ra.StageProgressDays == rb.StageProgressDays;
                offscreen &= reduced.Scheduler.GetTier(rb.Id) == SimulationTier.Reduced;
            }
            equal &= detailed.World.ComputeStateHash() == reduced.World.ComputeStateHash();
            TierCostSnapshot da = detailed.Scheduler.Costs;
            TierCostSnapshot dr = reduced.Scheduler.Costs;
            counter &= da.Population == population && da.DetailedCount == population && da.ReducedCount == 0
                && dr.Population == population && dr.DetailedCount == 0 && dr.ReducedCount == population
                && da.DetailedWorkUnits == (long)population && da.ReducedWorkUnits == 0L
                && dr.DetailedWorkUnits == 0L && dr.ReducedWorkUnits == (long)population;
            trace.Append(reduced.World.ComputeStateHash()).Append(':').Append(dr.ReducedWorkUnits).Append(';');
            if (tick == 500)
            {
                ResidentRecord cook;
                reduced.World.TryGetResident(reduced.Residents[7].Id, out cook);
                Check(offscreen && b.Outcome == TerminalOutcome.Completed
                    && StageTime(b, ExecutionStage.Complete) == startB + 2700L
                    && NeedsMatch(cook, 3000L, true, startB),
                    "SEIT-SIM-03-P1-offscreen-cook-45min-hunger-at-50min-pop" + population);
            }
        }
        Check(oracle && age, "SEIT-SIM-03-N1-independent-per-tick-needs-clock-aging-pop" + population);
        Check(equal && StageTime(a, ExecutionStage.Complete) == StageTime(b, ExecutionStage.Complete),
            "SEIT-IF-TIME-N6-matched-schedules-no-skipped-ticks-or-completions-pop" + population);
        Check(offscreen && reduced.World.EffectLedger.Count == population + 1
            && !reduced.World.CommitEffect(Core, b.Action),
            "SEIT-PERF-03-N2-reduced-throughout-and-completion-cannot-replay-pop" + population);
        Check(counter, "SEIT-PERF-03-P2-SEIT-IF-BUILD-P2-local-work-counts-pop" + population);
        Console.WriteLine("EVIDENCE seed=42 population=" + population + " ticks=1200 cookStarts="
            + startA + "," + startB + " costUnit=resident-integration (CPU time unmeasured)");
        return trace.ToString();
    }

    private static void DelayedPlanningWitness()
    {
        // A's tick-0 start/completion at tick 450 is asserted in SeededRun.
        // Prepare B through the real clock at tick 10, then run B against its
        // own schedule oracle. Different legal completion times are allowed.
        Stack s = NewStack(8);
        s.Clock.AdvanceRealSeconds(Core, 1.0);
        ExecutionRecord cook = PrepareCook(s);
        long start = StageTime(cook, ExecutionStage.Perform);
        ResidentRecord setup;
        s.World.TryGetResident(s.Residents[7].Id, out setup);
        // Anchor the externally reset Hunger at its stored float32 value; keep other oracles unchanged.
        float hungerAnchor = setup.GetNeed(Need.Hunger);
        bool oracle = start == 60L;
        for (int tick = 11; tick <= 1200; tick++)
        {
            s.Scheduler.AdvanceRealSeconds(Core, 0.1, Hidden());
            ResidentRecord resident;
            s.World.TryGetResident(s.Residents[7].Id, out resident);
            oracle &= s.World.GameSeconds == (long)tick * 6L
                && NeedsMatch(resident, (long)tick * 6L, true, start, hungerAnchor, start);
            if (tick < 460) oracle &= !cook.IsTerminal;
            else oracle &= cook.Outcome == TerminalOutcome.Completed;
        }
        Check(oracle && StageTime(cook, ExecutionStage.Complete) == 2760L,
            "SEIT-SIM-03-N1-legal-tick10-start-completes-tick460-own-oracle");
    }

    private static void Growth()
    {
        AppearanceStore appearances;
        WorldState world = ResidentFactory.CreateNewGame(7L, 42L, null, out appearances);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Stack s = Bind(world, clock, arbitrator, new ActivityExecutor(world, arbitrator, clock), appearances);
        ResidentRecord original = s.Residents[0];
        // Real state allocation into a new home with sufficient beds; the
        // eight-bed initial household is a household limit, not a world cap.
        HomeRecord home = world.AllocateHome(Core, 8);
        HouseholdRecord household = world.AllocateHousehold(Core, "Arrivals", home.Id);
        ResidentRecord ninth = world.AllocateResident(Core, "Ninth", LifeStage.Adult, household.Id, home.Id);
        ResidentRecord tenth = world.AllocateResident(Core, "Tenth", LifeStage.Adult, household.Id, home.Id);
        StartAutonomous(s, ninth.Id, "ACT-PARTY-01");
        StartAutonomous(s, tenth.Id, "ACT-PARTY-01");
        s.Scheduler.AdvanceRealSeconds(Core, 0.1, Context(new ResidentId[] { original.Id }));
        ResidentRecord r9;
        ResidentRecord r10;
        world.TryGetResident(ninth.Id, out r9);
        world.TryGetResident(tenth.Id, out r10);
        Check(world.ResidentCount == 10 && s.Scheduler.Costs.Population == 10
            && s.Scheduler.GetTier(ninth.Id) == SimulationTier.Reduced
            && s.Scheduler.GetTier(tenth.Id) == SimulationTier.Reduced
            && NeedsMatch(r9, 6L, false, 0L) && NeedsMatch(r10, 6L, false, 0L)
            && r9.StageProgressDays > 0.0f && r10.StageProgressDays > 0.0f,
            "SEIT-PERF-03-N1-SEIT-IF-BUILD-N4-ninth-tenth-accepted-and-simulated");
    }

    private static string Fingerprint(Stack s)
    {
        TierCostSnapshot c = s.Scheduler.Costs;
        return s.World.ComputeStateHash() + ":" + s.World.WriteAudit.Count + ":"
            + s.Clock.TotalTicks + ":" + s.Clock.TickRemainder + ":" + s.Autonomy.Decisions.Count
            + ":" + s.Autonomy.RngState + ":" + s.Arbitrator.CommandLog.Count
            + ":" + c.Population + ":" + c.DetailedCount + ":" + c.ReducedCount
            + ":" + c.DetailedWorkUnits + ":" + c.ReducedWorkUnits;
    }

    private static void RejectAdvance(double seconds, StateWriter writer, bool unknownContext, string name)
    {
        Stack s = NewStack(12);
        for (int i = 0; i < s.Residents.Length; i++)
            StartAutonomous(s, s.Residents[i].Id, "ACT-PARTY-01");
        s.Scheduler.Recompute(Hidden());
        string before = Fingerprint(s);
        bool refused = false;
        try
        {
            TierContext context = unknownContext ? Context(new ResidentId[] { (ResidentId)999999L }) : Hidden();
            s.Scheduler.AdvanceRealSeconds(writer, seconds, context);
        }
        catch (ArgumentException error) { refused = !string.IsNullOrWhiteSpace(error.Message); }
        bool tiers = true;
        for (int i = 0; i < s.Residents.Length; i++)
            tiers &= s.Scheduler.GetTier(s.Residents[i].Id) == SimulationTier.Reduced
                && s.Arbitrator.GetCurrent(s.Residents[i].Id).Activity.Code == "ACT-PARTY-01"
                && s.Arbitrator.GetPending(s.Residents[i].Id).Length == 0;
        Check(refused && tiers && before == Fingerprint(s), name);
    }

    private static void PauseAndPlanning()
    {
        Stack s = NewStack(12);
        for (int i = 0; i < s.Residents.Length; i++)
            StartAutonomous(s, s.Residents[i].Id, "ACT-PARTY-01");
        s.Scheduler.Recompute(Hidden());
        int before = s.World.ComputeStateHash();
        s.Clock.Pause();
        s.Scheduler.AdvanceRealSeconds(Core, 120.0, Hidden());
        Check(before == s.World.ComputeStateHash() && s.Clock.TotalTicks == 0L
            && s.Autonomy.Decisions.Count == 0,
            "SEIT-IF-TIME-N6-pause-freezes-both-tiers");

        Stack idle = NewStack(12);
        for (int tick = 0; tick < 10; tick++) idle.Scheduler.AdvanceRealSeconds(Core, 0.1, Hidden());
        bool bounded = true;
        for (int i = 0; i < idle.Residents.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < idle.Autonomy.Decisions.Count; j++)
                if (idle.Autonomy.Decisions[j].Resident == idle.Residents[i].Id
                    && idle.Autonomy.Decisions[j].GameSeconds <= 60L) found = true;
            bounded &= found;
        }
        Check(bounded, "SEIT-SIM-03-N1-reduced-idle-choice-recorded-within-one-minute");

        Stack urgent = NewStack(12);
        urgent.World.SetNeed(Core, urgent.Residents[0].Id, Need.Hunger, 8.0f);
        urgent.Scheduler.AdvanceRealSeconds(Core, 0.1, Hidden());
        bool woke = false;
        for (int i = 0; i < urgent.Autonomy.Decisions.Count; i++)
            if (urgent.Autonomy.Decisions[i].Resident == urgent.Residents[0].Id
                && urgent.Autonomy.Decisions[i].GameSeconds <= 6L
                && urgent.Autonomy.Decisions[i].Trigger == "Remedy:Hunger") woke = true;
        Check(woke, "SEIT-SIM-03-N1-urgent-need-wakes-reduced-on-first-tick");
    }

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private static void LoadRecomputes()
    {
        // Only this case owns this directory; real SaveV1 I/O, cleaned on failure.
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-tiers-" + Guid.NewGuid().ToString("N"));
        try
        {
            Stack s = NewStack(12);
            for (int i = 0; i < s.Residents.Length; i++)
                StartAutonomous(s, s.Residents[i].Id, "ACT-PARTY-01");
            s.Scheduler.AdvanceRealSeconds(Core, 1.0, Hidden());
            ResidentId id = s.Residents[7].Id;
            ResidentRecord before;
            s.World.TryGetResident(id, out before);
            SaveWriteResult saved = SaveV1.SaveGame(new GameStack(s.World, s.Clock,
                s.Arbitrator, s.Executor, s.Appearances), directory);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Check(saved.Ok && loaded.Ok, "SEIT-PERF-03-N3-real-save-load-fixture");
            if (!saved.Ok || !loaded.Ok) return;
            LoadedGame g = loaded.Game;
            Stack restored = Bind(g.World, g.Clock, g.Arbitrator, g.Executor, g.Appearances);
            int loadedHash = g.World.ComputeStateHash();
            restored.Scheduler.Recompute(Context(new ResidentId[] { id }));
            ResidentRecord after;
            g.World.TryGetResident(id, out after);
            bool continuity = before.Id == after.Id && before.StageProgressDays == after.StageProgressDays
                && s.World.GameSeconds == g.World.GameSeconds && s.World.RngState == g.World.RngState;
            for (int n = 0; n < StateSchema.NeedCount; n++)
                continuity &= before.GetNeed((Need)n) == after.GetNeed((Need)n);
            Check(continuity && loadedHash == g.World.ComputeStateHash()
                && restored.Scheduler.GetTier(id) == SimulationTier.Detailed
                && restored.Scheduler.Costs.DetailedCount == 1 && restored.Scheduler.Costs.ReducedCount == 11,
                "SEIT-PERF-03-N3-continuity-preserved-camera-tier-recomputed");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static bool HasStaticMutable(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; i++)
            if (!fields[i].IsLiteral && !fields[i].IsInitOnly) return true;
        Type[] nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < nested.Length; i++) if (HasStaticMutable(nested[i])) return true;
        return false;
    }

    public static int Main()
    {
        Membership();
        string eight = SeededRun(8);
        string twentyFour = SeededRun(24);
        Check(eight == SeededRun(8) && twentyFour == SeededRun(24),
            "SEIT-SIM-03-N1-seed42-repeated-entry-deterministic-traces-and-work");
        DelayedPlanningWitness();
        Growth();
        RejectAdvance(-0.1, Core, false, "SEIT-IF-TIME-N6-negative-time-refused-state-unchanged");
        RejectAdvance(double.NaN, Core, false, "SEIT-IF-TIME-N6-nan-time-refused-state-unchanged");
        RejectAdvance(double.PositiveInfinity, Core, false, "SEIT-IF-TIME-N6-infinite-time-refused-state-unchanged");
        RejectAdvance(0.1, (StateWriter)999, false, "SEIT-IF-TIME-N6-invalid-writer-refused-state-unchanged");
        RejectAdvance(0.1, Core, true, "SEIT-PERF-03-N2-unknown-camera-resident-refused-state-unchanged");
        PauseAndPlanning();
        LoadRecomputes();
        Check(!HasStaticMutable(typeof(PopulationScheduler)) && !HasStaticMutable(typeof(TierContext))
            && !HasStaticMutable(typeof(TierCostSnapshot)),
            "SEIT-SIM-03-N1-no-static-mutable-product-state");
        failures += M1Regressions.Run("TIERS");
        Console.WriteLine(failures == 0 ? "TIERS ALL PASS" : "TIERS FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
