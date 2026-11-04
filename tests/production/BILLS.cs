/*
BILLS@1 TDD contract, real STATE/CLOCK/ECONOMY/SAVE-V2 dependencies.
New product API in Blockparty.Simulation (Expenses.cs):
  Expenses(WorldState world, Accounts accounts)
  ConfigureHome(StateWriter writer, HomeId home, bool rented, AccountRef[] funding)
  Tick(StateWriter writer) -- process rollovers and retry after funding changes
  ExpenseDue AddDue(StateWriter writer, HomeId home, string kind, long amount,
                    long dueGameSeconds, bool divisible)
  IReadOnlyList<ExpenseDue> Dues -- retains paid and DueUnpaid records
ExpenseDue readable fields/properties: long DueId, DueGameSeconds, Amount, Paid,
Shortfall; HomeId Home; string Kind, State ("Paid" or "DueUnpaid").
AddDue supplies the explicit expense definitions required by SEIT-ECO-02-N3;
weekly Utilities/HousingCharge are indivisible per the N2 witness.
Save API: GameStack's economy overload also accepts Expenses as its final
argument; LoadedGame.Expenses restores the service bound to the loaded accounts.
Tests call Tick after real clock advances and account credits, like a host loop.

Oracles: SDoc BP-SEC-05 OWNER-BILLS-20261027, BP-ECO-02, SEIT-ECO-02-P1/N1/N2/
N3/N4. AC-05/ECO-02-P2 cover only the local expense persistence subclaim;
AC-08/WORLD-07-N1/N4 cover only no automatic housing/subsidy under shortfall.
WORLD-07 UI/capacity/purchase, notifications, progression and process restart
remain at their recorded completion gates; traceability is not whole-case PASS.
New module follows the package runner convention; no existing bill suite exists.
Resource group isolated-csharp; no Unity editor, ports, network or credentials.
Only save checks use a unique temporary directory, removed on every path.
"negative-fixture" injects a real second debit after reload; the same no-double-
payment assertion must print FAIL and the program must exit 1.
*/
using System;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class BillsChecks
{
    private const StateWriter Core = StateWriter.SimCore;
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
        public Accounts Accounts;
        public Expenses Bills;
        public GameStack Stack;
        public HomeId Home;
        public HouseholdId Household;
        public ResidentRecord[] Residents;
        public AccountRef Shared { get { return AccountRef.Shared(Household); } }
    }

    private static Fixture NewFixture(int residents, bool rented, long funds)
    {
        WorldState world = WorldState.CreateSeeded(701L, 4242L, residents, null);
        ResidentRecord[] people = world.ResidentsSnapshot();
        Clock clock = new Clock(world);
        clock.SetPreset(AgingPreset.Off);
        Accounts accounts = new Accounts(world);
        accounts.SetSharedBalance(Core, people[0].Household, funds);
        foreach (ResidentRecord person in people) world.SetMoney(Core, person.Id, 100L);
        Expenses bills = new Expenses(world, accounts);
        bills.ConfigureHome(Core, people[0].Home, rented,
            new[] { AccountRef.Shared(people[0].Household) });
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        ActivityExecutor executor = new ActivityExecutor(world, arbitrator, clock);
        Food food = new Food(world, arbitrator, executor, accounts);
        return new Fixture { World = world, Clock = clock, Accounts = accounts, Bills = bills,
            Home = people[0].Home, Household = people[0].Household, Residents = people,
            Stack = new GameStack(world, clock, arbitrator, executor, new AppearanceStore(),
                accounts, food, bills) };
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static void AdvanceTo(Fixture f, long seconds)
    {
        Require(seconds >= f.World.GameSeconds, "clock fixture cannot run backwards");
        f.Clock.AdvanceRealSeconds(Core, (double)(seconds - f.World.GameSeconds) / 60.0);
        Require(f.World.GameSeconds == seconds, "real CLOCK reached requested boundary");
        f.Bills.Tick(Core);
    }

    private static ExpenseDue Due(Expenses bills, HomeId home, string kind, long date)
    {
        ExpenseDue found = null;
        foreach (ExpenseDue due in bills.Dues)
        {
            if (due.Home != home || due.Kind != kind || due.DueGameSeconds != date) continue;
            Require(found == null, "duplicate due for home/kind/date");
            found = due;
        }
        Require(found != null, "missing " + kind + " at " + date);
        return found;
    }

    private static bool Payment(Accounts accounts, int index, AccountRef source, string kind,
        long amount, long date)
    {
        if (accounts.Ledger.Count <= index) return false;
        AccountTransaction row = accounts.Ledger[index];
        return row.Source.Equals(source) && row.Destination.Equals(AccountRef.Expense(kind))
            && row.Amount == amount && row.GameSeconds == date;
    }

    private static bool Status(ExpenseDue due, long amount, long paid, long shortfall, string state)
    {
        return due.Amount == amount && due.Paid == paid && due.Shortfall == shortfall && due.State == state;
    }

    private static void WeeklySchedule()
    {
        Fixture f = NewFixture(3, true, 500L);
        HomeRecord owned = f.World.AllocateHome(Core, 4);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Owned", owned.Id);
        for (int i = 0; i < 4; i++)
            f.World.AllocateResident(Core, "Owner " + i, LifeStage.Adult, household.Id, owned.Id);
        AccountRef ownedFunds = AccountRef.Shared(household.Id);
        f.Accounts.SetSharedBalance(Core, household.Id, 200L);
        f.Bills.ConfigureHome(Core, owned.Id, false, new[] { ownedFunds });
        f.Bills.Tick(Core);
        Check(f.Bills.Dues.Count == 0, "SEIT-ECO-02-P1-no-day-zero-bill");
        for (int day = 1; day <= 14; day++)
        {
            AdvanceTo(f, day * Day - 6L);
            int weeksBefore = (day - 1) / 7;
            Check(f.Bills.Dues.Count == weeksBefore * 3,
                "SEIT-ECO-02-N4-no-early-or-off-schedule-post-day-" + day);
            AdvanceTo(f, day * Day);
            f.Bills.Tick(Core);
            int weeks = day / 7;
            Check(f.Bills.Dues.Count == weeks * 3 && f.Accounts.Ledger.Count == weeks * 3
                && f.Accounts.Balance(f.Shared) == 500L - weeks * 120L
                && f.Accounts.Balance(ownedFunds) == 200L - weeks * 50L,
                "SEIT-ECO-02-P1-N4-weekly-once-day-" + day);
        }
        foreach (long date in new[] { 7L * Day, 14L * Day })
        {
            Check(Status(Due(f.Bills, f.Home, "Utilities", date), 45L, 45L, 0L, "Paid")
                && Status(Due(f.Bills, f.Home, "HousingCharge", date), 75L, 75L, 0L, "Paid")
                && Status(Due(f.Bills, owned.Id, "Utilities", date), 50L, 50L, 0L, "Paid"),
                "SEIT-ECO-02-P1-independent-amount-oracles-" + date);
        }
        Check(f.Accounts.Balance(AccountRef.Personal(f.Residents[0].Id)) == 100L,
            "SEIT-ECO-02-P1-only-configured-funding-spent");
    }

    private static void VacantAndOwned()
    {
        Fixture f = NewFixture(2, true, 200L);
        HomeRecord owned = f.World.AllocateHome(Core, 2);
        f.Bills.ConfigureHome(Core, owned.Id, false, new[] { f.Shared });
        foreach (ResidentRecord resident in f.Residents)
            f.World.SetHousehold(Core, resident.Id, resident.Household, owned.Id);
        for (int day = 1; day <= 13; day++) AdvanceTo(f, day * Day);
        Check(f.Bills.Dues.Count == 1 && f.Accounts.Ledger.Count == 1
            && Status(Due(f.Bills, owned.Id, "Utilities", 7L * Day), 40L, 40L, 0L, "Paid")
            && f.Accounts.Balance(f.Shared) == 160L,
            "SEIT-ECO-02-N4-vacated-rental-no-charge-owned-no-rent");
    }

    private static void Shortfall()
    {
        Fixture f = NewFixture(3, false, 20L);
        f.World.SetMoney(Core, f.Residents[0].Id, 30L);
        WorldSnapshot before = f.World.ExportSnapshot();
        AdvanceTo(f, 7L * Day);
        ExpenseDue due = Due(f.Bills, f.Home, "Utilities", 7L * Day);
        Check(Status(due, 45L, 0L, 25L, "DueUnpaid") && f.Accounts.Balance(f.Shared) == 20L
            && f.Accounts.Ledger.Count == 0,
            "SEIT-ECO-02-N1-exact-shortfall-no-partial-payment-no-debt");
        CommandResult groceries = f.Accounts.Transfer(Core, f.World.AllocateAction(Core),
            AccountRef.Personal(f.Residents[0].Id), AccountRef.Expense("Groceries"), 25L);
        f.Bills.Tick(Core);
        Check(groceries.Accepted && f.Accounts.Balance(AccountRef.Personal(f.Residents[0].Id)) == 5L
            && f.Accounts.Balance(f.Shared) == 20L && f.Accounts.Ledger.Count == 1
            && Status(due, 45L, 0L, 25L, "DueUnpaid"),
            "SEIT-ECO-02-N1-unrelated-25-unit-payment-not-blocked-or-pooled");
        AdvanceTo(f, 10L * Day);
        WorldSnapshot after = f.World.ExportSnapshot();
        bool sameHomes = before.Homes.Count == after.Homes.Count
            && before.Households.Count == after.Households.Count;
        foreach (ResidentRecord resident in f.World.ResidentsSnapshot())
            sameHomes &= resident.Home == f.Home && resident.Household == f.Household;
        Check(sameHomes && f.World.TownFund == before.TownFund
            && f.Accounts.Balance(f.Shared) == 20L && due.Paid == 0L,
            "SEIT-WORLD-07-N1-N4-AC-08-no-home-or-subsidy-created-by-shortfall");
    }

    private static void Fund(Fixture f, long amount)
    {
        Require(f.Accounts.Transfer(Core, f.World.AllocateAction(Core),
            AccountRef.Personal(f.Residents[0].Id), f.Shared, amount).Accepted, "real funding transfer");
        f.Bills.Tick(Core);
    }

    private static void SimultaneousRetry()
    {
        Fixture f = NewFixture(2, true, 80L);
        AdvanceTo(f, 7L * Day);
        ExpenseDue rent = Due(f.Bills, f.Home, "HousingCharge", 7L * Day);
        long rentId = rent.DueId;
        Check(Status(Due(f.Bills, f.Home, "Utilities", 7L * Day), 40L, 40L, 0L, "Paid")
            && Status(rent, 75L, 0L, 35L, "DueUnpaid") && f.Accounts.Balance(f.Shared) == 40L
            && f.Accounts.Ledger.Count == 1 && Payment(f.Accounts, 0, f.Shared, "Utilities", 40L, 7L * Day),
            "SEIT-ECO-02-N2-80-funds-utilities-before-rent-witness");
        Fund(f, 35L);
        f.Bills.Tick(Core);
        Check(rent.DueId == rentId && Status(rent, 75L, 75L, 0L, "Paid")
            && f.Accounts.Balance(f.Shared) == 0L && f.Accounts.Ledger.Count == 3
            && Payment(f.Accounts, 2, f.Shared, "HousingCharge", 75L, 7L * Day)
            && f.Accounts.Balance(AccountRef.Personal(f.Residents[0].Id)) == 65L,
            "SEIT-ECO-02-N2-credit-35-clears-same-rent-once-no-invented-funds");
    }

    private static void RetryOrderAndDivisibility()
    {
        Fixture f = NewFixture(2, false, 0L);
        AdvanceTo(f, 2L * Day);
        ExpenseDue newer = f.Bills.AddDue(Core, f.Home, "Utilities", 40L, 2L * Day, false);
        ExpenseDue older = f.Bills.AddDue(Core, f.Home, "HousingCharge", 40L, Day, false);
        Fund(f, 40L);
        Check(Status(older, 40L, 40L, 0L, "Paid") && Status(newer, 40L, 0L, 40L, "DueUnpaid")
            && Payment(f.Accounts, 1, f.Shared, "HousingCharge", 40L, 2L * Day),
            "SEIT-ECO-02-N2-due-date-precedes-kind-and-insertion-order");

        Fixture tie = NewFixture(2, false, 0L);
        AdvanceTo(tie, Day);
        ExpenseDue rent = tie.Bills.AddDue(Core, tie.Home, "HousingCharge", 40L, Day, false);
        ExpenseDue first = tie.Bills.AddDue(Core, tie.Home, "Utilities", 40L, Day, false);
        ExpenseDue second = tie.Bills.AddDue(Core, tie.Home, "Utilities", 40L, Day, false);
        Fund(tie, 40L);
        Check(first.DueId < second.DueId && first.Paid == 40L && second.Paid == 0L && rent.Paid == 0L,
            "SEIT-ECO-02-N2-same-date-utilities-before-housing-then-ascending-due-id");

        foreach (bool divisible in new[] { false, true })
        {
            Fixture partial = NewFixture(2, false, 20L);
            AdvanceTo(partial, Day);
            ExpenseDue due = partial.Bills.AddDue(Core, partial.Home, "Utilities", 40L, Day, divisible);
            partial.Bills.Tick(Core);
            Check(Status(due, 40L, divisible ? 20L : 0L, 20L, "DueUnpaid")
                && partial.Accounts.Balance(partial.Shared) == (divisible ? 0L : 20L)
                && partial.Accounts.Ledger.Count == (divisible ? 1 : 0),
                "SEIT-ECO-02-N3-partial-only-if-defined-divisible-" + divisible);
            long id = due.DueId;
            Fund(partial, 20L);
            Check(due.DueId == id && Status(due, 40L, 40L, 0L, "Paid")
                && partial.Accounts.Balance(partial.Shared) == 0L,
                "SEIT-ECO-02-N3-later-credit-clears-only-remainder-" + divisible);
        }
    }

    private static Fixture Reload(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(f.Stack, directory);
        Require(saved.Ok, "BILLS save through public codec: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
        Require(loaded.Ok && loaded.Game != null, "BILLS load through public codec: " + loaded.Notice);
        LoadedGame g = loaded.Game;
        Require(g.Expenses != null, "BILLS persisted service missing");
        return new Fixture { World = g.World, Clock = g.Clock, Accounts = g.Accounts, Bills = g.Expenses,
            Home = f.Home, Household = f.Household, Residents = g.World.ResidentsSnapshot(),
            Stack = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor, g.Appearances,
                g.Accounts, g.Food, g.Expenses) };
    }

    private static void Persistence(bool negativeFixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-bills-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture f = NewFixture(2, true, 200L);
            AdvanceTo(f, 7L * Day);
            long utilitiesId = Due(f.Bills, f.Home, "Utilities", 7L * Day).DueId;
            long rentId = Due(f.Bills, f.Home, "HousingCharge", 7L * Day).DueId;
            f = Reload(f, Path.Combine(root, "paid"));
            if (negativeFixture)
                Require(f.Accounts.Transfer(Core, f.World.AllocateAction(Core), f.Shared,
                    AccountRef.Expense("Utilities"), 40L).Accepted, "negative fixture second real debit");
            f.Bills.Tick(Core);
            Check(f.Bills.Dues.Count == 2 && f.Accounts.Ledger.Count == 2
                && f.Accounts.Balance(f.Shared) == 85L
                && Due(f.Bills, f.Home, "Utilities", 7L * Day).DueId == utilitiesId
                && Due(f.Bills, f.Home, "HousingCharge", 7L * Day).DueId == rentId,
                "SEIT-AC-05-ECO-02-P2-N2-reload-no-double-post-or-payment");

            Fixture unpaid = NewFixture(2, true, 80L);
            AdvanceTo(unpaid, 7L * Day);
            long unpaidId = Due(unpaid.Bills, unpaid.Home, "HousingCharge", 7L * Day).DueId;
            unpaid = Reload(unpaid, Path.Combine(root, "unpaid"));
            unpaid.Bills.Tick(Core);
            Check(Status(Due(unpaid.Bills, unpaid.Home, "HousingCharge", 7L * Day), 75L, 0L, 35L, "DueUnpaid")
                && unpaid.Accounts.Balance(unpaid.Shared) == 40L && unpaid.Accounts.Ledger.Count == 1,
                "SEIT-ECO-02-N2-reload-retains-unpaid-shortfall-and-sharing");
            Fund(unpaid, 35L);
            unpaid = Reload(unpaid, Path.Combine(root, "cleared"));
            unpaid.Bills.Tick(Core);
            Check(Due(unpaid.Bills, unpaid.Home, "HousingCharge", 7L * Day).DueId == unpaidId
                && Status(Due(unpaid.Bills, unpaid.Home, "HousingCharge", 7L * Day), 75L, 75L, 0L, "Paid")
                && unpaid.Accounts.Balance(unpaid.Shared) == 0L && unpaid.Accounts.Ledger.Count == 3,
                "SEIT-ECO-02-N2-clear-same-due-once-across-second-reload");
            AdvanceTo(unpaid, 14L * Day);
            Check(unpaid.Bills.Dues.Count == 4
                && Due(unpaid.Bills, unpaid.Home, "Utilities", 14L * Day).DueId > unpaidId
                && Due(unpaid.Bills, unpaid.Home, "HousingCharge", 14L * Day).DueId > unpaidId,
                "SEIT-ECO-02-P1-N2-reload-next-week-new-unique-dues");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static int Main(string[] args)
    {
        bool negativeFixture = args.Length == 1 && args[0] == "negative-fixture";
        Run("WeeklySchedule", WeeklySchedule);
        Run("VacantAndOwned", VacantAndOwned);
        Run("Shortfall", Shortfall);
        Run("SimultaneousRetry", SimultaneousRetry);
        Run("RetryOrderAndDivisibility", RetryOrderAndDivisibility);
        Run("Persistence", () => Persistence(negativeFixture));
        Run("RepeatedPlayEntry", () =>
        {
            for (int run = 0; run < 3; run++)
            {
                Fixture fresh = NewFixture(2, true, 200L);
                Check(fresh.Bills.Dues.Count == 0 && fresh.Accounts.Ledger.Count == 0,
                    "BILLS-play-entry-starts-clean-" + run);
                AdvanceTo(fresh, 7L * Day);
                Check(fresh.Bills.Dues.Count == 2 && fresh.Accounts.Ledger.Count == 2
                    && fresh.Accounts.Balance(fresh.Shared) == 85L
                    && Payment(fresh.Accounts, 0, fresh.Shared, "Utilities", 40L, 7L * Day)
                    && Payment(fresh.Accounts, 1, fresh.Shared, "HousingCharge", 75L, 7L * Day),
                    "BILLS-same-seed-same-dated-payments-" + run);
            }
        });
        if (failures == 0) Console.WriteLine("BILLS ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
