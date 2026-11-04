// COMMAND@1: the command arbitrator (CMD-ARBITRATOR, CONTRACT-BP-01-CMD).
// Consumes IFC-LOCK@1 (CommandEnvelope, CommandResult, QueueMode, RejectReason,
// FreshnessStamp, CommandQueuePolicy, VoiceMeaning set), STATE@1 (WorldState,
// ResidentRecord, LifeStage, StateWriter) and CLOCK@1 (Clock). Plain C# 9, no
// UnityEngine dependency. Every authoritative mutation flows through an audited
// WorldState method with StateWriter.CommandArbitrator (BP-SYS-02); clock
// control inputs route to the one Clock. Queues, the world-command sequence,
// automation enablement, last-valid wills and caregiver designations are this
// instance's runtime state; SAVE persists accepted player queues later.
// Fast Enter Play Mode: no static mutable state (the activity catalog is a
// static readonly table), explicit numeric conversions, no AppDomain,
// Assembly.Location, BinaryFormatter, wall-clock reads or String.GetHashCode.
// Normative source: BP-SEC-07 internal contracts (command contract), BP-SIM-04
// (Immediate/Append priority, takeover), BP-SIM-05 (one owner per action),
// BP-SIM-06 (interruption yields defined state) and the section 5 activity
// catalog stage bounds. Reasons this package never produces (TargetInvalidated,
// InsufficientFunds, ObjectUnavailable, ObjectOccupied, NoPath, UnsafeWeather)
// belong to successor packages (NAV, economy, EXEC); slot release within one
// tick and completion-effect suppression are EXEC obligations consuming the
// interruption records below.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // BP-SIM-05: exactly one owner per in-progress action. PlayerHuman,
    // PlayerAutomated and VoiceMapped are source annotations within Player,
    // never owners of their own.
    public enum ActionOwner
    {
        Player = 0,
        Autonomous = 1,
        ModelProposed = 2,
    }

    // One queued or in-progress action. Source is set only for Player-owned
    // entries; Autonomous and ModelProposed entries carry no source.
    public sealed class ActionEntry
    {
        public readonly CommandId Command;
        public readonly ActionInstanceId Action;
        public readonly CommandKind Kind;
        public readonly ActivityId Activity;
        public readonly long? TargetId;
        public readonly TravelTargetKind TargetKind;
        public readonly ActionOwner Owner;
        public readonly CommandSource? Source;

        public ActionEntry(CommandId command, ActionInstanceId action, CommandKind kind,
            ActivityId activity, long? targetId, ActionOwner owner, CommandSource? source,
            TravelTargetKind targetKind = TravelTargetKind.Unspecified)
        {
            Command = command;
            Action = action;
            Kind = kind;
            Activity = activity;
            TargetId = targetId;
            TargetKind = targetKind;
            Owner = owner;
            Source = source;
        }
    }

    // Once-only interruption commit (BP-SIM-06): recorded when the current
    // action is replaced or cancelled. EXEC releases the slot and applies the
    // catalog interruption policy from this record; the arbitrator never
    // touches effect ledgers, so a queue change can never refund a committed
    // past effect.
    public sealed class InterruptionRecord
    {
        public readonly ActionInstanceId Action;
        public readonly ActivityId Activity;
        public readonly ActionOwner Owner;
        public readonly CommandId ByCommand;

        public InterruptionRecord(ActionInstanceId action, ActivityId activity,
            ActionOwner owner, CommandId byCommand)
        {
            Action = action;
            Activity = activity;
            Owner = owner;
            ByCommand = byCommand;
        }
    }

    // Diagnostic command log: one record per submitted envelope, in arrival
    // order. Entries replaced by an accepted Immediate command are marked
    // Superseded with the replacing CommandId; the log never mutates state.
    public sealed class CommandLogEntry
    {
        public readonly CommandId Command;
        public readonly CommandKind Kind;
        public readonly ResidentId? Resident;
        public readonly bool Accepted;
        public readonly RejectReason Reason;
        public bool Superseded { get; private set; }
        public CommandId SupersededBy { get; private set; }

        public CommandLogEntry(CommandId command, CommandKind kind, ResidentId? resident,
            bool accepted, RejectReason reason)
        {
            Command = command;
            Kind = kind;
            Resident = resident;
            Accepted = accepted;
            Reason = reason;
            Superseded = false;
            SupersededBy = default(CommandId);
        }

        internal void MarkSuperseded(CommandId by)
        {
            Superseded = true;
            SupersededBy = by;
        }
    }

    // Minimum activity catalog stage bounds (SDoc section 5). Unstated stages
    // default to all residents; ACT-WORK-01 admits teens at career level 1 or
    // below only. Durations, costs, needs and interruption policies stay in
    // the SDoc: COMMAND validates identity and stage eligibility only.
    public readonly struct ActivityRule
    {
        public readonly string Code;
        public readonly LifeStage MinStage;
        public readonly LifeStage MaxStage;
        public readonly bool TeenAtLevelOneOnly;

        public ActivityRule(string code, LifeStage minStage, LifeStage maxStage, bool teenAtLevelOneOnly)
        {
            Code = code ?? string.Empty;
            MinStage = minStage;
            MaxStage = maxStage;
            TeenAtLevelOneOnly = teenAtLevelOneOnly;
        }
    }

    public sealed class CommandArbitrator
    {
        private long nextRuleCommandId = 1000000000L;
        internal CommandId AllocateRuleCommand()
        {
            return (CommandId)(nextRuleCommandId++);
        }
        private const StateWriter Writer = StateWriter.CommandArbitrator;

        private static readonly ActivityRule[] Catalog = new ActivityRule[]
        {
            new ActivityRule("ACT-FOOD-01", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-FOOD-02", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-FOOD-03", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-FOOD-04", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-WORK-01", LifeStage.YoungAdult, LifeStage.Elder, true),
            new ActivityRule("ACT-WORK-02", LifeStage.YoungAdult, LifeStage.Elder, false),
            new ActivityRule("ACT-WORK-03", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-WORK-04", LifeStage.YoungAdult, LifeStage.Elder, false),
            new ActivityRule("ACT-EDU-01", LifeStage.Child, LifeStage.Teen, false),
            new ActivityRule("ACT-EDU-02", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-EDU-03", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-EDU-04", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-01", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-02", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-03", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-04", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-05", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-REL-06", LifeStage.YoungAdult, LifeStage.Elder, false),
            new ActivityRule("ACT-PARTY-01", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-PARTY-02", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-SPORT-01", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-SPORT-02", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-SPORT-03", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-HEALTH-01", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-HEALTH-02", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-HEALTH-03", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-HEALTH-04", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-CLUB-01", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-CLUB-02", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-HOME-01", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-HOME-02", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-HOME-03", LifeStage.Teen, LifeStage.Elder, false),
            new ActivityRule("ACT-HOME-04", LifeStage.Newborn, LifeStage.Elder, false),
            new ActivityRule("ACT-HOME-05", LifeStage.YoungAdult, LifeStage.Elder, false),
            new ActivityRule("ACT-CREA-01", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-CREA-02", LifeStage.Child, LifeStage.Elder, false),
            new ActivityRule("ACT-CREA-03", LifeStage.Newborn, LifeStage.Elder, false),
        };

        // The ten voice-supported meanings (CONTRACT-BP-06-VOICE) as command
        // kinds. A VoiceMapped envelope carrying any other kind is rejected:
        // voice outside the supported set executes no command (SEIT-TB-01-N2).
        private static readonly CommandKind[] VoiceKinds = new CommandKind[]
        {
            CommandKind.SelectResident,
            CommandKind.FollowResident,
            CommandKind.StopFollowing,
            CommandKind.DirectActivity,
            CommandKind.TravelTo,
            CommandKind.Pause,
            CommandKind.Resume,
            CommandKind.SetSpeed,
            CommandKind.CancelCurrent,
            CommandKind.OpenMap,
            CommandKind.SaveGame,
        };

        private sealed class ResidentQueue
        {
            public ActionEntry Current;
            public readonly List<ActionEntry> Pending = new List<ActionEntry>();
        }

        private readonly WorldState world;
        private readonly Clock clock;
        private readonly Dictionary<ResidentId, ResidentQueue> queues = new Dictionary<ResidentId, ResidentQueue>();
        private readonly Dictionary<ResidentId, WillRecord> lastWills = new Dictionary<ResidentId, WillRecord>();
        private readonly Dictionary<ResidentId, ResidentId> caregiverDesignations = new Dictionary<ResidentId, ResidentId>();
        private Housing willHousing;
        private bool restoringWill;
        private readonly List<CommandLogEntry> log = new List<CommandLogEntry>();
        private readonly List<InterruptionRecord> interrupted = new List<InterruptionRecord>();
        private uint worldSequence;
        private bool automationEnabled = true;

        public CommandArbitrator(WorldState world, Clock clock)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            this.world = world;
            this.clock = clock;
        }

        public WorldState World { get { return world; } }
        public Clock Clock { get { return clock; } }
        public bool AutomationEnabled { get { return automationEnabled; } }
        public uint WorldSequence { get { return worldSequence; } }

        public IReadOnlyList<CommandLogEntry> CommandLog { get { return log.AsReadOnly(); } }
        public IReadOnlyList<InterruptionRecord> Interrupted { get { return interrupted.AsReadOnly(); } }

        // Manual takeover without a command: bumps the resident control
        // generation and disables PlayerAutomated acceptance until the player
        // explicitly re-enables automation. World and queues are preserved.
        public void Takeover(ResidentId resident)
        {
            RequireKnownResident(resident);
            world.TouchControlGeneration(Writer, resident);
            automationEnabled = false;
        }

        public void EnableAutomation()
        {
            automationEnabled = true;
        }

        // Submit one envelope. All validation runs before any mutation, so a
        // rejection leaves current action, queue and world state unchanged.
        // Accepted resident commands must carry exactly the next resident
        // sequence (current + 1); accepted residentless commands carry exactly
        // the next world-command sequence. Issuers read the current stamp from
        // CurrentStamp or Snapshot; anything older (or skipped) is StaleGeneration.
        public CommandResult Submit(CommandEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException("envelope");
            }
            if (envelope.Epoch != world.Epoch)
            {
                return Log(envelope, CommandResult.Reject(RejectReason.StaleEpoch));
            }
            if (!HasValidShape(envelope))
            {
                return Log(envelope, CommandResult.Reject(RejectReason.InvalidParameters));
            }
            if (envelope.Source == CommandSource.PlayerAutomated && !automationEnabled)
            {
                return Log(envelope, CommandResult.Reject(RejectReason.StaleGeneration));
            }

            bool residentScoped = IsResidentScoped(envelope.Kind);
            ResidentRecord record = null;
            if (residentScoped)
            {
                if (!world.TryGetResident(envelope.Resident.Value, out record))
                {
                    return Log(envelope, CommandResult.Reject(RejectReason.InvalidParameters));
                }
                if (envelope.ResidentSequence != record.ResidentSequence + 1u)
                {
                    return Log(envelope, CommandResult.Reject(RejectReason.StaleGeneration));
                }
            }
            else if (envelope.ResidentSequence != worldSequence + 1u)
            {
                return Log(envelope, CommandResult.Reject(RejectReason.StaleGeneration));
            }

            if (IsActionCommand(envelope.Kind) && !record.IsAlive)
            {
                return Log(envelope, CommandResult.Reject(RejectReason.ResidentDeceased));
            }

            if (envelope.Kind == CommandKind.DirectActivity || envelope.Kind == CommandKind.TravelTo)
            {
                ActivityRule rule;
                if (envelope.Kind == CommandKind.DirectActivity)
                {
                    if (!TryFindActivity(envelope.Activity.Value.Code, out rule))
                    {
                        return Log(envelope, CommandResult.Reject(RejectReason.UnknownActivity));
                    }
                    if (!IsStageEligible(rule, record))
                    {
                        return Log(envelope, CommandResult.Reject(RejectReason.StageIneligible));
                    }
                }
                if (envelope.TargetId.HasValue && !IsKnownCommandTarget(envelope))
                {
                    return Log(envelope, CommandResult.Reject(RejectReason.TargetMissing));
                }
                if (QueueModeOf(envelope) == QueueMode.Append
                    && CommandQueuePolicy.AppendIsFull(CountPendingPlayer(envelope.Resident.Value)))
                {
                    return Log(envelope, CommandResult.Reject(RejectReason.QueueFull));
                }
            }

            if (envelope.Kind == CommandKind.CancelQueued
                && !PendingContains(envelope.Resident.Value, envelope.TargetId.Value))
            {
                return Log(envelope, CommandResult.Reject(RejectReason.InvalidParameters));
            }

            if (envelope.Kind == CommandKind.WillEdit
                && !IsValidWill(((CommandParameters.Will)envelope.Parameters).Value, record))
            {
                return Log(envelope, CommandResult.Reject(RejectReason.InvalidParameters));
            }

            if (envelope.Kind == CommandKind.CaregiverAssign)
            {
                ResidentId guardian = ((CommandParameters.Caregiver)envelope.Parameters).Guardian;
                ResidentRecord guardianRecord;
                if (guardian == envelope.Resident.Value || !world.TryGetResident(guardian, out guardianRecord))
                {
                    return Log(envelope, CommandResult.Reject(RejectReason.InvalidParameters));
                }
            }

            // Validation passed: consume the sequence, then dispatch.
            if (residentScoped)
            {
                world.NextResidentSequence(Writer, record.Id);
            }
            else
            {
                worldSequence++;
            }

            switch (envelope.Kind)
            {
                case CommandKind.DirectActivity:
                case CommandKind.TravelTo:
                    AcceptActionCommand(envelope, record);
                    break;
                case CommandKind.CancelCurrent:
                    AcceptCancelCurrent(envelope, record);
                    break;
                case CommandKind.CancelQueued:
                    AcceptCancelQueued(envelope);
                    break;
                case CommandKind.Pause:
                    clock.Pause();
                    break;
                case CommandKind.Resume:
                    clock.Resume();
                    break;
                case CommandKind.SetSpeed:
                    clock.SetSpeed(((CommandParameters.Speed)envelope.Parameters).Value);
                    break;
                case CommandKind.RenameResident:
                    world.RenameResident(Writer, record.Id, ((CommandParameters.Rename)envelope.Parameters).Name);
                    break;
                case CommandKind.WillEdit:
                    WillRecord authored = ((CommandParameters.Will)envelope.Parameters).Value;
                    if (authored == null)
                    {
                        lastWills.Remove(record.Id);
                    }
                    else
                    {
                        lastWills[record.Id] = authored;
                    }
                    break;
                case CommandKind.CaregiverAssign:
                    caregiverDesignations[record.Id] = ((CommandParameters.Caregiver)envelope.Parameters).Guardian;
                    break;
                default:
                    break;
            }

            return Log(envelope, CommandResult.Accept());
        }

        // Apply a validated gateway decision proposal for one resident.
        // Rejects unless the full freshness stamp still matches current state,
        // the choice is inside the offered set, automation is enabled and the
        // catalog/target preconditions revalidate. Acceptance appends a
        // ModelProposed entry (removing queued non-player work first) and
        // invalidates the used stamp via the decision-slot bump; a replayed
        // proposal is StaleGeneration.
        public CommandResult ApplyProposal(ResidentId resident, DecisionProposal proposal,
            IReadOnlyList<AvailableAction> offered)
        {
            if (proposal == null)
            {
                throw new ArgumentNullException("proposal");
            }
            if (offered == null)
            {
                throw new ArgumentNullException("offered");
            }
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (proposal.RespondedTo.Epoch != world.Epoch)
            {
                return CommandResult.Reject(RejectReason.StaleEpoch);
            }
            if (!proposal.IsFresh(CurrentStamp(resident)))
            {
                return CommandResult.Reject(RejectReason.StaleGeneration);
            }
            if (!automationEnabled)
            {
                return CommandResult.Reject(RejectReason.StaleGeneration);
            }
            if (!OfferedContains(offered, proposal.Activity, proposal.TargetId))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            if (!record.IsAlive)
            {
                return CommandResult.Reject(RejectReason.ResidentDeceased);
            }
            ActivityRule rule;
            if (!TryFindActivity(proposal.Activity.Code, out rule))
            {
                return CommandResult.Reject(RejectReason.UnknownActivity);
            }
            if (!IsStageEligible(rule, record))
            {
                return CommandResult.Reject(RejectReason.StageIneligible);
            }
            if (proposal.TargetId.HasValue && !IsKnownTarget(proposal.TargetId.Value))
            {
                return CommandResult.Reject(RejectReason.TargetMissing);
            }
            ResidentQueue queue = QueueFor(resident);
            if (CommandQueuePolicy.AppendIsFull(CountPlayer(queue)))
            {
                return CommandResult.Reject(RejectReason.QueueFull);
            }
            RemovePendingNonPlayer(queue);
            queue.Pending.Add(new ActionEntry(
                world.AllocateCommand(Writer),
                world.AllocateAction(Writer),
                CommandKind.DirectActivity,
                proposal.Activity, proposal.TargetId, ActionOwner.ModelProposed, null));
            world.TouchDecisionSlot(Writer, resident);
            return CommandResult.Accept();
        }

        // Local-autonomy hook (and test fixture): append one Autonomous entry.
        // Refuses without mutation when the resident is unknown or deceased,
        // the activity or target is invalid, or 8 entries are already pending.
        public bool EnqueueAutonomous(ResidentId resident, ActivityId activity, long? targetId)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            ActivityRule rule;
            if (!record.IsAlive
                || !TryFindActivity(activity.Code, out rule)
                || !IsStageEligible(rule, record)
                || (targetId.HasValue && !IsKnownTarget(targetId.Value)))
            {
                return false;
            }
            ResidentQueue queue = QueueFor(resident);
            if (queue.Pending.Count >= CommandQueuePolicy.MaxPending)
            {
                return false;
            }
            queue.Pending.Add(new ActionEntry(
                world.AllocateCommand(Writer),
                world.AllocateAction(Writer),
                CommandKind.DirectActivity,
                activity, targetId, ActionOwner.Autonomous, null));
            world.TouchDecisionSlot(Writer, resident);
            return true;
        }

        // A local destination decision uses the same queue and ownership as an activity.
        public bool EnqueueAutonomousTravel(ResidentId resident, ObjectId target)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record) || !record.IsAlive
                || record.Stage < LifeStage.Child || !IsKnownTarget((long)target)) return false;
            ResidentQueue queue = QueueFor(resident);
            if (queue.Pending.Count >= CommandQueuePolicy.MaxPending) return false;
            queue.Pending.Add(new ActionEntry(world.AllocateCommand(Writer), world.AllocateAction(Writer),
                CommandKind.TravelTo, new ActivityId(null), (long)target, ActionOwner.Autonomous, null,
                TravelTargetKind.Object));
            world.TouchDecisionSlot(Writer, resident);
            return true;
        }

        // Promote the pending head to current at an unpaused tick. Returns
        // false while paused or when there is nothing to promote; EXEC owns
        // completion-driven promotion, this covers the arbitrator's own
        // Immediate-while-paused and post-cancel transitions.
        public bool PromoteNext(ResidentId resident)
        {
            RequireKnownResident(resident);
            if (clock.Paused)
            {
                return false;
            }
            ResidentQueue queue = QueueFor(resident);
            if (queue.Current != null || queue.Pending.Count == 0)
            {
                return false;
            }
            queue.Current = queue.Pending[0];
            queue.Pending.RemoveAt(0);
            world.TouchDecisionSlot(Writer, resident);
            return true;
        }

        // Natural-completion hook for EXEC: when the in-progress action reaches
        // a natural terminal (completed, failed or abandoned — never
        // interrupted, which the arbitrator already released), EXEC reports it
        // here with its id. A matching current is released and the pending
        // head, if any, starts at once as the new current, exactly once. A
        // null current or an id mismatch (stale or duplicate report) is a
        // no-op returning false, so a repeated signal can never start a second
        // action. Each successful mutation advances decision freshness;
        // resident sequence and control generation are preserved.
        public bool CompleteCurrent(ResidentId resident, ActionInstanceId action)
        {
            RequireKnownResident(resident);
            ResidentQueue queue = QueueFor(resident);
            if (queue.Current == null || queue.Current.Action != action)
            {
                return false;
            }
            queue.Current = null;
            if (queue.Pending.Count > 0)
            {
                queue.Current = queue.Pending[0];
                queue.Pending.RemoveAt(0);
            }
            world.TouchDecisionSlot(Writer, resident);
            return true;
        }

        public FreshnessStamp CurrentStamp(ResidentId resident)
        {
            ResidentRecord record = RequireKnownResident(resident);
            return new FreshnessStamp(
                world.Epoch,
                record.ResidentSequence,
                QueueFor(resident).Pending.Count,
                record.Home,
                record.DecisionSlotGeneration,
                record.ControlGeneration);
        }

        // Read-only decision input: stage-eligible catalog activities with
        // unbound targets plus the player queue head. Full resident state
        // stays SIM-CORE owned; NAV binds targets.
        public ActionContextSnapshot Snapshot(ResidentId resident)
        {
            ResidentRecord record = RequireKnownResident(resident);
            List<AvailableAction> available = new List<AvailableAction>();
            for (int i = 0; i < Catalog.Length; i++)
            {
                if (IsStageEligible(Catalog[i], record))
                {
                    available.Add(new AvailableAction(new ActivityId(Catalog[i].Code), null));
                }
            }
            CommandId? head = null;
            List<ActionEntry> pending = QueueFor(resident).Pending;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Owner == ActionOwner.Player)
                {
                    head = pending[i].Command;
                    break;
                }
            }
            return new ActionContextSnapshot(CurrentStamp(resident), available.AsReadOnly(), head);
        }

        public ActionEntry GetCurrent(ResidentId resident)
        {
            RequireKnownResident(resident);
            return QueueFor(resident).Current;
        }

        public ActionEntry[] GetPending(ResidentId resident)
        {
            RequireKnownResident(resident);
            return QueueFor(resident).Pending.ToArray();
        }

        public bool TryGetLastWill(ResidentId resident, out WillRecord will)
        {
            return lastWills.TryGetValue(resident, out will);
        }

        // ESTATE will validation needs real furniture ownership. The stack
        // binds the live Housing after it exists; older callers that do not
        // bind still validate object high-water and duplicates.
        internal void BindHousing(Housing housing)
        {
            willHousing = housing;
        }

        // A saved will was valid when authored; SAVE restores it through the
        // normal command path so sequence/command marks stay exact, but a
        // recipient that has since vanished or an heirloom since sold must
        // not turn the restore into a silent clear. New player edits still
        // run every existence and ownership check.
        internal void BeginWillRestore()
        {
            restoringWill = true;
        }

        internal void EndWillRestore()
        {
            restoringWill = false;
        }

        public bool TryGetCaregiver(ResidentId resident, out ResidentId guardian)
        {
            return caregiverDesignations.TryGetValue(resident, out guardian);
        }

        private void AcceptActionCommand(CommandEnvelope envelope, ResidentRecord record)
        {
            ResidentQueue queue = QueueFor(record.Id);
            ActivityId activity = envelope.Kind == CommandKind.DirectActivity
                ? envelope.Activity.Value
                : new ActivityId(null);
            ActionEntry entry = new ActionEntry(
                envelope.Id,
                world.AllocateAction(Writer),
                envelope.Kind,
                activity,
                envelope.TargetId,
                ActionOwner.Player,
                envelope.Source,
                envelope.Parameters is CommandParameters.Travel travel ? travel.TargetKind : TravelTargetKind.Unspecified);
            if (QueueModeOf(envelope) == QueueMode.Immediate)
            {
                InterruptCurrent(queue, record.Id, envelope.Id);
                for (int i = 0; i < queue.Pending.Count; i++)
                {
                    MarkSuperseded(queue.Pending[i].Command, envelope.Id);
                }
                queue.Pending.Clear();
                queue.Pending.Add(entry);
                world.TouchDecisionSlot(Writer, record.Id);
                TakeoverIfManual(envelope, record.Id);
                if (!clock.Paused)
                {
                    PromoteNext(record.Id);
                }
                return;
            }
            RemovePendingNonPlayer(queue);
            queue.Pending.Add(entry);
            world.TouchDecisionSlot(Writer, record.Id);
            TakeoverIfManual(envelope, record.Id);
        }

        private void AcceptCancelCurrent(CommandEnvelope envelope, ResidentRecord record)
        {
            ResidentQueue queue = QueueFor(record.Id);
            if (queue.Current == null)
            {
                return;
            }
            InterruptCurrent(queue, record.Id, envelope.Id);
            world.TouchDecisionSlot(Writer, record.Id);
            TakeoverIfManual(envelope, record.Id);
        }

        private void AcceptCancelQueued(CommandEnvelope envelope)
        {
            ResidentQueue queue = QueueFor(envelope.Resident.Value);
            for (int i = 0; i < queue.Pending.Count; i++)
            {
                if ((long)queue.Pending[i].Command == envelope.TargetId.Value)
                {
                    queue.Pending.RemoveAt(i);
                    world.TouchDecisionSlot(Writer, envelope.Resident.Value);
                    TakeoverIfManual(envelope, envelope.Resident.Value);
                    return;
                }
            }
        }

        // Manual human/voice action commands that change the queue seize
        // control: bump the control generation and disable PlayerAutomated
        // acceptance until explicit re-enable (BP-SIM-04 takeover).
        private void TakeoverIfManual(CommandEnvelope envelope, ResidentId resident)
        {
            if (envelope.Source == CommandSource.PlayerHuman || envelope.Source == CommandSource.VoiceMapped)
            {
                world.TouchControlGeneration(Writer, resident);
                automationEnabled = false;
            }
        }

        private void InterruptCurrent(ResidentQueue queue, ResidentId resident, CommandId by)
        {
            if (queue.Current == null)
            {
                return;
            }
            interrupted.Add(new InterruptionRecord(
                queue.Current.Action, queue.Current.Activity, queue.Current.Owner, by));
            queue.Current = null;
        }

        private void RemovePendingNonPlayer(ResidentQueue queue)
        {
            for (int i = queue.Pending.Count - 1; i >= 0; i--)
            {
                if (queue.Pending[i].Owner != ActionOwner.Player)
                {
                    queue.Pending.RemoveAt(i);
                }
            }
        }

        private int CountPendingPlayer(ResidentId resident)
        {
            return CountPlayer(QueueFor(resident));
        }

        private static int CountPlayer(ResidentQueue queue)
        {
            int count = 0;
            for (int i = 0; i < queue.Pending.Count; i++)
            {
                if (queue.Pending[i].Owner == ActionOwner.Player)
                {
                    count++;
                }
            }
            return count;
        }

        private bool PendingContains(ResidentId resident, long commandId)
        {
            List<ActionEntry> pending = QueueFor(resident).Pending;
            for (int i = 0; i < pending.Count; i++)
            {
                if ((long)pending[i].Command == commandId)
                {
                    return true;
                }
            }
            return false;
        }

        private void MarkSuperseded(CommandId replaced, CommandId by)
        {
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].Command == replaced)
                {
                    log[i].MarkSuperseded(by);
                }
            }
        }

        private CommandResult Log(CommandEnvelope envelope, CommandResult result)
        {
            log.Add(new CommandLogEntry(envelope.Id, envelope.Kind, envelope.Resident,
                result.Accepted, result.Reason));
            return result;
        }

        private ResidentQueue QueueFor(ResidentId resident)
        {
            ResidentQueue queue;
            if (!queues.TryGetValue(resident, out queue))
            {
                queue = new ResidentQueue();
                queues.Add(resident, queue);
            }
            return queue;
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

        private bool IsKnownCommandTarget(CommandEnvelope command)
        {
            var travel = command.Parameters as CommandParameters.Travel;
            if (travel == null || travel.TargetKind == TravelTargetKind.Unspecified)
                return IsKnownTarget(command.TargetId.Value);
            if (travel.TargetKind == TravelTargetKind.Object)
                return command.TargetId.Value > 0 && command.TargetId.Value < world.NextObjectId;
            ResidentRecord resident;
            return world.TryGetResident((ResidentId)command.TargetId.Value, out resident);
        }

        private bool IsKnownTarget(long targetId)
        {
            ResidentRecord resident;
            if (world.TryGetResident((ResidentId)targetId, out resident))
            {
                return true;
            }
            return targetId > 0L && targetId < world.NextObjectId;
        }

        private static bool OfferedContains(IReadOnlyList<AvailableAction> offered,
            ActivityId activity, long? targetId)
        {
            for (int i = 0; i < offered.Count; i++)
            {
                if (offered[i].Activity == activity && offered[i].TargetId == targetId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindActivity(string code, out ActivityRule rule)
        {
            for (int i = 0; i < Catalog.Length; i++)
            {
                if (string.Equals(Catalog[i].Code, code, StringComparison.Ordinal))
                {
                    rule = Catalog[i];
                    return true;
                }
            }
            rule = default(ActivityRule);
            return false;
        }

        private static bool IsStageEligible(ActivityRule rule, ResidentRecord record)
        {
            // The care-dependent resident cannot also be the ACT-HOME-05 caregiver.
            if (record.CareRequired && rule.Code == "ACT-HOME-05")
            {
                return false;
            }
            if (record.Stage < rule.MinStage || record.Stage > rule.MaxStage)
            {
                if (!rule.TeenAtLevelOneOnly || record.Stage != LifeStage.Teen || record.CareerLevel > 1)
                {
                    return false;
                }
            }
            return true;
        }

        // Null is the explicit clear operation. A valid will names only live
        // household recipients (plain id, H<n> or household:<n>), Charity, or
        // allocated objects the author owns; duplicates and absent recipients
        // are refused before the last valid will can be replaced.
        private bool IsValidWill(WillRecord will, ResidentRecord author)
        {
            if (will == null)
            {
                return true;
            }
            if (will.MoneyShares == null || will.Heirlooms == null || will.CareHouseholds == null)
            {
                return false;
            }
            if (will.HomeSharePercent < 0 || will.HomeSharePercent > 100)
            {
                return false;
            }
            int total = 0;
            for (int i = 0; i < will.MoneyShares.Count; i++)
            {
                if (will.MoneyShares[i].Percent < 0)
                {
                    return false;
                }
                total += will.MoneyShares[i].Percent;
                if (!restoringWill && !IsKnownWillRecipient(will.MoneyShares[i].Recipient))
                {
                    return false;
                }
            }
            if (total != 100)
            {
                return false;
            }
            HashSet<ObjectId> seen = new HashSet<ObjectId>();
            for (int i = 0; i < will.Heirlooms.Count; i++)
            {
                ObjectId id = will.Heirlooms[i];
                if ((long)id <= 0L || (long)id >= world.NextObjectId || !seen.Add(id))
                {
                    return false;
                }
                if (!restoringWill && willHousing != null)
                {
                    HousingItem item = willHousing.GetItem(id);
                    if (item != null && !OwnsItem(item.Owner, author))
                    {
                        return false;
                    }
                }
            }
            for (int i = 0; i < will.CareHouseholds.Count; i++)
            {
                if (!restoringWill && !world.HasHousehold(will.CareHouseholds[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsKnownWillRecipient(string recipient)
        {
            if (string.Equals(recipient, "Charity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            HouseholdId household;
            return TryParseHousehold(recipient, out household);
        }

        // The legacy string surface carries three household spellings; all
        // resolve to a real household id so an absent recipient is refused.
        private bool TryParseHousehold(string recipient, out HouseholdId household)
        {
            household = default(HouseholdId);
            string text = (recipient ?? string.Empty).Trim();
            if (text.StartsWith("household:", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(10);
            }
            else if (text.Length > 1 && (text[0] == 'H' || text[0] == 'h')
                && char.IsDigit(text[1]))
            {
                text = text.Substring(1);
            }
            long id;
            if (!long.TryParse(text, out id) || id <= 0L)
            {
                return false;
            }
            household = (HouseholdId)id;
            return world.HasHousehold(household);
        }

        private static bool OwnsItem(AccountRef owner, ResidentRecord author)
        {
            if (owner.IsPersonal)
            {
                return owner.Resident == author.Id;
            }
            if (owner.IsShared)
            {
                return owner.Household == author.Household;
            }
            return false;
        }

        private static bool IsResidentScoped(CommandKind kind)
        {
            switch (kind)
            {
                case CommandKind.DirectActivity:
                case CommandKind.TravelTo:
                case CommandKind.CancelCurrent:
                case CommandKind.CancelQueued:
                case CommandKind.SelectResident:
                case CommandKind.FollowResident:
                case CommandKind.RenameResident:
                case CommandKind.WillEdit:
                case CommandKind.CaregiverAssign:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsActionCommand(CommandKind kind)
        {
            switch (kind)
            {
                case CommandKind.DirectActivity:
                case CommandKind.TravelTo:
                case CommandKind.CancelCurrent:
                case CommandKind.CancelQueued:
                    return true;
                default:
                    return false;
            }
        }

        private static QueueMode QueueModeOf(CommandEnvelope envelope)
        {
            CommandParameters.Direct direct = envelope.Parameters as CommandParameters.Direct;
            if (direct != null)
            {
                return direct.Mode;
            }
            return ((CommandParameters.Travel)envelope.Parameters).Mode;
        }

        // Kind/parameter shape table. Any Kind value outside the contract
        // enum, any parameter object of the wrong type, or any field the Kind
        // does not use is InvalidParameters; VoiceMapped is further limited
        // to the ten voice-supported kinds.
        private static bool HasValidShape(CommandEnvelope envelope)
        {
            if (!Enum.IsDefined(typeof(CommandKind), envelope.Kind))
            {
                return false;
            }
            if (envelope.Source == CommandSource.VoiceMapped && !IsVoiceKind(envelope.Kind))
            {
                return false;
            }
            bool hasResident = envelope.Resident.HasValue;
            bool hasActivity = envelope.Activity.HasValue;
            bool hasTarget = envelope.TargetId.HasValue;
            switch (envelope.Kind)
            {
                case CommandKind.DirectActivity:
                    return hasResident && hasActivity && !string.IsNullOrEmpty(envelope.Activity.Value.Code)
                        && envelope.Parameters is CommandParameters.Direct;
                case CommandKind.TravelTo:
                    return hasResident && !hasActivity && envelope.Parameters is CommandParameters.Travel travel
                        && Enum.IsDefined(typeof(TravelTargetKind), travel.TargetKind)
                        && (envelope.Source != CommandSource.VoiceMapped || hasTarget)
                        && (hasTarget || travel.TargetKind == TravelTargetKind.Unspecified);
                case CommandKind.CancelCurrent:
                    return hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.None;
                case CommandKind.CancelQueued:
                    return hasResident && !hasActivity && hasTarget
                        && envelope.Parameters is CommandParameters.None;
                case CommandKind.Pause:
                case CommandKind.Resume:
                    return !hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.None;
                case CommandKind.SetSpeed:
                    return !hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.Speed
                        && IsDefinedSpeed(((CommandParameters.Speed)envelope.Parameters).Value);
                case CommandKind.SelectResident:
                case CommandKind.FollowResident:
                    return hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.None;
                case CommandKind.StopFollowing:
                case CommandKind.OpenMap:
                case CommandKind.SaveGame:
                    return !hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.None;
                case CommandKind.RenameResident:
                    return hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.Rename;
                case CommandKind.WillEdit:
                    return hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.Will;
                case CommandKind.CaregiverAssign:
                    return hasResident && !hasActivity && !hasTarget
                        && envelope.Parameters is CommandParameters.Caregiver;
                default:
                    return false;
            }
        }

        private static bool IsVoiceKind(CommandKind kind)
        {
            for (int i = 0; i < VoiceKinds.Length; i++)
            {
                if (VoiceKinds[i] == kind)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsDefinedSpeed(SimulationSpeed speed)
        {
            switch (speed)
            {
                case SimulationSpeed.Paused:
                case SimulationSpeed.Normal:
                case SimulationSpeed.Fast:
                case SimulationSpeed.Ultra:
                    return true;
                default:
                    return false;
            }
        }
    }
}
