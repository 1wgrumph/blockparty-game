// ESTATE@1: permanent-death estate and dependent-care settlement.
// Consumes STATE@1 (WorldState resident/household/estate/care state), AGING@1
// death events, COMMAND@1 (last valid will), ECONOMY@1 (Accounts), SHARES@1
// (Ownership agreements), HOUSING@1 (households, capacity, furniture) and the
// one Clock. Plain C# 9, no UnityEngine dependency.
// A death settles exactly once, keyed by the death EventId in the estate
// ledger. Money and the deceased household's home share follow a valid will,
// or the owner-selected fallback: spouse, living children, nearest living
// relative, then town fund/title. Only the deceased share moves; survivor
// shares and balances are never rewritten by a buyout. Dependents keep a
// surviving caregiver, move to the designated or nearest relative household
// with room, or receive a town facility place and persistent NPC guardian.
// Fast Enter Play Mode: no static mutable state, no lambdas, explicit numeric
// conversions, no AppDomain/Assembly.Location/BinaryFormatter and no
// String.GetHashCode.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.Simulation
{
    public sealed class EstateCare
    {
        // One pending money/title share. Household is set for a resident
        // household recipient; Charity and Town are the two external sinks.
        private sealed class Allocation
        {
            public string Key = string.Empty;
            public HouseholdId? Household;
            public bool Charity;
            public bool Town;
            public int Percent;
        }

        private sealed class MoneyRow
        {
            public Allocation Share;
            public long Floor;
            public long Remainder;
            public long Amount;
        }

        private const string DeathKind = "Death";
        private const string CareTransferKind = "CareTransfer";
        private const string TownHouseholdName = "Town";
        private const string TownOwner = "Town";

        private readonly WorldState world;
        private readonly Clock clock;
        private readonly CommandArbitrator arbitrator;
        private readonly Accounts accounts;
        private readonly Ownership ownership;
        private readonly Housing housing;
        private readonly ActivityExecutor executor;
        private readonly AppearanceStore appearances;

        public string Message { get; private set; }

        public EstateCare(GameStack stack)
        {
            if (stack == null)
            {
                throw new ArgumentNullException("stack");
            }
            world = stack.World;
            clock = stack.Clock;
            arbitrator = stack.Arbitrator;
            accounts = stack.Accounts;
            ownership = stack.Ownership;
            housing = stack.Housing;
            executor = stack.Executor;
            appearances = stack.Appearances;
            if (world == null || clock == null || arbitrator == null || accounts == null
                || ownership == null || housing == null)
            {
                throw new ArgumentException("EstateCare needs world, clock, arbitrator, accounts, ownership and housing", "stack");
            }
            Message = string.Empty;
        }

        // Durable town title inspection: the owning share is the town
        // household's entry in the existing ownership agreement, not a second
        // ownership store.
        public int TownHomeShare(HomeId home)
        {
            OwnershipAgreement agreement = ownership.GetAgreement(home);
            if (agreement == null)
            {
                return 0;
            }
            HouseholdId town = FindTownHousehold();
            if ((long)town == 0L)
            {
                return 0;
            }
            for (int i = 0; i < agreement.Shares.Count; i++)
            {
                if (agreement.Shares[i].Household == town)
                {
                    return agreement.Shares[i].Percent;
                }
            }
            return 0;
        }

        // A town-owned item is the durable Expense("Town") owner recorded by
        // the fallback, not a fabricated cash transaction.
        public bool TownOwns(ObjectId item)
        {
            HousingItem found = housing.GetItem(item);
            return found != null && found.Owner.Equals(AccountRef.Expense(TownOwner));
        }

        // Call at the shared clock boundary. Pause never settles; every death
        // event is settled once and a reload sees its estate-ledger marker.
        public void Tick(StateWriter writer)
        {
            if (clock.Paused)
            {
                return;
            }
            List<WorldEvent> deaths = new List<WorldEvent>();
            IReadOnlyList<WorldEvent> events = world.Events;
            for (int i = 0; i < events.Count; i++)
            {
                WorldEvent entry = events[i];
                if (entry.Actor.HasValue && string.Equals(entry.Kind, DeathKind, StringComparison.Ordinal))
                {
                    deaths.Add(entry);
                }
            }
            for (int i = 0; i < deaths.Count; i++)
            {
                if (world.IsEstateSettled(deaths[i].Event))
                {
                    continue;
                }
                if (!world.SettleEstate(writer, deaths[i].Event, "estate", "settled"))
                {
                    continue;
                }
                SettleDeath(writer, deaths[i]);
            }
        }

        private void SettleDeath(StateWriter writer, WorldEvent death)
        {
            ResidentId deceased = death.Actor.Value;
            ResidentRecord record;
            if (!world.TryGetResident(deceased, out record))
            {
                return;
            }
            List<string> notices = new List<string>();
            WillRecord will;
            bool hasWill = arbitrator.TryGetLastWill(deceased, out will) && will != null;
            if (!hasWill)
            {
                will = null;
            }
            List<Allocation> allocations = hasWill
                ? WillAllocations(will, record, notices)
                : FallbackAllocations(record, 100, notices);
            if (record.Money > 0L)
            {
                PayMoney(writer, death.Event, deceased, allocations, record.Money, notices);
            }
            if (!hasWill)
            {
                Allocation primary = PrimaryHouseholdAllocation(allocations);
                TransferHome(writer, death.Event, record, allocations, notices);
                if (primary != null)
                {
                    TransferItems(writer, death.Event, deceased, primary, notices);
                }
            }
            SettleCare(writer, death, record, will, notices);
            Message = notices.Count == 0
                ? "Estate settled for " + record.DisplayName
                : string.Join(" ", notices.ToArray());
        }

        // ---- will shares and fallback order ----

        private List<Allocation> WillAllocations(WillRecord will, ResidentRecord deceased, List<string> notices)
        {
            Dictionary<string, Allocation> combined = new Dictionary<string, Allocation>();
            List<string> order = new List<string>();
            for (int i = 0; i < will.MoneyShares.Count; i++)
            {
                string recipient = will.MoneyShares[i].Recipient;
                int percent = will.MoneyShares[i].Percent;
                if (percent <= 0)
                {
                    continue;
                }
                HouseholdId household;
                Allocation share;
                if (IsCharity(recipient))
                {
                    share = CharityAllocation();
                }
                else if (TryParseHousehold(recipient, out household))
                {
                    share = HouseholdAllocation(household);
                }
                else
                {
                    share = new Allocation();
                    share.Key = "missing:" + (recipient ?? string.Empty);
                }
                Merge(combined, order, share, percent);
            }
            List<Allocation> result = new List<Allocation>();
            for (int i = 0; i < order.Count; i++)
            {
                Allocation share = combined[order[i]];
                if (share.Key.StartsWith("missing:", StringComparison.Ordinal))
                {
                    // A vanished recipient falls back only for its own portion.
                    List<Allocation> fallback = FallbackAllocations(deceased, share.Percent, notices);
                    for (int f = 0; f < fallback.Count; f++)
                    {
                        MergeList(result, fallback[f]);
                    }
                }
                else
                {
                    MergeList(result, share);
                }
            }
            return result;
        }

        private List<Allocation> FallbackAllocations(ResidentRecord deceased, int percent, List<string> notices)
        {
            List<Allocation> result = new List<Allocation>();
            if (percent <= 0)
            {
                return result;
            }
            ResidentRecord spouse = FindSpouse(deceased);
            if (spouse != null && world.HasHousehold(spouse.Household))
            {
                notices.Add("Spouse " + ((long)spouse.Id).ToString() + " inherits via fallback.");
                result.Add(HouseholdAllocation(spouse.Household, percent));
                return result;
            }
            List<ResidentRecord> children = FindChildren(deceased);
            if (children.Count > 0)
            {
                notices.Add("Children inherit via fallback.");
                return SplitPeoplePercent(children, percent);
            }
            ResidentRecord relative = FindNearestRelative(deceased);
            if (relative != null && world.HasHousehold(relative.Household))
            {
                notices.Add("Nearest relative " + ((long)relative.Id).ToString() + " inherits via fallback.");
                result.Add(HouseholdAllocation(relative.Household, percent));
                return result;
            }
            notices.Add("Town fund inherits via fallback.");
            result.Add(TownAllocation(percent));
            return result;
        }

        private ResidentRecord FindSpouse(ResidentRecord deceased)
        {
            ResidentRecord best = null;
            for (int i = 0; i < deceased.Family.Count; i++)
            {
                FamilyLink link = deceased.Family[i];
                if (!IsKind(link.Kind, "Spouse")) continue;
                ResidentRecord other;
                if (world.TryGetResident(link.Other, out other) && other.IsAlive
                    && (best == null || (long)other.Id < (long)best.Id))
                {
                    best = other;
                }
            }
            if (best != null)
            {
                return best;
            }
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].IsAlive || all[i].Id == deceased.Id) continue;
                for (int f = 0; f < all[i].Family.Count; f++)
                {
                    if (all[i].Family[f].Other == deceased.Id && IsKind(all[i].Family[f].Kind, "Spouse")
                        && (best == null || (long)all[i].Id < (long)best.Id))
                    {
                        best = all[i];
                    }
                }
            }
            return best;
        }

        private List<ResidentRecord> FindChildren(ResidentRecord deceased)
        {
            List<ResidentRecord> found = new List<ResidentRecord>();
            for (int i = 0; i < deceased.Family.Count; i++)
            {
                if (!IsKind(deceased.Family[i].Kind, "Child")) continue;
                ResidentRecord child;
                if (world.TryGetResident(deceased.Family[i].Other, out child) && child.IsAlive)
                {
                    AddPerson(found, child);
                }
            }
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].IsAlive || all[i].Id == deceased.Id) continue;
                for (int f = 0; f < all[i].Family.Count; f++)
                {
                    if (all[i].Family[f].Other == deceased.Id && IsKind(all[i].Family[f].Kind, "Parent"))
                    {
                        AddPerson(found, all[i]);
                    }
                }
            }
            SortPeople(found);
            return found;
        }

        private ResidentRecord FindNearestRelative(ResidentRecord deceased)
        {
            Dictionary<ResidentId, int> distance = Distances(deceased.Id);
            ResidentRecord best = null;
            int bestDistance = int.MaxValue;
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                ResidentRecord candidate = all[i];
                if (!candidate.IsAlive || candidate.Id == deceased.Id) continue;
                int d;
                if (!distance.TryGetValue(candidate.Id, out d) || d <= 0) continue;
                if (d < bestDistance || (d == bestDistance && best != null && (long)candidate.Id < (long)best.Id))
                {
                    best = candidate;
                    bestDistance = d;
                }
            }
            return best;
        }

        // ---- money ----

        private void PayMoney(StateWriter writer, EventId death, ResidentId deceased,
            List<Allocation> allocations, long amount, List<string> notices)
        {
            if (allocations.Count == 0 || amount <= 0L)
            {
                return;
            }
            List<MoneyRow> rows = new List<MoneyRow>();
            long floors = 0L;
            for (int i = 0; i < allocations.Count; i++)
            {
                MoneyRow row = new MoneyRow();
                row.Share = allocations[i];
                row.Floor = ShareFloor(amount, allocations[i].Percent);
                row.Remainder = ShareRemainder(amount, allocations[i].Percent);
                row.Amount = row.Floor;
                floors += row.Floor;
                rows.Add(row);
            }
            SortMoneyRows(rows);
            long left = amount - floors;
            if (left > (long)rows.Count)
            {
                throw new InvalidOperationException("Estate shares exceed the estate total");
            }
            for (long i = 0L; i < left; i++)
            {
                rows[(int)i].Amount = rows[(int)i].Floor + 1L;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Amount <= 0L)
                {
                    continue;
                }
                string label = RecipientLabel(rows[i].Share);
                if (!world.SettleEstate(writer, death, "money:" + rows[i].Share.Key, label))
                {
                    continue;
                }
                CommandResult paid;
                if (rows[i].Share.Charity)
                {
                    paid = accounts.Transfer(writer, world.AllocateAction(writer),
                        AccountRef.Personal(deceased), AccountRef.Expense("Charity"), rows[i].Amount);
                }
                else if (rows[i].Share.Town)
                {
                    paid = accounts.Transfer(writer, world.AllocateAction(writer),
                        AccountRef.Personal(deceased), AccountRef.Expense("TownFund"), rows[i].Amount);
                    if (paid.Accepted)
                    {
                        world.SetTownFund(writer, world.TownFund + rows[i].Amount);
                    }
                }
                else
                {
                    paid = accounts.Transfer(writer, world.AllocateAction(writer),
                        AccountRef.Personal(deceased), AccountRef.Shared(rows[i].Share.Household.Value), rows[i].Amount);
                }
                if (!paid.Accepted)
                {
                    throw new InvalidOperationException("Estate payment refused: " + paid.Reason.ToString());
                }
                notices.Add("Money " + rows[i].Amount.ToString() + " to " + label + ".");
            }
        }

        private Allocation PrimaryHouseholdAllocation(List<Allocation> allocations)
        {
            for (int i = 0; i < allocations.Count; i++)
            {
                if (allocations[i].Household.HasValue)
                {
                    return allocations[i];
                }
            }
            for (int i = 0; i < allocations.Count; i++)
            {
                if (allocations[i].Town)
                {
                    return allocations[i];
                }
            }
            return null;
        }

        private void SortMoneyRows(List<MoneyRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                MoneyRow key = rows[i];
                int j = i - 1;
                while (j >= 0 && MoneyRowRanksBefore(key, rows[j]))
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = key;
            }
        }

        private static bool MoneyRowRanksBefore(MoneyRow candidate, MoneyRow incumbent)
        {
            if (candidate.Remainder != incumbent.Remainder)
            {
                return candidate.Remainder > incumbent.Remainder;
            }
            int candidateRank = RecipientRank(candidate.Share);
            int incumbentRank = RecipientRank(incumbent.Share);
            if (candidateRank != incumbentRank)
            {
                return candidateRank < incumbentRank;
            }
            if (candidate.Share.Household.HasValue && incumbent.Share.Household.HasValue)
            {
                return (long)candidate.Share.Household.Value < (long)incumbent.Share.Household.Value;
            }
            return string.CompareOrdinal(candidate.Share.Key, incumbent.Share.Key) < 0;
        }

        // Households rank before the town sink, and Charity is always last.
        private static int RecipientRank(Allocation share)
        {
            if (share.Charity)
            {
                return 2;
            }
            if (share.Town)
            {
                return 1;
            }
            return 0;
        }

        // ---- home title and heirlooms ----

        private void TransferHome(StateWriter writer, EventId death, ResidentRecord deceased,
            List<Allocation> allocations, List<string> notices)
        {
            List<HomeId> homes = new List<HomeId>();
            Dictionary<HomeId, OwnershipAgreement> byHome = new Dictionary<HomeId, OwnershipAgreement>();
            // Ownership has no public home enumeration, so inspect the homes
            // the world knows and keep those with an agreement.
            WorldSnapshot snapshot = world.ExportSnapshot();
            for (int i = 0; i < snapshot.Homes.Count; i++)
            {
                HomeId home = snapshot.Homes[i].Id;
                OwnershipAgreement agreement = ownership.GetAgreement(home);
                if (agreement != null)
                {
                    homes.Add(home);
                    byHome.Add(home, agreement);
                }
            }
            SortHomes(homes);
            for (int h = 0; h < homes.Count; h++)
            {
                HomeId home = homes[h];
                OwnershipAgreement current = byHome[home];
                int deceasedPercent = 0;
                for (int i = 0; i < current.Shares.Count; i++)
                {
                    if (current.Shares[i].Household == deceased.Household)
                    {
                        deceasedPercent = current.Shares[i].Percent;
                    }
                }
                if (deceasedPercent <= 0)
                {
                    continue;
                }
                List<Allocation> homeShares = SplitPercent(allocations, deceasedPercent);
                if (homeShares.Count == 0)
                {
                    continue;
                }
                bool town = false;
                int shareTotal = 0;
                for (int i = 0; i < homeShares.Count; i++)
                {
                    if (homeShares[i].Town) town = true;
                    shareTotal += homeShares[i].Percent;
                }
                if (shareTotal != deceasedPercent)
                {
                    continue;
                }
                List<OwnershipShare> next = new List<OwnershipShare>();
                for (int i = 0; i < current.Shares.Count; i++)
                {
                    if (current.Shares[i].Household != deceased.Household)
                    {
                        next.Add(new OwnershipShare(current.Shares[i].Household,
                            current.Shares[i].Percent, current.Shares[i].ChildcareDays));
                    }
                }
                string label = string.Empty;
                if (town)
                {
                    HouseholdId townHousehold = TownHousehold(writer, home);
                    AddOwnershipShare(next, townHousehold, deceasedPercent);
                    label = TownOwner;
                }
                else
                {
                    for (int i = 0; i < homeShares.Count; i++)
                    {
                        if (!homeShares[i].Household.HasValue || homeShares[i].Percent <= 0)
                        {
                            continue;
                        }
                        AddOwnershipShare(next, homeShares[i].Household.Value, homeShares[i].Percent);
                        label = ((long)homeShares[i].Household.Value).ToString();
                    }
                }
                int total = 0;
                for (int i = 0; i < next.Count; i++) total += next[i].Percent;
                if (total != 100)
                {
                    continue;
                }
                if (!ownership.SetAgreement(writer, home, current.RecordedValue, next.ToArray()).Accepted)
                {
                    continue;
                }
                world.SettleEstate(writer, death, "home:" + ((long)home).ToString(),
                    label.Length == 0 ? "Town" : label);
                notices.Add("Home title " + ((long)home).ToString() + " to " + (label.Length == 0 ? "Town" : label) + ".");
            }
        }

        private void TransferItems(StateWriter writer, EventId death, ResidentId deceased,
            Allocation primary, List<string> notices)
        {
            AccountRef target = primary.Town
                ? AccountRef.Expense(TownOwner)
                : AccountRef.Shared(primary.Household.Value);
            Housing.HousingSnapshot snapshot = housing.Export();
            List<ObjectId> changedIds = new List<ObjectId>();
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                Housing.HousingSnapshot.ItemRow row = snapshot.Items[i];
                if (row.Owner != null && row.Owner.Kind == "personal" && row.Owner.Id == (long)deceased)
                {
                    row.Owner = Housing.Describe(target);
                    changedIds.Add((ObjectId)row.Id);
                }
            }
            if (changedIds.Count == 0)
            {
                return;
            }
            // shortcut: Housing has no ownership-transfer API, so this reuses
            // its save import; placed items are re-registered and their
            // in-flight actions are invalidated. Replace when Housing owns a
            // transfer method.
            List<ObjectId> placed = new List<ObjectId>();
            List<bool> reachable = new List<bool>();
            if (executor != null)
            {
                for (int i = 0; i < snapshot.Items.Count; i++)
                {
                    if (snapshot.Items[i].InInventory)
                    {
                        continue;
                    }
                    ObjectId id = (ObjectId)snapshot.Items[i].Id;
                    bool wasReachable;
                    try
                    {
                        wasReachable = executor.IsReachable(id);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    executor.RemoveObject(id);
                    placed.Add(id);
                    reachable.Add(wasReachable);
                }
            }
            housing.Import(writer, snapshot);
            for (int i = 0; i < placed.Count; i++)
            {
                executor.SetReachable(placed[i], reachable[i]);
            }
            for (int i = 0; i < changedIds.Count; i++)
            {
                world.SettleEstate(writer, death, "item:" + ((long)changedIds[i]).ToString(),
                    primary.Town ? TownOwner : ((long)primary.Household.Value).ToString());
            }
            notices.Add("Heirlooms transferred to " + (primary.Town ? TownOwner : ((long)primary.Household.Value).ToString()) + ".");
        }

        private void AddOwnershipShare(List<OwnershipShare> shares, HouseholdId household, int percent)
        {
            for (int i = 0; i < shares.Count; i++)
            {
                if (shares[i].Household == household)
                {
                    shares[i] = new OwnershipShare(household, shares[i].Percent + percent,
                        shares[i].ChildcareDays);
                    return;
                }
            }
            shares.Add(new OwnershipShare(household, percent, 0));
        }

        private HouseholdId FindTownHousehold()
        {
            HouseholdRecord[] households = world.HouseholdsSnapshot();
            HouseholdId best = default(HouseholdId);
            bool found = false;
            for (int i = 0; i < households.Length; i++)
            {
                if (string.Equals(households[i].Name, TownHouseholdName, StringComparison.Ordinal)
                    && (!found || (long)households[i].Id < (long)best))
                {
                    best = households[i].Id;
                    found = true;
                }
            }
            return found ? best : default(HouseholdId);
        }

        private HouseholdId TownHousehold(StateWriter writer, HomeId home)
        {
            HouseholdId found = FindTownHousehold();
            if ((long)found != 0L)
            {
                return found;
            }
            return world.AllocateHousehold(writer, TownHouseholdName, home).Id;
        }

        // ---- dependent care ----

        private void SettleCare(StateWriter writer, WorldEvent death, ResidentRecord deceased,
            WillRecord will, List<string> notices)
        {
            List<ResidentRecord> dependents = DependentsOf(deceased);
            if (dependents.Count == 0)
            {
                return;
            }
            List<ResidentRecord> remaining = new List<ResidentRecord>();
            for (int i = 0; i < dependents.Count; i++)
            {
                CareAssignment current;
                if (!LatestCare(dependents[i].Id, out current) || current.Guardian != deceased.Id)
                {
                    continue;
                }
                if (current.Placement.Household.HasValue)
                {
                    ResidentId survivor;
                    if (TrySurvivingCaregiver(current.Placement.Household.Value, deceased.Id, out survivor))
                    {
                        CareAssignment retained = new CareAssignment(dependents[i].Id, survivor,
                            current.Placement, current.TransferEvent);
                        world.ReassignCare(writer, dependents[i].Id, retained);
                        notices.Add("Care retained by " + ((long)survivor).ToString() + ".");
                        continue;
                    }
                }
                remaining.Add(dependents[i]);
            }
            if (remaining.Count == 0)
            {
                return;
            }

            if (will != null)
            {
                for (int i = 0; i < will.CareHouseholds.Count; i++)
                {
                    HouseholdId household = will.CareHouseholds[i];
                    ResidentId guardian;
                    if (HasRoom(household, remaining.Count) && TryFindCaregiver(household, out guardian))
                    {
                        PlaceGroup(writer, remaining, household, guardian, notices);
                        return;
                    }
                }
            }

            HouseholdId nearest;
            ResidentId nearestGuardian;
            if (TryNearestCareHousehold(remaining, out nearest, out nearestGuardian))
            {
                PlaceGroup(writer, remaining, nearest, nearestGuardian, notices);
                return;
            }

            PlaceTown(writer, remaining, notices);
        }

        private List<ResidentRecord> DependentsOf(ResidentRecord deceased)
        {
            List<ResidentRecord> result = new List<ResidentRecord>();
            IReadOnlyList<CareAssignment> rows = world.CareAssignments;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Guardian != deceased.Id) continue;
                ResidentRecord dependent;
                if (world.TryGetResident(rows[i].Dependent, out dependent) && dependent.IsAlive)
                {
                    AddPerson(result, dependent);
                }
            }
            SortPeople(result);
            return result;
        }

        private bool LatestCare(ResidentId dependent, out CareAssignment assignment)
        {
            assignment = default(CareAssignment);
            IReadOnlyList<CareAssignment> rows = world.CareAssignments;
            bool found = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Dependent == dependent)
                {
                    assignment = rows[i];
                    found = true;
                }
            }
            return found;
        }

        private bool TrySurvivingCaregiver(HouseholdId household, ResidentId deceased, out ResidentId survivor)
        {
            survivor = default(ResidentId);
            HashSet<ResidentId> caregivers = Caregivers();
            ResidentRecord[] all = world.ResidentsSnapshot();
            bool found = false;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Household != household || !all[i].IsAlive || all[i].Id == deceased) continue;
                if (!caregivers.Contains(all[i].Id)) continue;
                if (!found || (long)all[i].Id < (long)survivor)
                {
                    survivor = all[i].Id;
                    found = true;
                }
            }
            return found;
        }

        private bool TryFindCaregiver(HouseholdId household, out ResidentId guardian)
        {
            guardian = default(ResidentId);
            HashSet<ResidentId> caregivers = Caregivers();
            ResidentRecord[] all = world.ResidentsSnapshot();
            bool found = false;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Household != household || !all[i].IsAlive) continue;
                if (!caregivers.Contains(all[i].Id)) continue;
                if (!found || (long)all[i].Id < (long)guardian)
                {
                    guardian = all[i].Id;
                    found = true;
                }
            }
            return found;
        }

        private bool TryNearestCareHousehold(List<ResidentRecord> dependents,
            out HouseholdId household, out ResidentId guardian)
        {
            household = default(HouseholdId);
            guardian = default(ResidentId);
            ResidentId anchor = dependents[0].Id;
            Dictionary<ResidentId, int> distance = Distances(anchor);
            List<ResidentId> candidates = new List<ResidentId>();
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                ResidentRecord candidate = all[i];
                if (!candidate.IsAlive || candidate.Id == anchor || IsDependent(candidate, dependents)) continue;
                int d;
                if (!distance.TryGetValue(candidate.Id, out d) || d <= 0) continue;
                if (!Caregivers().Contains(candidate.Id)) continue;
                if (!world.HasHousehold(candidate.Household)) continue;
                InsertCandidate(candidates, candidate.Id, distance);
            }
            HashSet<HouseholdId> considered = new HashSet<HouseholdId>();
            for (int i = 0; i < candidates.Count; i++)
            {
                ResidentRecord candidate;
                if (!world.TryGetResident(candidates[i], out candidate)) continue;
                if (!considered.Add(candidate.Household)) continue;
                ResidentId found;
                if (HasRoom(candidate.Household, dependents.Count) && TryFindCaregiver(candidate.Household, out found))
                {
                    household = candidate.Household;
                    guardian = found;
                    return true;
                }
            }
            return false;
        }

        private void InsertCandidate(List<ResidentId> candidates, ResidentId candidate,
            Dictionary<ResidentId, int> distance)
        {
            int d = distance[candidate];
            int insert = candidates.Count;
            for (int i = 0; i < candidates.Count; i++)
            {
                int other = distance[candidates[i]];
                if (d < other || (d == other && (long)candidate < (long)candidates[i]))
                {
                    insert = i;
                    break;
                }
            }
            candidates.Insert(insert, candidate);
        }

        private void PlaceGroup(StateWriter writer, List<ResidentRecord> dependents,
            HouseholdId household, ResidentId guardian, List<string> notices)
        {
            for (int i = 0; i < dependents.Count; i++)
            {
                if (housing.JoinHousehold(writer, dependents[i].Id, household).Accepted)
                {
                    EventId transfer = world.AppendEvent(writer, CareTransferKind, dependents[i].Id,
                        "Estate care transfer").Event;
                    world.AssignCare(writer, new CareAssignment(dependents[i].Id, guardian,
                        new CarePlacement(household, -1), transfer));
                    notices.Add("Care of " + ((long)dependents[i].Id).ToString() + " moved to household "
                        + ((long)household).ToString() + ".");
                }
            }
        }

        private void PlaceTown(StateWriter writer, List<ResidentRecord> dependents, List<string> notices)
        {
            HomeId home = dependents[0].Home;
            HouseholdId town = TownHousehold(writer, home);
            HouseholdRecord record;
            world.TryGetHousehold(town, out record);
            ResidentRecord guardian = world.AllocateResident(writer, "Town Caregiver", LifeStage.Adult,
                town, record != null ? record.Home : home);
            if (appearances != null)
            {
                appearances.Register(guardian.Id, new WorldRng(world.Seed + 7919L * (long)guardian.Id));
            }
            for (int i = 0; i < dependents.Count; i++)
            {
                EventId transfer = world.AppendEvent(writer, CareTransferKind, dependents[i].Id,
                    "Estate town care").Event;
                world.AssignCare(writer, new CareAssignment(dependents[i].Id, guardian.Id,
                    new CarePlacement(null, i), transfer));
                notices.Add("Care of " + ((long)dependents[i].Id).ToString() + " moved to town care.");
            }
        }

        private bool HasRoom(HouseholdId household, int extraDependents)
        {
            HouseholdRecord record;
            if (!world.TryGetHousehold(household, out record))
            {
                return false;
            }
            HomeRecord home;
            if (!world.TryGetHome(record.Home, out home))
            {
                return false;
            }
            // shortcut: mirrors Housing.NeedFor because Housing exposes no
            // add-with-capacity query; use that API if it lands.
            int nonDependent = 0;
            int dependent = 0;
            int caregiver = 0;
            HashSet<ResidentId> caregivers = Caregivers();
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Household != household) continue;
                if (IsUnderChild(all[i].Stage)) dependent++;
                else nonDependent++;
                if (caregivers.Contains(all[i].Id)) caregiver++;
            }
            int reserved = Reservations(household);
            int need = nonDependent + Math.Max(0, dependent + reserved - caregiver * 2);
            return need + extraDependents <= home.BedCount;
        }

        private HashSet<ResidentId> Caregivers()
        {
            HashSet<ResidentId> set = new HashSet<ResidentId>();
            Housing.HousingSnapshot snapshot = housing.Export();
            for (int i = 0; i < snapshot.Caregivers.Count; i++)
            {
                set.Add((ResidentId)snapshot.Caregivers[i]);
            }
            return set;
        }

        private int Reservations(HouseholdId household)
        {
            Housing.HousingSnapshot snapshot = housing.Export();
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                if ((HouseholdId)snapshot.Reservations[i].Household == household)
                {
                    return snapshot.Reservations[i].Count;
                }
            }
            return 0;
        }

        private static bool IsUnderChild(LifeStage stage)
        {
            return stage == LifeStage.Newborn || stage == LifeStage.Infant || stage == LifeStage.Toddler;
        }

        private static bool IsDependent(ResidentRecord candidate, List<ResidentRecord> dependents)
        {
            for (int i = 0; i < dependents.Count; i++)
            {
                if (dependents[i].Id == candidate.Id) return true;
            }
            return false;
        }

        // ---- deterministic family graph and shares ----

        private Dictionary<ResidentId, int> Distances(ResidentId from)
        {
            Dictionary<ResidentId, List<ResidentId>> adjacency =
                new Dictionary<ResidentId, List<ResidentId>>();
            ResidentRecord[] all = world.ResidentsSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                adjacency[all[i].Id] = new List<ResidentId>();
            }
            for (int i = 0; i < all.Length; i++)
            {
                for (int f = 0; f < all[i].Family.Count; f++)
                {
                    ResidentId other = all[i].Family[f].Other;
                    if (!adjacency.ContainsKey(other)) continue;
                    adjacency[all[i].Id].Add(other);
                    adjacency[other].Add(all[i].Id);
                }
            }
            Dictionary<ResidentId, int> distance = new Dictionary<ResidentId, int>();
            Queue<ResidentId> queue = new Queue<ResidentId>();
            distance[from] = 0;
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                ResidentId current = queue.Dequeue();
                List<ResidentId> links = adjacency[current];
                int next = distance[current] + 1;
                for (int i = 0; i < links.Count; i++)
                {
                    if (distance.ContainsKey(links[i])) continue;
                    distance[links[i]] = next;
                    queue.Enqueue(links[i]);
                }
            }
            return distance;
        }

        private List<Allocation> SplitPeoplePercent(List<ResidentRecord> people, int percent)
        {
            List<ResidentRecord> sorted = new List<ResidentRecord>(people);
            SortPeople(sorted);
            Dictionary<HouseholdId, int> combined = new Dictionary<HouseholdId, int>();
            List<HouseholdId> order = new List<HouseholdId>();
            int count = sorted.Count;
            int baseShare = count == 0 ? 0 : percent / count;
            int left = count == 0 ? 0 : percent % count;
            for (int i = 0; i < count; i++)
            {
                int share = baseShare + (i < left ? 1 : 0);
                if (share <= 0) continue;
                HouseholdId household = sorted[i].Household;
                if (!world.HasHousehold(household)) continue;
                int existing;
                if (combined.TryGetValue(household, out existing))
                {
                    combined[household] = existing + share;
                }
                else
                {
                    combined.Add(household, share);
                    order.Add(household);
                }
            }
            List<Allocation> result = new List<Allocation>();
            SortHouseholdIds(order);
            for (int i = 0; i < order.Count; i++)
            {
                result.Add(HouseholdAllocation(order[i], combined[order[i]]));
            }
            return result;
        }

        private List<Allocation> SplitPercent(List<Allocation> allocations, int totalPercent)
        {
            List<Allocation> result = new List<Allocation>();
            List<MoneyRow> rows = new List<MoneyRow>();
            long floors = 0L;
            int assigned = 0;
            for (int i = 0; i < allocations.Count; i++)
            {
                if (allocations[i].Percent <= 0) continue;
                MoneyRow row = new MoneyRow();
                row.Share = allocations[i];
                row.Floor = ShareFloor(totalPercent, allocations[i].Percent);
                row.Remainder = ShareRemainder(totalPercent, allocations[i].Percent);
                row.Amount = row.Floor;
                floors += row.Floor;
                rows.Add(row);
                assigned += allocations[i].Percent;
            }
            if (assigned != 100) return result;
            SortMoneyRows(rows);
            long left = (long)totalPercent - floors;
            if (left > (long)rows.Count) left = (long)rows.Count;
            for (long i = 0L; i < left; i++)
            {
                rows[(int)i].Amount = rows[(int)i].Floor + 1L;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Amount <= 0L) continue;
                Allocation share = new Allocation();
                share.Key = rows[i].Share.Key;
                share.Household = rows[i].Share.Household;
                share.Charity = rows[i].Share.Charity;
                share.Town = rows[i].Share.Town;
                share.Percent = (int)rows[i].Amount;
                result.Add(share);
            }
            return result;
        }

        private static void Merge(Dictionary<string, Allocation> combined, List<string> order,
            Allocation share, int percent)
        {
            Allocation found;
            if (combined.TryGetValue(share.Key, out found))
            {
                found.Percent += percent;
            }
            else
            {
                share.Percent = percent;
                combined.Add(share.Key, share);
                order.Add(share.Key);
            }
        }

        private static void MergeList(List<Allocation> result, Allocation share)
        {
            for (int i = 0; i < result.Count; i++)
            {
                if (string.Equals(result[i].Key, share.Key, StringComparison.Ordinal))
                {
                    result[i].Percent += share.Percent;
                    return;
                }
            }
            Allocation copy = new Allocation();
            copy.Key = share.Key;
            copy.Household = share.Household;
            copy.Charity = share.Charity;
            copy.Town = share.Town;
            copy.Percent = share.Percent;
            result.Add(copy);
        }

        private static Allocation HouseholdAllocation(HouseholdId household)
        {
            Allocation share = new Allocation();
            share.Key = "h:" + ((long)household).ToString();
            share.Household = household;
            return share;
        }

        private static Allocation HouseholdAllocation(HouseholdId household, int percent)
        {
            Allocation share = HouseholdAllocation(household);
            share.Percent = percent;
            return share;
        }

        private static Allocation CharityAllocation()
        {
            Allocation share = new Allocation();
            share.Key = "charity";
            share.Charity = true;
            return share;
        }

        private static Allocation TownAllocation(int percent)
        {
            Allocation share = new Allocation();
            share.Key = "town";
            share.Town = true;
            share.Percent = percent;
            return share;
        }

        private static string RecipientLabel(Allocation share)
        {
            if (share.Charity) return "Charity";
            if (share.Town) return "TownFund";
            return share.Household.HasValue ? ((long)share.Household.Value).ToString() : share.Key;
        }

        private static bool IsCharity(string recipient)
        {
            return string.Equals(recipient, "Charity", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryParseHousehold(string recipient, out HouseholdId household)
        {
            household = default(HouseholdId);
            string text = (recipient ?? string.Empty).Trim();
            if (text.StartsWith("household:", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(10);
            }
            else if (text.Length > 1 && (text[0] == 'H' || text[0] == 'h') && char.IsDigit(text[1]))
            {
                text = text.Substring(1);
            }
            long id;
            if (!long.TryParse(text, out id) || id <= 0L)
            {
                return false;
            }
            household = (HouseholdId)id;
            return world.HasHousehold(household);
        }

        private static bool IsKind(string kind, string wanted)
        {
            return string.Equals(kind, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static long ShareFloor(long total, int percent)
        {
            return (total / 100L) * (long)percent + ((total % 100L) * (long)percent) / 100L;
        }

        private static long ShareRemainder(long total, int percent)
        {
            return ((total % 100L) * (long)percent) % 100L;
        }

        private static void AddPerson(List<ResidentRecord> people, ResidentRecord person)
        {
            for (int i = 0; i < people.Count; i++)
            {
                if (people[i].Id == person.Id) return;
            }
            people.Add(person);
        }

        private static void SortPeople(List<ResidentRecord> people)
        {
            for (int i = 1; i < people.Count; i++)
            {
                ResidentRecord key = people[i];
                int j = i - 1;
                while (j >= 0 && (long)people[j].Id > (long)key.Id)
                {
                    people[j + 1] = people[j];
                    j--;
                }
                people[j + 1] = key;
            }
        }

        private static void SortHouseholdIds(List<HouseholdId> ids)
        {
            for (int i = 1; i < ids.Count; i++)
            {
                HouseholdId key = ids[i];
                int j = i - 1;
                while (j >= 0 && (long)ids[j] > (long)key)
                {
                    ids[j + 1] = ids[j];
                    j--;
                }
                ids[j + 1] = key;
            }
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
    }
}
