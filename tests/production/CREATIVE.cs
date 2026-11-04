/* CREATIVE@1 red contract, BP-ACT-12 / catalog ACT-CREA-01..03.
New API in Blockparty.Activities.Creative (Creative.cs):
  Creative(WorldState world)
  bool Complete(StateWriter writer, ActionInstanceId action, ActivityId activity,
                ResidentId author, EventId chosenMemory)
  IReadOnlyList<WorkItem> WorkItems
  bool Display(StateWriter writer, ObjectId item, ObjectId wall)
  bool ViewDuringChat(StateWriter writer, ResidentId viewer, ObjectId item,
                      float distanceMetres)
  IReadOnlyList<ViewerAward> ViewerAwards
WorkItem readable fields/getters: WorkItemRef Reference; string QualityTier.
ViewerAward readable fields/getters: ResidentId Viewer; ObjectId Item; long GameDay.
Complete is the post-executor completion boundary (like Relationships.Complete),
not a replacement for commands/navigation. It consumes a real committed action
once. MakeWallArt costs 10, grants Creativity +15 and creates one item; practice
grants Creativity +15 and Fun +10 without an item. Quality is crude/plain/fine
for skill levels 0-2/3-6/7-10. Chosen memory is a personally known factual event;
the summary records the author's personality without prescribing text format.
Display places the existing item. Successful chat viewing awards Fun +5 with
ledger key (viewer,item,game day), at most once per key.
Save seam: existing eight-argument GameStack overload additionally accepts the
live Creative as its ninth argument; LoadedGame.Creative restores that service.

Local mapping: ProvenanceAndQuality, Practice -> SEIT-ACT-12-P1, AC-04/15
subclaims; DailyAwards -> ACT-12-P1/N1; SaveRoundTrip -> ACT-12-N1. The IF-IMG
P1/N1..N4 and AC-04/15 whole cases remain at IMAGE/integration/acceptance gates:
this suite does not prove provider output, gateway faults, disclosure or UI.
New module follows the package selector; no existing creative suite exists.
Real STATE, COMMAND, NAV and public SAVE-V2 codec; no mocks or editor startup.
Resource group isolated-csharp; save uses a unique directory cleaned in finally.
negative-fixture changes the repeat viewer to a different resident: the unchanged
same-key rejection oracle must print FAIL and exit 1 once capability exists.
*/
using System;
using System.IO;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class CreativeChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const long Day = 86400L;
    private static int failures;

    private sealed class Scene : ISceneContent
    {
        public ObjectId Station;
        public void RegisterObjects(ActivityExecutor executor)
        {
            executor.RegisterObject(Station, new[] { new ApproachPose(0f, 0f, 0f) });
        }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public Creative Creative;
        public ResidentId Author, Viewer, OtherViewer;
        public EventId Memory, OtherMemory;
        public ObjectId Station, Wall;

        public GameStack SaveStack()
        {
            return new GameStack(World, Clock, Arbitrator, Executor, new AppearanceStore(),
                Accounts, Food, new Expenses(World, Accounts), Creative);
        }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new InvalidOperationException(detail);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static ResidentRecord Person(WorldState world, ResidentId id)
    {
        ResidentRecord resident;
        Require(world.TryGetResident(id, out resident), "owned fixture resident missing");
        return resident;
    }

    private static SkillEntry Skill(Fixture f)
    {
        foreach (SkillEntry skill in Person(f.World, f.Author).Skills)
            if (skill.SkillId == "Creativity") return skill;
        return new SkillEntry("Creativity", 0, 0);
    }

    private static Fixture NewFixture(int level = 4, int trait = 35)
    {
        Fixture f = new Fixture();
        f.World = WorldState.CreateSeeded(701L, 4242L, 3,
            new[] { "Author", "Viewer", "OtherViewer" });
        ResidentRecord[] people = f.World.ResidentsSnapshot();
        f.Author = people[0].Id;
        f.Viewer = people[1].Id;
        f.OtherViewer = people[2].Id;
        f.World.SetMoney(Core, f.Author, 100L);
        f.World.SetNeed(Core, f.Author, Need.Fun, 40f);
        f.World.SetNeed(Core, f.Viewer, Need.Fun, 40f);
        f.World.SetNeed(Core, f.OtherViewer, Need.Fun, 40f);
        for (int i = 0; i < StateSchema.TraitCount; i++)
            f.World.SetTrait(Core, f.Author, (TraitAxis)i, trait);
        // BP-SEC-05 level thresholds: 100 * (level + 1) points per level.
        f.World.AddSkillPoints(Core, f.Author, "Creativity", 50 * level * (level + 1));
        Require(Skill(f).Level == level && Skill(f).Points == 0, "skill fixture level");
        WorldEvent memory = f.World.AppendEvent(Core, "help", f.Author, "helped Viewer");
        f.Memory = memory.Event;
        f.World.AddMemory(Core, f.Author, new MemoryEntry(memory.Event, "help", 0L,
            people[0].Home, new[] { f.Author, f.Viewer }, "helped", "participant"));
        WorldEvent other = f.World.AppendEvent(Core, "argument", f.Author, "argued with Viewer");
        f.OtherMemory = other.Event;
        f.World.AddMemory(Core, f.Author, new MemoryEntry(other.Event, "argument", 0L,
            people[0].Home, new[] { f.Author, f.Viewer }, "argued", "participant"));
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, f.Accounts);
        f.Creative = new Creative(f.World);
        // TargetId shares the resident/object long domain; avoid owned resident IDs.
        do { f.Station = f.World.AllocateObject(Core); } while ((long)f.Station <= 3L);
        f.Wall = f.World.AllocateObject(Core);
        new Scene { Station = f.Station }.RegisterObjects(f.Executor);
        f.Executor.SetResidentPosition(f.Author, new ApproachPose(0f, 0f, 0f));
        return f;
    }

    private static ActionInstanceId Complete(Fixture f, string code, EventId memory)
    {
        ResidentRecord r = Person(f.World, f.Author);
        CommandResult submitted = f.Arbitrator.Submit(new CommandEnvelope(
            f.World.AllocateCommand(Core), f.World.Epoch, f.World.GameSeconds,
            CommandSource.PlayerHuman, r.ResidentSequence + 1u,
            CommandKind.DirectActivity, r.Id, new ActivityId(code), (long)f.Station,
            new CommandParameters.Direct(QueueMode.Immediate)));
        Require(submitted.Accepted, "fixture command refused: " + submitted.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Author).Action;
        f.Executor.Tick(Exec);
        f.Executor.Tick(Exec);
        Require(f.Executor.GetExecution(action).Stage == ExecutionStage.Perform,
            "fixture did not reach Perform");
        f.Clock.AdvanceRealSeconds(Core, code == "ACT-CREA-01" ? 90.0 : 60.0);
        f.Executor.Tick(Exec);
        Require(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed,
            "fixture did not complete creative activity");
        Require(f.Creative.Complete(Exec, action, new ActivityId(code), f.Author, memory),
            "creative completion not applied");
        return action;
    }

    private static WorkItem Make(Fixture f)
    {
        ActionInstanceId action = Complete(f, "ACT-CREA-01", f.Memory);
        f.Creative.Complete(Exec, action, new ActivityId("ACT-CREA-01"), f.Author, f.Memory);
        Require(f.Creative.WorkItems.Count == 1, "MakeWallArt must create exactly one item");
        return f.Creative.WorkItems[0];
    }

    private static void ProvenanceAndQuality()
    {
        int[] levels = { 0, 2, 3, 4, 6, 7, 10 };
        string[] expected = { "crude", "crude", "plain", "plain", "plain", "fine", "fine" };
        for (int i = 0; i < levels.Length; i++)
        {
            Fixture f = NewFixture(levels[i]);
            WorkItem item = Make(f);
            Check(item.Reference.Author == f.Author && item.Reference.MemoryEvent == f.Memory
                && !string.IsNullOrWhiteSpace(item.Reference.PersonalitySummary)
                && item.QualityTier == expected[i],
                "SEIT-ACT-12-P1-ProvenanceAndQuality-level-" + levels[i]);
            Check(Person(f.World, f.Author).Money == 90L && Skill(f).Points == 15,
                "SEIT-ACT-12-P1-MakeWallArt-materials-and-skill-" + levels[i]);
        }
        Fixture first = NewFixture();
        WorkItem original = Make(first);
        string summary = original.Reference.PersonalitySummary;
        Fixture contrasting = NewFixture(4, -35);
        WorkItem different = Make(contrasting);
        Check(summary != different.Reference.PersonalitySummary,
            "SEIT-ACT-12-P1-personality-input-affects-recorded-summary");
        first.World.SetTrait(Core, first.Author, TraitAxis.Creative, -35);
        first.World.AddSkillPoints(Core, first.Author, "Creativity", 5000);
        Complete(first, "ACT-CREA-01", first.OtherMemory);
        Check(original.Reference.PersonalitySummary == summary && original.QualityTier == "plain"
            && original.Reference.MemoryEvent == first.Memory
            && first.Creative.WorkItems[1].Reference.MemoryEvent == first.OtherMemory
            && first.Creative.WorkItems[1].Reference.Item != original.Reference.Item,
            "SEIT-ACT-12-P1-input-snapshots-and-chosen-memory-retained");
    }

    private static void Practice()
    {
        Fixture f = NewFixture();
        ActionInstanceId action = Complete(f, "ACT-CREA-02", default(EventId));
        Check(f.Creative.WorkItems.Count == 0 && Skill(f).Points == 15
            && Person(f.World, f.Author).GetNeed(Need.Fun) == 50f
            && Person(f.World, f.Author).Money == 100L,
            "SEIT-ACT-12-P1-Practice-no-item-required-skill-and-fun");
        f.Creative.Complete(Exec, action, new ActivityId("ACT-CREA-02"), f.Author, default(EventId));
        Check(f.Creative.WorkItems.Count == 0 && Skill(f).Points == 15
            && Person(f.World, f.Author).GetNeed(Need.Fun) == 50f,
            "SEIT-ACT-12-N1-Practice-completion-not-reapplied");
    }

    private static int Awards(Creative creative, ResidentId viewer, ObjectId item, long day)
    {
        int count = 0;
        foreach (var award in creative.ViewerAwards)
            if (award.Viewer == viewer && award.Item == item && award.GameDay == day) count++;
        return count;
    }

    private static void DailyAwards(bool negativeFixture)
    {
        Fixture f = NewFixture();
        ObjectId item = Make(f).Reference.Item;
        Require(f.Creative.Display(Exec, item, f.Wall), "display existing item");
        // Straddle midnight by one second: daily limits use calendar days, not 24h cooldowns.
        f.World.SetGameSeconds(Core, Day - 1L);
        Require(f.Creative.ViewDuringChat(Exec, f.Viewer, item, 5f), "first view award");
        bool repeat = f.Creative.ViewDuringChat(Exec,
            negativeFixture ? f.OtherViewer : f.Viewer, item, 5f);
        Check(!repeat && f.Creative.ViewerAwards.Count == 1
            && Awards(f.Creative, f.Viewer, item, 0L) == 1
            && Person(f.World, f.Viewer).GetNeed(Need.Fun) == 45f,
            "SEIT-ACT-12-N1-DailyAwards-second-award-same-day-refused");
        if (negativeFixture) return;
        Check(f.Creative.ViewDuringChat(Exec, f.OtherViewer, item, 5f)
            && Awards(f.Creative, f.OtherViewer, item, 0L) == 1
            && Person(f.World, f.OtherViewer).GetNeed(Need.Fun) == 45f,
            "SEIT-ACT-12-P1-DailyAwards-viewers-independent");
        f.World.SetGameSeconds(Core, Day);
        Check(f.Creative.ViewDuringChat(Exec, f.Viewer, item, 5f)
            && Awards(f.Creative, f.Viewer, item, 1L) == 1
            && Person(f.World, f.Viewer).GetNeed(Need.Fun) == 50f,
            "SEIT-ACT-12-P1-DailyAwards-midnight-resets-per-game-day");
        Complete(f, "ACT-CREA-01", f.OtherMemory);
        ObjectId second = f.Creative.WorkItems[1].Reference.Item;
        Require(f.Creative.Display(Exec, second, f.World.AllocateObject(Core)), "display second item");
        long currentDay = f.World.GameSeconds / Day;
        Check(f.Creative.ViewDuringChat(Exec, f.Viewer, second, 5f)
            && Awards(f.Creative, f.Viewer, item, currentDay) == 1
            && Awards(f.Creative, f.Viewer, second, currentDay) == 1,
            "SEIT-ACT-12-P1-DailyAwards-items-independent");
        Check(!f.Creative.ViewDuringChat(Exec, f.Viewer, item, 5f)
            && Person(f.World, f.Viewer).GetNeed(Need.Fun) == 55f,
            "SEIT-ACT-12-P1-DailyAwards-next-day-one-new-award");
    }

    private static void SaveRoundTrip()
    {
        Fixture f = NewFixture();
        WorkItem item = Make(f);
        Require(f.Creative.Display(Exec, item.Reference.Item, f.Wall), "display before save");
        Require(f.Creative.ViewDuringChat(Exec, f.Viewer, item.Reference.Item, 5f), "award before save");
        string directory = Path.Combine(Path.GetTempPath(), "creative-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveWriteResult saved = SaveV1.SaveGame(f.SaveStack(), directory);
            Require(saved.Ok, "public save failed: " + saved.Detail);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene { Station = f.Station });
            Require(loaded.Ok, "public load failed: " + loaded.Notice);
            Creative creative = loaded.Game.Creative;
            Require(creative != null && creative.WorkItems.Count == 1, "restored creative item missing");
            WorkItem restored = creative.WorkItems[0];
            Check(restored.Reference.Item == item.Reference.Item
                && restored.Reference.Author == f.Author && restored.Reference.MemoryEvent == f.Memory
                && restored.Reference.PersonalitySummary == item.Reference.PersonalitySummary
                && restored.QualityTier == "plain" && creative.ViewerAwards.Count == 1
                && Awards(creative, f.Viewer, item.Reference.Item, 0L) == 1,
                "SEIT-ACT-12-N1-SaveRoundTrip-item-inputs-quality-ledger");
            creative.Display(Exec, item.Reference.Item, f.Wall);
            Check(!creative.ViewDuringChat(Exec, f.Viewer, item.Reference.Item, 5f)
                && creative.WorkItems.Count == 1 && creative.ViewerAwards.Count == 1
                && Person(loaded.Game.World, f.Viewer).GetNeed(Need.Fun) == 45f
                && Person(loaded.Game.World, f.Author).Money == 90L,
                "SEIT-ACT-12-N1-SaveRoundTrip-redisplay-and-repeat-no-duplicates");
            loaded.Game.World.SetGameSeconds(Core, Day);
            Check(creative.ViewDuringChat(Exec, f.Viewer, item.Reference.Item, 5f)
                && creative.ViewerAwards.Count == 2
                && Person(loaded.Game.World, f.Viewer).GetNeed(Need.Fun) == 50f,
                "SEIT-ACT-12-P1-SaveRoundTrip-next-day-award");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public static int Main(string[] args)
    {
        bool negativeFixture = Array.IndexOf(args, "negative-fixture") >= 0;
        Run("ProvenanceAndQuality", ProvenanceAndQuality);
        Run("Practice", Practice);
        Run("DailyAwards", () => DailyAwards(negativeFixture));
        Run("SaveRoundTrip", SaveRoundTrip);
        if (failures != 0) return 1;
        Console.WriteLine("CREATIVE ALL PASS");
        return 0;
    }
}
