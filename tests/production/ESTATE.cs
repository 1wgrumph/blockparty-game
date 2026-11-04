/*
ESTATE@1 TDD red contract (new product API in Blockparty.Simulation):
  EstateCare(GameStack), Tick(StateWriter), string Message;
  int TownHomeShare(HomeId), bool TownOwns(ObjectId): durable town title/item
    inspection, not a cash buyout or a replacement Ownership implementation.
  WorldState.SetCareRequired(StateWriter, ResidentId, bool), ResidentRecord.CareRequired.
  WorldState.DissolveHousehold(StateWriter, HouseholdId): remove an empty household
    so a previously valid bequest can lose its recipient (ESTATE vanished-recipient case).
All other dependencies below are existing public producers. Call EstateCare.Tick
at the shared clock boundary; it drives the real executor under its own writer.
The constructor must not settle anything. Durable state must survive the public
SaveV1.SaveGame/LoadGame API; representation/schema version is product-owned.

Oracles: plan.json ESTATE, m3/test-engineer.json ESTATE suite_checks, canonical
SDoc BP-LIFE-03/04 and BP-SEC-05. Money fixtures are SEIT-LIFE-04-P2..P6/P11/N6.
New suite justified by the package selector; STATE only tests ledger primitives.
No world, clock, aging, accounts, housing, executor or codec test doubles.

Local mapping (subclaims, NOT whole SEIT acceptance):
  will-edit: SEIT-LIFE-04-P1/N1, SEIT-IF-CMD-P3/N6, SEIT-IF-SAVE-P2.
  estate-will: SEIT-LIFE-04-P2; estate-spouse/children/nearest/town:
    SEIT-LIFE-04-P3/P4/P5/P6, N2/N3, SEIT-IF-LIFE-P1/N1/N5.
  rounding: SEIT-LIFE-04-P11/N6 (combined households, Charity last, order).
  care-survivor/designated/relative/town: SEIT-LIFE-04-P7/P8/P9/P10/N4,
    SEIT-IF-LIFE-P1/N2 (child AND CareRequired elder in every branch).
  every estate/care scenario: SEIT-LIFE-04-N5, SEIT-IF-LIFE-N3,
    SEIT-IF-SAVE-P2/N4 (two independent loads before, two after settlement).
  permanent-death: SEIT-LIFE-03-P1/N1, SEIT-IF-CMD-N4, SEIT-SM-LIFE-I1.
  These are local portions of SEIT-AC-07. Rendered will/death UI, ordinary birth,
  mid-minigolf cancellation, notifications as rendered, carried/travel visuals,
  process-kill recovery and full acceptance remain at their recorded gates.
  WillRecord has no per-heirloom or home-recipient mapping; this suite tests
  will money and fallback-owned furniture/title, not an invented payload shape.

negative-fixture injects a duplicate payment into the real balance, then runs the
SAME conservation/once-only assertion. It must print FAIL and exit 1 when the
capability exists. Missing ESTATE capability is the expected current red.
Resource group isolated-csharp; no engine, display, ports or network. Unique temp
roots, cleaned in finally; no real-time waits or static product state injected.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class EstateChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;
    private sealed class Scene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }
    private sealed class Fixture
    {
        public GameStack G;
        public EstateCare Estate;
        public ResidentId[] R;
        public HouseholdId[] H;
        public HomeId[] Homes;
        public ObjectId Item;
    }
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static void Require(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
    }
    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + ": " + e.GetType().Name + " " + e.Message); }
    }
    private static ResidentRecord Person(Fixture f, int i)
    {
        ResidentRecord r;
        Require(f.G.World.TryGetResident(f.R[i], out r), "fixture identity " + i);
        return r;
    }
    private static string Recipient(HouseholdId h) { return ((long)h).ToString(); }
    private static string Json(object value)
    {
        return JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { IncludeFields = true });
    }
    private static Fixture NewFixture(long money = 101L)
    {
        var w = new WorldState(901L, 4242L);
        var f = new Fixture { R = new ResidentId[10], H = new HouseholdId[10], Homes = new HomeId[10] };
        for (int i = 0; i < f.R.Length; i++)
        {
            var home = w.AllocateHome(Core, 8);
            var household = w.AllocateHousehold(Core, "H" + i, home.Id);
            f.Homes[i] = home.Id; f.H[i] = household.Id;
            f.R[i] = w.AllocateResident(Core, "R" + i, LifeStage.Adult, household.Id, home.Id).Id;
            w.SetMoney(Core, f.R[i], i == 0 ? money : 200L);
        }
        var clock = new Clock(w);
        clock.SetPreset(AgingPreset.Off);
        var commands = new CommandArbitrator(w, clock);
        var executor = new ActivityExecutor(w, commands, clock);
        var accounts = new Accounts(w);
        var appearances = new AppearanceStore();
        foreach (var r in w.ResidentsSnapshot())
        {
            appearances.Register(r.Id, new WorldRng(4242L + (long)r.Id));
            executor.SetResidentPosition(r.Id, new ApproachPose(0f, 0f, 0f));
            accounts.SetSharedBalance(Core, r.Household, 300L);
        }
        var ownership = new Ownership(w, accounts);
        var housing = new Housing(w, accounts, ownership, executor, appearances);
        foreach (var home in f.Homes) housing.RegisterHome(Core, home, 0L);
        f.G = new GameStack(w, clock, commands, executor, appearances, accounts,
            new Food(w, commands, executor, accounts), new Expenses(w, accounts), ownership, housing);
        f.Estate = new EstateCare(f.G);
        return f;
    }
    private static CommandResult Edit(Fixture f, WillRecord will)
    {
        var w = f.G.World;
        return f.G.Arbitrator.Submit(new CommandEnvelope(w.AllocateCommand(Core), w.Epoch,
            w.GameSeconds, CommandSource.PlayerHuman, Person(f, 0).ResidentSequence + 1u,
            CommandKind.WillEdit, f.R[0], null, null, new CommandParameters.Will(will)));
    }
    private static WillRecord Will(MoneyShare[] shares, HouseholdId[] care = null, ObjectId[] items = null)
    {
        return new WillRecord(shares, 0, items ?? new ObjectId[0], "For my family",
            "Remember me at home", care ?? new HouseholdId[0]);
    }
    private static void Link(Fixture f, int child, int parent)
    {
        f.G.World.AddFamilyLink(Core, f.R[child], f.R[parent], "Parent");
        f.G.World.AddFamilyLink(Core, f.R[parent], f.R[child], "Child");
    }
    private static void Spouse(Fixture f, int i)
    {
        f.G.World.AddFamilyLink(Core, f.R[0], f.R[i], "Spouse");
        f.G.World.AddFamilyLink(Core, f.R[i], f.R[0], "Spouse");
    }
    private static void Tick(Fixture f, long seconds = 6L)
    {
        f.G.Clock.AdvanceRealSeconds(Core, seconds / 60.0, () => f.Estate.Tick(Core));
    }
    // Actual AGING producer: terminal elder boundary emits one OldAge Death.
    private static EventId Die(Fixture f)
    {
        var w = f.G.World;
        w.SetStage(Core, f.R[0], LifeStage.Elder, 1f);
        w.SetAging(Core, f.R[0], 1.0, 1.0, true);
        f.G.Clock.SetPreset(AgingPreset.Moderate);
        var aging = new Aging(w, f.G.Clock);
        f.G.Clock.AdvanceRealSeconds(Core, 0.1, () => aging.Tick(Core));
        f.G.Clock.SetPreset(AgingPreset.Off);
        Require(!Person(f, 0).IsAlive, "real AGING elder death");
        EventId death = default(EventId); int count = 0;
        foreach (var e in w.Events)
            if (e.Kind == "Death" && e.Actor == f.R[0]) { death = e.Event; count++; }
        Require(count == 1, "one real Death event");
        return death;
    }
    private static Fixture Reload(Fixture f, string path, bool save = true)
    {
        if (save)
        {
            var written = SaveV1.SaveGame(f.G, path);
            Require(written.Ok, "public SaveGame: " + written.Detail);
        }
        var loaded = SaveV1.LoadGame(path, new Scene());
        Require(loaded.Ok && loaded.Game != null, "public LoadGame: " + loaded.Notice);
        var g = loaded.Game;
        var stack = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor, g.Appearances,
            g.Accounts, g.Food, g.Expenses, g.Ownership, g.Housing, g.Family);
        return new Fixture { G = stack, Estate = new EstateCare(stack), R = f.R,
            H = f.H, Homes = f.Homes, Item = f.Item };
    }
    private static long Balance(Fixture f, int i) { return f.G.Accounts.Balance(AccountRef.Shared(f.H[i])); }
    private static int Share(Fixture f, int i)
    {
        var a = f.G.Ownership.GetAgreement(f.Homes[0]);
        if (a != null) foreach (var s in a.Shares) if (s.Household == f.H[i]) return s.Percent;
        return 0;
    }
    private static string Settlement(Fixture f)
    {
        var parts = new List<string>();
        foreach (var r in f.G.World.ResidentsSnapshot())
            parts.Add((long)r.Id + ":" + r.IsAlive + ":" + r.Money + ":" + (long)r.Household
                + ":" + r.CareRequired + ":" + Json(r.Family));
        foreach (var h in f.H) parts.Add("balance:" + Recipient(h) + ":" + f.G.Accounts.Balance(AccountRef.Shared(h)));
        parts.Add("town:" + f.G.World.TownFund + ":" + f.Estate.TownHomeShare(f.Homes[0])
            + ":" + ((long)f.Item == 0L ? false : f.Estate.TownOwns(f.Item)));
        parts.Add(Json(f.G.Accounts.Export()));
        parts.Add(Json(f.G.World.EstateLedger)); parts.Add(Json(f.G.World.CareAssignments));
        parts.Add(Json(f.G.Ownership.Export())); parts.Add(Json(f.G.Housing.Export()));
        return string.Join("|", parts);
    }
    // Every branch gets the same before/after codec and replay matrix.
    private static void AcrossReloads(string name, Fixture f, Action<Fixture> oracle,
        bool negative = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-estate-" + Guid.NewGuid().ToString("N"));
        try
        {
            EventId death = Die(f);
            string unsettled = Settlement(f);
            f.G.Clock.Pause(); Tick(f);
            Check(Settlement(f) == unsettled, name + "-paused-no-settlement");
            f.G.Clock.Resume();
            var a = Reload(f, Path.Combine(root, "before"));
            var b = Reload(f, Path.Combine(root, "before"), false);
            Check(Settlement(a) == unsettled && Settlement(b) == unsettled, name + "-pre-settlement-roundtrip");
            Tick(a); Tick(b); oracle(a); oracle(b);
            string settled = Settlement(a);
            Check(settled == Settlement(b), name + "-two-preloads-same-settlement");
            int rows = 0;
            foreach (var row in a.G.World.EstateLedger)
                if (row.DeathEvent == death && row.Settled && row.AssetRef.Length > 0 && row.Recipient.Length > 0) rows++;
            Check((rows > 0 || a.G.World.CareAssignments.Count > 0) && !string.IsNullOrWhiteSpace(a.Estate.Message), name + "-death-keyed-ledger-and-notice");
            var post = Reload(a, Path.Combine(root, "after"));
            Tick(post, 86400L);
            if (negative)
            {
                // Inject the forbidden second credit through the real balance API.
                post.G.Accounts.SetSharedBalance(Core, post.H[2], Balance(post, 2) + 101L);
            }
            Check(Settlement(post) == settled, name + "-no-double-settlement-after-reload");
            if (!negative) oracle(post);
            var twice = Reload(post, Path.Combine(root, "after-again"));
            Tick(twice, 86400L);
            Check(Settlement(twice) == settled, name + "-second-postload-no-payment-or-admission");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void WillEditing()
    {
        var f = NewFixture();
        f.G.Housing.RegisterFurniture(Core, "Heirloom", 75L, 1);
        ObjectId owned;
        Require(f.G.Housing.BuyFurniture(Core, f.G.World.AllocateAction(Core), f.H[0], "Heirloom",
            f.Homes[0], new ApproachPose(0f, 0f, 0f), AccountRef.Personal(f.R[0]), out owned).Accepted,
            "real owned object for duplicate-heirloom edit");
        var valid = Will(new[] { new MoneyShare(Recipient(f.H[2]), 60), new MoneyShare("Charity", 40) });
        Require(Edit(f, valid).Accepted, "valid authored will");
        var updated = Will(new[] { new MoneyShare(Recipient(f.H[2]), 50), new MoneyShare("Charity", 50) });
        Check(Edit(f, updated).Accepted, "will-edit-update");
        var invalid = new[] {
            Will(new[] { new MoneyShare(Recipient(f.H[2]), 90) }),
            Will(new[] { new MoneyShare("99999", 100) }),
            Will(new[] { new MoneyShare(Recipient(f.H[2]), 100) }, null, new[] { (ObjectId)99999L }),
            Will(new[] { new MoneyShare(Recipient(f.H[2]), 100) }, null, new[] { owned, owned }) };
        foreach (var bad in invalid)
        {
            var result = Edit(f, bad); WillRecord kept;
            Check(!result.Accepted && Enum.IsDefined(typeof(RejectReason), result.Reason)
                && f.G.Arbitrator.TryGetLastWill(f.R[0], out kept) && Json(kept) == Json(updated),
                "will-edit-invalid-keeps-last-valid");
        }
        string root = Path.Combine(Path.GetTempPath(), "blockparty-estate-will-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loaded = Reload(f, root); WillRecord kept;
            Check(loaded.G.Arbitrator.TryGetLastWill(f.R[0], out kept) && Json(kept) == Json(updated),
                "will-edit-note-preference-public-roundtrip");
            Check(Edit(loaded, null).Accepted, "will-edit-clear");
            var cleared = Reload(loaded, root);
            Check(!cleared.G.Arbitrator.TryGetLastWill(f.R[0], out kept) || kept == null,
                "will-edit-clear-absence-persists");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Estates(string branch, bool negative)
    {
        long amount = branch == "town" ? 80L : 101L;
        var f = NewFixture(amount);
        if (branch == "will" || branch == "vanished")
            Require(Edit(f, Will(new[] { new MoneyShare(Recipient(f.H[2]), 50),
                new MoneyShare(Recipient(f.H[4]), 50) })).Accepted, "will money 50/50");
        if (branch == "spouse") { Spouse(f, 2); Link(f, 4, 0); Link(f, 5, 0); }
        if (branch == "will") { Spouse(f, 5); Link(f, 6, 0); }
        if (branch == "children")
        {
            Spouse(f, 1); f.G.World.SetMoney(Core, f.R[1], 0L);
            f.G.World.SetAlive(Core, f.R[1], false);
            Link(f, 2, 0); Link(f, 4, 0);
        }
        if (branch == "vanished")
        {
            Spouse(f, 5);
            f.G.World.SetHousehold(Core, f.R[4], f.H[9], f.Homes[9]);
            f.G.Accounts.SetSharedBalance(Core, f.H[4], 0L);
            f.G.World.DissolveHousehold(Core, f.H[4]);
        }
        if (branch == "nearest")
        {
            Link(f, 0, 1); Link(f, 2, 1); Link(f, 4, 1);
            f.G.World.SetMoney(Core, f.R[1], 0L);
            f.G.World.SetAlive(Core, f.R[1], false);
            Link(f, 1, 6); Link(f, 7, 6); Link(f, 8, 7);
            f.G.World.SetMoney(Core, f.R[6], 0L);
            f.G.World.SetAlive(Core, f.R[6], false);
            f.G.World.SetMoney(Core, f.R[7], 0L);
            f.G.World.SetAlive(Core, f.R[7], false);
        }
        f.G.World.SetBond(Core, f.R[0], f.R[9], 95f, 95f);
        // An unrelated high-Romance resident is never a recorded spouse.
        if (branch != "will" && branch != "vanished")
        {
            Require(f.G.Ownership.SetAgreement(Core, f.Homes[0], 1000L,
                new[] { new OwnershipShare(f.H[0], 50), new OwnershipShare(f.H[3], 50) }).Accepted,
                "real 50/50 title");
            f.G.World.SetMoney(Core, f.R[0], amount + 75L);
            f.G.Housing.RegisterFurniture(Core, "Heirloom", 75L, 1);
            ObjectId item;
            Require(f.G.Housing.BuyFurniture(Core, f.G.World.AllocateAction(Core), f.H[0], "Heirloom",
                f.Homes[0], new ApproachPose(0f, 0f, 0f), AccountRef.Personal(f.R[0]), out item).Accepted,
                "real personally owned heirloom");
            f.Item = item;
        }
        AcrossReloads("estate-" + branch, f, x => {
            long total = x.G.World.TownFund;
            for (int i = 0; i < x.H.Length; i++)
            {
                long expected = branch == "vanished" && i == 4 ? 0L : 300L;
                if (branch == "will" || branch == "children") expected += i == 2 ? 51L : i == 4 ? 50L : 0L;
                else if (branch == "vanished") expected += i == 2 ? 51L : i == 5 ? 50L : 0L;
                else if (branch != "town" && i == 2) expected += amount;
                long personal = i == 0 || (branch == "children" && i == 1) || (branch == "nearest" && (i == 1 || i == 6 || i == 7)) ? 0L : 200L;
                Check(Balance(x, i) == expected && Person(x, i).Money == personal,
                    "estate-" + branch + "-recipient-and-survivor-" + i);
                total += Balance(x, i) - (branch == "vanished" && i == 4 ? 0L : 300L);
            }
            Check(total == amount && x.G.World.TownFund == (branch == "town" ? amount : 0L),
                "estate-" + branch + "-exact-total-no-cash-buyout");
            if (branch != "will" && branch != "vanished")
            {
                Check(Share(x, 3) == 50 && Share(x, 0) == 0, "estate-" + branch + "-survivor-half-retained");
                if (branch == "town")
                    Check(x.Estate.TownHomeShare(x.Homes[0]) == 50 && x.Estate.TownOwns(x.Item),
                        "estate-town-title-and-heirloom-recorded");
                else if (branch != "children")
                    Check(Share(x, 2) == 50 && x.G.Housing.GetItem(x.Item).Owner.Equals(AccountRef.Shared(x.H[2])),
                        "estate-" + branch + "-only-deceased-title-and-heirloom-transfer");
                else Check(Share(x, 2) == 25 && Share(x, 4) == 25, "estate-children-title-split");
            }
        }, negative && branch == "spouse");
    }
    private static void Rounding()
    {
        for (int mode = 0; mode < 5; mode++)
        {
            var f = NewFixture(10L);
            var shares = mode >= 3
                ? new[] { new MoneyShare("Charity", 35), new MoneyShare(Recipient(f.H[9]), 65) }
                : mode == 2
                ? new[] { new MoneyShare(Recipient(f.H[2]), 50), new MoneyShare(Recipient(f.H[4]), 35), new MoneyShare(Recipient(f.H[5]), 15) }
                : new[] { new MoneyShare(Recipient(f.H[2]), 25), new MoneyShare(Recipient(f.H[2]), 25),
                    new MoneyShare(Recipient(f.H[4]), 35), new MoneyShare(Recipient(f.H[5]), 15) };
            if (mode == 1 || mode == 3) Array.Reverse(shares);
            Require(Edit(f, Will(shares)).Accepted, "rounding valid will");
            bool charity = mode >= 3;
            AcrossReloads("rounding-" + mode, f, x => {
                long received = 0L;
                for (int i = 0; i < x.H.Length; i++)
                {
                    long extra = charity ? (i == 9 ? 7L : 0L) : i == 2 ? 5L : i == 4 ? 4L : i == 5 ? 1L : 0L;
                    Check(Balance(x, i) == 300L + extra, "rounding-household-" + i);
                    received += Balance(x, i) - 300L;
                }
                // Charity is external: check the real accounts transaction total, not TownFund.
                long charityPaid = 0L; int combinedPayments = 0;
                foreach (var row in x.G.Accounts.Ledger)
                {
                    if (row.Destination.Equals(AccountRef.Expense("Charity"))) charityPaid += row.Amount;
                    if (row.Destination.Equals(AccountRef.Shared(x.H[2]))) combinedPayments++;
                }
                Check(charity || combinedPayments == 1, "rounding-one-combined-household-payment");
                Check(charityPaid == (charity ? 3L : 0L) && received + charityPaid == 10L,
                    "rounding-combined-households-ascending-ties-charity-last");
            });
        }
    }
    private static void FillHome(Fixture f, int i)
    {
        int occupied = 0;
        foreach (var r in f.G.World.ResidentsSnapshot()) if (r.Household == f.H[i] && r.IsAlive) occupied++;
        for (int n = occupied; n < 8; n++)
        {
            var r = f.G.World.AllocateResident(Core, "Full home " + i + " resident " + n,
                LifeStage.Adult, f.H[i], f.Homes[i]);
            f.G.Appearances.Register(r.Id, new WorldRng(4242L + (long)r.Id));
            f.G.Executor.SetResidentPosition(r.Id, new ApproachPose(0f, 0f, 0f));
        }
    }
    private static void Care(string branch)
    {
        var f = NewFixture(0L);
        f.G.World.SetStage(Core, f.R[7], LifeStage.Child, 0f);
        f.G.World.SetStage(Core, f.R[8], LifeStage.Elder, 0f);
        f.G.World.SetCareRequired(Core, f.R[8], true);
        foreach (int i in new[] { 7, 8 })
        {
            f.G.World.SetHousehold(Core, f.R[i], f.H[0], f.Homes[0]);
            Link(f, i, 0);
            f.G.World.AssignCare(Core, new CareAssignment(f.R[i], f.R[0],
                new CarePlacement(f.H[0], -1), f.G.World.AppendEvent(Core, "Care", f.R[i], "initial caregiver").Event));
        }
        f.G.Housing.SetCaregiver(Core, f.R[0], true);
        if (branch == "survivor")
        {
            f.G.World.SetHousehold(Core, f.R[2], f.H[0], f.Homes[0]);
            f.G.Housing.SetCaregiver(Core, f.R[2], true);
        }
        else if (branch == "designated")
        {
            f.G.Housing.SetCaregiver(Core, f.R[2], true);
            Require(Edit(f, Will(new[] { new MoneyShare(Recipient(f.H[2]), 100) }, new[] { f.H[2] })).Accepted,
                "will-designated care household");
        }
        else
        {
            Link(f, 0, 1); Link(f, 2, 1); Link(f, 4, 1);
            f.G.World.SetMoney(Core, f.R[1], 0L);
            f.G.World.SetAlive(Core, f.R[1], false);
            f.G.Housing.SetCaregiver(Core, f.R[2], true);
            f.G.Housing.SetCaregiver(Core, f.R[4], true);
            Link(f, 3, 1);
            f.G.World.SetHousehold(Core, f.R[3], f.H[2], f.Homes[2]);
            FillHome(f, 2);
            if (branch == "town") FillHome(f, 4);
            Require(Edit(f, Will(new[] { new MoneyShare(Recipient(f.H[2]), 100) }, new[] { f.H[2] })).Accepted,
                "full designated household falls through");
        }
        AcrossReloads("care-" + branch, f, x => {
            foreach (int i in new[] { 7, 8 })
            {
                CareAssignment latest = default(CareAssignment); int found = 0;
                foreach (var row in x.G.World.CareAssignments)
                    if (row.Dependent == x.R[i]) { latest = row; found++; }
                ResidentRecord guardian = null;
                bool valid = found > 0 && x.G.World.TryGetResident(latest.Guardian, out guardian) && guardian.IsAlive;
                Check(valid && Person(x, i).IsAlive && Person(x, i).Money == 200L
                    && Person(x, i).GetNeed(Need.Health) == 80f && Person(x, i).Family.Count > 0,
                    "care-" + branch + "-identity-history-no-admission-reward-" + i);
                if (branch == "town")
                    Check(latest.Placement.Household == null && latest.Placement.FacilityPlace >= 0 && valid
                        && Array.IndexOf(x.H, guardian.Household) < 0,
                        "care-town-persistent-npc-and-place-" + i);
                else
                {
                    int target = branch == "relative" ? 4 : branch == "survivor" ? 0 : 2;
                    Check(latest.Placement.Household == x.H[target] && latest.Guardian == x.R[target == 0 ? 2 : target]
                        && Person(x, i).Household == x.H[target], "care-" + branch + "-correct-placement-" + i);
                }
                if (branch == "survivor") Check(found == 1, "care-survivor-no-readmission-" + i);
                else Check(found == 2, "care-one-transfer-after-initial-assignment-" + i);
            }
            for (int i = 0; i < x.H.Length; i++)
                Check(Balance(x, i) == 300L && x.G.Ownership.GetAgreement(x.Homes[i]) == null,
                    "care-admission-no-shared-money-or-title-reward-" + i);
            Check(Person(x, 8).CareRequired, "care-required-elder-flag-survives-codec");
        });
    }
    private static bool HasMemory(ResidentRecord r, EventId id)
    {
        foreach (var memory in r.Memories) if (memory.Event == id && memory.Kind == "FirstMeeting") return true;
        return false;
    }
    private static void PermanentDeath()
    {
        var f = NewFixture(); Link(f, 2, 0);
        var met = f.G.World.AppendEvent(Core, "FirstMeeting", f.R[0], "shared history");
        f.G.World.SetBond(Core, f.R[2], f.R[0], 50f, 10f);
        f.G.World.AddMemory(Core, f.R[2], new MemoryEntry(met.Event, "FirstMeeting", 0L, f.Homes[0],
            new[] { f.R[0], f.R[2] }, "met", "participant"));
        string family = Json(Person(f, 2).Family), bonds = Json(Person(f, 2).Bonds);
        AcrossReloads("permanent-death", f, x => {
            foreach (var source in new[] { CommandSource.PlayerHuman, CommandSource.PlayerAutomated })
            {
                var result = x.G.Arbitrator.Submit(new CommandEnvelope(x.G.World.AllocateCommand(Core),
                    x.G.World.Epoch, x.G.World.GameSeconds, source, Person(x, 0).ResidentSequence + 1u,
                    CommandKind.DirectActivity, x.R[0], new ActivityId("ACT-HOME-01"), null,
                    new CommandParameters.Direct(QueueMode.Immediate)));
                Check(!result.Accepted && result.Reason == RejectReason.ResidentDeceased,
                    "permanent-death-refuses-action-" + source);
            }
            Check(!x.G.Arbitrator.EnqueueAutonomous(x.R[0], new ActivityId("ACT-HOME-01"), null),
                "permanent-death-refuses-local-autonomy");
            var offered = new[] { new AvailableAction(new ActivityId("ACT-HOME-01"), null) };
            var proposed = x.G.Arbitrator.ApplyProposal(x.R[0], new DecisionProposal((GatewayRequestId)1L,
                offered[0].Activity, null, x.G.Arbitrator.CurrentStamp(x.R[0])), offered);
            Check(!proposed.Accepted && proposed.Reason == RejectReason.ResidentDeceased,
                "permanent-death-refuses-fresh-model-proposal");
            var travel = x.G.Arbitrator.Submit(new CommandEnvelope(x.G.World.AllocateCommand(Core),
                x.G.World.Epoch, x.G.World.GameSeconds, CommandSource.PlayerHuman,
                Person(x, 0).ResidentSequence + 1u, CommandKind.TravelTo, x.R[0], null,
                (long)x.Homes[2], new CommandParameters.Travel(QueueMode.Immediate)));
            Check(!travel.Accepted && travel.Reason == RejectReason.ResidentDeceased,
                "permanent-death-refuses-travel");
            Check(!Person(x, 0).IsAlive && x.G.Arbitrator.GetCurrent(x.R[0]) == null
                && Json(Person(x, 2).Family) == family && Json(Person(x, 2).Bonds) == bonds
                && HasMemory(Person(x, 2), met.Event), "permanent-death-survivor-links-and-history");
        });
        long old = f.G.World.NextResidentId;
        var arrival = f.G.World.AllocateResident(Core, "New arrival", LifeStage.Adult, f.H[1], f.Homes[1]);
        Check((long)arrival.Id == old && arrival.Id != f.R[0], "permanent-death-id-never-reused");
    }
    public static int Main(string[] args)
    {
        failures = 0;
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negative) Console.WriteLine("ESTATE negative-fixture: inject a second real spouse payment after reload");
        Run("will-edit", WillEditing);
        foreach (string branch in new[] { "will", "spouse", "children", "nearest", "town", "vanished" })
            Run("estate-" + branch, () => Estates(branch, negative));
        Run("rounding", Rounding);
        foreach (string branch in new[] { "survivor", "designated", "relative", "town" })
            Run("care-" + branch, () => Care(branch));
        Run("permanent-death", PermanentDeath);
        if (failures == 0) Console.WriteLine("ESTATE ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
