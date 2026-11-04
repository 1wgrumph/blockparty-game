/*
ECONOMY@1 red contract: all NEW product types below belong to namespace
Blockparty.Simulation in Assets/Blockparty/Simulation/Accounts.cs (the plan's
product write). No product implementation or replacement dependency is here.

public readonly struct AccountRef : IEquatable<AccountRef>
  static AccountRef Personal(ResidentId resident)
  static AccountRef Shared(HouseholdId household)
  static AccountRef Expense(string destination)
  bool Equals(AccountRef other)
public sealed class AccountTransaction
  readable ActionInstanceId Action; AccountRef Source, Destination;
  readable long Amount, GameSeconds;
public sealed class Accounts
  Accounts(WorldState world)
  void SetSharedBalance(StateWriter writer, HouseholdId household, long amount)
  long Balance(AccountRef account) // personal/shared; personal reads WorldState
  IReadOnlyList<AccountTransaction> Ledger // ordered completed transactions
  CommandResult Transfer(StateWriter writer, ActionInstanceId action,
                         AccountRef source, AccountRef destination, long amount)

SetSharedBalance seeds the fixture, not a transfer. Transfers use integer units,
WorldState's sole-writer API for resident money and world.GameSeconds for dates.
Expense is an external payee, not another resident balance. One action ID applies
at most once, including retries with changed payloads; duplicate result policy is
not prescribed here. Overdraft returns RejectReason.InsufficientFunds and changes
no balance or completed ledger entry. Personal/shared funds never auto-pool.

Oracles: BP-ECO-01 and SEIT-ECO-01-P1/N1/N2; AC-05's duplicate-payment subclaim.
P2 (packaged save/reload inspection) and the rest of AC-05 (catalog activities,
bills, progression, process restart) remain at their recorded completion gates.
These local checks do not claim whole-case acceptance. Separate suite justified
by the ECONOMY package runner convention; existing suites have no account tests.
Resource group: isolated-csharp; no engine, files, ports, or external I/O.
*/
using System;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Simulation;

