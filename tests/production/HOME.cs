/* HOME@1 TDD contract, product implementation belongs in Activities/Home.cs.
Blockparty.Activities.Home follows the existing Food service shape:
  Home(WorldState, CommandArbitrator, ActivityExecutor, Accounts)
  SetHomeState(StateWriter, HomeId, float cleanliness, int householdSupplies)
  SetDamaged(StateWriter, ObjectId, bool); IsDamaged(ObjectId)
  Cleanliness(HomeId); HouseholdSupplies(HomeId)
  Submit(CommandEnvelope); Tick(StateWriter); Complete(ResidentId, ActionInstanceId)
Tick drives the real executor; Complete is a replay notification, not permission
to finish early. State setters initialize fixtures through an authorized writer.
SAVE-V2 integration: GameStack's existing seven-argument constructor plus Home
as an eighth argument captures that live service; LoadedGame.Home restores it.
No literal save version, replacement executor, accounts or codec is used here.

Oracles: SDoc BP-ACT-11, section 5 ACT-HOME-01..03 (candidate values, unmeasured),
SEIT-ACT-11-P1/N1. New suite is required by the HOME package runner convention;
existing ECONOMY/FOOD/SAVE suites do not exercise home management effects.
Resource group isolated-csharp: no editor, display, network or shared resources.
Only public-save round trips use a unique temporary directory, always removed.
Local AC-04 home effects and N1 resume/cancel checks do not establish packaged
acceptance. SEIT-IF-EXEC-N9 furnishing/placement/restore remains at its owning gate;
an insufficient-funds repair is not evidence of that distinct furnishing case.
Run with negative-fixture to replace the successful repair's 50 funds with the
catalog's insufficient 10: its normal completion assertion must then FAIL.
*/
using System;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class HomeChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private static int failures;

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public Home Home;
        public ResidentRecord Resident;
        public ObjectId Stove;
        public ObjectId Other;
        public HomeId OtherHome;
    }

    private sealed class Scene : ISceneContent
    {
        public ObjectId Stove, Other;
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(Stove, new[] { new ApproachPose(0f, 0f, 0f) });
            executor.RegisterObject(Other, new[] { new ApproachPose(0f, 0f, 0f) });
        }
    }

    private static Fixture NewFixture(long funds = 50L, int supplies = 4)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 42L, 1, new[] { "Home resident" });
        f.Resident = f.World.ResidentsSnapshot()[0];
        f.World.SetStage(Core, f.Resident.Id, LifeStage.Adult, 0f);
        f.World.SetMoney(Core, f.Resident.Id, funds);
        f.OtherHome = f.World.AllocateHome(Core, 8).Id;
        f.Clock = new Clock(f.World);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, f.Accounts);
        f.Home = new Home(f.World, f.Arbitrator, f.Executor, f.Accounts);
        // Keep object ids distinct from resident ids, as in the real SAVE suite.
        do { f.Stove = f.World.AllocateObject(Core); } while ((long)f.Stove <= (long)f.Resident.Id);
        f.Other = f.World.AllocateObject(Core);
        new Scene { Stove = f.Stove, Other = f.Other }.RegisterObjects(f.Executor);
        f.Executor.SetResidentPosition(f.Resident.Id, new ApproachPose(0f, 0f, 0f));
        f.Home.SetHomeState(Core, f.Resident.Home, 40f, supplies);
        f.Home.SetHomeState(Core, f.OtherHome, 80f, 7);
        f.Home.SetDamaged(Core, f.Stove, true);
        f.Home.SetDamaged(Core, f.Other, true);
        return f;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord resident;
        if (!f.World.TryGetResident(f.Resident.Id, out resident)) throw new Exception("Fixture resident missing");
        return resident;
    }

    private static int Handiness(Fixture f)
    {
        foreach (SkillEntry skill in Resident(f).Skills)
            if (skill.SkillId == "Handiness") return skill.Points;
        return 0;
    }

    private static CommandEnvelope Command(Fixture f, string code, long? target,
        CommandKind kind = CommandKind.DirectActivity)
    {
        ResidentRecord r = Resident(f);
        return new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            kind, r.Id, new ActivityId(code), target,
            kind == CommandKind.DirectActivity ? new CommandParameters.Direct(QueueMode.Immediate) : null);
    }

    private static ActionInstanceId Begin(Fixture f, string code)
    {
        CommandResult result = f.Home.Submit(Command(f, code, (long)f.Stove));
        if (!result.Accepted) throw new Exception("HOME " + code + " refused: " + result.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Resident.Id).Action;
        f.Home.Tick(Exec);
        f.Home.Tick(Exec);
        if (f.Executor.GetExecution(action).Stage != ExecutionStage.Perform)
            throw new Exception("HOME " + code + " did not reach Perform");
        return action;
    }

    private static void Advance(Fixture f, int seconds)
    {
        for (int elapsed = 0; elapsed < seconds; elapsed += 6)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Home.Tick(Exec);
        }
    }

    private static bool Near(double actual, double expected)
    {
        // Allow accumulated float roundoff in cleanliness's per-tick daily decay.
        return Math.Abs(actual - expected) < 0.05;
    }

    private static bool SpentOnce(Fixture f, ActionInstanceId action, long amount)
    {
        if (f.Accounts.Ledger.Count != (amount == 0 ? 0 : 1)) return false;
        if (amount == 0) return true;
        AccountTransaction tx = f.Accounts.Ledger[0];
        return tx.Action == action && tx.Amount == amount
            && tx.Source.Equals(AccountRef.Personal(f.Resident.Id));
    }

    private static string State(Fixture f)
    {
        return f.World.ComputeStateHash() + "|" + f.Home.Cleanliness(f.Resident.Home)
            + "|" + f.Home.HouseholdSupplies(f.Resident.Home) + "|" + f.Home.IsDamaged(f.Stove)
            + "|" + f.Home.IsDamaged(f.Other) + "|" + f.Home.Cleanliness(f.OtherHome)
            + "|" + f.Home.HouseholdSupplies(f.OtherHome) + "|" + f.Accounts.Ledger.Count;
    }

    private static void Replay(Fixture f, ActionInstanceId action, string name)
    {
        string before = State(f);
        f.Home.Complete(f.Resident.Id, action);
        f.Home.Complete(f.Resident.Id, action);
        f.Home.Tick(Exec);
        Check(State(f) == before, name + "-replay-no-second-effect-or-spend");
    }

    private static void Completion(bool negativeFixture)
    {
        Fixture clean = NewFixture();
        Fixture control = NewFixture();
        ActionInstanceId cleaning = Begin(clean, "ACT-HOME-01");
        Advance(clean, 1800);
        Advance(control, 1800);
        Check(Near(clean.Home.Cleanliness(clean.Resident.Home) - control.Home.Cleanliness(control.Resident.Home), 30)
            && clean.Home.HouseholdSupplies(clean.Resident.Home) == 4
            && Near(clean.Home.Cleanliness(clean.OtherHome), control.Home.Cleanliness(control.OtherHome))
            && clean.Home.IsDamaged(clean.Stove) && clean.Home.IsDamaged(clean.Other)
            && Resident(clean).Money == 50 && SpentOnce(clean, cleaning, 0)
            && clean.Executor.GetExecution(cleaning).Outcome == TerminalOutcome.Completed,
            "SEIT-ACT-11-P1-clean-target-plus30-no-charge");
        Replay(clean, cleaning, "SEIT-AC-04-clean");

        Fixture repair = NewFixture(negativeFixture ? 10 : 50);
        ActionInstanceId repairing = Begin(repair, "ACT-HOME-02");
        Advance(repair, 3600);
        Check(!repair.Home.IsDamaged(repair.Stove) && repair.Home.IsDamaged(repair.Other)
            && Handiness(repair) == 10 && Resident(repair).Money == 35
            && SpentOnce(repair, repairing, 15)
            && repair.Executor.GetExecution(repairing).Outcome == TerminalOutcome.Completed,
            "SEIT-ACT-11-P1-repair-target-cost15-handiness10");
        Replay(repair, repairing, "SEIT-AC-04-repair");

        Fixture stock = NewFixture(100, 0);
        ActionInstanceId stocking = Begin(stock, "ACT-HOME-03");
        Advance(stock, 900);
        Check(stock.Home.HouseholdSupplies(stock.Resident.Home) == 4
            && stock.Home.HouseholdSupplies(stock.OtherHome) == 7
            && stock.Home.IsDamaged(stock.Stove) && stock.Home.IsDamaged(stock.Other)
            && Resident(stock).Money == 0 && SpentOnce(stock, stocking, 100)
            && stock.Executor.GetExecution(stocking).Outcome == TerminalOutcome.Completed,
            "SEIT-ACT-11-P1-stock-four-at25-each-target-home-only");
        Replay(stock, stocking, "SEIT-AC-04-stock");
        double stockedStart = stock.Home.Cleanliness(stock.Resident.Home);
        Fixture empty = NewFixture(100, 0);
        Advance(stock, 86400);
        Advance(empty, 86400);
        Check(Near(stockedStart - stock.Home.Cleanliness(stock.Resident.Home), 2)
            && Near(40 - empty.Home.Cleanliness(empty.Resident.Home), 4),
            "SEIT-ACT-11-P1-stock-restores-normal-daily-decay");
    }

    private static void InsufficientFunds()
    {
        foreach (long funds in new[] { 0L, 10L, 14L })
        {
            Fixture f = NewFixture(funds);
            CommandEnvelope command = Command(f, "ACT-HOME-02", (long)f.Stove);
            string before = State(f);
            CommandResult result = f.Home.Submit(command);
            Check(!result.Accepted && result.Reason == RejectReason.InsufficientFunds && State(f) == before,
                "SEIT-ACT-11-N1-repair-below15-refused-unchanged-" + funds);
            Advance(f, 3600);
            Check(f.Home.IsDamaged(f.Stove) && Handiness(f) == 0 && Resident(f).Money == funds
                && f.Accounts.Ledger.Count == 0,
                "SEIT-ACT-11-N1-refusal-no-later-repair-or-debit-" + funds);
        }
        Fixture exact = NewFixture(15);
        ActionInstanceId action = Begin(exact, "ACT-HOME-02");
        Advance(exact, 3600);
        Check(!exact.Home.IsDamaged(exact.Stove) && Resident(exact).Money == 0
            && Handiness(exact) == 10 && SpentOnce(exact, action, 15),
            "SEIT-ACT-11-N1-exact15-repair-succeeds");
    }

    private static void CancelledClean()
    {
        Fixture f = NewFixture();
        Fixture control = NewFixture();
        ActionInstanceId action = Begin(f, "ACT-HOME-01");
        Advance(f, 1740);
        Check(f.Home.Submit(Command(f, "", null, CommandKind.CancelCurrent)).Accepted,
            "SEIT-ACT-11-N1-clean-cancel-at29min-accepted");
        f.Home.Tick(Exec);
        f.Home.Complete(f.Resident.Id, action);
        Advance(f, 60);
        Advance(control, 1800);
        Check(Near(f.Home.Cleanliness(f.Resident.Home), control.Home.Cleanliness(control.Resident.Home))
            && f.Home.HouseholdSupplies(f.Resident.Home) == 4 && Resident(f).Money == 50
            && f.Accounts.Ledger.Count == 0
            && f.Executor.GetExecution(action).Outcome == TerminalOutcome.Interrupted,
            "SEIT-ACT-11-N1-cancelled-clean-no-completion-effect");
    }

    private static Fixture RoundTrip(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(new GameStack(f.World, f.Clock, f.Arbitrator,
            f.Executor, new AppearanceStore(), f.Accounts, f.Food, f.Home), directory);
        if (!saved.Ok) throw new Exception("HOME save failed: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene { Stove = f.Stove, Other = f.Other });
        if (!loaded.Ok) throw new Exception("HOME load failed: " + loaded.Notice);
        LoadedGame game = loaded.Game;
        return new Fixture { World = game.World, Clock = game.Clock, Arbitrator = game.Arbitrator,
            Executor = game.Executor, Accounts = game.Accounts, Food = game.Food, Home = game.Home,
            Resident = f.Resident, Stove = f.Stove, Other = f.Other, OtherHome = f.OtherHome };
    }

    private static void SaveResume()
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-HOME-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture f = NewFixture();
            Begin(f, "ACT-HOME-02");
            Advance(f, 2400);
            Check(f.Home.IsDamaged(f.Stove) && Resident(f).Money == 50 && Handiness(f) == 0,
                "SEIT-ACT-11-N1-repair40of60-no-early-effects");
            f = RoundTrip(f, Path.Combine(root, "in-progress"));
            Check(f.Home.IsDamaged(f.Stove) && Resident(f).Money == 50 && Handiness(f) == 0,
                "SEIT-ACT-11-N1-load-no-early-repair-or-debit");
            ActionEntry current = f.Arbitrator.GetCurrent(f.Resident.Id);
            if (current == null) throw new Exception("HOME saved repair not resumed");
            ActionInstanceId resumed = current.Action;
            Advance(f, 1200);
            Check(!f.Home.IsDamaged(f.Stove) && f.Home.IsDamaged(f.Other)
                && Handiness(f) == 10 && Resident(f).Money == 35 && SpentOnce(f, resumed, 15),
                "SEIT-ACT-11-N1-resume-remaining20min-cost15-once");
            Replay(f, resumed, "SEIT-ACT-11-N1-resumed-repair");
            f.Home.SetHomeState(Core, f.Resident.Home, 70f, 4);
            f = RoundTrip(f, Path.Combine(root, "completed"));
            Check(!f.Home.IsDamaged(f.Stove) && f.Home.IsDamaged(f.Other)
                && Near(f.Home.Cleanliness(f.Resident.Home), 70)
                && f.Home.HouseholdSupplies(f.Resident.Home) == 4
                && Handiness(f) == 10 && Resident(f).Money == 35 && f.Accounts.Ledger.Count == 1,
                "SEIT-ACT-11-N1-home-object-and-payment-roundtrip");
            Replay(f, resumed, "SEIT-ACT-11-N1-completed-repair-after-reload");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = NewFixture();
            ActionInstanceId action = Begin(f, "ACT-HOME-02");
            f.Clock.AdvanceRealSeconds(Core, 60.0, () => f.Home.Tick(writer));
            string actual = State(f);
            Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed
                && !f.Home.IsDamaged(f.Stove) && Handiness(f) == 10 && SpentOnce(f, action, 15)
                && (expected == null || actual == expected),
                "HOME-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    public static int Main(string[] args)
    {
        bool negativeFixture = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negativeFixture) Console.WriteLine("HOME negative-fixture: successful repair fixture has only 10 funds");
        Run("HOME-completion", () => Completion(negativeFixture));
        Run("HOME-insufficient-funds", InsufficientFunds);
        Run("HOME-cancelled-clean", CancelledClean);
        Run("HOME-save-resume", SaveResume);
        Run("home-tick-writer-contract", AcceptedWriterTick);
        if (failures == 0) Console.WriteLine("HOME ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
