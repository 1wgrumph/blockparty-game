// ECONOMY@1: personal/shared balances and exactly-once transfers (BP-ECO-01).
// Consumes STATE@1 (WorldState, StateWriter) and COMMAND@2 (CommandResult).
// Plain C# 9 with no engine reference. Personal money lives in WorldState through its
// sole-writer API; shared household balances live here, never auto-pooled.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public readonly struct AccountRef : IEquatable<AccountRef>
    {
        private enum Kind
        {
            Personal,
            Shared,
            Expense,
        }

        private readonly Kind kind;
        private readonly ResidentId resident;
        private readonly HouseholdId household;
        private readonly string destination;

        private AccountRef(Kind kind, ResidentId resident, HouseholdId household, string destination)
        {
            this.kind = kind;
            this.resident = resident;
            this.household = household;
            this.destination = destination;
        }

        public static AccountRef Personal(ResidentId resident)
        {
            return new AccountRef(Kind.Personal, resident, default(HouseholdId), null);
        }

        public static AccountRef Shared(HouseholdId household)
        {
            return new AccountRef(Kind.Shared, default(ResidentId), household, null);
        }

        public static AccountRef Expense(string destination)
        {
            return new AccountRef(Kind.Expense, default(ResidentId), default(HouseholdId),
                destination ?? string.Empty);
        }

        internal bool IsPersonal { get { return kind == Kind.Personal; } }
        internal bool IsShared { get { return kind == Kind.Shared; } }
        internal bool IsExpense { get { return kind == Kind.Expense; } }
        internal ResidentId Resident { get { return resident; } }
        internal HouseholdId Household { get { return household; } }
        internal string Destination { get { return destination; } }

        public bool Equals(AccountRef other)
        {
            if (kind != other.kind) return false;
            if (kind == Kind.Personal) return resident == other.resident;
            if (kind == Kind.Shared) return household == other.household;
            return destination == other.destination;
        }
    }

    public sealed class AccountTransaction
    {
        public ActionInstanceId Action { get; private set; }
        public AccountRef Source { get; private set; }
        public AccountRef Destination { get; private set; }
        public long Amount { get; private set; }
        public long GameSeconds { get; private set; }

        public AccountTransaction(ActionInstanceId action, AccountRef source,
            AccountRef destination, long amount, long gameSeconds)
        {
            Action = action;
            Source = source;
            Destination = destination;
            Amount = amount;
            GameSeconds = gameSeconds;
        }
    }

    // Save capture bundle: shared balances plus the ledger and the applied
    // set that makes transfers exactly-once. SAVE replays queues through
    // Submit, so queue actions take fresh ids on load; SAVE remaps the
    // Action fields through its saved-to-fresh map before Import, while
    // non-queue payment ids (and history rows) keep their saved values.
    public sealed class AccountsSnapshot
    {
        public sealed class BalanceRow
        {
            public long Household;
            public long Amount;
        }

        public sealed class AccountRefData
        {
            public string Kind;
            public long Id;
            public string Name;
        }

        public sealed class LedgerRow
        {
            public long Action;
            public AccountRefData Source;
            public AccountRefData Destination;
            public long Amount;
            public long GameSeconds;
        }

        public readonly List<BalanceRow> Shared = new List<BalanceRow>();
        public readonly List<LedgerRow> Ledger = new List<LedgerRow>();
        public readonly List<long> Applied = new List<long>();
    }

    public sealed class Accounts
    {
        private readonly WorldState world;
        private readonly Dictionary<HouseholdId, long> shared = new Dictionary<HouseholdId, long>();
        private readonly List<AccountTransaction> ledger = new List<AccountTransaction>();
        private readonly HashSet<ActionInstanceId> applied = new HashSet<ActionInstanceId>();

        public Accounts(WorldState world)
        {
            if (world == null) throw new ArgumentNullException("world");
            this.world = world;
        }

        public IReadOnlyList<AccountTransaction> Ledger { get { return ledger; } }

        public void SetSharedBalance(StateWriter writer, HouseholdId household, long amount)
        {
            CheckWriter(writer);
            if (amount < 0L) throw new ArgumentOutOfRangeException("amount", "Shared balance is never negative");
            shared[household] = amount;
        }

        public long Balance(AccountRef account)
        {
            if (account.IsPersonal) return PersonalBalance(account.Resident);
            if (account.IsShared)
            {
                long balance;
                return shared.TryGetValue(account.Household, out balance) ? balance : 0L;
            }
            throw new ArgumentException("Expense accounts hold no balance", "account");
        }

        public CommandResult Transfer(StateWriter writer, ActionInstanceId action,
            AccountRef source, AccountRef destination, long amount)
        {
            CheckWriter(writer);
            if (applied.Contains(action)) return CommandResult.Accept();
            if (amount <= 0L) return CommandResult.Reject(RejectReason.InvalidParameters);
            if ((!source.IsPersonal && !source.IsShared)
                || (!destination.IsPersonal && !destination.IsShared && !destination.IsExpense))
                return CommandResult.Reject(RejectReason.InvalidParameters);
            long funds = CreditOf(source);
            if (funds < amount) return CommandResult.Reject(RejectReason.InsufficientFunds);
            long destinationFunds = destination.IsExpense ? 0L : CreditOf(destination);
            if (!destination.IsExpense && !source.Equals(destination) && destinationFunds > long.MaxValue - amount)
                return CommandResult.Reject(RejectReason.InvalidParameters);
            Debit(writer, source, funds - amount);
            if (destination.IsPersonal)
                world.SetMoney(writer, destination.Resident, PersonalBalance(destination.Resident) + amount);
            else if (destination.IsShared)
                shared[destination.Household] = CreditOf(destination) + amount;
            ledger.Add(new AccountTransaction(action, source, destination, amount, world.GameSeconds));
            applied.Add(action);
            return CommandResult.Accept();
        }

        private long CreditOf(AccountRef account)
        {
            if (account.IsPersonal) return PersonalBalance(account.Resident);
            long balance;
            shared.TryGetValue(account.Household, out balance);
            return balance;
        }

        private void Debit(StateWriter writer, AccountRef source, long remainder)
        {
            if (source.IsPersonal) world.SetMoney(writer, source.Resident, remainder);
            else shared[source.Household] = remainder;
        }

        private long PersonalBalance(ResidentId id)
        {
            ResidentRecord record;
            if (!world.TryGetResident(id, out record))
                throw new ArgumentException("Unknown resident " + ((long)id).ToString(), "account");
            return record.Money;
        }

        // Save capture: shared balances and applied ids sorted for stable
        // bytes; the ledger keeps insertion order so history reads back
        // unchanged.
        public AccountsSnapshot Export()
        {
            AccountsSnapshot snapshot = new AccountsSnapshot();
            foreach (KeyValuePair<HouseholdId, long> pair in shared)
            {
                AccountsSnapshot.BalanceRow row = new AccountsSnapshot.BalanceRow();
                row.Household = (long)pair.Key;
                row.Amount = pair.Value;
                snapshot.Shared.Add(row);
            }
            SortBalances(snapshot.Shared);
            for (int i = 0; i < ledger.Count; i++)
            {
                AccountTransaction tx = ledger[i];
                AccountsSnapshot.LedgerRow row = new AccountsSnapshot.LedgerRow();
                row.Action = (long)tx.Action;
                row.Source = Describe(tx.Source);
                row.Destination = Describe(tx.Destination);
                row.Amount = tx.Amount;
                row.GameSeconds = tx.GameSeconds;
                snapshot.Ledger.Add(row);
            }
            foreach (ActionInstanceId action in applied)
            {
                snapshot.Applied.Add((long)action);
            }
            snapshot.Applied.Sort();
            return snapshot;
        }

        // Save restore: replaces all three parts after validating every row,
        // so a forged bundle is refused instead of half-applied.
        public void Import(StateWriter writer, AccountsSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.Shared == null || snapshot.Ledger == null || snapshot.Applied == null)
            {
                throw new ArgumentException("Accounts bundle lists must exist", "snapshot");
            }
            Dictionary<HouseholdId, long> nextShared = new Dictionary<HouseholdId, long>();
            for (int i = 0; i < snapshot.Shared.Count; i++)
            {
                AccountsSnapshot.BalanceRow row = snapshot.Shared[i];
                if (row == null)
                {
                    throw new ArgumentException("Null shared row " + i.ToString(), "snapshot");
                }
                if (row.Amount < 0L)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Shared balance is never negative");
                }
                HouseholdId household = (HouseholdId)row.Household;
                if (nextShared.ContainsKey(household))
                {
                    throw new ArgumentException("Duplicate shared row for household "
                        + row.Household.ToString(), "snapshot");
                }
                nextShared.Add(household, row.Amount);
            }
            List<AccountTransaction> nextLedger = new List<AccountTransaction>();
            HashSet<long> seenActions = new HashSet<long>();
            for (int i = 0; i < snapshot.Ledger.Count; i++)
            {
                AccountsSnapshot.LedgerRow row = snapshot.Ledger[i];
                if (row == null || row.Source == null || row.Destination == null)
                {
                    throw new ArgumentException("Null ledger row " + i.ToString(), "snapshot");
                }
                if (seenActions.Contains(row.Action))
                {
                    throw new ArgumentException("Duplicate ledger row for action "
                        + row.Action.ToString(), "snapshot");
                }
                if (row.Amount <= 0L || row.Source.Kind == "expense")
                    throw new ArgumentException("Invalid ledger amount or source", "snapshot");
                AccountRef source = RequireRef(row.Source, i);
                AccountRef destination = RequireRef(row.Destination, i);
                if (source.IsPersonal) PersonalBalance(source.Resident);
                if (destination.IsPersonal) PersonalBalance(destination.Resident);
                seenActions.Add(row.Action);
                nextLedger.Add(new AccountTransaction((ActionInstanceId)row.Action,
                    RequireRef(row.Source, i), RequireRef(row.Destination, i),
                    row.Amount, row.GameSeconds));
            }
            HashSet<ActionInstanceId> nextApplied = new HashSet<ActionInstanceId>();
            for (int i = 0; i < snapshot.Applied.Count; i++)
            {
                nextApplied.Add((ActionInstanceId)snapshot.Applied[i]);
            }
            if (nextApplied.Count != seenActions.Count)
                throw new ArgumentException("Ledger and applied actions differ", "snapshot");
            foreach (long id in seenActions)
                if (!nextApplied.Contains((ActionInstanceId)id))
                    throw new ArgumentException("Ledger payment is not marked applied", "snapshot");
            shared.Clear();
            foreach (KeyValuePair<HouseholdId, long> pair in nextShared)
            {
                shared.Add(pair.Key, pair.Value);
            }
            ledger.Clear();
            ledger.AddRange(nextLedger);
            applied.Clear();
            foreach (ActionInstanceId action in nextApplied)
            {
                applied.Add(action);
            }
        }

        private static AccountsSnapshot.AccountRefData Describe(AccountRef account)
        {
            AccountsSnapshot.AccountRefData data = new AccountsSnapshot.AccountRefData();
            if (account.IsPersonal)
            {
                data.Kind = "personal";
                data.Id = (long)account.Resident;
                data.Name = string.Empty;
            }
            else if (account.IsShared)
            {
                data.Kind = "shared";
                data.Id = (long)account.Household;
                data.Name = string.Empty;
            }
            else
            {
                data.Kind = "expense";
                data.Id = 0L;
                data.Name = account.Destination ?? string.Empty;
            }
            return data;
        }

        private static AccountRef RequireRef(AccountsSnapshot.AccountRefData data, int row)
        {
            if (data.Kind == null)
            {
                throw new ArgumentException("Null account kind on ledger row " + row.ToString(), "snapshot");
            }
            if (data.Kind.Equals("personal"))
            {
                if (data.Id <= 0L)
                {
                    throw new ArgumentException("Personal ref without a resident on ledger row "
                        + row.ToString(), "snapshot");
                }
                return AccountRef.Personal((ResidentId)data.Id);
            }
            if (data.Kind.Equals("shared"))
            {
                if (data.Id <= 0L)
                {
                    throw new ArgumentException("Shared ref without a household on ledger row "
                        + row.ToString(), "snapshot");
                }
                return AccountRef.Shared((HouseholdId)data.Id);
            }
            if (data.Kind.Equals("expense"))
            {
                return AccountRef.Expense(data.Name ?? string.Empty);
            }
            throw new ArgumentException("Unknown account kind \"" + data.Kind + "\" on ledger row "
                + row.ToString(), "snapshot");
        }

        private static void SortBalances(List<AccountsSnapshot.BalanceRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                AccountsSnapshot.BalanceRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Household > key.Household)
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
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
