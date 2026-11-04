/*
BIRTH@1 TDD red contract (FamilyCreation.cs, Blockparty.Simulation):
  GameStack.Family and LoadedGame.Family: live FamilyCreation bound to the stack.
  GameStack overload appends FamilyCreation to the existing ten service arguments
    when rebundling a LoadedGame, preserving the SAME restored service for save.
  SetReproductiveCapabilities(StateWriter, ResidentId, bool canCarry, bool canImpregnate)
  SetPartner(StateWriter, ResidentId initiator, ResidentId partner, bool consent)
  RegisterBed(StateWriter, ObjectId, HomeId) // existing two-slot scene Bed
  Submit(CommandEnvelope): validates family inputs, uses the shared arbitrator
  Tick(StateWriter): drives the real executor and processes conception/delivery
  TryGetPregnancy(ResidentId carrier, out PregnancyRecord)
  GetBirth(ResidentId child): null absent; otherwise Child, Carrier, OtherParent,
    ConceptionEvent, BirthEvent, WorldSeed, BaseBodySeed, BaseBody,
    CarrierAppearance, OtherParentAppearance, ChildAppearance (AppearanceRecord).
The birth row is the inspectable, persisted inheritance/once-only receipt, not an
alternate resident-creation path. BaseBodySeed records the seed of the single
WorldRng.NextInt(0, 2) body draw; deriving that seed from world seed/newborn identity
is product-owned. Conception uses a separate WorldRng(world.Seed) stream: seed
1234's first draw is 0.3011453000254337 (< candidate 0.8). No forced success,
replacement RNG, executor, housing, appearance store or save codec is supplied.

Oracles: plan BIRTH + m3/test-engineer BIRTH suite_checks; SDoc BP-CHR-04,
BP-LIFE-01, BP-SEC-05; owner Q2 2026-10-28. Existing WorldRng and appearance
catalog supply deterministic draws and catalog steps. Candidate tuning remains
unmeasured. This new module is the package runner entry; no existing suite drives
TryForBaby through pregnancy/delivery. All input residents and Beds are real
allocated world records. The scene adapter restores only authored Bed geometry.

Local case mapping (not whole SEIT-case acceptance):
  ordinary-conception-delivery: SEIT-LIFE-01-P1, SEIT-IF-CMD-P3,
    SEIT-IF-EXEC-P3/P4, SEIT-IF-LIFE-P2, SEIT-AC-07.
  inheritance-and-seeded-body: SEIT-CHR-04-P1/P2, SEIT-CHR-04-N1/N2;
    owner Q2 independence, one recorded draw, persisted result.
  pregnancy-and-delivery-roundtrip: SEIT-LIFE-01-N2, SEIT-IF-SAVE-P2/N4,
    SEIT-CHR-04-N3, SEIT-IF-TIME-P1/P2/N5.
  negative-fixture: inject a second real Newborn after post-birth reload;
    the SAME no-second-delivery assertion must print FAIL and return nonzero.
Rendered ordinary controls, packaged restart, aging/care activity execution and
other bound SEIT subclaims remain at their system/acceptance gates. No such PASS
is claimed here. Resource group isolated-csharp: no editor/display/ports/network;
save directories are unique per case and deleted in finally.
*/
using System;
using System.IO;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class BirthChecks
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
        public GameStack Game;
        public FamilyCreation Family;
        public ResidentId Carrier, Other;
        public HomeId Home;
        public HouseholdId Household;
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

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + ": " + e.GetType().Name + " " + e.Message); }
    }

    private static ResidentRecord Person(Fixture f, ResidentId id)
    {
        ResidentRecord r;
        Require(f.Game.World.TryGetResident(id, out r), "resident exists");
        return r;
    }

    private static AppearanceRecord Appearance(Fixture f, ResidentId id)
    {
        AppearanceRecord a;
        Require(f.Game.Appearances.TryGet(id, out a), "appearance exists");
        return a;
    }

    private static Fixture NewFixture(long seed = 1234L, BaseBody parentBody = BaseBody.Male,
        bool reverseCarrier = false)
    {
        var world = WorldState.CreateSeeded(701L, seed, 2, new[] { "Alex", "Bo" });
        var people = world.ResidentsSnapshot();
        var appearances = new AppearanceStore();
        for (int i = 0; i < people.Length; i++)
        {
            world.SetStage(Core, people[i].Id, LifeStage.Adult, 0f);
            appearances.Register(people[i].Id, new WorldRng(seed + 7919L * (i + 1L)));
            appearances.SetBaseBody(Core, people[i].Id, parentBody);
            foreach (AppearanceDimension d in new[] { AppearanceDimension.SkinTone,
                AppearanceDimension.EyeColor, AppearanceDimension.HairColor,
                AppearanceDimension.Face, AppearanceDimension.HairType, AppearanceDimension.Accessory })
                appearances.SetPreset(Core, people[i].Id, d,
                    i == 0 ? 0 : ResidentFactory.OptionCount(d) - 1);
        }
        var clock = new Clock(world);
        clock.SetPreset(AgingPreset.Off);
        var commands = new CommandArbitrator(world, clock);
        var executor = new ActivityExecutor(world, commands, clock);
        var accounts = new Accounts(world);
        var food = new Food(world, commands, executor, accounts);
        var expenses = new Expenses(world, accounts);
        var ownership = new Ownership(world, accounts);
        var housing = new Housing(world, accounts, ownership, executor, appearances);
        var game = new GameStack(world, clock, commands, executor, appearances,
            accounts, food, expenses, ownership, housing);
        var f = new Fixture { Game = game, Family = game.Family,
            Carrier = people[reverseCarrier ? 1 : 0].Id, Other = people[reverseCarrier ? 0 : 1].Id,
            Home = people[0].Home, Household = people[0].Household };
        Require(f.Family != null, "BIRTH live family service");
        world.SetBond(Core, f.Carrier, f.Other, 50f, 50f);
        world.SetBond(Core, f.Other, f.Carrier, 50f, 50f);
        housing.RegisterHome(Core, f.Home, 0L);
        housing.SetCaregiver(Core, f.Carrier, true);
        housing.SetCaregiver(Core, f.Other, true);
        f.Family.SetReproductiveCapabilities(Core, f.Carrier, true, false);
        f.Family.SetReproductiveCapabilities(Core, f.Other, false, true);
        f.Family.SetPartner(Core, f.Carrier, f.Other, true);
        ObjectId bed;
        do { bed = world.AllocateObject(Core); } while ((long)bed <= (long)people[1].Id);
        f.Scene = new Scene { Bed = bed };
        f.Scene.RegisterObjects(executor);
        f.Family.RegisterBed(Core, bed, f.Home);
        executor.SetResidentPosition(f.Carrier, new ApproachPose(0f, 0f, 0f));
        executor.SetResidentPosition(f.Other, new ApproachPose(0f, 0f, 0f));
        return f;
    }

    private static void Tick(Fixture f, long gameSeconds)
    {
        Tick(f, gameSeconds, StateWriter.ActivityExecutor);
    }

    private static void Tick(Fixture f, long gameSeconds, StateWriter writer)
    {
        Require(gameSeconds >= 0 && gameSeconds % 6L == 0L, "whole fixed ticks");
        f.Game.Clock.AdvanceRealSeconds(Core, (double)gameSeconds / 60.0,
            () => f.Family.Tick(writer));
    }

    private static ExecutionRecord Start(Fixture f)
    {
        var world = f.Game.World;
        CommandResult result = f.Family.Submit(new CommandEnvelope(world.AllocateCommand(Core),
            world.Epoch, world.GameSeconds, CommandSource.PlayerHuman,
            Person(f, f.Carrier).ResidentSequence + 1u, CommandKind.DirectActivity,
            f.Carrier, new ActivityId("ACT-REL-06"), (long)f.Scene.Bed,
            new CommandParameters.Direct(QueueMode.Immediate)));
        Require(result.Accepted, "BIRTH ACT-REL-06 accepted through DirectActivity: " + result.Reason);
        Tick(f, 12L);
        ExecutionRecord action = f.Game.Executor.GetResidentStatus(f.Carrier).Execution;
        Require(action != null && action.Stage == ExecutionStage.Perform,
            "BIRTH real executor reached Perform");
        Check(action.Kind == CommandKind.DirectActivity && action.Activity.Code == "ACT-REL-06"
            && action.TargetId == (long)f.Scene.Bed && action.Holds.Length == 2,
            "SEIT-IF-CMD-P3-IF-EXEC-P3-P4-local-shared-bed-two-exclusive-holds");
        PregnancyRecord notYet;
        Check(!f.Family.TryGetPregnancy(f.Carrier, out notYet) && world.ResidentCount == 2,
            "SEIT-LIFE-01-P1-local-no-conception-before-completion");
        return action;
    }

    private static PregnancyRecord Conceive(Fixture f)
    {
        ExecutionRecord action = Start(f);
        long performAt = -1L;
        foreach (StageRecord stage in action.Trace)
            if (stage.Stage == ExecutionStage.Perform && !stage.Skipped) performAt = stage.GameSeconds;
        Require(performAt >= 0L, "real Perform timestamp");
        Tick(f, performAt + 1800L - f.Game.World.GameSeconds);
        PregnancyRecord pregnancy;
        Require(f.Family.TryGetPregnancy(f.Carrier, out pregnancy),
            "BIRTH seeded eligible completed TryForBaby starts pregnancy");
        Check(action.Outcome == TerminalOutcome.Completed && action.Holds.Length == 0
            && pregnancy.Carrier == f.Carrier && pregnancy.OtherParent == f.Other
            && (long)pregnancy.ConceptionEvent > 0L && pregnancy.CapacityHold == 1
            && pregnancy.DueGameTime == f.Game.World.GameSeconds + 3L * Day
            && f.Game.World.ResidentCount == 2,
            "SEIT-LIFE-01-P1-local-one-pregnancy-record-due-three-days-one-capacity-hold");
        int held = 0;
        foreach (var reservation in f.Game.Housing.Export().Reservations)
            if (reservation.Household == (long)f.Household) held += reservation.Count;
        Check(held == 1, "SEIT-LIFE-01-P1-local-housing-holds-exactly-one-newborn-place");
        return pregnancy;
    }

    private static ResidentId OnlyChild(Fixture f)
    {
        ResidentId child = default(ResidentId);
        int count = 0;
        foreach (ResidentRecord r in f.Game.World.ResidentsSnapshot())
            if (r.Id != f.Carrier && r.Id != f.Other) { child = r.Id; count++; }
        Require(count == 1, "BIRTH exactly one child, observed " + count);
        return child;
    }

    private static bool Parent(ResidentRecord child, ResidentId parent)
    {
        int count = 0;
        foreach (FamilyLink link in child.Family)
            if (link.Other == parent && link.Kind == "Parent") count++;
        return count == 1;
    }

    private static void Delivered(Fixture f, PregnancyRecord pregnancy)
    {
        ResidentId id = OnlyChild(f);
        ResidentRecord child = Person(f, id);
        PregnancyRecord remaining;
        bool cared = false;
        foreach (CareAssignment care in f.Game.World.CareAssignments)
            if (care.Dependent == id && care.Placement.Household == f.Household
                && (care.Guardian == f.Carrier || care.Guardian == f.Other)
                && Person(f, care.Guardian).IsAlive) cared = true;
        var birth = f.Family.GetBirth(id);
        Require(birth != null, "BIRTH inspectable birth/inheritance receipt");
        int events = 0;
        foreach (WorldEvent e in f.Game.World.Events)
            if (e.Event == birth.BirthEvent && e.GameSeconds == pregnancy.DueGameTime) events++;
        Check(child.Stage == LifeStage.Newborn && child.IsAlive && (long)id > Math.Max((long)f.Carrier, (long)f.Other)
            && Array.IndexOf(WorldState.DefaultNames, child.DisplayName) >= 0
            && child.Household == f.Household && child.Home == f.Home
            && Parent(child, f.Carrier) && Parent(child, f.Other) && cared
            && !f.Family.TryGetPregnancy(f.Carrier, out remaining)
            && birth.Child == id && birth.Carrier == f.Carrier && birth.OtherParent == f.Other
            && birth.ConceptionEvent == pregnancy.ConceptionEvent && events == 1,
            "SEIT-AC-07-LIFE-01-P1-IF-LIFE-P2-local-one-valid-cared-for-newborn-and-birth-event");
        var reservations = f.Game.Housing.Export().Reservations;
        bool released = true;
        foreach (var hold in reservations)
            if (hold.Household == (long)f.Household && hold.Count != 0) released = false;
        Check(released, "SEIT-LIFE-01-P1-local-delivery-releases-newborn-capacity");
    }

    private static bool Ordered(int value, int a, int b)
    {
        // Even endpoint sums give an exact catalog midpoint, with no tie policy guessed.
        int midpoint = (a + b) / 2;
        return value >= Math.Min(a, b) && value <= Math.Max(a, b)
            && (value == a || value == b || value == midpoint ||
                ((a + b) % 2 != 0 && value == midpoint + 1));
    }

    private static string Json(object value)
    {
        return JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { IncludeFields = true });
    }

    private static void Inherited(Fixture f, ResidentId child)
    {
        var a = Appearance(f, f.Carrier);
        var b = Appearance(f, f.Other);
        var c = Appearance(f, child);
        var birth = f.Family.GetBirth(child);
        Require(birth != null, "BIRTH recorded inheritance exists");
        Check(Ordered(c.SkinTone, a.SkinTone, b.SkinTone)
            && Ordered(c.EyeColor, a.EyeColor, b.EyeColor)
            && Ordered(c.HairColor, a.HairColor, b.HairColor)
            && (c.Face == a.Face || c.Face == b.Face)
            && (c.HairType == a.HairType || c.HairType == b.HairType)
            && (c.Accessory == a.Accessory || c.Accessory == b.Accessory)
            && c.HairStyle == ResidentFactory.ValidHairStyles(c.HairType)[0]
            && Json(birth.CarrierAppearance) == Json(a)
            && Json(birth.OtherParentAppearance) == Json(b)
            && Json(birth.ChildAppearance) == Json(c),
            "SEIT-CHR-04-P1-P2-N1-N2-local-inherited-catalog-values-and-recorded-parent-snapshots");
        Check(birth.WorldSeed == f.Game.World.Seed && birth.BaseBody == c.BaseBody
            && (int)c.BaseBody == new WorldRng(birth.BaseBodySeed).NextInt(0, 2),
            "BIRTH-Q2-SEIT-CHR-04-P1-local-body-equals-recorded-seeded-draw");
    }

    private static Fixture Restore(Fixture before, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(before.Game, directory);
        Require(saved.Ok, "BIRTH public SaveGame: " + saved.Detail);
        return Load(before, directory);
    }

    private static Fixture Load(Fixture before, string directory)
    {
        SaveLoadResult result = SaveV1.LoadGame(directory, before.Scene);
        Require(result.Ok && result.Game != null, "BIRTH public LoadGame: " + result.Notice);
        var g = result.Game;
        Require(g.Family != null && g.Housing != null, "BIRTH restored family and housing services");
        return new Fixture { Game = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor,
            g.Appearances, g.Accounts, g.Food, g.Expenses, g.Ownership, g.Housing, g.Family),
            Family = g.Family, Carrier = before.Carrier, Other = before.Other,
            Home = before.Home, Household = before.Household, Scene = before.Scene };
    }

    private static void Lifecycle()
    {
        Fixture f = NewFixture();
        PregnancyRecord pregnancy = Conceive(f);
        f.Game.Clock.Pause();
        Tick(f, Day);
        Check(f.Game.World.ResidentCount == 2 && f.Game.World.GameSeconds == pregnancy.DueGameTime - 3L * Day,
            "SEIT-IF-TIME-N5-local-pause-stops-gestation");
        f.Game.Clock.Resume();
        Tick(f, 3L * Day - 6L);
        Check(f.Game.World.ResidentCount == 2, "SEIT-LIFE-01-P1-local-no-early-delivery");
        Tick(f, 6L);
        Delivered(f, pregnancy);
        Inherited(f, OnlyChild(f));
    }

    private static void RoundTrip(bool negative)
    {
        Fixture f = NewFixture();
        PregnancyRecord pregnancy = Conceive(f);
        Tick(f, 3L * Day - 60L);
        string root = Path.Combine(Path.GetTempPath(), "blockparty-birth-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pregnantPath = Path.Combine(root, "pregnant");
            Fixture a = Restore(f, pregnantPath);
            PregnancyRecord loaded;
            Check(a.Family.TryGetPregnancy(a.Carrier, out loaded)
                && loaded.Carrier == pregnancy.Carrier && loaded.OtherParent == pregnancy.OtherParent
                && loaded.ConceptionEvent == pregnancy.ConceptionEvent
                && loaded.DueGameTime == pregnancy.DueGameTime && loaded.CapacityHold == pregnancy.CapacityHold
                && a.Game.World.Seed == f.Game.World.Seed,
                "SEIT-IF-SAVE-P2-LIFE-01-N2-local-pregnancy-fields-survive-public-codec");
            Fixture b = Load(f, pregnantPath);
            Tick(a, 60L); Tick(b, 60L);
            Delivered(a, pregnancy); Delivered(b, pregnancy);
            ResidentId child = OnlyChild(a);
            string inherited = Json(a.Family.GetBirth(child));
            string appearance = Json(Appearance(a, child));
            int eventsAtBirth = a.Game.World.EventCount;
            long nextEventAtBirth = a.Game.World.NextEventId;
            Check(child == OnlyChild(b) && inherited == Json(b.Family.GetBirth(child))
                && appearance == Json(Appearance(b, child)),
                "SEIT-CHR-04-N3-LIFE-01-N2-local-two-prebirth-loads-identical-child-draw-and-receipt");
            var post = Restore(a, Path.Combine(root, "born"));
            Tick(post, Day);
            if (negative)
                post.Game.World.AllocateResident(Core, "Duplicate birth", LifeStage.Newborn,
                    post.Household, post.Home);
            // Negative mode injects the exact forbidden result through the real allocator.
            Check(post.Game.World.ResidentCount == 3 && OnlyOneBirth(post, child)
                && post.Game.World.NextResidentId == a.Game.World.NextResidentId
                && post.Game.World.EventCount == eventsAtBirth
                && post.Game.World.NextEventId == nextEventAtBirth
                && Json(Appearance(post, child)) == appearance
                && Json(post.Family.GetBirth(child)) == inherited,
                "SEIT-IF-SAVE-N4-LIFE-01-N2-local-no-second-delivery-after-reload");
            var twice = Restore(post, Path.Combine(root, "born-again"));
            Tick(twice, Day);
            Check(twice.Game.World.ResidentCount == 3 && OnlyOneBirth(twice, child)
                && Json(Appearance(twice, child)) == appearance
                && Json(twice.Family.GetBirth(child)) == inherited
                && twice.Game.World.EventCount == eventsAtBirth
                && twice.Game.World.NextEventId == nextEventAtBirth,
                "SEIT-CHR-04-N3-local-second-save-load-never-redraws-body");
            BaseBody edited = Appearance(twice, child).BaseBody == BaseBody.Male ? BaseBody.Female : BaseBody.Male;
            twice.Game.Appearances.SetBaseBody(Core, child, edited);
            Fixture editedLoad = Restore(twice, Path.Combine(root, "edited-body"));
            Check(Appearance(editedLoad, child).BaseBody == edited
                && Json(editedLoad.Family.GetBirth(child)) == inherited,
                "BIRTH-Q2-SEIT-CHR-04-N3-local-load-keeps-edited-body-and-original-draw-receipt");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static bool OnlyOneBirth(Fixture f, ResidentId child)
    {
        int count = 0;
        foreach (ResidentRecord r in f.Game.World.ResidentsSnapshot())
            if (f.Family.GetBirth(r.Id) != null) count++;
        return count == 1 && f.Family.GetBirth(child) != null;
    }

    private static void BodyIndependence()
    {
        int bodies = 0;
        foreach (long seed in new[] { 1234L, 999L, 7L, 42L, 4242L })
        {
            BaseBody? expected = null;
            foreach (BaseBody body in new[] { BaseBody.Male, BaseBody.Female })
            {
                foreach (bool reverse in new[] { false, true })
                {
                    Fixture f = NewFixture(seed, body, reverse);
                    PregnancyRecord p = Conceive(f);
                    Tick(f, 3L * Day);
                    ResidentId child = OnlyChild(f);
                    Inherited(f, child);
                    BaseBody actual = Appearance(f, child).BaseBody;
                    Check(!expected.HasValue || actual == expected.Value,
                        "BIRTH-Q2-seed-" + seed + "-body-independent-of-parents-and-capability-" + body + "-" + reverse);
                    expected = actual;
                    bodies |= 1 << (int)actual;
                }
            }
        }
        Check(bodies == 3, "BIRTH-Q2-recorded-seed-corpus-exercises-both-base-bodies");
    }

    // SIM-CORE drives play (V-02/V-04); the executor's own commit still belongs
    // to ActivityExecutor. The same seed must yield one identical conception
    // record through either driver, never "Unknown state writer".
    private static void SimCoreTick()
    {
        Fixture f = NewFixture();
        var world = f.Game.World;
        CommandResult result = f.Family.Submit(new CommandEnvelope(world.AllocateCommand(Core),
            world.Epoch, world.GameSeconds, CommandSource.PlayerHuman,
            Person(f, f.Carrier).ResidentSequence + 1u, CommandKind.DirectActivity,
            f.Carrier, new ActivityId("ACT-REL-06"), (long)f.Scene.Bed,
            new CommandParameters.Direct(QueueMode.Immediate)));
        Require(result.Accepted, "BIRTH SimCore ACT-REL-06 accepted: " + result.Reason);
        Tick(f, 12L, Core);
        ExecutionRecord action = f.Game.Executor.GetResidentStatus(f.Carrier).Execution;
        Require(action != null && action.Stage == ExecutionStage.Perform,
            "BIRTH SimCore driver reached real executor Perform");
        long performAt = -1L;
        foreach (StageRecord stage in action.Trace)
            if (stage.Stage == ExecutionStage.Perform && !stage.Skipped) performAt = stage.GameSeconds;
        Require(performAt >= 0L, "BIRTH SimCore real Perform timestamp");
        Tick(f, performAt + 1800L - world.GameSeconds, Core);
        PregnancyRecord byCore;
        Require(f.Family.TryGetPregnancy(f.Carrier, out byCore),
            "BIRTH Tick(SimCore) advances completed TryForBaby to conception");
        Fixture executor = NewFixture();
        PregnancyRecord byExecutor = Conceive(executor);
        Check(byCore.Carrier == byExecutor.Carrier
            && byCore.OtherParent == byExecutor.OtherParent
            && byCore.ConceptionEvent == byExecutor.ConceptionEvent
            && byCore.DueGameTime == byExecutor.DueGameTime
            && byCore.CapacityHold == byExecutor.CapacityHold
            && world.ResidentCount == 2,
            "BIRTH-Tick-SimCore-conception-matches-executor-path");
    }

    public static int Main(string[] args)
    {
        failures = 0;
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negative) Console.WriteLine("BIRTH negative-fixture: insert duplicate Newborn after public reload");
        Run("ordinary-conception-delivery", Lifecycle);
        Run("pregnancy-and-delivery-roundtrip", () => RoundTrip(negative));
        Run("inheritance-and-seeded-body", BodyIndependence);
        Run("family-tick-writer-contract", SimCoreTick);
        if (failures == 0) Console.WriteLine("BIRTH ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
