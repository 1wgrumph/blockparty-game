// TIERS@1: deterministic Detailed/Reduced population scheduler.
// Consumes STATE@1 (WorldState, ResidentRecord, needs, StateWriter), CLOCK@1
// (Clock, SimTick), COMMAND@1 (CommandArbitrator queues), EXEC
// (ActivityExecutor fixed-tick dispatch/advance) and PERSONALITY@1 (Autonomy
// idle planning). Plain C# 9, no UnityEngine dependency. Every authoritative
// mutation flows through an audited WorldState method, the real Clock, the
// real executor or the real autonomy instance; this file adds no writer.
// Fast Enter Play Mode: no static mutable state (static tables are readonly),
// explicit numeric conversions, no AppDomain, Assembly.Location,
// BinaryFormatter, wall-clock reads or String.GetHashCode.
// Membership: camera-visible, selected and followed residents plus the
// transitive closure over shared groups plus player-involved residents run
// Detailed; everyone else runs Reduced. Tiers change planning cadence only:
// needs, clock, aging and completion effects integrate identically every
// fixed 6-game-second tick, so off-camera populations advance with identical
// semantics. Needs anchors (value, game-seconds) per resident and need keep
// the SDoc TR-01 line exact across ticks: incremental float steps would
// random-walk past any tight oracle, while re-anchoring from a stored float
// mid-stream keeps that float's rounding. External need writes (setup,
// remedies) are adopted at the current tick start; clamp events re-pin to
// the exact 0/100 bound, matching clamp-each-step. Completion need deltas
// apply once per completed action from the SDoc section 5 flat entries;
// proportional party shares, partner fan-out and skill/career/money effects
// stay with their owning packages.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public enum SimulationTier
    {
        Detailed = 0,
        Reduced = 1,
    }

    public sealed class TierContext
    {
        public readonly ResidentId[] Visible;
        public readonly ResidentId? Selected;
        public readonly ResidentId? Followed;
        public readonly ResidentId[][] SharedGroups;

        public TierContext(ResidentId[] visible, ResidentId? selected,
            ResidentId? followed, ResidentId[][] sharedGroups)
        {
            Visible = visible ?? new ResidentId[0];
            Selected = selected;
            Followed = followed;
            SharedGroups = sharedGroups ?? new ResidentId[0][];
        }
    }

    public sealed class TierCostSnapshot
    {
        public int Population { get; private set; }
        public int DetailedCount { get; private set; }
        public int ReducedCount { get; private set; }
        public long DetailedWorkUnits { get; private set; }
        public long ReducedWorkUnits { get; private set; }

        public TierCostSnapshot(int population, int detailedCount, int reducedCount,
            long detailedWorkUnits, long reducedWorkUnits)
        {
            Population = population;
            DetailedCount = detailedCount;
            ReducedCount = reducedCount;
            DetailedWorkUnits = detailedWorkUnits;
            ReducedWorkUnits = reducedWorkUnits;
        }
    }

    public sealed class PopulationScheduler
    {
        private const StateWriter ExecutorWriter = StateWriter.ActivityExecutor;
        private const double UrgentNeedThreshold = 10.0;
        private const long ReducedPlanIntervalGameSeconds = 60L;

        // Candidate decay per game hour, awake (SDoc BP-SEC-05), Need order.
        private static readonly double[] HourlyDecay = new double[]
        {
            4.0, 3.0, 6.0, 2.0, 2.0, 2.0, 1.0, 0.0,
        };

        private readonly struct NeedDelta
        {
            public readonly Need Need;
            public readonly float Delta;
            public readonly bool SetToFull;

            public NeedDelta(Need need, float delta, bool setToFull)
            {
                Need = need;
                Delta = delta;
                SetToFull = setToFull;
            }
        }

        private readonly struct GainRule
        {
            public readonly string Code;
            public readonly NeedDelta Delta0;
            public readonly NeedDelta Delta1;
            public readonly NeedDelta Delta2;
            public readonly int Count;

            public GainRule(string code, NeedDelta delta0)
                : this(code, delta0, default(NeedDelta), default(NeedDelta), 1)
            {
            }

            public GainRule(string code, NeedDelta delta0, NeedDelta delta1)
                : this(code, delta0, delta1, default(NeedDelta), 2)
            {
            }

            public GainRule(string code, NeedDelta delta0, NeedDelta delta1, NeedDelta delta2)
                : this(code, delta0, delta1, delta2, 3)
            {
            }

            private GainRule(string code, NeedDelta delta0, NeedDelta delta1, NeedDelta delta2, int count)
            {
                Code = code ?? string.Empty;
                Delta0 = delta0;
                Delta1 = delta1;
                Delta2 = delta2;
                Count = count;
            }

            public NeedDelta DeltaAt(int index)
            {
                if (index == 0)
                {
                    return Delta0;
                }
                if (index == 1)
                {
                    return Delta1;
                }
                return Delta2;
            }
        }

        // Flat SDoc section 5 completion need deltas (actor side). Costs apply
        // once at completion like gains; proportional party shares, partner
        // fan-out, the shower/sink variant, sleep rates and non-need progress
        // belong to EXEC and the owning packages.
        private static readonly GainRule[] CompletionGains = new GainRule[]
        {
            new GainRule("ACT-FOOD-01", new NeedDelta(Need.Hunger, 45.0f, false), new NeedDelta(Need.Health, 2.0f, false)),
            new GainRule("ACT-FOOD-02", new NeedDelta(Need.Hunger, 40.0f, false)),
            new GainRule("ACT-FOOD-04", new NeedDelta(Need.Hunger, 35.0f, false), new NeedDelta(Need.Health, -3.0f, false), new NeedDelta(Need.Fun, 5.0f, false)),
            new GainRule("ACT-WORK-01", new NeedDelta(Need.Energy, -30.0f, false), new NeedDelta(Need.Fun, -10.0f, false)),
            new GainRule("ACT-WORK-02", new NeedDelta(Need.Energy, -40.0f, false), new NeedDelta(Need.Fun, -15.0f, false)),
            new GainRule("ACT-EDU-01", new NeedDelta(Need.Energy, -25.0f, false), new NeedDelta(Need.Social, 10.0f, false)),
            new GainRule("ACT-EDU-02", new NeedDelta(Need.Fun, -5.0f, false)),
            new GainRule("ACT-REL-01", new NeedDelta(Need.Social, 10.0f, false)),
            new GainRule("ACT-REL-02", new NeedDelta(Need.Social, 15.0f, false)),
            new GainRule("ACT-SPORT-01", new NeedDelta(Need.Health, 3.0f, false), new NeedDelta(Need.Energy, -15.0f, false), new NeedDelta(Need.Hygiene, -10.0f, false)),
            new GainRule("ACT-SPORT-02", new NeedDelta(Need.Fun, 25.0f, false), new NeedDelta(Need.Social, 10.0f, false)),
            new GainRule("ACT-SPORT-03", new NeedDelta(Need.Fun, 15.0f, false), new NeedDelta(Need.Energy, -5.0f, false), new NeedDelta(Need.Hygiene, -5.0f, false)),
            new GainRule("ACT-HEALTH-02", new NeedDelta(Need.Health, 8.0f, false), new NeedDelta(Need.Energy, 10.0f, false), new NeedDelta(Need.Comfort, 25.0f, false)),
            new GainRule("ACT-HEALTH-04", new NeedDelta(Need.Bladder, 0.0f, true)),
            new GainRule("ACT-CLUB-01", new NeedDelta(Need.Social, 20.0f, false)),
            new GainRule("ACT-CREA-02", new NeedDelta(Need.Fun, 10.0f, false)),
        };

        private sealed class NeedAnchors
        {
            public readonly double[] Baseline = new double[StateSchema.NeedCount];
            public readonly long[] Since = new long[StateSchema.NeedCount];
            public readonly bool[] Has = new bool[StateSchema.NeedCount];
        }

        private readonly WorldState world;
        private readonly Clock clock;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Autonomy autonomy;
        private readonly Dictionary<ResidentId, SimulationTier> tiers = new Dictionary<ResidentId, SimulationTier>();
        private readonly Dictionary<ResidentId, NeedAnchors> anchors = new Dictionary<ResidentId, NeedAnchors>();
        private readonly Dictionary<ActionInstanceId, bool> known = new Dictionary<ActionInstanceId, bool>();
        private readonly Dictionary<ActionInstanceId, bool> settled = new Dictionary<ActionInstanceId, bool>();
        private Dictionary<ResidentId, long> idleSince { get { return world.TierIdleSince; } }
        private TierCostSnapshot costs;
        private TierContext lastContext;

        public PopulationScheduler(WorldState world, Clock clock, CommandArbitrator arbitrator,
            ActivityExecutor executor, Autonomy autonomy)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            if (autonomy == null)
            {
                throw new ArgumentNullException("autonomy");
            }
            this.world = world;
            this.clock = clock;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.autonomy = autonomy;
            CaptureAnchors(world.ResidentsSnapshot(), world.GameSeconds);
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                tiers[residents[i].Id] = SimulationTier.Reduced;
            }
            costs = new TierCostSnapshot(residents.Length, 0, residents.Length, 0L, 0L);
        }

        public TierCostSnapshot Costs { get { return costs; } }

        // Optional offered set for autonomy (live play filters what its
        // services would refuse); null keeps the arbitrator snapshot.
        public Func<ResidentId, IReadOnlyList<AvailableAction>> Offered { get; set; }

        public void Recompute(TierContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            ValidateContext(context);
            RecomputeInternal(context);
        }

        public SimulationTier GetTier(ResidentId resident)
        {
            SimulationTier tier;
            if (tiers.TryGetValue(resident, out tier))
            {
                return tier;
            }
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            return SimulationTier.Reduced;
        }

        // One frame chunk: validate everything before mutating, advance the
        // real clock once, then integrate every due fixed tick through the
        // real executor and autonomy. Membership recomputes before each tick.
        public void AdvanceRealSeconds(StateWriter writer, double realSeconds, TierContext context)
        {
            CheckWriter(writer);
            CheckSeconds(realSeconds);
            CheckContext(context);
            lastContext = context;
            RecomputeInternal(context);
            clock.AdvanceRealSeconds(writer, realSeconds, delegate { Tick(writer); });
        }

        // Live frame boundary: integrate the needs line over the elapsed chunk
        // without advancing the shared clock twice; live autonomy plans through
        // PlanIdle. The per-fixed-tick callback would also write every need
        // five times per 0.5 s live frame.
        public void Settle(StateWriter writer, long sinceGameSeconds, TierContext context)
        {
            CheckWriter(writer);
            CheckContext(context);
            if (sinceGameSeconds < 0L || sinceGameSeconds > world.GameSeconds)
            {
                throw new ArgumentOutOfRangeException("sinceGameSeconds", "Chunk start must be inside the world clock");
            }
            lastContext = context;
            RecomputeInternal(context);
            if (sinceGameSeconds != world.GameSeconds)
            {
                Tick(writer, sinceGameSeconds, false);
            }
        }

        // Live frame boundary: local autonomy (BP-SIM-02) plans idle residents
        // every live frame, provider or not; the need line keeps its Settle cadence.
        public void PlanIdle(TierContext context)
        {
            CheckContext(context);
            lastContext = context;
            RecomputeInternal(context);
            PlanAutonomy(world.ResidentsSnapshot(), world.GameSeconds, world.GameSeconds);
        }

        // Snapshot boundary: write the needs line up to now without ticking the
        // executor, so a save between live settlements keeps the pending decay.
        public void SettleNeeds(StateWriter writer, long sinceGameSeconds)
        {
            CheckWriter(writer);
            if (sinceGameSeconds < 0L || sinceGameSeconds > world.GameSeconds)
            {
                throw new ArgumentOutOfRangeException("sinceGameSeconds", "Chunk start must be inside the world clock");
            }
            if (sinceGameSeconds != world.GameSeconds)
            {
                IntegrateNeeds(writer, world.ResidentsSnapshot(), world.GameSeconds, sinceGameSeconds, false);
            }
        }

        private void CheckSeconds(double realSeconds)
        {
            if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException("realSeconds", "Advance amount must be finite and non-negative");
            }
        }

        private void CheckContext(TierContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            ValidateContext(context);
        }

        private void Tick(StateWriter writer)
        {
            Tick(writer, world.GameSeconds - SimTick.TickGameSeconds, true);
        }

        // standalone=false is the live composition: the owning activity services
        // already apply completion deltas, and PlanIdle plans autonomy instead.
        private void Tick(StateWriter writer, long tickStart, bool standalone)
        {
            long now = world.GameSeconds;
            RecomputeInternal(lastContext);
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (arbitrator.GetCurrent(residents[i].Id) == null
                    && arbitrator.GetPending(residents[i].Id).Length > 0)
                {
                    arbitrator.PromoteNext(residents[i].Id);
                }
            }
            // Discover actions before this tick can remove the first restored completion.
            RememberActions(residents);
            IntegrateNeeds(writer, residents, now, tickStart, standalone);
            if (executor.FoodService != null) executor.FoodService.Tick(ExecutorWriter);
            else executor.Tick(ExecutorWriter);
            IntegrateNeeds(writer, residents, now, now, standalone);
            if (standalone)
            {
                PlanAutonomy(residents, now, tickStart);
            }
            long detailedWork = 0L;
            long reducedWork = 0L;
            for (int i = 0; i < residents.Length; i++)
            {
                if (!residents[i].IsAlive)
                {
                    continue;
                }
                if (GetTier(residents[i].Id) == SimulationTier.Detailed)
                {
                    detailedWork++;
                }
                else
                {
                    reducedWork++;
                }
            }
            costs = new TierCostSnapshot(residents.Length, costs.DetailedCount, costs.ReducedCount,
                detailedWork, reducedWork);
        }

        private void RecomputeInternal(TierContext context)
        {
            Dictionary<ResidentId, bool> detailed = new Dictionary<ResidentId, bool>();
            for (int i = 0; i < context.Visible.Length; i++)
            {
                detailed[context.Visible[i]] = true;
            }
            if (context.Selected.HasValue)
            {
                detailed[context.Selected.Value] = true;
            }
            if (context.Followed.HasValue)
            {
                detailed[context.Followed.Value] = true;
            }
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                ActionEntry current = arbitrator.GetCurrent(residents[i].Id);
                if (current != null && current.Owner == ActionOwner.Player)
                {
                    detailed[residents[i].Id] = true;
                    continue;
                }
                ActionEntry[] pending = arbitrator.GetPending(residents[i].Id);
                for (int p = 0; p < pending.Length; p++)
                {
                    if (pending[p].Owner == ActionOwner.Player)
                    {
                        detailed[residents[i].Id] = true;
                        break;
                    }
                }
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int g = 0; g < context.SharedGroups.Length; g++)
                {
                    ResidentId[] group = context.SharedGroups[g];
                    if (group == null)
                    {
                        continue;
                    }
                    bool touches = false;
                    for (int i = 0; i < group.Length; i++)
                    {
                        if (detailed.ContainsKey(group[i]))
                        {
                            touches = true;
                            break;
                        }
                    }
                    if (!touches)
                    {
                        continue;
                    }
                    for (int i = 0; i < group.Length; i++)
                    {
                        if (!detailed.ContainsKey(group[i]))
                        {
                            detailed[group[i]] = true;
                            changed = true;
                        }
                    }
                }
            }
            tiers.Clear();
            int detailedCount = 0;
            for (int i = 0; i < residents.Length; i++)
            {
                SimulationTier tier = detailed.ContainsKey(residents[i].Id)
                    ? SimulationTier.Detailed : SimulationTier.Reduced;
                tiers[residents[i].Id] = tier;
                if (tier == SimulationTier.Detailed)
                {
                    detailedCount++;
                }
            }
            costs = new TierCostSnapshot(residents.Length, detailedCount,
                residents.Length - detailedCount,
                costs.DetailedWorkUnits, costs.ReducedWorkUnits);
        }

        private void ValidateContext(TierContext context)
        {
            for (int i = 0; i < context.Visible.Length; i++)
            {
                RequireKnown(context.Visible[i]);
            }
            if (context.Selected.HasValue)
            {
                RequireKnown(context.Selected.Value);
            }
            if (context.Followed.HasValue)
            {
                RequireKnown(context.Followed.Value);
            }
            for (int g = 0; g < context.SharedGroups.Length; g++)
            {
                ResidentId[] group = context.SharedGroups[g];
                if (group == null)
                {
                    continue;
                }
                for (int i = 0; i < group.Length; i++)
                {
                    RequireKnown(group[i]);
                }
            }
        }

        private void RequireKnown(ResidentId resident)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }

        private void CaptureAnchors(ResidentRecord[] residents, long gameSeconds)
        {
            for (int i = 0; i < residents.Length; i++)
            {
                NeedAnchors anchor = new NeedAnchors();
                for (int n = 0; n < StateSchema.NeedCount; n++)
                {
                    anchor.Baseline[n] = (double)residents[i].GetNeed((Need)n);
                    anchor.Since[n] = gameSeconds;
                    anchor.Has[n] = true;
                }
                anchors[residents[i].Id] = anchor;
            }
        }

        private static double LineValue(int need, double baseline, long since, long gameSeconds)
        {
            double value = baseline - HourlyDecay[need] * (double)(gameSeconds - since) / 3600.0;
            if (value < 0.0)
            {
                return 0.0;
            }
            if (value > 100.0)
            {
                return 100.0;
            }
            return value;
        }

        private void IntegrateNeeds(StateWriter writer, ResidentRecord[] residents, long now, long tickStart, bool completionGains)
        {
            for (int i = 0; i < residents.Length; i++)
            {
                if (!residents[i].IsAlive)
                {
                    continue;
                }
                NeedAnchors anchor;
                if (!anchors.TryGetValue(residents[i].Id, out anchor))
                {
                    anchor = new NeedAnchors();
                    anchors[residents[i].Id] = anchor;
                }
                for (int n = 0; n < StateSchema.NeedCount; n++)
                {
                    float stored = residents[i].GetNeed((Need)n);
                    if (!anchor.Has[n])
                    {
                        anchor.Baseline[n] = (double)stored;
                        anchor.Since[n] = tickStart;
                        anchor.Has[n] = true;
                        continue;
                    }
                    if (stored != (float)LineValue(n, anchor.Baseline[n], anchor.Since[n], tickStart))
                    {
                        anchor.Baseline[n] = (double)stored;
                        anchor.Since[n] = tickStart;
                    }
                }
            }
            if (completionGains)
            {
                ApplyCompletionGains(residents, now);
            }
            for (int i = 0; i < residents.Length; i++)
            {
                if (!residents[i].IsAlive)
                {
                    continue;
                }
                NeedAnchors anchor = anchors[residents[i].Id];
                for (int n = 0; n < StateSchema.NeedCount; n++)
                {
                    world.SetNeed(writer, residents[i].Id, (Need)n,
                        (float)LineValue(n, anchor.Baseline[n], anchor.Since[n], now));
                }
            }
        }

        private void ApplyCompletionGains(ResidentRecord[] residents, long now)
        {
            RememberActions(residents);
            foreach (KeyValuePair<ActionInstanceId, bool> pair in known) SettleAction(pair.Key, now);
        }

        private void RememberActions(ResidentRecord[] residents)
        {
            for (int i = 0; i < residents.Length; i++)
            {
                ActionEntry current = arbitrator.GetCurrent(residents[i].Id);
                if (current != null)
                {
                    known[current.Action] = true;
                }
                ActionEntry[] pending = arbitrator.GetPending(residents[i].Id);
                for (int p = 0; p < pending.Length; p++)
                {
                    known[pending[p].Action] = true;
                }
            }

        }

        private void SettleAction(ActionInstanceId action, long now)
        {
            if (settled.ContainsKey(action))
            {
                return;
            }
            ExecutionRecord record = executor.GetExecution(action);
            if (record == null || !record.IsTerminal)
            {
                return;
            }
            settled[action] = true;
            if (record.Outcome != TerminalOutcome.Completed)
            {
                return;
            }
            if (executor.FoodService != null && record.Activity.Code.StartsWith("ACT-FOOD-", StringComparison.Ordinal)) return;
            NeedAnchors anchor;
            if (!anchors.TryGetValue(record.Resident, out anchor))
            {
                return;
            }
            for (int i = 0; i < CompletionGains.Length; i++)
            {
                if (!string.Equals(CompletionGains[i].Code, record.Activity.Code, StringComparison.Ordinal))
                {
                    continue;
                }
                for (int d = 0; d < CompletionGains[i].Count; d++)
                {
                    NeedDelta delta = CompletionGains[i].DeltaAt(d);
                    int index = (int)delta.Need;
                    if (!anchor.Has[index])
                    {
                        continue;
                    }
                    double current = LineValue(index, anchor.Baseline[index], anchor.Since[index], now);
                    anchor.Baseline[index] = delta.SetToFull ? 100.0 : Math.Max(0.0, Math.Min(100.0, current + delta.Delta));
                    anchor.Since[index] = now;
                }
                return;
            }
        }

        private void PlanAutonomy(ResidentRecord[] residents, long now, long tickStart)
        {
            for (int i = 0; i < residents.Length; i++)
            {
                ResidentId id = residents[i].Id;
                if (!residents[i].IsAlive)
                {
                    continue;
                }
                if (arbitrator.GetCurrent(id) != null || arbitrator.GetPending(id).Length > 0)
                {
                    idleSince[id] = now;
                    continue;
                }
                long since;
                if (!idleSince.TryGetValue(id, out since))
                {
                    since = tickStart;
                    idleSince[id] = since;
                }
                if (GetTier(id) == SimulationTier.Reduced
                    && now - since < ReducedPlanIntervalGameSeconds
                    && !IsUrgent(residents[i]))
                {
                    continue;
                }
                if (Offered == null) autonomy.Decide(id);
                else autonomy.Decide(id, Offered(id), null);
                if (arbitrator.GetCurrent(id) == null && arbitrator.GetPending(id).Length == 0)
                {
                    idleSince[id] = now;
                }
                else
                {
                    idleSince.Remove(id);
                }
            }
        }

        private static bool IsUrgent(ResidentRecord resident)
        {
            for (int n = 0; n < StateSchema.NeedCount; n++)
            {
                if ((double)resident.GetNeed((Need)n) <= UrgentNeedThreshold)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
