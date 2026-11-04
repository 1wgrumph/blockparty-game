/*
AGING@1 TDD red contract, Blockparty.Simulation:
  Aging(WorldState, Clock), Tick(StateWriter): consume elapsed shared-clock time,
    carrying progress across consecutive stages without advancing a second clock.
    Call after Clock.AdvanceRealSeconds, including aggregate elapsed intervals.
  ResidentRecord.ElderRemainderDays: once-drawn Moderate duration (double), saved
    with the resident; ResidentRecord.IsFrail: terminal elder segment (bool).
The RNG stream/seed derivation and save representation are product-owned. No RNG,
world, birth service, clock, executor or codec is replaced by a test double.

Oracles: plan.json AGING and m3/test-engineer.json AGING suite_checks; canonical
SDoc BP-SEC-05, BP-LIFE-01/02, BP-TIME-04/05. Numeric tuning is proposed/unmeasured.
This package entry is new because CLOCK clamps progress and does not test actual
transitions. All eight stages use real records; the enabled lifecycle starts
with a real BIRTH TryForBaby child, never an injected newborn.

Local mapping (not whole SEIT-case acceptance):
  stages-{preset}: eight stages, identity/history; SEIT-LIFE-02-P1, SEIT-AC-07.
  off-{stage}, preset-fractions: all presets/fraction retention; SEIT-TIME-04-P1/N1,
    SEIT-IF-TIME-N3, SEIT-SM-CLOCK-I2, SEIT-AC-09.
  elder-{preset}, seeded-elder: seeded remainder/Frail/death, saved once;
    SEIT-LIFE-02-P2/N2, SEIT-TIME-04-N1, SEIT-AC-07/09.
  pacing-pause: executed child+teen time; SEIT-TIME-05-P1/P2, SEIT-AC-09.
  birth producer setup: pregnancy due time survives preset/speed changes;
    SEIT-IF-TIME-N5 (local only).
  negative-fixture: inject Newborn -> Toddler instead of Infant, then run the
    SAME next-stage assertion; it must print FAIL and exit nonzero.
SEIT-LIFE-02-N1/P3 and SEIT-TIME-05-N1/P3 are build/spec inspections, not runtime
claims here. Rendered appearance/actions, estate/care, starvation under Off,
packaged restarts and full shared-clock consumer ratios remain at their gates.
Resource group isolated-csharp: no editor/display/ports/network; unique save
roots cleaned in finally. Only injected time advances, with no real-time waits.
*/
using System;
using System.IO;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class AgingChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const long Day = 86400L;
    private const long TickSeconds = 6L;
    private static int failures;
    // Independent BP-SEC-05 oracle, not Clockwork's implementation table.
    private static readonly double[] Days = { 2.25, 11.25, 15.75, 31.5, 47.25, 63, 94.5, 31.5 };

    private sealed class Scene : ISceneContent
    {
        public ObjectId Bed;
        public void RegisterObjects(ActivityExecutor executor)
        {
            if ((long)Bed != 0L)
                executor.RegisterObject(Bed, new[] { new ApproachPose(0f, 0f, 0f), new ApproachPose(0f, 0f, 0f) });
        }
    }

    private sealed class Fixture
    {
        public GameStack Game;
        public Aging Aging;
        public ResidentId Resident, Other;
        public Scene Scene;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + ": " + e.GetType().Name + " " + e.Message); }
    }

    private static double Scale(AgingPreset preset)
    {
        return preset == AgingPreset.Accelerated ? 0.5 : preset == AgingPreset.Slow ? 4.0 : 1.0;
    }

    private static ResidentRecord Person(Fixture f)
    {
        ResidentRecord record;
        Require(f.Game.World.TryGetResident(f.Resident, out record), "persistent resident exists");
        return record;
    }

    private static Fixture NewFixture(LifeStage stage, AgingPreset preset, long seed = 1234L,
        float progress = 0f, int count = 2)
    {
        var world = WorldState.CreateSeeded(801L, seed, count, null);
        var residents = world.ResidentsSnapshot();
        var appearances = new AppearanceStore();
        foreach (var r in residents)
        {
            world.SetStage(Core, r.Id, stage, progress);
            appearances.Register(r.Id, new WorldRng(seed + (long)r.Id));
        }
        var clock = new Clock(world);
        clock.SetPreset(preset);
        var commands = new CommandArbitrator(world, clock);
        var executor = new ActivityExecutor(world, commands, clock);
        var accounts = new Accounts(world);
        var food = new Food(world, commands, executor, accounts);
        var expenses = new Expenses(world, accounts);
        var ownership = new Ownership(world, accounts);
        var housing = new Housing(world, accounts, ownership, executor, appearances);
        return new Fixture { Game = new GameStack(world, clock, commands, executor, appearances,
            accounts, food, expenses, ownership, housing), Aging = new Aging(world, clock),
            Resident = residents[0].Id, Other = residents[1].Id, Scene = new Scene() };
    }

    private static void Advance(Fixture f, long gameSeconds)
    {
        Require(gameSeconds >= 0L && gameSeconds % TickSeconds == 0L, "whole fixed ticks");
        f.Game.Clock.AdvanceRealSeconds(Core, (double)gameSeconds / 60.0);
        f.Aging.Tick(Core);
    }

    private static Fixture Born()
    {
        Fixture f = NewFixture(LifeStage.Adult, AgingPreset.Off);
        var w = f.Game.World;
        var family = f.Game.Family;
        var parent = Person(f);
        f.Game.Housing.RegisterHome(Core, parent.Home, 0L);
        f.Game.Housing.SetCaregiver(Core, f.Resident, true);
        f.Game.Housing.SetCaregiver(Core, f.Other, true);
        w.SetBond(Core, f.Resident, f.Other, 50f, 50f);
        w.SetBond(Core, f.Other, f.Resident, 50f, 50f);
        family.SetReproductiveCapabilities(Core, f.Resident, true, false);
        family.SetReproductiveCapabilities(Core, f.Other, false, true);
        family.SetPartner(Core, f.Resident, f.Other, true);
        do { f.Scene.Bed = w.AllocateObject(Core); } while ((long)f.Scene.Bed <= (long)f.Other);
        f.Scene.RegisterObjects(f.Game.Executor);
        family.RegisterBed(Core, f.Scene.Bed, parent.Home);
        f.Game.Executor.SetResidentPosition(f.Resident, new ApproachPose(0f, 0f, 0f));
        f.Game.Executor.SetResidentPosition(f.Other, new ApproachPose(0f, 0f, 0f));
        var result = family.Submit(new CommandEnvelope(w.AllocateCommand(Core), w.Epoch,
            w.GameSeconds, CommandSource.PlayerHuman, parent.ResidentSequence + 1u,
            CommandKind.DirectActivity, f.Resident, new ActivityId("ACT-REL-06"),
            (long)f.Scene.Bed, new CommandParameters.Direct(QueueMode.Immediate)));
        Require(result.Accepted, "BIRTH TryForBaby accepted: " + result.Reason);
        f.Game.Clock.AdvanceRealSeconds(Core, 1812.0 / 60.0,
            () => family.Tick(StateWriter.ActivityExecutor));
        PregnancyRecord pregnancy;
        Require(family.TryGetPregnancy(f.Resident, out pregnancy), "real seeded BIRTH conception");
        long due = pregnancy.DueGameTime;
        f.Game.Clock.SetPreset(AgingPreset.Slow);
        f.Game.Clock.SetSpeed(SimulationSpeed.Fast);
        family.Tick(Core);
        PregnancyRecord after;
        Check(family.TryGetPregnancy(f.Resident, out after) && after.DueGameTime == due,
            "birth-preset-speed-SEIT-IF-TIME-N5-local-due-unchanged");
        f.Game.Clock.SetSpeed(SimulationSpeed.Normal);
        f.Game.Clock.SetPreset(AgingPreset.Off);
        f.Game.Clock.AdvanceRealSeconds(Core, (double)(due - w.GameSeconds) / 60.0,
            () => family.Tick(StateWriter.ActivityExecutor));
        Require(w.ResidentCount == 3, "real BIRTH produced exactly one child");
        foreach (var r in w.ResidentsSnapshot())
            if (r.Id != f.Resident && r.Id != f.Other) f.Resident = r.Id;
        Require(Person(f).Stage == LifeStage.Newborn && Person(f).Family.Count == 2
            && family.GetBirth(f.Resident) != null, "BIRTH newborn with factual parents and receipt");
        f.Aging = new Aging(w, f.Game.Clock);
        return f;
    }

    private static string History(Fixture f)
    {
        var r = Person(f);
        var traits = new int[6];
        for (int i = 0; i < traits.Length; i++) traits[i] = r.GetTrait((TraitAxis)i);
        return JsonSerializer.Serialize(new { r.Id, r.DisplayName, r.Household, r.Home,
            r.Money, r.JobId, r.CareerLevel, r.CareerPoints, r.EducationPoints,
            r.Skills, r.CompletedCourses, r.Bonds, r.Family, r.Memories, traits,
            Birth = f.Game.Family.GetBirth(r.Id) }, new JsonSerializerOptions { IncludeFields = true });
    }

    private static EventId AddHistory(Fixture f)
    {
        var w = f.Game.World;
        w.SetMoney(Core, f.Resident, 96L);
        w.AddSkillPoints(Core, f.Resident, "Cooking", 320);
        w.SetCareer(Core, f.Resident, "Barista", 2, 40);
        w.SetEducation(Core, f.Resident, 25);
        w.SetBond(Core, f.Resident, f.Other, 12.5f, 0f);
        var entry = w.AppendEvent(Core, "FirstMeeting", f.Resident, "aging continuity fixture");
        w.AddMemory(Core, f.Resident, new MemoryEntry(entry.Event, "FirstMeeting", w.GameSeconds,
            Person(f).Home, Array.AsReadOnly(new[] { f.Resident, f.Other }), "OK", "participant"));
        return entry.Event;
    }

    private static void Continuity(Fixture f, string history, EventId original, string name)
    {
        bool found = false;
        foreach (var e in f.Game.World.Events)
            if (e.Event == original && e.Kind == "FirstMeeting" && e.Resident == f.Resident) found = true;
        Check(History(f) == history && found, name + "-identity-family-progress-history");
    }

    private static Fixture Reload(Fixture f)
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockparty-aging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = SaveV1.SaveGame(f.Game, directory);
            Require(saved.Ok, "public SAVE-V2 write: " + saved.Detail);
            var loaded = SaveV1.LoadGame(directory, f.Scene);
            Require(loaded.Ok && loaded.Game != null, "public SAVE-V2 load: " + loaded.Notice);
            var g = loaded.Game;
            return new Fixture { Game = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor,
                g.Appearances, g.Accounts, g.Food, g.Expenses, g.Ownership, g.Housing, g.Family),
                Aging = new Aging(g.World, g.Clock), Resident = f.Resident, Other = f.Other, Scene = f.Scene };
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Stages(AgingPreset preset, bool negative)
    {
        Fixture f = Born();
        EventId historyEvent = AddHistory(f);
        string history = History(f);
        f.Game.Clock.SetPreset(preset);
        long started = f.Game.World.GameSeconds;
        double elapsed = 0.0;
        for (int stage = 0; stage < 7; stage++)
        {
            long duration = (long)(Days[stage] * Day * Scale(preset));
            Advance(f, duration - TickSeconds);
            Check(Person(f).Stage == (LifeStage)stage && Person(f).IsAlive,
                "stages-" + preset + "-" + (LifeStage)stage + "-no-early-transition");
            Advance(f, TickSeconds);
            if (negative && stage == 0)
                f.Game.World.SetStage(Core, f.Resident, LifeStage.Toddler, 0f);
            Check(Person(f).Stage == (LifeStage)(stage + 1) && Person(f).IsAlive
                && Math.Abs(Person(f).StageProgressDays) <= (double)TickSeconds / Day / Scale(preset),
                "stages-" + preset + "-" + (LifeStage)stage + "-next-stage-no-skip");
            Continuity(f, history, historyEvent, "stages-" + preset + "-" + (LifeStage)(stage + 1));
            if (negative) return;
            elapsed += duration;
            Check(f.Game.World.GameSeconds == started + (long)elapsed,
                "stages-" + preset + "-" + (LifeStage)stage + "-shared-clock-duration");
            if (stage == 3)
            {
                long now = f.Game.World.GameSeconds;
                float progress = Person(f).StageProgressDays;
                f = Reload(f);
                Check(f.Game.World.GameSeconds == now && Person(f).Stage == LifeStage.Teen
                    && Person(f).StageProgressDays == progress, "stages-" + preset + "-reload-no-catchup");
                Continuity(f, history, historyEvent, "stages-" + preset + "-reload");
            }
        }
        Check(Person(f).ElderRemainderDays >= 31.5 * 0.75 && Person(f).ElderRemainderDays <= 31.5 * 1.25,
            "stages-" + preset + "-elder-seeded-remainder-range");
    }

    private static void OffStages()
    {
        for (int stage = 0; stage < 8; stage++)
        {
            Fixture f = NewFixture((LifeStage)stage, AgingPreset.Off, progress: 0.5f);
            EventId entry = AddHistory(f);
            string history = History(f);
            f.Aging.Tick(Core);
            double remainder = Person(f).ElderRemainderDays;
            bool frail = Person(f).IsFrail;
            Advance(f, 1000L * Day);
            Check(Person(f).Stage == (LifeStage)stage && Person(f).StageProgressDays == 0.5f
                && Person(f).IsAlive && Person(f).ElderRemainderDays == remainder && Person(f).IsFrail == frail,
                "off-" + (LifeStage)stage + "-holds-stage-elder-and-life");
            Continuity(f, history, entry, "off-" + (LifeStage)stage);
        }
    }

    private static void PresetFractions()
    {
        Fixture f = NewFixture(LifeStage.Child, AgingPreset.Moderate, progress: 12.6f, count: 8);
        for (int i = 0; i < 20; i++)
            foreach (AgingPreset preset in new[] { AgingPreset.Slow, AgingPreset.Accelerated, AgingPreset.Off, AgingPreset.Moderate })
            {
                int events = f.Game.World.EventCount;
                f.Game.Clock.SetPreset(preset);
                f.Aging.Tick(Core);
                bool same = f.Game.World.EventCount == events;
                foreach (var r in f.Game.World.ResidentsSnapshot())
                    same &= r.Stage == LifeStage.Child && r.StageProgressDays == 12.6f && r.IsAlive;
                Check(same, "preset-fractions-" + i + "-" + preset + "-eight-residents-no-lifecycle-event");
            }
        foreach (AgingPreset preset in new[] { AgingPreset.Moderate, AgingPreset.Slow, AgingPreset.Accelerated })
        {
            Fixture p = NewFixture(LifeStage.Child, preset, progress: 12.6f);
            // SDoc fraction 0.4: actual transitions after 18.9/75.6/9.45 days.
            long remaining = (long)Math.Round(18.9 * Day * Scale(preset));
            Advance(p, remaining - TickSeconds);
            Check(Person(p).Stage == LifeStage.Child, "preset-fractions-" + preset + "-remaining-not-early");
            Advance(p, 2L * TickSeconds);
            Check(Person(p).Stage == LifeStage.Teen, "preset-fractions-" + preset + "-remaining-duration");
        }
    }

    private static Fixture Elder(AgingPreset preset, long seed = 1234L)
    {
        Fixture f = NewFixture(LifeStage.Adult, preset, seed, 94.25f);
        Advance(f, (long)(0.25 * Day * Scale(preset)));
        Require(Person(f).Stage == LifeStage.Elder, "AGING Adult -> Elder capability");
        return f;
    }

    private static void ElderLifetime(AgingPreset preset)
    {
        Fixture f = Elder(preset);
        EventId entry = AddHistory(f);
        string history = History(f);
        double duration = Person(f).ElderRemainderDays;
        Require(duration >= 23.625 && duration <= 39.375, "seeded Moderate elder duration in [23.625,39.375]");
        long untilFrail = (long)Math.Ceiling((duration - 1.0) * Day * Scale(preset) / TickSeconds) * TickSeconds;
        long untilDeath = (long)Math.Ceiling(duration * Day * Scale(preset) / TickSeconds) * TickSeconds;
        long half = untilFrail / (2L * TickSeconds) * TickSeconds;
        Advance(f, half);
        float progress = Person(f).StageProgressDays;
        f = Reload(f);
        Check(Person(f).ElderRemainderDays == duration && Person(f).StageProgressDays == progress
            && !Person(f).IsFrail && Person(f).IsAlive, "elder-" + preset + "-midstage-public-codec-no-redraw");
        Advance(f, untilFrail - TickSeconds - half);
        Check(Person(f).IsAlive && !Person(f).IsFrail, "elder-" + preset + "-before-final-scaled-day");
        Advance(f, TickSeconds);
        Check(Person(f).IsAlive && Person(f).IsFrail, "elder-" + preset + "-frail-final-scaled-day");
        progress = Person(f).StageProgressDays;
        foreach (AgingPreset changed in new[] { AgingPreset.Off, AgingPreset.Slow, AgingPreset.Accelerated, AgingPreset.Moderate, preset })
        {
            f.Game.Clock.SetPreset(changed);
            f.Aging.Tick(Core);
            if (changed == AgingPreset.Off) Advance(f, 100L * Day);
            Check(Person(f).ElderRemainderDays == duration && Person(f).StageProgressDays == progress
                && Person(f).IsFrail && Person(f).IsAlive, "elder-" + preset + "-" + changed + "-holds-draw-fraction-frail");
        }
        f = Reload(f);
        Check(Person(f).ElderRemainderDays == duration && Person(f).StageProgressDays == progress
            && Person(f).IsFrail && Person(f).IsAlive, "elder-" + preset + "-frail-public-codec-no-redraw");
        Advance(f, untilDeath - untilFrail - TickSeconds);
        Check(Person(f).IsAlive, "elder-" + preset + "-no-early-death");
        Advance(f, TickSeconds);
        Check(!Person(f).IsAlive && Person(f).Stage == LifeStage.Elder, "elder-" + preset + "-old-age-death");
        Continuity(f, history, entry, "elder-" + preset + "-death");
        int events = f.Game.World.EventCount;
        f = Reload(f);
        Advance(f, 2L * Day);
        Check(!Person(f).IsAlive && f.Game.World.EventCount == events
            && Person(f).ElderRemainderDays == duration, "elder-" + preset + "-reload-death-once-no-resurrection");
    }

    private static void SeededElder()
    {
        Fixture a = Elder(AgingPreset.Moderate), b = Elder(AgingPreset.Moderate);
        Fixture otherSeed = Elder(AgingPreset.Moderate, 9876L);
        var ar = a.Game.World.ResidentsSnapshot();
        var br = b.Game.World.ResidentsSnapshot();
        bool same = true, variedIdentity = false, variedSeed = false;
        for (int i = 0; i < ar.Length; i++)
        {
            double duration = ar[i].ElderRemainderDays;
            ResidentRecord other;
            Require(otherSeed.Game.World.TryGetResident(ar[i].Id, out other), "matching seeded identity");
            same &= ar[i].Id == br[i].Id && duration == br[i].ElderRemainderDays
                && duration >= 23.625 && duration <= 39.375;
            variedIdentity |= duration != ar[0].ElderRemainderDays;
            variedSeed |= duration != other.ElderRemainderDays;
            Console.WriteLine("DRAW seed=1234 resident=" + (long)ar[i].Id + " moderateDays=" + duration.ToString("R"));
        }
        Check(same && variedIdentity && variedSeed, "seeded-elder-repeat-entry-seed-and-identity-not-fixed-reference");
        a.Aging.Tick(Core);
        a.Aging.Tick(Core);
        Check(Person(a).ElderRemainderDays == Person(b).ElderRemainderDays, "seeded-elder-repeated-tick-no-redraw");
    }

    private static void PacingPause()
    {
        Fixture f = NewFixture(LifeStage.Child, AgingPreset.Moderate);
        Advance(f, (long)(31.5 * Day));
        Check(Person(f).Stage == LifeStage.Teen, "pacing-pause-child-12.6-injected-real-hours");
        long now = f.Game.World.GameSeconds;
        float progress = Person(f).StageProgressDays;
        f.Game.Clock.Pause();
        f.Game.Clock.AdvanceRealSeconds(Core, 600.0);
        f.Aging.Tick(Core);
        Check(f.Game.World.GameSeconds == now && Person(f).StageProgressDays == progress
            && Person(f).Stage == LifeStage.Teen, "pacing-pause-600-real-seconds-zero-aging");
        f.Game.Clock.Resume();
        Advance(f, (long)(47.25 * Day) - TickSeconds);
        Check(Person(f).Stage == LifeStage.Teen, "pacing-pause-no-early-young-adult");
        Advance(f, TickSeconds);
        double hours = (double)f.Game.World.GameSeconds / 60.0 / 3600.0;
        Check(Person(f).Stage == LifeStage.YoungAdult && Math.Abs(hours - 31.5) <= 0.1 / 3600.0
            && hours >= 29.9 && hours <= 33.1, "pacing-pause-executed-child-teen-31.5-hours");
    }

    public static int Main(string[] args)
    {
        bool negative = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negative)
        {
            Console.WriteLine("AGING negative-fixture: skip Infant using real WorldState.SetStage");
            Run("negative-fixture", () => Stages(AgingPreset.Moderate, true));
        }
        else
        {
            foreach (AgingPreset preset in new[] { AgingPreset.Accelerated, AgingPreset.Moderate, AgingPreset.Slow })
            {
                Run("stages-" + preset, () => Stages(preset, false));
                Run("elder-" + preset, () => ElderLifetime(preset));
            }
            Run("off-stages", OffStages);
            Run("preset-fractions", PresetFractions);
            Run("seeded-elder", SeededElder);
            Run("pacing-pause", PacingPause);
        }
        if (failures == 0) Console.WriteLine("AGING ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
