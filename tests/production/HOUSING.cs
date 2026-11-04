/*
HOUSING@1 red contract, namespace Blockparty.Simulation, Housing.cs:
  Housing(WorldState, Accounts, Ownership, ActivityExecutor, AppearanceStore)
  RegisterHome(StateWriter, HomeId, long price) // existing authored home, 0 = not for sale
  RegisterFurniture(StateWriter, string code, long price, int slots)
  GetOffer(HouseholdId, HomeId, AccountRef funding): Price, Shortfall, Fits,
    Reason and NextSteps (readable strings; reason identifies cost/capacity)
  CommandResult FormHousehold(StateWriter, string name, ResidentId[], HomeId)
  CommandResult JoinHousehold(StateWriter, ResidentId, HouseholdId)
  CommandResult LeaveHousehold(StateWriter, ResidentId, HomeId)
  CommandResult MoveHousehold(StateWriter, HouseholdId, HomeId)
  CommandResult BuyHome(StateWriter, ActionInstanceId, HouseholdId, HomeId, AccountRef)
  SetCaregiver(StateWriter, ResidentId, bool)
  SetNewbornReservations(StateWriter, HouseholdId, int)
  CommandResult BuyFurniture(StateWriter, ActionInstanceId, HouseholdId, string code,
    HomeId, ApproachPose, AccountRef, out ObjectId)
  CommandResult MoveFurniture(StateWriter, ObjectId, HomeId, ApproachPose)
  CommandResult StoreFurniture(StateWriter, ObjectId)
  CommandResult PlaceFurniture(StateWriter, ObjectId, HomeId, ApproachPose)
  CommandResult SellFurniture(StateWriter, ActionInstanceId, ObjectId)
  GetItem(ObjectId): null after sale, otherwise Id, Home, Owner (AccountRef),
    InInventory, PurchasePrice, ResaleValue
  Tick(StateWriter) // real host calls after advancing CLOCK
  string Message // last player-visible refusal reason
  GameStack overload appends Housing to its nine service arguments;
    LoadedGame.Housing restores it against the restored dependencies.

Oracles: plan HOUSING.suite_checks, M3 Test Engineer HOUSING, BP-WORLD-02/03/04/07,
BP-SEC-05 and the SEIT ids printed below. Numeric furniture inputs 75, 60/40 and
home boundaries 2499/2500, 3999/4000 come from the committed SEIT fixtures.
A separate module is required by the package runner; predecessor suites exercise
accounts/ownership/navigation, not this housing lifecycle. No replacement product
implementation, fake accounts, fake navigation, or fake save codec is used.
The negative-fixture argument adds the missing unit to a real account immediately
before the normal insufficient-funds assertion: fabricated affordability must fail
that SAME assertion. It is intentionally a failing run, never a passing negative.
Local subclaims only: packaged UI/camera, material variants, geometry placement,
asset provenance/runtime art, joint-item share distribution, cooking cancellation
and process restart remain at their recorded SEIT gates; this is no whole-case PASS.
Resource group isolated-csharp; no editor, ports, credentials or network. Save uses
one unique temporary directory per invocation and removes it on every exit path.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class HousingChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const long Day = 86400L;
    private static int failures;

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Commands;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public AppearanceStore Appearances;
        public Ownership Ownership;
        public Housing Housing;
        public GameStack Stack;
        public HomeId Source, Target;
        public HouseholdId Household;
        public ResidentId[] People;
        public AccountRef Shared { get { return AccountRef.Shared(Household); } }
    }

    private static Fixture NewFixture(int count = 2, long funds = 5000L, int beds = 2, long price = 2500L)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(701L, 4242L, count, null);
        WorldSnapshot initial = f.World.ExportSnapshot();
        f.Source = initial.Homes[0].Id;
        f.Household = initial.Households[0].Id;
        f.People = new ResidentId[count];
        ResidentRecord[] residents = f.World.ResidentsSnapshot();
        for (int i = 0; i < count; i++) f.People[i] = residents[i].Id;
        f.Target = f.World.AllocateHome(Core, beds).Id;
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Commands = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Commands, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Accounts.SetSharedBalance(Core, f.Household, funds);
        f.Appearances = new AppearanceStore();
        f.Ownership = new Ownership(f.World, f.Accounts);
        Food food = new Food(f.World, f.Commands, f.Executor, f.Accounts);
        Expenses bills = new Expenses(f.World, f.Accounts);
        f.Housing = new Housing(f.World, f.Accounts, f.Ownership, f.Executor, f.Appearances);
        f.Housing.RegisterHome(Core, f.Source, 0L);
        f.Housing.RegisterHome(Core, f.Target, price);
        f.Housing.RegisterFurniture(Core, "sofa", 75L, 1);
        f.Housing.RegisterFurniture(Core, "desk", 60L, 1);
        f.Stack = new GameStack(f.World, f.Clock, f.Commands, f.Executor, f.Appearances,
            f.Accounts, food, bills, f.Ownership, f.Housing);
        if (count >= 2)
        {
            f.World.SetBond(Core, f.People[0], f.People[1], 32.0f, 7.0f);
            f.World.AddFamilyLink(Core, f.People[0], f.People[1], "Sibling");
            WorldEvent met = f.World.AppendEvent(Core, "FirstMeeting", f.People[0], "met at home");
            f.World.AddFeeling(Core, f.People[0], FeelingKind.Grateful, met.Event, f.People[1], 1, 20L * Day);
            f.World.AddMemory(Core, f.People[0], new MemoryEntry(met.Event, "FirstMeeting", 0L,
                f.Source, new List<ResidentId>(f.People).AsReadOnly(), "OK", "participant"));
        }
        return f;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static ResidentRecord Person(WorldState world, ResidentId id)
    {
        ResidentRecord record;
        Require(world.TryGetResident(id, out record), "resident missing: " + (long)id);
        return record;
    }

    private static string Json(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    // Membership is asserted separately; all other resident fields must survive.
    private static string Identity(WorldState world, ResidentId id)
    {
        ResidentRecord r = Person(world, id);
        float[] needs = new float[StateSchema.NeedCount];
        int[] traits = new int[StateSchema.TraitCount];
        for (int i = 0; i < needs.Length; i++) needs[i] = r.GetNeed((Need)i);
        for (int i = 0; i < traits.Length; i++) traits[i] = r.GetTrait((TraitAxis)i);
        return Json(new { r.Id, r.DisplayName, r.Stage, r.StageProgressDays, r.Money,
            r.JobId, r.CareerLevel, r.CareerPoints, r.EducationPoints, r.IsAlive,
            r.StarvationHours, r.Ill, r.Fatigued, r.Injured, r.LastChanceDrawDay,
            r.Skills, r.Bonds, r.Family, r.Feelings, r.Memories, r.CompletedCourses, needs, traits });
    }

    private static bool Assigned(WorldState world, ResidentId id, HouseholdId household, HomeId home)
    {
        ResidentRecord r = Person(world, id);
        foreach (HouseholdRecord h in world.ExportSnapshot().Households)
            if (h.Id == household) return r.Household == household && r.Home == home && h.Home == home;
        return false;
    }

    private static string Snapshot(Fixture f)
    {
        return Json(f.World.ExportSnapshot()) + Json(f.Accounts.Export());
    }

    private static bool Has(string text, string part)
    {
        return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool NextStep(string text)
    {
        return Has(text, "sav") || Has(text, "shar") || Has(text, "rearrang");
    }

    private static void Advance(Fixture f, long to)
    {
        Require(to >= f.World.GameSeconds && (to - f.World.GameSeconds) % 6L == 0L, "clock fixture boundary");
        f.Clock.AdvanceRealSeconds(Core, (double)(to - f.World.GameSeconds) / 60.0);
        Require(f.World.GameSeconds == to, "real CLOCK reached boundary");
        f.Housing.Tick(Core);
    }

    private static void FormJoinLeaveMove()
    {
        Fixture f = NewFixture();
        ResidentId a = f.People[0], b = f.People[1];
        string beforeA = Identity(f.World, a), beforeB = Identity(f.World, b);
        long high = f.World.NextResidentId;
        WorldNavigation navigation = new WorldNavigation(f.World, f.Commands, f.Executor);
        Require(navigation.FollowResident(a).Accepted, "follow resident before household changes");
        Require(f.Housing.FormHousehold(Core, "New household", new[] { a }, f.Target).Accepted, "form household");
        HouseholdId formed = Person(f.World, a).Household;
        Check(formed != f.Household && Assigned(f.World, a, formed, f.Target)
            && Assigned(f.World, b, f.Household, f.Source), "SEIT-WORLD-02-P2-form-updates-both-memberships");
        Require(f.Housing.JoinHousehold(Core, b, formed).Accepted, "join household");
        Check(Assigned(f.World, b, formed, f.Target), "SEIT-WORLD-02-P2-join-updates-membership");
        Require(f.Housing.LeaveHousehold(Core, b, f.Source).Accepted, "leave household");
        Check(Person(f.World, b).Household != formed
            && Assigned(f.World, b, Person(f.World, b).Household, f.Source), "SEIT-WORLD-02-P2-leave-updates-membership");
        for (int i = 0; i < 5; i++)
        {
            HomeId destination = i % 2 == 0 ? f.Source : f.Target;
            Require(f.Housing.MoveHousehold(Core, formed, destination).Accepted, "repeat move " + i);
            Check(Assigned(f.World, a, formed, destination), "SEIT-WORLD-02-P1-move-" + i + "-assignment");
        }
        navigation.Refresh();
        Check(Identity(f.World, a) == beforeA && Identity(f.World, b) == beforeB
            && f.World.NextResidentId == high && f.World.ResidentCount == 2,
            "SEIT-WORLD-02-P1-P2-N3-identities-relationships-history-no-replacement");
        Check(navigation.FollowedResident == a && navigation.SearchByName(Person(f.World, a).DisplayName)[0].Home
            == Person(f.World, a).Home, "SEIT-IF-RESOLVE-N8-local-follow-id-and-live-home");
    }

    private static void Capacity()
    {
        Fixture f = NewFixture(3);
        string before = Snapshot(f);
        Check(!f.Housing.MoveHousehold(Core, f.Household, f.Target).Accepted && Snapshot(f) == before
            && Has(f.Housing.Message, "capacity"), "SEIT-WORLD-02-N1-full-bed-move-refused-unchanged");
        Check(!f.Housing.FormHousehold(Core, "Too large", f.People, f.Target).Accepted
            && Snapshot(f) == before, "SEIT-WORLD-02-N1-overcapacity-formation-atomic");

        Fixture children = NewFixture(4);
        children.World.SetStage(Core, children.People[2], LifeStage.Toddler, 0.0f);
        children.World.SetStage(Core, children.People[3], LifeStage.Toddler, 0.0f);
        children.Housing.SetCaregiver(Core, children.People[0], true);
        Check(children.Housing.GetOffer(children.Household, children.Target, children.Shared).Fits
            && children.Housing.MoveHousehold(Core, children.Household, children.Target).Accepted,
            "SEIT-WORLD-07-N3-two-adults-two-toddlers-fit-two-beds");

        Fixture reserved = NewFixture(3);
        reserved.World.SetStage(Core, reserved.People[2], LifeStage.Toddler, 0.0f);
        reserved.Housing.SetCaregiver(Core, reserved.People[0], true);
        reserved.Housing.SetNewbornReservations(Core, reserved.Household, 1);
        Require(reserved.Housing.MoveHousehold(Core, reserved.Household, reserved.Target).Accepted,
            "toddler plus newborn reservation fits caregiver bed");
        HouseholdId visiting = reserved.World.AllocateHousehold(Core, "Visitor", reserved.Source).Id;
        ResidentId infant = reserved.World.AllocateResident(Core, "Infant", LifeStage.Infant, visiting, reserved.Source).Id;
        before = Snapshot(reserved);
        Check(!reserved.Housing.JoinHousehold(Core, infant, reserved.Household).Accepted
            && Snapshot(reserved) == before && Has(reserved.Housing.Message, "capacity"),
            "SEIT-WORLD-02-N2-reserved-newborn-counted-refusal-atomic");
        reserved.Housing.SetNewbornReservations(Core, reserved.Household, 0);
        Check(reserved.Housing.JoinHousehold(Core, infant, reserved.Household).Accepted
            && Assigned(reserved.World, infant, reserved.Household, reserved.Target),
            "SEIT-WORLD-02-N2-without-reservation-infant-fits");
    }

    private static void HomePurchases(bool negative)
    {
        foreach (long price in new[] { 2500L, 4000L })
        {
            foreach (long funds in new[] { price - 1L, price })
            {
                Fixture f = NewFixture(2, funds, price == 2500L ? 2 : 6, price);
                f.World.SetMoney(Core, f.People[1], 100000L);
                var offer = f.Housing.GetOffer(f.Household, f.Target, f.Shared);
                Check(offer.Price == price && offer.Fits && offer.Shortfall == price - funds,
                    "SEIT-WORLD-07-N4-listed-price-" + price + "-funds-" + funds);
                ActionInstanceId action = f.World.AllocateAction(Core);
                string before = Snapshot(f);
                if (negative && price == 2500L && funds < price)
                    f.Accounts.SetSharedBalance(Core, f.Household, price);
                CommandResult result = f.Housing.BuyHome(Core, action, f.Household, f.Target, f.Shared);
                if (funds < price)
                {
                    Check(!result.Accepted && result.Reason == RejectReason.InsufficientFunds
                        && Snapshot(f) == before && f.Ownership.GetAgreement(f.Target) == null
                        && Has(offer.Reason, price.ToString()) && Has(offer.Reason, "1")
                        && NextStep(offer.NextSteps),
                        "SEIT-WORLD-07-N4-fabricated-funds-refused-price-" + price);
                }
                else
                {
                    Check(result.Accepted && f.Accounts.Balance(f.Shared) == 0L && f.Accounts.Ledger.Count == 1
                        && f.Accounts.Ledger[0].Amount == price && f.Accounts.Ledger[0].Source.Equals(f.Shared)
                        && Assigned(f.World, f.People[0], f.Household, f.Target)
                        && Assigned(f.World, f.People[1], f.Household, f.Target)
                        && f.Ownership.GetAgreement(f.Target) != null
                        && f.Ownership.GetAgreement(f.Target).Shares.Count == 1
                        && f.Ownership.GetAgreement(f.Target).Shares[0].Household == f.Household
                        && f.Ownership.GetAgreement(f.Target).Shares[0].Percent == 100,
                        "SEIT-AC-08-local-buy-" + price + "-one-exact-debit-and-title");
                    before = Snapshot(f);
                    f.Housing.BuyHome(Core, action, f.Household, f.Target, f.Shared);
                    Check(Snapshot(f) == before, "SEIT-AC-08-local-buy-" + price + "-retry-no-second-debit");
                }
                Check(Person(f.World, f.People[1]).Money == 100000L,
                    "SEIT-WORLD-07-N4-no-unselected-personal-funding-" + price + "-" + funds);
            }
        }
    }

    private static void Scarcity()
    {
        Fixture f = NewFixture(5, 3000L);
        HomeId large = f.World.AllocateHome(Core, 6).Id;
        f.Housing.RegisterHome(Core, large, 4000L);
        var smallOffer = f.Housing.GetOffer(f.Household, f.Target, f.Shared);
        var largeOffer = f.Housing.GetOffer(f.Household, large, f.Shared);
        Check(smallOffer.Price == 2500L && !smallOffer.Fits && Has(smallOffer.Reason, "capacity")
            && Has(smallOffer.Reason, "5") && Has(smallOffer.Reason, "2")
            && NextStep(smallOffer.NextSteps)
            && largeOffer.Price == 4000L && largeOffer.Fits && largeOffer.Shortfall == 1000L
            && Has(largeOffer.Reason, "4000") && Has(largeOffer.Reason, "1000")
            && NextStep(largeOffer.NextSteps),
            "SEIT-WORLD-07-P1-local-exact-cost-capacity-and-next-steps");
        int homes = f.World.ExportSnapshot().Homes.Count;
        string identity = Identity(f.World, f.People[0]);
        Advance(f, 10L * Day);
        Check(f.World.ExportSnapshot().Homes.Count == homes && f.Accounts.Balance(f.Shared) == 3000L
            && f.Accounts.Ledger.Count == 0 && Assigned(f.World, f.People[0], f.Household, f.Source),
            "SEIT-WORLD-07-N1-no-automatic-home-money-or-move");
        Check(Identity(f.World, f.People[0]) == identity,
            "SEIT-WORLD-07-N2-local-housing-tick-adds-no-crowding-penalty");
    }

    private static ObjectId BuySofa(Fixture f)
    {
        ObjectId id;
        Require(f.Housing.BuyFurniture(Core, f.World.AllocateAction(Core), f.Household, "sofa",
            f.Source, new ApproachPose(0.0f, 0.0f, 0.0f), f.Shared, out id).Accepted, "buy sofa");
        return id;
    }

    private static ExecutionRecord Use(Fixture f, ObjectId id)
    {
        ResidentRecord r = Person(f.World, f.People[0]);
        Require(f.Commands.Submit(new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId("ACT-HEALTH-02"), (long)id,
            new CommandParameters.Direct(QueueMode.Immediate))).Accepted, "direct sofa use");
        f.Executor.Tick(Exec);
        f.Executor.Tick(Exec);
        ExecutionRecord execution = f.Executor.GetResidentStatus(r.Id).Execution;
        Require(execution != null && execution.Stage == ExecutionStage.Perform
            && execution.Holds.Length == 1, "real NAV performing through furniture slot");
        return execution;
    }

    private static bool Registered(ActivityExecutor executor, ObjectId id)
    {
        try { return executor.IsReachable(id); }
        catch (ArgumentException) { return false; }
    }

    private static void Furnishing()
    {
        Fixture f = NewFixture(2, 100L);
        ObjectId id = BuySofa(f);
        var item = f.Housing.GetItem(id);
        Check(item != null && item.Id == id && item.Home == f.Source && item.Owner.Equals(f.Shared)
            && !item.InInventory && item.PurchasePrice == 75L && item.ResaleValue == 37L
            && f.Accounts.Balance(f.Shared) == 25L && f.Accounts.Ledger.Count == 1
            && f.Accounts.Ledger[0].Amount == 75L && f.Accounts.Ledger[0].Source.Equals(f.Shared)
            && Registered(f.Executor, id), "SEIT-WORLD-04-P1-local-furnish-price-owner-slot-and-one-debit");
        ExecutionRecord use = Use(f, id);
        Require(f.Housing.MoveFurniture(Core, id, f.Source, new ApproachPose(1.0f, 0.0f, 0.0f)).Accepted,
            "move owned sofa");
        Check(use.Outcome == TerminalOutcome.Failed && use.BlockedReason == RejectReason.TargetInvalidated
            && use.Holds.Length == 0 && !f.Executor.GetHold(id, 0).HasValue && Registered(f.Executor, id),
            "SEIT-WORLD-04-P4-local-move-invalidates-and-releases-slot");
        long next = f.World.NextObjectId;
        Require(f.Housing.StoreFurniture(Core, id).Accepted, "store sofa");
        Check(f.Housing.GetItem(id).InInventory && !Registered(f.Executor, id),
            "SEIT-WORLD-04-P2-store-keeps-identity-and-unregisters-slot");
        Require(f.Housing.PlaceFurniture(Core, id, f.Source, new ApproachPose(0.0f, 0.0f, 0.0f)).Accepted,
            "restore sofa from inventory");
        Check(f.Housing.GetItem(id).Id == id && !f.Housing.GetItem(id).InInventory
            && f.World.NextObjectId == next && f.Accounts.Balance(f.Shared) == 25L
            && f.Accounts.Ledger.Count == 1 && Registered(f.Executor, id),
            "SEIT-WORLD-04-P2-inventory-moves-never-repurchase-or-credit");
        use = Use(f, id);
        ActionInstanceId sale = f.World.AllocateAction(Core);
        Require(f.Housing.SellFurniture(Core, sale, id).Accepted, "explicit sale");
        Check(f.Housing.GetItem(id) == null && !Registered(f.Executor, id)
            && use.BlockedReason == RejectReason.TargetInvalidated && use.Holds.Length == 0
            && f.Accounts.Balance(f.Shared) == 62L && f.Accounts.Ledger.Count == 2
            && f.Accounts.Ledger[1].Amount == 37L && f.Accounts.Ledger[1].Destination.Equals(f.Shared),
            "SEIT-WORLD-04-P3-P4-sale-floor-37-removes-object-and-holds");
        string before = Snapshot(f);
        f.Housing.SellFurniture(Core, sale, id);
        Check(Snapshot(f) == before, "SEIT-WORLD-04-P3-sale-credits-only-once");

        Fixture poor = NewFixture(2, 40L);
        ActionInstanceId buy = poor.World.AllocateAction(Core);
        before = Snapshot(poor);
        ObjectId refused;
        CommandResult result = poor.Housing.BuyFurniture(Core, buy, poor.Household, "desk", poor.Source,
            new ApproachPose(0.0f, 0.0f, 0.0f), poor.Shared, out refused);
        Check(!result.Accepted && result.Reason == RejectReason.InsufficientFunds
            && Snapshot(poor) == before && Has(poor.Housing.Message, "InsufficientFunds"),
            "SEIT-WORLD-04-N1-60-from-40-no-object-no-debit-visible-reason");
    }

    private static void Vacancy()
    {
        Fixture f = NewFixture();
        Require(f.Housing.MoveHousehold(Core, f.Household, f.Target).Accepted, "vacate source");
        f.Housing.Tick(Core);
        string a = Identity(f.World, f.People[0]), b = Identity(f.World, f.People[1]);
        long high = f.World.NextResidentId;
        Advance(f, 2L * Day);
        Check(f.World.ResidentCount == 2, "SEIT-WORLD-03-N2-world-younger-than-three-days-no-arrival");
        Advance(f, 3L * Day - 6L);
        Check(f.World.ResidentCount == 2, "SEIT-WORLD-03-N2-world-age-boundary-no-arrival");
        Advance(f, 4L * Day);
        ResidentRecord arrival = null;
        foreach (ResidentRecord r in f.World.ResidentsSnapshot())
            if ((long)r.Id >= high && r.Home == f.Source) arrival = r;
        AppearanceRecord appearance;
        Check(f.World.ResidentCount == 3 && arrival != null
            && Assigned(f.World, arrival.Id, arrival.Household, f.Source)
            && Array.IndexOf(WorldState.DefaultNames, arrival.DisplayName) >= 0
            && f.Appearances.TryGet(arrival.Id, out appearance)
            && ResidentFactory.IsValidHairStyle(appearance.HairType, appearance.HairStyle)
            && Identity(f.World, f.People[0]) == a && Identity(f.World, f.People[1]) == b,
            "SEIT-WORLD-03-P1-N1-new-vacancy-id-name-presets-prior-records-intact");
        Advance(f, 8L * Day);
        Check(f.World.ResidentCount == 3 && f.World.NextResidentId == high + 1L,
            "SEIT-WORLD-03-N3-one-arrival-per-vacancy");

        Fixture youngVacancy = NewFixture();
        youngVacancy.Clock.AdvanceRealSeconds(Core, (double)(3L * Day) / 60.0);
        Require(youngVacancy.World.GameSeconds == 3L * Day, "prepared day-three world");
        Require(youngVacancy.Housing.MoveHousehold(Core, youngVacancy.Household, youngVacancy.Target).Accepted,
            "vacate at world day three");
        youngVacancy.Housing.Tick(Core);
        int count = youngVacancy.World.ResidentCount;
        Advance(youngVacancy, 5L * Day - 6L);
        Check(youngVacancy.World.ResidentCount == count, "SEIT-WORLD-03-N2-vacancy-under-two-days-no-arrival");
    }

    private static void ArrivalLimitsAndHistory()
    {
        Fixture f = NewFixture();
        HomeId annex = f.World.AllocateHome(Core, 10).Id;
        HouseholdId household = f.World.AllocateHousehold(Core, "Populated annex", annex).Id;
        f.Housing.RegisterHome(Core, annex, 0L);
        for (int i = 0; i < 22; i++)
        {
            ResidentId id = f.World.AllocateResident(Core, "Neighbour " + i,
                i < 10 ? LifeStage.Adult : LifeStage.Toddler, household, annex).Id;
            if (i < 6) f.Housing.SetCaregiver(Core, id, true);
        }
        Require(f.World.ResidentCount == 24, "SEIT population boundary fixture");
        Require(f.Housing.MoveHousehold(Core, f.Household, f.Target).Accepted, "vacancy at population bound");
        f.Housing.Tick(Core);
        long high = f.World.NextResidentId;
        Advance(f, 4L * Day);
        Check(f.World.ResidentCount == 24 && f.World.NextResidentId == high,
            "SEIT-WORLD-03-N2-population-at-max-24-total-beds-no-arrival");

        Fixture two = NewFixture();
        HomeId third = two.World.AllocateHome(Core, 3).Id;
        two.Housing.RegisterHome(Core, third, 0L);
        HouseholdId departedHousehold = two.World.AllocateHousehold(Core, "Departed", two.Target).Id;
        ResidentId departed = two.World.AllocateResident(Core, "Departed", LifeStage.Adult,
            departedHousehold, two.Target).Id;
        ResidentId deceased = two.World.AllocateResident(Core, "Deceased", LifeStage.Elder,
            two.Household, two.Source).Id;
        two.World.SetAlive(Core, deceased, false);
        Require(two.Housing.MoveHousehold(Core, two.Household, third).Accepted, "first vacancy");
        Require(two.Housing.MoveHousehold(Core, departedHousehold, third).Accepted, "second vacancy");
        string dead = Identity(two.World, deceased), moved = Identity(two.World, departed);
        two.Housing.Tick(Core);
        high = two.World.NextResidentId;
        Advance(two, 4L * Day);
        int sourceArrivals = 0, targetArrivals = 0;
        foreach (ResidentRecord r in two.World.ResidentsSnapshot())
        {
            if ((long)r.Id < high) continue;
            if (r.Home == two.Source) sourceArrivals++;
            if (r.Home == two.Target) targetArrivals++;
        }
        Check(sourceArrivals == 1 && targetArrivals == 1 && two.World.ResidentCount == 6
            && Identity(two.World, deceased) == dead && Identity(two.World, departed) == moved,
            "SEIT-WORLD-03-N1-N3-two-vacancies-one-each-no-deceased-or-departed-overwrite");
        Advance(two, 8L * Day);
        Check(two.World.ResidentCount == 6 && two.World.NextResidentId == high + 2L,
            "SEIT-WORLD-03-N3-no-second-arrival-in-filled-homes");
    }

    private static void Repeatability()
    {
        string expected = null;
        for (int entry = 0; entry < 3; entry++)
        {
            Fixture f = NewFixture();
            ObjectId sofa = BuySofa(f);
            Require(f.Housing.StoreFurniture(Core, sofa).Accepted, "repeat entry store");
            Require(f.Housing.MoveHousehold(Core, f.Household, f.Target).Accepted, "repeat entry move");
            f.Housing.Tick(Core);
            Advance(f, 4L * Day);
            string observed = Snapshot(f) + Json(f.Appearances.Export()) + Json(f.Housing.GetItem(sofa));
            Check(f.World.ResidentCount == 3 && f.Accounts.Balance(f.Shared) == 4925L
                && (expected == null || observed == expected),
                "HOUSING-seed-4242-repeated-entry-" + entry);
            expected = observed;
        }
    }

    private static void SaveRoundTrip()
    {
        Fixture f = NewFixture(3, 100L);
        f.World.SetStage(Core, f.People[2], LifeStage.Toddler, 0.0f);
        f.Housing.SetCaregiver(Core, f.People[0], true);
        f.Housing.SetNewbornReservations(Core, f.Household, 1);
        ObjectId id = BuySofa(f);
        Require(f.Housing.StoreFurniture(Core, id).Accepted, "store before save");
        Require(f.Housing.MoveHousehold(Core, f.Household, f.Target).Accepted, "move before save");
        f.Housing.Tick(Core);
        Advance(f, Day);
        string identity = Identity(f.World, f.People[0]);
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-housing-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveWriteResult saved = SaveV1.SaveGame(f.Stack, directory);
            Require(saved.Ok, "public save: " + saved.Detail);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok && loaded.Game != null, "public load: " + loaded.Notice);
            var g = loaded.Game;
            Require(g.Housing != null, "HOUSING restored service");
            Check(!object.ReferenceEquals(g.World, f.World)
                && Identity(g.World, f.People[0]) == identity
                && Assigned(g.World, f.People[0], f.Household, f.Target)
                && g.Housing.GetItem(id).Id == id && g.Housing.GetItem(id).InInventory
                && g.Housing.GetItem(id).Owner.Equals(f.Shared)
                && g.Accounts.Balance(f.Shared) == 25L && g.Accounts.Ledger.Count == 1,
                "SEIT-WORLD-02-P3-WORLD-04-P2-local-roundtrip-household-identity-inventory-money");
            Require(g.Housing.PlaceFurniture(Core, id, f.Target, new ApproachPose(0.0f, 0.0f, 0.0f)).Accepted,
                "place restored inventory");
            Fixture restored = new Fixture { World = g.World, Clock = g.Clock, Commands = g.Arbitrator,
                Executor = g.Executor, Accounts = g.Accounts, Housing = g.Housing, People = f.People };
            ExecutionRecord use = Use(restored, id);
            Check(use.Holds.Length == 1 && Registered(g.Executor, id)
                && g.Accounts.Balance(f.Shared) == 25L && g.Accounts.Ledger.Count == 1,
                "SEIT-WORLD-04-N4-local-restored-item-usable-without-recharge");
            restored.Housing.Tick(Core);
            Advance(restored, 3L * Day - 6L);
            Check(g.World.ResidentCount == 3, "SEIT-WORLD-03-N2-local-reload-keeps-world-age-boundary");
            Advance(restored, 4L * Day);
            int arrivals = 0;
            foreach (ResidentRecord r in g.World.ResidentsSnapshot())
                if (r.Home == f.Source && (long)r.Id >= f.World.NextResidentId) arrivals++;
            Check(g.World.ResidentCount == 4 && arrivals == 1,
                "SEIT-WORLD-02-P3-WORLD-03-P1-local-vacancy-timer-survives-reload");
            HomeId visitHome = g.World.AllocateHome(Core, 1).Id;
            HouseholdId visitor = g.World.AllocateHousehold(Core, "Visitor", visitHome).Id;
            ResidentId infant = g.World.AllocateResident(Core, "Infant", LifeStage.Infant, visitor, visitHome).Id;
            string before = Json(g.World.ExportSnapshot());
            Check(!g.Housing.JoinHousehold(Core, infant, f.Household).Accepted
                && Json(g.World.ExportSnapshot()) == before,
                "SEIT-WORLD-02-N2-local-reservation-survives-public-codec");
            g.Housing.SetNewbornReservations(Core, f.Household, 0);
            Check(g.Housing.JoinHousehold(Core, infant, f.Household).Accepted,
                "SEIT-WORLD-02-N2-local-caregiver-survives-public-codec");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public static int Main(string[] args)
    {
        failures = 0;
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negative) Console.WriteLine("HOUSING negative-fixture: fabricate the missing affordability unit");
        Run("form-join-leave-move", FormJoinLeaveMove);
        Run("capacity-and-reservations", Capacity);
        Run("buy-home", () => HomePurchases(negative));
        Run("readable-scarcity", Scarcity);
        Run("furnishing", Furnishing);
        Run("vacancy", Vacancy);
        Run("arrival-limits-and-history", ArrivalLimitsAndHistory);
        Run("repeated-entry", Repeatability);
        Run("public-save-roundtrip", SaveRoundTrip);
        Console.WriteLine(failures == 0 ? "HOUSING ALL PASS" : "HOUSING FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
