// SHARES@1: joint home ownership shares with departure settlement and exact
// integer sale proceeds (BP-ECO-03, BP-SEC-05). Consumes STATE@1 (WorldState,
// StateWriter) and ECONOMY@1 (Accounts shared balances, ledger). Plain C# 9,
// no engine dependency. Agreements list owner percents summing to 100; invalid
// writes are refused without replacing the last valid agreement. Departure
// buys out in one shared-accounts transaction (childcare days credit 20 each)
// or lists for seven game days; sales split proceeds by flooring each share
// then distributing remainders in descending fractional order, ties broken by
// ascending HouseholdId.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class OwnershipShare
    {
        public HouseholdId Household { get; private set; }
        public int Percent { get; private set; }
        public int ChildcareDays { get; private set; }

        public OwnershipShare(HouseholdId household, int percent, int childcareDays = 0)
        {
            Household = household;
            Percent = percent;
            ChildcareDays = childcareDays;
        }
    }

    public sealed class OwnershipAgreement
    {
        public HomeId Home { get; private set; }
        public long RecordedValue { get; private set; }
        public IReadOnlyList<OwnershipShare> Shares { get; private set; }

        public OwnershipAgreement(HomeId home, long recordedValue, IList<OwnershipShare> shares)
        {
            if (shares == null)
            {
                throw new ArgumentNullException("shares");
            }
            OwnershipShare[] copy = new OwnershipShare[shares.Count];
            for (int i = 0; i < shares.Count; i++)
            {
                copy[i] = shares[i];
            }
            Home = home;
            RecordedValue = recordedValue;
            Shares = copy;
        }
    }

    public sealed class OwnershipSettlement
    {
        public long Value { get; private set; }
        public long NoncashCredit { get; private set; }
        public long AmountDue { get; private set; }
        public bool BoughtOut { get; private set; }
        public bool Listed { get; private set; }
        public long DueGameSeconds { get; private set; }

        public OwnershipSettlement(long value, long noncashCredit, long amountDue,
            bool boughtOut, bool listed, long dueGameSeconds)
        {
            Value = value;
            NoncashCredit = noncashCredit;
            AmountDue = amountDue;
            BoughtOut = boughtOut;
            Listed = listed;
            DueGameSeconds = dueGameSeconds;
        }
    }

    // Save capture bundle: accepted-agreement history per home plus pending
    // listings. No action ids are stored (settlement transfers live in the
    // accounts ledger), so SAVE restores this section without a remap.
    public sealed class OwnershipSnapshot
    {
        public sealed class ShareRow
        {
            public long Household;
            public int Percent;
            public int ChildcareDays;
        }

        public sealed class AgreementRow
        {
            public long Value;
            public readonly List<ShareRow> Shares = new List<ShareRow>();
        }

        public sealed class HomeHistory
        {
            public long Home;
            public readonly List<AgreementRow> Agreements = new List<AgreementRow>();
        }

        public sealed class ListingRow
        {
            public long Home;
            public long Seller;
            public long Value;
            public long AmountDue;
            public long DueGameSeconds;
        }

        public readonly List<HomeHistory> History = new List<HomeHistory>();
        public readonly List<ListingRow> Listings = new List<ListingRow>();
    }

    public sealed class Ownership
    {
        private const long ChildcareCreditPerDay = 20L;
        private const long ListingDays = 7L;
        private const long DaySeconds = 86400L;

        private sealed class Listing
        {
            public HouseholdId Seller;
            public long Value;
            public long AmountDue;
            public long DueGameSeconds;
        }

        private readonly WorldState world;
        private readonly Accounts accounts;
        private readonly Dictionary<HomeId, List<OwnershipAgreement>> histories =
            new Dictionary<HomeId, List<OwnershipAgreement>>();
        private readonly Dictionary<HomeId, Listing> listings = new Dictionary<HomeId, Listing>();

        public Ownership(WorldState world, Accounts accounts)
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

        public CommandResult SetAgreement(StateWriter writer, HomeId home, long value,
            OwnershipShare[] shares)
        {
            CheckWriter(writer);
            if (shares == null || shares.Length == 0 || value < 0L)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            HashSet<HouseholdId> seen = new HashSet<HouseholdId>();
            int total = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                OwnershipShare share = shares[i];
                if (share == null || share.Percent <= 0 || share.Percent > 100
                    || share.ChildcareDays < 0)
                {
                    return CommandResult.Reject(RejectReason.InvalidParameters);
                }
                if (seen.Contains(share.Household))
                {
                    return CommandResult.Reject(RejectReason.InvalidParameters);
                }
                seen.Add(share.Household);
                total += share.Percent;
            }
            if (total != 100)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            OwnershipShare[] copy = new OwnershipShare[shares.Length];
            for (int i = 0; i < shares.Length; i++)
            {
                copy[i] = new OwnershipShare(shares[i].Household, shares[i].Percent,
                    shares[i].ChildcareDays);
            }
            List<OwnershipAgreement> history;
            if (!histories.TryGetValue(home, out history))
            {
                history = new List<OwnershipAgreement>();
                histories.Add(home, history);
            }
            history.Add(new OwnershipAgreement(home, value, copy));
            listings.Remove(home);
            return CommandResult.Accept();
        }

        public OwnershipAgreement GetAgreement(HomeId home)
        {
            List<OwnershipAgreement> history;
            if (histories.TryGetValue(home, out history) && history.Count > 0)
            {
                return history[history.Count - 1];
            }
            return null;
        }

        public IReadOnlyList<OwnershipAgreement> History(HomeId home)
        {
            List<OwnershipAgreement> history;
            if (histories.TryGetValue(home, out history))
            {
                return history.AsReadOnly();
            }
            return new OwnershipAgreement[0];
        }

        public OwnershipSettlement Depart(StateWriter writer, ActionInstanceId action,
            HomeId home, HouseholdId seller, HouseholdId buyer)
        {
            CheckWriter(writer);
            OwnershipAgreement current = GetAgreement(home);
            if (current == null || seller == buyer)
            {
                return new OwnershipSettlement(0L, 0L, 0L, false, false, 0L);
            }
            OwnershipShare sellerShare = null;
            for (int i = 0; i < current.Shares.Count; i++)
            {
                if (current.Shares[i].Household == seller)
                {
                    sellerShare = current.Shares[i];
                }
            }
            if (sellerShare == null)
            {
                return new OwnershipSettlement(0L, 0L, 0L, false, false, 0L);
            }
            long value = ShareFloor(current.RecordedValue, sellerShare.Percent);
            long credit = (long)sellerShare.ChildcareDays * ChildcareCreditPerDay;
            if (credit > value)
            {
                credit = value;
            }
            long due = value - credit;
            if (accounts.Balance(AccountRef.Shared(buyer)) < due)
            {
                long dueSeconds = world.GameSeconds + ListingDays * DaySeconds;
                Listing pending = new Listing();
                pending.Seller = seller;
                pending.Value = value;
                pending.AmountDue = due;
                pending.DueGameSeconds = dueSeconds;
                listings[home] = pending;
                return new OwnershipSettlement(value, credit, due, false, true, dueSeconds);
            }
            if (due > 0L)
            {
                CommandResult paid = accounts.Transfer(writer, action,
                    AccountRef.Shared(buyer), AccountRef.Shared(seller), due);
                if (!paid.Accepted)
                {
                    return new OwnershipSettlement(value, credit, due, false, false, 0L);
                }
            }
            ReplaceSeller(home, current, seller, sellerShare.Percent, buyer);
            listings.Remove(home);
            return new OwnershipSettlement(value, credit, due, true, false, 0L);
        }

        public CommandResult CompleteListing(StateWriter writer, ActionInstanceId action,
            HomeId home, HouseholdId buyer)
        {
            CheckWriter(writer);
            Listing listing;
            if (!listings.TryGetValue(home, out listing))
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            OwnershipAgreement current = GetAgreement(home);
            if (current == null || buyer == listing.Seller)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            int sellerPercent = 0;
            bool sellerPresent = false;
            for (int i = 0; i < current.Shares.Count; i++)
            {
                if (current.Shares[i].Household == listing.Seller)
                {
                    sellerPresent = true;
                    sellerPercent = current.Shares[i].Percent;
                }
            }
            if (!sellerPresent)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            // SDoc line 214: the seven-game-day wait is a hard gate. Refuse
            // early completion before touching any balance or agreement.
            if (world.GameSeconds < listing.DueGameSeconds)
            {
                return CommandResult.Reject(RejectReason.StageIneligible);
            }
            if (accounts.Balance(AccountRef.Shared(buyer)) < listing.AmountDue)
            {
                return CommandResult.Reject(RejectReason.InsufficientFunds);
            }
            if (listing.AmountDue > 0L)
            {
                CommandResult paid = accounts.Transfer(writer, action,
                    AccountRef.Shared(buyer), AccountRef.Shared(listing.Seller), listing.AmountDue);
                if (!paid.Accepted)
                {
                    return paid;
                }
            }
            ReplaceSeller(home, current, listing.Seller, sellerPercent, buyer);
            listings.Remove(home);
            return CommandResult.Accept();
        }

        // Proceeds are external funds: the first owner's shared account fronts
        // them, then one transfer per owner distributes the exact shares (the
        // funder's own share travels as a same-account entry, so every owner
        // has exactly one ledger row and the funder nets its share).
        public CommandResult Sell(StateWriter writer, ActionInstanceId action,
            HomeId home, long proceeds)
        {
            CheckWriter(writer);
            OwnershipAgreement current = GetAgreement(home);
            if (current == null || proceeds <= 0L)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            int count = current.Shares.Count;
            long[] amounts = new long[count];
            long[] remainders = new long[count];
            long floorSum = 0L;
            for (int i = 0; i < count; i++)
            {
                amounts[i] = ShareFloor(proceeds, current.Shares[i].Percent);
                remainders[i] = ShareRemainder(proceeds, current.Shares[i].Percent);
                floorSum += amounts[i];
            }
            int[] order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }
            for (int i = 1; i < count; i++)
            {
                int key = order[i];
                int j = i - 1;
                while (j >= 0 && RanksBefore(current, remainders, key, order[j]))
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }
            long left = proceeds - floorSum;
            for (long k = 0L; k < left; k++)
            {
                amounts[order[(int)k]]++;
            }
            HouseholdId funder = current.Shares[0].Household;
            long funderBalance = accounts.Balance(AccountRef.Shared(funder));
            if (funderBalance > long.MaxValue - proceeds)
            {
                return CommandResult.Reject(RejectReason.InvalidParameters);
            }
            accounts.SetSharedBalance(writer, funder, funderBalance + proceeds);
            for (int i = 0; i < count; i++)
            {
                if (amounts[i] <= 0L)
                {
                    continue;
                }
                ActionInstanceId entry = i == 0 ? action : world.AllocateAction(writer);
                CommandResult paid = accounts.Transfer(writer, entry,
                    AccountRef.Shared(funder), AccountRef.Shared(current.Shares[i].Household),
                    amounts[i]);
                if (!paid.Accepted)
                {
                    return paid;
                }
            }
            return CommandResult.Accept();
        }

        // Save capture: histories in stored order per home (homes ascending
        // for stable bytes; share order preserved so agreements read back
        // unchanged) plus pending listings by home.
        public OwnershipSnapshot Export()
        {
            OwnershipSnapshot snapshot = new OwnershipSnapshot();
            List<HomeId> homes = new List<HomeId>(histories.Keys);
            SortHomes(homes);
            for (int h = 0; h < homes.Count; h++)
            {
                OwnershipSnapshot.HomeHistory row = new OwnershipSnapshot.HomeHistory();
                row.Home = (long)homes[h];
                List<OwnershipAgreement> history = histories[homes[h]];
                for (int a = 0; a < history.Count; a++)
                {
                    OwnershipSnapshot.AgreementRow agreement = new OwnershipSnapshot.AgreementRow();
                    agreement.Value = history[a].RecordedValue;
                    for (int s = 0; s < history[a].Shares.Count; s++)
                    {
                        OwnershipSnapshot.ShareRow share = new OwnershipSnapshot.ShareRow();
                        share.Household = (long)history[a].Shares[s].Household;
                        share.Percent = history[a].Shares[s].Percent;
                        share.ChildcareDays = history[a].Shares[s].ChildcareDays;
                        agreement.Shares.Add(share);
                    }
                    row.Agreements.Add(agreement);
                }
                snapshot.History.Add(row);
            }
            List<HomeId> listed = new List<HomeId>(listings.Keys);
            SortHomes(listed);
            for (int i = 0; i < listed.Count; i++)
            {
                Listing pending = listings[listed[i]];
                OwnershipSnapshot.ListingRow row = new OwnershipSnapshot.ListingRow();
                row.Home = (long)listed[i];
                row.Seller = (long)pending.Seller;
                row.Value = pending.Value;
                row.AmountDue = pending.AmountDue;
                row.DueGameSeconds = pending.DueGameSeconds;
                snapshot.Listings.Add(row);
            }
            return snapshot;
        }

        // Save restore: replaces histories and listings after validating every
        // row, so a forged bundle is refused instead of half-applied.
        public void Import(StateWriter writer, OwnershipSnapshot snapshot)
        {
            CheckWriter(writer);
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (snapshot.History == null || snapshot.Listings == null)
            {
                throw new ArgumentException("Ownership bundle lists must exist", "snapshot");
            }
            Dictionary<HomeId, List<OwnershipAgreement>> nextHistories =
                new Dictionary<HomeId, List<OwnershipAgreement>>();
            for (int h = 0; h < snapshot.History.Count; h++)
            {
                OwnershipSnapshot.HomeHistory row = snapshot.History[h];
                if (row == null || row.Agreements == null)
                {
                    throw new ArgumentException("Null ownership history row " + h.ToString(), "snapshot");
                }
                HomeId home = (HomeId)row.Home;
                if (nextHistories.ContainsKey(home))
                {
                    throw new ArgumentException("Duplicate ownership history for home "
                        + row.Home.ToString(), "snapshot");
                }
                if (row.Agreements.Count == 0)
                {
                    throw new ArgumentException("Empty ownership history for home "
                        + row.Home.ToString(), "snapshot");
                }
                List<OwnershipAgreement> history = new List<OwnershipAgreement>();
                for (int a = 0; a < row.Agreements.Count; a++)
                {
                    OwnershipSnapshot.AgreementRow saved = row.Agreements[a];
                    if (saved == null || saved.Shares == null)
                    {
                        throw new ArgumentException("Null ownership agreement " + a.ToString(), "snapshot");
                    }
                    if (saved.Value < 0L)
                    {
                        throw new ArgumentOutOfRangeException("snapshot", "Recorded value is never negative");
                    }
                    if (saved.Shares.Count == 0)
                    {
                        throw new ArgumentException("Empty ownership shares", "snapshot");
                    }
                    HashSet<HouseholdId> seen = new HashSet<HouseholdId>();
                    int total = 0;
                    OwnershipShare[] shares = new OwnershipShare[saved.Shares.Count];
                    for (int s = 0; s < saved.Shares.Count; s++)
                    {
                        OwnershipSnapshot.ShareRow share = saved.Shares[s];
                        if (share == null)
                        {
                            throw new ArgumentException("Null ownership share", "snapshot");
                        }
                        if (share.Percent <= 0 || share.Percent > 100 || share.ChildcareDays < 0)
                        {
                            throw new ArgumentException("Invalid ownership share", "snapshot");
                        }
                        HouseholdId household = (HouseholdId)share.Household;
                        if (seen.Contains(household))
                        {
                            throw new ArgumentException("Duplicate ownership share", "snapshot");
                        }
                        seen.Add(household);
                        total += share.Percent;
                        shares[s] = new OwnershipShare(household, share.Percent, share.ChildcareDays);
                    }
                    if (total != 100)
                    {
                        throw new ArgumentException("Ownership shares do not total 100", "snapshot");
                    }
                    history.Add(new OwnershipAgreement(home, saved.Value, shares));
                }
                nextHistories.Add(home, history);
            }
            Dictionary<HomeId, Listing> nextListings = new Dictionary<HomeId, Listing>();
            for (int i = 0; i < snapshot.Listings.Count; i++)
            {
                OwnershipSnapshot.ListingRow row = snapshot.Listings[i];
                if (row == null)
                {
                    throw new ArgumentException("Null ownership listing " + i.ToString(), "snapshot");
                }
                if (row.Value < 0L || row.AmountDue < 0L || row.DueGameSeconds < 0L)
                {
                    throw new ArgumentOutOfRangeException("snapshot", "Ownership listing amounts are never negative");
                }
                if (row.AmountDue > row.Value)
                {
                    throw new ArgumentException("Ownership listing due exceeds value", "snapshot");
                }
                HomeId home = (HomeId)row.Home;
                if (nextListings.ContainsKey(home))
                {
                    throw new ArgumentException("Duplicate ownership listing for home "
                        + row.Home.ToString(), "snapshot");
                }
                List<OwnershipAgreement> history;
                if (!nextHistories.TryGetValue(home, out history))
                {
                    throw new ArgumentException("Ownership listing without an agreement", "snapshot");
                }
                OwnershipAgreement current = history[history.Count - 1];
                bool sellerPresent = false;
                for (int s = 0; s < current.Shares.Count; s++)
                {
                    if (current.Shares[s].Household == (HouseholdId)row.Seller)
                    {
                        sellerPresent = true;
                    }
                }
                if (!sellerPresent)
                {
                    throw new ArgumentException("Ownership listing seller is not an owner", "snapshot");
                }
                Listing pending = new Listing();
                pending.Seller = (HouseholdId)row.Seller;
                pending.Value = row.Value;
                pending.AmountDue = row.AmountDue;
                pending.DueGameSeconds = row.DueGameSeconds;
                nextListings.Add(home, pending);
            }
            histories.Clear();
            foreach (KeyValuePair<HomeId, List<OwnershipAgreement>> pair in nextHistories)
            {
                histories.Add(pair.Key, pair.Value);
            }
            listings.Clear();
            foreach (KeyValuePair<HomeId, Listing> pair in nextListings)
            {
                listings.Add(pair.Key, pair.Value);
            }
        }

        private void ReplaceSeller(HomeId home, OwnershipAgreement current, HouseholdId seller,
            int sellerPercent, HouseholdId buyer)
        {
            List<OwnershipShare> next = new List<OwnershipShare>();
            bool buyerFound = false;
            for (int i = 0; i < current.Shares.Count; i++)
            {
                OwnershipShare share = current.Shares[i];
                if (share.Household == seller)
                {
                    continue;
                }
                if (share.Household == buyer)
                {
                    next.Add(new OwnershipShare(buyer, share.Percent + sellerPercent,
                        share.ChildcareDays));
                    buyerFound = true;
                }
                else
                {
                    next.Add(new OwnershipShare(share.Household, share.Percent,
                        share.ChildcareDays));
                }
            }
            if (!buyerFound)
            {
                next.Add(new OwnershipShare(buyer, sellerPercent, 0));
            }
            histories[home].Add(new OwnershipAgreement(home, current.RecordedValue, next));
        }

        private static bool RanksBefore(OwnershipAgreement current, long[] remainders,
            int candidate, int incumbent)
        {
            if (remainders[candidate] != remainders[incumbent])
            {
                return remainders[candidate] > remainders[incumbent];
            }
            return (long)current.Shares[candidate].Household < (long)current.Shares[incumbent].Household;
        }

        // Overflow-safe percent split: total is at most long.MaxValue and
        // percent at most 100, so the quotient/rest form never overflows.
        private static long ShareFloor(long total, int percent)
        {
            return (total / 100L) * (long)percent + ((total % 100L) * (long)percent) / 100L;
        }

        private static long ShareRemainder(long total, int percent)
        {
            return ((total % 100L) * (long)percent) % 100L;
        }

        private static void SortHomes(List<HomeId> homes)
        {
            for (int i = 1; i < homes.Count; i++)
            {
                HomeId key = homes[i];
                int j = i - 1;
                while (j >= 0 && (long)homes[j] > (long)key)
                {
                    homes[j + 1] = homes[j];
                    j--;
                }
                homes[j + 1] = key;
            }
        }

        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
