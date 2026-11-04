/* STORM@1 red contract: NEW SevereStorm belongs in Simulation/SevereStorm.cs.
 * Public surface in Blockparty.Simulation:
 *   SevereStorm(WorldState, ActivityExecutor, Home, Club)
 *   RegisterObject(StateWriter, ObjectId, bool outdoor, bool electric,
 *                  bool common, HomeId? roofHome)
 *   SetOutdoors(StateWriter, ResidentId, bool outdoors, ObjectId? shelter)
 *   Tick(StateWriter)
 *   long? ScheduledStartGameSeconds; EventId? ActiveEvent;
 *   long CooldownUntilGameSeconds;
 *   string DamageState(ObjectId); bool IsElectricEnabled(ObjectId)
 * Scene classifications and resident/shelter assignments initialize real
 * authored-world inputs; no substitute scheduler, executor or codec lives here.
 * Durable storm state is owned by WorldState and restored through SaveV1's
 * public SAVE-V2 API; reconstructing the service must retain that state.
 * DamageState is empty when undamaged, Damaged for common objects and
 * Damaged(Leak) for roofs. HOME/CLUB repairs must update this same damage.
 * Warning events name Severe Storm and the scheduled game-second start in
 * Detail (invariant decimal); event-kind spelling is deliberately unspecified.
 *
 * Oracles: BP-LIFE-05, BP-SEC-05 Severe Storm candidate state contract / TR-02,
 * SEIT-LIFE-05-P1/N1/N2 and SEIT-AC-19 (local simulation slices only).
 * These proposed numeric candidates are unmeasured, not balance validation.
 * New module required by the package selector; no existing suite covers storms.
 * Resource group isolated-csharp; no editor/display/service/shared resources.
 * Public-save tests own a unique temporary directory and always remove it.
 * negative-fixture changes the below-population fixture from 15 to 16: the
 * unchanged no-schedule assertion must FAIL, detecting a bypassed gate oracle.
 * Packaged process restart, UI notifications and acceptance remain at their gates.
 */
