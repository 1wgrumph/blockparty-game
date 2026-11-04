// HOME@1: cleaning, repair and stocking catalog activities (BP-ACT-11).
// Follows the FOOD@1 service shape: Home(WorldState, CommandArbitrator,
// ActivityExecutor, Accounts), Submit/Tick/Complete, fixture setters through
// an authorized writer, and an Export/Import snapshot seam for SAVE.
// Plain C# 9, no engine dependency. Every authoritative mutation flows
// through an audited WorldState method or Accounts transfer with
// StateWriter.ActivityExecutor; home state, damage flags and tracked actions
// are this instance's runtime state, like the arbitrator's queues.
// Fast Enter Play Mode: no static mutable state (the catalog is a static
// readonly table), explicit numeric conversions, no wall-clock reads.
// Normative source: REV-0 BP-ACT-11 catalog, BP-ACT-01/02, BP-ECO-01/04,
// SEIT-ACT-11-P1/N1, SEIT-AC-04 and SEIT-IF-EXEC-N9 (traceability).
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Home
    {
        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const string CleanCode = "ACT-HOME-01";
        private const string RepairCode = "ACT-HOME-02";
        private const string StockCode = "ACT-HOME-03";
        private const string HandinessSkill = "Handiness";
        private const long RepairCost = 15L;
        private const long StockUnitCost = 25L;
        private const int StockUnits = 4;
        private const float CleanBonus = 30f;
        private const float MaxCleanliness = 100f;
        private const int HandinessGain = 10;
        private const double StockedDailyDecay = 2.0;
        private const double EmptyDailyDecay = 4.0;

        private static readonly string[] Catalog = new string[]
        {
            CleanCode,
            RepairCode,
            StockCode,
        };

        private sealed class HomeState
        {
            public float Cleanliness;
            public int Supplies;
            public long LastDecayGameSeconds;
        }

        private sealed class Tracked
        {
            public ResidentId Resident;
            public string Code;
            public bool Done;
        }

        // Save capture bundle: home state plus damage flags and the tracked
        // home actions. SAVE remaps the Action fields through its
        // saved-to-fresh map before Import; dropped actions keep saved ids.
        public sealed class HomeSnapshot
        {
            public sealed class StateRow
            {
                public long Home;
                public double Cleanliness;
                public int Supplies;
                public long LastGameSeconds;
            }

            public sealed class DamageRow
            {
                public long Object;
                public bool Damaged;
            }

            public sealed class TrackedRow
            {
                public long Action;
                public long Resident;
                public string Code;
                public bool Done;
            }

            public readonly List<StateRow> States = new List<StateRow>();
            public readonly List<DamageRow> Damaged = new List<DamageRow>();
            public readonly List<TrackedRow> Tracked = new List<TrackedRow>();
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Accounts accounts;
        private readonly Dictionary<HomeId, HomeState> states = new Dictionary<HomeId, HomeState>();
        private readonly Dictionary<ObjectId, bool> damaged = new Dictionary<ObjectId, bool>();
        private readonly Dictionary<ActionInstanceId, Tracked> tracked = new Dictionary<ActionInstanceId, Tracked>();

        public Home(WorldState world, CommandArbitrator arbitrator,
            ActivityExecutor executor, Accounts accounts)
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
            if (accounts == null)
            {
                throw new ArgumentNullException("accounts");
            }
            this.world = world;
            this.arbitrator = arbitrator;
            this.executor = executor;
            this.accounts = accounts;
        }

        // Fixture/simulation initialization through an authorized writer.
        public void SetHomeState(StateWriter writer, HomeId home, float cleanliness, int supplies)
        {
            CheckWriter(writer);
            if (supplies < 0)
            {
                throw new ArgumentOutOfRangeException("supplies", "Household supplies are never negative");
            }
            HomeState entry;
            if (!states.TryGetValue(home, out entry))
            {
                entry = new HomeState();
                states.Add(home, entry);
            }
            entry.Cleanliness = ClampCleanliness(cleanliness);
            entry.Supplies = supplies;
            entry.LastDecayGameSeconds = world.GameSeconds;
        }

        public void SetDamaged(StateWriter writer, ObjectId id, bool value)
        {
            CheckWriter(writer);
            damaged[id] = value;
        }

        public bool IsDamaged(ObjectId id)
        {
            bool value;
            if (damaged.TryGetValue(id, out value))
            {
                return value;
            }
            return false;
        }

        public float Cleanliness(HomeId home)
        {
            HomeState entry;
            if (states.TryGetValue(home, out entry))
            {
                return entry.Cleanliness;
            }
            return 0f;
        }

        public int HouseholdSupplies(HomeId home)
        {
            HomeState entry;
            if (states.TryGetValue(home, out entry))
            {
                return entry.Supplies;
            }
            return 0;
        }

        // Save capture: home states, damage flags and tracked rows sorted by
        // id for stable bytes.
        public HomeSnapshot Export()
        {
            HomeSnapshot snapshot = new HomeSnapshot();
            foreach (KeyValuePair<HomeId, HomeState> pair in states)
            {
                HomeSnapshot.StateRow row = new HomeSnapshot.StateRow();
                row.Home = (long)pair.Key;
                row.Cleanliness = pair.Value.Cleanliness;
                row.Supplies = pair.Value.Supplies;
                row.LastGameSeconds = pair.Value.LastDecayGameSeconds;
                snapshot.States.Add(row);
            }
            SortStates(snapshot.States);
            foreach (KeyValuePair<ObjectId, bool> pair in damaged)
            {
                HomeSnapshot.DamageRow row = new HomeSnapshot.DamageRow();
                row.Object = (long)pair.Key;
                row.Damaged = pair.Value;
                snapshot.Damaged.Add(row);
            }
            SortDamaged(snapshot.Damaged);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                HomeSnapshot.TrackedRow row = new HomeSnapshot.TrackedRow();
                row.Action = (long)pair.Key;
                row.Resident = (long)pair.Value.Resident;
                row.Code = pair.Value.Code;
                row.Done = pair.Value.Done;
                snapshot.Tracked.Add(row);
            }
            SortTracked(snapshot.Tracked);
            return snapshot;
        }

        // Save restore: replaces home state after validating every row, so a
        // forged bundle is refused instead of half-applied.
        public void Import(StateWriter writer, HomeSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.States == null || snapshot.Damaged == null || snapshot.Tracked == null)
            {
                throw new ArgumentException("Home bundle lists must exist", "snapshot");
            }
            Dictionary<HomeId, HomeState> nextStates = new Dictionary<HomeId, HomeState>();
            for (int i = 0; i < snapshot.States.Count; i++)
            {
                HomeSnapshot.StateRow row = snapshot.States[i];
                if (row == null)
                {
                    throw new ArgumentException("Null state row " + i.ToString(), "snapshot");
                }
                if (double.IsNaN(row.Cleanliness) || double.IsInfinity(row.Cleanliness)
                    || row.Cleanliness < 0.0 || row.Cleanliness > MaxCleanliness)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Home cleanliness stays within 0..100");
                }
                if (row.Supplies < 0)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Household supplies are never negative");
                }
                if (row.LastGameSeconds < 0L)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Home decay time is never negative");
                }
                HomeId home = (HomeId)row.Home;
                if (nextStates.ContainsKey(home))
                {
                    throw new ArgumentException("Duplicate state row for home "
                        + row.Home.ToString(), "snapshot");
                }
                HomeState entry = new HomeState();
                entry.Cleanliness = (float)row.Cleanliness;
                entry.Supplies = row.Supplies;
                entry.LastDecayGameSeconds = row.LastGameSeconds;
                nextStates.Add(home, entry);
            }
            Dictionary<ObjectId, bool> nextDamaged = new Dictionary<ObjectId, bool>();
            for (int i = 0; i < snapshot.Damaged.Count; i++)
            {
                HomeSnapshot.DamageRow row = snapshot.Damaged[i];
                if (row == null)
                {
                    throw new ArgumentException("Null damage row " + i.ToString(), "snapshot");
                }
                ObjectId id = (ObjectId)row.Object;
                if (nextDamaged.ContainsKey(id))
                {
                    throw new ArgumentException("Duplicate damage row for object "
                        + row.Object.ToString(), "snapshot");
                }
                nextDamaged.Add(id, row.Damaged);
            }
            Dictionary<ActionInstanceId, Tracked> nextTracked = new Dictionary<ActionInstanceId, Tracked>();
            for (int i = 0; i < snapshot.Tracked.Count; i++)
            {
                HomeSnapshot.TrackedRow row = snapshot.Tracked[i];
                if (row == null)
                {
                    throw new ArgumentException("Null tracked row " + i.ToString(), "snapshot");
                }
                if (row.Code == null || row.Code.Length == 0 || !IsHomeCode(row.Code))
                {
                    throw new ArgumentException("Tracked row " + i.ToString()
                        + " is not a home activity", "snapshot");
                }
                ActionInstanceId action = (ActionInstanceId)row.Action;
                if (nextTracked.ContainsKey(action))
                {
                    throw new ArgumentException("Duplicate tracked row for action "
                        + row.Action.ToString(), "snapshot");
                }
                ResidentRecord owner;
                if (!world.TryGetResident((ResidentId)row.Resident, out owner))
                    throw new ArgumentException("Tracked home resident is missing", "snapshot");
                bool done = row.Done;
                if (!done)
                {
                    ActionEntry live = arbitrator.GetCurrent(owner.Id);
                    if (live == null || live.Action != action)
                    {
                        live = null;
                        foreach (ActionEntry pending in arbitrator.GetPending(owner.Id))
                            if (pending.Action == action) live = pending;
                    }
                    ExecutionRecord execution = executor.GetExecution(action);
                    bool matches = live != null && live.Activity.Code == row.Code;
                    matches |= execution != null && execution.Resident == owner.Id && execution.Activity.Code == row.Code;
                    if (!matches) throw new ArgumentException("Tracked home action does not match resident/activity", "snapshot");
                    // A mapped action that already terminated without completing
                    // (a failed resume) releases at load, like Tick.
                    if (execution != null && execution.IsTerminal
                        && execution.Outcome != TerminalOutcome.Completed)
                    {
                        done = true;
                    }
                }
                Tracked entry = new Tracked();
                entry.Resident = (ResidentId)row.Resident;
                entry.Code = row.Code;
                entry.Done = done;
                nextTracked.Add(action, entry);
            }
            states.Clear();
            foreach (KeyValuePair<HomeId, HomeState> pair in nextStates)
            {
                states.Add(pair.Key, pair.Value);
            }
            damaged.Clear();
            foreach (KeyValuePair<ObjectId, bool> pair in nextDamaged)
            {
                damaged.Add(pair.Key, pair.Value);
            }
            tracked.Clear();
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in nextTracked)
            {
                tracked.Add(pair.Key, pair.Value);
            }
        }

        // Home inputs validate before forwarding; every other command uses
        // the real arbitrator unchanged. A refusal mutates nothing. Cancel
        // envelopes drop the activity/target dressing a direct-activity
        // helper leaves behind so the arbitrator shape check accepts them.
        public CommandResult Submit(CommandEnvelope command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            if (command.Kind == CommandKind.CancelCurrent || command.Kind == CommandKind.CancelQueued)
            {
                command = new CommandEnvelope(command.Id, command.Epoch, command.IssuedAtGameSeconds,
                    command.Source, command.ResidentSequence, command.Kind, command.Resident,
                    null, command.Kind == CommandKind.CancelQueued ? command.TargetId : null,
                    CommandParameters.None.Instance);
            }
            string code = HomeCode(command);
            if (code != null)
            {
                ResidentRecord record;
                if (command.Resident.HasValue && world.TryGetResident(command.Resident.Value, out record))
                {
                    CommandResult refused = Refuse(code, record);
                    if (!refused.Accepted)
                    {
                        return refused;
                    }
                }
            }
            CommandResult result = arbitrator.Submit(command);
            if (result.Accepted && code != null && command.Resident.HasValue)
            {
                TrackHome(command.Resident.Value);
            }
            return result;
        }

        // Drives the real executor, decays cleanliness from elapsed game
        // time, and applies catalog effects only on natural timed
        // completion. Interrupted, failed and abandoned actions finish with
        // no effect. The arbitrator sweep first adopts home actions
        // submitted past Submit (BOT, autonomy).
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            ApplyDecay();
            SweepArbitrator();
            executor.Tick(StateWriter.ActivityExecutor);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                if (entry.Done)
                {
                    continue;
                }
                ExecutionRecord execution = executor.GetExecution(pair.Key);
                if (execution == null)
                {
                    continue;
                }
                if (execution.IsTerminal)
                {
                    ResidentRecord record;
                    world.TryGetResident(entry.Resident, out record);
                    if (execution.Outcome == TerminalOutcome.Completed && record != null && record.IsAlive && Refuse(entry.Code, record).Accepted)
                    {
                        ApplyReward(pair.Key, entry);
                    }
                    entry.Done = true;
                    continue;
                }
                if (execution.Stage != ExecutionStage.Perform)
                {
                    continue;
                }
                ResidentRecord performing;
                world.TryGetResident(entry.Resident, out performing);
                if (!Refuse(entry.Code, performing).Accepted)
                {
                    CancelHome(entry.Resident, arbitrator.GetCurrent(entry.Resident), true, entry);
                    continue;
                }
            }
        }

        // Replayable completion notification, never authority to finish an
        // early, interrupted, mismatched or unknown action. Rewards already
        // applied exactly once by Tick, so every notification is refused
        // with no mutation.
        public CommandResult Complete(ResidentId resident, ActionInstanceId action)
        {
            ResidentRecord record;
            if (!world.TryGetResident(resident, out record))
            {
                throw new ArgumentException("Unknown resident " + ((long)resident).ToString(), "resident");
            }
            return CommandResult.Reject(RejectReason.InvalidParameters);
        }

        private CommandResult Refuse(string code, ResidentRecord record)
        {
            if (string.Equals(code, RepairCode, StringComparison.Ordinal))
            {
                if (record.Money < RepairCost)
                {
                    return CommandResult.Reject(RejectReason.InsufficientFunds);
                }
                return CommandResult.Accept();
            }
            if (string.Equals(code, StockCode, StringComparison.Ordinal))
            {
                if (record.Money < StockUnitCost)
                {
                    return CommandResult.Reject(RejectReason.InsufficientFunds);
                }
                return CommandResult.Accept();
            }
            return CommandResult.Accept();
        }

        // Cleanliness decays per elapsed game day: 2 while the home holds
        // supplies, 4 once it runs out. Stocking restores the normal rate.
        private void ApplyDecay()
        {
            long now = world.GameSeconds;
            foreach (KeyValuePair<HomeId, HomeState> pair in states)
            {
                HomeState entry = pair.Value;
                long elapsed = now - entry.LastDecayGameSeconds;
                if (elapsed <= 0L)
                {
                    continue;
                }
                double rate = entry.Supplies > 0 ? StockedDailyDecay : EmptyDailyDecay;
                entry.Cleanliness = ClampCleanliness(entry.Cleanliness
                    - (float)(rate * (double)elapsed / (double)Clockwork.GameSecondsPerGameDay));
                entry.LastDecayGameSeconds = now;
            }
        }

        // Adopts every queued or in-progress home action whatever submitted
        // it. Runs before the executor advances so an action completing on
        // this tick is still tracked; TrackEntry dedupes Submit-tracked ones.
        private void SweepArbitrator()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].IsAlive) TrackHome(residents[i].Id);
            }
        }

        private void TrackHome(ResidentId resident)
        {
            ActionEntry current = arbitrator.GetCurrent(resident);
            if (current != null)
            {
                TrackEntry(resident, current, true);
            }
            ActionEntry[] pending = arbitrator.GetPending(resident);
            for (int i = 0; i < pending.Length; i++)
            {
                TrackEntry(resident, pending[i], false);
            }
        }

        private void TrackEntry(ResidentId resident, ActionEntry entry, bool current)
        {
            if (entry.Kind != CommandKind.DirectActivity)
            {
                return;
            }
            if (!IsHomeCode(entry.Activity.Code))
            {
                return;
            }
            Tracked known;
            tracked.TryGetValue(entry.Action, out known);
            if (known != null && known.Done)
            {
                return;
            }
            ResidentRecord record;
            world.TryGetResident(resident, out record);
            if (!Refuse(entry.Activity.Code, record).Accepted)
            {
                CancelHome(resident, entry, current, known);
                return;
            }
            if (known != null) return;
            Tracked next = new Tracked();
            next.Resident = resident;
            next.Code = entry.Activity.Code;
            tracked.Add(entry.Action, next);
        }

        private void CancelHome(ResidentId resident, ActionEntry entry, bool current, Tracked known)
        {
            // Bypassed submissions are already accepted; COMMAND cancellation lets EXEC release holds.
            CommandResult result = arbitrator.Submit(new CommandEnvelope(world.AllocateCommand(Writer),
                world.Epoch, world.GameSeconds,
                arbitrator.AutomationEnabled ? CommandSource.PlayerAutomated : CommandSource.PlayerHuman,
                arbitrator.CurrentStamp(resident).Sequence + 1u,
                current ? CommandKind.CancelCurrent : CommandKind.CancelQueued, resident, null,
                current ? (long?)null : (long)entry.Command, CommandParameters.None.Instance));
            if (!result.Accepted) throw new InvalidOperationException("Home cancellation refused: " + result.Reason);
            if (known != null)
            {
                known.Done = true;
            }
        }

        private void ApplyReward(ActionInstanceId action, Tracked entry)
        {
            ResidentRecord record;
            world.TryGetResident(entry.Resident, out record);
            if (string.Equals(entry.Code, CleanCode, StringComparison.Ordinal))
            {
                HomeState state = RequireState(record.Home);
                state.Cleanliness = ClampCleanliness(state.Cleanliness + CleanBonus);
                return;
            }
            if (string.Equals(entry.Code, RepairCode, StringComparison.Ordinal))
            {
                if (!accounts.Transfer(Writer, action,
                    AccountRef.Personal(entry.Resident), AccountRef.Expense("HomeRepair"), RepairCost).Accepted) return;
                ExecutionRecord execution = executor.GetExecution(action);
                if (execution != null && execution.TargetId.HasValue)
                {
                    damaged[(ObjectId)execution.TargetId.Value] = false;
                }
                world.AddSkillPoints(Writer, entry.Resident, HandinessSkill, HandinessGain);
                return;
            }
            int room = int.MaxValue - HouseholdSupplies(record.Home);
            int units = (int)Math.Min((long)StockUnits, Math.Min(record.Money / StockUnitCost, (long)room));
            long cost = StockUnitCost * (long)units;
            if (units > 0)
            {
                if (!accounts.Transfer(Writer, action,
                    AccountRef.Personal(entry.Resident), AccountRef.Expense("HomeSupplies"), cost).Accepted) return;
                RequireState(record.Home).Supplies += units;
            }
        }

        private HomeState RequireState(HomeId home)
        {
            HomeState entry;
            if (!states.TryGetValue(home, out entry))
            {
                entry = new HomeState();
                entry.LastDecayGameSeconds = world.GameSeconds;
                states.Add(home, entry);
            }
            return entry;
        }

        private static float ClampCleanliness(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }
            if (value > MaxCleanliness)
            {
                return MaxCleanliness;
            }
            return value;
        }

        private static string HomeCode(CommandEnvelope command)
        {
            if (command.Kind != CommandKind.DirectActivity || !command.Activity.HasValue)
            {
                return null;
            }
            if (IsHomeCode(command.Activity.Value.Code))
            {
                return command.Activity.Value.Code;
            }
            return null;
        }

        private static bool IsHomeCode(string code)
        {
            for (int i = 0; i < Catalog.Length; i++)
            {
                if (string.Equals(Catalog[i], code, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void SortStates(List<HomeSnapshot.StateRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                HomeSnapshot.StateRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Home > key.Home)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static void SortDamaged(List<HomeSnapshot.DamageRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                HomeSnapshot.DamageRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Object > key.Object)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static void SortTracked(List<HomeSnapshot.TrackedRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                HomeSnapshot.TrackedRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Action > key.Action)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
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
