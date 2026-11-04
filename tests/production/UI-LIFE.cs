/*
UI-LIFE@1 test-author contract: Assets/Blockparty/UI/HouseholdLife.cs, plain C# 9.
  HouseholdLife(GameStack)
  HouseholdLifeSnapshot Inspect(ResidentId)
    ResidentId Resident; HouseholdId Household; HomeId Home;
    IReadOnlyList<ResidentId> Members; WillRecord Will (null means no will);
    PregnancyRecord? PendingDelivery; IReadOnlyList<CareAssignment> Care;
    OwnershipAgreement Ownership (null means no agreement).
    Snapshot fields/getters are detached from authoritative collections.
  HousingOffer HomeOffer(HouseholdId, HomeId, AccountRef)
  CommandResult Submit(CommandEnvelope); string Message (last refusal reason).
  CommandResult FormHousehold(string, ResidentId[], HomeId)
  CommandResult JoinHousehold(ResidentId, HouseholdId)
  CommandResult LeaveHousehold(ResidentId, HomeId)
  CommandResult MoveHousehold(HouseholdId, HomeId)
  CommandResult BuyHome(ActionInstanceId, HouseholdId, HomeId, AccountRef)
  CommandResult SetOwnership(HomeId, long, OwnershipShare[])
These controls use the existing normal command APIs: envelope controls use
FamilyCreation.Submit/CommandArbitrator.Submit; household/ownership controls use
Housing/Ownership's CommandResult APIs under the command writer. No new command
kind, direct UI world mutation, simulated payment, birth or care implementation.

Scope inventory: new suite and red.log only. The runner discovers the suite;
no callers, fixtures, config, exports, consumer suites or product files change.
Oracles: plan UI-LIFE outcome + M3 Test Engineer suite_checks; SEIT candidate
prices 2500/4000 and five-person household with 3000 funds; existing BIRTH seed
1234 (real conception draw), ESTATE care, will-edit and HOUSING producers.

Local mapping (subclaims only, not whole-case acceptance):
  CostsAndOwnership: costs/blocked reasons from authority -> SEIT-WORLD-07-P1,
    SEIT-AC-08, SEIT-UI-02-P1; negative-fixture blanks a blocked offer's Reason
    and runs the SAME HasBlockedReason assertion (must FAIL/exit 1).
  HouseholdControls: normal household<home> commands -> SEIT-AC-08/12,
    SEIT-WORLD-07-N1/N2/N3/N4 (local affordability/capacity refusal only).
  WillAndCommandControls: author/update/inspect/clear, validation, caregiver ->
    SEIT-LIFE-04-P1/N1, SEIT-IF-CMD-P3/N6, SEIT-AC-07/12.
  FamilyCareAndReload: normal ACT-REL-06, pending delivery and real care ->
    SEIT-LIFE-01-P1/N1/N2/N6, SEIT-IF-EXEC-P3/P4, SEIT-IF-LIFE-P1/P2/N2/N3,
    SEIT-IF-SAVE-P2/N4, SEIT-IF-TIME-P1/P2/N5, SEIT-LIFE-04-P7/N5, SEIT-AC-07.
Remaining SEIT estate branch matrices, stage transitions, notifications/history,
rendered input/accessibility and uncoached-player acceptance stay at their gates.
Visual review belongs to CONTROLS-PLAY and M3-LOOK, not this model suite.

UI_LIFE_PRODUCER_CHECK compiles/runs ONLY real fixture producers before the
missing model exists; its distinct marker cannot satisfy the package gate.
No mocks or replacement product. Resource group isolated-csharp; no editor,
display, ports, network or real-time waits. Each save root is unique and removed
in finally. New instances exercise re-entry; persistence uses public SaveGame /
LoadGame, never a literal schema version. Green-after is the product author's job.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class UiLifeChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const long Day = 86400L;
    private static int failures;

    private sealed class Scene : ISceneContent
    {
        public ObjectId Bed;
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(Bed, new[] {
                new ApproachPose(0f, 0f, 0f), new ApproachPose(0f, 0f, 0f) });
        }
    }
    private sealed class Fixture
    {
        public GameStack G;
        public ResidentId A, B;
        public HomeId Home, Small, Large;
        public HouseholdId Household, Other;
        public Scene Scene;
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
    private static void Run(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(false, name + ": " + e.GetType().Name + " " + e.Message); }
    }
    private static string Json(object value)
    {
        return JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { IncludeFields = true });
    }
    private static ResidentRecord Person(Fixture f, ResidentId id)
    {
        ResidentRecord r;
        Require(f.G.World.TryGetResident(id, out r), "real resident exists");
        return r;
    }
    private static Fixture NewFixture(int count = 2)
    {
        var w = new WorldState(701L, 1234L);
        var home = w.AllocateHome(Core, 8);
        var household = w.AllocateHousehold(Core, "Family", home.Id);
        var f = new Fixture { Home = home.Id, Household = household.Id };
        var appearances = new AppearanceStore();
        for (int i = 0; i < count; i++)
        {
            var r = w.AllocateResident(Core, "R" + i, LifeStage.Adult, household.Id, home.Id);
            w.SetMoney(Core, r.Id, 200L);
            appearances.Register(r.Id, new WorldRng(1234L + 7919L * (long)r.Id));
            if (i == 0) f.A = r.Id;
            if (i == 1) f.B = r.Id;
        }
        f.Small = w.AllocateHome(Core, 2).Id;
        f.Large = w.AllocateHome(Core, 8).Id;
        f.Other = w.AllocateHousehold(Core, "Other", f.Large).Id;
        var clock = new Clock(w); clock.SetPreset(AgingPreset.Off);
        var commands = new CommandArbitrator(w, clock);
        var executor = new ActivityExecutor(w, commands, clock);
        foreach (var r in w.ResidentsSnapshot()) executor.SetResidentPosition(r.Id, new ApproachPose(0f, 0f, 0f));
        var accounts = new Accounts(w); accounts.SetSharedBalance(Core, f.Household, 3000L);
        var ownership = new Ownership(w, accounts);
        var housing = new Housing(w, accounts, ownership, executor, appearances);
        housing.RegisterHome(Core, f.Home, 0L);
        housing.RegisterHome(Core, f.Small, 2500L);
        housing.RegisterHome(Core, f.Large, 4000L);
        f.G = new GameStack(w, clock, commands, executor, appearances, accounts,
            new Food(w, commands, executor, accounts), new Expenses(w, accounts), ownership, housing);
        w.SetBond(Core, f.A, f.B, 50f, 50f); w.SetBond(Core, f.B, f.A, 50f, 50f);
        housing.SetCaregiver(Core, f.A, true); housing.SetCaregiver(Core, f.B, true);
        f.G.Family.SetReproductiveCapabilities(Core, f.A, true, false);
        f.G.Family.SetReproductiveCapabilities(Core, f.B, false, true);
        f.G.Family.SetPartner(Core, f.A, f.B, true);
        ObjectId bed;
        do { bed = w.AllocateObject(Core); } while ((long)bed <= w.ResidentCount);
        f.Scene = new Scene { Bed = bed }; f.Scene.RegisterObjects(executor);
        f.G.Family.RegisterBed(Core, bed, f.Home);
        return f;
    }
    private static CommandEnvelope Envelope(Fixture f, CommandKind kind, CommandParameters parameters,
        ActivityId? activity = null, long? target = null)
    {
        return new CommandEnvelope(f.G.World.AllocateCommand(Core), f.G.World.Epoch,
            f.G.World.GameSeconds, CommandSource.PlayerHuman, Person(f, f.A).ResidentSequence + 1u,
            kind, f.A, activity, target, parameters);
    }
    private static WillRecord Will(Fixture f, int share = 60)
    {
        return new WillRecord(new[] { new MoneyShare(((long)f.Other).ToString(), share),
            new MoneyShare("Charity", 100 - share) }, 0, new ObjectId[0],
            "For my family", "Remember me at home", new[] { f.Other });
    }
    private static string Authority(Fixture f)
    {
        return Json(f.G.World.ExportSnapshot()) + Json(f.G.World.WriteAudit) + Json(f.G.Arbitrator.CommandLog)
            + Json(f.G.Accounts.Export()) + Json(f.G.Housing.Export())
            + Json(f.G.Ownership.Export()) + Json(f.G.Family.Export());
    }
    private static void Advance(Fixture f, long seconds)
    {
        Require(seconds >= 0L && seconds % 6L == 0L, "whole shared-clock ticks");
        f.G.Clock.AdvanceRealSeconds(Core, seconds / 60.0, () => f.G.Family.Tick(Core));
    }
    private static PregnancyRecord Conceive(Fixture f, Func<CommandEnvelope, CommandResult> submit)
    {
        var command = Envelope(f, CommandKind.DirectActivity, new CommandParameters.Direct(QueueMode.Immediate),
            new ActivityId("ACT-REL-06"), (long)f.Scene.Bed);
        Require(submit(command).Accepted, "real TryForBaby command accepted");
        Advance(f, 1830L);
        PregnancyRecord pregnancy;
        Require(f.G.Family.TryGetPregnancy(f.A, out pregnancy), "seed 1234 real conception");
        bool due = false;
        foreach (var e in f.G.World.Events)
            if (e.Event == pregnancy.ConceptionEvent) due = pregnancy.DueGameTime == e.GameSeconds + 3L * Day;
        Require(due, "real conception due three days after its event");
        return pregnancy;
    }
    private static Fixture Reload(Fixture f, string root)
    {
        var saved = SaveV1.SaveGame(f.G, root); Require(saved.Ok, "SaveGame: " + saved.Detail);
        var loaded = SaveV1.LoadGame(root, f.Scene); Require(loaded.Ok, "LoadGame: " + loaded.Notice);
        var g = loaded.Game;
        return new Fixture { G = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor, g.Appearances,
            g.Accounts, g.Food, g.Expenses, g.Ownership, g.Housing, g.Family),
            A = f.A, B = f.B, Home = f.Home, Small = f.Small, Large = f.Large,
            Household = f.Household, Other = f.Other, Scene = f.Scene };
    }
    private static ResidentId Child(Fixture f)
    {
        foreach (var r in f.G.World.ResidentsSnapshot())
            if (r.Stage == LifeStage.Newborn) return r.Id;
        throw new InvalidOperationException("real delivery did not produce newborn");
    }
    private static bool SamePregnancy(PregnancyRecord? value, PregnancyRecord expected)
    {
        return value.HasValue && value.Value.Carrier == expected.Carrier
            && value.Value.OtherParent == expected.OtherParent && value.Value.ConceptionEvent == expected.ConceptionEvent
            && value.Value.DueGameTime == expected.DueGameTime && value.Value.CapacityHold == expected.CapacityHold;
    }
    private static bool HasBlockedReason(HousingOffer offer)
    {
        return offer != null && (!offer.Fits || offer.Shortfall > 0L)
            && !string.IsNullOrWhiteSpace(offer.Reason) && !string.IsNullOrWhiteSpace(offer.NextSteps);
    }
    private static void SettleCarrierDeath(Fixture f)
    {
        f.G.World.SetStage(Core, f.A, LifeStage.Elder, 1f);
        f.G.World.SetAging(Core, f.A, 1.0, 1.0, true); f.G.Clock.SetPreset(AgingPreset.Moderate);
        var aging = new Aging(f.G.World, f.G.Clock);
        f.G.Clock.AdvanceRealSeconds(Core, 0.1, () => aging.Tick(Core));
        f.G.Clock.SetPreset(AgingPreset.Off);
        Require(!Person(f, f.A).IsAlive, "real elder death");
        new EstateCare(f.G).Tick(Core);
    }
    private static void RealProducerCheck(bool negative)
    {
        var f = NewFixture(5);
        var small = f.G.Housing.GetOffer(f.Household, f.Small, AccountRef.Shared(f.Household));
        var large = f.G.Housing.GetOffer(f.Household, f.Large, AccountRef.Shared(f.Household));
        Require(small.Price == 2500L && !small.Fits && large.Price == 4000L
            && large.Fits && large.Shortfall == 1000L && HasBlockedReason(small) && HasBlockedReason(large),
            "real five-person price/capacity producers");
        if (negative) large = new HousingOffer(large.Price, large.Shortfall, large.Fits, "", large.NextSteps);
        Check(HasBlockedReason(small) && HasBlockedReason(large),
            "CostsAndOwnership.SEIT-WORLD-07-P1.blocked-offer-has-reason");
        f = NewFixture();
        Require(f.G.Arbitrator.Submit(Envelope(f, CommandKind.WillEdit, new CommandParameters.Will(Will(f)))).Accepted,
            "real will producer");
        Require(f.G.Ownership.SetAgreement(Core, f.Home, 1000L,
            new[] { new OwnershipShare(f.Household, 60), new OwnershipShare(f.Other, 40, 3) }).Accepted,
            "real ownership producer");
        var pregnancy = Conceive(f, f.G.Family.Submit);
        string root = Path.Combine(Path.GetTempPath(), "blockparty-ui-life-producer-" + Guid.NewGuid().ToString("N"));
        try
        {
            f = Reload(f, root);
            PregnancyRecord restored;
            Require(f.G.Family.TryGetPregnancy(f.A, out restored) && SamePregnancy(restored, pregnancy), "real pregnancy codec");
            Advance(f, pregnancy.DueGameTime - f.G.World.GameSeconds);
            Require(f.G.World.CareAssignments.Count == 1 && f.G.World.CareAssignments[0].Dependent == Child(f),
                "real delivered care producer");
            SettleCarrierDeath(f);
            Require(f.G.World.CareAssignments.Count == 1 && f.G.World.CareAssignments[0].Guardian == f.B,
                "real ESTATE surviving caregiver producer");
            f = Reload(f, root);
            Require(f.G.World.CareAssignments.Count == 1 && f.G.World.CareAssignments[0].Guardian == f.B, "real care codec");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

#if !UI_LIFE_PRODUCER_CHECK
    private static void CostsAndOwnership(bool negative)
    {
        var f = NewFixture(5); var ui = new HouseholdLife(f.G);
        string before = Authority(f);
        var small = ui.HomeOffer(f.Household, f.Small, AccountRef.Shared(f.Household));
        var large = ui.HomeOffer(f.Household, f.Large, AccountRef.Shared(f.Household));
        var smallExpected = f.G.Housing.GetOffer(f.Household, f.Small, AccountRef.Shared(f.Household));
        var largeExpected = f.G.Housing.GetOffer(f.Household, f.Large, AccountRef.Shared(f.Household));
        Check(small.Price == 2500L && !small.Fits && small.Shortfall == 0L
            && large.Price == 4000L && large.Fits && large.Shortfall == 1000L
            && small.Reason == smallExpected.Reason && large.Reason == largeExpected.Reason
            && small.NextSteps == smallExpected.NextSteps && large.NextSteps == largeExpected.NextSteps,
            "CostsAndOwnership.SEIT-WORLD-07-P1.SEIT-AC-08.authoritative-prices-capacity-shortfall-next-steps");
        if (negative) large = new HousingOffer(large.Price, large.Shortfall, large.Fits, "", large.NextSteps);
        Check(HasBlockedReason(small) && HasBlockedReason(large),
            "CostsAndOwnership.SEIT-WORLD-07-P1.blocked-offer-has-reason");
        Check(Authority(f) == before, "CostsAndOwnership.SEIT-UI-02-P1.reads-do-not-mutate-authority");
        f.G.Accounts.SetSharedBalance(Core, f.Household, 5000L);
        Check(ui.HomeOffer(f.Household, f.Large, AccountRef.Shared(f.Household)).Shortfall == 0L
            && largeExpected.Shortfall == 1000L, "CostsAndOwnership.refresh-from-accounts");
        Require(f.G.Ownership.SetAgreement(Core, f.Home, 1000L,
            new[] { new OwnershipShare(f.Household, 60), new OwnershipShare(f.Other, 40, 3) }).Accepted, "real agreement");
        var view = ui.Inspect(f.A);
        Check(view.Resident == f.A && view.Household == f.Household && view.Home == f.Home && view.Members.Count == 5
            && Json(view.Ownership) == Json(f.G.Ownership.GetAgreement(f.Home)),
            "CostsAndOwnership.SEIT-UI-02-P1.members-and-ownership-match-authority");
        Require(f.G.Housing.JoinHousehold(Core, f.B, f.Other).Accepted, "real member move");
        Require(f.G.Ownership.SetAgreement(Core, f.Home, 2000L,
            new[] { new OwnershipShare(f.Household, 50), new OwnershipShare(f.Other, 50) }).Accepted, "real ownership edit");
        Check(view.Members.Count == 5 && view.Ownership.RecordedValue == 1000L
            && ui.Inspect(f.A).Members.Count == 4 && ui.Inspect(f.A).Ownership.RecordedValue == 2000L,
            "CostsAndOwnership.detached-view-refreshes-members-and-shares");
    }
    private static void HouseholdControls()
    {
        var f = NewFixture(); var ui = new HouseholdLife(f.G);
        Check(ui.FormHousehold("New", new[] { f.A }, f.Large).Accepted,
            "HouseholdControls.SEIT-AC-08.SEIT-AC-12.form-normal-command");
        var formed = Person(f, f.A).Household;
        Check(formed != f.Household && ui.JoinHousehold(f.B, formed).Accepted
            && Person(f, f.B).Household == formed, "HouseholdControls.join-normal-command");
        Check(ui.LeaveHousehold(f.B, f.Home).Accepted && Person(f, f.B).Household != formed,
            "HouseholdControls.leave-normal-command");
        Check(ui.MoveHousehold(formed, f.Home).Accepted && Person(f, f.A).Home == f.Home,
            "HouseholdControls.move-normal-command");
        f = NewFixture(5); ui = new HouseholdLife(f.G);
        string before = Authority(f);
        var refused = ui.MoveHousehold(f.Household, f.Small);
        Check(!refused.Accepted && !string.IsNullOrWhiteSpace(ui.Message) && Authority(f) == before,
            "HouseholdControls.SEIT-WORLD-07-N1.capacity-refusal-no-mutation-with-reason");
        var purchase = f.G.World.AllocateAction(Core); before = Authority(f);
        refused = ui.BuyHome(purchase, f.Household, f.Large, AccountRef.Shared(f.Household));
        Check(!refused.Accepted && !string.IsNullOrWhiteSpace(ui.Message) && Authority(f) == before,
            "HouseholdControls.SEIT-WORLD-07-N2.N3.N4.insufficient-funds-no-purchase-with-reason");
        f.G.Accounts.SetSharedBalance(Core, f.Household, 5000L);
        Check(ui.BuyHome(purchase, f.Household, f.Large, AccountRef.Shared(f.Household)).Accepted
            && f.G.Accounts.Balance(AccountRef.Shared(f.Household)) == 1000L
            && Person(f, f.A).Home == f.Large && f.G.Ownership.GetAgreement(f.Large).RecordedValue == 4000L,
            "HouseholdControls.buy-normal-command-debits-recorded-price-and-records-title");
        Check(ui.BuyHome(purchase, f.Household, f.Large, AccountRef.Shared(f.Household)).Accepted
            && f.G.Accounts.Balance(AccountRef.Shared(f.Household)) == 1000L,
            "HouseholdControls.purchase-replay-does-not-charge-twice");
        Check(ui.SetOwnership(f.Large, 4000L, new[] { new OwnershipShare(f.Household, 60), new OwnershipShare(f.Other, 40) }).Accepted,
            "HouseholdControls.ownership-normal-command");
        string agreement = Json(f.G.Ownership.Export());
        refused = ui.SetOwnership(f.Large, 4000L, new[] { new OwnershipShare(f.Household, 50), new OwnershipShare(f.Other, 40) });
        Check(!refused.Accepted && !string.IsNullOrWhiteSpace(ui.Message) && Json(f.G.Ownership.Export()) == agreement
            && ui.Inspect(f.A).Ownership.Shares[0].Percent == 60,
            "HouseholdControls.SEIT-AC-08.invalid-90-percent-ownership-preserves-last-valid");
    }
    private static void Logged(Fixture f, CommandEnvelope command, CommandResult result, string name)
    {
        var log = f.G.Arbitrator.CommandLog;
        Check(log.Count > 0 && log[log.Count - 1].Command == command.Id
            && log[log.Count - 1].Kind == command.Kind && log[log.Count - 1].Resident == command.Resident
            && log[log.Count - 1].Accepted == result.Accepted && log[log.Count - 1].Reason == result.Reason,
            name + ".normal-command-log");
    }
    private static void WillAndCommandControls()
    {
        var f = NewFixture(); var ui = new HouseholdLife(f.G);
        Check(ui.Inspect(f.A).Will == null, "WillAndCommandControls.SEIT-LIFE-04-P1.explicit-no-will");
        foreach (int percent in new[] { 60, 50 })
        {
            var command = Envelope(f, CommandKind.WillEdit, new CommandParameters.Will(Will(f, percent)));
            var result = ui.Submit(command); Logged(f, command, result, "WillAndCommandControls.SEIT-IF-CMD-P3");
            WillRecord authority; Require(f.G.Arbitrator.TryGetLastWill(f.A, out authority), "authoritative will stored");
            Check(result.Accepted && Json(ui.Inspect(f.A).Will) == Json(authority)
                && ui.Inspect(f.A).Will.MoneyShares[0].Percent == percent,
                "WillAndCommandControls.SEIT-LIFE-04-P1.author-update-inspect-exact-values");
        }
        var valid = Json(ui.Inspect(f.A).Will);
        var invalid = new WillRecord(new[] { new MoneyShare("Charity", 90) }, 0,
            new ObjectId[0], "invalid", "", new HouseholdId[0]);
        var bad = Envelope(f, CommandKind.WillEdit, new CommandParameters.Will(invalid));
        var rejected = ui.Submit(bad); Logged(f, bad, rejected, "WillAndCommandControls.SEIT-IF-CMD-N6");
        Check(!rejected.Accepted && !string.IsNullOrWhiteSpace(ui.Message) && Json(ui.Inspect(f.A).Will) == valid,
            "WillAndCommandControls.SEIT-LIFE-04-N1.invalid-will-keeps-valid-and-reason");
        var care = Envelope(f, CommandKind.CaregiverAssign, new CommandParameters.Caregiver(f.B));
        var assigned = ui.Submit(care); Logged(f, care, assigned, "WillAndCommandControls.caregiver");
        ResidentId guardian = default(ResidentId);
        Check(assigned.Accepted && f.G.Arbitrator.TryGetCaregiver(f.A, out guardian) && guardian == f.B,
            "WillAndCommandControls.caregiver-normal-command");
        var clear = Envelope(f, CommandKind.WillEdit, new CommandParameters.Will(null));
        var cleared = ui.Submit(clear); Logged(f, clear, cleared, "WillAndCommandControls.clear");
        Check(cleared.Accepted && ui.Inspect(f.A).Will == null,
            "WillAndCommandControls.SEIT-LIFE-04-P1.clear-explicit-no-will");
        string root = Path.Combine(Path.GetTempPath(), "blockparty-ui-life-will-" + Guid.NewGuid().ToString("N"));
        try
        {
            f = Reload(f, root); ui = new HouseholdLife(f.G);
            Check(ui.Inspect(f.A).Will == null, "WillAndCommandControls.cleared-will-roundtrip");
            var stale = new CommandEnvelope(clear.Id, clear.Epoch, clear.IssuedAtGameSeconds,
                clear.Source, clear.ResidentSequence, clear.Kind, clear.Resident, clear.Activity, clear.TargetId, clear.Parameters);
            var result = ui.Submit(stale);
            Check(!result.Accepted && result.Reason == RejectReason.StaleEpoch && ui.Inspect(f.A).Will == null,
                "WillAndCommandControls.SEIT-IF-CMD-N6.stale-envelope-not-rebuilt-or-bypassed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void FamilyCareAndReload()
    {
        var f = NewFixture(); var ui = new HouseholdLife(f.G);
        Check(!ui.Inspect(f.A).PendingDelivery.HasValue && ui.Inspect(f.A).Care.Count == 0,
            "FamilyCareAndReload.explicit-no-pregnancy-no-care");
        f.G.Family.SetPartner(Core, f.A, f.B, false);
        var refused = ui.Submit(Envelope(f, CommandKind.DirectActivity, new CommandParameters.Direct(QueueMode.Immediate),
            new ActivityId("ACT-REL-06"), (long)f.Scene.Bed));
        Check(!refused.Accepted && !string.IsNullOrWhiteSpace(ui.Message)
            && !ui.Inspect(f.A).PendingDelivery.HasValue && f.G.Arbitrator.GetCurrent(f.A) == null,
            "FamilyCareAndReload.SEIT-LIFE-01-N1.N6.family-refusal-is-not-arbitrator-bypass");
        f.G.Family.SetPartner(Core, f.A, f.B, true);
        var pregnancy = Conceive(f, ui.Submit);
        var action = f.G.Arbitrator.CommandLog[f.G.Arbitrator.CommandLog.Count - 1];
        Check(action.Kind == CommandKind.DirectActivity && action.Accepted && action.Resident == f.A
            && SamePregnancy(ui.Inspect(f.A).PendingDelivery, pregnancy),
            "FamilyCareAndReload.SEIT-LIFE-01-P1.SEIT-IF-EXEC-P3.P4.normal-command-pending-delivery");
        var edit = Envelope(f, CommandKind.WillEdit, new CommandParameters.Will(Will(f)));
        Require(ui.Submit(edit).Accepted, "will before pregnancy save");
        string root = Path.Combine(Path.GetTempPath(), "blockparty-ui-life-family-" + Guid.NewGuid().ToString("N"));
        try
        {
            f = Reload(f, root); ui = new HouseholdLife(f.G);
            Check(SamePregnancy(ui.Inspect(f.A).PendingDelivery, pregnancy)
                && Json(ui.Inspect(f.A).Will) == Json(Will(f)),
                "FamilyCareAndReload.SEIT-IF-SAVE-P2.N4.pending-and-will-roundtrip");
            f.G.Clock.Pause(); long now = f.G.World.GameSeconds;
            f.G.Clock.AdvanceRealSeconds(Core, 1.0, () => f.G.Family.Tick(Core));
            Check(f.G.World.GameSeconds == now && SamePregnancy(ui.Inspect(f.A).PendingDelivery, pregnancy),
                "FamilyCareAndReload.SEIT-IF-TIME-P1.P2.N5.paused-pending-does-not-deliver");
            f.G.Clock.Resume(); Advance(f, pregnancy.DueGameTime - f.G.World.GameSeconds);
            var child = Child(f); var view = ui.Inspect(child);
            Check(!ui.Inspect(f.A).PendingDelivery.HasValue && view.Care.Count == 1
                && Json(view.Care[0]) == Json(f.G.World.CareAssignments[0]) && view.Care[0].Dependent == child,
                "FamilyCareAndReload.SEIT-IF-LIFE-P1.P2.N2.real-birth-care-matches-authority");
            SettleCarrierDeath(f);
            Check(ui.Inspect(child).Care.Count == 1 && ui.Inspect(child).Care[0].Guardian == f.B
                && Json(ui.Inspect(child).Care[0]) == Json(f.G.World.CareAssignments[0]),
                "FamilyCareAndReload.SEIT-LIFE-04-P7.authoritative-surviving-caregiver-refresh");
            string care = Json(ui.Inspect(child).Care[0]);
            f = Reload(f, root); ui = new HouseholdLife(f.G);
            new EstateCare(f.G).Tick(Core);
            Check(!ui.Inspect(f.A).PendingDelivery.HasValue && ui.Inspect(child).Care.Count == 1
                && Json(ui.Inspect(child).Care[0]) == care && f.G.World.ResidentCount == 3,
                "FamilyCareAndReload.SEIT-LIFE-01-N2.SEIT-LIFE-04-N5.SEIT-IF-LIFE-N3.no-duplicate-after-reload");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
#endif
    public static int Main(string[] args)
    {
        failures = 0;
#if UI_LIFE_PRODUCER_CHECK
        Run("real-producers", () => RealProducerCheck(Array.IndexOf(args, "negative-fixture") >= 0));
        if (failures == 0) Console.WriteLine("UI-LIFE PRODUCERS PASS (not package acceptance)");
#else
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        Run("CostsAndOwnership", () => CostsAndOwnership(negative));
        Run("HouseholdControls", HouseholdControls);
        Run("WillAndCommandControls", WillAndCommandControls);
        Run("FamilyCareAndReload", FamilyCareAndReload);
        if (failures == 0) Console.WriteLine("UI-LIFE ALL PASS");
#endif
        return failures == 0 ? 0 : 1;
    }
}
