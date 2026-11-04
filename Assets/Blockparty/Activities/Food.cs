// FOOD@1: cooking and fast-food catalog activities (BP-ACT-03).
// Consumes STATE@1 (WorldState, StateWriter), COMMAND@2 (CommandArbitrator,
// CompleteCurrent promotion), NAV@2 (ActivityExecutor stages/slots/ledger)
// and ECONOMY@1 (Accounts transfers). Plain C# 9, no engine dependency.
// Every authoritative mutation flows through an audited WorldState method
// with StateWriter.ActivityExecutor (BP-SYS-02); home stock and tracked
// actions are this instance's runtime state, like the arbitrator's queues.
// Fast Enter Play Mode: no static mutable state (the catalog is a static
// readonly table), explicit numeric conversions, no wall-clock reads.
// Normative source: REV-0 BP-ACT-03 / BP-SEC-05 catalog, BP-ACT-01/02,
// BP-ECO-01/04, SEIT-ACT-03-P1/N1/N2 and SEIT-IF-EXEC-N4.
using System;
using System.Collections.Generic;
using System.Globalization;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Activities
{
    public sealed class Food
    {
        private const StateWriter Writer = StateWriter.ActivityExecutor;
        private const string CookCode = "ACT-FOOD-01";
        private const string EatCode = "ACT-FOOD-02";
        private const string GroceriesCode = "ACT-FOOD-03";
        private const string FastFoodCode = "ACT-FOOD-04";
        private const string CookingSkill = "Cooking";
        private const string CompletedKind = "FoodCompleted";
        private const long FastFoodCost = 18L;
        private const long GroceryUnitCost = 25L;

        private static readonly string[] Catalog = new string[]
        {
            CookCode,
            EatCode,
            GroceriesCode,
            FastFoodCode,
        };

        private sealed class Stock
        {
            public int Groceries;
            public int Meals;
        }

        private sealed class Tracked
        {
            public ResidentId Resident;
            public string Code;
            public int ReservedGroceries;
            public int ReservedMeals;
            public bool Done;
        }

        // Save capture bundle: home stock plus the tracked food actions and
        // their reservations. SAVE remaps the Action fields through its
        // saved-to-fresh map before Import; dropped actions keep saved ids.
        public sealed class FoodSnapshot
        {
            public sealed class StockRow
            {
                public long Home;
                public int Groceries;
                public int Meals;
            }

            public sealed class TrackedRow
            {
                public long Action;
                public long Resident;
                public string Code;
                public int ReservedGroceries;
                public int ReservedMeals;
                public bool Done;
            }

            public readonly List<StockRow> Stock = new List<StockRow>();
            public readonly List<TrackedRow> Tracked = new List<TrackedRow>();
        }

        private readonly WorldState world;
        private readonly CommandArbitrator arbitrator;
        private readonly ActivityExecutor executor;
        private readonly Accounts accounts;
        private readonly Dictionary<HomeId, Stock> stock = new Dictionary<HomeId, Stock>();
        private readonly Dictionary<ActionInstanceId, Tracked> tracked = new Dictionary<ActionInstanceId, Tracked>();

        public Food(WorldState world, CommandArbitrator arbitrator,
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
            executor.FoodService = this;
        }

        // Fixture/simulation initialization through an authorized writer.
        public void SetHomeStock(StateWriter writer, HomeId home, int groceries, int meals)
        {
            CheckWriter(writer);
            if (groceries < 0)
            {
                throw new ArgumentOutOfRangeException("groceries", "Grocery stock is never negative");
            }
            if (meals < 0)
            {
                throw new ArgumentOutOfRangeException("meals", "Prepared meals are never negative");
            }
            Stock entry;
            if (!stock.TryGetValue(home, out entry))
            {
                entry = new Stock();
                stock.Add(home, entry);
            }
            entry.Groceries = groceries;
            entry.Meals = meals;
        }

        public int GroceryUnits(HomeId home)
        {
            Stock entry;
            if (stock.TryGetValue(home, out entry))
            {
                return entry.Groceries;
            }
            return 0;
        }

        public int PreparedMeals(HomeId home)
        {
            Stock entry;
            if (stock.TryGetValue(home, out entry))
            {
                return entry.Meals;
            }
            return 0;
        }

        // Save capture: stock and tracked rows sorted by id for stable bytes.
        public FoodSnapshot Export()
        {
            FoodSnapshot snapshot = new FoodSnapshot();
            foreach (KeyValuePair<HomeId, Stock> pair in stock)
            {
                FoodSnapshot.StockRow row = new FoodSnapshot.StockRow();
                row.Home = (long)pair.Key;
                row.Groceries = pair.Value.Groceries;
                row.Meals = pair.Value.Meals;
                snapshot.Stock.Add(row);
            }
            SortStock(snapshot.Stock);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                FoodSnapshot.TrackedRow row = new FoodSnapshot.TrackedRow();
                row.Action = (long)pair.Key;
                row.Resident = (long)pair.Value.Resident;
                row.Code = pair.Value.Code;
                row.ReservedGroceries = pair.Value.ReservedGroceries;
                row.ReservedMeals = pair.Value.ReservedMeals;
                row.Done = pair.Value.Done;
                snapshot.Tracked.Add(row);
            }
            SortTracked(snapshot.Tracked);
            return snapshot;
        }

        // Save restore: replaces stock and tracked state after validating
        // every row, so a forged bundle is refused instead of half-applied.
        public void Import(StateWriter writer, FoodSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.Stock == null || snapshot.Tracked == null)
            {
                throw new ArgumentException("Food bundle lists must exist", "snapshot");
            }
            Dictionary<HomeId, Stock> nextStock = new Dictionary<HomeId, Stock>();
            for (int i = 0; i < snapshot.Stock.Count; i++)
            {
                FoodSnapshot.StockRow row = snapshot.Stock[i];
                if (row == null)
                {
                    throw new ArgumentException("Null stock row " + i.ToString(), "snapshot");
                }
                if (row.Groceries < 0 || row.Meals < 0)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Food stock is never negative");
                }
                HomeId home = (HomeId)row.Home;
                if (nextStock.ContainsKey(home))
                {
                    throw new ArgumentException("Duplicate stock row for home "
                        + row.Home.ToString(), "snapshot");
                }
                Stock entry = new Stock();
                entry.Groceries = row.Groceries;
                entry.Meals = row.Meals;
                nextStock.Add(home, entry);
            }
            Dictionary<ActionInstanceId, Tracked> nextTracked = new Dictionary<ActionInstanceId, Tracked>();
            for (int i = 0; i < snapshot.Tracked.Count; i++)
            {
                FoodSnapshot.TrackedRow row = snapshot.Tracked[i];
                if (row == null)
                {
                    throw new ArgumentException("Null tracked row " + i.ToString(), "snapshot");
                }
                if (row.Code == null || row.Code.Length == 0 || !IsFoodCode(row.Code))
                {
                    throw new ArgumentException("Tracked row " + i.ToString()
                        + " is not a food activity", "snapshot");
                }
                if (row.ReservedGroceries < 0 || row.ReservedMeals < 0)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Food reservations are never negative");
                }
                ActionInstanceId action = (ActionInstanceId)row.Action;
                if (nextTracked.ContainsKey(action))
                {
                    throw new ArgumentException("Duplicate tracked row for action "
                        + row.Action.ToString(), "snapshot");
                }
                ResidentRecord owner;
                if (!world.TryGetResident((ResidentId)row.Resident, out owner))
                    throw new ArgumentException("Tracked food resident is missing", "snapshot");
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
                    if (!matches) throw new ArgumentException("Tracked food action does not match resident/activity", "snapshot");
                    // A mapped action that already terminated without completing
                    // (a failed resume) releases its reservation at load, like Tick.
                    if (execution != null && execution.IsTerminal
                        && execution.Outcome != TerminalOutcome.Completed)
                    {
                        done = true;
                    }
                }
                Tracked entry = new Tracked();
                entry.Resident = (ResidentId)row.Resident;
                entry.Code = row.Code;
                entry.ReservedGroceries = done ? 0 : row.ReservedGroceries;
                entry.ReservedMeals = done ? 0 : row.ReservedMeals;
                entry.Done = done;
                nextTracked.Add(action, entry);
            }
            stock.Clear();
            foreach (KeyValuePair<HomeId, Stock> pair in nextStock)
            {
                stock.Add(pair.Key, pair.Value);
            }
            tracked.Clear();
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in nextTracked)
            {
                tracked.Add(pair.Key, pair.Value);
            }
        }

        // Food inputs validate before forwarding; every other command uses
        // the real arbitrator unchanged. A refusal mutates nothing.
        public CommandResult Submit(CommandEnvelope command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            string code = FoodCode(command);
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
                TrackFood(command.Resident.Value);
            }
            return result;
        }

        // Drives the real executor, then reserves grocery stock at Perform
        // and applies catalog costs/rewards only on natural timed
        // completion. Interrupted, failed and abandoned actions release
        // their reservation with no reward. The arbitrator sweep first
        // adopts food actions submitted past Submit (BOT, autonomy).
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            // Cancelled actions have left COMMAND even before EXEC consumes interruptions.
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                Tracked entry = pair.Value;
                ActionEntry current = arbitrator.GetCurrent(entry.Resident);
                bool live = current != null && current.Action == pair.Key;
                foreach (ActionEntry pending in arbitrator.GetPending(entry.Resident)) live |= pending.Action == pair.Key;
                if (!live) { entry.ReservedGroceries = 0; entry.ReservedMeals = 0; }
            }
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
                    entry.ReservedGroceries = 0;
                    entry.ReservedMeals = 0;
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
                if (!Refuse(entry.Code, performing, entry).Accepted)
                {
                    CancelFood(entry.Resident, arbitrator.GetCurrent(entry.Resident), true, entry);
                    continue;
                }
                if (string.Equals(entry.Code, CookCode, StringComparison.Ordinal))
                {
                    entry.ReservedGroceries = 1;
                }
                else if (string.Equals(entry.Code, EatCode, StringComparison.Ordinal))
                {
                    entry.ReservedMeals = 1;
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

        private CommandResult Refuse(string code, ResidentRecord record, Tracked own = null)
        {
            if (string.Equals(code, CookCode, StringComparison.Ordinal))
            {
                if (AvailableGroceries(record.Home) + (own == null ? 0 : own.ReservedGroceries) < 1)
                {
                    return CommandResult.Reject(RejectReason.ObjectUnavailable);
                }
                return CommandResult.Accept();
            }
            if (string.Equals(code, EatCode, StringComparison.Ordinal))
            {
                if (AvailableMeals(record.Home) + (own == null ? 0 : own.ReservedMeals) < 1)
                {
                    return CommandResult.Reject(RejectReason.ObjectUnavailable);
                }
                return CommandResult.Accept();
            }
            if (string.Equals(code, FastFoodCode, StringComparison.Ordinal))
            {
                if (record.Stage < LifeStage.Teen)
                    return CommandResult.Reject(RejectReason.StageIneligible);
                if (record.Money < FastFoodCost)
                {
                    return CommandResult.Reject(RejectReason.InsufficientFunds);
                }
                return CommandResult.Accept();
            }
            if (record.Money < GroceryUnitCost)
            {
                return CommandResult.Reject(RejectReason.InsufficientFunds);
            }
            return CommandResult.Accept();
        }

        // Adopts every queued or in-progress food action whatever submitted
        // it. Runs before the executor advances so an action completing on
        // this tick is still tracked; TrackEntry dedupes Submit-tracked ones.
        private void SweepArbitrator()
        {
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].IsAlive) TrackFood(residents[i].Id);
            }
        }

        private void TrackFood(ResidentId resident)
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
            if (!IsFoodCode(entry.Activity.Code))
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
            if (!Refuse(entry.Activity.Code, record, known).Accepted)
            {
                CancelFood(resident, entry, current, known);
                return;
            }
            if (known != null) return;
            Tracked next = new Tracked();
            next.Resident = resident;
            next.Code = entry.Activity.Code;
            tracked.Add(entry.Action, next);
        }

        private void CancelFood(ResidentId resident, ActionEntry entry, bool current, Tracked known)
        {
            // Bypassed submissions are already accepted; COMMAND cancellation lets EXEC release holds.
            CommandResult result = arbitrator.Submit(new CommandEnvelope(world.AllocateCommand(Writer),
                world.Epoch, world.GameSeconds,
                arbitrator.AutomationEnabled ? CommandSource.PlayerAutomated : CommandSource.PlayerHuman,
                arbitrator.CurrentStamp(resident).Sequence + 1u,
                current ? CommandKind.CancelCurrent : CommandKind.CancelQueued, resident, null,
                current ? (long?)null : (long)entry.Command, CommandParameters.None.Instance));
            if (!result.Accepted) throw new InvalidOperationException("Food cancellation refused: " + result.Reason);
            if (known != null)
            {
                known.ReservedGroceries = 0;
                known.ReservedMeals = 0;
                known.Done = true;
            }
        }

        private void ApplyReward(ActionInstanceId action, Tracked entry)
        {
            ResidentRecord record;
            world.TryGetResident(entry.Resident, out record);
            if (string.Equals(entry.Code, CookCode, StringComparison.Ordinal))
            {
                AddNeed(record, Need.Hunger, 45.0f);
                AddNeed(record, Need.Health, 2.0f);
                int cookingGain = 10;
                bool certified = false;
                foreach (string course in record.CompletedCourses) certified |= course == "CulinaryCertificate";
                foreach (SkillEntry skill in record.Skills)
                    if (skill.SkillId == CookingSkill && skill.Level >= (certified ? 10 : 5)) cookingGain = 0;
                world.AddSkillPoints(Writer, entry.Resident, CookingSkill, cookingGain);
                Stock pantry = RequireStock(record.Home);
                pantry.Groceries -= 1;
                pantry.Meals += 2;
                AppendCompleted(action, entry, "hunger=45;health=2;fun=0;cooking=" + cookingGain.ToString(CultureInfo.InvariantCulture) + ";groceries=-1;meals=2;money=0");
                return;
            }
            if (string.Equals(entry.Code, EatCode, StringComparison.Ordinal))
            {
                AddNeed(record, Need.Hunger, 40.0f);
                RequireStock(record.Home).Meals -= 1;
                AppendCompleted(action, entry, "hunger=40;health=0;fun=0;cooking=0;groceries=0;meals=-1;money=0");
                return;
            }
            if (string.Equals(entry.Code, FastFoodCode, StringComparison.Ordinal))
            {
                if (!accounts.Transfer(Writer, action,
                    AccountRef.Personal(entry.Resident), AccountRef.Expense("FastFood"), FastFoodCost).Accepted) return;
                AddNeed(record, Need.Hunger, 35.0f);
                AddNeed(record, Need.Health, -3.0f);
                AddNeed(record, Need.Fun, 5.0f);
                AppendCompleted(action, entry, "hunger=35;health=-3;fun=5;cooking=0;groceries=0;meals=0;money=-18");
                return;
            }
            int units = (int)Math.Min(4L, Math.Min(record.Money / GroceryUnitCost,
                (long)int.MaxValue - GroceryUnits(record.Home)));
            long cost = GroceryUnitCost * (long)units;
            if (units > 0)
            {
                if (!accounts.Transfer(Writer, action,
                    AccountRef.Personal(entry.Resident), AccountRef.Expense("Groceries"), cost).Accepted) return;
                RequireStock(record.Home).Groceries += units;
            }
            AppendCompleted(action, entry, "hunger=0;health=0;fun=0;cooking=0;groceries=" + units.ToString(CultureInfo.InvariantCulture)
                + ";meals=0;money=" + (-cost).ToString(CultureInfo.InvariantCulture));
        }

        private void AddNeed(ResidentRecord record, Need need, float delta)
        {
            world.SetNeed(Writer, record.Id, need, record.GetNeed(need) + delta);
        }

        private void AppendCompleted(ActionInstanceId action, Tracked entry, string deltas)
        {
            world.AppendEvent(Writer, CompletedKind, entry.Resident,
                "action=" + ((long)action).ToString(CultureInfo.InvariantCulture)
                + ";activity=" + entry.Code + ";" + deltas);
        }

        private int AvailableGroceries(HomeId home)
        {
            int available = GroceryUnits(home);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                if (!pair.Value.Done)
                {
                    ResidentRecord record;
                    if (world.TryGetResident(pair.Value.Resident, out record) && record.Home == home)
                    {
                        available -= pair.Value.ReservedGroceries;
                    }
                }
            }
            return available;
        }

        private int AvailableMeals(HomeId home)
        {
            int available = PreparedMeals(home);
            foreach (KeyValuePair<ActionInstanceId, Tracked> pair in tracked)
            {
                if (!pair.Value.Done)
                {
                    ResidentRecord record;
                    if (world.TryGetResident(pair.Value.Resident, out record) && record.Home == home)
                    {
                        available -= pair.Value.ReservedMeals;
                    }
                }
            }
            return available;
        }

        private Stock RequireStock(HomeId home)
        {
            Stock entry;
            if (!stock.TryGetValue(home, out entry))
            {
                entry = new Stock();
                stock.Add(home, entry);
            }
            return entry;
        }

        private static string FoodCode(CommandEnvelope command)
        {
            if (command.Kind != CommandKind.DirectActivity || !command.Activity.HasValue)
            {
                return null;
            }
            if (IsFoodCode(command.Activity.Value.Code))
            {
                return command.Activity.Value.Code;
            }
            return null;
        }

        private static bool IsFoodCode(string code)
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

        private static void SortStock(List<FoodSnapshot.StockRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                FoodSnapshot.StockRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Home > key.Home)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static void SortTracked(List<FoodSnapshot.TrackedRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                FoodSnapshot.TrackedRow key = rows[i];
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
