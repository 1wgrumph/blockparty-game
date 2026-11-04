// PERSONALITY@1: local autonomy (BP-CHR-03, BP-SIM-02, BP-SEC-05 scoring).
// Consumes STATE@1 (WorldState, ResidentRecord, needs/traits/bonds/feelings/
// memories, WorldRng) and COMMAND@1 (CommandArbitrator snapshot + autonomous
// enqueue). Plain C# 9, no UnityEngine dependency. Reads world state only;
// the one mutation (enqueue the winner) flows through the arbitrator's own
// audited path as StateWriter.CommandArbitrator, so this file needs no new
// writer identity (BP-SYS-02). Decision records are this instance's runtime
// state, like arbitrator queues; SAVE persists them later.
// Fast Enter Play Mode: no static mutable state (static tables are readonly),
// explicit numeric conversions, no AppDomain, Assembly.Location,
// BinaryFormatter, wall-clock reads or String.GetHashCode.
// Normative source: SDoc BP-SEC-05 (benefit/trait/social/history/routine/
// jitter score, remedy restriction, acceptance and sentiment draws) plus the
// section 5 catalog gains. One jitter draw is taken per scored candidate:
// a single per-decision multiplier could never change a ranking, which is
// the formula's evident purpose; every draw is recorded in the log below.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // One scored candidate: every input the BP-SEC-05 formula used, so an
    // independent oracle can recompute the score from this record alone.
    public sealed class CandidateScore
    {
        public readonly ActivityId Activity;
        public readonly long? TargetId;
        public readonly double Benefit;
        public readonly double TraitFactor;
        public readonly double SocialFactor;
        public readonly double HistoryFactor;
        public readonly double RoutineFactor;
        public readonly double Jitter;
        public readonly double PathMinutes;
        public readonly double Score;
        public readonly long HistoryEventId;

        public CandidateScore(ActivityId activity, long? targetId, double benefit,
            double traitFactor, double socialFactor, double historyFactor,
            double routineFactor, double jitter, double pathMinutes, double score,
            long historyEventId)
        {
            Activity = activity;
            TargetId = targetId;
            Benefit = benefit;
            TraitFactor = traitFactor;
            SocialFactor = socialFactor;
            HistoryFactor = historyFactor;
            RoutineFactor = routineFactor;
            Jitter = jitter;
            PathMinutes = pathMinutes;
            Score = score;
            HistoryEventId = historyEventId;
        }
    }

    // An offered action that never reached scoring, with the reason.
    public readonly struct InvalidCandidate
    {
        public readonly ActivityId Activity;
        public readonly long? TargetId;
        public readonly string Reason;

        public InvalidCandidate(ActivityId activity, long? targetId, string reason)
        {
            Activity = activity;
            TargetId = targetId;
            Reason = reason ?? string.Empty;
        }
    }

    // One autonomous decision: every scored candidate with inputs, the chance
    // used per candidate, and the selected activity (BP-SIM-02). IdleReason
    // is non-empty exactly when nothing was chosen; BlockedReason is
    // non-empty when the <=10 remedy restriction found no remedy and the
    // remaining options were scored instead.
    public sealed class AutonomyDecision
    {
        public readonly long Sequence;
        public readonly long GameSeconds;
        public readonly ResidentId Resident;
        public readonly string Trigger;
        public readonly int OfferedCount;
        public readonly IReadOnlyList<CandidateScore> Candidates;
        public readonly IReadOnlyList<InvalidCandidate> Invalid;
        public readonly ActivityId Chosen;
        public readonly long? ChosenTarget;
        public readonly string IdleReason;
        public readonly string BlockedReason;
        public readonly string CommittedEffect;

        public AutonomyDecision(long sequence, long gameSeconds, ResidentId resident,
            string trigger, int offeredCount, IReadOnlyList<CandidateScore> candidates,
            IReadOnlyList<InvalidCandidate> invalid, ActivityId chosen, long? chosenTarget,
            string idleReason, string blockedReason, string committedEffect)
        {
            Sequence = sequence;
            GameSeconds = gameSeconds;
            Resident = resident;
            Trigger = trigger ?? string.Empty;
            OfferedCount = offeredCount;
            Candidates = candidates;
            Invalid = invalid;
            Chosen = chosen;
            ChosenTarget = chosenTarget;
            IdleReason = idleReason ?? string.Empty;
            BlockedReason = blockedReason ?? string.Empty;
            CommittedEffect = committedEffect ?? string.Empty;
        }

        public bool HasChoice { get { return IdleReason.Length == 0; } }
    }

    // One recorded uniform draw in [0,1) behind a social outcome.
    public sealed class SocialDraw
    {
        public readonly long Sequence;
        public readonly long GameSeconds;
        public readonly string Kind;
        public readonly ResidentId Resident;
        public readonly ResidentId Other;
        public readonly double Probability;
        public readonly double Draw;
        public readonly bool Accepted;

        public SocialDraw(long sequence, long gameSeconds, string kind,
            ResidentId resident, ResidentId other, double probability,
            double draw, bool accepted)
        {
            Sequence = sequence;
            GameSeconds = gameSeconds;
            Kind = kind ?? string.Empty;
            Resident = resident;
            Other = other;
            Probability = probability;
            Draw = draw;
            Accepted = accepted;
        }
    }

    public sealed class Autonomy
    {
        public const double JitterLow = 0.9;
        public const double JitterHigh = 1.1;
        public const long DayGameSeconds = 86400L;
        public const string PlayerOnlyActivity = "ACT-HOME-04";
        // M5-OD-59 (owner, 2026-11-04): Chat's routine factor is 1 + this x Social urgency.
        public const double LonelyChatWeight = 4.0;

        // Path length in reachable minutes for one candidate. NAV exposes no
        // path-length API yet, so a null provider means 0 (co-located).
        public delegate double PathMinutesFn(ActivityId activity, long? targetId);

        private readonly struct NeedGain
        {
            public readonly Need Need;
            public readonly float Gain;
            public readonly bool SetToFull;

            public NeedGain(Need need, float gain, bool setToFull)
            {
                Need = need;
                Gain = gain;
                SetToFull = setToFull;
            }
        }

        private readonly struct GainRule
        {
            public readonly string Code;
            public readonly NeedGain Gain0;
            public readonly NeedGain Gain1;
            public readonly NeedGain Gain2;
            public readonly int Count;

            public GainRule(string code, NeedGain gain0)
                : this(code, gain0, default(NeedGain), default(NeedGain), 1)
            {
            }

            public GainRule(string code, NeedGain gain0, NeedGain gain1)
                : this(code, gain0, gain1, default(NeedGain), 2)
            {
            }

            public GainRule(string code, NeedGain gain0, NeedGain gain1, NeedGain gain2)
                : this(code, gain0, gain1, gain2, 3)
            {
            }

            private GainRule(string code, NeedGain gain0, NeedGain gain1, NeedGain gain2, int count)
            {
                Code = code ?? string.Empty;
                Gain0 = gain0;
                Gain1 = gain1;
                Gain2 = gain2;
                Count = count;
            }

            public NeedGain GainAt(int index)
            {
                if (index == 0)
                {
                    return Gain0;
                }
                if (index == 1)
                {
                    return Gain1;
                }
                return Gain2;
            }
        }

        // Actor's own expected positive need gains at completion (section 5
        // catalog; costs such as Energy -30 or Fun -5 are not gains and are
        // absent by construction). Open-ended ACT-HEALTH-01 uses its 60-minute
        // rate. Set-to-full entries gain (100 - current). ACT-HOME-05 gains
        // go to the dependent, not the actor, so the actor row is empty.
        private static readonly GainRule[] Gains = new GainRule[]
        {
            new GainRule("ACT-FOOD-01", new NeedGain(Need.Hunger, 45.0f, false), new NeedGain(Need.Health, 2.0f, false)),
            new GainRule("ACT-FOOD-02", new NeedGain(Need.Hunger, 40.0f, false)),
            new GainRule("ACT-FOOD-04", new NeedGain(Need.Hunger, 35.0f, false), new NeedGain(Need.Fun, 5.0f, false)),
            new GainRule("ACT-EDU-01", new NeedGain(Need.Social, 10.0f, false)),
            new GainRule("ACT-REL-01", new NeedGain(Need.Social, 10.0f, false)),
            new GainRule("ACT-REL-02", new NeedGain(Need.Social, 15.0f, false)),
            new GainRule("ACT-PARTY-01", new NeedGain(Need.Social, 30.0f, false), new NeedGain(Need.Fun, 20.0f, false)),
            new GainRule("ACT-PARTY-02", new NeedGain(Need.Social, 30.0f, false), new NeedGain(Need.Fun, 20.0f, false)),
            new GainRule("ACT-SPORT-01", new NeedGain(Need.Health, 3.0f, false)),
            new GainRule("ACT-SPORT-02", new NeedGain(Need.Fun, 25.0f, false), new NeedGain(Need.Social, 10.0f, false)),
            new GainRule("ACT-SPORT-03", new NeedGain(Need.Fun, 15.0f, false)),
            new GainRule("ACT-HEALTH-01", new NeedGain(Need.Energy, 12.0f, false)),
            new GainRule("ACT-HEALTH-02", new NeedGain(Need.Health, 8.0f, false), new NeedGain(Need.Energy, 10.0f, false), new NeedGain(Need.Comfort, 25.0f, false)),
            new GainRule("ACT-HEALTH-03", new NeedGain(Need.Hygiene, 0.0f, true)),
            new GainRule("ACT-HEALTH-04", new NeedGain(Need.Bladder, 0.0f, true)),
            new GainRule("ACT-CLUB-01", new NeedGain(Need.Social, 20.0f, false)),
            new GainRule("ACT-CREA-02", new NeedGain(Need.Fun, 10.0f, false)),
        };

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly List<AutonomyDecision> decisions = new List<AutonomyDecision>();
        private readonly List<SocialDraw> draws = new List<SocialDraw>();
        private WorldRng rng;
        private long nextDecisionSequence = 1;
        private long nextDrawSequence = 1;

        public Autonomy(WorldState world, CommandArbitrator arbitrator)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            this.world = world;
            this.arbitrator = arbitrator;
            rng = new WorldRng(world.Seed);
        }

        public WorldState World { get { return world; } }
        public PathMinutesFn PathMinutes { get; set; }
        // Settings "Free will": off means residents only act on player orders.
        public bool FreeWill { get; set; } = true;
        public ulong RngState { get { return rng.State; } }
        public IReadOnlyList<AutonomyDecision> Decisions { get { return decisions.AsReadOnly(); } }
        public IReadOnlyList<SocialDraw> DrawLog { get { return draws.AsReadOnly(); } }

        // Need urgency: (100 - need)/100, x2 at <=25, x4 instead at <=10.
        public static double Urgency(float need)
        {
            double urgency = (100.0 - (double)need) / 100.0;
            if (need <= 10.0f)
            {
                return urgency * 4.0;
            }
            if (need <= 25.0f)
            {
                return urgency * 2.0;
            }
            return urgency;
        }

        // Trait factor: 1 + mean relevant trait / 100; 1 with no relevant trait.
        public static double TraitFactorFor(string code, ResidentRecord resident)
        {
            if (resident == null)
            {
                throw new ArgumentNullException("resident");
            }
            string prefix = code != null && code.Length >= 9 ? code.Substring(0, 9) : string.Empty;
            int first;
            int second;
            int count;
            if (string.Equals(prefix, "ACT-PARTY", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Sociable);
                second = resident.GetTrait(TraitAxis.Playful);
                count = 2;
            }
            else if (string.Equals(prefix, "ACT-SPORT", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Active);
                second = resident.GetTrait(TraitAxis.Playful);
                count = 2;
            }
            else if (string.Equals(prefix, "ACT-REL-0", StringComparison.Ordinal)
                || string.Equals(prefix, "ACT-CLUB-", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Sociable);
                second = 0;
                count = 1;
            }
            else if (string.Equals(prefix, "ACT-WORK-", StringComparison.Ordinal)
                || string.Equals(prefix, "ACT-EDU-0", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Ambitious);
                second = 0;
                count = 1;
            }
            else if (string.Equals(prefix, "ACT-HEALT", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Active);
                second = 0;
                count = 1;
            }
            else if (string.Equals(prefix, "ACT-CREA-", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Creative);
                second = 0;
                count = 1;
            }
            else if (string.Equals(prefix, "ACT-HOME-", StringComparison.Ordinal))
            {
                first = resident.GetTrait(TraitAxis.Neat);
                second = 0;
                count = 1;
            }
            else
            {
                return 1.0;
            }
            return 1.0 + (((double)first + (double)second) / (double)count) / 100.0;
        }

        // Social acceptance probability (Reconcile uses the same rule).
        public static double AcceptanceProbability(float friendship, float romance,
            int recipientSociable, int negativeIntensity)
        {
            return Clamp(0.5 + (double)friendship / 400.0 + (double)romance / 400.0
                + (double)recipientSociable / 200.0 - 0.1 * (double)negativeIntensity,
                0.05, 0.95);
        }

        // Per-resident sentiment probability for an eligible catalog feeling.
        public static double SentimentProbability(int sociable, float socialNeed, float friendshipToOther)
        {
            return Clamp(0.5 + (double)sociable / 200.0
                + ((double)socialNeed - 50.0) / 400.0 + (double)friendshipToOther / 400.0,
                0.05, 0.95);
        }

        // Decide once for an idle resident from the arbitrator snapshot.
        // fixedJitter pins every candidate draw (tests/SEIT oracles); null draws.
        public AutonomyDecision Decide(ResidentId resident, double? fixedJitter)
        {
            return Decide(resident, null, fixedJitter);
        }

        public AutonomyDecision Decide(ResidentId resident)
        {
            return Decide(resident, null, null);
        }

        // Decide from a caller-supplied offered set (e.g. snapshot codes with
        // NAV-bound partner targets). Offered codes arrive stage-eligible;
        // unknown codes, the player-only activity and unknown targets are
        // recorded invalid and never scored.
        public AutonomyDecision Decide(ResidentId resident, IReadOnlyList<AvailableAction> offered, double? fixedJitter)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            if (fixedJitter.HasValue && (fixedJitter.Value < JitterLow || fixedJitter.Value > JitterHigh))
            {
                throw new ArgumentOutOfRangeException("fixedJitter", "Jitter is bounded to [0.9,1.1]");
            }
            ActionContextSnapshot snapshot = arbitrator.Snapshot(resident);
            IReadOnlyList<AvailableAction> options = offered ?? snapshot.Available;
            if (snapshot.PlayerQueueHead.HasValue)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(),
                    new List<InvalidCandidate>().AsReadOnly(),
                    new ActivityId(null), null, "PlayerQueued", string.Empty, "YieldedToPlayer"));
            }
            // Not logged: an idle household would add one entry per resident per tick.
            if (!FreeWill)
            {
                return new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(),
                    new List<InvalidCandidate>().AsReadOnly(),
                    new ActivityId(null), null, "FreeWillOff", string.Empty, "AwaitingPlayer");
            }
            if (arbitrator.GetCurrent(resident) != null)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(),
                    new List<InvalidCandidate>().AsReadOnly(),
                    new ActivityId(null), null, "Busy", string.Empty, "NoDecisionWhileBusy"));
            }
            if (arbitrator.GetPending(resident).Length > 0)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(),
                    new List<InvalidCandidate>().AsReadOnly(),
                    new ActivityId(null), null, "AlreadyQueued", string.Empty, "NoDecisionWhileQueued"));
            }
            if (!record.IsAlive)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(),
                    new List<InvalidCandidate>().AsReadOnly(),
                    new ActivityId(null), null, "Deceased", string.Empty, "NoDecisionWhenDeceased"));
            }

            List<AvailableAction> valid = new List<AvailableAction>();
            List<InvalidCandidate> invalid = new List<InvalidCandidate>();
            for (int i = 0; i < options.Count; i++)
            {
                string reason = InvalidReason(options[i]);
                if (reason == null)
                {
                    valid.Add(options[i]);
                }
                else
                {
                    invalid.Add(new InvalidCandidate(options[i].Activity, options[i].TargetId, reason));
                }
            }
            if (valid.Count == 0)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, "Autonomy", options.Count,
                    new List<CandidateScore>().AsReadOnly(), invalid.AsReadOnly(),
                    new ActivityId(null), null, "NoValidOption", string.Empty, "IdleWithReason"));
            }

            string trigger = "Autonomy";
            string blocked = string.Empty;
            List<AvailableAction> pool = valid;
            Need? urgent = LowestUrgentNeed(record);
            if (urgent.HasValue)
            {
                List<AvailableAction> remedies = new List<AvailableAction>();
                for (int i = 0; i < valid.Count; i++)
                {
                    if (Improves(valid[i].Activity.Code, urgent.Value, record))
                    {
                        remedies.Add(valid[i]);
                    }
                }
                if (remedies.Count > 0)
                {
                    pool = remedies;
                    trigger = "Remedy:" + urgent.Value.ToString();
                }
                else
                {
                    blocked = "NoRemedyFor:" + urgent.Value.ToString();
                }
            }

            List<CandidateScore> scored = new List<CandidateScore>(pool.Count);
            for (int i = 0; i < pool.Count; i++)
            {
                scored.Add(ScoreCandidate(record, pool[i], fixedJitter));
            }
            CandidateScore winner = null;
            for (int i = 0; i < scored.Count; i++)
            {
                if (!CanAutonomousSelect(record, scored[i]))
                {
                    continue;
                }
                if (winner == null || Beats(scored[i], winner))
                {
                    winner = scored[i];
                }
            }

            if (winner == null)
            {
                return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                    resident, trigger, options.Count,
                    scored.AsReadOnly(), invalid.AsReadOnly(),
                    new ActivityId(null), null, string.IsNullOrEmpty(record.JobId) ? "Unemployed" : "NoMatchingJob", blocked, "IdleWithReason"));
            }

            string effect;
            if (arbitrator.EnqueueAutonomous(resident, winner.Activity, winner.TargetId))
            {
                effect = "Enqueued:" + winner.Activity.Code;
            }
            else
            {
                effect = "NotEnqueued:Refused";
            }
            return Log(new AutonomyDecision(nextDecisionSequence++, world.GameSeconds,
                resident, trigger, options.Count, scored.AsReadOnly(), invalid.AsReadOnly(),
                winner.Activity, winner.TargetId, string.Empty, blocked, effect));
        }

        // One social acceptance draw for recipient toward initiator, recorded.
        public bool DrawAcceptance(ResidentId recipient, ResidentId initiator)
        {
            ResidentRecord self;
            ResidentRecord other;
            if (!world.TryGetResident(recipient, out self))
            {
                throw new ArgumentException("Unknown resident " + ((long)recipient).ToString(), "recipient");
            }
            if (!world.TryGetResident(initiator, out other))
            {
                throw new ArgumentException("Unknown resident " + ((long)initiator).ToString(), "initiator");
            }
            float friendship;
            float romance;
            BondToward(self, initiator, out friendship, out romance);
            double probability = AcceptanceProbability(friendship, romance,
                self.GetTrait(TraitAxis.Sociable), ActiveNegativeIntensity(self, initiator));
            double draw = rng.NextDouble();
            bool accepted = draw < probability;
            draws.Add(new SocialDraw(nextDrawSequence++, world.GameSeconds, "Acceptance",
                recipient, initiator, probability, draw, accepted));
            return accepted;
        }

        // One sentiment draw: assign the eligible catalog feeling to resident
        // only when the draw falls below the sentiment probability, recorded.
        public bool DrawSentiment(ResidentId resident, ResidentId other)
        {
            ResidentRecord self;
            ResidentRecord peer;
            if (!world.TryGetResident(resident, out self))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            if (!world.TryGetResident(other, out peer))
            {
                throw new ArgumentException("Unknown resident " + ((long)other).ToString(), "other");
            }
            float friendship;
            float romance;
            BondToward(self, other, out friendship, out romance);
            double probability = SentimentProbability(self.GetTrait(TraitAxis.Sociable),
                self.GetNeed(Need.Social), friendship);
            double draw = rng.NextDouble();
            bool assigned = draw < probability;
            draws.Add(new SocialDraw(nextDrawSequence++, world.GameSeconds, "Sentiment",
                resident, other, probability, draw, assigned));
            return assigned;
        }

        private AutonomyDecision Log(AutonomyDecision decision)
        {
            decisions.Add(decision);
            return decision;
        }

        private string InvalidReason(AvailableAction option)
        {
            if (string.IsNullOrEmpty(option.Activity.Code))
            {
                return "UnknownActivity";
            }
            if (string.Equals(option.Activity.Code, PlayerOnlyActivity, StringComparison.Ordinal))
            {
                return "PlayerOnly";
            }
            if (!IsKnownCode(option.Activity.Code))
            {
                return "UnknownActivity";
            }
            if (option.TargetId.HasValue && !IsKnownTarget(option.TargetId.Value))
            {
                return "TargetMissing";
            }
            return null;
        }

        private CandidateScore ScoreCandidate(ResidentRecord record, AvailableAction option, double? fixedJitter)
        {
            double benefit = BenefitFor(record, option.Activity.Code);
            double trait = TraitFactorFor(option.Activity.Code, record);
            double social = 1.0;
            double history = 1.0;
            long historyEvent = -1L;
            ResidentId partner;
            if (option.TargetId.HasValue && TryPartner(option.TargetId.Value, out partner))
            {
                social = SocialFactor(record, partner);
                history = HistoryFactor(record, partner, out historyEvent);
            }
            double routine = RoutineFactor(record, option.Activity.Code);
            double jitter = fixedJitter.HasValue ? fixedJitter.Value : JitterLow + rng.NextDouble() * (JitterHigh - JitterLow);
            double pathMinutes = 0.0;
            if (PathMinutes != null)
            {
                double raw = PathMinutes(option.Activity, option.TargetId);
                pathMinutes = !(raw >= 0.0) ? 0.0 : raw;
            }
            double score = benefit * trait * social * history * routine * jitter / (1.0 + pathMinutes / 60.0);
            return new CandidateScore(option.Activity, option.TargetId, benefit, trait,
                social, history, routine, jitter, pathMinutes, score, historyEvent);
        }

        private static double BenefitFor(ResidentRecord record, string code)
        {
            double benefit = 1.0;
            for (int g = 0; g < Gains.Length; g++)
            {
                if (!string.Equals(Gains[g].Code, code, StringComparison.Ordinal))
                {
                    continue;
                }
                for (int i = 0; i < Gains[g].Count; i++)
                {
                    NeedGain gain = Gains[g].GainAt(i);
                    float current = record.GetNeed(gain.Need);
                    double expected = gain.SetToFull
                        ? (double)StateSchema.NeedMax - (double)current
                        : (double)gain.Gain;
                    if (expected > 0.0)
                    {
                        benefit += Urgency(current) * expected;
                    }
                }
            }
            return benefit;
        }

        private double SocialFactor(ResidentRecord record, ResidentId partner)
        {
            float friendship;
            float romance;
            BondToward(record, partner, out friendship, out romance);
            double factor = 1.0 + (double)friendship / 200.0 + (double)romance / 400.0;
            if (factor < 0.1)
            {
                factor = 0.1;
            }
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                Feeling feeling = record.Feelings[i];
                if (feeling.Other != partner || world.GameSeconds >= feeling.ExpiryGameSeconds)
                {
                    continue;
                }
                if (IsPositiveFeeling(feeling.Kind))
                {
                    factor *= 1.0 + 0.25 * (double)feeling.Intensity;
                }
                else
                {
                    factor *= 1.0 - 0.25 * (double)feeling.Intensity;
                }
            }
            return factor;
        }

        // Latest personally known significant event with the partner only;
        // no unobserved history is read. help/gift/reconcile widen to 1.2,
        // argument/rejection narrow to 0.8, anything else (or none) is 1.
        private static double HistoryFactor(ResidentRecord record, ResidentId partner, out long historyEvent)
        {
            MemoryEntry latest = null;
            for (int i = 0; i < record.Memories.Count; i++)
            {
                MemoryEntry memory = record.Memories[i];
                if (!Involves(memory, partner))
                {
                    continue;
                }
                if (latest == null || memory.GameSeconds > latest.GameSeconds)
                {
                    latest = memory;
                }
            }
            if (latest == null)
            {
                historyEvent = -1L;
                return 1.0;
            }
            historyEvent = (long)latest.Event;
            string kind = (latest.Kind ?? string.Empty).ToLowerInvariant();
            if (string.Equals(kind, "help", StringComparison.Ordinal)
                || string.Equals(kind, "reconcile", StringComparison.Ordinal)
                || kind.StartsWith("gift", StringComparison.Ordinal))
            {
                return 1.2;
            }
            if (string.Equals(kind, "argument", StringComparison.Ordinal)
                || kind.StartsWith("reject", StringComparison.Ordinal))
            {
                return 0.8;
            }
            return 1.0;
        }

        // Routine factor 2 for a due eligible school/work/club activity in
        // its entry window; an available invitation instead carries
        // 1 + 0.5 x normalized Sociable, so presets can yield routines to it.
        // Chat carries 1 + LonelyChatWeight x Social urgency, so a lonely resident seeks one out (M5-OD-59).
        private double RoutineFactor(ResidentRecord record, string code)
        {
            if (string.Equals(code, "ACT-REL-01", StringComparison.Ordinal))
            {
                return 1.0 + LonelyChatWeight * Urgency(record.GetNeed(Need.Social));
            }
            if (string.Equals(code, "ACT-PARTY-02", StringComparison.Ordinal))
            {
                return 1.0 + 0.5 * ((double)record.GetTrait(TraitAxis.Sociable) / 50.0);
            }
            long day = world.GameSeconds / DayGameSeconds;
            int weekday = (int)(day % 7L);
            long clock = world.GameSeconds % DayGameSeconds;
            if (weekday <= 4)
            {
                if (string.Equals(code, "ACT-EDU-01", StringComparison.Ordinal)
                    && clock >= 28800L && clock < 30600L)
                {
                    return 2.0;
                }
                if ((string.Equals(code, "ACT-WORK-01", StringComparison.Ordinal)
                        || string.Equals(code, "ACT-WORK-02", StringComparison.Ordinal)
                        || string.Equals(code, "ACT-WORK-04", StringComparison.Ordinal))
                    && (record.JobId ?? string.Empty).Length > 0
                    && MatchesJob(record.JobId, code)
                    && clock >= 32400L && clock < 34200L)
                {
                    return 2.0;
                }
            }
            if (string.Equals(code, "ACT-CLUB-01", StringComparison.Ordinal)
                && ((weekday == 5 && clock >= 36000L && clock < 37800L)
                    || (weekday == 6 && clock >= 50400L && clock < 52200L)))
            {
                return 2.0;
            }
            return 1.0;
        }

        private static Need? LowestUrgentNeed(ResidentRecord record)
        {
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                if (record.GetNeed((Need)i) <= 10.0f)
                {
                    return (Need)i;
                }
            }
            return null;
        }

        private static bool Improves(string code, Need need, ResidentRecord record)
        {
            for (int g = 0; g < Gains.Length; g++)
            {
                if (!string.Equals(Gains[g].Code, code, StringComparison.Ordinal))
                {
                    continue;
                }
                for (int i = 0; i < Gains[g].Count; i++)
                {
                    NeedGain gain = Gains[g].GainAt(i);
                    if (gain.Need != need)
                    {
                        continue;
                    }
                    if (gain.SetToFull)
                    {
                        return record.GetNeed(need) < StateSchema.NeedMax;
                    }
                    return gain.Gain > 0.0f;
                }
            }
            return false;
        }

        private bool TryPartner(long targetId, out ResidentId partner)
        {
            ResidentRecord record;
            if (world.TryGetResident((ResidentId)targetId, out record))
            {
                partner = record.Id;
                return true;
            }
            partner = default(ResidentId);
            return false;
        }

        private bool IsKnownTarget(long targetId)
        {
            ResidentRecord record;
            if (world.TryGetResident((ResidentId)targetId, out record))
            {
                return true;
            }
            return targetId > 0L && targetId < world.NextObjectId;
        }

        private int ActiveNegativeIntensity(ResidentRecord record, ResidentId other)
        {
            int total = 0;
            for (int i = 0; i < record.Feelings.Count; i++)
            {
                Feeling feeling = record.Feelings[i];
                if (feeling.Other == other && world.GameSeconds < feeling.ExpiryGameSeconds
                    && !IsPositiveFeeling(feeling.Kind))
                {
                    total += feeling.Intensity;
                }
            }
            return total;
        }

        private static void BondToward(ResidentRecord record, ResidentId other,
            out float friendship, out float romance)
        {
            friendship = 0.0f;
            romance = 0.0f;
            for (int i = 0; i < record.Bonds.Count; i++)
            {
                if (record.Bonds[i].Other == other)
                {
                    friendship = record.Bonds[i].Friendship;
                    romance = record.Bonds[i].Romance;
                    return;
                }
            }
        }

        private static bool Involves(MemoryEntry memory, ResidentId partner)
        {
            for (int i = 0; i < memory.Participants.Count; i++)
            {
                if (memory.Participants[i] == partner)
                {
                    return true;
                }
            }
            return false;
        }

        // Exact ties resolve by ActivityId, then target (unbound first).
        private static bool Beats(CandidateScore challenger, CandidateScore holder)
        {
            if (challenger.Score != holder.Score)
            {
                return challenger.Score > holder.Score;
            }
            int code = string.Compare(challenger.Activity.Code, holder.Activity.Code, StringComparison.Ordinal);
            if (code != 0)
            {
                return code < 0;
            }
            long challengerTarget = challenger.TargetId.HasValue ? challenger.TargetId.Value : -1L;
            long holderTarget = holder.TargetId.HasValue ? holder.TargetId.Value : -1L;
            return challengerTarget < holderTarget;
        }

        private static bool CanAutonomousSelect(ResidentRecord record, CandidateScore candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Activity.Code)) return false;
            if (IsWorkShift(candidate.Activity.Code))
            {
                return MatchesJob(record.JobId, candidate.Activity.Code);
            }
            return true;
        }

        private static bool IsWorkShift(string code)
        {
            return string.Equals(code, "ACT-WORK-01", StringComparison.Ordinal)
                || string.Equals(code, "ACT-WORK-02", StringComparison.Ordinal)
                || string.Equals(code, "ACT-WORK-04", StringComparison.Ordinal);
        }

        private static bool MatchesJob(string jobId, string code)
        {
            if (string.IsNullOrEmpty(jobId)) return false;
            if (string.Equals(code, "ACT-WORK-01", StringComparison.Ordinal))
                return string.Equals(jobId, "Barista", StringComparison.Ordinal);
            if (string.Equals(code, "ACT-WORK-02", StringComparison.Ordinal))
                return string.Equals(jobId, "JuniorTechnician", StringComparison.Ordinal)
                    || string.Equals(jobId, "Technician", StringComparison.Ordinal);
            if (string.Equals(code, "ACT-WORK-04", StringComparison.Ordinal))
                return string.Equals(jobId, "AIEngineer", StringComparison.Ordinal);
            return false;
        }

        private static bool IsKnownCode(string code)
        {
            if (IsGainCode(code))
            {
                return true;
            }
            switch (code)
            {
                case "ACT-FOOD-03":
                case "ACT-WORK-01":
                case "ACT-WORK-02":
                case "ACT-WORK-03":
                case "ACT-EDU-02":
                case "ACT-EDU-03":
                case "ACT-EDU-04":
                case "ACT-REL-03":
                case "ACT-REL-04":
                case "ACT-REL-05":
                case "ACT-REL-06":
                case "ACT-CLUB-02":
                case "ACT-HOME-01":
                case "ACT-HOME-02":
                case "ACT-HOME-03":
                case "ACT-HOME-04":
                case "ACT-HOME-05":
                case "ACT-CREA-01":
                case "ACT-CREA-03":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsGainCode(string code)
        {
            for (int i = 0; i < Gains.Length; i++)
            {
                if (string.Equals(Gains[i].Code, code, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPositiveFeeling(FeelingKind kind)
        {
            return kind == FeelingKind.Grateful || kind == FeelingKind.Affectionate
                || kind == FeelingKind.Proud;
        }

        private static double Clamp(double value, double low, double high)
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
    }
}
