// HOUSING@1: households, home purchase, furnishing and vacancy arrivals.
// Forming, moving, purchasing, furnishing and filling vacancies preserve
// households, identity and capacity with readable scarcity. Plain C# 9.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    public sealed class HousingOffer
    {
        public long Price { get; private set; }
        public long Shortfall { get; private set; }
        public bool Fits { get; private set; }
        public string Reason { get; private set; }
        public string NextSteps { get; private set; }
        public HousingOffer(long price, long shortfall, bool fits, string reason, string nextSteps)
        { Price = price; Shortfall = shortfall; Fits = fits; Reason = reason; NextSteps = nextSteps; }
    }

    public sealed class HousingItem
    {
        public ObjectId Id { get; private set; }
        public HomeId Home { get; internal set; }
        public AccountRef Owner { get; private set; }
        public bool InInventory { get; internal set; }
        public long PurchasePrice { get; private set; }
        public long ResaleValue { get { return PurchasePrice / 2L; } }
        internal string Code; internal ApproachPose Pose; internal int Slots;
        public HousingItem(ObjectId id, HomeId home, AccountRef owner, bool inv, long price, string code, ApproachPose pose, int slots)
        { Id = id; Home = home; Owner = owner; InInventory = inv; PurchasePrice = price; Code = code; Pose = pose; Slots = slots; }
    }

    public sealed class Housing
    {
        private const long DaySeconds = 86400L;
        private sealed class FurnitureDef { public long Price; public int Slots; }

        public sealed class HousingSnapshot
        {
            public sealed class HomeRow { public long Home; public long Price; }
            public sealed class FurnitureRow { public string Code; public long Price; public int Slots; }
            public sealed class ItemRow { public long Id; public string Code; public long Home; public AccountsSnapshot.AccountRefData Owner; public bool InInventory; public long PurchasePrice; public float X; public float Y; public float Z; public int Slots; }
            public sealed class ReservationRow { public long Household; public int Count; }
            public sealed class VacancyRow { public long Home; public long Since; }
            public readonly List<HomeRow> Homes = new List<HomeRow>();
            public readonly List<FurnitureRow> Furniture = new List<FurnitureRow>();
            public readonly List<ItemRow> Items = new List<ItemRow>();
            public readonly List<long> Caregivers = new List<long>();
            public readonly List<ReservationRow> Reservations = new List<ReservationRow>();
            public readonly List<VacancyRow> Vacancies = new List<VacancyRow>();
            public readonly List<long> Applied = new List<long>();
        }

        private readonly WorldState world;
        private readonly Accounts accounts;
        private readonly Ownership ownership;
        private readonly ActivityExecutor executor;
        private readonly AppearanceStore appearances;
        private readonly Dictionary<HomeId, long> homePrices = new Dictionary<HomeId, long>();
        private readonly Dictionary<string, FurnitureDef> furniture = new Dictionary<string, FurnitureDef>();
        private readonly Dictionary<ObjectId, HousingItem> items = new Dictionary<ObjectId, HousingItem>();
        private readonly HashSet<ResidentId> caregivers = new HashSet<ResidentId>();
        private readonly Dictionary<HouseholdId, int> reservations = new Dictionary<HouseholdId, int>();
        private readonly Dictionary<HomeId, long> vacancies = new Dictionary<HomeId, long>();
        private readonly HashSet<long> applied = new HashSet<long>();

        public string Message { get; private set; }

        public Housing(WorldState world, Accounts accounts, Ownership ownership, ActivityExecutor executor, AppearanceStore appearances)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (accounts == null) throw new ArgumentNullException("accounts");
            if (ownership == null) throw new ArgumentNullException("ownership");
            if (executor == null) throw new ArgumentNullException("executor");
            if (appearances == null) throw new ArgumentNullException("appearances");
            this.world = world; this.accounts = accounts; this.ownership = ownership;
            this.executor = executor; this.appearances = appearances; Message = string.Empty;
        }

        public void RegisterHome(StateWriter writer, HomeId home, long price)
        { CheckWriter(writer); homePrices[home] = price; }

        public void RegisterFurniture(StateWriter writer, string code, long price, int slots)
        { CheckWriter(writer); furniture[code] = new FurnitureDef { Price = price, Slots = slots }; }

        public void SetCaregiver(StateWriter writer, ResidentId resident, bool value)
        { CheckWriter(writer); if (value) caregivers.Add(resident); else caregivers.Remove(resident); }

        public void SetNewbornReservations(StateWriter writer, HouseholdId household, int count)
        { CheckWriter(writer); if (count <= 0) reservations.Remove(household); else reservations[household] = count; }

        // --- capacity ---
        private int Beds(HomeId home)
        {
            foreach (HomeRecord h in world.ExportSnapshot().Homes) if (h.Id == home) return h.BedCount;
            return 0;
        }
        private static bool IsDependent(LifeStage s) { return s == LifeStage.Newborn || s == LifeStage.Infant || s == LifeStage.Toddler; }
        private int NeedFor(List<ResidentRecord> members, HouseholdId household, ResidentRecord extra)
        {
            int nonDep = 0, dep = 0, cg = 0;
            List<ResidentRecord> all = new List<ResidentRecord>(members);
            if (extra != null) all.Add(extra);
            for (int i = 0; i < all.Count; i++)
            {
                if (IsDependent(all[i].Stage)) dep++; else nonDep++;
                if (caregivers.Contains(all[i].Id)) cg++;
            }
            int res; reservations.TryGetValue(household, out res);
            int need = nonDep + Math.Max(0, dep + res - cg * 2);
            return need;
        }
        private List<ResidentRecord> Members(HouseholdId household)
        {
            List<ResidentRecord> list = new List<ResidentRecord>();
            foreach (ResidentRecord r in world.ResidentsSnapshot()) if (r.Household == household) list.Add(r);
            return list;
        }
        private bool FitsCapacity(List<ResidentRecord> members, HouseholdId household, ResidentRecord extra, HomeId home)
        { return NeedFor(members, household, extra) <= Beds(home); }

        private CommandResult Refuse(RejectReason reason, string message)
        { Message = message; return CommandResult.Reject(reason); }
        private CommandResult CapacityRefuse(int need, int beds)
        { return Refuse(RejectReason.InvalidParameters, "capacity: need " + need + " of " + beds + " beds"); }

        public HousingOffer GetOffer(HouseholdId household, HomeId home, AccountRef funding)
        {
            long price; if (!homePrices.TryGetValue(home, out price)) price = 0L;
            long balance = 0L;
            try { balance = accounts.Balance(funding); } catch (ArgumentException) { balance = 0L; }
            long shortfall = price > balance ? price - balance : 0L;
            List<ResidentRecord> members = Members(household);
            int need = NeedFor(members, household, null);
            int beds = Beds(home);
            bool fits = need <= beds;
            string reason;
            if (!fits) reason = "capacity: " + members.Count + " residents need " + need + " of " + beds + " beds; price " + price + ", shortfall " + shortfall;
            else if (shortfall > 0L) reason = "price " + price + ", shortfall " + shortfall + " (short by " + shortfall + ")";
            else reason = "price " + price + ", fits " + beds + " beds";
            return new HousingOffer(price, shortfall, fits, reason,
                "Save more, share costs from a shared account, or rearrange household members.");
        }

        // --- household moves ---
        public CommandResult FormHousehold(StateWriter writer, string name, ResidentId[] residents, HomeId home)
        {
            CheckWriter(writer);
            if (residents == null || residents.Length == 0) return Refuse(RejectReason.InvalidParameters, "InvalidParameters: no residents");
            List<ResidentRecord> members = new List<ResidentRecord>();
            for (int i = 0; i < residents.Length; i++) { ResidentRecord r; if (!world.TryGetResident(residents[i], out r)) return Refuse(RejectReason.TargetMissing, "TargetMissing"); members.Add(r); }
            int need = NeedFor(members, default(HouseholdId), null);
            if (need > Beds(home)) return CapacityRefuse(need, Beds(home));
            HouseholdRecord created = world.AllocateHousehold(writer, name, home);
            for (int i = 0; i < members.Count; i++) world.SetHousehold(writer, members[i].Id, created.Id, home);
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult JoinHousehold(StateWriter writer, ResidentId resident, HouseholdId household)
        {
            CheckWriter(writer);
            ResidentRecord r; if (!world.TryGetResident(resident, out r)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            HomeId home = default(HomeId); bool found = false;
            foreach (HouseholdRecord h in world.ExportSnapshot().Households) if (h.Id == household) { home = h.Home; found = true; }
            if (!found) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            List<ResidentRecord> members = Members(household);
            int need = NeedFor(members, household, r);
            if (need > Beds(home)) return CapacityRefuse(need, Beds(home));
            world.SetHousehold(writer, resident, household, home);
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult LeaveHousehold(StateWriter writer, ResidentId resident, HomeId home)
        {
            CheckWriter(writer);
            ResidentRecord r; if (!world.TryGetResident(resident, out r)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            List<ResidentRecord> single = new List<ResidentRecord>(); single.Add(r);
            int need = NeedFor(single, default(HouseholdId), null);
            if (need > Beds(home)) return CapacityRefuse(need, Beds(home));
            HouseholdRecord created = world.AllocateHousehold(writer, r.DisplayName + " household", home);
            world.SetHousehold(writer, resident, created.Id, home);
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult MoveHousehold(StateWriter writer, HouseholdId household, HomeId home)
        {
            CheckWriter(writer);
            List<ResidentRecord> members = Members(household);
            int need = NeedFor(members, household, null);
            if (need > Beds(home)) return CapacityRefuse(need, Beds(home));
            world.MoveHousehold(writer, household, home);
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult BuyHome(StateWriter writer, ActionInstanceId action, HouseholdId household, HomeId home, AccountRef funding)
        {
            CheckWriter(writer);
            if (applied.Contains((long)action)) return CommandResult.Accept();
            long price; if (!homePrices.TryGetValue(home, out price)) price = 0L;
            List<ResidentRecord> members = Members(household);
            int need = NeedFor(members, household, null);
            if (need > Beds(home)) return CapacityRefuse(need, Beds(home));
            if (price > 0L)
            {
                CommandResult paid = accounts.Transfer(writer, action, funding, AccountRef.Expense("HomePurchase"), price);
                if (!paid.Accepted) { Message = paid.Reason.ToString() + ": price " + price; return paid; }
            }
            applied.Add((long)action);
            ownership.SetAgreement(writer, home, price, new[] { new OwnershipShare(household, 100, 0) });
            world.MoveHousehold(writer, household, home);
            Message = string.Empty; return CommandResult.Accept();
        }

        // --- furniture ---
        public CommandResult BuyFurniture(StateWriter writer, ActionInstanceId action, HouseholdId household, string code, HomeId home, ApproachPose pose, AccountRef funding, out ObjectId id)
        {
            CheckWriter(writer); id = default(ObjectId);
            FurnitureDef def;
            if (code == null || !furniture.TryGetValue(code, out def)) return Refuse(RejectReason.InvalidParameters, "InvalidParameters: unknown furniture");
            if (applied.Contains((long)action)) return Refuse(RejectReason.InvalidParameters, "InvalidParameters: duplicate action");
            CommandResult paid = accounts.Transfer(writer, action, funding, AccountRef.Expense("FurniturePurchase"), def.Price);
            if (!paid.Accepted) { Message = paid.Reason.ToString(); return paid; }
            applied.Add((long)action);
            id = world.AllocateObject(writer);
            HousingItem item = new HousingItem(id, home, funding, false, def.Price, code, pose, def.Slots);
            items.Add(id, item);
            executor.RegisterObject(id, Poses(item));
            Message = string.Empty; return CommandResult.Accept();
        }

        private static ApproachPose[] Poses(HousingItem item)
        {
            ApproachPose[] poses = new ApproachPose[Math.Max(1, item.Slots)];
            for (int i = 0; i < poses.Length; i++) poses[i] = item.Pose;
            return poses;
        }
        private bool IsRegistered(ObjectId id) { try { return executor.IsReachable(id); } catch (ArgumentException) { return false; } }

        public CommandResult MoveFurniture(StateWriter writer, ObjectId id, HomeId home, ApproachPose pose)
        {
            CheckWriter(writer); HousingItem item;
            if (!items.TryGetValue(id, out item)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            item.Home = home; item.Pose = pose;
            if (!item.InInventory) executor.MoveObject(id, Poses(item));
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult StoreFurniture(StateWriter writer, ObjectId id)
        {
            CheckWriter(writer); HousingItem item;
            if (!items.TryGetValue(id, out item)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            if (!item.InInventory && IsRegistered(id)) executor.RemoveObject(id);
            item.InInventory = true; Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult PlaceFurniture(StateWriter writer, ObjectId id, HomeId home, ApproachPose pose)
        {
            CheckWriter(writer); HousingItem item;
            if (!items.TryGetValue(id, out item)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            item.Home = home; item.Pose = pose; item.InInventory = false;
            if (!IsRegistered(id)) executor.RegisterObject(id, Poses(item));
            Message = string.Empty; return CommandResult.Accept();
        }

        public CommandResult SellFurniture(StateWriter writer, ActionInstanceId action, ObjectId id)
        {
            CheckWriter(writer);
            if (applied.Contains((long)action)) return CommandResult.Accept();
            HousingItem item;
            if (!items.TryGetValue(id, out item)) return Refuse(RejectReason.TargetMissing, "TargetMissing");
            long resale = item.ResaleValue;
            if (!item.InInventory && IsRegistered(id)) executor.RemoveObject(id);
            items.Remove(id);
            applied.Add((long)action);
            if (resale > 0L && item.Owner.IsShared)
            {
                HouseholdId hh = item.Owner.Household;
                accounts.SetSharedBalance(writer, hh, accounts.Balance(item.Owner) + resale);
                accounts.Transfer(writer, action, item.Owner, item.Owner, resale);
            }
            Message = string.Empty; return CommandResult.Accept();
        }

        public HousingItem GetItem(ObjectId id) { HousingItem item; return items.TryGetValue(id, out item) ? item : null; }

        // --- vacancy arrivals ---
        public void Tick(StateWriter writer)
        {
            CheckWriter(writer);
            // Record newly vacant registered homes.
            List<HomeId> registered = new List<HomeId>(homePrices.Keys);
            registered.Sort(delegate (HomeId x, HomeId y) { return ((long)x).CompareTo((long)y); });
            HashSet<HomeId> occupied = new HashSet<HomeId>();
            foreach (ResidentRecord r in world.ResidentsSnapshot()) occupied.Add(r.Home);
            for (int i = 0; i < registered.Count; i++)
            {
                if (occupied.Contains(registered[i])) vacancies.Remove(registered[i]);
                else if (!vacancies.ContainsKey(registered[i])) vacancies[registered[i]] = world.GameSeconds;
            }
            if (world.GameSeconds < 3L * DaySeconds) return;
            int totalBeds = 0;
            foreach (HomeRecord h in world.ExportSnapshot().Homes) totalBeds += h.BedCount;
            int cap = Math.Max(24, totalBeds);
            List<HomeId> vacant = new List<HomeId>(vacancies.Keys);
            vacant.Sort(delegate (HomeId x, HomeId y) { return ((long)x).CompareTo((long)y); });
            for (int i = 0; i < vacant.Count; i++)
            {
                if (world.ResidentCount >= cap) break;
                if (world.GameSeconds - vacancies[vacant[i]] < 2L * DaySeconds) continue;
                HomeId home = vacant[i];
                HouseholdRecord household = world.AllocateHousehold(writer, "New household", home);
                ResidentRecord arrival = world.AllocateResident(writer, null, LifeStage.Adult, household.Id, home);
                appearances.Register(arrival.Id, new WorldRng(world.Seed + 7919L * (long)arrival.Id));
                vacancies.Remove(home);
            }
        }

        // --- save ---
        public HousingSnapshot Export()
        {
            HousingSnapshot s = new HousingSnapshot();
            List<HomeId> homes = new List<HomeId>(homePrices.Keys);
            homes.Sort(delegate (HomeId x, HomeId y) { return ((long)x).CompareTo((long)y); });
            foreach (HomeId h in homes) s.Homes.Add(new HousingSnapshot.HomeRow { Home = (long)h, Price = homePrices[h] });
            List<string> codes = new List<string>(furniture.Keys); codes.Sort();
            foreach (string c in codes) s.Furniture.Add(new HousingSnapshot.FurnitureRow { Code = c, Price = furniture[c].Price, Slots = furniture[c].Slots });
            List<ObjectId> ids = new List<ObjectId>(items.Keys);
            ids.Sort(delegate (ObjectId x, ObjectId y) { return ((long)x).CompareTo((long)y); });
            foreach (ObjectId id in ids)
            {
                HousingItem it = items[id];
                s.Items.Add(new HousingSnapshot.ItemRow { Id = (long)id, Code = it.Code, Home = (long)it.Home, Owner = Describe(it.Owner), InInventory = it.InInventory, PurchasePrice = it.PurchasePrice, X = it.Pose.X, Y = it.Pose.Y, Z = it.Pose.Z, Slots = it.Slots });
            }
            foreach (ResidentId r in caregivers) s.Caregivers.Add((long)r);
            s.Caregivers.Sort();
            List<HouseholdId> hh = new List<HouseholdId>(reservations.Keys);
            hh.Sort(delegate (HouseholdId x, HouseholdId y) { return ((long)x).CompareTo((long)y); });
            foreach (HouseholdId h in hh) s.Reservations.Add(new HousingSnapshot.ReservationRow { Household = (long)h, Count = reservations[h] });
            List<HomeId> vac = new List<HomeId>(vacancies.Keys);
            vac.Sort(delegate (HomeId x, HomeId y) { return ((long)x).CompareTo((long)y); });
            foreach (HomeId h in vac) s.Vacancies.Add(new HousingSnapshot.VacancyRow { Home = (long)h, Since = vacancies[h] });
            foreach (long a in applied) s.Applied.Add(a);
            s.Applied.Sort();
            return s;
        }

        public void Import(StateWriter writer, HousingSnapshot s)
        {
            CheckWriter(writer);
            if (s == null) throw new ArgumentNullException("snapshot");
            homePrices.Clear(); furniture.Clear(); items.Clear(); caregivers.Clear(); reservations.Clear(); vacancies.Clear(); applied.Clear();
            foreach (HousingSnapshot.HomeRow r in s.Homes) homePrices[(HomeId)r.Home] = r.Price;
            foreach (HousingSnapshot.FurnitureRow r in s.Furniture) furniture[r.Code] = new FurnitureDef { Price = r.Price, Slots = r.Slots };
            foreach (long c in s.Caregivers) caregivers.Add((ResidentId)c);
            foreach (HousingSnapshot.ReservationRow r in s.Reservations) reservations[(HouseholdId)r.Household] = r.Count;
            foreach (HousingSnapshot.VacancyRow r in s.Vacancies) vacancies[(HomeId)r.Home] = r.Since;
            foreach (long a in s.Applied) applied.Add(a);
            foreach (HousingSnapshot.ItemRow r in s.Items)
            {
                HousingItem item = new HousingItem((ObjectId)r.Id, (HomeId)r.Home, RequireRef(r.Owner), r.InInventory, r.PurchasePrice, r.Code, new ApproachPose(r.X, r.Y, r.Z), r.Slots);
                items.Add(item.Id, item);
                if (!item.InInventory) executor.RegisterObject(item.Id, Poses(item));
            }
        }

        internal static AccountsSnapshot.AccountRefData Describe(AccountRef account)
        {
            AccountsSnapshot.AccountRefData d = new AccountsSnapshot.AccountRefData();
            if (account.IsPersonal) { d.Kind = "personal"; d.Id = (long)account.Resident; d.Name = string.Empty; }
            else if (account.IsShared) { d.Kind = "shared"; d.Id = (long)account.Household; d.Name = string.Empty; }
            else { d.Kind = "expense"; d.Id = 0L; d.Name = account.Destination ?? string.Empty; }
            return d;
        }
        private static AccountRef RequireRef(AccountsSnapshot.AccountRefData d)
        {
            if (d != null && d.Kind == "personal") return AccountRef.Personal((ResidentId)d.Id);
            if (d != null && d.Kind == "shared") return AccountRef.Shared((HouseholdId)d.Id);
            if (d != null && d.Kind == "expense") return AccountRef.Expense(d.Name);
            throw new ArgumentException("Unknown account ref", "snapshot");
        }
        private static void CheckWriter(StateWriter writer)
        {
            if (writer != StateWriter.SimCore && writer != StateWriter.CommandArbitrator && writer != StateWriter.ActivityExecutor)
                throw new ArgumentException("Unknown state writer", "writer");
        }
    }
}