public sealed class EconomyChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private int failures;

    private sealed class Fixture
    {
        public WorldState World;
        public Accounts Accounts;
        public ResidentRecord[] Residents;
        public HouseholdId Household;
        public AccountRef[] Personal;
        public AccountRef Shared;
    }

    private static Fixture NewFixture(long first = 100L, long shared = 50L)
    {
        WorldState world = WorldState.CreateSeeded(7L, 4242L, 3,
            new string[] { "Contributor", "Recipient", "NonContributor" });
        ResidentRecord[] residents = world.ResidentsSnapshot();
        Accounts accounts = new Accounts(world);
        AccountRef[] personal = new AccountRef[3];
        for (int i = 0; i < residents.Length; i++)
        {
            world.SetMoney(Core, residents[i].Id, i == 0 ? first : (long)(i + 1) * 100L);
            personal[i] = AccountRef.Personal(residents[i].Id);
        }
        HouseholdId household = residents[0].Household;
        accounts.SetSharedBalance(Core, household, shared);
        return new Fixture
        {
            World = world, Accounts = accounts, Residents = residents,
            Household = household, Personal = personal, Shared = AccountRef.Shared(household),
        };
    }

    private void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static long Money(Fixture f, int index)
    {
        ResidentRecord resident;
        if (!f.World.TryGetResident(f.Residents[index].Id, out resident))
            throw new InvalidOperationException("Fixture resident disappeared");
        return resident.Money;
    }

    private static bool Balances(Fixture f, long a, long b, long c, long shared)
    {
        return Money(f, 0) == a && Money(f, 1) == b && Money(f, 2) == c
            && f.Accounts.Balance(f.Personal[0]) == a
            && f.Accounts.Balance(f.Personal[1]) == b
            && f.Accounts.Balance(f.Personal[2]) == c
            && f.Accounts.Balance(f.Shared) == shared;
    }

    private static bool Row(Fixture f, int index, ActionInstanceId action,
        AccountRef source, AccountRef destination, long amount, long date)
    {
        if (f.Accounts.Ledger.Count <= index) return false;
        AccountTransaction row = f.Accounts.Ledger[index];
        return row.Action == action && row.Source.Equals(source)
            && row.Destination.Equals(destination) && row.Amount == amount
            && row.GameSeconds == date;
    }

    // Capture all observable account state plus upstream state; the expected
    // transfer arithmetic below comes from the SEIT fixture, not this digest.
    private static string Snapshot(Fixture f)
    {
        StringBuilder text = new StringBuilder();
        text.Append(f.World.ComputeStateHash()).Append('|');
        for (int i = 0; i < 3; i++)
            text.Append(Money(f, i)).Append(':').Append(f.Accounts.Balance(f.Personal[i])).Append('|');
        text.Append(f.Accounts.Balance(f.Shared)).Append('|');
        for (int i = 0; i < f.Accounts.Ledger.Count; i++)
        {
            AccountTransaction row = f.Accounts.Ledger[i];
            text.Append((long)row.Action).Append(':').Append(row.Amount)
                .Append(':').Append(row.GameSeconds).Append(':');
            for (int j = 0; j < 3; j++)
                text.Append(row.Source.Equals(f.Personal[j])).Append(row.Destination.Equals(f.Personal[j]));
            text.Append(row.Source.Equals(f.Shared)).Append(row.Destination.Equals(f.Shared))
                .Append(row.Destination.Equals(AccountRef.Expense("HouseholdPurchase"))).Append('|');
        }
        return text.ToString();
    }

    private void Transfers()
    {
        Fixture f = NewFixture();
        Check(Balances(f, 100L, 200L, 300L, 50L) && f.Accounts.Ledger.Count == 0,
            "SEIT-ECO-01-P1-distinct-personal-and-shared-balances");
        AccountRef expense = AccountRef.Expense("HouseholdPurchase");
        AccountRef[] sources = { f.Personal[0], f.Shared, f.Personal[0], f.Shared };
        AccountRef[] destinations = { f.Shared, f.Personal[1], f.Personal[2], expense };
        long[] amounts = { 25L, 10L, 15L, 20L };
        long[,] expected = { { 75L, 200L, 300L, 75L }, { 75L, 210L, 300L, 65L },
                            { 60L, 210L, 315L, 65L }, { 60L, 210L, 315L, 45L } };
        string[] names = { "personal-to-shared", "shared-to-personal", "personal-gift", "shared-expense" };
        ActionInstanceId[] actions = new ActionInstanceId[4];
        for (int i = 0; i < 4; i++)
        {
            actions[i] = f.World.AllocateAction(Core);
            long date = (long)(i + 1) * 3600L;
            f.World.SetGameSeconds(Core, date);
            CommandResult result = f.Accounts.Transfer(Core, actions[i], sources[i], destinations[i], amounts[i]);
            Check(result.Accepted && Balances(f, expected[i, 0], expected[i, 1], expected[i, 2], expected[i, 3])
                && f.Accounts.Ledger.Count == i + 1
                && Row(f, i, actions[i], sources[i], destinations[i], amounts[i], date),
                "SEIT-ECO-01-P1-" + names[i] + "-dated-ledger-and-arithmetic");
        }
        bool ledger = f.Accounts.Ledger.Count == 4;
        for (int i = 0; i < 4; i++)
            ledger &= Row(f, i, actions[i], sources[i], destinations[i], amounts[i], (long)(i + 1) * 3600L);
        Check(ledger && Money(f, 0) + Money(f, 1) + Money(f, 2) + f.Accounts.Balance(f.Shared) == 650L - 20L,
            "SEIT-ECO-01-P1-four-ordered-transactions-reconcile");
    }

    private void Overdrafts()
    {
        string[] names = { "personal-to-shared", "shared-to-personal", "personal-gift", "shared-expense" };
        for (int i = 0; i < 4; i++)
        {
            // Fresh real WorldState for every N1 fault, with the specified 30 units.
            Fixture f = NewFixture(30L, 30L);
            AccountRef source = i == 0 || i == 2 ? f.Personal[0] : f.Shared;
            AccountRef destination = i == 0 ? f.Shared : i == 1 ? f.Personal[1]
                : i == 2 ? f.Personal[2] : AccountRef.Expense("HouseholdPurchase");
            ActionInstanceId action = f.World.AllocateAction(Core);
            string before = Snapshot(f);
            CommandResult result = f.Accounts.Transfer(Core, action, source, destination, 31L);
            Check(!result.Accepted && result.Reason == RejectReason.InsufficientFunds
                && Snapshot(f) == before && Balances(f, 30L, 200L, 300L, 30L)
                && f.Accounts.Ledger.Count == 0,
                "SEIT-ECO-01-N1-" + names[i] + "-31-from-30-refused-unchanged");
        }
        Fixture exact = NewFixture(30L, 30L);
        CommandResult personal = exact.Accounts.Transfer(Core, exact.World.AllocateAction(Core),
            exact.Personal[0], exact.Shared, 30L);
        CommandResult shared = exact.Accounts.Transfer(Core, exact.World.AllocateAction(Core),
            exact.Shared, AccountRef.Expense("HouseholdPurchase"), 60L);
        Check(personal.Accepted && shared.Accepted && Balances(exact, 0L, 200L, 300L, 0L)
            && exact.Accounts.Ledger.Count == 2, "SEIT-ECO-01-N1-exact-funds-allowed-no-negative-balance");
    }

    private void NonContributors()
    {
        Fixture f = NewFixture(30L, 0L);
        CommandResult contribution = f.Accounts.Transfer(Core, f.World.AllocateAction(Core),
            f.Personal[0], f.Shared, 30L);
        CommandResult purchase = f.Accounts.Transfer(Core, f.World.AllocateAction(Core),
            f.Shared, AccountRef.Expense("HouseholdPurchase"), 20L);
        Check(contribution.Accepted && purchase.Accepted && Balances(f, 0L, 200L, 300L, 10L)
            && f.Accounts.Ledger.Count == 2
            && f.Accounts.Ledger[1].Source.Equals(f.Shared),
            "SEIT-ECO-01-N2-purchase-only-spends-explicit-contribution");

        Fixture shortfall = NewFixture(30L, 0L);
        ActionInstanceId action = shortfall.World.AllocateAction(Core);
        string before = Snapshot(shortfall);
        CommandResult refused = shortfall.Accounts.Transfer(Core, action,
            shortfall.Shared, AccountRef.Expense("HouseholdPurchase"), 1L);
        Check(!refused.Accepted && refused.Reason == RejectReason.InsufficientFunds
            && Snapshot(shortfall) == before && Balances(shortfall, 30L, 200L, 300L, 0L),
            "SEIT-ECO-01-N2-empty-shared-refuses-without-personal-fallback");
    }

    private void ExactlyOnce()
    {
        for (int i = 0; i < 4; i++)
        {
            Fixture f = NewFixture();
            AccountRef source = i == 0 || i == 2 ? f.Personal[0] : f.Shared;
            AccountRef destination = i == 0 ? f.Shared : i == 1 ? f.Personal[1]
                : i == 2 ? f.Personal[2] : AccountRef.Expense("HouseholdPurchase");
            ActionInstanceId action = f.World.AllocateAction(Core);
            f.World.SetGameSeconds(Core, 3600L);
            CommandResult first = f.Accounts.Transfer(Core, action, source, destination, 10L);
            f.World.SetGameSeconds(Core, 7200L);
            string before = Snapshot(f);
            f.Accounts.Transfer(Core, action, source, destination, 10L);
            f.Accounts.Transfer(Core, action, source, destination, 11L);
            Check(first.Accepted && Snapshot(f) == before && f.Accounts.Ledger.Count == 1
                && Row(f, 0, action, source, destination, 10L, 3600L),
                "SEIT-AC-05-duplicate-payment-" + i.ToString() + "-same-action-applies-once");
        }
    }

    private void Repeatability()
    {
        string expected = null;
        for (int run = 0; run < 3; run++)
        {
            Fixture f = NewFixture();
            bool valid = Balances(f, 100L, 200L, 300L, 50L) && f.Accounts.Ledger.Count == 0;
            f.World.SetGameSeconds(Core, 3600L);
            valid &= f.Accounts.Transfer(Core, f.World.AllocateAction(Core), f.Personal[0], f.Shared, 25L).Accepted;
            f.World.SetGameSeconds(Core, 7200L);
            valid &= f.Accounts.Transfer(Core, f.World.AllocateAction(Core), f.Shared, f.Personal[1], 10L).Accepted;
            valid &= Balances(f, 75L, 210L, 300L, 65L) && f.Accounts.Ledger.Count == 2;
            string observed = Snapshot(f);
            Check(valid && (expected == null || expected == observed),
                "SEIT-ECO-01-P1-seed-4242-repeated-entry-" + run.ToString());
            expected = observed;
        }
        Check(!HasStaticMutable(typeof(Accounts)) && !HasStaticMutable(typeof(AccountRef))
            && !HasStaticMutable(typeof(AccountTransaction)), "SEIT-ECO-01-P1-no-static-mutable-state");
    }

    private static bool HasStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsLiteral && !field.IsInitOnly) return true;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (HasStaticMutable(nested)) return true;
        return false;
    }

    public static int Main()
    {
        EconomyChecks checks = new EconomyChecks();
        checks.Transfers();
        checks.Overdrafts();
        checks.NonContributors();
        checks.ExactlyOnce();
        checks.Repeatability();
        checks.failures += M1Regressions.Run("ECONOMY");
        Console.WriteLine(checks.failures == 0 ? "ECONOMY ALL PASS" : "ECONOMY FAILURES: " + checks.failures.ToString());
        return checks.failures == 0 ? 0 : 1;
    }
}
