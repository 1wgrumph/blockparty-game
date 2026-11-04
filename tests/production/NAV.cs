// NAV production checks. Standalone console program: compiled and run with
// the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity test
// path. Positive checks prove the BP-ACT-01 stage lifecycle, exclusive slots,
// approach poses, busy waits (10-minute default abandon, 30-minute cook
// fail), occupied/unreachable/invalidated recovery and clock-driven motion.
// Negative checks prove failed validation takes no hold and refused holds
// consume nothing. Mirrors the COMMAND/CLOCK/STATE pattern.
using System;
using System.Collections.Generic;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class NavChecks
{
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const StateWriter Core = StateWriter.SimCore;

    private static int failures;
    private static long nextId = 2000;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static CommandId NextCommand()
    {
        nextId++;
        return (CommandId)nextId;
    }

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
    }

    private static Stack NewStack()
    {
        WorldState world = WorldState.CreateSeeded(7L, 42L, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        return new Stack
        {
            World = world,
            Clock = clock,
            Arbitrator = arbitrator,
            Executor = new ActivityExecutor(world, arbitrator, clock),
        };
    }

    private static ResidentRecord AddResident(WorldState world, string name, LifeStage stage)
    {
        ResidentRecord[] existing = world.ResidentsSnapshot();
        HouseholdId household;
        HomeId home;
        if (existing.Length == 0)
        {
            HomeRecord homeRecord = world.AllocateHome(Core, 8);
            HouseholdRecord householdRecord = world.AllocateHousehold(Core, "H", homeRecord.Id);
            household = householdRecord.Id;
            home = homeRecord.Id;
        }
        else
        {
            household = existing[0].Household;
            home = existing[0].Home;
        }
        return world.AllocateResident(Core, name, stage, household, home);
    }

    private static CommandResult Direct(Stack stack, ResidentRecord resident, string activity,
        QueueMode mode, long? targetId)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        return stack.Arbitrator.Submit(new CommandEnvelope(
            NextCommand(), stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            CommandKind.DirectActivity, resident.Id, new ActivityId(activity),
            targetId, new CommandParameters.Direct(mode)));
    }

    private static CommandResult Travel(Stack stack, ResidentRecord resident, long? targetId)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        return stack.Arbitrator.Submit(new CommandEnvelope(
            NextCommand(), stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            CommandKind.TravelTo, resident.Id, null,
            targetId, new CommandParameters.Travel(QueueMode.Immediate)));
    }

    private static ObjectId NewObject(Stack stack, params ApproachPose[] poses)
    {
        ObjectId id = stack.World.AllocateObject(Core);
        stack.Executor.RegisterObject(id, poses);
        return id;
    }

    private static void Step(Stack stack, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            stack.Clock.AdvanceRealSeconds(Core, 0.1);
            stack.Executor.Tick(Exec);
        }
    }

    private static ExecutionRecord CurrentExecution(Stack stack, ResidentId resident)
    {
        ResidentStatus status = stack.Executor.GetResidentStatus(resident);
        return status.Execution;
    }

    private static bool TraceIs(ExecutionRecord record, ExecutionStage[] stages, bool[] skipped)
    {
        IReadOnlyList<StageRecord> trace = record.Trace;
        if (trace.Count != stages.Length)
        {
            return false;
        }
        for (int i = 0; i < stages.Length; i++)
        {
            if (trace[i].Stage != stages[i] || trace[i].Skipped != skipped[i])
            {
                return false;
            }
            if (i > 0 && trace[i].GameSeconds < trace[i - 1].GameSeconds)
            {
                return false;
            }
        }
        return true;
    }

    private static bool NeverDoubleHeld(ActivityExecutor executor)
    {
        IReadOnlyList<SlotEvent> log = executor.ReservationLog;
        Dictionary<string, bool> held = new Dictionary<string, bool>();
        for (int i = 0; i < log.Count; i++)
        {
            string key = ((long)log[i].Hold.Object).ToString() + ":" + log[i].Hold.SlotIndex.ToString();
            if (log[i].Acquired)
            {
                bool busy;
                if (held.TryGetValue(key, out busy) && busy)
                {
                    return false;
                }
                held[key] = true;
            }
            else
            {
                held[key] = false;
            }
        }
        return true;
    }

    private static EffectLedgerEntry FindLedger(WorldState world, ActionInstanceId action)
    {
        List<EffectLedgerEntry> ledger = world.ExportSnapshot().EffectLedger;
        for (int i = 0; i < ledger.Count; i++)
        {
            if (ledger[i].Action == action)
            {
                return ledger[i];
            }
        }
        return null;
    }

    private static double Distance(ApproachPose a, ApproachPose b)
    {
        double dx = (double)a.X - (double)b.X;
        double dy = (double)a.Y - (double)b.Y;
        double dz = (double)a.Z - (double)b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // M4-D3: COMMAND/NAV round trips; baked-path following belongs to LOD-NAV.
    private static void BlockWorldRoundTrips()
    {
        foreach (string id in new[] { "apartment", "common-room", "cafe", "workshop", "office", "school" })
        {
            Stack stack = NewStack();
            var sites = new BlockSites(stack.World, stack.Executor);
            BlockSite site = null;
            foreach (BlockSite row in sites.Sites) if (row.Id == id) site = row;
            Check(site != null, "BLOCK-WORLD registered-entrance-" + id + " SEIT-WORLD-01-P1");
            if (site == null) continue;
            ResidentRecord resident = AddResident(stack.World, "Block walker", LifeStage.Adult);
            ApproachPose start = new ApproachPose(site.Entry.X - 60f, site.Entry.Y, site.Entry.Z);
            stack.Executor.SetResidentPosition(resident.Id, start);
            ObjectId returnTarget = NewObject(stack, start);
            foreach (ObjectId target in new[] { site.Object, returnTarget })
            {
                ApproachPose destination = target == site.Object ? site.Entry : start;
                ApproachPose before = stack.Executor.GetResidentPosition(resident.Id);
                long began = stack.World.GameSeconds;
                ResidentRecord current;
                stack.World.TryGetResident(resident.Id, out current);
                CommandResult result = stack.Arbitrator.Submit(new CommandEnvelope(NextCommand(), stack.World.Epoch,
                    began, CommandSource.PlayerHuman, current.ResidentSequence + 1u,
                    CommandKind.TravelTo, resident.Id, null, (long)target,
                    new CommandParameters.Travel(QueueMode.Immediate, TravelTargetKind.Object)));
                Check(result.Accepted && Distance(before, stack.Executor.GetResidentPosition(resident.Id)) == 0,
                    "BLOCK-WORLD no-submit-teleport-" + id + " SEIT-UI-03-P1");
                stack.Executor.Tick(Exec);
                ExecutionRecord run = CurrentExecution(stack, resident.Id);
                bool bounded = true;
                // Existing TravelEverySite budget covers the SEIT 60 m trip in simulated time.
                for (int i = 0; i < 2000 && run != null && !run.IsTerminal; i++)
                {
                    before = stack.Executor.GetResidentPosition(resident.Id);
                    long time = stack.World.GameSeconds;
                    Step(stack, 1);
                    bounded &= Distance(before, stack.Executor.GetResidentPosition(resident.Id))
                        <= ActivityExecutor.TravelMetersPerGameMinute * (stack.World.GameSeconds - time) / 60.0 + 0.00001;
                }
                Check(bounded && run != null && run.Outcome == TerminalOutcome.Completed
                    && stack.World.GameSeconds > began
                    && Distance(destination, stack.Executor.GetResidentPosition(resident.Id)) <= ActivityExecutor.ArrivalToleranceMeters,
                    "BLOCK-WORLD round-trip-" + id + " SEIT-UI-03-P1");
            }
        }
    }

    public static int Main()
    {
        BlockWorldRoundTrips();
        // 1. Full cook lifecycle: validate..release in order, exactly-once
        // commit, approach pose, hold released.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId stove = NewObject(stack, new ApproachPose(6.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Check(Direct(stack, r1, "ACT-FOOD-01", QueueMode.Immediate, (long)stove).Accepted, "cook-accepted");
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            ActionInstanceId action = record.Action;
            Check(record != null && !record.IsTerminal, "cook-dispatched");
            Step(stack, 600);
            Check(record.IsTerminal && record.Outcome == TerminalOutcome.Completed, "cook-completed");
            ExecutionStage[] stages = new ExecutionStage[]
            {
                ExecutionStage.Validate, ExecutionStage.Reserve, ExecutionStage.Travel,
                ExecutionStage.Align, ExecutionStage.Perform, ExecutionStage.Complete,
                ExecutionStage.Release,
            };
            Check(TraceIs(record, stages, new bool[7]), "cook-trace-in-order");
            Check(Distance(stack.Executor.GetResidentPosition(r1.Id),
                new ApproachPose(6.0f, 0.0f, 0.0f)) <= 0.001, "cook-arrived-at-pose");
            Check(!stack.Executor.GetHold(stove, 0).HasValue, "cook-hold-released");
            Check(!stack.World.CommitEffect(Core, action), "cook-commit-once-refuses-replay");
            Check(!stack.World.ReserveEffect(Core, action, "ACT-FOOD-01",
                new LedgerRecipient(r1.Id)), "cook-reserve-once-refuses-replay");
            IReadOnlyList<SlotEvent> log = stack.Executor.ReservationLog;
            Check(log.Count == 2 && log[0].Acquired && !log[1].Acquired
                && log[0].Hold == log[1].Hold
                && log[0].Hold.Object == stove && log[0].Hold.SlotIndex == 0
                && log[0].Hold.Holder == r1.Id && log[0].Hold.Action == action
                && Distance(log[0].Hold.Pose, new ApproachPose(6.0f, 0.0f, 0.0f)) <= 0.15,
                "cook-hold-record");
        }

        // 2. Motion follows the clock: bounded steps, no teleport, pause
        // freezes travel, resume finishes.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId desk = NewObject(stack, new ApproachPose(3.0f, 0.0f, 4.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-EDU-02", QueueMode.Immediate, (long)desk);
            stack.Executor.Tick(Exec);
            double maxStep = 0.0;
            for (int i = 0; i < 10; i++)
            {
                ApproachPose before = stack.Executor.GetResidentPosition(r1.Id);
                Step(stack, 1);
                double step = Distance(before, stack.Executor.GetResidentPosition(r1.Id));
                if (step > maxStep)
                {
                    maxStep = step;
                }
            }
            Check(maxStep <= 0.1 + 1e-4 && maxStep > 0.0, "travel-bounded-steps");
            Check(CurrentExecution(stack, r1.Id).Stage == ExecutionStage.Travel, "travel-in-progress");
            ApproachPose frozen = stack.Executor.GetResidentPosition(r1.Id);
            stack.Clock.Pause();
            for (int i = 0; i < 10; i++)
            {
                stack.Executor.Tick(Exec);
            }
            Check(Distance(frozen, stack.Executor.GetResidentPosition(r1.Id)) == 0.0
                && CurrentExecution(stack, r1.Id).Stage == ExecutionStage.Travel, "travel-pause-freezes");
            stack.Clock.Resume();
            Step(stack, 700);
            Check(CurrentExecution(stack, r1.Id) == null
                && stack.Executor.GetResidentStatus(r1.Id).IdleReason.StartsWith("Completed"),
                "travel-resume-completes");
        }

        // 3. Two-slot Chat holds both slots on one object.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId table = NewObject(stack,
                new ApproachPose(1.0f, 0.0f, 0.0f), new ApproachPose(1.0f, 0.0f, 1.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(1.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-REL-01", QueueMode.Immediate, (long)table);
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record.Holds.Length == 2
                && stack.Executor.GetHold(table, 0).HasValue
                && stack.Executor.GetHold(table, 1).HasValue, "chat-two-slots");
            Step(stack, 200);
            Check(record.Outcome == TerminalOutcome.Completed
                && !stack.Executor.GetHold(table, 0).HasValue
                && !stack.Executor.GetHold(table, 1).HasValue, "chat-completes-releases");
        }

        // 4. In-place activity skips reserve/travel/align, performs, completes.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record.Stage == ExecutionStage.Perform, "inplace-performs");
            Step(stack, 300);
            Check(record.Outcome == TerminalOutcome.Completed, "inplace-completes");
            ExecutionStage[] stages = new ExecutionStage[]
            {
                ExecutionStage.Validate, ExecutionStage.Reserve, ExecutionStage.Travel,
                ExecutionStage.Align, ExecutionStage.Perform, ExecutionStage.Complete,
                ExecutionStage.Release,
            };
            Check(TraceIs(record, stages,
                new bool[] { false, true, true, true, false, false, false }), "inplace-trace-skips");
        }

        // 5. TravelTo object: no hold, completes on arrival and starts Append.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId shop = NewObject(stack, new ApproachPose(2.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Check(Travel(stack, r1, (long)shop).Accepted, "travel-accepted");
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(Direct(stack, r1, "ACT-REL-04", QueueMode.Append, null).Accepted,
                "travel-append-accepted");
            Check(!stack.Executor.GetHold(shop, 0).HasValue
                && stack.Executor.ReservationLog.Count == 0, "travel-no-hold");
            Step(stack, 30);
            Check(record.Outcome == TerminalOutcome.Completed, "travel-arrives");
            ExecutionStage[] stages = new ExecutionStage[]
            {
                ExecutionStage.Validate, ExecutionStage.Reserve, ExecutionStage.Travel,
                ExecutionStage.Align, ExecutionStage.Perform, ExecutionStage.Complete,
                ExecutionStage.Release,
            };
            Check(TraceIs(record, stages,
                new bool[] { false, true, false, false, true, false, false }), "travel-trace");
            ExecutionRecord successor = CurrentExecution(stack, r1.Id);
            Check(successor != null && successor.Action != record.Action
                && successor.Activity.Code == "ACT-REL-04"
                && stack.Arbitrator.GetPending(r1.Id).Length == 0,
                "travel-append-dispatched");
            Step(stack, 150);
            Check(successor != null && successor.Outcome == TerminalOutcome.Completed
                && stack.Arbitrator.GetCurrent(r1.Id) == null,
                "travel-append-completes-idle");
        }

        // 6. TravelTo resident completes at the partner position.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(1.0f, 0.0f, 0.0f));
            Travel(stack, r1, (long)r2.Id);
            stack.Executor.Tick(Exec);
            Step(stack, 20);
            ResidentStatus status = stack.Executor.GetResidentStatus(r1.Id);
            Check(status.IsIdle && status.IdleReason == "Completed Travel"
                && Distance(stack.Executor.GetResidentPosition(r1.Id),
                    new ApproachPose(1.0f, 0.0f, 0.0f)) <= 0.06, "travel-to-resident");
        }

        // R13: the same number identifies a live resident and registered object.
        foreach (var kind in new[] { TravelTargetKind.Resident, TravelTargetKind.Object })
        {
            var stack = NewStack();
            var destination = AddResident(stack.World, "Destination", LifeStage.Adult);
            var walker = AddResident(stack.World, "Walker", LifeStage.Adult);
            var collision = NewObject(stack, new ApproachPose(50f, 0f, 0f));
            Check((long)collision == (long)destination.Id, "R13-real-target-id-collision");
            stack.Executor.SetResidentPosition(destination.Id, new ApproachPose(12f, 0f, 0f));
            stack.Executor.SetResidentPosition(walker.Id, new ApproachPose(0f, 0f, 0f));
            Check(stack.Arbitrator.Submit(new CommandEnvelope(NextCommand(), stack.World.Epoch,
                stack.World.GameSeconds, CommandSource.PlayerHuman, walker.ResidentSequence + 1u,
                CommandKind.TravelTo, walker.Id, null, (long)collision,
                new CommandParameters.Travel(QueueMode.Immediate, kind))).Accepted, "R13-tagged-travel-accepted-" + kind);
            stack.Executor.Tick(Exec);
            var execution = CurrentExecution(stack, walker.Id);
            if (kind == TravelTargetKind.Resident) stack.Executor.RemoveObject(collision);
            else stack.Executor.SetResidentPosition(destination.Id, new ApproachPose(100f, 0f, 0f));
            Step(stack, 600);
            Check(execution.Outcome == TerminalOutcome.Completed
                && Math.Abs(stack.Executor.GetResidentPosition(walker.Id).X - (kind == TravelTargetKind.Resident ? 12f : 50f)) < .06f,
                "R13-no-target-kind-fallback-" + kind);
        }

        // 7. Occupied chair: ObjectOccupied within 1 tick, FIFO acquire on
        // release, never two holders.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ObjectId chair = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(5.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-FOOD-02", QueueMode.Immediate, (long)chair);
            stack.Executor.Tick(Exec);
            Direct(stack, r2, "ACT-FOOD-02", QueueMode.Immediate, (long)chair);
            stack.Executor.Tick(Exec);
            ExecutionRecord waiting = CurrentExecution(stack, r2.Id);
            Check(waiting.BlockedReason == RejectReason.ObjectOccupied
                && waiting.QueuePosition == 1, "occupied-visible-in-one-tick");
            Step(stack, 50);
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            ExecutionRecord acquired = CurrentExecution(stack, r2.Id);
            Check(acquired != null && !acquired.Queued && acquired.Holds.Length == 1,
                "occupied-acquires-on-release");
            Step(stack, 500);
            Check(CurrentExecution(stack, r2.Id) == null
                && stack.Executor.GetResidentStatus(r2.Id).IdleReason.StartsWith("Completed"),
                "occupied-completes-after-wait");
            Check(NeverDoubleHeld(stack.Executor), "occupied-never-double-held");
        }

        // 8. Default 10-minute abandon: reason every queued sample, abandon at
        // the bound within 1 tick, nothing spent, next action is separate.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ObjectId easel = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(5.0f, 0.0f, 0.0f));
            long moneyBefore = r2.Money;
            float funBefore = r2.GetNeed(Need.Fun);
            Direct(stack, r1, "ACT-CREA-01", QueueMode.Immediate, (long)easel);
            stack.Executor.Tick(Exec);
            Direct(stack, r2, "ACT-CREA-01", QueueMode.Immediate, (long)easel);
            stack.Executor.Tick(Exec);
            ExecutionRecord waiting = CurrentExecution(stack, r2.Id);
            ActionInstanceId action = waiting.Action;
            long enqueuedAt = waiting.EnqueueGameSeconds;
            bool reasonAlways = true;
            bool earlyTerminal = false;
            long terminalAt = -1L;
            for (int i = 0; i < 130; i++)
            {
                Step(stack, 1);
                if (!waiting.IsTerminal)
                {
                    if (waiting.BlockedReason != RejectReason.ObjectOccupied)
                    {
                        reasonAlways = false;
                    }
                    if (stack.World.GameSeconds < enqueuedAt + 600L && waiting.IsTerminal)
                    {
                        earlyTerminal = true;
                    }
                }
                else if (terminalAt < 0L)
                {
                    terminalAt = stack.World.GameSeconds;
                }
            }
            Check(reasonAlways && !earlyTerminal, "abandon-reason-until-bound");
            Check(waiting.Outcome == TerminalOutcome.Abandoned, "abandon-outcome");
            Check(terminalAt >= enqueuedAt + 600L && terminalAt < enqueuedAt + 600L + 12L,
                "abandon-at-bound-within-one-tick");
            Check(stack.Executor.QueueLength(easel) == 0, "abandon-releases-queue-place");
            Check(FindLedger(stack.World, action) == null, "abandon-no-ledger");
            ResidentRecord after;
            stack.World.TryGetResident(r2.Id, out after);
            Check(after.Money == moneyBefore && after.GetNeed(Need.Fun) == funBefore,
                "abandon-nothing-spent");
            Direct(stack, r2, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            ExecutionRecord next = CurrentExecution(stack, r2.Id);
            Check(next != null && next.Action != action, "abandon-next-is-separate-action");
        }

        // 9. Cook keeps the explicit 30-minute wait, then fails with no
        // substitute.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ObjectId stove = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(5.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-FOOD-01", QueueMode.Immediate, (long)stove);
            stack.Executor.Tick(Exec);
            Direct(stack, r2, "ACT-FOOD-01", QueueMode.Immediate, (long)stove);
            stack.Executor.Tick(Exec);
            ExecutionRecord waiting = CurrentExecution(stack, r2.Id);
            Step(stack, 290);
            Check(!waiting.IsTerminal
                && waiting.BlockedReason == RejectReason.ObjectOccupied, "cook-waits-past-default");
            Step(stack, 30);
            Check(waiting.Outcome == TerminalOutcome.Failed
                && waiting.BlockedReason == RejectReason.ObjectOccupied, "cook-fails-at-30");
            Check(FindLedger(stack.World, waiting.Action) == null
                && CurrentExecution(stack, r2.Id) == null, "cook-fail-no-substitute");
        }

        // 10. Unreachable object: NoPath, no hold, no ledger.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId easel = NewObject(stack, new ApproachPose(1.0f, 0.0f, 0.0f));
            stack.Executor.SetReachable(easel, false);
            Direct(stack, r1, "ACT-CREA-01", QueueMode.Immediate, (long)easel);
            ActionInstanceId nopathAction = stack.Arbitrator.GetCurrent(r1.Id).Action;
            stack.Executor.Tick(Exec);
            ExecutionRecord record = stack.Executor.GetExecution(nopathAction);
            Check(record.Outcome == TerminalOutcome.Failed
                && record.BlockedReason == RejectReason.NoPath, "nopath-reason");
            Check(!stack.Executor.GetHold(easel, 0).HasValue
                && stack.Executor.ReservationLog.Count == 0
                && FindLedger(stack.World, record.Action) == null, "nopath-no-hold-no-ledger");
        }

        // 11. Removal mid-travel invalidates the hold with TargetInvalidated.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId desk = NewObject(stack, new ApproachPose(5.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-EDU-02", QueueMode.Immediate, (long)desk);
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record.Stage == ExecutionStage.Travel && record.Holds.Length == 1, "removed-was-travelling");
            stack.Executor.RemoveObject(desk);
            Check(record.Outcome == TerminalOutcome.Failed
                && record.BlockedReason == RejectReason.TargetInvalidated
                && record.Holds.Length == 0
                && FindLedger(stack.World, record.Action) == null, "removed-invalidates");
        }

        // 12. Move mid-travel invalidates; the new pose works after.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId sofa = NewObject(stack, new ApproachPose(5.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-HEALTH-02", QueueMode.Immediate, (long)sofa);
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            stack.Executor.MoveObject(sofa, new ApproachPose[] { new ApproachPose(1.0f, 0.0f, 0.0f) });
            Check(record.Outcome == TerminalOutcome.Failed
                && record.BlockedReason == RejectReason.TargetInvalidated, "moved-invalidates");
            Direct(stack, r1, "ACT-HEALTH-02", QueueMode.Immediate, (long)sofa);
            stack.Executor.Tick(Exec);
            Step(stack, 200);
            ExecutionRecord retry = CurrentExecution(stack, r1.Id);
            Check(retry != null && retry.Stage == ExecutionStage.Perform
                && Distance(stack.Executor.GetResidentPosition(r1.Id),
                    new ApproachPose(1.0f, 0.0f, 0.0f)) <= 0.06, "moved-new-pose-works");
        }

        // 13. Interruption releases the slot within one tick and suppresses
        // the completion commit.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId desk = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-EDU-02", QueueMode.Immediate, (long)desk);
            stack.Executor.Tick(Exec);
            Step(stack, 5);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record.Stage == ExecutionStage.Perform, "interrupt-was-performing");
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            Check(record.Outcome == TerminalOutcome.Interrupted
                && !stack.Executor.GetHold(desk, 0).HasValue, "interrupt-releases-in-one-tick");
            EffectLedgerEntry entry = FindLedger(stack.World, record.Action);
            Check(entry != null && entry.State == LedgerState.Reserved, "interrupt-suppresses-commit");
            Step(stack, 10);
            Check(FindLedger(stack.World, record.Action).State == LedgerState.Reserved,
                "interrupt-stays-suppressed");
        }

        // 14. Load reconciliation keeps only the live hold, no orphans.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ObjectId a = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            ObjectId b = NewObject(stack, new ApproachPose(9.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(9.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-EDU-02", QueueMode.Immediate, (long)a);
            Direct(stack, r2, "ACT-EDU-02", QueueMode.Immediate, (long)b);
            stack.Executor.Tick(Exec);
            ActionInstanceId liveAction = CurrentExecution(stack, r1.Id).Action;
            ActionInstanceId deadAction = CurrentExecution(stack, r2.Id).Action;
            stack.Executor.ReconcileAfterLoad(new ActionInstanceId[] { liveAction });
            Check(stack.Executor.GetHold(a, 0).HasValue
                && !stack.Executor.GetHold(b, 0).HasValue
                && stack.Executor.GetExecution(liveAction) != null
                && stack.Executor.GetExecution(deadAction) == null, "reconcile-keeps-live-only");
            Check(NeverDoubleHeld(stack.Executor), "reconcile-no-duplicate");
        }

        // 15. Idle always carries a recorded reason.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentStatus fresh = stack.Executor.GetResidentStatus(r1.Id);
            Check(fresh.IsIdle && fresh.IdleReason == "AwaitingDecision", "idle-fresh-reason");
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            Step(stack, 300);
            ResidentStatus done = stack.Executor.GetResidentStatus(r1.Id);
            Check(done.IsIdle && done.IdleReason == "Completed ACT-HOME-01", "idle-after-complete");
            Step(stack, 20);
            ResidentStatus later = stack.Executor.GetResidentStatus(r1.Id);
            Check(later.IsIdle && later.IdleReason.Length > 0, "idle-reason-persists");
        }

        // 16. Sleep is open-ended until Energy 100.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId bed = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.World.SetNeed(Core, r1.Id, Need.Energy, 40.0f);
            Direct(stack, r1, "ACT-HEALTH-01", QueueMode.Immediate, (long)bed);
            stack.Executor.Tick(Exec);
            Step(stack, 700);
            Check(CurrentExecution(stack, r1.Id).Stage == ExecutionStage.Perform, "sleep-open-ended");
            stack.World.SetNeed(Core, r1.Id, Need.Energy, 100.0f);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r1.Id) == null, "sleep-completes-at-full-energy");
        }

        // 17. Minigolf never ends on time alone.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ObjectId course = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-SPORT-02", QueueMode.Immediate, (long)course);
            stack.Executor.Tick(Exec);
            Step(stack, 700);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record != null && !record.IsTerminal
                && record.Stage == ExecutionStage.Perform, "minigolf-no-time-end");
        }

        // 18. Same-tick contention: exactly one holder across trials.
        {
            bool ok = true;
            for (int trial = 0; trial < 20 && ok; trial++)
            {
                Stack stack = NewStack();
                ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
                ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
                ObjectId stove = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
                stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
                stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
                Direct(stack, r1, "ACT-FOOD-01", QueueMode.Immediate, (long)stove);
                Direct(stack, r2, "ACT-FOOD-01", QueueMode.Immediate, (long)stove);
                stack.Executor.Tick(Exec);
                int holders = CurrentExecution(stack, r1.Id).Holds.Length
                    + CurrentExecution(stack, r2.Id).Holds.Length;
                int queued = (CurrentExecution(stack, r1.Id).Queued ? 1 : 0)
                    + (CurrentExecution(stack, r2.Id).Queued ? 1 : 0);
                ok = holders == 1 && queued == 1 && NeverDoubleHeld(stack.Executor);
            }
            Check(ok, "contention-single-holder-20-trials");
        }

        // 19. FIFO across three residents.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ResidentRecord r3 = AddResident(stack.World, "R3", LifeStage.Adult);
            ObjectId shower = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r3.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-HEALTH-03", QueueMode.Immediate, (long)shower);
            Direct(stack, r2, "ACT-HEALTH-03", QueueMode.Immediate, (long)shower);
            Direct(stack, r3, "ACT-HEALTH-03", QueueMode.Immediate, (long)shower);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r2.Id).QueuePosition == 1
                && CurrentExecution(stack, r3.Id).QueuePosition == 2, "fifo-positions");
            Step(stack, 50);
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r2.Id).Holds.Length == 1
                && CurrentExecution(stack, r3.Id).QueuePosition == 1, "fifo-head-acquires-first");
            Step(stack, 10);
            Direct(stack, r2, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r3.Id).Holds.Length == 1, "fifo-second-acquires-next");
        }

        // 20. Placement codes complete at perform entry.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            Direct(stack, r1, "ACT-CREA-03", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            ResidentStatus status = stack.Executor.GetResidentStatus(r1.Id);
            Check(status.IsIdle && status.IdleReason == "Completed ACT-CREA-03",
                "placement-completes-same-tick");
        }

        // 21. No static mutable state on NAV types.
        {
            Check(!HasStaticMutable(typeof(ActivityExecutor))
                && !HasStaticMutable(typeof(ExecutionRecord))
                && !HasStaticMutable(typeof(ResidentStatus))
                && !HasStaticMutable(typeof(SlotEvent)), "no-static-mutable");
        }

        // 22. Failed validation takes no hold, no travel, no ledger write.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Teen);
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            ObjectId real = NewObject(stack, new ApproachPose(4.0f, 0.0f, 0.0f));
            long ghost = 999983L;
            ActionEntry unknown = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.DirectActivity, new ActivityId("ACT-NOPE-99"), (long)real,
                ActionOwner.Player, CommandSource.PlayerHuman);
            ActionEntry missing = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.DirectActivity, new ActivityId("ACT-EDU-02"), ghost,
                ActionOwner.Player, CommandSource.PlayerHuman);
            ActionEntry noTravel = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.TravelTo, new ActivityId(null), null,
                ActionOwner.Player, CommandSource.PlayerHuman);
            ActionEntry pause = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.Pause, new ActivityId(null), null,
                ActionOwner.Player, CommandSource.PlayerHuman);
            ActionEntry dead = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.DirectActivity, new ActivityId("ACT-EDU-02"), (long)real,
                ActionOwner.Player, CommandSource.PlayerHuman);
            stack.World.SetAlive(Core, r2.Id, false);
            int hashBefore = stack.World.ComputeStateHash();
            ExecutionRecord badActivity = stack.Executor.Dispatch(r1.Id, unknown);
            ExecutionRecord badTarget = stack.Executor.Dispatch(r1.Id, missing);
            ExecutionRecord badTravel = stack.Executor.Dispatch(r1.Id, noTravel);
            ExecutionRecord badKind = stack.Executor.Dispatch(r1.Id, pause);
            ExecutionRecord badDead = stack.Executor.Dispatch(r2.Id, dead);
            Check(badActivity.BlockedReason == RejectReason.UnknownActivity
                && badTarget.BlockedReason == RejectReason.TargetMissing
                && badTravel.BlockedReason == RejectReason.TargetMissing
                && badKind.BlockedReason == RejectReason.InvalidParameters
                && badDead.BlockedReason == RejectReason.ResidentDeceased, "validation-typed-reasons");
            ExecutionRecord[] all = new ExecutionRecord[]
                { badActivity, badTarget, badTravel, badKind, badDead };
            bool clean = true;
            for (int i = 0; i < all.Length && clean; i++)
            {
                IReadOnlyList<StageRecord> trace = all[i].Trace;
                for (int j = 0; j < trace.Count && clean; j++)
                {
                    if (!trace[j].Skipped && (trace[j].Stage == ExecutionStage.Reserve
                        || trace[j].Stage == ExecutionStage.Travel))
                    {
                        clean = false;
                    }
                }
                clean = clean && all[i].Holds.Length == 0
                    && FindLedger(stack.World, all[i].Action) == null;
            }
            Check(clean, "validation-no-hold-no-travel-no-ledger");
            Check(stack.Executor.ReservationLog.Count == 0, "validation-no-reservation");
            Check(Distance(stack.Executor.GetResidentPosition(r1.Id),
                new ApproachPose(0.0f, 0.0f, 0.0f)) == 0.0, "validation-no-motion");
            Check(stack.World.ComputeStateHash() == hashBefore, "validation-world-untouched");
        }

        // 23. Refused holds consume nothing; failed current advances freshness.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ResidentRecord r3 = AddResident(stack.World, "R3", LifeStage.Adult);
            ObjectId held = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            ObjectId blocked = NewObject(stack, new ApproachPose(8.0f, 0.0f, 0.0f));
            stack.Executor.SetReachable(blocked, false);
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r3.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-CREA-01", QueueMode.Immediate, (long)held);
            stack.Executor.Tick(Exec);
            Direct(stack, r2, "ACT-CREA-01", QueueMode.Immediate, (long)held);
            Direct(stack, r3, "ACT-CREA-01", QueueMode.Immediate, (long)blocked);
            ActionEntry gone = new ActionEntry(NextCommand(), stack.World.AllocateAction(Core),
                CommandKind.DirectActivity, new ActivityId("ACT-CREA-01"), 999979L,
                ActionOwner.Player, CommandSource.PlayerHuman);
            stack.Executor.Dispatch(r1.Id, gone);
            WorldState expected = WorldState.LoadFromSnapshot(stack.World.ExportSnapshot());
            expected.TouchDecisionSlot(StateWriter.CommandArbitrator, r3.Id);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r2.Id).BlockedReason == RejectReason.ObjectOccupied
                && CurrentExecution(stack, r3.Id) == null, "refused-reasons-set");
            WorldState actual = WorldState.LoadFromSnapshot(stack.World.ExportSnapshot());
            Check(actual.ComputeStateHash() == expected.ComputeStateHash(),
                "refused-world-only-decision-generation-changes");
        }

        // 24. Invalid inputs throw at the boundary.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            bool nullEntry = false;
            bool unknownObject = false;
            bool unknownSlot = false;
            bool unknownResident = false;
            bool wrongWriter = false;
            bool nullReconcile = false;
            bool emptyPoses = false;
            bool duplicateRegister = false;
            try
            {
                stack.Executor.Dispatch(r1.Id, null);
            }
            catch (ArgumentNullException)
            {
                nullEntry = true;
            }
            ObjectId id = stack.World.AllocateObject(Core);
            try
            {
                stack.Executor.GetHold(stack.World.AllocateObject(Core), 0);
            }
            catch (ArgumentException)
            {
                unknownObject = true;
            }
            stack.Executor.RegisterObject(id, new ApproachPose[] { new ApproachPose(0.0f, 0.0f, 0.0f) });
            try
            {
                stack.Executor.GetHold(id, 3);
            }
            catch (ArgumentException)
            {
                unknownSlot = true;
            }
            try
            {
                stack.Executor.SetResidentPosition((ResidentId)999999L, new ApproachPose(0.0f, 0.0f, 0.0f));
            }
            catch (ArgumentException)
            {
                unknownResident = true;
            }
            try
            {
                stack.Executor.Tick(Core);
            }
            catch (ArgumentException)
            {
                wrongWriter = true;
            }
            try
            {
                stack.Executor.ReconcileAfterLoad(null);
            }
            catch (ArgumentNullException)
            {
                nullReconcile = true;
            }
            try
            {
                stack.Executor.MoveObject(id, new ApproachPose[0]);
            }
            catch (ArgumentException)
            {
                emptyPoses = true;
            }
            try
            {
                stack.Executor.RegisterObject(id, new ApproachPose[] { new ApproachPose(1.0f, 0.0f, 0.0f) });
            }
            catch (ArgumentException)
            {
                duplicateRegister = true;
            }
            Check(nullEntry && unknownObject && unknownSlot && unknownResident
                && wrongWriter && nullReconcile && emptyPoses && duplicateRegister, "invalid-inputs-throw");
        }

        // 25. Retry keeps FIFO order: a second release round still ordered.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            ResidentRecord r2 = AddResident(stack.World, "R2", LifeStage.Adult);
            ObjectId sink = NewObject(stack, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r1.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            stack.Executor.SetResidentPosition(r2.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
            Direct(stack, r1, "ACT-HEALTH-03", QueueMode.Immediate, (long)sink);
            stack.Executor.Tick(Exec);
            Direct(stack, r2, "ACT-HEALTH-03", QueueMode.Immediate, (long)sink);
            stack.Executor.Tick(Exec);
            Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null);
            stack.Executor.Tick(Exec);
            Check(CurrentExecution(stack, r2.Id).Holds.Length == 1
                && CurrentExecution(stack, r1.Id).Stage == ExecutionStage.Perform,
                "interrupt-passes-slot-to-waiter");
        }

        // 26. Natural completion releases the arbitrator current and starts
        // the queued Append exactly once (COMMAND completion regression).
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            Check(Direct(stack, r1, "ACT-HOME-01", QueueMode.Immediate, null).Accepted
                && Direct(stack, r1, "ACT-REL-04", QueueMode.Append, null).Accepted,
                "completion-setup-accepted");
            stack.Executor.Tick(Exec);
            ExecutionRecord first = CurrentExecution(stack, r1.Id);
            ActionInstanceId firstAction = first.Action;
            Step(stack, 300);
            Check(first.Outcome == TerminalOutcome.Completed, "completion-first-completes");
            ActionEntry nowCurrent = stack.Arbitrator.GetCurrent(r1.Id);
            Check(nowCurrent != null && nowCurrent.Activity.Code == "ACT-REL-04",
                "completion-append-promoted");
            Check(stack.Arbitrator.GetPending(r1.Id).Length == 0, "completion-pending-drained-once");
            stack.Executor.Tick(Exec);
            ExecutionRecord second = CurrentExecution(stack, r1.Id);
            Check(second != null && second.Action != firstAction
                && second.Activity.Code == "ACT-REL-04", "completion-append-dispatched");
            Step(stack, 150);
            Check(second != null && second.Outcome == TerminalOutcome.Completed
                && stack.Arbitrator.GetCurrent(r1.Id) == null, "completion-second-completes-idle");
        }

        // 27. Idle Append starts at the next unpaused tick without a driver hook.
        {
            Stack stack = NewStack();
            ResidentRecord r1 = AddResident(stack.World, "R1", LifeStage.Adult);
            stack.Clock.Pause();
            Check(Direct(stack, r1, "ACT-REL-04", QueueMode.Append, null).Accepted,
                "idle-append-accepted");
            stack.Executor.Tick(Exec);
            Check(stack.Arbitrator.GetCurrent(r1.Id) == null
                && stack.Arbitrator.GetPending(r1.Id).Length == 1
                && CurrentExecution(stack, r1.Id) == null, "idle-append-paused-waits");
            stack.Clock.Resume();
            stack.Executor.Tick(Exec);
            ExecutionRecord record = CurrentExecution(stack, r1.Id);
            Check(record != null && record.Activity.Code == "ACT-REL-04"
                && stack.Arbitrator.GetPending(r1.Id).Length == 0,
                "idle-append-starts-without-promote");
            Step(stack, 150);
            Check(record != null && record.Outcome == TerminalOutcome.Completed
                && stack.Arbitrator.GetCurrent(r1.Id) == null, "idle-append-completes");
        }

        if (failures == 0)
        {
            Console.WriteLine("NAV ALL PASS");
        }
        else
        {
            Console.WriteLine("NAV FAILURES: " + failures.ToString());
        }
        return failures;
    }

    private static bool HasStaticMutable(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!fields[i].IsInitOnly && !fields[i].IsLiteral)
            {
                return true;
            }
        }
        return false;
    }
}
