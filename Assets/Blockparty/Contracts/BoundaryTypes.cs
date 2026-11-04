// Boundary types for the 12 candidate contracts (CONTRACT-BP-01..12).
// Binds SDoc BP-SEC-07 internal contracts; stamps interface v1.
// Pure data + pure predicates only: SIM-CORE remains the sole writer
// of authoritative state (BP-SYS-02). No static mutable state, so there
// is no Fast Enter Play Mode reset obligation in this file.
using System;
using System.Collections.Generic;

namespace Blockparty.Contracts
{
    // Interface stamp. Every type in this file is version 1 of the
    // IFC-LOCK boundary; consumers re-read on display refresh.
    public static class InterfaceVersion
    {
        public const int Value = 1;
    }

    // 64-bit monotonic per-world identifiers (BP-SEC-07). Numeric identities
    // are never reused in a world across save/load or epoch changes; the
    // next-id high-water marks persist in the save payload, not here.
    public readonly struct ResidentId : IEquatable<ResidentId>
    {
        public readonly long Value;
        public ResidentId(long value) { Value = value; }
        public static explicit operator long(ResidentId id) { return id.Value; }
        public static explicit operator ResidentId(long value) { return new ResidentId(value); }
        public bool Equals(ResidentId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is ResidentId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(ResidentId a, ResidentId b) { return a.Value == b.Value; }
        public static bool operator !=(ResidentId a, ResidentId b) { return a.Value != b.Value; }
    }

    public readonly struct HouseholdId : IEquatable<HouseholdId>
    {
        public readonly long Value;
        public HouseholdId(long value) { Value = value; }
        public static explicit operator long(HouseholdId id) { return id.Value; }
        public static explicit operator HouseholdId(long value) { return new HouseholdId(value); }
        public bool Equals(HouseholdId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is HouseholdId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(HouseholdId a, HouseholdId b) { return a.Value == b.Value; }
        public static bool operator !=(HouseholdId a, HouseholdId b) { return a.Value != b.Value; }
    }

    public readonly struct HomeId : IEquatable<HomeId>
    {
        public readonly long Value;
        public HomeId(long value) { Value = value; }
        public static explicit operator long(HomeId id) { return id.Value; }
        public static explicit operator HomeId(long value) { return new HomeId(value); }
        public bool Equals(HomeId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is HomeId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(HomeId a, HomeId b) { return a.Value == b.Value; }
        public static bool operator !=(HomeId a, HomeId b) { return a.Value != b.Value; }
    }

    public readonly struct ObjectId : IEquatable<ObjectId>
    {
        public readonly long Value;
        public ObjectId(long value) { Value = value; }
        public static explicit operator long(ObjectId id) { return id.Value; }
        public static explicit operator ObjectId(long value) { return new ObjectId(value); }
        public bool Equals(ObjectId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is ObjectId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(ObjectId a, ObjectId b) { return a.Value == b.Value; }
        public static bool operator !=(ObjectId a, ObjectId b) { return a.Value != b.Value; }
    }

    public readonly struct ActionInstanceId : IEquatable<ActionInstanceId>
    {
        public readonly long Value;
        public ActionInstanceId(long value) { Value = value; }
        public static explicit operator long(ActionInstanceId id) { return id.Value; }
        public static explicit operator ActionInstanceId(long value) { return new ActionInstanceId(value); }
        public bool Equals(ActionInstanceId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is ActionInstanceId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(ActionInstanceId a, ActionInstanceId b) { return a.Value == b.Value; }
        public static bool operator !=(ActionInstanceId a, ActionInstanceId b) { return a.Value != b.Value; }
    }

    public readonly struct EventId : IEquatable<EventId>
    {
        public readonly long Value;
        public EventId(long value) { Value = value; }
        public static explicit operator long(EventId id) { return id.Value; }
        public static explicit operator EventId(long value) { return new EventId(value); }
        public bool Equals(EventId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is EventId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(EventId a, EventId b) { return a.Value == b.Value; }
        public static bool operator !=(EventId a, EventId b) { return a.Value != b.Value; }
    }

    public readonly struct CommandId : IEquatable<CommandId>
    {
        public readonly long Value;
        public CommandId(long value) { Value = value; }
        public static explicit operator long(CommandId id) { return id.Value; }
        public static explicit operator CommandId(long value) { return new CommandId(value); }
        public bool Equals(CommandId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is CommandId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(CommandId a, CommandId b) { return a.Value == b.Value; }
        public static bool operator !=(CommandId a, CommandId b) { return a.Value != b.Value; }
    }

    public readonly struct GatewayRequestId : IEquatable<GatewayRequestId>
    {
        public readonly long Value;
        public GatewayRequestId(long value) { Value = value; }
        public static explicit operator long(GatewayRequestId id) { return id.Value; }
        public static explicit operator GatewayRequestId(long value) { return new GatewayRequestId(value); }
        public bool Equals(GatewayRequestId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is GatewayRequestId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(GatewayRequestId a, GatewayRequestId b) { return a.Value == b.Value; }
        public static bool operator !=(GatewayRequestId a, GatewayRequestId b) { return a.Value != b.Value; }
    }

    public readonly struct CaptureId : IEquatable<CaptureId>
    {
        public readonly long Value;
        public CaptureId(long value) { Value = value; }
        public static explicit operator long(CaptureId id) { return id.Value; }
        public static explicit operator CaptureId(long value) { return new CaptureId(value); }
        public bool Equals(CaptureId other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is CaptureId other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(CaptureId a, CaptureId b) { return a.Value == b.Value; }
        public static bool operator !=(CaptureId a, CaptureId b) { return a.Value != b.Value; }
    }

    // 32-bit counter incremented on every world load or replacement.
    public readonly struct WorldEpoch : IEquatable<WorldEpoch>
    {
        public readonly uint Value;
        public WorldEpoch(uint value) { Value = value; }
        public static explicit operator uint(WorldEpoch epoch) { return epoch.Value; }
        public static explicit operator WorldEpoch(uint value) { return new WorldEpoch(value); }
        public bool Equals(WorldEpoch other) { return Value == other.Value; }
        public override bool Equals(object obj) { return obj is WorldEpoch other && Equals(other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
        public static bool operator ==(WorldEpoch a, WorldEpoch b) { return a.Value == b.Value; }
        public static bool operator !=(WorldEpoch a, WorldEpoch b) { return a.Value != b.Value; }
    }

    // Stable ACT-code string from the versioned activity catalog.
    // FNV-1a hash: deterministic across runs (String.GetHashCode is not).
    public readonly struct ActivityId : IEquatable<ActivityId>
    {
        public readonly string Code;
        public ActivityId(string code) { Code = code ?? string.Empty; }
        public bool Equals(ActivityId other) { return string.Equals(Code, other.Code, StringComparison.Ordinal); }
        public override bool Equals(object obj) { return obj is ActivityId other && Equals(other); }
        public override int GetHashCode()
        {
            unchecked
            {
                uint hash = 2166136261u;
                string code = Code ?? string.Empty;
                for (int i = 0; i < code.Length; i++)
                {
                    hash ^= code[i];
                    hash *= 16777619u;
                }
                return (int)hash;
            }
        }
        public static bool operator ==(ActivityId a, ActivityId b) { return a.Equals(b); }
        public static bool operator !=(ActivityId a, ActivityId b) { return !a.Equals(b); }
        public override string ToString() { return Code; }
    }

    // CONTRACT-BP-03-TIME. One shared clock: 64-bit game seconds since world
    // creation plus speed and pause flags. Fixed ticks of 6 game seconds
    // (10 ticks per game minute) independent of render frames.
    public enum SimulationSpeed
    {
        Paused = 0,
        Normal = 1,
        Fast = 3,
        Ultra = 7,
    }

    public enum AgingPreset
    {
        Off,
        Slow,
        Moderate,
        Accelerated,
    }

    public static class SimTick
    {
        public const long TickGameSeconds = 6;
        public const int TicksPerGameMinute = 10;
    }

    public readonly struct ClockState
    {
        public readonly long GameSeconds;
        public readonly SimulationSpeed Speed;
        public readonly bool Paused;

        public ClockState(long gameSeconds, SimulationSpeed speed, bool paused)
        {
            GameSeconds = gameSeconds;
            Speed = speed;
            Paused = paused;
        }
    }

    // CONTRACT-BP-01-CMD. Sources are annotations within action owner Player,
    // not three action owners.
    public enum CommandSource
    {
        PlayerHuman,
        PlayerAutomated,
        VoiceMapped,
    }

    public enum CommandKind
    {
        SelectResident,
        FollowResident,
        StopFollowing,
        DirectActivity,
        TravelTo,
        Pause,
        Resume,
        SetSpeed,
        CancelCurrent,
        CancelQueued,
        OpenMap,
        SaveGame,
        RenameResident,
        WillEdit,
        CaregiverAssign,
    }

    // Unspecified retains the pre-existing object-first command/save behavior.
    public enum TravelTargetKind { Unspecified, Object, Resident }

    public enum QueueMode
    {
        Immediate,
        Append,
    }

    // Typed union of per-Kind parameters. Kinds without extra data use None.
    public abstract class CommandParameters
    {
        private CommandParameters() { }

        public sealed class None : CommandParameters
        {
            public static readonly None Instance = new None();
        }

        public sealed class Direct : CommandParameters
        {
            public readonly QueueMode Mode;
            public Direct(QueueMode mode) { Mode = mode; }
        }

        public sealed class Travel : CommandParameters
        {
            public readonly QueueMode Mode;
            public readonly TravelTargetKind TargetKind;
            public Travel(QueueMode mode, TravelTargetKind targetKind = TravelTargetKind.Unspecified)
            { Mode = mode; TargetKind = targetKind; }
        }

        public sealed class Speed : CommandParameters
        {
            public readonly SimulationSpeed Value;
            public Speed(SimulationSpeed value) { Value = value; }
        }

        public sealed class Rename : CommandParameters
        {
            public readonly string Name;
            public Rename(string name) { Name = name ?? string.Empty; }
        }

        public sealed class Will : CommandParameters
        {
            public readonly WillRecord Value;
            public Will(WillRecord value) { Value = value; }
        }

        public sealed class Caregiver : CommandParameters
        {
            public readonly ResidentId Guardian;
            public Caregiver(ResidentId guardian) { Guardian = guardian; }
        }
    }

    public sealed class CommandEnvelope
    {
        public readonly CommandId Id;
        public readonly WorldEpoch Epoch;
        public readonly long IssuedAtGameSeconds;
        public readonly CommandSource Source;
        public readonly uint ResidentSequence;
        public readonly CommandKind Kind;
        public readonly ResidentId? Resident;
        public readonly ActivityId? Activity;
        public readonly long? TargetId;
        public readonly CommandParameters Parameters;

        public CommandEnvelope(
            CommandId id,
            WorldEpoch epoch,
            long issuedAtGameSeconds,
            CommandSource source,
            uint residentSequence,
            CommandKind kind,
            ResidentId? resident,
            ActivityId? activity,
            long? targetId,
            CommandParameters parameters)
        {
            Id = id;
            Epoch = epoch;
            IssuedAtGameSeconds = issuedAtGameSeconds;
            Source = source;
            ResidentSequence = residentSequence;
            Kind = kind;
            Resident = resident;
            Activity = activity;
            TargetId = targetId;
            Parameters = parameters ?? CommandParameters.None.Instance;
        }
    }

    public enum RejectReason
    {
        UnknownActivity,
        StageIneligible,
        TargetMissing,
        TargetInvalidated,
        InsufficientFunds,
        ObjectUnavailable,
        ObjectOccupied,
        NoPath,
        QueueFull,
        InvalidParameters,
        ResidentDeceased,
        StaleEpoch,
        StaleGeneration,
        UnsafeWeather,
    }

    public readonly struct CommandResult
    {
        public readonly bool Accepted;
        public readonly bool Superseded;
        public readonly RejectReason Reason;
        public readonly CommandId SupersededBy;

        private CommandResult(bool accepted, bool superseded, RejectReason reason, CommandId supersededBy)
        {
            Accepted = accepted;
            Superseded = superseded;
            Reason = reason;
            SupersededBy = supersededBy;
        }

        public static CommandResult Accept() { return new CommandResult(true, false, default(RejectReason), default(CommandId)); }
        public static CommandResult Reject(RejectReason reason) { return new CommandResult(false, false, reason, default(CommandId)); }
        public static CommandResult Supersede(CommandId by) { return new CommandResult(false, true, default(RejectReason), by); }
    }

    // Resident action queue bound is 8 pending entries. An accepted Immediate
    // command replaces pending entries under the queue-mode policy and never
    // overflows; Append first removes pending non-player entries, then
    // rejects QueueFull when 8 pending player entries remain.
    public static class CommandQueuePolicy
    {
        public const int MaxPending = 8;

        public static bool AppendIsFull(int pendingPlayerCount)
        {
            return pendingPlayerCount >= MaxPending;
        }
    }

    // Freshness tuple (DES-BP-FRESHNESS-TUPLE): context version carried by
    // every request. Residentless purposes use the world-command sequence in
    // place of ResidentSequence.
    public readonly struct FreshnessStamp : IEquatable<FreshnessStamp>
    {
        public readonly WorldEpoch Epoch;
        public readonly uint Sequence;
        public readonly int QueueLength;
        public readonly HomeId Location;
        public readonly uint DecisionSlotGeneration;
        public readonly uint ControlGeneration;

        public FreshnessStamp(
            WorldEpoch epoch,
            uint sequence,
            int queueLength,
            HomeId location,
            uint decisionSlotGeneration,
            uint controlGeneration)
        {
            Epoch = epoch;
            Sequence = sequence;
            QueueLength = queueLength;
            Location = location;
            DecisionSlotGeneration = decisionSlotGeneration;
            ControlGeneration = controlGeneration;
        }

        public bool Matches(FreshnessStamp current)
        {
            return Epoch == current.Epoch
                && Sequence == current.Sequence
                && QueueLength == current.QueueLength
                && Location == current.Location
                && DecisionSlotGeneration == current.DecisionSlotGeneration
                && ControlGeneration == current.ControlGeneration;
        }

        public bool Equals(FreshnessStamp other) { return Matches(other); }
        public override bool Equals(object obj) { return obj is FreshnessStamp other && Equals(other); }
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Epoch.GetHashCode();
                hash = hash * 31 + Sequence.GetHashCode();
                hash = hash * 31 + QueueLength.GetHashCode();
                hash = hash * 31 + Location.GetHashCode();
                hash = hash * 31 + DecisionSlotGeneration.GetHashCode();
                hash = hash * 31 + ControlGeneration.GetHashCode();
                return hash;
            }
        }
        public static bool operator ==(FreshnessStamp a, FreshnessStamp b) { return a.Matches(b); }
        public static bool operator !=(FreshnessStamp a, FreshnessStamp b) { return !a.Matches(b); }
    }

    // Read-only decision input: available ActivityIds with targets plus the
    // player queue head. Full resident state stays SIM-CORE owned.
    public readonly struct AvailableAction
    {
        public readonly ActivityId Activity;
        public readonly long? TargetId;

        public AvailableAction(ActivityId activity, long? targetId)
        {
            Activity = activity;
            TargetId = targetId;
        }
    }

    public sealed class ActionContextSnapshot
    {
        public readonly FreshnessStamp Version;
        public readonly IReadOnlyList<AvailableAction> Available;
        public readonly CommandId? PlayerQueueHead;

        public ActionContextSnapshot(
            FreshnessStamp version,
            IReadOnlyList<AvailableAction> available,
            CommandId? playerQueueHead)
        {
            Version = version;
            Available = available;
            PlayerQueueHead = playerQueueHead;
        }
    }

    // AI gateway (CONTRACT-BP-04/05/06/07). Vendor payloads are
    // adapter-private; the game side carries typed fields only.
    public enum GatewayPurpose
    {
        ResidentDecision,
        ImageGeneration,
        VoiceTranscription,
        ImageSearch,
    }

    // Timeout, denial, malformed output and unprovisioned adapter are
    // distinct logged outcome classes; each takes the defined local or
    // manual alternative. Free text never executes.
    public enum GatewayOutcome
    {
        Applied,
        Timeout,
        Denied,
        Cancelled,
        Malformed,
        Unprovisioned,
        Stale,
        RateLimitedLocal,
    }

    // Candidate service values, proposed and unmeasured (BP-SEC-07).
    public static class GatewayLimits
    {
        public const int ServiceTimeoutRealSeconds = 10;
        public const int DecisionFallbackGameMinutes = 3;
        public const int MaxConcurrentPerDecisionPurpose = 2;
        public const int MaxConcurrentGameWide = 8;
        public const int DecisionLogMaxRecords = 1024;
    }

    public sealed class GatewayRequest
    {
        public readonly GatewayRequestId Id;
        public readonly WorldEpoch Epoch;
        public readonly ResidentId? Resident;
        public readonly FreshnessStamp ContextVersion;
        public readonly string ModelId;
        public readonly long IssuedAtGameSeconds;
        public readonly GatewayPurpose Purpose;

        public GatewayRequest(
            GatewayRequestId id,
            WorldEpoch epoch,
            ResidentId? resident,
            FreshnessStamp contextVersion,
            string modelId,
            long issuedAtGameSeconds,
            GatewayPurpose purpose)
        {
            Id = id;
            Epoch = epoch;
            Resident = resident;
            ContextVersion = contextVersion;
            ModelId = modelId ?? string.Empty;
            IssuedAtGameSeconds = issuedAtGameSeconds;
            Purpose = purpose;
        }
    }

    // CONTRACT-BP-04-D1U. Bounded proposal: a response applies only if its
    // epoch, sequence, decision-slot and control generations still match and
    // the choice is inside the offered set.
    public sealed class DecisionProposal
    {
        public readonly GatewayRequestId Request;
        public readonly ActivityId Activity;
        public readonly long? TargetId;
        public readonly FreshnessStamp RespondedTo;

        public DecisionProposal(
            GatewayRequestId request,
            ActivityId activity,
            long? targetId,
            FreshnessStamp respondedTo)
        {
            Request = request;
            Activity = activity;
            TargetId = targetId;
            RespondedTo = respondedTo;
        }

        public bool IsFresh(FreshnessStamp current)
        {
            return RespondedTo.Matches(current);
        }
    }

    // CONTRACT-BP-05-IMG. WorkItem identity + author + ACT-CREA-01 + recorded
    // inputs. No preview/confirmation step exists in the canonical SDoc.
    public static class CreativeWork
    {
        public const string MakeWallArtActivity = "ACT-CREA-01";
    }

    public readonly struct WorkItemRef
    {
        public readonly ObjectId Item;
        public readonly ResidentId Author;
        public readonly string PersonalitySummary;
        public readonly EventId MemoryEvent;

        public WorkItemRef(ObjectId item, ResidentId author, string personalitySummary, EventId memoryEvent)
        {
            Item = item;
            Author = author;
            PersonalitySummary = personalitySummary ?? string.Empty;
            MemoryEvent = memoryEvent;
        }
    }

    // CONTRACT-BP-06-VOICE. The ten supported meanings; additions need a
    // recorded catalog change. Ambiguous or empty text maps to no meaning
    // and executes no command.
    public enum VoiceMeaning
    {
        SelectResident,
        FollowResident,
        StopFollowing,
        DirectResident,
        Pause,
        Resume,
        SetSpeed,
        CancelCurrent,
        OpenMap,
        SaveGame,
    }

    public readonly struct VoiceTranscript
    {
        public readonly string Text;
        public readonly VoiceMeaning? Meaning;

        public VoiceTranscript(string text, VoiceMeaning? meaning)
        {
            Text = text ?? string.Empty;
            Meaning = meaning;
        }

        public bool ExecutesCommand
        {
            get { return Meaning.HasValue; }
        }
    }

    // CONTRACT-BP-07-SAM. Outlines carry image coordinates only: no entity
    // identity, world location or activity fields exist on this contract.
    // Zero matches are shown as an empty outline list.
    public readonly struct ImagePoint
    {
        public readonly float X;
        public readonly float Y;

        public ImagePoint(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public sealed class SamOutline
    {
        public readonly CaptureId Capture;
        public readonly IReadOnlyList<ImagePoint> Polygon;

        public SamOutline(CaptureId capture, IReadOnlyList<ImagePoint> polygon)
        {
            Capture = capture;
            Polygon = polygon;
        }
    }

    public sealed class SamResult
    {
        public readonly CaptureId Capture;
        public readonly IReadOnlyList<SamOutline> Outlines;

        public SamResult(CaptureId capture, IReadOnlyList<SamOutline> outlines)
        {
            Capture = capture;
            Outlines = outlines;
        }
    }

    // CONTRACT-BP-08-RESOLVE. Read-only query over authoritative data; the
    // resolver is a query role, not a new component. Results are snapshots
    // at issue simulation time.
    public readonly struct ResidentSummary
    {
        public readonly ResidentId Id;
        public readonly string DisplayName;

        public ResidentSummary(ResidentId id, string displayName)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
        }
    }

    public interface IResidentDirectory
    {
        IReadOnlyList<ResidentSummary> SearchByName(string name);
        bool TryGet(ResidentId id, out ResidentSummary summary);
    }

    // CONTRACT-BP-02-EXEC. Exactly-once commit keyed by ActionInstanceId
    // (DES-BP-EFFECT-LEDGER). Recipient is one resident or one unordered
    // participant pair; pairs normalize order so both directions match.
    public enum LedgerState
    {
        Reserved,
        Committed,
    }

    public readonly struct LedgerRecipient : IEquatable<LedgerRecipient>
    {
        public readonly ResidentId First;
        public readonly ResidentId? Second;

        public LedgerRecipient(ResidentId resident)
        {
            First = resident;
            Second = null;
        }

        public LedgerRecipient(ResidentId a, ResidentId b)
        {
            if (((long)a) <= ((long)b))
            {
                First = a;
                Second = b;
            }
            else
            {
                First = b;
                Second = a;
            }
        }

        public bool Equals(LedgerRecipient other) { return First == other.First && Second == other.Second; }
        public override bool Equals(object obj) { return obj is LedgerRecipient other && Equals(other); }
        public override int GetHashCode()
        {
            unchecked
            {
                return First.GetHashCode() * 31 + (Second.HasValue ? Second.Value.GetHashCode() : 0);
            }
        }
        public static bool operator ==(LedgerRecipient a, LedgerRecipient b) { return a.Equals(b); }
        public static bool operator !=(LedgerRecipient a, LedgerRecipient b) { return !a.Equals(b); }
    }

    public sealed class EffectLedgerEntry
    {
        public readonly ActionInstanceId Action;
        public readonly string EffectKind;
        public readonly LedgerRecipient Recipient;
        public LedgerState State { get; private set; }

        public EffectLedgerEntry(ActionInstanceId action, string effectKind, LedgerRecipient recipient)
        {
            Action = action;
            EffectKind = effectKind ?? string.Empty;
            Recipient = recipient;
            State = LedgerState.Reserved;
        }

        // Completion checks the entry state before applying effects, so a
        // replayed completion is refused and never pays twice.
        public bool TryCommit()
        {
            if (State == LedgerState.Committed)
            {
                return false;
            }
            State = LedgerState.Committed;
            return true;
        }
    }

    // Object-slot interface (BP-SEC-07): a hold is exclusive until its
    // matching release. Approach pose is in world meters.
    public readonly struct ApproachPose
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public ApproachPose(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public readonly struct SlotHold : IEquatable<SlotHold>
    {
        public readonly ObjectId Object;
        public readonly int SlotIndex;
        public readonly ApproachPose Pose;
        public readonly ResidentId Holder;
        public readonly ActionInstanceId Action;

        public SlotHold(ObjectId @object, int slotIndex, ApproachPose pose, ResidentId holder, ActionInstanceId action)
        {
            Object = @object;
            SlotIndex = slotIndex;
            Pose = pose;
            Holder = holder;
            Action = action;
        }

        public bool Equals(SlotHold other)
        {
            return Object == other.Object && SlotIndex == other.SlotIndex && Holder == other.Holder && Action == other.Action;
        }
        public override bool Equals(object obj) { return obj is SlotHold other && Equals(other); }
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Object.GetHashCode();
                hash = hash * 31 + SlotIndex.GetHashCode();
                hash = hash * 31 + Holder.GetHashCode();
                hash = hash * 31 + Action.GetHashCode();
                return hash;
            }
        }
        public static bool operator ==(SlotHold a, SlotHold b) { return a.Equals(b); }
        public static bool operator !=(SlotHold a, SlotHold b) { return !a.Equals(b); }
    }

    // CONTRACT-BP-09-SAVE. System.Text.Json, UTF-8, schema v1. Atomic write:
    // snapshot between ticks, write tmp, flush, validate, retain bak, then
    // atomically replace the save file.
    public static class SaveFormat
    {
        public const int SchemaVersion = 2;
        public const string FileName = "save.json";
        public const string TempName = "save.tmp";
        public const string BackupName = "save.bak";
    }

    public readonly struct SaveHeader
    {
        public readonly int SchemaVersion;
        public readonly long WorldId;
        public readonly WorldEpoch Epoch;
        public readonly long SavedAtGameSeconds;
        public readonly string Sha256;

        public SaveHeader(int schemaVersion, long worldId, WorldEpoch epoch, long savedAtGameSeconds, string sha256)
        {
            SchemaVersion = schemaVersion;
            WorldId = worldId;
            Epoch = epoch;
            SavedAtGameSeconds = savedAtGameSeconds;
            Sha256 = sha256 ?? string.Empty;
        }

        // Unknown versions are refused by name; never silently loaded.
        public bool IsSupported
        {
            get { return SchemaVersion == 1 || SchemaVersion == SaveFormat.SchemaVersion; }
        }
    }

    // INPUT-ACCESS: the one remappable action inventory with its default
    // single-input bindings (keyboard keys or Mouse0..Mouse4, never a chord).
    // Accessibility owns the live overrides; SaveV1 validates against this
    // same table so disk never holds an unknown or conflicting binding.
    public static class InputActions
    {
        public static readonly string[] Names =
        {
            "CameraPanUp", "CameraPanDown", "CameraPanLeft", "CameraPanRight",
            "CameraZoomIn", "CameraZoomOut", "CameraRotateLeft", "CameraRotateRight",
            "SelectNext", "SelectPrevious", "OpenActionMenu", "CloseActionMenu",
            "Confirm", "Cancel", "SpeedUp", "SpeedDown", "Pause", "OpenMap",
            "OpenSettings", "ImmediateCommand", "AppendCommand", "Inspect"
        };

        private static readonly string[] Defaults =
        {
            "W", "S", "A", "D", "E", "Q", "R", "F",
            "N", "B", "Space", "X",
            "Return", "Backspace", "PageUp", "PageDown", "Escape", "M",
            "O", "I", "U", "H"
        };

        public static bool IsKnown(string action)
        {
            return IndexOf(action) >= 0;
        }

        public static string DefaultBinding(string action)
        {
            int index = IndexOf(action);
            return index < 0 ? string.Empty : Defaults[index];
        }

        private static int IndexOf(string action)
        {
            if (action == null)
            {
                return -1;
            }
            for (int i = 0; i < Names.Length; i++)
            {
                if (Names[i] == action)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    // Separately versioned local settings record: survives process restart
    // and world load, never mutates the paused world.
    // ACCESS adds the essential-cue flags and the input-remap overrides;
    // TextScale and ReducedMotion are reused, never duplicated.
    // INPUT-ACCESS adds cursor/camera speeds and pointer/narration toggles
    // as additive fields; older constructors supply the neutral defaults.
    public readonly struct SettingsRecord
    {
        public readonly int SchemaVersion;
        public readonly float MixerLevel;
        public readonly float TextScale;
        public readonly bool ReducedMotion;
        public readonly float AmbienceLevel;
        public readonly float WorldLevel;
        public readonly float VoiceLevel;
        public readonly float InterfaceLevel;
        public readonly bool NonColorCues;
        public readonly bool Captions;
        public readonly IReadOnlyDictionary<string, string> InputRemaps;
        public readonly float CursorSize;
        public readonly float CameraPanSpeed;
        public readonly float CameraZoomSpeed;
        public readonly float CameraRotateSpeed;
        public readonly bool HighContrastCursor;
        public readonly bool EdgeScroll;
        public readonly bool InvertX;
        public readonly bool InvertY;
        public readonly bool SnapSelection;
        public readonly bool NarrationEnabled;
        public readonly bool ToggleControls;

        public SettingsRecord(int schemaVersion, float mixerLevel, float textScale, bool reducedMotion)
            : this(schemaVersion, mixerLevel, textScale, reducedMotion,
                  mixerLevel, mixerLevel, mixerLevel, mixerLevel)
        {
        }

        public SettingsRecord(int schemaVersion, float mixerLevel, float textScale, bool reducedMotion,
            float ambienceLevel, float worldLevel, float voiceLevel, float interfaceLevel)
            : this(schemaVersion, mixerLevel, textScale, reducedMotion,
                  ambienceLevel, worldLevel, voiceLevel, interfaceLevel,
                  true, true, new Dictionary<string, string>())
        {
        }

        public SettingsRecord(int schemaVersion, float mixerLevel, float textScale, bool reducedMotion,
            float ambienceLevel, float worldLevel, float voiceLevel, float interfaceLevel,
            bool nonColorCues, bool captions, IReadOnlyDictionary<string, string> inputRemaps)
            : this(schemaVersion, mixerLevel, textScale, reducedMotion,
                  ambienceLevel, worldLevel, voiceLevel, interfaceLevel,
                  nonColorCues, captions, inputRemaps,
                  1f, 1f, 1f, 1f, false, false, false, false, false, false, false)
        {
        }

        public SettingsRecord(int schemaVersion, float mixerLevel, float textScale, bool reducedMotion,
            float ambienceLevel, float worldLevel, float voiceLevel, float interfaceLevel,
            bool nonColorCues, bool captions, IReadOnlyDictionary<string, string> inputRemaps,
            float cursorSize, float cameraPanSpeed, float cameraZoomSpeed, float cameraRotateSpeed,
            bool highContrastCursor, bool edgeScroll, bool invertX, bool invertY,
            bool snapSelection, bool narrationEnabled, bool toggleControls)
        {
            SchemaVersion = schemaVersion;
            MixerLevel = mixerLevel;
            TextScale = textScale;
            ReducedMotion = reducedMotion;
            AmbienceLevel = ambienceLevel;
            WorldLevel = worldLevel;
            VoiceLevel = voiceLevel;
            InterfaceLevel = interfaceLevel;
            NonColorCues = nonColorCues;
            Captions = captions;
            // Defensive copy: later caller mutation must not rewrite history.
            InputRemaps = inputRemaps == null
                ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()
                : new Dictionary<string, string>(inputRemaps);
            CursorSize = cursorSize;
            CameraPanSpeed = cameraPanSpeed;
            CameraZoomSpeed = cameraZoomSpeed;
            CameraRotateSpeed = cameraRotateSpeed;
            HighContrastCursor = highContrastCursor;
            EdgeScroll = edgeScroll;
            InvertX = invertX;
            InvertY = invertY;
            SnapSelection = snapSelection;
            NarrationEnabled = narrationEnabled;
            ToggleControls = toggleControls;
        }
    }

    // CONTRACT-BP-10-ASSET. Register entry gating build inclusion: an asset
    // without a register entry, or a paid-only candidate, is excluded from
    // the submission build.
    public readonly struct AssetRegistration
    {
        public readonly string AssetId;
        public readonly string SourceEdition;
        public readonly string License;
        public readonly string Attribution;
        public readonly bool ModificationAllowed;
        public readonly bool ExportAllowed;
        public readonly bool PaidOnly;

        public AssetRegistration(
            string assetId,
            string sourceEdition,
            string license,
            string attribution,
            bool modificationAllowed,
            bool exportAllowed,
            bool paidOnly)
        {
            AssetId = assetId ?? string.Empty;
            SourceEdition = sourceEdition ?? string.Empty;
            License = license ?? string.Empty;
            Attribution = attribution ?? string.Empty;
            ModificationAllowed = modificationAllowed;
            ExportAllowed = exportAllowed;
            PaidOnly = paidOnly;
        }

        public bool IsRegistered
        {
            get { return AssetId.Length > 0; }
        }

        public bool ExcludedFromSubmission
        {
            get { return !IsRegistered || PaidOnly; }
        }
    }

    // CONTRACT-BP-11-BUILD. Build identity must be recordable on every
    // runtime claim and recording.
    public readonly struct BuildIdentity
    {
        public readonly string BuildId;
        public readonly string UnityVersion;
        public readonly string CommitHash;
        public readonly string Platform;

        public BuildIdentity(string buildId, string unityVersion, string commitHash, string platform)
        {
            BuildId = buildId ?? string.Empty;
            UnityVersion = unityVersion ?? string.Empty;
            CommitHash = commitHash ?? string.Empty;
            Platform = platform ?? string.Empty;
        }
    }

    // CONTRACT-BP-12-LIFE. Settlements are idempotent by EventId ledger
    // across save/load. Full homes are skipped; survivor shares never move.
    public readonly struct PregnancyRecord
    {
        public readonly ResidentId Carrier;
        public readonly ResidentId OtherParent;
        public readonly EventId ConceptionEvent;
        public readonly long DueGameTime;
        public readonly int CapacityHold;

        public PregnancyRecord(ResidentId carrier, ResidentId otherParent, EventId conceptionEvent, long dueGameTime, int capacityHold)
        {
            Carrier = carrier;
            OtherParent = otherParent;
            ConceptionEvent = conceptionEvent;
            DueGameTime = dueGameTime;
            CapacityHold = capacityHold;
        }
    }

    public readonly struct MoneyShare
    {
        // Recipient is a HouseholdId or Charity marker; shares total 100.
        public readonly string Recipient;
        public readonly int Percent;

        public MoneyShare(string recipient, int percent)
        {
            Recipient = recipient ?? string.Empty;
            Percent = percent;
        }
    }

    public sealed class WillRecord
    {
        public readonly IReadOnlyList<MoneyShare> MoneyShares;
        public readonly int HomeSharePercent;
        public readonly IReadOnlyList<ObjectId> Heirlooms;
        public readonly string Note;
        public readonly string FuneralPreference;
        public readonly IReadOnlyList<HouseholdId> CareHouseholds;

        public WillRecord(
            IReadOnlyList<MoneyShare> moneyShares,
            int homeSharePercent,
            IReadOnlyList<ObjectId> heirlooms,
            string note,
            string funeralPreference,
            IReadOnlyList<HouseholdId> careHouseholds)
        {
            MoneyShares = moneyShares;
            HomeSharePercent = homeSharePercent;
            Heirlooms = heirlooms;
            Note = note ?? string.Empty;
            FuneralPreference = funeralPreference ?? string.Empty;
            CareHouseholds = careHouseholds;
        }

        public int MoneyTotalPercent()
        {
            int total = 0;
            for (int i = 0; i < MoneyShares.Count; i++)
            {
                total += MoneyShares[i].Percent;
            }
            return total;
        }
    }

    public readonly struct EstateLedgerEntry
    {
        public readonly EventId DeathEvent;
        public readonly string AssetRef;
        public readonly string Recipient;
        public readonly bool Settled;

        public EstateLedgerEntry(EventId deathEvent, string assetRef, string recipient, bool settled)
        {
            DeathEvent = deathEvent;
            AssetRef = assetRef ?? string.Empty;
            Recipient = recipient ?? string.Empty;
            Settled = settled;
        }
    }

    public readonly struct CarePlacement
    {
        public readonly HouseholdId? Household;
        public readonly int FacilityPlace;

        public CarePlacement(HouseholdId? household, int facilityPlace)
        {
            Household = household;
            FacilityPlace = facilityPlace;
        }
    }

    public readonly struct CareAssignment
    {
        public readonly ResidentId Dependent;
        public readonly ResidentId Guardian;
        public readonly CarePlacement Placement;
        public readonly EventId TransferEvent;

        public CareAssignment(ResidentId dependent, ResidentId guardian, CarePlacement placement, EventId transferEvent)
        {
            Dependent = dependent;
            Guardian = guardian;
            Placement = placement;
            TransferEvent = transferEvent;
        }
    }
}
