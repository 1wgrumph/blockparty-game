// NAV@1: the activity executor (ACT-EXEC, INT-EXEC + INT-SLOT, SM-ACT).
// Consumes IFC-LOCK@1 (SlotHold, ApproachPose, RejectReason, SimTick),
// STATE@1 (WorldState, StateWriter, effect ledger), CLOCK@1 (Clock) and
// COMMAND@1 (CommandArbitrator, ActionEntry, InterruptionRecord). Plain C# 9,
// no UnityEngine dependency. Every authoritative mutation flows through an
// audited WorldState method with StateWriter.ActivityExecutor (BP-SYS-02);
// slot holds, queues, positions and traces are this instance's runtime state,
// like the arbitrator's queues; SAVE persists them later.
// Fast Enter Play Mode: no static mutable state (static tables are readonly),
// explicit numeric conversions, no AppDomain, Assembly.Location,
// BinaryFormatter, wall-clock reads or String.GetHashCode.
// Normative source: BP-ACT-01 (validate, reserve, travel, align, perform,
// complete|fail, release in order), BP-ACT-02 + BP-WORLD-05 + BP-SEC-05
// catalog + OWNER-BUSY-WAIT-20261027 (default occupied wait 10 game minutes
// then abandon to normal decision-making; ACT-FOOD-01 keeps its explicit 30
// then fail; release on completion, interruption, failure, load
// reconciliation and object move/removal; typed BlockedReason; no resource
// consumed for a refused hold) and BP-SEC-07 INT-SLOT (exclusive hold until
// the matching release).
// Motion follows the clock: positions advance only from game-time deltas at
// the candidate speed below, never teleport; pause freezes travel, perform
// and waits while dispatch, refusal and release still process. Needs, money,
// grocery stock and catalog gains belong to successor packages (FOOD,
// economy); NAV reserves/commits the exactly-once ledger marker only and
// touches no need, money or stock, so refused holds consume nothing by
// construction. Every natural terminal (completed, failed, abandoned)
// reports through CommandArbitrator.CompleteCurrent, which releases the
// current action and starts the pending head; Dispatch is idempotent, so no
// terminal-id tracking is needed here.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public enum ExecutionStage
    {
        Validate = 0,
        Reserve = 1,
        Travel = 2,
        Align = 3,
        Perform = 4,
        Complete = 5,
        Fail = 6,
        Release = 7,
    }

    public enum TerminalOutcome
    {
        Completed = 0,
        Failed = 1,
        Abandoned = 2,
        Interrupted = 3,
    }

    public readonly struct StageRecord
    {
        public readonly ExecutionStage Stage;
        public readonly long GameSeconds;
        public readonly bool Skipped;

        public StageRecord(ExecutionStage stage, long gameSeconds, bool skipped)
        {
            Stage = stage;
            GameSeconds = gameSeconds;
            Skipped = skipped;
        }
    }

    public sealed class SlotEvent
    {
        public readonly long GameSeconds;
        public readonly bool Acquired;
        public readonly SlotHold Hold;

        public SlotEvent(long gameSeconds, bool acquired, SlotHold hold)
        {
            GameSeconds = gameSeconds;
            Acquired = acquired;
            Hold = hold;
        }
    }

    // One executed action: traversed stages stay in BP-ACT-01 order and every
    // stage is recorded exactly once (Skipped when it does not apply), so a
    // trace always reads validate, reserve, travel, align, perform,
    // complete|fail, release with non-decreasing game seconds.
    public sealed class ExecutionRecord
    {
        private readonly List<StageRecord> trace = new List<StageRecord>();
        private readonly List<SlotHold> holds = new List<SlotHold>();

        public ActionInstanceId Action { get; private set; }
        public ResidentId Resident { get; private set; }
        public ActivityId Activity { get; private set; }
        public CommandKind Kind { get; private set; }
        public long? TargetId { get; private set; }
        public TravelTargetKind TargetKind { get; private set; }
        public ExecutionStage Stage { get; private set; }
        public RejectReason? BlockedReason { get; private set; }
        public int QueuePosition { get; private set; }
        public TerminalOutcome? Outcome { get; private set; }

        public IReadOnlyList<StageRecord> Trace { get { return trace.AsReadOnly(); } }
        public SlotHold[] Holds { get { return holds.ToArray(); } }
        public bool IsTerminal { get { return Outcome.HasValue; } }

        internal long EnqueueGameSeconds { get; set; }
        internal long PerformStartGameSeconds { get; set; }
        // Read-only view for observers: perform progress restored by SAVE survives reload.
        public long PerformStartedAtGameSeconds { get { return PerformStartGameSeconds; } }
        internal bool Queued { get; set; }
        internal bool LedgerReserved { get; set; }

        internal ExecutionRecord(ActionInstanceId action, ResidentId resident,
            ActivityId activity, CommandKind kind, long? targetId,
            TravelTargetKind targetKind = TravelTargetKind.Unspecified)
        {
            Action = action;
            Resident = resident;
            Activity = activity;
            Kind = kind;
            TargetId = targetId;
            TargetKind = targetKind;
            Stage = ExecutionStage.Validate;
            EnqueueGameSeconds = -1L;
            PerformStartGameSeconds = -1L;
        }

        internal void AddStage(ExecutionStage stage, long gameSeconds, bool skipped)
        {
            trace.Add(new StageRecord(stage, gameSeconds, skipped));
            if (!skipped)
            {
                Stage = stage;
            }
        }

        internal void SetBlocked(RejectReason? reason, int queuePosition)
        {
            BlockedReason = reason;
            QueuePosition = queuePosition;
        }

        internal void AddHold(SlotHold hold)
        {
            holds.Add(hold);
        }

        internal void ClearHolds()
        {
            holds.Clear();
        }

        internal void Terminate(TerminalOutcome outcome, long gameSeconds, bool completed)
        {
            AddStage(completed ? ExecutionStage.Complete : ExecutionStage.Fail, gameSeconds, false);
            AddStage(ExecutionStage.Release, gameSeconds, false);
            Outcome = outcome;
        }
    }

    // Per-resident status: the in-flight execution, or the idle record. The
    // idle reason is never empty, so no resident idles without a recorded
    // reason (BP-ACT-02).
    public sealed class ResidentStatus
    {
        public readonly ResidentId Resident;
        public readonly ExecutionRecord Execution;
        public readonly string IdleReason;
        public readonly long IdleSinceGameSeconds;

        public ResidentStatus(ResidentId resident, ExecutionRecord execution,
            string idleReason, long idleSinceGameSeconds)
        {
            Resident = resident;
            Execution = execution;
            IdleReason = idleReason ?? string.Empty;
            IdleSinceGameSeconds = idleSinceGameSeconds;
        }

        public bool IsIdle { get { return Execution == null; } }
    }

    public sealed class ActivityExecutor
    {
        internal Blockparty.Activities.Food FoodService { get; set; }
        internal Blockparty.Activities.Minigolf MinigolfService { get; set; }
        internal Blockparty.Activities.Party PartyService { get; set; }
        private const StateWriter Writer = StateWriter.ActivityExecutor;

        public const long DefaultBusyWaitGameSeconds = 600L;
        public const long CookBusyWaitGameSeconds = 1800L;
        public const string CookActivityCode = "ACT-FOOD-01";
        public const string StormShelterActivityCode = "STORM-SHELTER";
        public const double TravelMetersPerGameMinute = 1.0;
        public BicycleTravel Bicycles { get; set; }
        public Func<ApproachPose, ApproachPose, double, ApproachPose> WalkStep { get; set; }
        public const double ArrivalToleranceMeters = 0.05;
        public const double ChatProximityMeters = 2.0;

        private readonly struct DurationRule
        {
            public readonly string Code;
            public readonly long GameMinutes;
            public readonly bool OpenEnded;

            public DurationRule(string code, long gameMinutes, bool openEnded)
            {
                Code = code;
                GameMinutes = gameMinutes;
                OpenEnded = openEnded;
            }
        }

        // Catalog perform durations in game minutes (SDoc section 5 canonical
        // candidate data; ACT-REL-06/ACT-HOME-05 from BP-SEC-05). Sleep ends at
        // Energy 100; minigolf never ends on time (hole completion is the
        // MINIGOLF package); placement codes complete at perform entry.
        private static readonly DurationRule[] Durations = new DurationRule[]
        {
            new DurationRule("ACT-FOOD-01", 45L, false),
            new DurationRule("ACT-FOOD-02", 20L, false),
            new DurationRule("ACT-FOOD-03", 15L, false),
            new DurationRule("ACT-FOOD-04", 15L, false),
            new DurationRule("ACT-WORK-01", 360L, false),
            new DurationRule("ACT-WORK-02", 480L, false),
            new DurationRule("ACT-WORK-03", 60L, false),
            new DurationRule("ACT-WORK-04", 480L, false),
            new DurationRule("ACT-EDU-01", 360L, false),
            new DurationRule("ACT-EDU-02", 60L, false),
            new DurationRule("ACT-EDU-03", 120L, false),
            new DurationRule("ACT-EDU-04", 120L, false),
            new DurationRule("ACT-REL-01", 15L, false),
            new DurationRule("ACT-REL-02", 45L, false),
            new DurationRule("ACT-REL-03", 20L, false),
            new DurationRule("ACT-REL-04", 10L, false),
            new DurationRule("ACT-REL-05", 20L, false),
            new DurationRule("ACT-REL-06", 30L, false),
            new DurationRule("ACT-PARTY-01", 180L, false),
            new DurationRule("ACT-PARTY-02", 180L, false),
            new DurationRule("ACT-SPORT-01", 45L, false),
            new DurationRule("ACT-SPORT-02", 60L, true),
            new DurationRule("ACT-SPORT-03", 30L, false),
            new DurationRule("ACT-HEALTH-01", 0L, true),
            new DurationRule("ACT-HEALTH-02", 120L, false),
            new DurationRule("ACT-HEALTH-03", 15L, false),
            new DurationRule("ACT-HEALTH-04", 10L, false),
            new DurationRule("ACT-CLUB-01", 90L, false),
            new DurationRule("ACT-CLUB-02", 45L, false),
            new DurationRule("ACT-HOME-01", 30L, false),
            new DurationRule("ACT-HOME-02", 60L, false),
            new DurationRule("ACT-HOME-03", 15L, false),
            new DurationRule("ACT-HOME-04", 0L, false),
            new DurationRule("ACT-HOME-05", 30L, false),
            new DurationRule("ACT-CREA-01", 90L, false),
            new DurationRule("ACT-CREA-02", 60L, false),
            new DurationRule("ACT-CREA-03", 0L, false),
        };

        private sealed class QueuedAction
        {
            public ActionInstanceId Action;
            public ResidentId Resident;
            public long EnqueueGameSeconds;
            public int RequiredSlots;
        }

        private sealed class SlottedObject
        {
            public readonly ObjectId Id;
            public ApproachPose[] Poses;
            public bool Reachable = true;
            public readonly SlotHold?[] Holders;
            public readonly List<QueuedAction> Waiters = new List<QueuedAction>();

            public SlottedObject(ObjectId id, ApproachPose[] poses)
            {
                Id = id;
                Poses = poses;
                Holders = new SlotHold?[poses.Length];
            }
        }

        private sealed class IdleRecord
        {
            public string Reason;
            public long SinceGameSeconds;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly Clock clock;
        private readonly Dictionary<ObjectId, SlottedObject> objects = new Dictionary<ObjectId, SlottedObject>();
        private readonly Dictionary<ActionInstanceId, ExecutionRecord> executions = new Dictionary<ActionInstanceId, ExecutionRecord>();
        private readonly Dictionary<ResidentId, ApproachPose> positions = new Dictionary<ResidentId, ApproachPose>();
        private readonly Dictionary<ResidentId, IdleRecord> idle = new Dictionary<ResidentId, IdleRecord>();
        private readonly HashSet<ObjectId> unsafeWeatherObjects = new HashSet<ObjectId>();
        private readonly List<SlotEvent> reservationLog = new List<SlotEvent>();
        private readonly HashSet<ObjectId> removedObjects = new HashSet<ObjectId>();
        private int processedInterruptions;

        public ActivityExecutor(WorldState world, CommandArbitrator arbitrator, Clock clock)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            this.world = world;
            this.arbitrator = arbitrator;
            this.clock = clock;
        }

        public WorldState World { get { return world; } }
        public IReadOnlyList<SlotEvent> ReservationLog { get { return reservationLog.AsReadOnly(); } }

        public static long WaitBoundGameSeconds(string activityCode)
        {
            if (string.Equals(activityCode, CookActivityCode, StringComparison.Ordinal))
            {
                return CookBusyWaitGameSeconds;
            }
            return DefaultBusyWaitGameSeconds;
        }

        // STORM supplies the outdoor and electric object ids; keeping this in
        // the executor makes refusal and interruption use the same slot release.
        public void SetUnsafeWeather(StateWriter writer, ObjectId[] blocked)
        {
            if (writer != StateWriter.SimCore && writer != Writer)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
            unsafeWeatherObjects.Clear();
            if (blocked == null)
            {
                return;
            }
            for (int i = 0; i < blocked.Length; i++)
            {
                unsafeWeatherObjects.Add(blocked[i]);
            }
        }

        // Object registry: poses fix the stable slot indices and approach
        // poses in world meters. Holds start unheld; reconcile between ticks.
        public void RegisterObject(ObjectId id, ApproachPose[] poses)
        {
            if (poses == null || poses.Length == 0)
            {
                throw new ArgumentException("An object needs at least one slot pose", "poses");
            }
            if (!IsAllocatedObject((long)id))
            {
                throw new ArgumentException("Unknown object " + ((long)id).ToString(), "id");
            }
            if (objects.ContainsKey(id))
            {
                throw new ArgumentException("Object " + ((long)id).ToString() + " is already registered", "id");
            }
            ApproachPose[] copy = new ApproachPose[poses.Length];
            Array.Copy(poses, copy, poses.Length);
            removedObjects.Remove(id);
            objects.Add(id, new SlottedObject(id, copy));
        }

        public void SetReachable(ObjectId id, bool reachable)
        {
            SlottedObject registered = RequireObject(id);
            registered.Reachable = reachable;
        }

        public bool IsReachable(ObjectId id)
        {
            return RequireObject(id).Reachable;
        }

        // Move invalidates every in-flight action on the object with
        // TargetInvalidated and installs the new poses; removal additionally
        // unregisters it. Both release holds without consuming resources.
        public void MoveObject(ObjectId id, ApproachPose[] poses)
        {
            if (poses == null || poses.Length == 0)
            {
                throw new ArgumentException("A moved object needs at least one slot pose", "poses");
            }
            SlottedObject registered = RequireObject(id);
            InvalidateObjectActions(id, false);
            ApproachPose[] copy = new ApproachPose[poses.Length];
            Array.Copy(poses, copy, poses.Length);
            registered.Poses = copy;
        }

        public void RemoveObject(ObjectId id)
        {
            RequireObject(id);
            InvalidateObjectActions(id, true);
            objects.Remove(id);
            removedObjects.Add(id);
        }

        // Fixture positions owned here until PRESENTATION lands; motion below
        // only advances them from game-time deltas, never teleports.
        public void SetResidentPosition(ResidentId resident, ApproachPose pose)
        {
            RequireKnownResident(resident);
            positions[resident] = pose;
        }

        public ApproachPose GetResidentPosition(ResidentId resident)
        {
            RequireKnownResident(resident);
            ApproachPose pose;
            if (positions.TryGetValue(resident, out pose))
            {
                return pose;
            }
            return new ApproachPose(0.0f, 0.0f, 0.0f);
        }

        public SlotHold? GetHold(ObjectId id, int slotIndex)
        {
            SlottedObject registered;
            if (!objects.TryGetValue(id, out registered))
            {
                // A registered-then-removed object has no live slots; an
                // object that was never registered is still an error.
                if (removedObjects.Contains(id)) return null;
                throw new ArgumentException("Unknown object " + ((long)id).ToString(), "id");
            }
            if (slotIndex < 0 || slotIndex >= registered.Holders.Length)
            {
                throw new ArgumentException("Unknown slot " + slotIndex.ToString(), "slotIndex");
            }
            return registered.Holders[slotIndex];
        }

        public bool IsObjectRegistered(ObjectId id)
        {
            return objects.ContainsKey(id);
        }

        public int QueueLength(ObjectId id)
        {
            return RequireObject(id).Waiters.Count;
        }

        public ExecutionRecord GetExecution(ActionInstanceId action)
        {
            ExecutionRecord record;
            if (executions.TryGetValue(action, out record))
            {
                return record;
            }
            return null;
        }

        // Every held execution, running or terminal. Live play reads completions
        // here: GetResidentStatus only shows the running action.
        public ExecutionRecord[] Executions
        {
            get
            {
                ExecutionRecord[] all = new ExecutionRecord[executions.Count];
                executions.Values.CopyTo(all, 0);
                return all;
            }
        }

        public ResidentStatus GetResidentStatus(ResidentId resident)
        {
            RequireKnownResident(resident);
            ExecutionRecord[] live = LiveFor(resident);
            if (live.Length > 0)
            {
                return new ResidentStatus(resident, live[0], string.Empty, world.GameSeconds);
            }
            IdleRecord record;
            if (!idle.TryGetValue(resident, out record))
            {
                return new ResidentStatus(resident, null, "AwaitingDecision", world.GameSeconds);
            }
            return new ResidentStatus(resident, null, record.Reason, record.SinceGameSeconds);
        }

        // One fixed-tick boundary call: dispatch new arbitrator currents,
        // apply interruptions, acquire queued holds FIFO, then advance travel,
        // perform and busy waits from the game clock. Pause freezes the
        // time-based stages; dispatch, refusal and release still run.
        public void Tick(StateWriter writer)
        {
            if (writer != Writer)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
            ProcessInterruptions();
            if (Bicycles != null && !clock.Paused) Bicycles.ContinueReturns(arbitrator, this, world.GameSeconds);
            DispatchNew();
            AcquireQueued();
            if (!clock.Paused)
            {
                Advance();
            }
        }

        // Load reconciliation between ticks: only the given in-progress
        // actions keep their holds and queue places; every other hold is an
        // orphan and is released, never duplicated.
        public void ReconcileAfterLoad(ActionInstanceId[] liveActions)
        {
            if (liveActions == null)
            {
                throw new ArgumentNullException("liveActions");
            }
            Dictionary<ActionInstanceId, bool> live = new Dictionary<ActionInstanceId, bool>();
            for (int i = 0; i < liveActions.Length; i++)
            {
                live[liveActions[i]] = true;
            }
            foreach (KeyValuePair<ObjectId, SlottedObject> pair in objects)
            {
                SlottedObject registered = pair.Value;
                for (int i = 0; i < registered.Holders.Length; i++)
                {
                    if (registered.Holders[i].HasValue && !live.ContainsKey(registered.Holders[i].Value.Action))
                    {
                        reservationLog.Add(new SlotEvent(world.GameSeconds, false, registered.Holders[i].Value));
                        registered.Holders[i] = null;
                    }
                }
                for (int i = registered.Waiters.Count - 1; i >= 0; i--)
                {
                    if (!live.ContainsKey(registered.Waiters[i].Action))
                    {
                        registered.Waiters.RemoveAt(i);
                    }
                }
            }
            List<ActionInstanceId> drop = new List<ActionInstanceId>();
            foreach (KeyValuePair<ActionInstanceId, ExecutionRecord> pair in executions)
            {
                if (!live.ContainsKey(pair.Key))
                {
                    pair.Value.ClearHolds();
                    drop.Add(pair.Key);
                }
            }
            for (int i = 0; i < drop.Count; i++)
            {
                executions.Remove(drop[i]);
            }
        }

        // Externally owned terminal (MINIGOLF shared match): release slots and
        // queue places and close the execution record without touching the
        // arbitrator, which the owning service clears separately.
        public bool TerminateAction(ActionInstanceId action, TerminalOutcome outcome)
        {
            ExecutionRecord record;
            if (!executions.TryGetValue(action, out record) || record.IsTerminal)
            {
                return false;
            }
            long now = world.GameSeconds;
            SkipToPerform(record, now);
            ReleaseExecution(record);
            record.SetBlocked(null, 0);
            record.Terminate(outcome, now, outcome == TerminalOutcome.Completed);
            SetIdle(record, (outcome == TerminalOutcome.Completed ? "Completed " : "Failed ")
                + record.Activity.Code + ": external match");
            return true;
        }

        public ExecutionRecord Dispatch(ResidentId resident, ActionEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }
            RequireKnownResident(resident);
            ExecutionRecord known;
            if (executions.TryGetValue(entry.Action, out known))
            {
                return known;
            }
            ExecutionRecord record = new ExecutionRecord(
                entry.Action, resident, entry.Activity, entry.Kind, entry.TargetId, entry.TargetKind);
            executions.Add(entry.Action, record);
            long now = world.GameSeconds;

            ResidentRecord self;
            world.TryGetResident(resident, out self);
            if (!self.IsAlive)
            {
                record.AddStage(ExecutionStage.Validate, now, false);
                Fail(record, RejectReason.ResidentDeceased, TerminalOutcome.Failed);
                return record;
            }
            if (entry.Kind == CommandKind.TravelTo)
            {
                DispatchTravel(record, now);
                return record;
            }
            if (entry.Kind != CommandKind.DirectActivity)
            {
                record.AddStage(ExecutionStage.Validate, now, false);
                Fail(record, RejectReason.InvalidParameters, TerminalOutcome.Failed);
                return record;
            }
            DurationRule rule;
            if (!TryFindDuration(entry.Activity.Code, out rule))
            {
                record.AddStage(ExecutionStage.Validate, now, false);
                Fail(record, RejectReason.UnknownActivity, TerminalOutcome.Failed);
                return record;
            }
            record.AddStage(ExecutionStage.Validate, now, false);
            SlottedObject blockedObject;
            if (entry.TargetId.HasValue
                && TryResolveObject(entry.TargetId.Value, out blockedObject)
                && unsafeWeatherObjects.Contains(blockedObject.Id))
            {
                Fail(record, RejectReason.UnsafeWeather, TerminalOutcome.Failed);
                return record;
            }
            if (!entry.TargetId.HasValue)
            {
                // In-place activity: no slot, no travel.
                record.AddStage(ExecutionStage.Reserve, now, true);
                record.AddStage(ExecutionStage.Travel, now, true);
                record.AddStage(ExecutionStage.Align, now, true);
                EnterPerform(record, rule, now);
                return record;
            }
            SlottedObject registered;
            if (TryResolveObject(entry.TargetId.Value, out registered))
            {
                record.AddStage(ExecutionStage.Reserve, now, false);
                return DispatchSlotTarget(record, registered, entry.Activity.Code, now);
            }
            ResidentRecord partner;
            if (TryResolveResident(entry.TargetId.Value, out partner))
            {
                // Social target: travel to the partner, no object hold.
                // shortcut: Chat dual-slot binding deferred to RELATIONSHIP;
                // NAV performs resident-targeted actions at proximity.
                record.AddStage(ExecutionStage.Reserve, now, true);
                record.AddStage(ExecutionStage.Travel, now, false);
                if (DistanceTo(GetResidentPosition(resident), GetResidentPosition(partner.Id))
                    <= ChatProximityMeters)
                {
                    record.AddStage(ExecutionStage.Align, now, false);
                    EnterPerform(record, rule, now);
                }
                return record;
            }
            Fail(record, RejectReason.TargetMissing, TerminalOutcome.Failed);
            return record;
        }

        private ExecutionRecord DispatchSlotTarget(ExecutionRecord record,
            SlottedObject registered, string activityCode, long now)
        {
            if (!registered.Reachable)
            {
                record.SetBlocked(RejectReason.NoPath, 0);
                Fail(record, RejectReason.NoPath, TerminalOutcome.Failed);
                return record;
            }
            int required = RequiredSlots(activityCode);
            if (TryAcquire(record, registered, required, now))
            {
                record.AddStage(ExecutionStage.Travel, now, false);
                return record;
            }
            registered.Waiters.Add(new QueuedAction
            {
                Action = record.Action,
                Resident = record.Resident,
                EnqueueGameSeconds = now,
                RequiredSlots = required,
            });
            record.Queued = true;
            record.EnqueueGameSeconds = now;
            record.SetBlocked(RejectReason.ObjectOccupied, registered.Waiters.Count);
            return record;
        }

        private void DispatchTravel(ExecutionRecord record, long now)
        {
            record.AddStage(ExecutionStage.Validate, now, false);
            record.EnqueueGameSeconds = now;
            if (!record.TargetId.HasValue)
            {
                Fail(record, RejectReason.TargetMissing, TerminalOutcome.Failed);
                return;
            }
            SlottedObject registered;
            ResidentRecord partner;
            if (TryResolveObject(record, out registered))
            {
                record.AddStage(ExecutionStage.Reserve, now, true);
                if (!registered.Reachable)
                {
                    record.SetBlocked(RejectReason.NoPath, 0);
                    Fail(record, RejectReason.NoPath, TerminalOutcome.Failed);
                    return;
                }
                record.AddStage(ExecutionStage.Travel, now, false);
                return;
            }
            if (TryResolveResident(record, out partner))
            {
                record.AddStage(ExecutionStage.Reserve, now, true);
                record.AddStage(ExecutionStage.Travel, now, false);
                return;
            }
            Fail(record, RejectReason.TargetMissing, TerminalOutcome.Failed);
        }

        private void ProcessInterruptions()
        {
            IReadOnlyList<InterruptionRecord> records = arbitrator.Interrupted;
            for (; processedInterruptions < records.Count; processedInterruptions++)
            {
                InterruptionRecord interruption = records[processedInterruptions];
                ExecutionRecord record;
                if (!executions.TryGetValue(interruption.Action, out record) || record.IsTerminal)
                {
                    continue;
                }
                ReleaseExecution(record);
                record.SetBlocked(null, 0);
                SkipToPerform(record, world.GameSeconds);
                record.Terminate(TerminalOutcome.Interrupted, world.GameSeconds, false);
                SetIdle(record, "Interrupted " + record.Activity.Code);
            }
        }

        private void DispatchNew()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                arbitrator.PromoteNext(residents[i].Id);
                ActionEntry current = arbitrator.GetCurrent(residents[i].Id);
                if (current != null)
                {
                    Dispatch(residents[i].Id, current);
                }
            }
        }

        private void AcquireQueued()
        {
            long now = world.GameSeconds;
            foreach (KeyValuePair<ObjectId, SlottedObject> pair in objects)
            {
                SlottedObject registered = pair.Value;
                while (registered.Waiters.Count > 0)
                {
                    QueuedAction head = registered.Waiters[0];
                    ExecutionRecord record;
                    if (!executions.TryGetValue(head.Action, out record) || record.IsTerminal)
                    {
                        registered.Waiters.RemoveAt(0);
                        continue;
                    }
                    if (!registered.Reachable)
                    {
                        // Unreachable while queued: fail in order, keep FIFO.
                        registered.Waiters.RemoveAt(0);
                        record.Queued = false;
                        record.SetBlocked(RejectReason.NoPath, 0);
                        Fail(record, RejectReason.NoPath, TerminalOutcome.Failed);
                        continue;
                    }
                    if (!TryAcquire(record, registered, head.RequiredSlots, now))
                    {
                        break;
                    }
                    registered.Waiters.RemoveAt(0);
                    record.Queued = false;
                    record.SetBlocked(null, 0);
                    record.AddStage(ExecutionStage.Travel, now, false);
                }
                for (int i = 0; i < registered.Waiters.Count; i++)
                {
                    ExecutionRecord record;
                    if (executions.TryGetValue(registered.Waiters[i].Action, out record)
                        && !record.IsTerminal)
                    {
                        record.SetBlocked(RejectReason.ObjectOccupied, i + 1);
                    }
                }
            }
        }

        private void Advance()
        {
            long now = world.GameSeconds;
            List<ExecutionRecord> live = new List<ExecutionRecord>();
            foreach (KeyValuePair<ActionInstanceId, ExecutionRecord> pair in executions)
            {
                if (!pair.Value.IsTerminal)
                {
                    live.Add(pair.Value);
                }
            }
            for (int i = 0; i < live.Count; i++)
            {
                ExecutionRecord record = live[i];
                ResidentRecord self;
                if (!world.TryGetResident(record.Resident, out self) || !self.IsAlive)
                {
                    ReleaseExecution(record);
                    Fail(record, RejectReason.ResidentDeceased, TerminalOutcome.Failed);
                    continue;
                }
                SlottedObject blockedObject;
                if (record.Kind == CommandKind.DirectActivity && record.TargetId.HasValue
                    && TryResolveObject(record, out blockedObject)
                    && unsafeWeatherObjects.Contains(blockedObject.Id))
                {
                    Fail(record, RejectReason.UnsafeWeather, TerminalOutcome.Failed);
                    continue;
                }
                if (record.Queued)
                {
                    long bound = WaitBoundGameSeconds(record.Activity.Code);
                    if (now >= record.EnqueueGameSeconds + bound)
                    {
                        Dequeue(record);
                        record.Queued = false;
                        if (string.Equals(record.Activity.Code, CookActivityCode, StringComparison.Ordinal))
                        {
                            Fail(record, RejectReason.ObjectOccupied, TerminalOutcome.Failed);
                        }
                        else
                        {
                            // Default bound: abandon the request with nothing
                            // spent; the next action comes from the arbitrator
                            // queue through normal dispatch, never from here.
                            record.SetBlocked(RejectReason.ObjectOccupied, 0);
                            SkipToPerform(record, now);
                            record.Terminate(TerminalOutcome.Abandoned, now, false);
                            SetIdle(record, "Abandoned " + record.Activity.Code + ": ObjectOccupied");
                            arbitrator.CompleteCurrent(record.Resident, record.Action);
                        }
                    }
                    continue;
                }
                if (record.Stage == ExecutionStage.Travel)
                {
                    AdvanceTravel(record, now);
                    continue;
                }
                if (record.Stage == ExecutionStage.Perform)
                {
                    AdvancePerform(record, now);
                }
            }
        }

        private void AdvanceTravel(ExecutionRecord record, long now)
        {
            ApproachPose destination;
            if (!TryTravelDestination(record, out destination))
            {
                ReleaseExecution(record);
                Fail(record, RejectReason.TargetInvalidated, TerminalOutcome.Failed);
                return;
            }
            if (IsObjectTarget(record))
            {
                SlottedObject registered;
                if (objects.TryGetValue((ObjectId)record.TargetId.Value, out registered)
                    && !registered.Reachable)
                {
                    ReleaseExecution(record);
                    record.SetBlocked(RejectReason.NoPath, 0);
                    Fail(record, RejectReason.NoPath, TerminalOutcome.Failed);
                    return;
                }
            }
            ApproachPose current = GetResidentPosition(record.Resident);
            if (Bicycles != null)
            {
                if (Bicycles.IsReturning(record.Resident)) return;
                // Storm shelter travel is dispatched outside the arbitrator;
                // no current entry exists, so the owner is autonomous.
                ActionEntry entry = arbitrator.GetCurrent(record.Resident);
                ActionOwner owner = entry != null ? entry.Owner : ActionOwner.Autonomous;
                ApproachPose next;
                bool travelling = Bicycles.Step(record, owner, current, destination, false, now, out next);
                positions[record.Resident] = next;
                if (travelling) return;
                current = next;
            }
            double remaining = DistanceTo(current, destination);
            if (remaining <= ArrivalToleranceMeters)
            {
                positions[record.Resident] = destination;
                record.AddStage(ExecutionStage.Align, now, false);
                if (record.Kind == CommandKind.TravelTo)
                {
                    record.AddStage(ExecutionStage.Perform, now, true);
                    record.Terminate(TerminalOutcome.Completed, now, true);
                    SetIdle(record, "Completed Travel");
                    arbitrator.CompleteCurrent(record.Resident, record.Action);
                    return;
                }
                DurationRule rule;
                TryFindDuration(record.Activity.Code, out rule);
                EnterPerform(record, rule, now);
                return;
            }
            // Shelter travel starts at the storm boundary but moves only after
            // the shared clock advances; same-time ticks cannot teleport it.
            if (string.Equals(record.Activity.Code, StormShelterActivityCode, StringComparison.Ordinal)
                && now <= record.EnqueueGameSeconds)
            {
                return;
            }
            double step = TravelMetersPerGameMinute / 60.0 * (double)SimTick.TickGameSeconds;
            double advance = step < remaining ? step : remaining;
            double dx = (double)destination.X - (double)current.X;
            double dy = (double)destination.Y - (double)current.Y;
            double dz = (double)destination.Z - (double)current.Z;
            double ratio = advance / remaining;
            positions[record.Resident] = WalkStep != null ? WalkStep(current, destination, step) : new ApproachPose(
                (float)((double)current.X + dx * ratio),
                (float)((double)current.Y + dy * ratio),
                (float)((double)current.Z + dz * ratio));
        }

        private void AdvancePerform(ExecutionRecord record, long now)
        {
            if (Bicycles != null)
            {
                if (Bicycles.IsReturning(record.Resident)) return;
                if (record.Activity.Code == "ACT-SPORT-03")
                {
                    var entry = arbitrator.GetCurrent(record.Resident);
                    ApproachPose current = GetResidentPosition(record.Resident), next;
                    if (!Bicycles.CanContinueLeisure(record, current))
                    {
                        Fail(record, RejectReason.ObjectUnavailable, TerminalOutcome.Failed);
                        return;
                    }
                    bool travelling = Bicycles.Step(record, entry.Owner, current, current, true, now, out next);
                    positions[record.Resident] = next;
                    if (travelling) return;
                }
            }
            DurationRule rule;
            TryFindDuration(record.Activity.Code, out rule);
            if (rule.OpenEnded)
            {
                if (string.Equals(rule.Code, "ACT-HEALTH-01", StringComparison.Ordinal))
                {
                    ResidentRecord self;
                    world.TryGetResident(record.Resident, out self);
                    if (self.GetNeed(Need.Energy) >= 100.0f)
                    {
                        Complete(record, now);
                    }
                }
                return;
            }
            long end = record.PerformStartGameSeconds + rule.GameMinutes * 60L;
            if (now >= end)
            {
                Complete(record, now);
            }
        }

        private void EnterPerform(ExecutionRecord record, DurationRule rule, long now)
        {
            record.AddStage(ExecutionStage.Perform, now, false);
            record.PerformStartGameSeconds = now;
            world.ReserveEffect(Writer, record.Action, record.Activity.Code,
                new LedgerRecipient(record.Resident));
            record.LedgerReserved = true;
            if (!rule.OpenEnded && rule.GameMinutes <= 0L)
            {
                Complete(record, now);
            }
        }

        private void Complete(ExecutionRecord record, long now)
        {
            world.CommitEffect(Writer, record.Action);
            ReleaseExecution(record);
            record.SetBlocked(null, 0);
            record.Terminate(TerminalOutcome.Completed, now, true);
            SetIdle(record, "Completed " + record.Activity.Code);
            arbitrator.CompleteCurrent(record.Resident, record.Action);
        }

        private void Fail(ExecutionRecord record, RejectReason reason, TerminalOutcome outcome)
        {
            long now = world.GameSeconds;
            SkipToPerform(record, now);
            ReleaseExecution(record);
            record.SetBlocked(reason, 0);
            record.Terminate(outcome, now, false);
            SetIdle(record, "Failed " + record.Activity.Code + ": " + reason.ToString());
            arbitrator.CompleteCurrent(record.Resident, record.Action);
        }

        private void ReleaseExecution(ExecutionRecord record)
        {
            Dequeue(record);
            record.Queued = false;
            SlotHold[] held = record.Holds;
            record.ClearHolds();
            for (int i = 0; i < held.Length; i++)
            {
                SlottedObject registered;
                if (objects.TryGetValue(held[i].Object, out registered)
                    && held[i].SlotIndex >= 0 && held[i].SlotIndex < registered.Holders.Length
                    && registered.Holders[held[i].SlotIndex].HasValue
                    && registered.Holders[held[i].SlotIndex].Value == held[i])
                {
                    registered.Holders[held[i].SlotIndex] = null;
                    reservationLog.Add(new SlotEvent(world.GameSeconds, false, held[i]));
                }
            }
        }

        private void Dequeue(ExecutionRecord record)
        {
            foreach (KeyValuePair<ObjectId, SlottedObject> pair in objects)
            {
                List<QueuedAction> waiters = pair.Value.Waiters;
                for (int i = waiters.Count - 1; i >= 0; i--)
                {
                    if (waiters[i].Action == record.Action)
                    {
                        waiters.RemoveAt(i);
                    }
                }
            }
        }

        private bool TryAcquire(ExecutionRecord record, SlottedObject registered, int required, long now)
        {
            List<int> free = new List<int>();
            for (int i = 0; i < registered.Holders.Length && free.Count < required; i++)
            {
                if (!registered.Holders[i].HasValue)
                {
                    free.Add(i);
                }
            }
            if (free.Count < required)
            {
                return false;
            }
            for (int i = 0; i < free.Count; i++)
            {
                SlotHold hold = new SlotHold(
                    registered.Id, free[i], registered.Poses[free[i]], record.Resident, record.Action);
                registered.Holders[free[i]] = hold;
                record.AddHold(hold);
                reservationLog.Add(new SlotEvent(now, true, hold));
            }
            return true;
        }

        private bool TryTravelDestination(ExecutionRecord record, out ApproachPose destination)
        {
            if (!record.TargetId.HasValue)
            {
                destination = default(ApproachPose);
                return false;
            }
            ResidentRecord partner;
            if (TryResolveResident(record, out partner))
            {
                destination = GetResidentPosition(partner.Id);
                return true;
            }
            SlottedObject registered;
            if (!TryResolveObject(record, out registered))
            {
                destination = default(ApproachPose);
                return false;
            }
            SlotHold[] held = record.Holds;
            if (held.Length > 0)
            {
                destination = held[0].Pose;
                return true;
            }
            destination = registered.Poses[0];
            return true;
        }

        private bool IsObjectTarget(ExecutionRecord record)
        {
            if (!record.TargetId.HasValue)
            {
                return false;
            }
            SlottedObject registered;
            return TryResolveObject(record, out registered);
        }

        private bool TryResolveObject(ExecutionRecord record, out SlottedObject registered)
        {
            if (record.TargetKind == TravelTargetKind.Resident) { registered = null; return false; }
            return TryResolveObject(record.TargetId.Value, out registered);
        }

        private bool TryResolveResident(ExecutionRecord record, out ResidentRecord partner)
        {
            if (record.TargetKind == TravelTargetKind.Object) { partner = null; return false; }
            return record.TargetKind == TravelTargetKind.Resident
                ? world.TryGetResident((ResidentId)record.TargetId.Value, out partner)
                : TryResolveResident(record.TargetId.Value, out partner);
        }

        // TargetId is one bare long shared by residents and objects (both
        // counters start at 1), so a value can name both. A registered object
        // wins: game code must not register an object under a live resident
        // id it still targets socially. Explicit travel kinds never use this fallback.
        private bool TryResolveObject(long targetId, out SlottedObject registered)
        {
            return objects.TryGetValue((ObjectId)targetId, out registered);
        }

        private bool TryResolveResident(long targetId, out ResidentRecord partner)
        {
            SlottedObject registered;
            if (objects.TryGetValue((ObjectId)targetId, out registered))
            {
                partner = null;
                return false;
            }
            return world.TryGetResident((ResidentId)targetId, out partner);
        }

        private void InvalidateObjectActions(ObjectId id, bool removed)
        {
            long now = world.GameSeconds;
            List<ExecutionRecord> doomed = new List<ExecutionRecord>();
            foreach (KeyValuePair<ActionInstanceId, ExecutionRecord> pair in executions)
            {
                ExecutionRecord record = pair.Value;
                if (!record.IsTerminal && record.TargetId.HasValue
                    && record.TargetKind != TravelTargetKind.Resident
                    && record.TargetId.Value == (long)id)
                {
                    doomed.Add(record);
                }
            }
            for (int i = 0; i < doomed.Count; i++)
            {
                ExecutionRecord record = doomed[i];
                SkipToPerform(record, now);
                ReleaseExecution(record);
                record.SetBlocked(RejectReason.TargetInvalidated, 0);
                record.Terminate(TerminalOutcome.Failed, now, false);
                SetIdle(record, "Failed " + record.Activity.Code + ": TargetInvalidated"
                    + (removed ? " (removed)" : " (moved)"));
                arbitrator.CompleteCurrent(record.Resident, record.Action);
            }
            SlottedObject registered;
            if (objects.TryGetValue(id, out registered))
            {
                for (int i = 0; i < registered.Holders.Length; i++)
                {
                    if (registered.Holders[i].HasValue)
                    {
                        reservationLog.Add(new SlotEvent(now, false, registered.Holders[i].Value));
                        registered.Holders[i] = null;
                    }
                }
                registered.Waiters.Clear();
            }
        }

        private static void SkipToPerform(ExecutionRecord record, long now)
        {
            if (record.Stage == ExecutionStage.Validate)
            {
                record.AddStage(ExecutionStage.Reserve, now, true);
            }
            if (record.Stage == ExecutionStage.Validate || record.Stage == ExecutionStage.Reserve)
            {
                record.AddStage(ExecutionStage.Travel, now, true);
                record.AddStage(ExecutionStage.Align, now, true);
                record.AddStage(ExecutionStage.Perform, now, true);
            }
            else if (record.Stage == ExecutionStage.Travel)
            {
                record.AddStage(ExecutionStage.Align, now, true);
                record.AddStage(ExecutionStage.Perform, now, true);
            }
            else if (record.Stage == ExecutionStage.Align)
            {
                record.AddStage(ExecutionStage.Perform, now, true);
            }
        }

        private void SetIdle(ExecutionRecord record, string reason)
        {
            IdleRecord idleRecord;
            if (!idle.TryGetValue(record.Resident, out idleRecord))
            {
                idleRecord = new IdleRecord();
                idle.Add(record.Resident, idleRecord);
            }
            idleRecord.Reason = reason;
            idleRecord.SinceGameSeconds = world.GameSeconds;
        }

        private ExecutionRecord[] LiveFor(ResidentId resident)
        {
            List<ExecutionRecord> live = new List<ExecutionRecord>();
            foreach (KeyValuePair<ActionInstanceId, ExecutionRecord> pair in executions)
            {
                if (!pair.Value.IsTerminal && pair.Value.Resident == resident)
                {
                    live.Add(pair.Value);
                }
            }
            return live.ToArray();
        }

        private static bool TryFindDuration(string code, out DurationRule rule)
        {
            for (int i = 0; i < Durations.Length; i++)
            {
                if (string.Equals(Durations[i].Code, code, StringComparison.Ordinal))
                {
                    rule = Durations[i];
                    return true;
                }
            }
            rule = default(DurationRule);
            return false;
        }

        private static int RequiredSlots(string code)
        {
            // Chat and TryForBaby each take both partner slots on one object.
            if (string.Equals(code, "ACT-REL-01", StringComparison.Ordinal)
                || string.Equals(code, "ACT-REL-06", StringComparison.Ordinal))
            {
                return 2;
            }
            return 1;
        }

        private static double DistanceTo(ApproachPose a, ApproachPose b)
        {
            double dx = (double)a.X - (double)b.X;
            double dy = (double)a.Y - (double)b.Y;
            double dz = (double)a.Z - (double)b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private SlottedObject RequireObject(ObjectId id)
        {
            SlottedObject registered;
            if (!objects.TryGetValue(id, out registered))
            {
                throw new ArgumentException("Unknown object " + ((long)id).ToString(), "id");
            }
            return registered;
        }

        private ResidentRecord RequireKnownResident(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            return record;
        }

        private bool IsAllocatedObject(long targetId)
        {
            return targetId > 0L && targetId < world.NextObjectId;
        }
    }
}
