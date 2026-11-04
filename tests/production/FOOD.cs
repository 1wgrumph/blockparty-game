/* FOOD@1 red contract, implemented only in Assets/Blockparty/Activities/Food.cs.
Namespace Blockparty.Activities, sealed class Food:
  Food(WorldState world, CommandArbitrator arbitrator, ActivityExecutor executor,
       Accounts accounts)
  void SetHomeStock(StateWriter writer, HomeId home, int groceries, int meals)
  int GroceryUnits(HomeId home); int PreparedMeals(HomeId home)
  CommandResult Submit(CommandEnvelope command)
  void Tick(StateWriter writer)
  CommandResult Complete(ResidentId resident, ActionInstanceId action)
Submit validates food inputs before forwarding to the real arbitrator; non-food
commands also use that arbitrator. Tick drives the real executor and food effects
with StateWriter.ActivityExecutor. Do not also tick the executor externally.
Reserve groceries at Perform; consume/reward only on natural timed completion;
release on interruption/failure. Complete is a replayable completion notification,
never authority to finish an early, interrupted, mismatched or unknown action.
A repeated completion is refused with a reason and no mutation. Costs use Accounts.
SetHomeStock is fixture/simulation initialization through an authorized StateWriter.
Successful food completion appends one WorldEvent: Kind "FoodCompleted", Actor
resident, Detail invariant text (integer catalog units):
  action=<id>;activity=<code>;hunger=<delta>;health=<delta>;fun=<delta>;
  cooking=<points>;groceries=<delta>;meals=<delta>;money=<delta>
(The detail is a single line with no whitespace.) Effects/ledger/slot release and
CommandArbitrator.CompleteCurrent must agree once per ActionInstanceId.
Existing API only: Blockparty.Contracts and Blockparty.Simulation, plus Reflection.
Oracles: REV-0 BP-ACT-03 / BP-SEC-05 catalog, BP-ACT-01/02, BP-ECO-01/04,
SEIT-ACT-03-P1/N1/N2 and SEIT-IF-EXEC-N4. Numeric values are catalog starting
values, not measured tuning. No needs-decay service exists in this fixture, so
exact deltas need no decay correction. Resource group isolated-csharp, no engine.
Local evidence only: SEIT-AC-04/05/20 full acceptance and N1 process/save/reload
qualification remain pending their recorded gates; this file does not replace them.
*/
using System;
using System.Reflection;
using Blockparty.Activities;
using Blockparty.Automation;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class FoodChecks
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
        public ResidentRecord Resident;
        public ObjectId Station;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static Fixture NewFixture(int groceries = 4, int meals = 0,
        long money = 50L, LifeStage stage = LifeStage.Adult)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(7L, 42L, 0, null);
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord household = f.World.AllocateHousehold(Core, "Food household", home.Id);
        f.Resident = f.World.AllocateResident(Core, "Cook", stage, household.Id, home.Id);
        f.World.SetMoney(Core, f.Resident.Id, money);
        for (int i = 0; i < StateSchema.NeedCount; i++)
            f.World.SetNeed(Core, f.Resident.Id, (Need)i, 40.0f);
        f.Clock = new Clock(f.World);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, f.Accounts);
        f.Station = f.World.AllocateObject(Core);
        f.Executor.RegisterObject(f.Station, new[] { new ApproachPose(0.0f, 0.0f, 0.0f) });
        f.Executor.SetResidentPosition(f.Resident.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
        f.Food.SetHomeStock(Core, home.Id, groceries, meals);
        return f;
    }

    private static CommandEnvelope Command(Fixture f, string code, long? target,
        QueueMode mode = QueueMode.Immediate)
    {
        ResidentRecord r;
        f.World.TryGetResident(f.Resident.Id, out r);
        return new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId(code), target,
            new CommandParameters.Direct(mode));
    }

    private static ActionInstanceId Begin(Fixture f, string code)
    {
        CommandResult result = f.Food.Submit(Command(f, code, (long)f.Station));
        if (!result.Accepted) throw new Exception("Fixture command refused: " + code + " " + result.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Resident.Id).Action;
        f.Food.Tick(Exec);
        f.Food.Tick(Exec); // Dispatch then align at the owned station, without time passing.
        if (f.Executor.GetExecution(action).Stage != ExecutionStage.Perform)
            throw new Exception("Fixture did not reach Perform: " + code);
        return action;
    }

    private static void Advance(Fixture f, int seconds)
    {
        // Clock and NAV share the existing six-game-second fixed tick.
        for (int elapsed = 0; elapsed < seconds; elapsed += 6)
        {
            f.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Food.Tick(Exec);
        }
    }

    private static ResidentRecord Resident(Fixture f)
    {
        ResidentRecord r;
        f.World.TryGetResident(f.Resident.Id, out r);
        return r;
    }

    private static int CookingPoints(Fixture f)
    {
        foreach (SkillEntry skill in Resident(f).Skills)
            if (skill.SkillId == "Cooking") return skill.Points;
        return 0;
    }

    private static bool Needs(Fixture f, float hunger, float health, float fun)
    {
        ResidentRecord r = Resident(f);
        for (int i = 0; i < StateSchema.NeedCount; i++)
        {
            Need n = (Need)i;
            float expected = n == Need.Hunger ? hunger : n == Need.Health ? health : n == Need.Fun ? fun : 40.0f;
            if (r.GetNeed(n) != expected) return false;
        }
        return true;
    }

    private static int Committed(Fixture f, ActionInstanceId action)
    {
        int count = 0;
        foreach (EffectLedgerEntry e in f.World.EffectLedger)
            if (e.Action == action && e.State == LedgerState.Committed) count++;
        return count;
    }

    private static bool EventIs(Fixture f, ActionInstanceId action, string code, string deltas)
    {
        int count = 0;
        string expected = "action=" + ((long)action).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";activity=" + code + ";" + deltas;
        foreach (WorldEvent e in f.World.Events)
        {
            if (e.Kind != "FoodCompleted") continue;
            count++;
            if (e.Actor != f.Resident.Id || e.Detail != expected) return false;
        }
        return count == 1;
    }

    private static string Snapshot(Fixture f)
    {
        return f.World.ComputeStateHash().ToString() + ":" + f.Food.GroceryUnits(f.Resident.Home)
            + ":" + f.Food.PreparedMeals(f.Resident.Home) + ":" + f.Accounts.Ledger.Count
            + ":" + f.Executor.ReservationLog.Count + ":" + f.Arbitrator.GetPending(f.Resident.Id).Length
            + ":" + (f.Arbitrator.GetCurrent(f.Resident.Id) == null ? "idle"
                : ((long)f.Arbitrator.GetCurrent(f.Resident.Id).Action).ToString());
    }

    private static void RefusedUnchanged(Fixture f, CommandEnvelope command,
        RejectReason reason, string name)
    {
        string before = Snapshot(f);
        CommandResult result = f.Food.Submit(command);
        Check(!result.Accepted && result.Reason == reason && Snapshot(f) == before, name);
    }

    private static bool NoStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsLiteral && !field.IsInitOnly) return false;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (!NoStaticMutable(nested)) return false;
        return true;
    }

    private static void AdoptedRefusals()
    {
        // F1: the real BOT/autonomy routes bypass Food.Submit's resource checks.
        foreach (bool bot in new[] { true, false })
        foreach (string code in new[] { "ACT-FOOD-01", "ACT-FOOD-02", "ACT-FOOD-03", "ACT-FOOD-04" })
        {
            Fixture f = NewFixture(0, 0, 0L);
            if (bot)
            {
                f.Arbitrator.EnableAutomation();
                RulePlayer player = new RulePlayer(f.Arbitrator);
                PlayerObservation observation = new PlayerObservation(f.Resident.Id,
                    f.Arbitrator.CurrentStamp(f.Resident.Id), f.World.GameSeconds,
                    Array.AsReadOnly(new float[StateSchema.NeedCount]),
                    Array.AsReadOnly(new[] { new AvailableAction(new ActivityId(code), (long)f.Station) }));
                if (!player.Apply(player.Decide(observation)).Accepted) throw new Exception("BOT fixture refused");
            }
            else if (!f.Arbitrator.EnqueueAutonomous(f.Resident.Id, new ActivityId(code), (long)f.Station))
                throw new Exception("Autonomous fixture refused");
            ActionInstanceId action = f.Arbitrator.GetPending(f.Resident.Id)[0].Action;
            Advance(f, 3000);
            ExecutionRecord execution = f.Executor.GetExecution(action);
            Check(Needs(f, 40f, 40f, 40f) && CookingPoints(f) == 0 && Resident(f).Money == 0
                && f.Food.GroceryUnits(f.Resident.Home) == 0 && f.Food.PreparedMeals(f.Resident.Home) == 0
                && f.Accounts.Ledger.Count == 0 && Committed(f, action) == 0
                && (execution == null || execution.Outcome != TerminalOutcome.Completed)
                && f.Arbitrator.GetCurrent(f.Resident.Id) == null
                && !f.Executor.GetHold(f.Station, 0).HasValue && (!bot || f.Arbitrator.AutomationEnabled),
                "F1 adopted-unaffordable-no-effects-" + (bot ? "BOT-" : "autonomy-") + code);
        }

        Fixture spent = NewFixture();
        ActionInstanceId spending = Begin(spent, "ACT-FOOD-04");
        spent.Accounts.Transfer(Core, spent.World.AllocateAction(Core), AccountRef.Personal(spent.Resident.Id),
            AccountRef.Expense("Other purchase"), 45L);
        Advance(spent, 900);
        Check(Needs(spent, 40f, 40f, 40f) && Resident(spent).Money == 5L
            && spent.Accounts.Ledger.Count == 1 && Committed(spent, spending) == 0
            && !spent.Executor.GetHold(spent.Station, 0).HasValue,
            "F1 funds-spent-before-completion-no-unpaid-reward");

        Fixture shared = NewFixture(1);
        ResidentRecord first = shared.Resident;
        shared.Arbitrator.EnqueueAutonomous(first.Id, new ActivityId("ACT-FOOD-01"), null);
        shared.Resident = shared.World.AllocateResident(Core, "Competing cook", LifeStage.Adult,
            first.Household, first.Home);
        shared.Arbitrator.EnqueueAutonomous(shared.Resident.Id, new ActivityId("ACT-FOOD-01"), null);
        Advance(shared, 3000);
        Check(shared.Food.GroceryUnits(first.Home) == 0 && shared.Food.PreparedMeals(first.Home) == 2
            && first.GetNeed(Need.Hunger) == 85f && shared.Arbitrator.GetCurrent(first.Id) == null
            && shared.Arbitrator.GetCurrent(shared.Resident.Id) == null,
            "F1 adopted-cooks-share-one-grocery-without-negative-stock");
    }

    private static void AcceptedWriterTick()
    {
        string expected = null;
        foreach (StateWriter writer in new[] { Exec, Core, StateWriter.CommandArbitrator })
        {
            Fixture f = NewFixture();
            ActionInstanceId action = Begin(f, "ACT-FOOD-01");
            f.Clock.AdvanceRealSeconds(Core, 45.0, () => f.Food.Tick(writer));
            string actual = Snapshot(f);
            Check(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed
                && Committed(f, action) == 1 && f.Food.GroceryUnits(f.Resident.Home) == 3
                && f.Food.PreparedMeals(f.Resident.Home) == 2
                && (expected == null || actual == expected),
                "FOOD-Tick-" + writer + "-matches-executor-path");
            if (expected == null) expected = actual;
        }
    }

    public static int Main()
    {
        failures = 0;
        try { AcceptedWriterTick(); }
        catch (Exception e) { Check(false, "food-tick-writer-contract " + e); }
        AdoptedRefusals();
        Fixture cook = NewFixture();
        ActionInstanceId cooking = Begin(cook, "ACT-FOOD-01");
        Advance(cook, 2694);
        Check(Needs(cook, 40.0f, 40.0f, 40.0f) && CookingPoints(cook) == 0
            && cook.Food.GroceryUnits(cook.Resident.Home) == 4 && cook.Food.PreparedMeals(cook.Resident.Home) == 0
            && Committed(cook, cooking) == 0 && Resident(cook).Money == 50L,
            "SEIT-ACT-03-P1 cook-no-gains-before-45-minutes");
        Advance(cook, 6);
        Check(Needs(cook, 85.0f, 42.0f, 40.0f) && CookingPoints(cook) == 10
            && cook.Food.GroceryUnits(cook.Resident.Home) == 3 && cook.Food.PreparedMeals(cook.Resident.Home) == 2
            && Resident(cook).Money == 50L && cook.Accounts.Ledger.Count == 0
            && cook.Executor.GetExecution(cooking).Outcome == TerminalOutcome.Completed
            && !cook.Executor.GetHold(cook.Station, 0).HasValue && cook.Arbitrator.GetCurrent(cook.Resident.Id) == null
            && Committed(cook, cooking) == 1,
            "SEIT-ACT-03-P1 SEIT-AC-04 cook-catalog-completes-once");
        Check(EventIs(cook, cooking, "ACT-FOOD-01", "hunger=45;health=2;fun=0;cooking=10;groceries=-1;meals=2;money=0"),
            "SEIT-ACT-03-P1 cook-event-deltas");

        Fixture fast = NewFixture();
        ActionInstanceId fastAction = Begin(fast, "ACT-FOOD-04");
        Advance(fast, 894);
        Check(Needs(fast, 40.0f, 40.0f, 40.0f) && Committed(fast, fastAction) == 0,
            "SEIT-ACT-03-P1 fast-food-no-reward-before-15-minutes");
        Advance(fast, 6);
        Check(Needs(fast, 75.0f, 37.0f, 45.0f) && Resident(fast).Money == 32L
            && CookingPoints(fast) == 0 && fast.Food.GroceryUnits(fast.Resident.Home) == 4
            && fast.Food.PreparedMeals(fast.Resident.Home) == 0 && fast.Accounts.Ledger.Count == 1
            && fast.Accounts.Ledger[0].Action == fastAction && fast.Accounts.Ledger[0].Amount == 18L
            && fast.Accounts.Ledger[0].Source.Equals(AccountRef.Personal(fast.Resident.Id))
            && Committed(fast, fastAction) == 1 && !fast.Executor.GetHold(fast.Station, 0).HasValue
            && fast.Arbitrator.GetCurrent(fast.Resident.Id) == null,
            "SEIT-ACT-03-P1 SEIT-AC-05 fast-food-catalog-and-accounts");
        Check(EventIs(fast, fastAction, "ACT-FOOD-04", "hunger=35;health=-3;fun=5;cooking=0;groceries=0;meals=0;money=-18"),
            "SEIT-ACT-03-P1 fast-food-event-deltas");
        foreach (Fixture f in new[] { cook, fast })
        {
            ActionInstanceId done = f == cook ? cooking : fastAction;
            string before = Snapshot(f);
            CommandResult replay = f.Food.Complete(f.Resident.Id, done);
            f.Food.Tick(Exec);
            CommandResult replayAgain = f.Food.Complete(f.Resident.Id, done);
            Check(!replay.Accepted && !replayAgain.Accepted && Snapshot(f) == before,
                "SEIT-IF-EXEC-N4 duplicate-completion-no-effect-" + (f == cook ? "cook" : "fast-food"));
        }

        Fixture empty = NewFixture(0);
        RefusedUnchanged(empty, Command(empty, "ACT-FOOD-01", (long)empty.Station), RejectReason.ObjectUnavailable,
            "SEIT-ACT-03-N2 no-grocery-refused-with-reason");
        Fixture poor = NewFixture(4, 0, 10L);
        RefusedUnchanged(poor, Command(poor, "ACT-FOOD-04", (long)poor.Station), RejectReason.InsufficientFunds,
            "SEIT-ACT-03-N2 insufficient-funds-no-effect");
        Fixture child = NewFixture(4, 0, 10L, LifeStage.Child);
        RefusedUnchanged(child, Command(child, "ACT-FOOD-04", (long)child.Station), RejectReason.StageIneligible,
            "SEIT-ACT-03-N2 child-without-teen-payer-no-effect");
        RefusedUnchanged(child, Command(child, "ACT-FOOD-01", (long)child.Station), RejectReason.StageIneligible,
            "SEIT-ACT-03-N2 child-cannot-cook");

        Fixture reserved = NewFixture(1);
        ActionInstanceId owner = Begin(reserved, "ACT-FOOD-01");
        ResidentRecord first = reserved.Resident;
        reserved.Resident = reserved.World.AllocateResident(Core, "Second cook", LifeStage.Adult,
            first.Household, first.Home);
        ObjectId otherStove = reserved.World.AllocateObject(Core);
        reserved.Executor.RegisterObject(otherStove, new[] { new ApproachPose(0.0f, 0.0f, 0.0f) });
        reserved.Executor.SetResidentPosition(reserved.Resident.Id, new ApproachPose(0.0f, 0.0f, 0.0f));
        CommandResult competing = reserved.Food.Submit(Command(reserved, "ACT-FOOD-01", (long)otherStove));
        // A reserved unit cannot validate another cook even at a different stove.
        Check(!competing.Accepted && competing.Reason == RejectReason.ObjectUnavailable
            && reserved.Food.GroceryUnits(first.Home) == 1 && reserved.Food.PreparedMeals(first.Home) == 0
            && reserved.Executor.GetExecution(owner).Stage == ExecutionStage.Perform
            && !reserved.Executor.GetHold(otherStove, 0).HasValue,
            "SEIT-ACT-03-N1 SEIT-ACT-03-N2 reserved-grocery-unavailable-to-second-cook");

        Fixture interrupted = NewFixture(1);
        ActionInstanceId cancelled = Begin(interrupted, "ACT-FOOD-01");
        Advance(interrupted, 1200);
        Check(interrupted.Food.Submit(Command(interrupted, "ACT-HOME-01", null)).Accepted,
            "SEIT-ACT-03-N1 immediate-interruption-accepted");
        interrupted.Food.Tick(Exec);
        string cancelledState = Snapshot(interrupted);
        CommandResult stale = interrupted.Food.Complete(interrupted.Resident.Id, cancelled);
        Check(!stale.Accepted && Snapshot(interrupted) == cancelledState
            && Needs(interrupted, 40.0f, 40.0f, 40.0f) && CookingPoints(interrupted) == 0
            && interrupted.Food.GroceryUnits(interrupted.Resident.Home) == 1
            && interrupted.Food.PreparedMeals(interrupted.Resident.Home) == 0
            && interrupted.Executor.GetExecution(cancelled).Outcome == TerminalOutcome.Interrupted
            && !interrupted.Executor.GetHold(interrupted.Station, 0).HasValue && Committed(interrupted, cancelled) == 0,
            "SEIT-ACT-03-N1 SEIT-IF-EXEC-N4 cancel-releases-no-stale-reward");
        ActionInstanceId retry = Begin(interrupted, "ACT-FOOD-01");
        Advance(interrupted, 2700);
        Check(retry != cancelled && interrupted.Food.GroceryUnits(interrupted.Resident.Home) == 0
            && interrupted.Food.PreparedMeals(interrupted.Resident.Home) == 2 && CookingPoints(interrupted) == 10,
            "SEIT-ACT-03-N1 cancelled-unit-can-be-reserved-again");

        Fixture early = NewFixture();
        ActionInstanceId running = Begin(early, "ACT-FOOD-01");
        string earlyState = Snapshot(early);
        CommandResult premature = early.Food.Complete(early.Resident.Id, running);
        CommandResult mismatch = early.Food.Complete(early.Resident.Id, (ActionInstanceId)99999L);
        Check(!premature.Accepted && !mismatch.Accepted && Snapshot(early) == earlyState,
            "SEIT-IF-EXEC-N4 early-and-mismatched-callbacks-no-effect");

        // Related catalog paths use the same stock and Accounts, not test doubles.
        Fixture eat = NewFixture(0, 1, 50L, LifeStage.Child);
        ActionInstanceId eating = Begin(eat, "ACT-FOOD-02");
        Advance(eat, 1200);
        Check(Needs(eat, 80.0f, 40.0f, 40.0f) && eat.Food.PreparedMeals(eat.Resident.Home) == 0
            && Resident(eat).Money == 50L && Committed(eat, eating) == 1,
            "SEIT-AC-04 prepared-meal-catalog-local");
        foreach (long funds in new[] { 100L, 60L })
        {
            Fixture groceries = NewFixture(0, 0, funds);
            Begin(groceries, "ACT-FOOD-03");
            Advance(groceries, 900);
            int units = funds == 100L ? 4 : 2;
            Check(groceries.Food.GroceryUnits(groceries.Resident.Home) == units
                && Resident(groceries).Money == funds - 25L * (long)units
                && groceries.Accounts.Ledger.Count == 1 && groceries.Accounts.Ledger[0].Amount == 25L * (long)units
                && Needs(groceries, 40.0f, 40.0f, 40.0f),
                "SEIT-AC-05 groceries-whole-affordable-units-" + funds);
        }
        Check(NoStaticMutable(typeof(Food)), "SEIT-ACT-03-P1 no-static-mutable-state");
        Fixture repeated = NewFixture();
        ActionInstanceId repeatedAction = Begin(repeated, "ACT-FOOD-01");
        Advance(repeated, 2700);
        // Fresh instance after prior unrelated runs: fixed seed 42, no random draws.
        Check(repeatedAction == cooking && Snapshot(repeated) == Snapshot(cook)
            && repeated.World.RngState == cook.World.RngState,
            "SEIT-ACT-03-P1 repeated-seed-42-determinism");
        failures += M1Regressions.Run("FOOD");
        Console.WriteLine(failures == 0 ? "FOOD ALL PASS" : "FOOD FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
