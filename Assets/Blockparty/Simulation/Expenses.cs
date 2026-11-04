// BILLS@1: weekly utilities/rent posting and ordered unpaid retry (BP-ECO-02).
// Consumes STATE@1 (WorldState, StateWriter), CLOCK@1 (game seconds) and
// ECONOMY@1 (Accounts transfers, ledger) through their public seams, and SAVE
// persists it through the Export/Import seam below. Plain C# 9, no engine
// reference. Posting is idempotent per home per week; retries never invent
// funds, pool unconfigured accounts, or move residents (BP-WORLD-07).
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class ExpenseDue
    {
        public long DueId { get; private set; }
        public HomeId Home { get; private set; }
        public string Kind { get; private set; }
        public long Amount { get; private set; }
        public long DueGameSeconds { get; private set; }
        public long Paid { get; private set; }
        public long Shortfall { get; private set; }
        public string State { get; private set; }

        internal bool Divisible { get; private set; }

        internal ExpenseDue(long dueId, HomeId home, string kind, long amount,
            long dueGameSeconds, bool divisible)
        {
            DueId = dueId;
            Home = home;
            Kind = kind;
            Amount = amount;
            DueGameSeconds = dueGameSeconds;
            Divisible = divisible;
            Paid = 0L;
            Shortfall = amount;
            State = "DueUnpaid";
        }

        internal void Settle(long paid, long shortfall)
        {
            Paid = paid;
            Shortfall = shortfall;
            State = paid >= Amount ? "Paid" : "DueUnpaid";
        }
    }

    public sealed class Expenses
    {
        private const long WeekSeconds = 7L * 86400L;
        private const long BaseUtilities = 30L;
        private const long UtilitiesPerResident = 5L;
        private const long RentAmount = 75L;
        private const string UtilitiesKind = "Utilities";
        private const string HousingKind = "HousingCharge";

        private sealed class HomeConfig
        {
            public bool Rented;
            public AccountRef[] Funding = new AccountRef[0];
            public long NextWeek = 1L;
        }

        // Save capture bundle: home configs plus every retained due. SAVE
        // writes no action ids here because payments live in the accounts
        // ledger, so no saved-to-fresh remap applies.
        public sealed class ExpensesSnapshot
        {
            public sealed class HomeRow
            {
                public long Home;
                public bool Rented;
                public long NextWeek;
                public readonly List<AccountsSnapshot.AccountRefData> Funding =
                    new List<AccountsSnapshot.AccountRefData>();
            }

            public sealed class DueRow
            {
                public long DueId;
                public long Home;
                public string Kind;
                public long Amount;
                public long DueGameSeconds;
                public long Paid;
                public long Shortfall;
                public bool Divisible;
            }

            public long NextDueId;
            public readonly List<HomeRow> Homes = new List<HomeRow>();
            public readonly List<DueRow> Dues = new List<DueRow>();
        }

        private readonly WorldState world;
        private readonly Accounts accounts;
        private readonly Dictionary<HomeId, HomeConfig> homes = new Dictionary<HomeId, HomeConfig>();
        private readonly List<ExpenseDue> dues = new List<ExpenseDue>();
        private long nextDueId = 1L;

        public Expenses(WorldState world, Accounts accounts)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }
            if (accounts == null)
            {
                throw new ArgumentNullException("accounts");
            }
            this.world = world;
            this.accounts = accounts;
        }

        public IReadOnlyList<ExpenseDue> Dues { get { return dues; } }

        public void ConfigureHome(StateWriter writer, HomeId home, bool rented,
            AccountRef[] funding)
        {
            CheckWriter(writer);
            if (funding == null)
            {
                throw new ArgumentNullException("funding");
            }
            AccountRef[] copy = new AccountRef[funding.Length];
            for (int i = 0; i < funding.Length; i++)
            {
                if (!funding[i].IsPersonal && !funding[i].IsShared)
                {
                    throw new ArgumentException("Funding must be a personal or shared account",
                        "funding");
                }
                copy[i] = funding[i];
            }
            HomeConfig config;
            if (!homes.TryGetValue(home, out config))
            {
                config = new HomeConfig();
                config.NextWeek = CurrentWeek() + 1L;
                homes.Add(home, config);
            }
            config.Rented = rented;
            config.Funding = copy;
        }

        public ExpenseDue AddDue(StateWriter writer, HomeId home, string kind, long amount,
            long dueGameSeconds, bool divisible)
        {
            CheckWriter(writer);
            if (kind == null || kind.Length == 0)
            {
                throw new ArgumentException("Expense kind must be named", "kind");
            }
            if (amount <= 0L)
            {
                throw new ArgumentOutOfRangeException("amount", "Expense amount must be positive");
            }
            if (dueGameSeconds < 0L)
            {
                throw new ArgumentOutOfRangeException("dueGameSeconds", "Due date is never negative");
            }
            ExpenseDue due = new ExpenseDue(nextDueId, home, kind, amount, dueGameSeconds,
                divisible);
            nextDueId++;
            dues.Add(due);
            return due;
        }

        // Posts each completed week once per home, then retries every unpaid
        // due in owner-approved order: due date, Utilities before
        // HousingCharge, then ascending DueId. Repeating a tick with no clock
        // or funding change moves no money.
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            PostDueWeeks();
            SettleUnpaid(writer);
        }

        // Save capture: homes sorted by id for stable bytes; dues keep DueId
        // order so history reads back unchanged.
        public ExpensesSnapshot Export()
        {
            ExpensesSnapshot snapshot = new ExpensesSnapshot();
            snapshot.NextDueId = nextDueId;
            List<HomeId> ids = new List<HomeId>(homes.Keys);
            SortIds(ids);
            for (int i = 0; i < ids.Count; i++)
            {
                HomeConfig config = homes[ids[i]];
                ExpensesSnapshot.HomeRow row = new ExpensesSnapshot.HomeRow();
                row.Home = (long)ids[i];
                row.Rented = config.Rented;
                row.NextWeek = config.NextWeek;
                for (int j = 0; j < config.Funding.Length; j++)
                {
                    row.Funding.Add(Describe(config.Funding[j]));
                }
                snapshot.Homes.Add(row);
            }
            for (int i = 0; i < dues.Count; i++)
            {
                ExpenseDue due = dues[i];
                ExpensesSnapshot.DueRow row = new ExpensesSnapshot.DueRow();
                row.DueId = due.DueId;
                row.Home = (long)due.Home;
                row.Kind = due.Kind;
                row.Amount = due.Amount;
                row.DueGameSeconds = due.DueGameSeconds;
                row.Paid = due.Paid;
                row.Shortfall = due.Shortfall;
                row.Divisible = due.Divisible;
                snapshot.Dues.Add(row);
            }
            return snapshot;
        }

        // Save restore: replaces configs and dues after validating every row,
        // so a forged bundle is refused instead of half-applied.
        public void Import(StateWriter writer, ExpensesSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.Homes == null || snapshot.Dues == null)
            {
                throw new ArgumentException("Expenses bundle lists must exist", "snapshot");
            }
            if (snapshot.NextDueId < 1L)
            {
                throw new ArgumentException("Next due id must be positive", "snapshot");
            }
            Dictionary<HomeId, HomeConfig> nextHomes = new Dictionary<HomeId, HomeConfig>();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                ExpensesSnapshot.HomeRow row = snapshot.Homes[i];
                if (row == null || row.Funding == null)
                {
                    throw new ArgumentException("Null home row " + i.ToString(), "snapshot");
                }
                if (row.NextWeek < 1L)
                {
                    throw new ArgumentException("Next week must be positive", "snapshot");
                }
                HomeId home = (HomeId)row.Home;
                if (nextHomes.ContainsKey(home))
                {
                    throw new ArgumentException("Duplicate home row for home "
                        + row.Home.ToString(), "snapshot");
                }
                AccountRef[] funding = new AccountRef[row.Funding.Count];
                for (int j = 0; j < row.Funding.Count; j++)
                {
                    funding[j] = RequireRef(row.Funding[j], i);
                }
                HomeConfig config = new HomeConfig();
                config.Rented = row.Rented;
                config.Funding = funding;
                config.NextWeek = row.NextWeek;
                nextHomes.Add(home, config);
            }
            List<ExpenseDue> nextDues = new List<ExpenseDue>();
            HashSet<long> seen = new HashSet<long>();
            for (int i = 0; i < snapshot.Dues.Count; i++)
            {
                ExpensesSnapshot.DueRow row = snapshot.Dues[i];
                if (row == null || row.Kind == null || row.Kind.Length == 0)
                {
                    throw new ArgumentException("Null due row " + i.ToString(), "snapshot");
                }
                if (row.DueId < 1L || row.DueId >= snapshot.NextDueId || seen.Contains(row.DueId))
                {
                    throw new ArgumentException("Due row " + i.ToString()
                        + " has a duplicate or out-of-range id", "snapshot");
                }
                if (row.Amount <= 0L || row.DueGameSeconds < 0L || row.Paid < 0L
                    || row.Paid > row.Amount || row.Shortfall < 0L)
                {
                    throw new ArgumentException("Due row " + i.ToString() + " is invalid",
                        "snapshot");
                }
                seen.Add(row.DueId);
                ExpenseDue due = new ExpenseDue(row.DueId, (HomeId)row.Home, row.Kind,
                    row.Amount, row.DueGameSeconds, row.Divisible);
                due.Settle(row.Paid, row.Shortfall);
                nextDues.Add(due);
            }
            homes.Clear();
            foreach (KeyValuePair<HomeId, HomeConfig> pair in nextHomes)
            {
                homes.Add(pair.Key, pair.Value);
            }
            dues.Clear();
            dues.AddRange(nextDues);
            nextDueId = snapshot.NextDueId;
        }

        private long CurrentWeek()
        {
            return world.GameSeconds / WeekSeconds;
        }

        private void PostDueWeeks()
        {
            long currentWeek = CurrentWeek();
            if (currentWeek < 1L)
            {
                return;
            }
            List<HomeId> ids = new List<HomeId>(homes.Keys);
            SortIds(ids);
            for (int i = 0; i < ids.Count; i++)
            {
                HomeConfig config = homes[ids[i]];
                if (config.NextWeek > currentWeek)
                {
                    continue;
                }
                for (long week = config.NextWeek; week <= currentWeek; week++)
                {
                    int occupants = Occupants(ids[i]);
                    if (occupants > 0)
                    {
                        long date = week * WeekSeconds;
                        Post(ids[i], UtilitiesKind,
                            BaseUtilities + UtilitiesPerResident * occupants, date);
                        if (config.Rented)
                        {
                            Post(ids[i], HousingKind, RentAmount, date);
                        }
                    }
                }
                config.NextWeek = currentWeek + 1L;
            }
        }

        private void Post(HomeId home, string kind, long amount, long date)
        {
            ExpenseDue due = new ExpenseDue(nextDueId, home, kind, amount, date, false);
            nextDueId++;
            dues.Add(due);
        }

        private int Occupants(HomeId home)
        {
            int count = 0;
            ResidentRecord[] residents = world.ResidentsSnapshot();
            for (int i = 0; i < residents.Length; i++)
            {
                if (residents[i].Home == home)
                {
                    count++;
                }
            }
            return count;
        }

        private void SettleUnpaid(StateWriter writer)
        {
            List<ExpenseDue> unpaid = new List<ExpenseDue>();
            for (int i = 0; i < dues.Count; i++)
            {
                if (dues[i].Paid < dues[i].Amount)
                {
                    unpaid.Add(dues[i]);
                }
            }
            SortUnpaid(unpaid);
            for (int i = 0; i < unpaid.Count; i++)
            {
                SettleOne(writer, unpaid[i]);
            }
        }

        private void SettleOne(StateWriter writer, ExpenseDue due)
        {
            HomeConfig config;
            if (!homes.TryGetValue(due.Home, out config))
            {
                return;
            }
            long available = 0L;
            for (int i = 0; i < config.Funding.Length; i++)
            {
                available += accounts.Balance(config.Funding[i]);
            }
            long remaining = due.Amount - due.Paid;
            long pay = 0L;
            if (available >= remaining)
            {
                pay = remaining;
            }
            else if (due.Divisible && available > 0L)
            {
                pay = available;
            }
            if (pay > 0L)
            {
                long left = pay;
                for (int i = 0; i < config.Funding.Length && left > 0L; i++)
                {
                    long funds = accounts.Balance(config.Funding[i]);
                    long take = funds < left ? funds : left;
                    if (take <= 0L)
                    {
                        continue;
                    }
                    CommandResult moved = accounts.Transfer(writer, world.AllocateAction(writer),
                        config.Funding[i], AccountRef.Expense(due.Kind), take);
                    if (moved.Accepted)
                    {
                        left -= take;
                    }
                }
                pay -= left;
            }
            long leftover = available - pay;
            if (leftover < 0L)
            {
                leftover = 0L;
            }
            long shortfall = due.Amount - due.Paid - pay - leftover;
            if (shortfall < 0L)
            {
                shortfall = 0L;
            }
            due.Settle(due.Paid + pay, shortfall);
        }

        private static int KindRank(string kind)
        {
            if (kind == UtilitiesKind)
            {
                return 0;
            }
            if (kind == HousingKind)
            {
                return 1;
            }
            return 2;
        }

        private static void SortUnpaid(List<ExpenseDue> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                ExpenseDue key = rows[i];
                int j = i - 1;
                while (j >= 0 && Before(key, rows[j]))
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static bool Before(ExpenseDue a, ExpenseDue b)
        {
            if (a.DueGameSeconds != b.DueGameSeconds)
            {
                return a.DueGameSeconds < b.DueGameSeconds;
            }
            if (KindRank(a.Kind) != KindRank(b.Kind))
            {
                return KindRank(a.Kind) < KindRank(b.Kind);
            }
            return a.DueId < b.DueId;
        }

        private static void SortIds(List<HomeId> ids)
        {
            for (int i = 1; i < ids.Count; i++)
            {
                HomeId key = ids[i];
                int j = i - 1;
                while (j >= 0 && (long)ids[j] > (long)key)
                {
                    ids[j + 1] = ids[j];
                    j--;
                }
                ids[j + 1] = key;
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
            if (data == null || data.Kind == null)
            {
                throw new ArgumentException("Null funding ref on home row " + row.ToString(),
                    "snapshot");
            }
            if (data.Kind.Equals("personal"))
            {
                if (data.Id <= 0L)
                {
                    throw new ArgumentException("Personal funding without a resident on home row "
                        + row.ToString(), "snapshot");
                }
                return AccountRef.Personal((ResidentId)data.Id);
            }
            if (data.Kind.Equals("shared"))
            {
                if (data.Id <= 0L)
                {
                    throw new ArgumentException("Shared funding without a household on home row "
                        + row.ToString(), "snapshot");
                }
                return AccountRef.Shared((HouseholdId)data.Id);
            }
            throw new ArgumentException("Unknown funding kind \"" + data.Kind + "\" on home row "
                + row.ToString(), "snapshot");
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
