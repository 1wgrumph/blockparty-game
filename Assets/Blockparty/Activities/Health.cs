// HEALTH@1: sleep, exercise and rest catalog effects plus illness, injury
// and fatigue restrictions (BP-ACT-09). Consumes COMMAND (CommandArbitrator),
// NAV (ActivityExecutor stages/slots/ledger) and STATE (WorldState writers).
// Plain C# 9, no engine dependency. Every authoritative mutation flows through
// an audited WorldState method with StateWriter.ActivityExecutor; tracked
// actions are this instance's runtime state, like the arbitrator's queues.
// Fast Enter Play Mode: no static mutable state, explicit numeric conversions,
// no wall-clock reads. Normative source: REV-0 BP-ACT-09 catalog and the
// section 5 recovery thresholds (unmeasured candidates, not medical claims).
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Health
    {
        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const string SleepCode = "ACT-HEALTH-01";
        private const string RestCode = "ACT-HEALTH-02";
        private const string ExerciseCode = "ACT-SPORT-01";
        private const string RideCode = "ACT-SPORT-03";
        private const string FitnessSkill = "Fitness";
        private const float SleepEnergyPerGameHour = 12f;
        private const long SleepBonusMinGameSeconds = 6L * 3600L;

        private sealed class Tracked
        {
            public ResidentId Resident;
            public string Code;
            public bool Done;
            public bool SleepBaseSet;
            public float SleepBaseEnergy;
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Dictionary<ActionInstanceId, Tracked> tracked = new Dictionary<ActionInstanceId, Tracked>();

        // One row per rewarded completion: the need/skill deltas actually
        // applied, keyed by the action, so callers can show effects exactly once.
        public sealed class Completion
        {
            public ActionInstanceId Action;
            public string Code;
            public readonly Dictionary<string, float> Effects = new Dictionary<string, float>(StringComparer.Ordinal);
        }
        private readonly List<Completion> completions = new List<Completion>();
        public IReadOnlyList<Completion> Completions { get { return completions.AsReadOnly(); } }

        public Health(WorldState world, CommandArbitrator arbitrator, ActivityExecutor executor)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (arbitrator == null)
            {
                throw new ArgumentNullException("arbitrator");
            }
            if (executor == null)
            {
                throw new ArgumentNullException("executor");
            }
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
        }

        // Flagged residents are refused work shifts and exercise with a typed
        // reason and no mutation; every other command uses the real arbitrator
        // unchanged, so Sleep, RestRecover, hygiene and eating stay available.
        public CommandResult Submit(CommandEnvelope command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            string code = DirectCode(command);
            if (code != null && IsBlockedWhenFlagged(code) && command.Resident.HasValue)
            {
                ResidentRecord record;
                if (world.TryGetResident(command.Resident.Value, out record)
                    && (record.Ill || record.Fatigued || record.Injured))
                {
                    return CommandResult.Reject(RejectReason.StageIneligible);
                }
            }
            CommandResult result = arbitrator.Submit(command);
            if (result.Accepted && code != null && IsHealthCode(code) && command.Resident.HasValue)
            {
                Track(command.Resident.Value);
            }
            return result;
        }

        // Drives sleep recovery before the real executor (so sleep completes at
        // Energy 100 on this tick), then applies catalog effects only on natural
        // timed completion. Interrupted, failed and abandoned actions end with no
        // reward. The arbitrator sweep first adopts health actions submitted past
        // Submit. Callers must not tick the executor separately.
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            Sweep();
            ApplySleepRegen();
            executor.Tick(StateWriter.ActivityExecutor);
            ApplyCompletions();
        }

        // Sleep energy is a pure function of elapsed perform time, so every tick
        // recomputes the same value and extra ticks without clock movement are
        // exact no-ops. Interrupted sleep keeps its partial energy, never a bonus.
        private void ApplySleepRegen()
        {
            long now = world.GameSeconds;
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                if (entry.Done || !string.Equals(entry.Code, SleepCode, StringComparison.Ordinal))
                {
                    continue;
                }
                ExecutionRecord execution = executor.GetExecution(pair.Key);
                if (execution == null || execution.IsTerminal || execution.Stage != ExecutionStage.Perform)
                {
                    continue;
                }
                if (execution.PerformStartGameSeconds < 0L)
                {
                    continue;
                }
                ResidentRecord record;
                if (!world.TryGetResident(entry.Resident, out record) || !record.IsAlive)
                {
                    continue;
                }
                if (!entry.SleepBaseSet)
                {
                    entry.SleepBaseEnergy = record.GetNeed(Need.Energy);
                    entry.SleepBaseSet = true;
                }
                long elapsed = now - execution.PerformStartGameSeconds;
                if (elapsed <= 0L)
                {
                    continue;
                }
                float target = entry.SleepBaseEnergy
                    + (SleepEnergyPerGameHour * (float)elapsed) / 3600f;
                if (target > StateSchema.NeedMax)
                {
                    target = StateSchema.NeedMax;
                }
                world.SetNeed(Writer, entry.Resident, Need.Energy, target);
            }
        }

        private void ApplyCompletions()
        {
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                if (entry.Done)
                {
                    continue;
                }
                ExecutionRecord execution = executor.GetExecution(pair.Key);
                if (execution == null || !execution.IsTerminal)
                {
                    continue;
                }
                entry.Done = true;
                if (execution.Outcome != TerminalOutcome.Completed)
                {
                    continue;
                }
                ResidentRecord record;
                if (!world.TryGetResident(entry.Resident, out record) || !record.IsAlive)
                {
                    continue;
                }
                ApplyReward(entry, execution, record);
            }
        }

        private void ApplyReward(Tracked entry,
            ExecutionRecord execution, ResidentRecord record)
        {
            Completion completion = new Completion();
            completion.Action = execution.Action;
            completion.Code = entry.Code;
            completions.Add(completion);
            ApplyEffects(entry, execution, record, completion);
        }

        // Records the requested delta when the stored value moved by it (float
        // rounding only); a clamped change records the delta actually stored.
        private void Adjust(Completion completion, ResidentId id, Need need, float delta)
        {
            ResidentRecord record;
            world.TryGetResident(id, out record);
            float before = record.GetNeed(need);
            world.SetNeed(Writer, id, need, before + delta);
            world.TryGetResident(id, out record);
            float moved = record.GetNeed(need) - before;
            completion.Effects[need.ToString()] = Math.Abs(moved - delta) <= 0.001f ? delta : moved;
        }

        private void AddSkill(Completion completion, ResidentId id, string skill, int points)
        {
            world.AddSkillPoints(Writer, id, skill, points);
            completion.Effects[skill] = points;
        }

        private void ApplyEffects(Tracked entry,
            ExecutionRecord execution, ResidentRecord record, Completion completion)
        {
            if (string.Equals(entry.Code, RideCode, StringComparison.Ordinal))
            {
                Adjust(completion, record.Id, Need.Fun, 15f);
                Adjust(completion, record.Id, Need.Energy, -5f);
                Adjust(completion, record.Id, Need.Hygiene, -5f);
                return;
            }
            if (string.Equals(entry.Code, ExerciseCode, StringComparison.Ordinal))
            {
                Adjust(completion, record.Id, Need.Health, 3f);
                Adjust(completion, record.Id, Need.Energy, -15f);
                Adjust(completion, record.Id, Need.Hygiene, -10f);
                AddSkill(completion, record.Id, FitnessSkill, 15);
                return;
            }
            if (string.Equals(entry.Code, RestCode, StringComparison.Ordinal))
            {
                Adjust(completion, record.Id, Need.Health, 8f);
                Adjust(completion, record.Id, Need.Energy, 10f);
                Adjust(completion, record.Id, Need.Comfort, 25f);
                bool ill = record.Ill && record.GetNeed(Need.Health) < 40f;
                bool fatigued = record.Fatigued && record.GetNeed(Need.Energy) < 25f;
                bool injured = record.Injured && record.GetNeed(Need.Health) < 40f;
                world.SetHealthFlags(Writer, record.Id, record.StarvationHours,
                    ill, fatigued, injured, record.LastChanceDrawDay);
                return;
            }
            // Sleep: Health +1 once for an uninterrupted sleep of at least 6
            // game hours, keyed to this sleep action.
            if (string.Equals(entry.Code, SleepCode, StringComparison.Ordinal)
                && execution.PerformStartGameSeconds >= 0L
                && world.GameSeconds - execution.PerformStartGameSeconds >= SleepBonusMinGameSeconds)
            {
                Adjust(completion, record.Id, Need.Health, 1f);
            }
        }

        private void Sweep()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].IsAlive)
                {
                    Track(residents[i].Id);
                }
            }
        }

        private void Track(ResidentId resident)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null)
            {
                TrackEntry(resident, current);
            }
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++)
            {
                TrackEntry(resident, pending[i]);
            }
        }

        private void TrackEntry(ResidentId resident, ActionEntry entry)
        {
            if (entry.Kind != CommandKind.DirectActivity || !IsHealthCode(entry.Activity.Code))
            {
                return;
            }
            if (tracked.ContainsKey(entry.Action))
            {
                return;
            }
            Tracked next = new Tracked();
            next.Resident = resident;
            next.Code = entry.Activity.Code;
            tracked.Add(entry.Action, next);
        }

        private static string DirectCode(CommandEnvelope command)
        {
            if (command.Kind != CommandKind.DirectActivity || !command.Activity.HasValue)
            {
                return null;
            }
            return command.Activity.Value.Code;
        }

        private static bool IsHealthCode(string code)
        {
            return string.Equals(code, SleepCode, StringComparison.Ordinal)
                || string.Equals(code, RestCode, StringComparison.Ordinal)
                || string.Equals(code, ExerciseCode, StringComparison.Ordinal)
                || string.Equals(code, RideCode, StringComparison.Ordinal);
        }

        private static bool IsBlockedWhenFlagged(string code)
        {
            return string.Equals(code, "ACT-WORK-01", StringComparison.Ordinal)
                || string.Equals(code, "ACT-WORK-02", StringComparison.Ordinal)
                || string.Equals(code, ExerciseCode, StringComparison.Ordinal)
                || string.Equals(code, RideCode, StringComparison.Ordinal);
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
            {
                throw new ArgumentException("Unknown state writer", "writer");
            }
        }
    }
}