using System;
using System.Globalization;
using System.IO;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class StormChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const long Day = 86400L;
    private const long Hour = 3600L;
    private static int failures;

    private sealed class Scene : ISceneContent
    {
        public ObjectId[] Objects;
        public ObjectId Shelter;
        public void RegisterObjects(ActivityExecutor executor)
        {
            foreach (ObjectId id in Objects)
                executor.RegisterObject(id, new[] { new ApproachPose(id == Shelter ? 12f : 0f, 0f, 0f) });
        }
    }

    private sealed class Fixture
    {
        public GameStack Stack;
        public SevereStorm Storm;
        public Scene Scene;
        public ResidentId Outside, Inside;
        public HomeId OccupiedHome;
        public ObjectId OldDamage, Common1, Common2, SpareCommon;
        public ObjectId VacantRoof, Roof, SecondRoof, Outdoor, Electric, Indoor, Shelter;
        public WorldState World { get { return Stack.World; } }
    }

    private static Fixture NewFixture(int population = 16)
    {
        Fixture f = new Fixture();
        WorldState world = WorldState.CreateSeeded(7L, 42L, 0, null);
        HomeRecord home = world.AllocateHome(Core, population);
        HouseholdRecord household = world.AllocateHousehold(Core, "Storm household", home.Id);
        for (int i = 0; i < population; i++)
        {
            ResidentRecord resident = world.AllocateResident(Core, "Storm R" + i,
                LifeStage.Adult, household.Id, home.Id);
            world.SetNeed(Core, resident.Id, Need.Health, 80f);
            world.SetMoney(Core, resident.Id, 100L);
            if (i == 0) f.Outside = resident.Id;
            if (i == 1) f.Inside = resident.Id;
        }
        f.OccupiedHome = home.Id;
        Clock clock = new Clock(world);
        CommandArbitrator commands = new CommandArbitrator(world, clock);
        ActivityExecutor executor = new ActivityExecutor(world, commands, clock);
        Accounts accounts = new Accounts(world);
        Blockparty.Activities.Food food = new Blockparty.Activities.Food(world, commands, executor, accounts);
        Blockparty.Activities.Home management = new Blockparty.Activities.Home(world, commands, executor, accounts);
        f.Stack = new GameStack(world, clock, commands, executor, new AppearanceStore(), accounts, food, management);
        f.Storm = new SevereStorm(world, executor, management, f.Stack.Club);
        // Avoid ambiguous numeric resident/object targets, as the HOME suite does.
        ObjectId id;
        do { id = world.AllocateObject(Core); } while ((long)id <= population);
        f.OldDamage = id;
        f.Common1 = world.AllocateObject(Core);
        f.Common2 = world.AllocateObject(Core);
        f.SpareCommon = world.AllocateObject(Core);
        f.VacantRoof = world.AllocateObject(Core);
        f.Roof = world.AllocateObject(Core);
        f.SecondRoof = world.AllocateObject(Core);
        f.Outdoor = world.AllocateObject(Core);
        f.Electric = world.AllocateObject(Core);
        f.Indoor = world.AllocateObject(Core);
        f.Shelter = world.AllocateObject(Core);
        f.Scene = new Scene { Objects = new[] { f.OldDamage, f.Common1, f.Common2, f.SpareCommon,
            f.VacantRoof, f.Roof, f.SecondRoof, f.Outdoor, f.Electric, f.Indoor, f.Shelter }, Shelter = f.Shelter };
        f.Scene.RegisterObjects(executor);
        // Register in reverse id order: the contract selects ids, not insertion order.
        foreach (ObjectId common in new[] { f.SpareCommon, f.Common2, f.Common1, f.OldDamage })
            f.Storm.RegisterObject(Core, common, true, false, true, null);
        HomeRecord vacant = world.AllocateHome(Core, 2);
        f.Storm.RegisterObject(Core, f.VacantRoof, false, false, false, vacant.Id);
        HomeRecord other = world.AllocateHome(Core, 2);
        HouseholdRecord otherHousehold = world.AllocateHousehold(Core, "Second occupied", other.Id);
        // Move one existing resident without changing the gate's population.
        world.SetHousehold(Core, f.Inside, otherHousehold.Id, other.Id);
        f.Storm.RegisterObject(Core, f.SecondRoof, false, false, false, other.Id);
        f.Storm.RegisterObject(Core, f.Roof, false, false, false, home.Id);
        f.Storm.RegisterObject(Core, f.Outdoor, true, false, false, null);
        f.Storm.RegisterObject(Core, f.Electric, false, true, false, null);
        f.Storm.RegisterObject(Core, f.Indoor, false, false, false, null);
        f.Storm.RegisterObject(Core, f.Shelter, false, false, false, null);
        management.SetDamaged(Core, f.OldDamage, true);
        f.Stack.Club.RegisterHelpTarget(Core, f.OldDamage, household.Id, "Damaged");
        f.Stack.Club.RegisterHelpTarget(Core, f.Common1, household.Id, "Ready");
        f.Stack.Club.RegisterHelpTarget(Core, f.Common2, household.Id, "Ready");
        f.Stack.Club.RegisterHelpTarget(Core, f.SpareCommon, household.Id, "Ready");
        f.Storm.SetOutdoors(Core, f.Outside, true, f.Shelter);
        return f;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static void At(Fixture f, long seconds)
    {
        f.World.SetGameSeconds(Core, seconds);
        f.Storm.Tick(Core);
    }

    private static ResidentRecord Resident(Fixture f, ResidentId id)
    {
        ResidentRecord resident;
        if (!f.World.TryGetResident(id, out resident)) throw new Exception("Fixture resident missing");
        return resident;
    }

    private static int Warnings(Fixture f, long scheduled)
    {
        int count = 0;
        foreach (WorldEvent entry in f.World.Events)
            if (entry.Detail.Contains("Severe Storm")
                && entry.Detail.Contains(scheduled.ToString(CultureInfo.InvariantCulture))) count++;
        return count;
    }

    private static void Gates(bool negative)
    {
        Fixture low = NewFixture(negative ? 16 : 15);
        for (int day = 40; day <= 70; day++) At(low, day * Day);
        Check(!low.Storm.ScheduledStartGameSeconds.HasValue && !low.Storm.ActiveEvent.HasValue
            && !low.Stack.Home.IsDamaged(low.Common1) && Resident(low, low.Outside).GetNeed(Need.Health) == 80f,
            "SEIT-LIFE-05-N1-population15-through-day70-no-schedule-or-effects");
        if (!negative)
        {
            ResidentRecord r = Resident(low, low.Outside);
            low.World.AllocateResident(Core, "Sixteenth", LifeStage.Adult, r.Household, r.Home);
            At(low, 70 * Day);
            Check(low.Storm.ScheduledStartGameSeconds == 71 * Day,
                "SEIT-AC-19-crossing-population16-schedules-one-day-warning");
        }
        Fixture young = NewFixture();
        At(young, 25 * Day);
        At(young, 30 * Day - 1L);
        Check(!young.Storm.ScheduledStartGameSeconds.HasValue && !young.Storm.ActiveEvent.HasValue,
            "SEIT-LIFE-05-N1-age-below30-no-schedule");
        At(young, 30 * Day);
        Check(young.Storm.ScheduledStartGameSeconds == 31 * Day && Warnings(young, 31 * Day) == 1,
            "SEIT-LIFE-05-P1-exact16-age30-warning-names-start");
        At(young, 30 * Day);
        At(young, 31 * Day - 1L);
        Check(young.Storm.ScheduledStartGameSeconds == 31 * Day && !young.Storm.ActiveEvent.HasValue
            && Warnings(young, 31 * Day) == 1 && young.Storm.IsElectricEnabled(young.Electric)
            && !young.Stack.Home.IsDamaged(young.Common1),
            "SEIT-LIFE-05-P1-warning-once-no-early-storm-damage-or-outage");
    }

    private static Fixture Active()
    {
        Fixture f = NewFixture();
        At(f, 30 * Day);
        At(f, 31 * Day);
        return f;
    }

    private static ExecutionRecord Dispatch(Fixture f, ResidentId resident, string code, ObjectId target)
    {
        return f.Stack.Executor.Dispatch(resident, new ActionEntry(f.World.AllocateCommand(Core),
            f.World.AllocateAction(Core), CommandKind.DirectActivity, new ActivityId(code),
            (long)target, ActionOwner.Player, CommandSource.PlayerHuman));
    }

    private static void Consequences()
    {
        Fixture f = Active();
        Check(f.Storm.ActiveEvent.HasValue && !f.Storm.IsElectricEnabled(f.Electric),
            "SEIT-LIFE-05-P1-start-and-electric-outage");
        Check(f.Stack.Home.IsDamaged(f.OldDamage) && f.Stack.Home.IsDamaged(f.Common1)
            && f.Stack.Home.IsDamaged(f.Common2) && !f.Stack.Home.IsDamaged(f.SpareCommon)
            && f.Stack.Home.IsDamaged(f.Roof) && !f.Stack.Home.IsDamaged(f.VacantRoof)
            && !f.Stack.Home.IsDamaged(f.SecondRoof) && f.Storm.DamageState(f.Roof) == "Damaged(Leak)",
            "SEIT-LIFE-05-P1-lowest-two-undamaged-common-and-lowest-occupied-roof-leak");
        Check(Resident(f, f.Outside).GetNeed(Need.Health) == 60f && Resident(f, f.Outside).Injured
            && Resident(f, f.Inside).GetNeed(Need.Health) == 80f && !Resident(f, f.Inside).Injured,
            "SEIT-LIFE-05-P1-only-outdoor-exposure-health-minus20-injured");
        ApproachPose before = f.Stack.Executor.GetResidentPosition(f.Outside);
        Check(before.X == 0f && before.Y == 0f && before.Z == 0f,
            "SEIT-AC-19-storm-start-does-not-teleport-to-shelter");
        ExecutionRecord outdoor = Dispatch(f, f.Inside, "ACT-SPORT-01", f.Outdoor);
        Check(outdoor.IsTerminal && outdoor.Outcome == TerminalOutcome.Failed
            && outdoor.BlockedReason == RejectReason.UnsafeWeather
            && !f.Stack.Executor.GetHold(f.Outdoor, 0).HasValue,
            "SEIT-LIFE-05-P1-outdoor-executor-fails-UnsafeWeather-releases-slot");
        ExecutionRecord electric = Dispatch(f, f.Inside, "ACT-EDU-02", f.Electric);
        Check(electric.IsTerminal && electric.Outcome == TerminalOutcome.Failed
            && (electric.BlockedReason == RejectReason.ObjectUnavailable
                || electric.BlockedReason == RejectReason.UnsafeWeather),
            "SEIT-LIFE-05-P1-electric-use-refused-by-executor");
        ExecutionRecord indoor = Dispatch(f, f.Inside, "ACT-EDU-02", f.Indoor);
        Check(!indoor.IsTerminal, "SEIT-LIFE-05-P1-indoor-nonelectric-remains-available");
        At(f, 31 * Day + 6 * Hour - 1L);
        Check(f.Storm.ActiveEvent.HasValue && !f.Storm.IsElectricEnabled(f.Electric)
            && Resident(f, f.Outside).GetNeed(Need.Health) == 60f,
            "SEIT-LIFE-05-P1-active-for-full-six-hours-exposure-once");
        At(f, 31 * Day + 6 * Hour);
        Check(!f.Storm.ActiveEvent.HasValue && f.Storm.IsElectricEnabled(f.Electric)
            && f.Stack.Home.IsDamaged(f.Common1) && f.Stack.Home.IsDamaged(f.Roof)
            && f.Storm.CooldownUntilGameSeconds == 51 * Day + 6 * Hour,
            "SEIT-LIFE-05-P1-end-restores-electric-not-damage-cooldown20-days-from-end");
        Check(!Dispatch(f, f.Inside, "ACT-EDU-02", f.Electric).IsTerminal,
            "SEIT-LIFE-05-P1-electric-executor-usable-after-storm");
        At(f, 51 * Day + 6 * Hour - 1L);
        Check(!f.Storm.ScheduledStartGameSeconds.HasValue && !f.Storm.ActiveEvent.HasValue,
            "SEIT-LIFE-05-P1-no-warning-or-storm-during-cooldown");
        At(f, 51 * Day + 6 * Hour);
        Check(f.Storm.ScheduledStartGameSeconds == 52 * Day + 6 * Hour && !f.Storm.ActiveEvent.HasValue,
            "SEIT-LIFE-05-P1-cooldown-boundary-schedules-new-full-warning");
    }

    private static void InterruptionAndShelter()
    {
        Fixture f = NewFixture();
        At(f, 30 * Day);
        At(f, 31 * Day - 120L);
        ExecutionRecord outdoor = Dispatch(f, f.Inside, "ACT-SPORT-01", f.Outdoor);
        f.Stack.Executor.Tick(Exec);
        f.Stack.Executor.Tick(Exec);
        Check(!outdoor.IsTerminal && outdoor.Stage == ExecutionStage.Perform,
            "SEIT-LIFE-05-P1-outdoor-action-running-before-storm");
        At(f, 31 * Day);
        f.Stack.Executor.Tick(Exec);
        Check(outdoor.IsTerminal && outdoor.Outcome != TerminalOutcome.Completed
            && outdoor.BlockedReason == RejectReason.UnsafeWeather
            && !f.Stack.Executor.GetHold(f.Outdoor, 0).HasValue,
            "SEIT-LIFE-05-P1-storm-interrupts-running-outdoor-action-without-completion");
        f.Stack.Executor.Tick(Exec);
        ApproachPose start = f.Stack.Executor.GetResidentPosition(f.Outside);
        Check(start.X == 0f, "SEIT-AC-19-shelter-routing-no-instant-move");
        f.Stack.Clock.AdvanceRealSeconds(Core, 0.1);
        f.Storm.Tick(Core);
        f.Stack.Executor.Tick(Exec);
        ApproachPose moving = f.Stack.Executor.GetResidentPosition(f.Outside);
        Check(moving.X > 0f && moving.X < 12f,
            "SEIT-AC-19-shelter-routing-advances-through-intermediate-position");
        for (int tick = 0; tick < 130; tick++)
        {
            f.Stack.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Storm.Tick(Core);
            f.Stack.Executor.Tick(Exec);
        }
        Check(Math.Abs(f.Stack.Executor.GetResidentPosition(f.Outside).X - 12f) <= 0.05f
            && Resident(f, f.Outside).GetNeed(Need.Health) == 60f,
            "SEIT-AC-19-reachable-shelter-arrival-without-repeat-exposure");

        Fixture blocked = NewFixture();
        blocked.Stack.Executor.SetReachable(blocked.Shelter, false);
        At(blocked, 30 * Day);
        At(blocked, 31 * Day);
        blocked.Stack.Executor.Tick(Exec);
        ResidentStatus status = blocked.Stack.Executor.GetResidentStatus(blocked.Outside);
        Check(blocked.Stack.Executor.GetResidentPosition(blocked.Outside).X == 0f
            && (status.IdleReason.Contains("NoPath") || (status.Execution != null
                && status.Execution.BlockedReason == RejectReason.NoPath)),
            "SEIT-AC-19-unreachable-shelter-visible-NoPath-no-teleport");
    }

    private static void Repair(Fixture f, ObjectId target)
    {
        ResidentRecord resident = Resident(f, f.Inside);
        CommandEnvelope command = new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
            f.World.GameSeconds, CommandSource.PlayerHuman, resident.ResidentSequence + 1u,
            CommandKind.DirectActivity, resident.Id, new ActivityId("ACT-HOME-02"), (long)target,
            new CommandParameters.Direct(QueueMode.Immediate));
        if (!f.Stack.Home.Submit(command).Accepted) throw new Exception("Catalog repair refused");
        f.Stack.Home.Tick(Exec);
        f.Stack.Home.Tick(Exec);
        for (int tick = 0; tick < 600; tick++)
        {
            f.Stack.Clock.AdvanceRealSeconds(Core, 0.1);
            f.Stack.Home.Tick(Exec);
            f.Storm.Tick(Core);
        }
    }

    private static void Recovery()
    {
        Fixture f = Active();
        At(f, 31 * Day + 6 * Hour);
        long money = Resident(f, f.Inside).Money;
        Check(f.Stack.Club.CommunityHelp(Core, f.Inside, f.Common1, new[] { f.Inside }).Applied,
            "SEIT-LIFE-05-P1-CommunityHelp-accepted");
        f.Storm.Tick(Core);
        Check(!f.Stack.Home.IsDamaged(f.Common1) && f.Storm.DamageState(f.Common1) == string.Empty
            && f.Stack.Club.HelpTargetState(f.Common1) == "Ready" && Resident(f, f.Inside).Money == money,
            "SEIT-LIFE-05-P1-CommunityHelp-clears-common-damage-without-charge");
        Repair(f, f.Common2);
        Repair(f, f.Roof);
        long total = 0L;
        foreach (AccountTransaction row in f.Stack.Accounts.Ledger) total += row.Amount;
        Check(!f.Stack.Home.IsDamaged(f.Common2) && !f.Stack.Home.IsDamaged(f.Roof)
            && f.Storm.DamageState(f.Common2) == string.Empty && f.Storm.DamageState(f.Roof) == string.Empty
            && Resident(f, f.Inside).Money == money - 30L && total == 30L
            && f.Stack.Accounts.Ledger.Count == 2 && f.Stack.Home.IsDamaged(f.OldDamage),
            "SEIT-LIFE-05-P1-RepairObject-common-and-roof-clear-total-parts30-no-structural-editor");
    }

    private static void RoundTrip(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(f.Stack, directory);
        if (!saved.Ok) throw new Exception("Public save failed: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, f.Scene);
        if (!loaded.Ok) throw new Exception("Public load failed: " + loaded.Notice);
        LoadedGame game = loaded.Game;
        f.Stack = new GameStack(game.World, game.Clock, game.Arbitrator, game.Executor,
            game.Appearances, game.Accounts, game.Food, game.Home);
        // GameStack constructs a Club; carry forward the public codec-restored state.
        f.Stack.Club.Import(Core, game.Club.Export());
        f.Storm = new SevereStorm(game.World, game.Executor, game.Home, f.Stack.Club);
    }

    private static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-storm-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture f = NewFixture();
            At(f, 30 * Day);
            RoundTrip(f, root);
            At(f, 31 * Day - 1L);
            Check(f.Storm.ScheduledStartGameSeconds == 31 * Day && !f.Storm.ActiveEvent.HasValue
                && Warnings(f, 31 * Day) == 1, "SEIT-LIFE-05-N2-saved-warning-retains-start-no-duplicate");
            At(f, 31 * Day);
            EventId? storm = f.Storm.ActiveEvent;
            for (int reload = 0; reload < 2; reload++)
            {
                RoundTrip(f, root);
                f.Storm.Tick(Core);
                Check(storm.HasValue && f.Storm.ActiveEvent == storm
                    && Resident(f, f.Outside).GetNeed(Need.Health) == 60f && Resident(f, f.Outside).Injured
                    && f.Stack.Home.IsDamaged(f.Common1) && f.Storm.DamageState(f.Roof) == "Damaged(Leak)"
                    && !f.Storm.IsElectricEnabled(f.Electric),
                    "SEIT-LIFE-05-N2-reload" + reload + "-same-event-exposure-once-damage-outage-retained");
            }
            At(f, 31 * Day + 6 * Hour);
            RoundTrip(f, root);
            At(f, 51 * Day + 6 * Hour - 1L);
            Check(!f.Storm.ActiveEvent.HasValue && !f.Storm.ScheduledStartGameSeconds.HasValue
                && f.Storm.CooldownUntilGameSeconds == 51 * Day + 6 * Hour
                && f.Stack.Home.IsDamaged(f.Roof) && f.Storm.IsElectricEnabled(f.Electric),
                "SEIT-LIFE-05-N2-reloaded-cooldown-and-unrepaired-damage-retained");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void EpidemicsDisabled()
    {
        Fixture f = NewFixture();
        for (int day = 30; day <= 230; day++) At(f, day * Day);
        bool epidemic = false;
        foreach (WorldEvent entry in f.World.Events)
            epidemic |= (entry.Kind + " " + entry.Detail).IndexOf("epidemic", StringComparison.OrdinalIgnoreCase) >= 0;
        Check(!epidemic, "SEIT-LIFE-05-N1-200-eligible-days-no-epidemic-event");
    }

    public static int Main(string[] args)
    {
        failures = 0;
        bool negative = args.Length > 0 && args[0] == "negative-fixture";
        Run("Gates", () => Gates(negative));
        Run("Consequences", Consequences);
        Run("InterruptionAndShelter", InterruptionAndShelter);
        Run("Recovery", Recovery);
        Run("Persistence", Persistence);
        Run("EpidemicsDisabled", EpidemicsDisabled);
        if (failures == 0) Console.WriteLine("STORM ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
