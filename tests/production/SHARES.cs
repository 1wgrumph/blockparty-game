/*
SHARES@1 red contract, namespace Blockparty.Simulation, Ownership.cs:
  OwnershipShare(HouseholdId household, int percent, int childcareDays = 0)
    readable Household, Percent, ChildcareDays
  OwnershipAgreement: readable Home, RecordedValue,
    IReadOnlyList<OwnershipShare> Shares
  Ownership(WorldState world, Accounts accounts)
    CommandResult SetAgreement(StateWriter, HomeId, long value, OwnershipShare[])
    OwnershipAgreement GetAgreement(HomeId) // null when no agreement
    IReadOnlyList<OwnershipAgreement> History(HomeId) // accepted agreements only
    Depart(StateWriter, ActionInstanceId, HomeId, HouseholdId seller, HouseholdId buyer)
      returns readable Value, NoncashCredit, AmountDue, BoughtOut, Listed,
      DueGameSeconds; buyout uses shared Accounts in one transaction
    CommandResult CompleteListing(StateWriter, ActionInstanceId, HomeId, HouseholdId buyer)
    CommandResult Sell(StateWriter, ActionInstanceId, HomeId, long proceeds)
      credits the named shared accounts and records their distribution in Accounts.Ledger
  GameStack has an eight-argument overload appending Ownership to the existing
    seven-argument constructor; LoadedGame exposes restored Ownership.

Oracles: BP-ECO-03, BP-SEC-05 ownership settlement, SEIT-ECO-03-P1/P3/P6/N3/N4.
New suite required by package runner; ECONOMY tests accounts, not ownership.
Invalid creation/replacement, departure, save and rounding use real world,
accounts and save codec; no replacement product implementation or mock codec.
Candidate childcare credit is 20/day, credited against the 400-unit buyout.
The negative-fixture argument substitutes the SEIT-P6 floor-only distribution
for the observed ledger amounts: identical assertions must reject lost units.
Resource group isolated-csharp: no editor, ports, credentials or network.
P2 is specification inspection; P4/P5/N2 require ESTATE, N1 household lifecycle
requires HOUSING. Full AC-05/AC-08 and these whole cases remain at their gates;
local AC-08 coverage is refusal plus rounding, AC-05 is durable account state.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class SharesChecks
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
        public Accounts Accounts;
        public Ownership Ownership;
        public GameStack Stack;
        public HomeId Home;
        public HouseholdId[] Owners;
    }

    private static Fixture NewFixture()
    {
        AppearanceStore appearances;
        WorldState world = ResidentFactory.CreateNewGame(7L, 4242L, null, out appearances);
        ResidentRecord[] residents = world.ResidentsSnapshot();
        HomeId home = residents[0].Home;
        HouseholdId[] owners = new HouseholdId[4];
        owners[0] = residents[0].Household;
        for (int i = 1; i < owners.Length; i++)
            owners[i] = world.AllocateHousehold(Core, "Owner " + (i + 1), home).Id;
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        ActivityExecutor executor = new ActivityExecutor(world, arbitrator, clock);
        Accounts accounts = new Accounts(world);
        Food food = new Food(world, arbitrator, executor, accounts);
        Ownership ownership = new Ownership(world, accounts);
        return new Fixture
        {
            World = world, Accounts = accounts, Ownership = ownership, Home = home, Owners = owners,
            Stack = new GameStack(world, clock, arbitrator, executor, appearances, accounts, food, ownership),
        };
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
        catch (Exception error) { Check(false, name + " " + error); }
    }

    private static OwnershipShare[] SixtyForty(Fixture f, int days = 0)
    {
        return new[] { new OwnershipShare(f.Owners[0], 60), new OwnershipShare(f.Owners[1], 40, days) };
    }

    private static string Agreement(OwnershipAgreement agreement)
    {
        if (agreement == null) return "absent";
        StringBuilder text = new StringBuilder();
        text.Append((long)agreement.Home).Append(':').Append(agreement.RecordedValue);
        foreach (OwnershipShare share in agreement.Shares)
            text.Append('|').Append((long)share.Household).Append(':').Append(share.Percent)
                .Append(':').Append(share.ChildcareDays);
        return text.ToString();
    }

    private static string History(Ownership ownership, HomeId home)
    {
        StringBuilder text = new StringBuilder();
        foreach (OwnershipAgreement agreement in ownership.History(home))
            text.Append('[').Append(Agreement(agreement)).Append(']');
        return text.ToString();
    }

    private static bool Shares(OwnershipAgreement agreement, HouseholdId[] owners, int[] percentages)
    {
        if (agreement == null || agreement.Shares.Count != owners.Length) return false;
        int total = 0;
        for (int i = 0; i < owners.Length; i++)
        {
            int matches = 0;
            foreach (OwnershipShare share in agreement.Shares)
                if (share.Household == owners[i] && share.Percent == percentages[i]) matches++;
            if (matches != 1) return false;
            total += percentages[i];
        }
        return total == 100;
    }

    private static long Balance(Fixture f, int owner)
    {
        return f.Accounts.Balance(AccountRef.Shared(f.Owners[owner]));
    }

    private static void AgreementAndSave()
    {
        Fixture f = NewFixture();
        Require(f.Ownership.SetAgreement(Core, f.Home, 1000L, SixtyForty(f, 3)).Accepted, "create 60/40");
        Check(Shares(f.Ownership.GetAgreement(f.Home), new[] { f.Owners[0], f.Owners[1] }, new[] { 60, 40 })
            && f.Ownership.GetAgreement(f.Home).RecordedValue == 1000L,
            "SEIT-ECO-03-P1-named-60-40-agreement");
        string agreement = Agreement(f.Ownership.GetAgreement(f.Home));
        string history = History(f.Ownership, f.Home);
        f.Accounts.SetSharedBalance(Core, f.Owners[0], 900L);
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-shares-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Require(SaveV1.SaveGame(f.Stack, directory).Ok, "SHARES save via public codec");
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok && loaded.Game != null, "SHARES load: " + loaded.Notice);
            Require(loaded.Game.Ownership != null, "loaded SHARES service");
            Check(!object.ReferenceEquals(f.World, loaded.Game.World)
                && Agreement(loaded.Game.Ownership.GetAgreement(f.Home)) == agreement
                && History(loaded.Game.Ownership, f.Home) == history,
                "SEIT-ECO-03-P1-fresh-world-save-roundtrip-agreement-history-and-childcare");
            var settlement = loaded.Game.Ownership.Depart(Core, loaded.Game.World.AllocateAction(Core),
                f.Home, f.Owners[1], f.Owners[0]);
            Check(settlement.BoughtOut && settlement.Value == 400L && settlement.NoncashCredit == 60L
                && settlement.AmountDue == 340L
                && loaded.Game.Accounts.Balance(AccountRef.Shared(f.Owners[0])) == 560L
                && loaded.Game.Accounts.Balance(AccountRef.Shared(f.Owners[1])) == 340L,
                "SEIT-AC-05-local-restored-shares-use-restored-accounts");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void InvalidAgreements()
    {
        int[,] invalid = { { 60, 30 }, { 60, 50 }, { 70, 20 }, { 70, 40 } };
        for (int row = 0; row < invalid.GetLength(0); row++)
        {
            foreach (bool replace in new[] { false, true })
            {
                Fixture f = NewFixture();
                if (replace) Require(f.Ownership.SetAgreement(Core, f.Home, 1000L, SixtyForty(f)).Accepted,
                    "valid agreement before rejected edit");
                string before = Agreement(f.Ownership.GetAgreement(f.Home));
                string history = History(f.Ownership, f.Home);
                int world = f.World.ComputeStateHash();
                CommandResult result = f.Ownership.SetAgreement(Core, f.Home, 2000L, new[] {
                    new OwnershipShare(f.Owners[0], invalid[row, 0]),
                    new OwnershipShare(f.Owners[1], invalid[row, 1]) });
                Check(!result.Accepted && result.Reason == RejectReason.InvalidParameters
                    && Agreement(f.Ownership.GetAgreement(f.Home)) == before
                    && History(f.Ownership, f.Home) == history && f.World.ComputeStateHash() == world
                    && Balance(f, 0) == 0L && Balance(f, 1) == 0L && f.Accounts.Ledger.Count == 0,
                    "SEIT-ECO-03-N3-SEIT-AC-08-" + (replace ? "edit" : "create")
                    + "-" + invalid[row, 0] + "-" + invalid[row, 1] + "-reason-and-no-mutation");
            }
        }
        Fixture valid = NewFixture();
        Require(valid.Ownership.SetAgreement(Core, valid.Home, 1000L, SixtyForty(valid)).Accepted, "create");
        Check(valid.Ownership.SetAgreement(Core, valid.Home, 1000L, new[] {
            new OwnershipShare(valid.Owners[0], 50), new OwnershipShare(valid.Owners[1], 50) }).Accepted
            && Shares(valid.Ownership.GetAgreement(valid.Home), new[] { valid.Owners[0], valid.Owners[1] }, new[] { 50, 50 }),
            "SEIT-ECO-03-N3-valid-100-percent-replacement-remains-available");
    }

    private static void Departure()
    {
        foreach (int days in new[] { 0, 3 })
        {
            Fixture f = NewFixture();
            f.World.SetGameSeconds(Core, 3600L);
            f.Accounts.SetSharedBalance(Core, f.Owners[0], 900L);
            f.Accounts.SetSharedBalance(Core, f.Owners[1], 17L);
            Require(f.Ownership.SetAgreement(Core, f.Home, 1000L, SixtyForty(f, days)).Accepted, "create");
            var settlement = f.Ownership.Depart(Core, f.World.AllocateAction(Core), f.Home, f.Owners[1], f.Owners[0]);
            long due = days == 0 ? 400L : 340L;
            Check(settlement.BoughtOut && !settlement.Listed && settlement.Value == 400L
                && settlement.NoncashCredit == (days == 0 ? 0L : 60L) && settlement.AmountDue == due
                && Balance(f, 0) == 900L - due && Balance(f, 1) == 17L + due
                && f.Accounts.Ledger.Count == 1 && f.Accounts.Ledger[0].Amount == due
                && f.Accounts.Ledger[0].Source.Equals(AccountRef.Shared(f.Owners[0]))
                && f.Accounts.Ledger[0].Destination.Equals(AccountRef.Shared(f.Owners[1]))
                && f.Accounts.Ledger[0].GameSeconds == 3600L
                && Shares(f.Ownership.GetAgreement(f.Home), new[] { f.Owners[0] }, new[] { 100 }),
                "SEIT-ECO-03-P3-departure-value-buyout-single-transaction-childcare-days-" + days);
        }
        Fixture listed = NewFixture();
        listed.World.SetGameSeconds(Core, 3600L);
        Require(listed.Ownership.SetAgreement(Core, listed.Home, 1000L, SixtyForty(listed)).Accepted, "create");
        var waiting = listed.Ownership.Depart(Core, listed.World.AllocateAction(Core), listed.Home,
            listed.Owners[1], listed.Owners[0]);
        Check(waiting.Listed && !waiting.BoughtOut && waiting.Value == 400L && waiting.AmountDue == 400L
            && waiting.DueGameSeconds == 3600L + 7L * Day
            && listed.Accounts.Ledger.Count == 0 && Balance(listed, 0) == 0L && Balance(listed, 1) == 0L,
            "SEIT-ECO-03-P3-unfunded-buyout-lists-seven-game-days-without-cash");
        listed.World.SetGameSeconds(Core, 3600L + 7L * Day);
        HouseholdId arriving = listed.World.AllocateHousehold(Core, "Arriving buyer", listed.Home).Id;
        listed.Accounts.SetSharedBalance(Core, arriving, 500L);
        CommandResult sold = listed.Ownership.CompleteListing(Core, listed.World.AllocateAction(Core), listed.Home, arriving);
        Check(sold.Accepted && Balance(listed, 1) == 400L && Balance(listed, 0) == 0L
            && listed.Accounts.Balance(AccountRef.Shared(arriving)) == 100L
            && listed.Accounts.Ledger.Count == 1 && listed.Accounts.Ledger[0].Amount == 400L
            && listed.Accounts.Ledger[0].Source.Equals(AccountRef.Shared(arriving))
            && listed.Accounts.Ledger[0].Destination.Equals(AccountRef.Shared(listed.Owners[1]))
            && Shares(listed.Ownership.GetAgreement(listed.Home), new[] { listed.Owners[0], arriving }, new[] { 60, 40 }),
            "SEIT-ECO-03-P3-listed-share-sold-to-arriving-household-at-recorded-value");
    }

    private static void ListingWait()
    {
        Fixture f = NewFixture();
        const long listedAt = 3600L;
        f.World.SetGameSeconds(Core, listedAt);
        f.Accounts.SetSharedBalance(Core, f.Owners[0], 13L);
        f.Accounts.SetSharedBalance(Core, f.Owners[1], 17L);
        Require(f.Ownership.SetAgreement(Core, f.Home, 1000L, SixtyForty(f)).Accepted, "create");
        var waiting = f.Ownership.Depart(Core, f.World.AllocateAction(Core), f.Home,
            f.Owners[1], f.Owners[0]);
        Require(waiting.Listed && !waiting.BoughtOut && waiting.Value == 400L
            && waiting.DueGameSeconds == listedAt + 7L * Day, "seven-day listing created");
        HouseholdId arriving = f.World.AllocateHousehold(Core, "Arriving buyer", f.Home).Id;
        f.Accounts.SetSharedBalance(Core, arriving, 500L);
        string agreement = Agreement(f.Ownership.GetAgreement(f.Home));
        string history = History(f.Ownership, f.Home);
        f.World.SetGameSeconds(Core, listedAt + 7L * Day - 1L);
        CommandResult early = f.Ownership.CompleteListing(Core, f.World.AllocateAction(Core), f.Home, arriving);
        Require(!early.Accepted && Agreement(f.Ownership.GetAgreement(f.Home)) == agreement
            && History(f.Ownership, f.Home) == history
            && Balance(f, 0) == 13L && Balance(f, 1) == 17L
            && f.Accounts.Balance(AccountRef.Shared(arriving)) == 500L
            && f.Accounts.Ledger.Count == 0,
            "SEIT-ECO-03-P3-listed-share-refuses-before-seven-game-days-without-mutation; early.Accepted="
            + early.Accepted);
        f.World.SetGameSeconds(Core, listedAt + 7L * Day);
        CommandResult sold = f.Ownership.CompleteListing(Core, f.World.AllocateAction(Core), f.Home, arriving);
        Check(sold.Accepted && Balance(f, 0) == 13L && Balance(f, 1) == 417L
            && f.Accounts.Balance(AccountRef.Shared(arriving)) == 100L
            && f.Accounts.Ledger.Count == 1 && f.Accounts.Ledger[0].Amount == 400L
            && f.Accounts.Ledger[0].Source.Equals(AccountRef.Shared(arriving))
            && f.Accounts.Ledger[0].Destination.Equals(AccountRef.Shared(f.Owners[1]))
            && f.Accounts.Ledger[0].GameSeconds == listedAt + 7L * Day
            && Shares(f.Ownership.GetAgreement(f.Home), new[] { f.Owners[0], arriving }, new[] { 60, 40 }),
            "SEIT-ECO-03-P3-listed-share-completes-at-seven-game-days-at-recorded-value");
    }

    private static void Sale(long proceeds, bool fourOwners, bool negativeFixture)
    {
        Fixture f = NewFixture();
        OwnershipShare[] shares = fourOwners ? new[] {
            new OwnershipShare(f.Owners[3], 25), new OwnershipShare(f.Owners[2], 25),
            new OwnershipShare(f.Owners[1], 25), new OwnershipShare(f.Owners[0], 25) } : new[] {
            new OwnershipShare(f.Owners[1], 34), new OwnershipShare(f.Owners[0], 33),
            new OwnershipShare(f.Owners[2], 33) };
        long[] expected = fourOwners ? (proceeds == 1003L ? new long[] { 251, 251, 251, 250 }
            : new long[] { 250, 250, 250, 249 }) : new long[] { 331, 341, 330 };
        Require(f.Ownership.SetAgreement(Core, f.Home, 1000L, shares).Accepted, "sale agreement");
        f.World.SetGameSeconds(Core, 7200L);
        CommandResult result = f.Ownership.Sell(Core, f.World.AllocateAction(Core), f.Home, proceeds);
        Check(result.Accepted, "SEIT-ECO-03-sale-accepted-" + proceeds);
        long[] actual = new long[expected.Length];
        bool ledger = f.Accounts.Ledger.Count == expected.Length;
        for (int i = 0; i < expected.Length; i++)
        {
            int rows = 0;
            foreach (AccountTransaction entry in f.Accounts.Ledger)
                if (entry.Destination.Equals(AccountRef.Shared(f.Owners[i])))
                {
                    actual[i] += entry.Amount;
                    rows++;
                    ledger &= entry.GameSeconds == 7200L;
                }
            ledger &= rows == 1 && Balance(f, i) == expected[i];
        }
        if (negativeFixture) actual = new long[] { 330L, 340L, 330L };
        long total = 0L;
        bool exact = true;
        for (int i = 0; i < expected.Length; i++)
        {
            total += actual[i];
            exact &= actual[i] == expected[i];
        }
        string id = fourOwners ? "SEIT-ECO-03-N4" : "SEIT-ECO-03-P6";
        Check(ledger && exact && total == proceeds,
            id + "-SEIT-AC-08-largest-remainder-ascending-HouseholdId-total-" + proceeds);
    }

    public static int Main(string[] args)
    {
        failures = 0;
        bool negativeFixture = args.Length == 1 && args[0] == "negative-fixture";
        Run("agreement-save", AgreementAndSave);
        Run("invalid-agreements", InvalidAgreements);
        Run("departure", Departure);
        Run("listing-wait", ListingWait);
        Run("sale-1002", () => Sale(1002L, false, negativeFixture));
        Run("sale-1003", () => Sale(1003L, true, false));
        Run("sale-999", () => Sale(999L, true, false));
        if (failures == 0) Console.WriteLine("SHARES ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
