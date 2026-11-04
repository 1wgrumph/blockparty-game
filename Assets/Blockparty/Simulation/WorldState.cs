// STATE@1: seeded resident/world records and dated event ledgers (SIM-CORE state).
// Consumes IFC-LOCK@1 boundary types. Plain C# 9, no UnityEngine dependency.
// Sole-writer rule (BP-SYS-02): every mutation runs through a WorldState method
// that takes a StateWriter and appends to the write audit; all reads expose
// snapshots or read-only views, so presentation/resolver paths cannot mutate.
// Fast Enter Play Mode: no static mutable state (only const/readonly data),
// explicit numeric conversions, no AppDomain/Assembly.Location/BinaryFormatter,
// and string hashing is never persisted (ids are 64-bit integers).
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // BP-SIM-01 12-group resident record plus BP-SEC-07 identity rules.
    public enum LifeStage
    {
        Newborn = 0,
        Infant = 1,
        Toddler = 2,
        Child = 3,
        Teen = 4,
        YoungAdult = 5,
        Adult = 6,
        Elder = 7,
    }

    // Schema order is normative for need tie-breaks (SDoc section 5).
    public enum Need
    {
        Hunger = 0,
        Energy = 1,
        Bladder = 2,
        Hygiene = 3,
        Social = 4,
        Fun = 5,
        Comfort = 6,
        Health = 7,
    }

    public enum TraitAxis
    {
        Sociable = 0,
        Ambitious = 1,
        Active = 2,
        Creative = 3,
        Neat = 4,
        Playful = 5,
    }

    public enum FeelingKind
    {
        Grateful = 0,
        Hurt = 1,
        Angry = 2,
        Affectionate = 3,
        Embarrassed = 4,
        Proud = 5,
    }

    // Allowed sole writers of authoritative state (BP-SYS-02). Any other
    // value is rejected at the API boundary and audited as a failure.
    public enum StateWriter
    {
        SimCore = 0,
        CommandArbitrator = 1,
        ActivityExecutor = 2,
    }

    public static class StateSchema
    {
        public const int NeedCount = 8;
        public const int TraitCount = 6;
        public const int SnapshotVersion = 1;
        public const float NeedMin = 0.0f;
        public const float NeedMax = 100.0f;
        public const int TraitMin = -50;
        public const int TraitMax = 50;
        public const float BondMin = -100.0f;
        public const float BondMax = 100.0f;
        public const int FeelingIntensityMin = 1;
        public const int FeelingIntensityMax = 3;
        public const int SkillLevelMax = 10;
        public const int CareerLevelMax = 5;
        public const float NeedDefault = 80.0f;
    }

    // Self-contained deterministic RNG: System.Random differs between runtimes,
    // so seeded worlds carry this explicit generator state in the save payload.
    public struct WorldRng
    {
        private ulong state;

        public WorldRng(long seed)
        {
            state = ((ulong)seed * 6364136223846793005ul) + 1442695040888963407ul;
            if (state == 0ul)
            {
                state = 0x9E3779B97F4A7C15ul;
            }
        }

        private WorldRng(ulong state, bool _)
        {
            this.state = state;
        }

        public ulong State
        {
            get { return state; }
        }

        public static WorldRng FromState(ulong state)
        {
            return new WorldRng(state == 0ul ? 0x9E3779B97F4A7C15ul : state, true);
        }

        public ulong Next()
        {
            ulong x = state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            state = x;
            return x * 2685821657736338717ul;
        }

        public double NextDouble()
        {
            return ((double)(Next() >> 11)) * (1.0 / 9007199254740992.0);
        }

        public int NextInt(int lowInclusive, int highExclusive)
        {
            if (highExclusive <= lowInclusive)
            {
                return lowInclusive;
            }
            long span = (long)highExclusive - (long)lowInclusive;
            return lowInclusive + ((int)((long)(Next() % (ulong)span)));
        }
    }

    public sealed class SkillEntry
    {
        public string SkillId { get; private set; }
        public int Level { get; private set; }
        public int Points { get; private set; }

        public SkillEntry(string skillId, int level, int points)
        {
            SkillId = skillId ?? string.Empty;
            Level = level;
            Points = points;
        }

        internal void SetLevel(int level) { Level = level; }
        internal void SetPoints(int points) { Points = points; }
        public SkillEntry Clone() { return new SkillEntry(SkillId, Level, Points); }
    }

    public sealed class PairBond
    {
        public ResidentId Other { get; private set; }
        public float Friendship { get; private set; }
        public float Romance { get; private set; }

        public PairBond(ResidentId other, float friendship, float romance)
        {
            Other = other;
            Friendship = friendship;
            Romance = romance;
        }

        internal void Set(float friendship, float romance)
        {
            Friendship = friendship;
            Romance = romance;
        }

        public PairBond Clone() { return new PairBond(Other, Friendship, Romance); }
    }

    public sealed class FamilyLink
    {
        public ResidentId Other { get; private set; }
        public string Kind { get; private set; }

        public FamilyLink(ResidentId other, string kind)
        {
            Other = other;
            Kind = kind ?? string.Empty;
        }

        public FamilyLink Clone() { return new FamilyLink(Other, Kind); }
    }

    public sealed class Feeling
    {
        public FeelingKind Kind { get; private set; }
        public EventId SourceEvent { get; private set; }
        public ResidentId Other { get; private set; }
        public int Intensity { get; private set; }
        public long ExpiryGameSeconds { get; private set; }

        public Feeling(FeelingKind kind, EventId sourceEvent, ResidentId other, int intensity, long expiryGameSeconds)
        {
            Kind = kind;
            SourceEvent = sourceEvent;
            Other = other;
            Intensity = intensity;
            ExpiryGameSeconds = expiryGameSeconds;
        }

        public Feeling Clone() { return new Feeling(Kind, SourceEvent, Other, Intensity, ExpiryGameSeconds); }
    }

    public sealed class MemoryEntry
    {
        public EventId Event { get; private set; }
        public string Kind { get; private set; }
        public long GameSeconds { get; private set; }
        public HomeId Place { get; private set; }
        public IReadOnlyList<ResidentId> Participants { get; private set; }
        public string OutcomeCode { get; private set; }
        public string LearnedBy { get; private set; }

        public MemoryEntry(EventId @event, string kind, long gameSeconds, HomeId place,
            IReadOnlyList<ResidentId> participants, string outcomeCode, string learnedBy)
        {
            Event = @event;
            Kind = kind ?? string.Empty;
            GameSeconds = gameSeconds;
            Place = place;
            Participants = participants ?? new List<ResidentId>().AsReadOnly();
            OutcomeCode = outcomeCode ?? string.Empty;
            LearnedBy = learnedBy ?? string.Empty;
        }
    }

    // One persistent resident record: the 12 BP-SIM-01 field groups plus the
    // freshness counters the command path needs (sequence, decision-slot and
    // control generations). Public surface is read-only; WorldState mutates.
    public sealed class ResidentRecord
    {
        private readonly float[] needs = new float[StateSchema.NeedCount];
        private readonly int[] traits = new int[StateSchema.TraitCount];
        private readonly List<SkillEntry> skills = new List<SkillEntry>();
        private readonly List<PairBond> bonds = new List<PairBond>();
        private readonly List<FamilyLink> family = new List<FamilyLink>();
        private readonly List<Feeling> feelings = new List<Feeling>();
        private readonly List<MemoryEntry> memories = new List<MemoryEntry>();
        private readonly List<string> completedCourses = new List<string>();

        public ResidentId Id { get; private set; }
        public string DisplayName { get; private set; }
        public LifeStage Stage { get; private set; }
        public float StageProgressDays { get; private set; }
        public double ElderRemainderDays { get; private set; }
        public bool IsFrail { get; private set; }
        internal double AgingProgressDays { get; private set; }
        internal bool HasAgingProgress { get; private set; }
        public HouseholdId Household { get; private set; }
        public HomeId Home { get; private set; }
        public long Money { get; private set; }
        public string JobId { get; private set; }
        public int CareerLevel { get; private set; }
        public int CareerPoints { get; private set; }
        public int EducationPoints { get; private set; }
        public bool IsAlive { get; private set; }
        public bool CareRequired { get; private set; }
        public uint ResidentSequence { get; private set; }
        public uint DecisionSlotGeneration { get; private set; }
        public uint ControlGeneration { get; private set; }
        public float StarvationHours { get; private set; }
        public bool Ill { get; private set; }
        public bool Fatigued { get; private set; }
        public bool Injured { get; private set; }
        public int LastChanceDrawDay { get; private set; }

        public ResidentRecord(ResidentId id, string displayName, LifeStage stage, HouseholdId household, HomeId home)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
            Stage = stage;
            Household = household;
            Home = home;
            JobId = string.Empty;
            IsAlive = true;
            LastChanceDrawDay = -1;
            for (int i = 0; i < needs.Length; i++)
            {
                needs[i] = StateSchema.NeedDefault;
            }
        }

        public float GetNeed(Need need) { return needs[(int)need]; }
        public int GetTrait(TraitAxis axis) { return traits[(int)axis]; }
        public IReadOnlyList<SkillEntry> Skills { get { return skills.AsReadOnly(); } }
        public IReadOnlyList<PairBond> Bonds { get { return bonds.AsReadOnly(); } }
        public IReadOnlyList<FamilyLink> Family { get { return family.AsReadOnly(); } }
        public IReadOnlyList<Feeling> Feelings { get { return feelings.AsReadOnly(); } }
        public IReadOnlyList<MemoryEntry> Memories { get { return memories.AsReadOnly(); } }
        public IReadOnlyList<string> CompletedCourses { get { return completedCourses.AsReadOnly(); } }

        internal void SetNeed(int index, float value) { needs[index] = value; }
        internal void SetTrait(int index, int value) { traits[index] = value; }
        internal void SetDisplayName(string value) { DisplayName = value ?? string.Empty; }
        internal void SetStage(LifeStage stage, float progressDays) { Stage = stage; StageProgressDays = progressDays; }
        internal void SetAging(double progress, double remainder, bool frail)
        {
            AgingProgressDays = progress; ElderRemainderDays = remainder; IsFrail = frail; HasAgingProgress = true;
        }
        internal void SetHousehold(HouseholdId household, HomeId home) { Household = household; Home = home; }
        internal void SetMoney(long value) { Money = value; }
        internal void SetCareer(string jobId, int level, int points) { JobId = jobId ?? string.Empty; CareerLevel = level; CareerPoints = points; }
        internal void SetEducation(int points) { EducationPoints = points; }
        internal void AddCompletedCourse(string course) { completedCourses.Add(course ?? string.Empty); }
        internal void SetAlive(bool alive) { IsAlive = alive; }
        internal void SetCareRequired(bool value) { CareRequired = value; }
        internal void SetResidentSequence(uint value) { ResidentSequence = value; }
        internal void SetDecisionSlotGeneration(uint value) { DecisionSlotGeneration = value; }
        internal void SetControlGeneration(uint value) { ControlGeneration = value; }
        internal void SetHealthFlags(float starvationHours, bool ill, bool fatigued, bool injured, int drawDay)
        {
            StarvationHours = starvationHours;
            Ill = ill;
            Fatigued = fatigued;
            Injured = injured;
            LastChanceDrawDay = drawDay;
        }

        internal List<SkillEntry> SkillList { get { return skills; } }
        internal List<PairBond> BondList { get { return bonds; } }
        internal List<FamilyLink> FamilyList { get { return family; } }
        internal List<Feeling> FeelingList { get { return feelings; } }
        internal List<MemoryEntry> MemoryList { get { return memories; } }
    }

    public sealed class HouseholdRecord
    {
        public HouseholdId Id { get; private set; }
        public string Name { get; private set; }
        public HomeId Home { get; private set; }

        public HouseholdRecord(HouseholdId id, string name, HomeId home)
        {
            Id = id;
            Name = name ?? string.Empty;
            Home = home;
        }

        internal void Rename(string name) { Name = name ?? string.Empty; }
        internal void Move(HomeId home) { Home = home; }
    }

    public sealed class HomeRecord
    {
        public HomeId Id { get; private set; }
        public int BedCount { get; private set; }

        public HomeRecord(HomeId id, int bedCount)
        {
            Id = id;
            BedCount = bedCount;
        }
    }

    // Durable dated world event: append-only, never evicted (BP-SEC-07).
    public sealed class WorldEvent
    {
        public EventId Event { get; private set; }
        public long GameSeconds { get; private set; }
        public string Kind { get; private set; }
        public ResidentId? Actor { get; private set; }
        public ResidentId? Resident { get { return Actor; } }
        public string Detail { get; private set; }

        public WorldEvent(EventId @event, long gameSeconds, string kind, ResidentId? actor, string detail)
        {
            Event = @event;
            GameSeconds = gameSeconds;
            Kind = kind ?? string.Empty;
            Actor = actor;
            Detail = detail ?? string.Empty;
        }
    }

    // Bounded diagnostic decision record: the 8 BP-DATA-02 fields. The ring is
    // separate from durable events/ledgers, which are never evicted for it.
    public sealed class DecisionRecord
    {
        public long Sequence { get; private set; }
        public string Trigger { get; private set; }
        public ResidentId Resident { get; private set; }
        public IReadOnlyList<ActivityId> Candidates { get; private set; }
        public ActivityId Chosen { get; private set; }
        public string Source { get; private set; }
        public GatewayRequestId? Request { get; private set; }
        public string ValidationOutcome { get; private set; }
        public string CommittedEffect { get; private set; }

        public DecisionRecord(long sequence, string trigger, ResidentId resident,
            IReadOnlyList<ActivityId> candidates, ActivityId chosen, string source,
            GatewayRequestId? request, string validationOutcome, string committedEffect)
        {
            Sequence = sequence;
            Trigger = trigger ?? string.Empty;
            Resident = resident;
            Candidates = candidates ?? new List<ActivityId>().AsReadOnly();
            Chosen = chosen;
            Source = source ?? string.Empty;
            Request = request;
            ValidationOutcome = validationOutcome ?? string.Empty;
            CommittedEffect = committedEffect ?? string.Empty;
        }
    }

    public sealed class WriteAuditEntry
    {
        public long Sequence { get; private set; }
        public StateWriter Writer { get; private set; }
        public string Detail { get; private set; }

        public WriteAuditEntry(long sequence, StateWriter writer, string detail)
        {
            Sequence = sequence;
            Writer = writer;
            Detail = detail ?? string.Empty;
        }
    }

    // STORM durable state: authored scene classifications, resident shelter
    // assignments, warning/active/cooldown times and the per-event exposure
    // ledger. Times and event ids use 0 for "none"; ids start at 1.
    public sealed class StormObjectRow
    {
        public long Object;
        public bool Outdoor;
        public bool Electric;
        public bool Common;
        public long RoofHome;
        public bool StormDamaged;
    }

    public sealed class StormResidentRow
    {
        public long Resident;
        public bool Outdoors;
        public long Shelter;
        public long RoutedEvent;
    }

    public sealed class StormExposureRow
    {
        public long Event;
        public long Resident;
    }

    public sealed class StormSnapshot
    {
        public long ScheduledStartGameSeconds;
        public long ActiveEvent;
        public long ActiveEndGameSeconds;
        public long CooldownUntilGameSeconds;
        public readonly List<StormObjectRow> Objects = new List<StormObjectRow>();
        public readonly List<StormResidentRow> Residents = new List<StormResidentRow>();
        public readonly List<StormExposureRow> Exposures = new List<StormExposureRow>();

        public bool HasState()
        {
            return ScheduledStartGameSeconds != 0L || ActiveEvent != 0L
                || ActiveEndGameSeconds != 0L || CooldownUntilGameSeconds != 0L
                || Objects.Count != 0 || Residents.Count != 0 || Exposures.Count != 0;
        }

        public StormSnapshot Clone()
        {
            StormSnapshot copy = new StormSnapshot();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(StormSnapshot source)
        {
            StormSnapshot from = source ?? new StormSnapshot();
            ScheduledStartGameSeconds = from.ScheduledStartGameSeconds;
            ActiveEvent = from.ActiveEvent;
            ActiveEndGameSeconds = from.ActiveEndGameSeconds;
            CooldownUntilGameSeconds = from.CooldownUntilGameSeconds;
            Objects.Clear();
            for (int i = 0; i < from.Objects.Count; i++)
            {
                StormObjectRow row = new StormObjectRow();
                row.Object = from.Objects[i].Object;
                row.Outdoor = from.Objects[i].Outdoor;
                row.Electric = from.Objects[i].Electric;
                row.Common = from.Objects[i].Common;
                row.RoofHome = from.Objects[i].RoofHome;
                row.StormDamaged = from.Objects[i].StormDamaged;
                Objects.Add(row);
            }
            Residents.Clear();
            for (int i = 0; i < from.Residents.Count; i++)
            {
                StormResidentRow row = new StormResidentRow();
                row.Resident = from.Residents[i].Resident;
                row.Outdoors = from.Residents[i].Outdoors;
                row.Shelter = from.Residents[i].Shelter;
                row.RoutedEvent = from.Residents[i].RoutedEvent;
                Residents.Add(row);
            }
            Exposures.Clear();
            for (int i = 0; i < from.Exposures.Count; i++)
            {
                StormExposureRow row = new StormExposureRow();
                row.Event = from.Exposures[i].Event;
                row.Resident = from.Exposures[i].Resident;
                Exposures.Add(row);
            }
        }
    }

    // Deep-copy snapshot DTO for save/load (CONTRACT-BP-09-SAVE shape). The
    // diagnostic decision ring and write audit are not persisted; the decision
    // sequence counter is, so record ids stay monotonic across a load.
    public sealed class WorldSnapshot
    {
        public int SchemaVersion;
        public long WorldId;
        public uint Epoch;
        public long Seed;
        public ulong RngState;
        public long GameSeconds;
        public AgingPreset Aging;
        public long TownFund;
        public ulong RelationshipRngState;
        public long NextRelationshipDraw = 1L;
        public Dictionary<ResidentId, long> TierIdleSince = new Dictionary<ResidentId, long>();
        public long NextDecisionSequence;
        public long NextResidentId;
        public long NextHouseholdId;
        public long NextHomeId;
        public long NextObjectId;
        public long NextActionId;
        public long NextEventId;
        public long NextCommandId;
        public long NextGatewayId;
        public List<ResidentRecord> Residents = new List<ResidentRecord>();
        public List<HouseholdRecord> Households = new List<HouseholdRecord>();
        public List<HomeRecord> Homes = new List<HomeRecord>();
        public List<WorldEvent> Events = new List<WorldEvent>();
        public List<EffectLedgerEntry> EffectLedger = new List<EffectLedgerEntry>();
        public List<EstateLedgerEntry> EstateLedger = new List<EstateLedgerEntry>();
        public List<CareAssignment> CareAssignments = new List<CareAssignment>();
        public StormSnapshot Storm = new StormSnapshot();
    }

    // Authoritative world state. Sole writer of resident/world/relationship,
    // economy and progression records; every mutation is audited by writer.
    public sealed class WorldState : IResidentDirectory
    {
        public static readonly string[] DefaultNames = new string[]
        {
            "Ash", "Blair", "Cam", "Devon", "Ellis", "Finley", "Gray",
            "Harper", "Indigo", "Jules", "Kai", "Lark", "Marlowe", "Noor",
            "Onyx", "Parker", "Quinn", "Rowan", "Sage", "Tatum",
            "Uma", "Vesper", "Wren", "Yarrow",
        };

        private readonly Dictionary<ResidentId, ResidentRecord> residents = new Dictionary<ResidentId, ResidentRecord>();
        private readonly Dictionary<HouseholdId, HouseholdRecord> households = new Dictionary<HouseholdId, HouseholdRecord>();
        private readonly Dictionary<HomeId, HomeRecord> homes = new Dictionary<HomeId, HomeRecord>();
        private readonly List<WorldEvent> events = new List<WorldEvent>();
        private readonly List<EffectLedgerEntry> effectLedger = new List<EffectLedgerEntry>();
        private readonly List<EstateLedgerEntry> estateLedger = new List<EstateLedgerEntry>();
        private readonly List<CareAssignment> careAssignments = new List<CareAssignment>();
        private readonly StormSnapshot storm = new StormSnapshot();
        private readonly Queue<DecisionRecord> decisionLog = new Queue<DecisionRecord>();
        private readonly List<WriteAuditEntry> writeAudit = new List<WriteAuditEntry>();

        private WorldRng rng;
        private WorldRng relationshipRng;
        private long nextRelationshipDraw = 1L;
        internal readonly Dictionary<ResidentId, long> TierIdleSince = new Dictionary<ResidentId, long>();
        internal double NextRelationshipRandom(StateWriter writer)
        {
            CheckWriter(writer, "RelationshipRandom");
            return relationshipRng.NextDouble();
        }
        internal long NextRelationshipSequence(StateWriter writer)
        {
            CheckWriter(writer, "RelationshipSequence");
            return nextRelationshipDraw++;
        }
        private long nextAuditSequence = 1;
        private long nextDecisionSequence = 1;
        private bool decisionLogTruncated;
        private long decisionLogDropped;

        public long WorldId { get; private set; }
        public WorldEpoch Epoch { get; private set; }
        public long Seed { get; private set; }
        public long GameSeconds { get; private set; }
        public AgingPreset Aging { get; private set; }
        public long TownFund { get; private set; }
        public long NextResidentId { get; private set; }
        public long NextHouseholdId { get; private set; }
        public long NextHomeId { get; private set; }
        public long NextObjectId { get; private set; }
        public long NextActionId { get; private set; }
        public long NextEventId { get; private set; }
        public long NextCommandId { get; private set; }
        public long NextGatewayId { get; private set; }

        public WorldState(long worldId, long seed)
        {
            WorldId = worldId;
            Seed = seed;
            rng = new WorldRng(seed);
            relationshipRng = new WorldRng(seed);
            Epoch = (WorldEpoch)0u;
            Aging = AgingPreset.Moderate;
            NextResidentId = 1;
            NextHouseholdId = 1;
            NextHomeId = 1;
            NextObjectId = 1;
            NextActionId = 1;
            NextEventId = 1;
            NextCommandId = 1;
            NextGatewayId = 1;
        }

        public ulong RngState
        {
            get { return rng.State; }
        }

        public int ResidentCount
        {
            get { return residents.Count; }
        }

        public int EventCount
        {
            get { return events.Count; }
        }

        public int DecisionLogCount
        {
            get { return decisionLog.Count; }
        }

        public bool DecisionLogTruncated
        {
            get { return decisionLogTruncated; }
        }

        public long DecisionLogDropped
        {
            get { return decisionLogDropped; }
        }

        public IReadOnlyList<WorldEvent> Events
        {
            get { return events.AsReadOnly(); }
        }

        public IReadOnlyList<EffectLedgerEntry> EffectLedger
        {
            get { return effectLedger.AsReadOnly(); }
        }

        public IReadOnlyList<EstateLedgerEntry> EstateLedger
        {
            get { return estateLedger.AsReadOnly(); }
        }

        public IReadOnlyList<CareAssignment> CareAssignments
        {
            get { return careAssignments.AsReadOnly(); }
        }

        public StormSnapshot Storm
        {
            get { return storm; }
        }

        public IReadOnlyList<WriteAuditEntry> WriteAudit
        {
            get { return writeAudit.AsReadOnly(); }
        }

        public IReadOnlyList<DecisionRecord> DecisionLogSnapshot()
        {
            return new List<DecisionRecord>(decisionLog).AsReadOnly();
        }

        public ResidentRecord[] ResidentsSnapshot()
        {
            ResidentRecord[] copy = new ResidentRecord[residents.Count];
            residents.Values.CopyTo(copy, 0);
            return copy;
        }

        public HouseholdRecord[] HouseholdsSnapshot()
        {
            HouseholdRecord[] copy = new HouseholdRecord[households.Count];
            households.Values.CopyTo(copy, 0);
            return copy;
        }

        public bool HasHousehold(HouseholdId id)
        {
            return households.ContainsKey(id);
        }

        public bool TryGetHousehold(HouseholdId id, out HouseholdRecord record)
        {
            return households.TryGetValue(id, out record);
        }

        public bool TryGetHome(HomeId id, out HomeRecord record)
        {
            return homes.TryGetValue(id, out record);
        }

        public bool TryGetResident(ResidentId id, out ResidentRecord record)
        {
            return residents.TryGetValue(id, out record);
        }

        // CONTRACT-BP-08-RESOLVE: read-only query role. Builds fresh snapshots,
        // so searching, following and map reads mutate nothing.
        public IReadOnlyList<ResidentSummary> SearchByName(string name)
        {
            List<ResidentSummary> matches = new List<ResidentSummary>();
            string want = name ?? string.Empty;
            foreach (ResidentRecord record in residents.Values)
            {
                if (string.Equals(record.DisplayName, want, StringComparison.Ordinal))
                {
                    matches.Add(new ResidentSummary(record.Id, record.DisplayName));
                }
            }
            return matches.AsReadOnly();
        }

        bool IResidentDirectory.TryGet(ResidentId id, out ResidentSummary summary)
        {
            ResidentRecord record;
            if (residents.TryGetValue(id, out record))
            {
                summary = new ResidentSummary(record.Id, record.DisplayName);
                return true;
            }
            summary = default(ResidentSummary);
            return false;
        }

        // Deterministic seeded world: same seed builds identical records, so
        // paired runs and personality/history comparisons share a baseline.
        public static WorldState CreateSeeded(long worldId, long seed, int residentCount, string[] names)
        {
            if (residentCount < 0)
            {
                throw new ArgumentOutOfRangeException("residentCount");
            }
            WorldState world = new WorldState(worldId, seed);
            HomeRecord home = world.AllocateHome(StateWriter.SimCore, 8);
            HouseholdRecord household = world.AllocateHousehold(StateWriter.SimCore, "First Household", home.Id);
            for (int i = 0; i < residentCount; i++)
            {
                string name = names != null && i < names.Length ? names[i] : null;
                ResidentRecord resident = world.AllocateResident(StateWriter.SimCore, name, LifeStage.Adult, household.Id, home.Id);
                for (int t = 0; t < StateSchema.TraitCount; t++)
                {
                    resident.SetTrait(t, world.rng.NextInt(StateSchema.TraitMin, StateSchema.TraitMax + 1));
                }
            }
            return world;
        }

        private void CheckWriter(StateWriter writer, string detail)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
            writeAudit.Add(new WriteAuditEntry(nextAuditSequence, writer, detail));
            nextAuditSequence++;
        }

        public ResidentRecord AllocateResident(StateWriter writer, string name, LifeStage stage, HouseholdId household, HomeId home)
        {
            CheckWriter(writer, "AllocateResident");
            string shown = name;
            if (string.IsNullOrEmpty(shown))
            {
                shown = GeneratedName(null);
            }
            ResidentRecord record = new ResidentRecord((ResidentId)NextResidentId, shown, stage, household, home);
            residents.Add(record.Id, record);
            NextResidentId++;
            return record;
        }

        public HouseholdRecord AllocateHousehold(StateWriter writer, string name, HomeId home)
        {
            CheckWriter(writer, "AllocateHousehold");
            HouseholdRecord record = new HouseholdRecord((HouseholdId)NextHouseholdId, name, home);
            households.Add(record.Id, record);
            NextHouseholdId++;
            return record;
        }

        public HomeRecord AllocateHome(StateWriter writer, int bedCount)
        {
            CheckWriter(writer, "AllocateHome");
            HomeRecord record = new HomeRecord((HomeId)NextHomeId, bedCount);
            homes.Add(record.Id, record);
            NextHomeId++;
            return record;
        }

        public ObjectId AllocateObject(StateWriter writer)
        {
            CheckWriter(writer, "AllocateObject");
            ObjectId id = (ObjectId)NextObjectId;
            NextObjectId++;
            return id;
        }

        public ActionInstanceId AllocateAction(StateWriter writer)
        {
            CheckWriter(writer, "AllocateAction");
            ActionInstanceId id = (ActionInstanceId)NextActionId;
            NextActionId++;
            return id;
        }

        public CommandId AllocateCommand(StateWriter writer)
        {
            CheckWriter(writer, "AllocateCommand");
            CommandId id = (CommandId)NextCommandId;
            NextCommandId++;
            return id;
        }

        public GatewayRequestId AllocateGatewayRequest(StateWriter writer)
        {
            CheckWriter(writer, "AllocateGatewayRequest");
            GatewayRequestId id = (GatewayRequestId)NextGatewayId;
            NextGatewayId++;
            return id;
        }

        public void SetNeed(StateWriter writer, ResidentId id, Need need, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException("Need value must be finite", "value");
            }
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetNeed " + ((long)id).ToString() + " " + ((int)need).ToString());
            record.SetNeed((int)need, Clamp(value, StateSchema.NeedMin, StateSchema.NeedMax));
        }

        public void SetTrait(StateWriter writer, ResidentId id, TraitAxis axis, int value)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetTrait " + ((long)id).ToString() + " " + ((int)axis).ToString());
            record.SetTrait((int)axis, ClampInt(value, StateSchema.TraitMin, StateSchema.TraitMax));
        }

        public void SetMoney(StateWriter writer, ResidentId id, long value)
        {
            if (value < 0L)
            {
                throw new ArgumentOutOfRangeException("value", "Money is integer currency units, never negative");
            }
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetMoney " + ((long)id).ToString());
            record.SetMoney(value);
        }

        public void SetTownFund(StateWriter writer, long value)
        {
            if (value < 0L)
            {
                throw new ArgumentOutOfRangeException("value", "TownFund is never negative");
            }
            CheckWriter(writer, "SetTownFund");
            TownFund = value;
        }

        public void SetGameSeconds(StateWriter writer, long value)
        {
            if (value < GameSeconds)
            {
                throw new ArgumentOutOfRangeException("value", "Shared clock never runs backward");
            }
            CheckWriter(writer, "SetGameSeconds " + value.ToString());
            GameSeconds = value;
        }

        public void SetStage(StateWriter writer, ResidentId id, LifeStage stage, float progressDays)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetStage " + ((long)id).ToString());
            record.SetStage(stage, progressDays);
        }

        public void SetAging(StateWriter writer, ResidentId id, double progress, double remainder, bool frail)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetAging " + ((long)id).ToString());
            record.SetAging(progress, remainder, frail);
        }

        public void RenameResident(StateWriter writer, ResidentId id, string name)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "RenameResident " + ((long)id).ToString());
            string blank = name;
            if (string.IsNullOrEmpty(blank))
            {
                blank = GeneratedName(record);
            }
            record.SetDisplayName(blank);
        }

        // A generated name is never blank and never repeats a living resident's
        // name. The single seeded draw picks the starting default as before;
        // taken defaults are skipped in list order, and once every default is
        // taken a numeric suffix keeps the name unique ("Finley 2").
        private string GeneratedName(ResidentRecord self)
        {
            int start = rng.NextInt(0, DefaultNames.Length);
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResidentRecord other in residents.Values)
            {
                if (other.IsAlive && !ReferenceEquals(other, self))
                {
                    taken.Add(other.DisplayName);
                }
            }
            for (int suffix = 1; ; suffix++)
            {
                for (int k = 0; k < DefaultNames.Length; k++)
                {
                    string candidate = DefaultNames[(start + k) % DefaultNames.Length];
                    if (suffix > 1)
                    {
                        candidate = candidate + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (!taken.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        public void SetHousehold(StateWriter writer, ResidentId id, HouseholdId household, HomeId home)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetHousehold " + ((long)id).ToString());
            record.SetHousehold(household, home);
        }

        // HOUSING: move a household record and every member to a new home.
        public void MoveHousehold(StateWriter writer, HouseholdId household, HomeId home)
        {
            HouseholdRecord record;
            if (!households.TryGetValue(household, out record))
            {
                throw new ArgumentException("Unknown household " + ((long)household).ToString(), "household");
            }
            CheckWriter(writer, "MoveHousehold " + ((long)household).ToString());
            record.Move(home);
            foreach (ResidentRecord resident in residents.Values)
            {
                if (resident.Household == household)
                {
                    resident.SetHousehold(household, home);
                }
            }
        }

        public void SetCareer(StateWriter writer, ResidentId id, string jobId, int level, int points)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetCareer " + ((long)id).ToString());
            record.SetCareer(jobId, ClampInt(level, 0, StateSchema.CareerLevelMax), points < 0 ? 0 : points);
        }

        public void SetEducation(StateWriter writer, ResidentId id, int points)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetEducation " + ((long)id).ToString());
            record.SetEducation(points < 0 ? 0 : points);
        }

        public void AddCompletedCourse(StateWriter writer, ResidentId id, string course)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "AddCompletedCourse " + ((long)id).ToString());
            record.AddCompletedCourse(course);
        }

        public void AddSkillPoints(StateWriter writer, ResidentId id, string skillId, int points)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "AddSkillPoints " + ((long)id).ToString());
            SkillEntry found = null;
            for (int i = 0; i < record.SkillList.Count; i++)
            {
                if (string.Equals(record.SkillList[i].SkillId, skillId ?? string.Empty, StringComparison.Ordinal))
                {
                    found = record.SkillList[i];
                }
            }
            if (found == null)
            {
                found = new SkillEntry(skillId, 0, 0);
                record.SkillList.Add(found);
            }
            int total = found.Points + points;
            int level = found.Level;
            while (level < StateSchema.SkillLevelMax && total >= 100 * (level + 1))
            {
                total -= 100 * (level + 1);
                level++;
            }
            found.SetLevel(level);
            found.SetPoints(total);
        }

        public void SetBond(StateWriter writer, ResidentId id, ResidentId other, float friendship, float romance)
        {
            ResidentRecord record = RequireResident(id);
            RequireResident(other);
            CheckWriter(writer, "SetBond " + ((long)id).ToString());
            PairBond found = null;
            for (int i = 0; i < record.BondList.Count; i++)
            {
                if (record.BondList[i].Other == other)
                {
                    found = record.BondList[i];
                }
            }
            if (found == null)
            {
                found = new PairBond(other, 0.0f, 0.0f);
                record.BondList.Add(found);
            }
            found.Set(Clamp(friendship, StateSchema.BondMin, StateSchema.BondMax),
                Clamp(romance, StateSchema.BondMin, StateSchema.BondMax));
        }

        public void AddFamilyLink(StateWriter writer, ResidentId id, ResidentId other, string kind)
        {
            ResidentRecord record = RequireResident(id);
            RequireResident(other);
            CheckWriter(writer, "AddFamilyLink " + ((long)id).ToString());
            record.FamilyList.Add(new FamilyLink(other, kind));
        }

        public void AddFeeling(StateWriter writer, ResidentId id, FeelingKind kind, EventId sourceEvent,
            ResidentId other, int intensity, long expiryGameSeconds)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "AddFeeling " + ((long)id).ToString());
            record.FeelingList.Add(new Feeling(kind, sourceEvent, other,
                ClampInt(intensity, StateSchema.FeelingIntensityMin, StateSchema.FeelingIntensityMax), expiryGameSeconds));
        }

        public void AddMemory(StateWriter writer, ResidentId id, MemoryEntry memory)
        {
            ResidentRecord record = RequireResident(id);
            if (memory == null)
            {
                throw new ArgumentNullException("memory");
            }
            CheckWriter(writer, "AddMemory " + ((long)id).ToString());
            record.MemoryList.Add(memory);
        }

        public void SetAlive(StateWriter writer, ResidentId id, bool alive)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetAlive " + ((long)id).ToString());
            record.SetAlive(alive);
        }

        // ESTATE care flag: a dependent elder needs continuing care; persisted
        // with the resident so a reload cannot orphan it.
        public void SetCareRequired(StateWriter writer, ResidentId id, bool value)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetCareRequired " + ((long)id).ToString());
            record.SetCareRequired(value);
        }

        // ESTATE vanished-recipient fixture: remove an empty household so a
        // previously valid bequest loses that recipient. Non-empty households
        // are refused without mutation.
        public bool DissolveHousehold(StateWriter writer, HouseholdId household)
        {
            if (!households.ContainsKey(household))
            {
                return false;
            }
            foreach (ResidentRecord resident in residents.Values)
            {
                if (resident.Household == household)
                {
                    return false;
                }
            }
            CheckWriter(writer, "DissolveHousehold " + ((long)household).ToString());
            households.Remove(household);
            return true;
        }

        public void SetHealthFlags(StateWriter writer, ResidentId id, float starvationHours,
            bool ill, bool fatigued, bool injured, int drawDay)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "SetHealthFlags " + ((long)id).ToString());
            record.SetHealthFlags(starvationHours, ill, fatigued, injured, drawDay);
        }

        public uint NextResidentSequence(StateWriter writer, ResidentId id)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "NextResidentSequence " + ((long)id).ToString());
            uint next = record.ResidentSequence + 1u;
            record.SetResidentSequence(next);
            return next;
        }

        public void TouchDecisionSlot(StateWriter writer, ResidentId id)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "TouchDecisionSlot " + ((long)id).ToString());
            record.SetDecisionSlotGeneration(record.DecisionSlotGeneration + 1u);
        }

        public void TouchControlGeneration(StateWriter writer, ResidentId id)
        {
            ResidentRecord record = RequireResident(id);
            CheckWriter(writer, "TouchControlGeneration " + ((long)id).ToString());
            record.SetControlGeneration(record.ControlGeneration + 1u);
        }

        // Dated event ledger: append-only with the current game time.
        public WorldEvent AppendEvent(StateWriter writer, string kind, ResidentId? actor, string detail)
        {
            CheckWriter(writer, "AppendEvent");
            WorldEvent entry = new WorldEvent((EventId)NextEventId, GameSeconds, kind, actor, detail);
            events.Add(entry);
            NextEventId++;
            return entry;
        }

        // Exactly-once effect ledger: a second reserve for one action is refused.
        public bool ReserveEffect(StateWriter writer, ActionInstanceId action, string effectKind, LedgerRecipient recipient)
        {
            CheckWriter(writer, "ReserveEffect " + ((long)action).ToString());
            for (int i = 0; i < effectLedger.Count; i++)
            {
                if (effectLedger[i].Action == action)
                {
                    return false;
                }
            }
            effectLedger.Add(new EffectLedgerEntry(action, effectKind, recipient));
            return true;
        }

        public bool CommitEffect(StateWriter writer, ActionInstanceId action)
        {
            CheckWriter(writer, "CommitEffect " + ((long)action).ToString());
            for (int i = 0; i < effectLedger.Count; i++)
            {
                if (effectLedger[i].Action == action)
                {
                    return effectLedger[i].TryCommit();
                }
            }
            return false;
        }

        // Keyed exactly-once ledger for actions that pay several recipients or
        // pairs under one shared id (SEIT-IF-EXEC-P5, DES-BP-EFFECT-LEDGER).
        // Dedupe is on (action, effect kind, recipient/pair); the single-key
        // methods above keep their one-entry-per-action contract.
        public bool ReserveEffectKeyed(StateWriter writer, ActionInstanceId action,
            string effectKind, LedgerRecipient recipient)
        {
            CheckWriter(writer, "ReserveEffectKeyed " + ((long)action).ToString());
            string kind = effectKind ?? string.Empty;
            for (int i = 0; i < effectLedger.Count; i++)
            {
                if (effectLedger[i].Action == action
                    && string.Equals(effectLedger[i].EffectKind, kind, StringComparison.Ordinal)
                    && effectLedger[i].Recipient == recipient)
                {
                    return false;
                }
            }
            effectLedger.Add(new EffectLedgerEntry(action, kind, recipient));
            return true;
        }

        public bool CommitEffectKeyed(StateWriter writer, ActionInstanceId action,
            string effectKind, LedgerRecipient recipient)
        {
            CheckWriter(writer, "CommitEffectKeyed " + ((long)action).ToString());
            string kind = effectKind ?? string.Empty;
            for (int i = 0; i < effectLedger.Count; i++)
            {
                if (effectLedger[i].Action == action
                    && string.Equals(effectLedger[i].EffectKind, kind, StringComparison.Ordinal)
                    && effectLedger[i].Recipient == recipient)
                {
                    return effectLedger[i].TryCommit();
                }
            }
            return false;
        }

        // Estate settlement is idempotent per death event plus asset/recipient:
        // a reload or replayed settlement applies nothing twice.
        public bool SettleEstate(StateWriter writer, EventId deathEvent, string assetRef, string recipient)
        {
            CheckWriter(writer, "SettleEstate " + ((long)deathEvent).ToString());
            string asset = assetRef ?? string.Empty;
            string to = recipient ?? string.Empty;
            for (int i = 0; i < estateLedger.Count; i++)
            {
                EstateLedgerEntry entry = estateLedger[i];
                if (entry.DeathEvent == deathEvent
                    && string.Equals(entry.AssetRef, asset, StringComparison.Ordinal)
                    && string.Equals(entry.Recipient, to, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            estateLedger.Add(new EstateLedgerEntry(deathEvent, asset, to, true));
            return true;
        }

        // Read-only settlement guard: ESTATE marks every settled death with
        // the "estate"/"settled" row, so a tick can skip without auditing a
        // refused duplicate every tick.
        public bool IsEstateSettled(EventId deathEvent)
        {
            for (int i = 0; i < estateLedger.Count; i++)
            {
                if (estateLedger[i].DeathEvent == deathEvent
                    && string.Equals(estateLedger[i].AssetRef, "estate", StringComparison.Ordinal)
                    && string.Equals(estateLedger[i].Recipient, "settled", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // One care transfer event admits its dependent exactly once.
        public bool AssignCare(StateWriter writer, CareAssignment assignment)
        {
            if (assignment.Placement.Household == null && assignment.Placement.FacilityPlace < 0)
            {
                throw new ArgumentException("Care placement needs a household or facility place", "assignment");
            }
            CheckWriter(writer, "AssignCare " + ((long)assignment.TransferEvent).ToString());
            for (int i = 0; i < careAssignments.Count; i++)
            {
                if (careAssignments[i].TransferEvent == assignment.TransferEvent)
                {
                    return false;
                }
            }
            careAssignments.Add(assignment);
            return true;
        }

        // ESTATE surviving-caregiver branch: no readmission, so the existing
        // dependent row keeps its transfer event and placement and only the
        // guardian changes. Returns false when the dependent has no row.
        public bool ReassignCare(StateWriter writer, ResidentId dependent, CareAssignment assignment)
        {
            if (assignment.Dependent != dependent)
            {
                throw new ArgumentException("Care assignment names another dependent", "assignment");
            }
            if (assignment.Placement.Household == null && assignment.Placement.FacilityPlace < 0)
            {
                throw new ArgumentException("Care placement needs a household or facility place", "assignment");
            }
            int index = -1;
            for (int i = careAssignments.Count - 1; i >= 0; i--)
            {
                if (careAssignments[i].Dependent == dependent)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                return false;
            }
            CheckWriter(writer, "ReassignCare " + ((long)dependent).ToString());
            careAssignments[index] = assignment;
            return true;
        }

        // Bounded diagnostic ring: oldest record evicted first with a visible
        // truncation marker and a monotonic sequence (BP-SEC-07).
        public DecisionRecord AppendDecision(StateWriter writer, string trigger, ResidentId resident,
            IReadOnlyList<ActivityId> candidates, ActivityId chosen, string source,
            GatewayRequestId? request, string validationOutcome, string committedEffect)
        {
            RequireResident(resident);
            CheckWriter(writer, "AppendDecision");
            DecisionRecord record = new DecisionRecord(nextDecisionSequence, trigger, resident,
                candidates, chosen, source, request, validationOutcome, committedEffect);
            nextDecisionSequence++;
            if (decisionLog.Count >= GatewayLimits.DecisionLogMaxRecords)
            {
                decisionLog.Dequeue();
                decisionLogTruncated = true;
                decisionLogDropped++;
            }
            decisionLog.Enqueue(record);
            return record;
        }

        private ResidentRecord RequireResident(ResidentId id)
        {
            ResidentRecord record;
            if (!residents.TryGetValue(id, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)id).ToString(), "id");
            }
            return record;
        }

        private static float Clamp(float value, float low, float high)
        {
            if (value < low)
            {
                return low;
            }
            if (value > high)
            {
                return high;
            }
            return value;
        }

        private static int ClampInt(int value, int low, int high)
        {
            if (value < low)
            {
                return low;
            }
            if (value > high)
            {
                return high;
            }
            return value;
        }

        // Deterministic FNV-1a hash over authoritative state only (diagnostic
        // ring and write audit excluded): the oracle for resolver-read-only,
        // no-mutation and round-trip checks. Never persisted across runs.
        public int ComputeStateHash()
        {
            FnvHash hash = new FnvHash();
            hash.Add(WorldId);
            hash.Add((long)(uint)Epoch);
            hash.Add(Seed);
            hash.Add((long)RngState);
            hash.Add(GameSeconds);
            hash.Add((long)(int)Aging);
            hash.Add(TownFund);
            hash.Add(NextResidentId);
            hash.Add(NextHouseholdId);
            hash.Add(NextHomeId);
            hash.Add(NextObjectId);
            hash.Add(NextActionId);
            hash.Add(NextEventId);
            hash.Add(NextCommandId);
            hash.Add(NextGatewayId);
            List<long> residentIds = new List<long>();
            foreach (ResidentId id in residents.Keys)
            {
                residentIds.Add((long)id);
            }
            residentIds.Sort();
            for (int i = 0; i < residentIds.Count; i++)
            {
                HashResident(hash, residents[(ResidentId)residentIds[i]]);
            }
            List<long> householdIds = new List<long>();
            foreach (HouseholdId id in households.Keys)
            {
                householdIds.Add((long)id);
            }
            householdIds.Sort();
            for (int i = 0; i < householdIds.Count; i++)
            {
                HouseholdRecord household = households[(HouseholdId)householdIds[i]];
                hash.Add((long)household.Id);
                hash.Add(household.Name);
                hash.Add((long)household.Home);
            }
            List<long> homeIds = new List<long>();
            foreach (HomeId id in homes.Keys)
            {
                homeIds.Add((long)id);
            }
            homeIds.Sort();
            for (int i = 0; i < homeIds.Count; i++)
            {
                HomeRecord home = homes[(HomeId)homeIds[i]];
                hash.Add((long)home.Id);
                hash.Add((long)home.BedCount);
            }
            for (int i = 0; i < events.Count; i++)
            {
                hash.Add((long)events[i].Event);
                hash.Add(events[i].GameSeconds);
                hash.Add(events[i].Kind);
                hash.Add(events[i].Actor.HasValue ? (long)events[i].Actor.Value : 0L);
                hash.Add(events[i].Detail);
            }
            for (int i = 0; i < effectLedger.Count; i++)
            {
                hash.Add((long)effectLedger[i].Action);
                hash.Add(effectLedger[i].EffectKind);
                hash.Add((long)effectLedger[i].Recipient.First);
                hash.Add(effectLedger[i].Recipient.Second.HasValue ? (long)effectLedger[i].Recipient.Second.Value : 0L);
                hash.Add((long)(int)effectLedger[i].State);
            }
            for (int i = 0; i < estateLedger.Count; i++)
            {
                hash.Add((long)estateLedger[i].DeathEvent);
                hash.Add(estateLedger[i].AssetRef);
                hash.Add(estateLedger[i].Recipient);
                hash.Add(estateLedger[i].Settled ? 1L : 0L);
            }
            for (int i = 0; i < careAssignments.Count; i++)
            {
                hash.Add((long)careAssignments[i].Dependent);
                hash.Add((long)careAssignments[i].Guardian);
                hash.Add(careAssignments[i].Placement.Household.HasValue ? (long)careAssignments[i].Placement.Household.Value : 0L);
                hash.Add((long)careAssignments[i].Placement.FacilityPlace);
                hash.Add((long)careAssignments[i].TransferEvent);
            }
            return hash.Value;
        }

        private static void HashResident(FnvHash hash, ResidentRecord record)
        {
            hash.Add((long)record.Id);
            hash.Add(record.DisplayName);
            hash.Add((long)(int)record.Stage);
            hash.Add(FloatBits(record.StageProgressDays));
            hash.Add((long)record.Household);
            hash.Add((long)record.Home);
            hash.Add(record.Money);
            hash.Add(record.JobId);
            hash.Add((long)record.CareerLevel);
            hash.Add((long)record.CareerPoints);
            hash.Add((long)record.EducationPoints);
            hash.Add(record.IsAlive ? 1L : 0L);
            hash.Add(record.CareRequired ? 1L : 0L);
            hash.Add((long)record.ResidentSequence);
            hash.Add((long)record.DecisionSlotGeneration);
            hash.Add((long)record.ControlGeneration);
            hash.Add(FloatBits(record.StarvationHours));
            hash.Add(record.Ill ? 1L : 0L);
            hash.Add(record.Fatigued ? 1L : 0L);
            hash.Add(record.Injured ? 1L : 0L);
            hash.Add((long)record.LastChanceDrawDay);
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                hash.Add(FloatBits(record.GetNeed((Need)i)));
            }
            for (int i = 0; i < StateSchema.TraitCount; i++)
            {
                hash.Add((long)record.GetTrait((TraitAxis)i));
            }
            for (int i = 0; i < record.Skills.Count; i++)
            {
                hash.Add(record.Skills[i].SkillId);
                hash.Add((long)record.Skills[i].Level);
                hash.Add((long)record.Skills[i].Points);
            }
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                hash.Add((long)record.Bonds[i].Other);
                hash.Add(FloatBits(record.Bonds[i].Friendship));
                hash.Add(FloatBits(record.Bonds[i].Romance));
            }
            for (int i = 0; i < record.Family.Count; i++)
            {
                hash.Add((long)record.Family[i].Other);
                hash.Add(record.Family[i].Kind);
            }
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                hash.Add((long)(int)record.Feelings[i].Kind);
                hash.Add((long)record.Feelings[i].SourceEvent);
                hash.Add((long)record.Feelings[i].Other);
                hash.Add((long)record.Feelings[i].Intensity);
                hash.Add(record.Feelings[i].ExpiryGameSeconds);
            }
            for (int i = 0; i < record.Memories.Count; i++)
            {
                MemoryEntry memory = record.Memories[i];
                hash.Add((long)memory.Event);
                hash.Add(memory.Kind);
                hash.Add(memory.GameSeconds);
                hash.Add((long)memory.Place);
                for (int p = 0; p < memory.Participants.Count; p++)
                {
                    hash.Add((long)memory.Participants[p]);
                }
                hash.Add(memory.OutcomeCode);
                hash.Add(memory.LearnedBy);
            }
            for (int i = 0; i < record.CompletedCourses.Count; i++)
            {
                hash.Add(record.CompletedCourses[i]);
            }
        }

        private static long FloatBits(float value)
        {
            byte[] bytes = new byte[4];
            FloatBitsInto(value, bytes);
            long bits = (long)bytes[0]
                | (((long)bytes[1]) << 8)
                | (((long)bytes[2]) << 16)
                | (((long)bytes[3]) << 24);
            return bits;
        }

        // BitConverter-free float decomposition: explicit and portable.
        private static void FloatBitsInto(float value, byte[] bytes)
        {
            float remaining = value;
            bool negative = remaining < 0.0f;
            if (negative)
            {
                remaining = -remaining;
            }
            if (remaining == 0.0f)
            {
                bytes[0] = 0;
                bytes[1] = 0;
                bytes[2] = 0;
                bytes[3] = (byte)(negative ? 128 : 0);
                return;
            }
            int exponent = 0;
            while (remaining >= 2.0f)
            {
                remaining /= 2.0f;
                exponent++;
            }
            while (remaining < 1.0f)
            {
                remaining *= 2.0f;
                exponent--;
            }
            long mantissa = (long)((remaining - 1.0f) * 8388608.0f + 0.5f);
            long bits = mantissa | (((long)(exponent + 127)) << 23);
            if (negative)
            {
                bits |= ((long)1) << 31;
            }
            bytes[0] = (byte)(bits & 255L);
            bytes[1] = (byte)((bits >> 8) & 255L);
            bytes[2] = (byte)((bits >> 16) & 255L);
            bytes[3] = (byte)((bits >> 24) & 255L);
        }

        private sealed class FnvHash
        {
            private uint hash = 2166136261u;

            public int Value
            {
                get { return (int)hash; }
            }

            public void Add(long value)
            {
                unchecked
                {
                    ulong bits = (ulong)value;
                    for (int i = 0; i < 8; i++)
                    {
                        hash ^= (uint)(bits & 255ul);
                        hash *= 16777619u;
                        bits >>= 8;
                    }
                }
            }

            public void Add(string value)
            {
                unchecked
                {
                    string text = value ?? string.Empty;
                    Add((long)text.Length);
                    for (int i = 0; i < text.Length; i++)
                    {
                        hash ^= (uint)text[i];
                        hash *= 16777619u;
                    }
                }
            }
        }

        public WorldSnapshot ExportSnapshot()
        {
            WorldSnapshot snap = new WorldSnapshot();
            snap.SchemaVersion = StateSchema.SnapshotVersion;
            snap.WorldId = WorldId;
            snap.Epoch = (uint)Epoch;
            snap.Seed = Seed;
            snap.RngState = rng.State;
            snap.RelationshipRngState = relationshipRng.State;
            snap.NextRelationshipDraw = nextRelationshipDraw;
            foreach (KeyValuePair<ResidentId, long> idle in TierIdleSince) snap.TierIdleSince.Add(idle.Key, idle.Value);
            snap.GameSeconds = GameSeconds;
            snap.Aging = Aging;
            snap.TownFund = TownFund;
            snap.NextDecisionSequence = nextDecisionSequence;
            snap.NextResidentId = NextResidentId;
            snap.NextHouseholdId = NextHouseholdId;
            snap.NextHomeId = NextHomeId;
            snap.NextObjectId = NextObjectId;
            snap.NextActionId = NextActionId;
            snap.NextEventId = NextEventId;
            snap.NextCommandId = NextCommandId;
            snap.NextGatewayId = NextGatewayId;
            foreach (ResidentRecord record in residents.Values)
            {
                snap.Residents.Add(CloneResident(record));
            }
            foreach (HouseholdRecord household in households.Values)
            {
                snap.Households.Add(new HouseholdRecord(household.Id, household.Name, household.Home));
            }
            foreach (HomeRecord home in homes.Values)
            {
                snap.Homes.Add(new HomeRecord(home.Id, home.BedCount));
            }
            for (int i = 0; i < events.Count; i++)
            {
                snap.Events.Add(new WorldEvent(events[i].Event, events[i].GameSeconds, events[i].Kind, events[i].Actor, events[i].Detail));
            }
            for (int i = 0; i < effectLedger.Count; i++)
            {
                EffectLedgerEntry entry = new EffectLedgerEntry(effectLedger[i].Action, effectLedger[i].EffectKind, effectLedger[i].Recipient);
                if (effectLedger[i].State == LedgerState.Committed)
                {
                    entry.TryCommit();
                }
                snap.EffectLedger.Add(entry);
            }
            for (int i = 0; i < estateLedger.Count; i++)
            {
                snap.EstateLedger.Add(estateLedger[i]);
            }
            for (int i = 0; i < careAssignments.Count; i++)
            {
                snap.CareAssignments.Add(careAssignments[i]);
            }
            snap.Storm = storm.Clone();
            return snap;
        }

        private static ResidentRecord CloneResident(ResidentRecord record)
        {
            ResidentRecord copy = new ResidentRecord(record.Id, record.DisplayName, record.Stage, record.Household, record.Home);
            copy.SetMoney(record.Money);
            copy.SetCareer(record.JobId, record.CareerLevel, record.CareerPoints);
            copy.SetEducation(record.EducationPoints);
            copy.SetAlive(record.IsAlive);
            copy.SetCareRequired(record.CareRequired);
            copy.SetResidentSequence(record.ResidentSequence);
            copy.SetDecisionSlotGeneration(record.DecisionSlotGeneration);
            copy.SetControlGeneration(record.ControlGeneration);
            copy.SetStage(record.Stage, record.StageProgressDays);
            if (record.HasAgingProgress) copy.SetAging(record.AgingProgressDays, record.ElderRemainderDays, record.IsFrail);
            copy.SetHealthFlags(record.StarvationHours, record.Ill, record.Fatigued, record.Injured, record.LastChanceDrawDay);
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                copy.SetNeed(i, record.GetNeed((Need)i));
            }
            for (int i = 0; i < StateSchema.TraitCount; i++)
            {
                copy.SetTrait(i, record.GetTrait((TraitAxis)i));
            }
            for (int i = 0; i < record.Skills.Count; i++)
            {
                copy.SkillList.Add(record.Skills[i].Clone());
            }
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                copy.BondList.Add(record.Bonds[i].Clone());
            }
            for (int i = 0; i < record.Family.Count; i++)
            {
                copy.FamilyList.Add(record.Family[i].Clone());
            }
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                copy.FeelingList.Add(record.Feelings[i].Clone());
            }
            for (int i = 0; i < record.Memories.Count; i++)
            {
                MemoryEntry memory = record.Memories[i];
                List<ResidentId> participants = new List<ResidentId>();
                for (int p = 0; p < memory.Participants.Count; p++)
                {
                    participants.Add(memory.Participants[p]);
                }
                copy.MemoryList.Add(new MemoryEntry(memory.Event, memory.Kind, memory.GameSeconds,
                    memory.Place, participants.AsReadOnly(), memory.OutcomeCode, memory.LearnedBy));
            }
            for (int i = 0; i < record.CompletedCourses.Count; i++)
            {
                copy.AddCompletedCourse(record.CompletedCourses[i]);
            }
            return copy;
        }

        // Load validates into a fresh world before replacing: unknown schema,
        // duplicate identities or a high-water mark below a live id are refused
        // by name. Epoch increments; numeric ids are never reused.
        public static WorldState LoadFromSnapshot(WorldSnapshot snap)
        {
            if (snap == null)
            {
                throw new ArgumentNullException("snap");
            }
            if (snap.SchemaVersion != StateSchema.SnapshotVersion)
            {
                throw new InvalidOperationException("Unsupported snapshot schema v" + snap.SchemaVersion.ToString());
            }
            WorldState world = new WorldState(snap.WorldId, snap.Seed);
            world.rng = WorldRng.FromState(snap.RngState);
            if (snap.RelationshipRngState != 0UL) world.relationshipRng = WorldRng.FromState(snap.RelationshipRngState);
            world.nextRelationshipDraw = snap.NextRelationshipDraw;
            foreach (KeyValuePair<ResidentId, long> idle in snap.TierIdleSince) world.TierIdleSince.Add(idle.Key, idle.Value);
            world.Epoch = (WorldEpoch)(snap.Epoch + 1u);
            world.GameSeconds = snap.GameSeconds;
            world.Aging = snap.Aging;
            world.TownFund = snap.TownFund;
            world.nextDecisionSequence = snap.NextDecisionSequence;
            world.NextResidentId = snap.NextResidentId;
            world.NextHouseholdId = snap.NextHouseholdId;
            world.NextHomeId = snap.NextHomeId;
            world.NextObjectId = snap.NextObjectId;
            world.NextActionId = snap.NextActionId;
            world.NextEventId = snap.NextEventId;
            world.NextCommandId = snap.NextCommandId;
            world.NextGatewayId = snap.NextGatewayId;
            for (int i = 0; i < snap.Homes.Count; i++)
            {
                if (world.homes.ContainsKey(snap.Homes[i].Id))
                {
                    throw new InvalidOperationException("Duplicate HomeId " + ((long)snap.Homes[i].Id).ToString());
                }
                world.homes.Add(snap.Homes[i].Id, new HomeRecord(snap.Homes[i].Id, snap.Homes[i].BedCount));
            }
            for (int i = 0; i < snap.Households.Count; i++)
            {
                if (world.households.ContainsKey(snap.Households[i].Id))
                {
                    throw new InvalidOperationException("Duplicate HouseholdId " + ((long)snap.Households[i].Id).ToString());
                }
                world.households.Add(snap.Households[i].Id,
                    new HouseholdRecord(snap.Households[i].Id, snap.Households[i].Name, snap.Households[i].Home));
            }
            for (int i = 0; i < snap.Residents.Count; i++)
            {
                if (world.residents.ContainsKey(snap.Residents[i].Id))
                {
                    throw new InvalidOperationException("Duplicate ResidentId " + ((long)snap.Residents[i].Id).ToString());
                }
                world.residents.Add(snap.Residents[i].Id, CloneResident(snap.Residents[i]));
            }
            for (int i = 0; i < snap.Events.Count; i++)
            {
                world.events.Add(new WorldEvent(snap.Events[i].Event, snap.Events[i].GameSeconds,
                    snap.Events[i].Kind, snap.Events[i].Actor, snap.Events[i].Detail));
            }
            for (int i = 0; i < snap.EffectLedger.Count; i++)
            {
                EffectLedgerEntry entry = new EffectLedgerEntry(snap.EffectLedger[i].Action,
                    snap.EffectLedger[i].EffectKind, snap.EffectLedger[i].Recipient);
                if (snap.EffectLedger[i].State == LedgerState.Committed)
                {
                    entry.TryCommit();
                }
                world.effectLedger.Add(entry);
            }
            for (int i = 0; i < snap.EstateLedger.Count; i++)
            {
                world.estateLedger.Add(snap.EstateLedger[i]);
            }
            for (int i = 0; i < snap.CareAssignments.Count; i++)
            {
                world.careAssignments.Add(snap.CareAssignments[i]);
            }
            world.storm.CopyFrom(snap.Storm);
            world.CheckStormState();
            world.CheckHighWaterMarks();
            return world;
        }

        private void CheckStormState()
        {
            if (storm.ScheduledStartGameSeconds < 0L || storm.ActiveEndGameSeconds < 0L
                || storm.CooldownUntilGameSeconds < 0L)
            {
                throw new InvalidOperationException("STORM times must be non-negative");
            }
            if (storm.ScheduledStartGameSeconds != 0L && storm.ActiveEvent != 0L)
            {
                throw new InvalidOperationException("STORM cannot be scheduled and active at once");
            }
            if (storm.ActiveEvent != 0L && storm.ActiveEndGameSeconds <= 0L)
            {
                throw new InvalidOperationException("Active STORM has no end time");
            }
            if (storm.ActiveEvent != 0L && !HasEvent(storm.ActiveEvent))
            {
                throw new InvalidOperationException("Active STORM names an unknown event");
            }
            HashSet<long> objectIds = new HashSet<long>();
            for (int i = 0; i < storm.Objects.Count; i++)
            {
                StormObjectRow row = storm.Objects[i];
                if (row.Object <= 0L || row.Object >= NextObjectId)
                {
                    throw new InvalidOperationException("STORM object is outside the object high-water mark");
                }
                if (!objectIds.Add(row.Object))
                {
                    throw new InvalidOperationException("Duplicate STORM object " + row.Object.ToString());
                }
                if (row.RoofHome < 0L || (row.RoofHome != 0L && !homes.ContainsKey((HomeId)row.RoofHome)))
                {
                    throw new InvalidOperationException("STORM roof names an unknown home");
                }
            }
            HashSet<long> residentIds = new HashSet<long>();
            for (int i = 0; i < storm.Residents.Count; i++)
            {
                StormResidentRow row = storm.Residents[i];
                if (!residents.ContainsKey((ResidentId)row.Resident))
                {
                    throw new InvalidOperationException("STORM resident is unknown");
                }
                if (!residentIds.Add(row.Resident))
                {
                    throw new InvalidOperationException("Duplicate STORM resident " + row.Resident.ToString());
                }
                if (row.Shelter < 0L || (row.Shelter != 0L
                    && (row.Shelter >= NextObjectId || row.Shelter <= 0L)))
                {
                    throw new InvalidOperationException("STORM shelter is outside the object high-water mark");
                }
                if (row.RoutedEvent < 0L || (row.RoutedEvent != 0L
                    && (row.RoutedEvent >= NextEventId || !HasEvent(row.RoutedEvent))))
                {
                    throw new InvalidOperationException("STORM route names an unknown event");
                }
            }
            for (int i = 0; i < storm.Exposures.Count; i++)
            {
                StormExposureRow row = storm.Exposures[i];
                if (!residents.ContainsKey((ResidentId)row.Resident) || !HasEvent(row.Event))
                {
                    throw new InvalidOperationException("STORM exposure references unknown state");
                }
                for (int j = 0; j < i; j++)
                {
                    if (storm.Exposures[j].Event == row.Event
                        && storm.Exposures[j].Resident == row.Resident)
                    {
                        throw new InvalidOperationException("Duplicate STORM exposure");
                    }
                }
            }
        }

        private bool HasEvent(long id)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if ((long)events[i].Event == id)
                {
                    return true;
                }
            }
            return false;
        }

        private void CheckHighWaterMarks()
        {
            long maxResident = 0;
            foreach (ResidentId id in residents.Keys)
            {
                if ((long)id > maxResident)
                {
                    maxResident = (long)id;
                }
            }
            if (NextResidentId <= maxResident)
            {
                throw new InvalidOperationException("NextResidentId would reuse ResidentId " + NextResidentId.ToString());
            }
            long maxHousehold = 0;
            foreach (HouseholdId id in households.Keys)
            {
                if ((long)id > maxHousehold)
                {
                    maxHousehold = (long)id;
                }
            }
            if (NextHouseholdId <= maxHousehold)
            {
                throw new InvalidOperationException("NextHouseholdId would reuse HouseholdId " + NextHouseholdId.ToString());
            }
            long maxHome = 0;
            foreach (HomeId id in homes.Keys)
            {
                if ((long)id > maxHome)
                {
                    maxHome = (long)id;
                }
            }
            if (NextHomeId <= maxHome)
            {
                throw new InvalidOperationException("NextHomeId would reuse HomeId " + NextHomeId.ToString());
            }
            long maxEvent = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if ((long)events[i].Event > maxEvent)
                {
                    maxEvent = (long)events[i].Event;
                }
            }
            if (NextEventId <= maxEvent)
            {
                throw new InvalidOperationException("NextEventId would reuse EventId " + NextEventId.ToString());
            }
        }
    }
}
