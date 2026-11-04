// SAVE production checks. Standalone console program: compiled and run with
// the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the Unity test
// path. Positive checks prove atomic complete v1 snapshots: exact round-trip
// of every state group, in-progress resume with elapsed game seconds
// preserved, completion at the same absolute game second as an uninterrupted
// reference, exactly-once effects across the boundary, no catch-up, and a
// separately versioned settings record. Negative checks prove fault-injected
// tmp/flush/rename failures keep the previous valid save, corrupt input is
// refused with the bytes retained and the running world untouched, unknown
// versions are refused naming the version, and unrestorable work reconciles
// with a report note instead of duplicating effects. Mirrors the NAV pattern.
// Object ids avoid live resident ids throughout: TargetId is one bare long
// shared by both counters, and NAV resolves a registered object first, so
// fixtures allocate the object past the last resident id.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class SaveChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;

    private static int failures;
    private static int dirCounter;
    private static long nextId = 5000;
    private static readonly string runId = Guid.NewGuid().ToString("N");
    private static readonly List<string> ownedDirs = new List<string>();

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static CommandId NextCommand()
    {
        nextId++;
        return (CommandId)nextId;
    }

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public AppearanceStore Appearances;

        public GameStack SaveStack()
        {
            return new GameStack(World, Clock, Arbitrator, Executor, Appearances);
        }
    }

    private static Stack NewStack(long worldId, long seed, string[] names)
    {
        AppearanceStore appearances;
        WorldState world = ResidentFactory.CreateNewGame(worldId, seed, names, out appearances);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Stack stack = new Stack();
        stack.World = world;
        stack.Clock = clock;
        stack.Arbitrator = arbitrator;
        stack.Executor = new ActivityExecutor(world, arbitrator, clock);
        stack.Appearances = appearances;
        return stack;
    }

    private static Stack NewSeeded(long worldId, long seed, int residents)
    {
        WorldState world = WorldState.CreateSeeded(worldId, seed, residents, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Stack stack = new Stack();
        stack.World = world;
        stack.Clock = clock;
        stack.Arbitrator = arbitrator;
        stack.Executor = new ActivityExecutor(world, arbitrator, clock);
        stack.Appearances = new AppearanceStore();
        return stack;
    }

    private static ResidentRecord[] SortedResidents(WorldState world)
    {
        ResidentRecord[] residents = world.ResidentsSnapshot();
        for (int i = 1; i < residents.Length; i++)
        {
            ResidentRecord key = residents[i];
            int j = i - 1;
            while (j >= 0 && (long)residents[j].Id > (long)key.Id)
            {
                residents[j + 1] = residents[j];
                j--;
            }
            residents[j + 1] = key;
        }
        return residents;
    }

    private static CommandResult Direct(Stack stack, ResidentRecord resident, string activity,
        QueueMode mode, long? targetId)
    {
        return DirectAs(stack, resident, activity, mode, targetId, CommandSource.PlayerHuman);
    }

    private static CommandResult DirectAs(Stack stack, ResidentRecord resident, string activity,
        QueueMode mode, long? targetId, CommandSource source)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        return stack.Arbitrator.Submit(new CommandEnvelope(
            NextCommand(), stack.World.Epoch, stack.World.GameSeconds,
            source, current.ResidentSequence + 1u,
            CommandKind.DirectActivity, resident.Id, new ActivityId(activity),
            targetId, new CommandParameters.Direct(mode)));
    }

    private static CommandResult Travel(Stack stack, ResidentRecord resident, long? targetId)
    {
        ResidentRecord current;
        stack.World.TryGetResident(resident.Id, out current);
        return stack.Arbitrator.Submit(new CommandEnvelope(
            NextCommand(), stack.World.Epoch, stack.World.GameSeconds,
            CommandSource.PlayerHuman, current.ResidentSequence + 1u,
            CommandKind.TravelTo, resident.Id, null,
            targetId, new CommandParameters.Travel(QueueMode.Immediate)));
    }

    // Integer tick steps: 0.1 real seconds at Normal is exactly one tick.
    // Fractional edges save/load the exact sub-tick remainder (fraction-*
    // checks), so both sides of the boundary advance identically there too.
    private static void Step(Stack stack, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            stack.Clock.AdvanceRealSeconds(Core, 0.1);
            stack.Executor.Tick(Exec);
        }
    }

    private static void StepLoaded(LoadedGame game, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            game.Clock.AdvanceRealSeconds(Core, 0.1);
            game.Executor.Tick(Exec);
        }
    }

    private static string UniqueDir(string tag)
    {
        dirCounter++;
        string dir = Path.Combine(Path.GetTempPath(),
            "save-test-" + tag + "-" + runId + "-" + dirCounter.ToString());
        ownedDirs.Add(dir);
        return dir;
    }

    // Only directories this run created via UniqueDir are ever deleted.
    private static void CleanupOwnedDirs()
    {
        for (int i = 0; i < ownedDirs.Count; i++)
        {
            try
            {
                if (Directory.Exists(ownedDirs[i]))
                {
                    Directory.Delete(ownedDirs[i], true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ACCESS: every pre-existing and new settings field plus each remap entry.
    private static bool SameAccess(SettingsRecord actual, SettingsRecord expected)
    {
        if (actual.SchemaVersion != expected.SchemaVersion
            || actual.MixerLevel != expected.MixerLevel
            || actual.TextScale != expected.TextScale
            || actual.ReducedMotion != expected.ReducedMotion
            || actual.AmbienceLevel != expected.AmbienceLevel
            || actual.WorldLevel != expected.WorldLevel
            || actual.VoiceLevel != expected.VoiceLevel
            || actual.InterfaceLevel != expected.InterfaceLevel
            || actual.NonColorCues != expected.NonColorCues
            || actual.Captions != expected.Captions
            || actual.InputRemaps.Count != expected.InputRemaps.Count)
        {
            return false;
        }
        foreach (KeyValuePair<string, string> pair in expected.InputRemaps)
        {
            string key;
            if (!actual.InputRemaps.TryGetValue(pair.Key, out key) || key != pair.Value)
            {
                return false;
            }
        }
        return true;
    }

    private sealed class SceneObj
    {
        public ObjectId Id;
        public ApproachPose[] Poses;
        public bool Reachable = true;
    }

    private sealed class FixedScene : ISceneContent
    {
        public readonly List<SceneObj> Objs = new List<SceneObj>();

        public void RegisterObjects(ActivityExecutor executor)
        {
            for (int i = 0; i < Objs.Count; i++)
            {
                executor.RegisterObject(Objs[i].Id, Objs[i].Poses);
                executor.SetReachable(Objs[i].Id, Objs[i].Reachable);
            }
        }
    }

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor)
        {
        }
    }

    private enum Fault
    {
        None,
        ThrowWrite,
        WriteThenThrow,
        PartialThenThrow,
        ThrowReplace,
    }

    // Fault-injecting file store over a real store: every step delegates
    // except the armed one, so each BP-SAVE-02 kill point is reproducible.
    private sealed class FaultStore : ISaveFileStore
    {
        public Fault Mode = Fault.None;
        private readonly SaveFileStore real = new SaveFileStore();

        public bool Exists(string path)
        {
            return real.Exists(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            return real.ReadAllBytes(path);
        }

        public void EnsureDirectory(string directory)
        {
            real.EnsureDirectory(directory);
        }

        public void WriteTemp(string tempPath, byte[] bytes)
        {
            if (Mode == Fault.ThrowWrite)
            {
                throw new IOException("injected tmp failure");
            }
            if (Mode == Fault.PartialThenThrow)
            {
                byte[] torn = new byte[bytes.Length / 2];
                Array.Copy(bytes, torn, torn.Length);
                real.WriteTemp(tempPath, torn);
                throw new IOException("injected kill mid-tmp-write");
            }
            real.WriteTemp(tempPath, bytes);
            if (Mode == Fault.WriteThenThrow)
            {
                throw new IOException("injected kill after tmp write");
            }
        }

        public void Replace(string tempPath, string destPath, string backupPath)
        {
            if (Mode == Fault.ThrowReplace)
            {
                throw new IOException("injected kill after validate");
            }
            real.Replace(tempPath, destPath, backupPath);
        }
    }

    private static bool NotesContain(string[] notes, string fragment)
    {
        for (int i = 0; i < notes.Length; i++)
        {
            if (notes[i].Contains(fragment))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountCommitted(WorldState world, string effectKind)
    {
        int count = 0;
        IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
        for (int i = 0; i < ledger.Count; i++)
        {
            if (ledger[i].State == LedgerState.Committed
                && ledger[i].EffectKind.Equals(effectKind))
            {
                count++;
            }
        }
        return count;
    }

    private static LedgerState? LedgerStateOf(WorldState world, ActionInstanceId action)
    {
        IReadOnlyList<EffectLedgerEntry> ledger = world.EffectLedger;
        for (int i = 0; i < ledger.Count; i++)
        {
            if (ledger[i].Action == action)
            {
                return ledger[i].State;
            }
        }
        return null;
    }

    private static bool ResidentFieldsEqual(ResidentRecord a, ResidentRecord b)
    {
        if (a.Id != b.Id || !a.DisplayName.Equals(b.DisplayName) || a.Stage != b.Stage
            || a.StageProgressDays != b.StageProgressDays || a.Household != b.Household
            || a.Home != b.Home || a.Money != b.Money || !a.JobId.Equals(b.JobId)
            || a.CareerLevel != b.CareerLevel || a.CareerPoints != b.CareerPoints
            || a.EducationPoints != b.EducationPoints || a.IsAlive != b.IsAlive
            || a.StarvationHours != b.StarvationHours || a.Ill != b.Ill
            || a.Fatigued != b.Fatigued || a.Injured != b.Injured
            || a.LastChanceDrawDay != b.LastChanceDrawDay)
        {
            return false;
        }
        for (int i = 0; i < StateSchema.NeedCount; i++)
        {
            if (a.GetNeed((Need)i) != b.GetNeed((Need)i))
            {
                return false;
            }
        }
        for (int i = 0; i < StateSchema.TraitCount; i++)
        {
            if (a.GetTrait((TraitAxis)i) != b.GetTrait((TraitAxis)i))
            {
                return false;
            }
        }
        if (a.Skills.Count != b.Skills.Count || a.Bonds.Count != b.Bonds.Count
            || a.Family.Count != b.Family.Count || a.Feelings.Count != b.Feelings.Count
            || a.Memories.Count != b.Memories.Count
            || a.CompletedCourses.Count != b.CompletedCourses.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Skills.Count; i++)
        {
            if (!a.Skills[i].SkillId.Equals(b.Skills[i].SkillId)
                || a.Skills[i].Level != b.Skills[i].Level
                || a.Skills[i].Points != b.Skills[i].Points)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Bonds.Count; i++)
        {
            if (a.Bonds[i].Other != b.Bonds[i].Other
                || a.Bonds[i].Friendship != b.Bonds[i].Friendship
                || a.Bonds[i].Romance != b.Bonds[i].Romance)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Family.Count; i++)
        {
            if (a.Family[i].Other != b.Family[i].Other
                || !a.Family[i].Kind.Equals(b.Family[i].Kind))
            {
                return false;
            }
        }
        for (int i = 0; i < a.Feelings.Count; i++)
        {
            if (a.Feelings[i].Kind != b.Feelings[i].Kind
                || a.Feelings[i].SourceEvent != b.Feelings[i].SourceEvent
                || a.Feelings[i].Other != b.Feelings[i].Other
                || a.Feelings[i].Intensity != b.Feelings[i].Intensity
                || a.Feelings[i].ExpiryGameSeconds != b.Feelings[i].ExpiryGameSeconds)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Memories.Count; i++)
        {
            MemoryEntry x = a.Memories[i];
            MemoryEntry y = b.Memories[i];
            if (x.Event != y.Event || !x.Kind.Equals(y.Kind)
                || x.GameSeconds != y.GameSeconds || x.Place != y.Place
                || x.Participants.Count != y.Participants.Count
                || !x.OutcomeCode.Equals(y.OutcomeCode)
                || !x.LearnedBy.Equals(y.LearnedBy))
            {
                return false;
            }
            for (int p = 0; p < x.Participants.Count; p++)
            {
                if (x.Participants[p] != y.Participants[p])
                {
                    return false;
                }
            }
        }
        for (int i = 0; i < a.CompletedCourses.Count; i++)
        {
            if (!a.CompletedCourses[i].Equals(b.CompletedCourses[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SnapshotsEqual(WorldSnapshot a, WorldSnapshot b)
    {
        if (a.WorldId != b.WorldId || a.Seed != b.Seed || a.RngState != b.RngState
            || a.GameSeconds != b.GameSeconds || a.Aging != b.Aging
            || a.TownFund != b.TownFund
            || a.NextDecisionSequence != b.NextDecisionSequence
            || a.NextResidentId != b.NextResidentId
            || a.NextHouseholdId != b.NextHouseholdId
            || a.NextHomeId != b.NextHomeId || a.NextObjectId != b.NextObjectId
            || a.NextEventId != b.NextEventId || a.NextGatewayId != b.NextGatewayId
            || a.Residents.Count != b.Residents.Count
            || a.Households.Count != b.Households.Count
            || a.Homes.Count != b.Homes.Count || a.Events.Count != b.Events.Count
            || a.EstateLedger.Count != b.EstateLedger.Count
            || a.CareAssignments.Count != b.CareAssignments.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Residents.Count; i++)
        {
            ResidentRecord x = null;
            ResidentRecord y = null;
            for (int j = 0; j < b.Residents.Count; j++)
            {
                if (b.Residents[j].Id == a.Residents[i].Id)
                {
                    y = b.Residents[j];
                }
            }
            x = a.Residents[i];
            if (y == null || !ResidentFieldsEqual(x, y))
            {
                return false;
            }
        }
        for (int i = 0; i < a.Households.Count; i++)
        {
            bool found = false;
            for (int j = 0; j < b.Households.Count; j++)
            {
                if (b.Households[j].Id == a.Households[i].Id
                    && b.Households[j].Name.Equals(a.Households[i].Name)
                    && b.Households[j].Home == a.Households[i].Home)
                {
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Homes.Count; i++)
        {
            bool found = false;
            for (int j = 0; j < b.Homes.Count; j++)
            {
                if (b.Homes[j].Id == a.Homes[i].Id
                    && b.Homes[j].BedCount == a.Homes[i].BedCount)
                {
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Events.Count; i++)
        {
            WorldEvent x = a.Events[i];
            WorldEvent y = b.Events[i];
            if (x.Event != y.Event || x.GameSeconds != y.GameSeconds
                || !x.Kind.Equals(y.Kind) || x.Actor != y.Actor
                || !x.Detail.Equals(y.Detail))
            {
                return false;
            }
        }
        // Restore re-reserves resumed actions under their fresh ids, so the
        // loaded ledger is the saved prefix plus one Reserved re-reserve per
        // resumed action; the extras are pinned by a dedicated check.
        if (b.EffectLedger.Count < a.EffectLedger.Count)
        {
            return false;
        }
        for (int i = 0; i < a.EffectLedger.Count; i++)
        {
            EffectLedgerEntry x = a.EffectLedger[i];
            EffectLedgerEntry y = b.EffectLedger[i];
            if (x.Action != y.Action || !x.EffectKind.Equals(y.EffectKind)
                || x.Recipient != y.Recipient || x.State != y.State)
            {
                return false;
            }
        }
        for (int i = 0; i < a.EstateLedger.Count; i++)
        {
            EstateLedgerEntry x = a.EstateLedger[i];
            EstateLedgerEntry y = b.EstateLedger[i];
            if (x.DeathEvent != y.DeathEvent || !x.AssetRef.Equals(y.AssetRef)
                || !x.Recipient.Equals(y.Recipient) || x.Settled != y.Settled)
            {
                return false;
            }
        }
        for (int i = 0; i < a.CareAssignments.Count; i++)
        {
            CareAssignment x = a.CareAssignments[i];
            CareAssignment y = b.CareAssignments[i];
            if (x.Dependent != y.Dependent || x.Guardian != y.Guardian
                || x.Placement.Household != y.Placement.Household
                || x.Placement.FacilityPlace != y.Placement.FacilityPlace
                || x.TransferEvent != y.TransferEvent)
            {
                return false;
            }
        }
        return true;
    }

    private static bool AppearancesEqual(AppearanceStore a, AppearanceStore b)
    {
        List<AppearanceEntry> x = a.Export();
        List<AppearanceEntry> y = b.Export();
        if (x.Count != y.Count)
        {
            return false;
        }
        for (int i = 0; i < x.Count; i++)
        {
            AppearanceEntry found = null;
            for (int j = 0; j < y.Count; j++)
            {
                if (y[j].ResidentId == x[i].ResidentId)
                {
                    found = y[j];
                }
            }
            if (found == null || found.SkinTone != x[i].SkinTone
                || found.Face != x[i].Face || found.EyeColor != x[i].EyeColor
                || found.HairType != x[i].HairType || found.HairStyle != x[i].HairStyle
                || found.HairColor != x[i].HairColor || found.Accessory != x[i].Accessory
                || found.OutfitEveryday != x[i].OutfitEveryday
                || found.OutfitSleep != x[i].OutfitSleep
                || found.OutfitExercise != x[i].OutfitExercise
                || found.OverrideCategory != x[i].OverrideCategory)
            {
                return false;
            }
        }
        return true;
    }

    private static bool QueueEntryMatches(ActionEntry entry, long command, CommandKind kind,
        string activity, long? target, ActionOwner owner, CommandSource? source)
    {
        if (entry == null || (long)entry.Command != command || entry.Kind != kind
            || !entry.Activity.Code.Equals(activity) || entry.TargetId != target
            || entry.Owner != owner || entry.Source != source)
        {
            return false;
        }
        return true;
    }

    private static CommandEnvelope CookCommand(WorldState world, CommandArbitrator arbitrator,
        ResidentId resident, long? target)
    {
        return new CommandEnvelope(NextCommand(), world.Epoch, world.GameSeconds,
            CommandSource.PlayerHuman, arbitrator.CurrentStamp(resident).Sequence + 1u,
            CommandKind.DirectActivity, resident, new ActivityId("ACT-FOOD-01"), target,
            new CommandParameters.Direct(QueueMode.Immediate));
    }

    private static void FailedResumeFoodReservation(string root)
    {
        Stack stack = NewStack(71L, 42L, new string[8]);
        ResidentRecord resident = SortedResidents(stack.World)[0];
        Accounts accounts = new Accounts(stack.World);
        Food food = new Food(stack.World, stack.Arbitrator, stack.Executor, accounts);
        food.SetHomeStock(Core, resident.Home, 1, 0);
        ObjectId stove;
        do { stove = stack.World.AllocateObject(Core); } while ((long)stove <= 8L);
        ApproachPose[] poses = { new ApproachPose(0f, 0f, 0f) };
        stack.Executor.RegisterObject(stove, poses);
        stack.Executor.SetResidentPosition(resident.Id, poses[0]);
        if (!food.Submit(CookCommand(stack.World, stack.Arbitrator, resident.Id,
            (long)stove)).Accepted) throw new Exception("Cook fixture refused");
        stack.Clock.AdvanceRealSeconds(Core, 0.1);
        food.Tick(Exec);
        Food.FoodSnapshot before = food.Export();
        Check(before.Tracked.Count == 1 && before.Tracked[0].ReservedGroceries == 1,
            "ocr-B-fixture-cook-reserved");
        string directory = Path.Combine(root, "failed-resume");
        if (!SaveV1.SaveGame(new GameStack(stack.World, stack.Clock, stack.Arbitrator,
            stack.Executor, stack.Appearances, accounts, food), directory).Ok)
            throw new Exception("Cook fixture save failed");
        FixedScene unreachable = new FixedScene();
        unreachable.Objs.Add(new SceneObj { Id = stove, Poses = poses, Reachable = false });
        SaveLoadResult loaded = SaveV1.LoadGame(directory, unreachable);
        if (!loaded.Ok) throw new Exception(loaded.Notice);
        LoadedGame game = loaded.Game;
        Food.FoodSnapshot restored = game.Food.Export();
        ExecutionRecord failed = game.Executor.GetExecution(
            (ActionInstanceId)restored.Tracked[0].Action);
        Check(failed != null && failed.IsTerminal && failed.Outcome == TerminalOutcome.Failed
            && NotesContain(game.Notes, "NoPath"), "ocr-B-fixture-mapped-resume-failed");
        // Retry before the next tick: load reconciliation must make stock usable immediately.
        CommandResult retry = game.Food.Submit(CookCommand(game.World, game.Arbitrator,
            resident.Id, null));
        Console.WriteLine("OCR-B restoredReserved=" + restored.Tracked[0].ReservedGroceries
            + " retryAccepted=" + retry.Accepted + " reason=" + retry.Reason);
        Check(retry.Accepted, "ocr-B-failed-resume-releases-grocery-for-immediate-retry");
        for (int i = 0; i < 500; i++)
        {
            game.Clock.AdvanceRealSeconds(Core, 0.1);
            game.Food.Tick(Exec);
        }
        Console.WriteLine("OCR-B groceries=" + game.Food.GroceryUnits(resident.Home)
            + " meals=" + game.Food.PreparedMeals(resident.Home));
        Check(game.Food.GroceryUnits(resident.Home) == 0
            && game.Food.PreparedMeals(resident.Home) == 2,
            "ocr-B-failed-resume-retry-completes-once");
    }

    private static void SavedServiceActionHighWaterMark(string root)
    {
        Stack stack = NewStack(72L, 42L, new string[8]);
        ResidentRecord resident = SortedResidents(stack.World)[0];
        stack.World.SetMoney(Core, resident.Id, 50L);
        Accounts accounts = new Accounts(stack.World);
        Food food = new Food(stack.World, stack.Arbitrator, stack.Executor, accounts);
        food.SetHomeStock(Core, resident.Home, 1, 0);
        if (!food.Submit(CookCommand(stack.World, stack.Arbitrator, resident.Id, null)).Accepted)
            throw new Exception("Service fixture cook refused");
        for (int i = 0; i < 500; i++)
        {
            stack.Clock.AdvanceRealSeconds(Core, 0.1);
            food.Tick(Exec);
        }
        Check(food.Export().Tracked[0].Done && food.PreparedMeals(resident.Home) == 2,
            "ocr-C-fixture-completed-food");
        ActionInstanceId payment = stack.World.AllocateAction(Core);
        if (!accounts.Transfer(Core, payment, AccountRef.Personal(resident.Id),
            AccountRef.Expense("test"), 1L).Accepted) throw new Exception("Service fixture payment refused");
        string directory = Path.Combine(root, "service-id");
        if (!SaveV1.SaveGame(new GameStack(stack.World, stack.Clock, stack.Arbitrator,
            stack.Executor, stack.Appearances, accounts, food), directory).Ok)
            throw new Exception("Service fixture save failed");
        string path = Path.Combine(directory, SaveFormat.FileName);
        byte[] original = File.ReadAllBytes(path);
        SaveLoadResult valid = SaveV1.LoadGame(directory, new EmptyScene());
        Check(valid.Ok && valid.Game.Food.PreparedMeals(resident.Home) == 2
            && valid.Game.Accounts.Ledger.Count == 1, "ocr-C-valid-service-history-loads");
        if (!valid.Ok) throw new Exception(valid.Notice);
        CommandResult validPayment = valid.Game.Accounts.Transfer(Core,
            valid.Game.World.AllocateAction(Core), AccountRef.Personal(resident.Id),
            AccountRef.Expense("new-payment"), 1L);
        Check(validPayment.Accepted && valid.Game.Accounts.Balance(AccountRef.Personal(resident.Id)) == 48L
            && valid.Game.Accounts.Ledger.Count == 2, "ocr-C-valid-fresh-payment-debits-once");
        foreach (string service in new[] { "accounts", "food" })
        {
            JsonObject document = SaveV1.ParseDocument(original);
            JsonObject payload = document.GetObject("payload", "root");
            long next = payload.GetObject("world", "payload").GetLong("nextActionId", "world");
            JsonObject section = payload.GetObject(service, "payload");
            if (service == "accounts")
            {
                section.GetArray("ledger", service).GetObject(0, service)
                    .Set("action", new JsonNumber(next.ToString()));
                JsonArray applied = new JsonArray();
                applied.Add(new JsonNumber(next.ToString()));
                section.Set("applied", applied);
            }
            else
            {
                section.GetArray("tracked", service).GetObject(0, service)
                    .Set("action", new JsonNumber(next.ToString()));
            }
            File.WriteAllBytes(path, SaveV1.AssembleDocument(payload, SaveFormat.SchemaVersion,
                stack.World.WorldId, (uint)stack.World.Epoch, stack.World.GameSeconds));
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Console.WriteLine("OCR-C " + service + " savedAction=" + next + " loaded=" + loaded.Ok);
            if (loaded.Ok)
            {
                ActionInstanceId fresh = loaded.Game.World.AllocateAction(Core);
                Console.WriteLine("OCR-C " + service + " freshAction=" + (long)fresh
                    + " collides=" + ((long)fresh == next));
                if (service == "accounts")
                {
                    long balance = loaded.Game.Accounts.Balance(AccountRef.Personal(resident.Id));
                    CommandResult transfer = loaded.Game.Accounts.Transfer(Core, fresh,
                        AccountRef.Personal(resident.Id), AccountRef.Expense("new-payment"), 1L);
                    Console.WriteLine("OCR-C freshPaymentAccepted=" + transfer.Accepted
                        + " debited=" + (balance - loaded.Game.Accounts.Balance(AccountRef.Personal(resident.Id)))
                        + " ledgerCount=" + loaded.Game.Accounts.Ledger.Count);
                }
            }
            Check(!loaded.Ok, "ocr-C-" + service + "-action-at-next-id-refused");
        }
    }

    public static int Main()
    {
        try
        {
            return Run();
        }
        finally
        {
            CleanupOwnedDirs();
        }
    }

    private static int Run()
    {
        string[] names = new string[8];
        names[0] = "Zed";
        Stack s = NewStack(7L, 42L, names);
        ResidentRecord[] r = SortedResidents(s.World);
        Check(r.Length == 8, "fixture-eight-residents");

        // Non-player queue entries first: later human submits disable
        // automation, which would reject the automated proposal. The
        // proposal evicts the first autonomous entry, so a second one is
        // queued after to cover both non-player owner classes.
        Check(s.Arbitrator.EnqueueAutonomous(r[3].Id, new ActivityId("ACT-EDU-02"), null),
            "fixture-autonomous-queued");
        ActionContextSnapshot offer = s.Arbitrator.Snapshot(r[3].Id);
        DecisionProposal proposal = new DecisionProposal(
            (GatewayRequestId)99L, offer.Available[0].Activity,
            offer.Available[0].TargetId, s.Arbitrator.CurrentStamp(r[3].Id));
        List<AvailableAction> offered = new List<AvailableAction>();
        for (int i = 0; i < offer.Available.Count; i++)
        {
            offered.Add(offer.Available[i]);
        }
        Check(s.Arbitrator.ApplyProposal(r[3].Id, proposal, offered.AsReadOnly()).Accepted,
            "fixture-proposal-accepted");
        Check(s.Arbitrator.EnqueueAutonomous(r[3].Id, new ActivityId("ACT-EDU-03"), null),
            "fixture-autonomous-second");
        Check(s.Arbitrator.GetPending(r[3].Id).Length == 2, "fixture-r3-two-nonplayer");

        // Diverse state across every group the payload carries.
        s.World.SetNeed(Core, r[0].Id, Need.Hunger, 33.33f);
        s.World.SetNeed(Core, r[0].Id, Need.Energy, 99.5f);
        s.World.SetTrait(Core, r[0].Id, TraitAxis.Ambitious, 42);
        s.World.SetMoney(Core, r[0].Id, 12345L);
        s.World.SetCareer(Core, r[0].Id, "job-1", 3, 250);
        s.World.SetEducation(Core, r[0].Id, 77);
        s.World.AddCompletedCourse(Core, r[0].Id, "course-a");
        s.World.AddSkillPoints(Core, r[0].Id, "skill-cooking", 150);
        s.World.SetBond(Core, r[0].Id, r[1].Id, 12.5f, -3.25f);
        s.World.AddFamilyLink(Core, r[0].Id, r[1].Id, "sibling");
        WorldEvent ev = s.World.AppendEvent(Core, "test-kind", r[0].Id, "detail-a");
        s.World.AppendEvent(Core, "world-kind", null, "detail-b");
        s.World.AddFeeling(Core, r[0].Id, FeelingKind.Grateful, ev.Event, r[1].Id, 2, 99999L);
        List<ResidentId> parts = new List<ResidentId>();
        parts.Add(r[0].Id);
        parts.Add(r[1].Id);
        s.World.AddMemory(Core, r[0].Id, new MemoryEntry(ev.Event, "kind",
            s.World.GameSeconds, r[0].Home, parts.AsReadOnly(), "won", "saw"));
        s.World.SetHealthFlags(Core, r[0].Id, 2.5f, true, false, false, 4);
        s.World.SetTownFund(Core, 98765L);
        s.World.SetMoney(Core, r[1].Id, 500L);
        List<ActivityId> candidates = new List<ActivityId>();
        candidates.Add(new ActivityId("ACT-EDU-02"));
        s.World.AppendDecision(Core, "trigger", r[0].Id, candidates.AsReadOnly(),
            new ActivityId("ACT-EDU-02"), "test", null, "ok", "none");
        s.World.SettleEstate(Core, ev.Event, "asset:home:1", "household:1");
        s.World.AssignCare(Core, new CareAssignment(r[7].Id, r[0].Id,
            new CarePlacement(r[0].Household, -1), ev.Event));
        s.Appearances.SetPreset(Core, r[0].Id, AppearanceDimension.SkinTone, 3);
        s.Appearances.SetOutfit(Core, r[0].Id, OutfitCategory.Sleep, 100);
        s.Appearances.SetOverride(Core, r[0].Id, OutfitCategory.Exercise);
        s.Appearances.SetPreset(Core, r[1].Id, AppearanceDimension.EyeColor, 3);
        s.Appearances.SetPreset(Core, r[1].Id, AppearanceDimension.HairType, 3);
        s.Appearances.SetPreset(Core, r[1].Id, AppearanceDimension.HairStyle,
            ResidentFactory.ValidHairStyles(3)[1]);
        s.Appearances.SetPreset(Core, r[1].Id, AppearanceDimension.HairColor, 5);
        s.Clock.SetSpeed(SimulationSpeed.Fast);
        s.Clock.SetPreset(AgingPreset.Slow);

        // The slot object sits past the last resident id (guidance: object
        // ids must not collide with live resident ids they target socially).
        for (int i = 0; i < 8; i++)
        {
            s.World.AllocateObject(Core);
        }
        ObjectId stove = s.World.AllocateObject(Core);
        ApproachPose[] stovePoses = new ApproachPose[1];
        stovePoses[0] = new ApproachPose(0.0f, 0.0f, 0.0f);
        s.Executor.RegisterObject(stove, stovePoses);
        s.Executor.SetResidentPosition(r[7].Id, new ApproachPose(5.0f, 0.0f, 0.0f));

        // One role per resident: full queue plus will, slot holder, slot
        // waiter, non-player entries, late deceased submit, released
        // completion with pending, idle, and an in-flight travel.
        Check(Direct(s, r[0], "ACT-EDU-02", QueueMode.Immediate, null).Accepted,
            "fixture-r0-current");
        Check(Direct(s, r[0], "ACT-EDU-03", QueueMode.Append, null).Accepted,
            "fixture-r0-p1");
        Check(Direct(s, r[0], "ACT-EDU-04", QueueMode.Append, null).Accepted,
            "fixture-r0-p2");
        ResidentRecord r0now;
        s.World.TryGetResident(r[0].Id, out r0now);
        List<MoneyShare> shares = new List<MoneyShare>();
        shares.Add(new MoneyShare("household:1", 60));
        shares.Add(new MoneyShare("charity", 40));
        List<ObjectId> heirlooms = new List<ObjectId>();
        heirlooms.Add(stove);
        List<HouseholdId> careHomes = new List<HouseholdId>();
        careHomes.Add(r[0].Household);
        Check(s.Arbitrator.Submit(new CommandEnvelope(NextCommand(), s.World.Epoch,
            s.World.GameSeconds, CommandSource.PlayerHuman, r0now.ResidentSequence + 1u,
            CommandKind.WillEdit, r[0].Id, null, null, new CommandParameters.Will(
                new WillRecord(shares.AsReadOnly(), 50, heirlooms.AsReadOnly(),
                    "note", "brief", careHomes.AsReadOnly())))).Accepted,
            "fixture-r0-will");
        s.World.TryGetResident(r[0].Id, out r0now);
        Check(s.Arbitrator.Submit(new CommandEnvelope(NextCommand(), s.World.Epoch,
            s.World.GameSeconds, CommandSource.PlayerHuman, r0now.ResidentSequence + 1u,
            CommandKind.CaregiverAssign, r[0].Id, null, null,
            new CommandParameters.Caregiver(r[1].Id))).Accepted,
            "fixture-r0-caregiver");
        Check(Direct(s, r[1], "ACT-FOOD-02", QueueMode.Immediate, (long)stove).Accepted,
            "fixture-r1-holder");
        Check(Direct(s, r[2], "ACT-FOOD-02", QueueMode.Immediate, (long)stove).Accepted,
            "fixture-r2-waiter");
        Check(Direct(s, r[5], "ACT-HOME-04", QueueMode.Immediate, null).Accepted,
            "fixture-r5-short");
        Check(Travel(s, r[7], (long)stove).Accepted, "fixture-r7-travel");

        // Release contract: the short action completes during the steps and
        // CompleteCurrent releases it, so r5 is idle (null current) while the
        // later Append waits for the next tick.
        ActionInstanceId r5shortAction = s.Arbitrator.GetCurrent(r[5].Id).Action;

        Step(s, 12);
        Check(Direct(s, r[5], "ACT-EDU-02", QueueMode.Append, null).Accepted,
            "fixture-r5-pending");
        // Deceased after the last tick: a current with no execution, whose
        // replay must reject ResidentDeceased instead of resurrecting work.
        Check(Direct(s, r[4], "ACT-EDU-02", QueueMode.Immediate, null).Accepted,
            "fixture-r4-submit");
        s.World.SetAlive(Core, r[4].Id, false);

        ActionEntry r0current = s.Arbitrator.GetCurrent(r[0].Id);
        ActionEntry r1current = s.Arbitrator.GetCurrent(r[1].Id);
        ActionEntry r2current = s.Arbitrator.GetCurrent(r[2].Id);
        ActionEntry r5current = s.Arbitrator.GetCurrent(r[5].Id);
        ActionEntry r7current = s.Arbitrator.GetCurrent(r[7].Id);
        ExecutionRecord r0exec = s.Executor.GetExecution(r0current.Action);
        ExecutionRecord r1exec = s.Executor.GetExecution(r1current.Action);
        ExecutionRecord r2exec = s.Executor.GetExecution(r2current.Action);
        ActionEntry[] r5pendingNow = s.Arbitrator.GetPending(r[5].Id);
        ExecutionRecord r7exec = s.Executor.GetExecution(r7current.Action);
        Check(r0exec != null && r0exec.Stage == ExecutionStage.Perform
            && !r0exec.IsTerminal, "fixture-r0-mid-perform");
        Check(r1exec != null && r1exec.Stage == ExecutionStage.Perform
            && r1exec.Holds.Length == 1, "fixture-r1-holds");
        Check(r2exec != null && r2exec.Queued && r2exec.QueuePosition == 1,
            "fixture-r2-waiting");
        Check(r5current == null, "fixture-r5-released-on-complete");
        Check(r5pendingNow.Length == 1
            && QueueEntryMatches(r5pendingNow[0], (long)r5pendingNow[0].Command,
                CommandKind.DirectActivity, "ACT-EDU-02", null, ActionOwner.Player,
                CommandSource.PlayerHuman), "fixture-r5-pending-waits");
        ExecutionRecord r5shortExec = s.Executor.GetExecution(r5shortAction);
        Check(r5shortExec != null && r5shortExec.IsTerminal
            && r5shortExec.Outcome.Value == TerminalOutcome.Completed,
            "fixture-r5-completed-once");
        Check(r7exec != null && r7exec.Stage == ExecutionStage.Travel
            && !r7exec.IsTerminal, "fixture-r7-mid-travel");
        long r0performStart = r0exec.PerformStartGameSeconds;
        long r1performStart = r1exec.PerformStartGameSeconds;
        long r2enqueue = r2exec.EnqueueGameSeconds;
        ActionInstanceId r0actionId = r0current.Action;
        WorldSnapshot pre = s.World.ExportSnapshot();

        string dirA = UniqueDir("a");
        SaveWriteResult saved = SaveV1.SaveGame(s.SaveStack(), dirA);
        Check(saved.Ok, "save-ok");
        string savePath = Path.Combine(dirA, SaveFormat.FileName);
        byte[] saveBytes = File.ReadAllBytes(savePath);
        Console.WriteLine("SAVE bytes: " + saveBytes.Length.ToString());
        Check(saveBytes.Length < 5 * 1024 * 1024, "save-under-size-budget");

        string dirA2 = UniqueDir("a2");
        Check(SaveV1.SaveGame(s.SaveStack(), dirA2).Ok, "save-again-ok");
        byte[] saveBytes2 = File.ReadAllBytes(Path.Combine(dirA2, SaveFormat.FileName));
        bool identical = saveBytes.Length == saveBytes2.Length;
        for (int i = 0; identical && i < saveBytes.Length; i++)
        {
            identical = saveBytes[i] == saveBytes2[i];
        }
        Check(identical, "save-deterministic-bytes");

        JsonObject document = SaveV1.ParseDocument(saveBytes);
        JsonObject headerJson = document.GetObject("header", "check");
        JsonObject payloadJson = document.GetObject("payload", "check");
        Check(headerJson.GetInt("schemaVersion", "check") == SaveFormat.SchemaVersion,
            "header-schema-version");
        Check(headerJson.GetLong("worldId", "check") == pre.WorldId, "header-world-id");
        Check(headerJson.GetUInt("worldEpoch", "check") == pre.Epoch, "header-epoch");
        Check(headerJson.GetLong("savedAtGameSeconds", "check") == pre.GameSeconds,
            "header-saved-at");
        string storedHash = headerJson.GetString("sha256", "check");
        string recomputed = SaveV1.Sha256Hex(Encoding.UTF8.GetBytes(
            SaveV1.RenderJson(payloadJson)));
        Check(storedHash.Length == 64 && storedHash.Equals(recomputed), "header-sha256");
        Check(payloadJson.Has("world") && payloadJson.Has("clock")
            && payloadJson.Has("residents") && payloadJson.Has("households")
            && payloadJson.Has("homes") && payloadJson.Has("events")
            && payloadJson.Has("effectLedger") && payloadJson.Has("estateLedger")
            && payloadJson.Has("care") && payloadJson.Has("objects")
            && payloadJson.Has("actions") && payloadJson.Has("automation")
            && payloadJson.Has("appearances"), "payload-sections");
        JsonArray appearanceRows = payloadJson.GetArray("appearances", "check");
        bool noBodyChoices = appearanceRows.Count == 8;
        for (int i = 0; i < appearanceRows.Count; i++)
        {
            JsonObject row = appearanceRows.GetObject(i, "check");
            noBodyChoices &= !row.Has("build") && !row.Has("height");
        }
        Check(noBodyChoices, "appearance-body-build-height-absent");

        FixedScene scene = new FixedScene();
        SceneObj stoveScene = new SceneObj();
        stoveScene.Id = stove;
        stoveScene.Poses = stovePoses;
        scene.Objs.Add(stoveScene);
        SaveLoadResult loaded = SaveV1.LoadGame(dirA, scene);
        Check(loaded.Ok, "load-ok");
        LoadedGame g = loaded.Game;
        Check((uint)g.Header.Epoch == pre.Epoch, "loaded-header-epoch");
        Check(g.Header.SavedAtGameSeconds == pre.GameSeconds, "loaded-header-saved-at");
        Check((uint)g.World.Epoch == pre.Epoch + 1u, "loaded-epoch-plus-one");
        Check(SnapshotsEqual(pre, g.World.ExportSnapshot()), "roundtrip-world");
        Check(g.World.RngState == pre.RngState, "roundtrip-rng-state");
        Check(g.World.GameSeconds == pre.GameSeconds, "roundtrip-no-catchup");
        Check(g.World.NextActionId == pre.NextActionId + 7L, "roundtrip-action-marks");
        Check(g.World.NextCommandId == pre.NextCommandId + 2L, "roundtrip-command-marks");
        Check(g.Clock.Speed == SimulationSpeed.Fast
            && !g.Clock.Paused && g.Clock.Preset == AgingPreset.Slow
            && g.Clock.TotalTicks == s.Clock.TotalTicks, "roundtrip-clock");
        Check(AppearancesEqual(s.Appearances, g.Appearances), "roundtrip-appearances");
        Check(!g.Arbitrator.AutomationEnabled, "roundtrip-automation-false");
        // No "dropped terminal current": natural completion releases the
        // current synchronously, so a terminal current never reaches a save.
        Check(NotesContain(g.Notes, "discarded 2 non-player")
            && NotesContain(g.Notes, "ResidentDeceased")
            && NotesContain(g.Notes, "re-arrived on resume"), "roundtrip-notes");

        ResidentRecord[] lr = SortedResidents(g.World);
        long[] expectedSeq = new long[8];
        expectedSeq[0] = 5;
        expectedSeq[1] = 1;
        expectedSeq[2] = 1;
        expectedSeq[3] = 0;
        expectedSeq[4] = 0;
        expectedSeq[5] = 1;
        expectedSeq[6] = 0;
        expectedSeq[7] = 1;
        bool seqOk = true;
        bool idleExact = lr[6].ResidentSequence == r[6].ResidentSequence
            && lr[6].DecisionSlotGeneration == r[6].DecisionSlotGeneration
            && lr[6].ControlGeneration == r[6].ControlGeneration;
        for (int i = 0; i < 8; i++)
        {
            ResidentRecord before;
            s.World.TryGetResident(lr[i].Id, out before);
            if ((long)lr[i].ResidentSequence
                != (long)before.ResidentSequence + expectedSeq[i])
            {
                seqOk = false;
            }
        }
        Check(seqOk, "roundtrip-sequences");
        Check(idleExact, "roundtrip-idle-exact");

        ActionEntry g0 = g.Arbitrator.GetCurrent(lr[0].Id);
        ActionEntry[] g0pending = g.Arbitrator.GetPending(lr[0].Id);
        ActionEntry[] r0pending = s.Arbitrator.GetPending(r[0].Id);
        Check(QueueEntryMatches(g0, (long)r0current.Command, CommandKind.DirectActivity,
                "ACT-EDU-02", null, ActionOwner.Player, CommandSource.PlayerHuman)
            && g0pending.Length == 2
            && QueueEntryMatches(g0pending[0], (long)r0pending[0].Command,
                CommandKind.DirectActivity, "ACT-EDU-03", null, ActionOwner.Player,
                CommandSource.PlayerHuman)
            && QueueEntryMatches(g0pending[1], (long)r0pending[1].Command,
                CommandKind.DirectActivity, "ACT-EDU-04", null, ActionOwner.Player,
                CommandSource.PlayerHuman)
            && (long)g0.Action >= pre.NextActionId, "roundtrip-r0-queue");
        ActionEntry g1 = g.Arbitrator.GetCurrent(lr[1].Id);
        ActionEntry g2 = g.Arbitrator.GetCurrent(lr[2].Id);
        ActionEntry g7 = g.Arbitrator.GetCurrent(lr[7].Id);
        Check(QueueEntryMatches(g1, (long)r1current.Command, CommandKind.DirectActivity,
                "ACT-FOOD-02", (long)stove, ActionOwner.Player, CommandSource.PlayerHuman)
            && QueueEntryMatches(g2, (long)r2current.Command, CommandKind.DirectActivity,
                "ACT-FOOD-02", (long)stove, ActionOwner.Player, CommandSource.PlayerHuman)
            && QueueEntryMatches(g7, (long)r7current.Command, CommandKind.TravelTo,
                string.Empty, (long)stove, ActionOwner.Player, CommandSource.PlayerHuman),
            "roundtrip-r1-r2-r7-queue");
        Check(g.Arbitrator.GetCurrent(lr[3].Id) == null
            && g.Arbitrator.GetPending(lr[3].Id).Length == 0, "roundtrip-r3-discarded");
        Check(g.Arbitrator.GetCurrent(lr[4].Id) == null
            && g.Arbitrator.GetPending(lr[4].Id).Length == 0, "roundtrip-r4-dropped");
        ActionEntry g5current = g.Arbitrator.GetCurrent(lr[5].Id);
        ActionEntry[] g5pending = g.Arbitrator.GetPending(lr[5].Id);
        ActionEntry[] r5pending = s.Arbitrator.GetPending(r[5].Id);
        // The resume tick promotes the restored Append through the normal
        // path, so r5 reloads already running it instead of holding pending.
        Check(g5current != null
            && QueueEntryMatches(g5current, (long)r5pending[0].Command,
                CommandKind.DirectActivity, "ACT-EDU-02", null, ActionOwner.Player,
                CommandSource.PlayerHuman)
            && g5pending.Length == 0, "roundtrip-r5-pending");
        ActionInstanceId g5action = g5current == null ? r0actionId : g5current.Action;
        Check(g.Arbitrator.GetCurrent(lr[6].Id) == null
            && g.Arbitrator.GetPending(lr[6].Id).Length == 0, "roundtrip-r6-idle");

        ExecutionRecord g0exec = g.Executor.GetExecution(g0.Action);
        ExecutionRecord g1exec = g.Executor.GetExecution(g1.Action);
        ExecutionRecord g2exec = g.Executor.GetExecution(g2.Action);
        ExecutionRecord g7exec = g.Executor.GetExecution(g7.Action);
        Check(g0exec != null && g0exec.Stage == ExecutionStage.Perform
            && g0exec.PerformStartGameSeconds == r0performStart
            && g0exec.LedgerReserved, "resume-r0-elapsed");
        Check(g1exec != null && g1exec.Stage == ExecutionStage.Perform
            && g1exec.PerformStartGameSeconds == r1performStart
            && g1exec.Holds.Length == 1 && g1exec.Holds[0].Object == stove
            && g1exec.Holds[0].SlotIndex == 0 && g1exec.Holds[0].Holder == lr[1].Id,
            "resume-r1-hold");
        Check(g2exec != null && g2exec.Queued
            && g2exec.EnqueueGameSeconds == r2enqueue && g2exec.QueuePosition == 1,
            "resume-r2-wait");
        Check(g7exec != null && g7exec.Stage == ExecutionStage.Travel
            && !g7exec.IsTerminal, "resume-r7-travel");
        ApproachPose r7pos = s.Executor.GetResidentPosition(r[7].Id);
        ApproachPose g7pos = g.Executor.GetResidentPosition(lr[7].Id);
        Check(r7pos.X == g7pos.X && r7pos.Y == g7pos.Y && r7pos.Z == g7pos.Z,
            "resume-r7-position");
        WillRecord gwill;
        ResidentId gguardian;
        Check(g.Arbitrator.TryGetLastWill(lr[0].Id, out gwill)
            && gwill.MoneyTotalPercent() == 100 && gwill.HomeSharePercent == 50
            && gwill.Heirlooms.Count == 1 && gwill.Heirlooms[0] == stove
            && gwill.Note.Equals("note") && gwill.FuneralPreference.Equals("brief")
            && gwill.CareHouseholds.Count == 1
            && g.Arbitrator.TryGetCaregiver(lr[0].Id, out gguardian)
            && gguardian == lr[1].Id, "roundtrip-will-caregiver");
        Check(LedgerStateOf(g.World, r0actionId) == LedgerState.Reserved
            && LedgerStateOf(g.World, g0.Action) == LedgerState.Reserved,
            "resume-ledger-reserved");
        IReadOnlyList<EffectLedgerEntry> gledger = g.World.EffectLedger;
        // The resume tick runs r5's promoted Append to perform, so it
        // re-reserves between r0 at dispatch and r1 at slot re-arrival.
        Check(gledger.Count == pre.EffectLedger.Count + 3
            && gledger[pre.EffectLedger.Count].Action == g0.Action
            && gledger[pre.EffectLedger.Count].EffectKind.Equals("ACT-EDU-02")
            && gledger[pre.EffectLedger.Count].State == LedgerState.Reserved
            && gledger[pre.EffectLedger.Count + 1].Action == g5action
            && gledger[pre.EffectLedger.Count + 1].EffectKind.Equals("ACT-EDU-02")
            && gledger[pre.EffectLedger.Count + 1].State == LedgerState.Reserved
            && gledger[pre.EffectLedger.Count + 2].Action == g1.Action
            && gledger[pre.EffectLedger.Count + 2].EffectKind.Equals("ACT-FOOD-02")
            && gledger[pre.EffectLedger.Count + 2].State == LedgerState.Reserved,
            "resume-ledger-rereserves");

        // Completion parity: the loaded world finishes the same action at
        // the same absolute game second as the uninterrupted reference, and
        // each completion effect commits exactly once across the boundary.
        int refTicks = 0;
        while (refTicks < 400)
        {
            ExecutionRecord want = s.Executor.GetExecution(r0current.Action);
            if (want != null && want.IsTerminal)
            {
                break;
            }
            Step(s, 1);
            refTicks++;
        }
        int loadTicks = 0;
        while (loadTicks < 400)
        {
            ExecutionRecord want = g.Executor.GetExecution(g0.Action);
            if (want != null && want.IsTerminal)
            {
                break;
            }
            StepLoaded(g, 1);
            loadTicks++;
        }
        ExecutionRecord r0done = s.Executor.GetExecution(r0current.Action);
        ExecutionRecord g0done = g.Executor.GetExecution(g0.Action);
        Check(r0done != null && r0done.IsTerminal && g0done != null && g0done.IsTerminal
            && refTicks == loadTicks, "parity-same-ticks");
        Check(s.World.GameSeconds == g.World.GameSeconds, "parity-same-time");
        Check(CountCommitted(s.World, "ACT-EDU-02") == 1
            && CountCommitted(g.World, "ACT-EDU-02") == 1
            && LedgerStateOf(g.World, r0actionId) == LedgerState.Reserved,
            "parity-exactly-once");

        // Resumed pending work promotes through the normal tick path and
        // completes exactly once; the pre-save completed action id is never
        // re-dispatched in the loaded world.
        int g5ticks = 0;
        while (g5ticks < 800)
        {
            ExecutionRecord want = g.Executor.GetExecution(g5action);
            if (want != null && want.IsTerminal)
            {
                break;
            }
            StepLoaded(g, 1);
            g5ticks++;
        }
        ExecutionRecord g5done = g.Executor.GetExecution(g5action);
        Check(g5done != null && g5done.IsTerminal
            && g5done.Outcome.Value == TerminalOutcome.Completed,
            "resume-r5-pending-completes");
        Check(CountCommitted(g.World, "ACT-EDU-02") == 2, "resume-r5-commits-once");
        Check(g.Executor.GetExecution(r5shortAction) == null, "resume-r5-no-reexecute");

        // Fractional-edge save/load: a save mid-tick at speed >1 must
        // resume the exact sub-tick remainder, so the loaded world matches
        // the uninterrupted reference at every tick.
        Stack frac = NewSeeded(91L, 4242L, 1);
        frac.Clock.SetSpeed(SimulationSpeed.Fast);
        frac.Clock.AdvanceRealSeconds(Core, 0.07);
        frac.Executor.Tick(Exec);
        string dirFrac = UniqueDir("fraction");
        Check(SaveV1.SaveGame(frac.SaveStack(), dirFrac).Ok, "fraction-save-ok");
        SaveLoadResult loadedFrac = SaveV1.LoadGame(dirFrac, new EmptyScene());
        Check(loadedFrac.Ok, "fraction-load-ok");
        LoadedGame gfrac = loadedFrac.Game;
        Check(gfrac.Clock.TotalTicks == frac.Clock.TotalTicks, "fraction-ticks-preserved");
        Check(gfrac.Clock.TickRemainder == frac.Clock.TickRemainder, "fraction-remainder-exact");
        bool edgeParity = gfrac.World.GameSeconds == frac.World.GameSeconds;
        for (int step = 0; step < 20; step++)
        {
            frac.Clock.AdvanceRealSeconds(Core, 0.07);
            frac.Executor.Tick(Exec);
            gfrac.Clock.AdvanceRealSeconds(Core, 0.07);
            gfrac.Executor.Tick(Exec);
            if (frac.World.GameSeconds != gfrac.World.GameSeconds
                || frac.Clock.TotalTicks != gfrac.Clock.TotalTicks)
            {
                edgeParity = false;
            }
        }
        Check(edgeParity, "fraction-edge-parity");

        // Automation saved enabled restores enabled (replay disables it
        // mid-restore through human takeovers, then re-enables).
        Stack b = NewStack(11L, 7L, null);
        ResidentRecord[] rb = SortedResidents(b.World);
        Check(Direct(b, rb[0], "ACT-EDU-02", QueueMode.Immediate, null).Accepted,
            "fixture-b-submit");
        Check(!b.Arbitrator.AutomationEnabled, "fixture-b-disabled");
        b.Arbitrator.EnableAutomation();
        Step(b, 2);
        string dirB = UniqueDir("b");
        Check(SaveV1.SaveGame(b.SaveStack(), dirB).Ok, "save-b-ok");
        SaveLoadResult loadedB = SaveV1.LoadGame(dirB, new EmptyScene());
        Check(loadedB.Ok && loadedB.Game.Arbitrator.AutomationEnabled
            && !NotesContain(loadedB.Game.Notes, "takeover"), "roundtrip-automation-true");

        // Automation saved disabled with an automated-only queue restores
        // disabled through a takeover (replay itself runs enabled).
        Stack t = NewStack(16L, 11L, null);
        ResidentRecord[] rt = SortedResidents(t.World);
        Check(DirectAs(t, rt[0], "ACT-EDU-02", QueueMode.Immediate, null,
            CommandSource.PlayerAutomated).Accepted, "fixture-t-submit");
        t.Arbitrator.Takeover(rt[1].Id);
        Check(!t.Arbitrator.AutomationEnabled, "fixture-t-disabled");
        Step(t, 2);
        string dirT = UniqueDir("t");
        Check(SaveV1.SaveGame(t.SaveStack(), dirT).Ok, "save-t-ok");
        SaveLoadResult loadedT = SaveV1.LoadGame(dirT, new EmptyScene());
        Check(loadedT.Ok && !loadedT.Game.Arbitrator.AutomationEnabled
            && NotesContain(loadedT.Game.Notes, "disabled via takeover"),
            "roundtrip-automation-takeover");

        // Paused save: nothing applies while paused; resume promotes the
        // restored head and completes with a single reservation.
        Stack c = NewStack(12L, 9L, null);
        ResidentRecord[] rc = SortedResidents(c.World);
        Check(Direct(c, rc[0], "ACT-EDU-02", QueueMode.Immediate, null).Accepted,
            "fixture-c-submit");
        Step(c, 3);
        ActionEntry ccurrent = c.Arbitrator.GetCurrent(rc[0].Id);
        ExecutionRecord cexec = c.Executor.GetExecution(ccurrent.Action);
        long cperformStart = cexec.PerformStartGameSeconds;
        float cneed = rc[0].GetNeed(Need.Hunger);
        c.Clock.Pause();
        string dirC = UniqueDir("c");
        Check(SaveV1.SaveGame(c.SaveStack(), dirC).Ok, "save-c-ok");
        SaveLoadResult loadedC = SaveV1.LoadGame(dirC, new EmptyScene());
        Check(loadedC.Ok, "load-c-ok");
        LoadedGame gc = loadedC.Game;
        ResidentRecord[] lrc = SortedResidents(gc.World);
        Check(gc.Clock.Paused, "paused-restored");
        ActionEntry[] cpending = gc.Arbitrator.GetPending(lrc[0].Id);
        Check(gc.Arbitrator.GetCurrent(lrc[0].Id) == null && cpending.Length == 1
            && (long)cpending[0].Command == (long)ccurrent.Command,
            "paused-head-waits");
        ExecutionRecord gcexec = gc.Executor.GetExecution(cpending[0].Action);
        Check(gcexec != null && gcexec.PerformStartGameSeconds == cperformStart
            && LedgerStateOf(gc.World, cpending[0].Action) == LedgerState.Reserved
            && lrc[0].GetNeed(Need.Hunger) == cneed, "paused-no-rewards");
        gc.Clock.Resume();
        Check(gc.Arbitrator.PromoteNext(lrc[0].Id), "paused-promote");
        Check((long)gc.Arbitrator.GetCurrent(lrc[0].Id).Action == (long)cpending[0].Action,
            "paused-same-action");
        int cTicks = 0;
        while (cTicks < 800)
        {
            ExecutionRecord want = gc.Executor.GetExecution(cpending[0].Action);
            if (want != null && want.IsTerminal)
            {
                break;
            }
            StepLoaded(gc, 1);
            cTicks++;
        }
        Check(CountCommitted(gc.World, "ACT-EDU-02") == 1, "paused-completes-once");

        // Kill points: every failure before the replace keeps the previous
        // valid save; a completed replace counts even without UI ack.
        Stack k = NewSeeded(13L, 3L, 2);
        ResidentRecord[] rk = SortedResidents(k.World);
        Check(Direct(k, rk[0], "ACT-EDU-02", QueueMode.Immediate, null).Accepted,
            "fixture-k-submit");
        Step(k, 2);
        string dirK = UniqueDir("k");
        Check(SaveV1.SaveGame(k.SaveStack(), dirK).Ok, "save-k-v1");
        long t1 = k.World.GameSeconds;
        Step(k, 5);
        byte[] v1bytes = File.ReadAllBytes(Path.Combine(dirK, SaveFormat.FileName));
        Fault[] faults = new Fault[4];
        faults[0] = Fault.ThrowWrite;
        faults[1] = Fault.WriteThenThrow;
        faults[2] = Fault.PartialThenThrow;
        faults[3] = Fault.ThrowReplace;
        string[] faultNames = new string[4];
        faultNames[0] = "kill-before-tmp";
        faultNames[1] = "kill-after-tmp";
        faultNames[2] = "kill-mid-tmp";
        faultNames[3] = "kill-after-validate";
        for (int f = 0; f < faults.Length; f++)
        {
            FaultStore store = new FaultStore();
            store.Mode = faults[f];
            SaveWriteResult attempt = SaveV1.SaveGame(k.SaveStack(), dirK, store);
            byte[] after = File.ReadAllBytes(Path.Combine(dirK, SaveFormat.FileName));
            bool same = after.Length == v1bytes.Length;
            for (int i = 0; same && i < after.Length; i++)
            {
                same = after[i] == v1bytes[i];
            }
            SaveLoadResult back = SaveV1.LoadGame(dirK, new EmptyScene(), store);
            Check(!attempt.Ok && same && back.Ok && back.Game.World.GameSeconds == t1,
                faultNames[f]);
        }
        Check(SaveV1.SaveGame(k.SaveStack(), dirK).Ok, "save-k-v2");
        SaveLoadResult v2 = SaveV1.LoadGame(dirK, new EmptyScene());
        Check(v2.Ok && v2.Game.World.GameSeconds == k.World.GameSeconds,
            "kill-after-rename-is-success");
        SaveLoadResult bak = SaveV1.LoadBackupGame(dirK, new EmptyScene());
        Check(bak.Ok && bak.Game.World.GameSeconds == t1, "backup-loads-v1");

        // Corrupt input: refused with the bytes retained and the running
        // world untouched; the backup stays available.
        string dirF = UniqueDir("f");
        Check(SaveV1.SaveGame(k.SaveStack(), dirF).Ok, "save-f-ok");
        string pathF = Path.Combine(dirF, SaveFormat.FileName);
        byte[] goodF = File.ReadAllBytes(pathF);
        int beforeHash = k.World.ComputeStateHash();
        byte[] flipped = new byte[goodF.Length];
        Array.Copy(goodF, flipped, goodF.Length);
        // Flip a digit inside a payload value so the document still parses
        // and the hash check (not the parser) is what refuses it.
        int flipAt = -1;
        string needle = "\"townFund\":";
        for (int n = 0; n + needle.Length < flipped.Length; n++)
        {
            bool hit = true;
            for (int m = 0; hit && m < needle.Length; m++)
            {
                hit = flipped[n + m] == (byte)needle[m];
            }
            if (hit)
            {
                flipAt = n + needle.Length;
            }
        }
        Check(flipAt > 0, "corrupt-needle");
        flipped[flipAt] = (byte)(flipped[flipAt] == (byte)'0' ? (byte)'1' : (byte)'0');
        File.WriteAllBytes(pathF, flipped);
        SaveLoadResult corrupt = SaveV1.LoadGame(dirF, new EmptyScene());
        byte[] keptF = File.ReadAllBytes(pathF);
        bool keptSame = keptF.Length == flipped.Length && keptF[flipAt] == flipped[flipAt];
        Check(!corrupt.Ok && corrupt.Notice.Contains("integrity") && keptSame
            && k.World.ComputeStateHash() == beforeHash, "corrupt-refused");
        File.WriteAllBytes(pathF, new byte[0]);
        SaveLoadResult empty = SaveV1.LoadGame(dirF, new EmptyScene());
        Check(!empty.Ok && File.ReadAllBytes(pathF).Length == 0
            && k.World.ComputeStateHash() == beforeHash, "truncated-refused");
        File.WriteAllBytes(pathF, goodF);
        Check(SaveV1.SaveGame(k.SaveStack(), dirF).Ok, "save-f-v2");
        File.WriteAllBytes(pathF, flipped);
        SaveLoadResult corrupt2 = SaveV1.LoadGame(dirF, new EmptyScene());
        Check(!corrupt2.Ok && corrupt2.BackupAvailable, "corrupt-offers-backup");

        // Unknown versions are refused naming the version; the file is left
        // untouched. Forgeries are reassembled with a valid hash so the
        // version check (not the hash check) is what refuses them.
        string dirG = UniqueDir("g");
        Check(SaveV1.SaveGame(k.SaveStack(), dirG).Ok, "save-g-ok");
        byte[] goodG = File.ReadAllBytes(Path.Combine(dirG, SaveFormat.FileName));
        JsonObject forgeDoc = SaveV1.ParseDocument(goodG);
        JsonObject forgeHeader = forgeDoc.GetObject("header", "forge");
        JsonObject forgePayload = forgeDoc.GetObject("payload", "forge");
        long forgeWorld = forgeHeader.GetLong("worldId", "forge");
        uint forgeEpoch = forgeHeader.GetUInt("worldEpoch", "forge");
        long forgeAt = forgeHeader.GetLong("savedAtGameSeconds", "forge");
        string dirH = UniqueDir("h");
        Directory.CreateDirectory(dirH);
        byte[] v999 = SaveV1.AssembleDocument(forgePayload, 999, forgeWorld, forgeEpoch, forgeAt);
        string pathH = Path.Combine(dirH, SaveFormat.FileName);
        File.WriteAllBytes(pathH, v999);
        SaveLoadResult refused999 = SaveV1.LoadGame(dirH, new EmptyScene());
        byte[] keptH = File.ReadAllBytes(pathH);
        bool hSame = keptH.Length == v999.Length;
        for (int i = 0; hSame && i < keptH.Length; i++)
        {
            hSame = keptH[i] == v999[i];
        }
        Check(!refused999.Ok && refused999.Notice.Contains("999") && hSame,
            "version-999-refused");
        JsonObject forgeWorldJson = forgePayload.GetObject("world", "forge");
        forgeWorldJson.Set("worldId", new JsonNumber((forgeWorld + 1L).ToString()));
        byte[] mismatch = SaveV1.AssembleDocument(forgePayload, SaveFormat.SchemaVersion,
            forgeWorld, forgeEpoch, forgeAt);
        File.WriteAllBytes(pathH, mismatch);
        SaveLoadResult mismatched = SaveV1.LoadGame(dirH, new EmptyScene());
        Check(!mismatched.Ok && mismatched.Notice.Contains("match"),
            "header-mismatch-refused");
        forgeWorldJson.Set("worldId", new JsonNumber(forgeWorld.ToString()));
        JsonObject forgeClock = forgePayload.GetObject("clock", "forge");
        forgeClock.Set("tickRemainder", new JsonNumber("1.5"));
        byte[] badRemainder = SaveV1.AssembleDocument(forgePayload, SaveFormat.SchemaVersion,
            forgeWorld, forgeEpoch, forgeAt);
        File.WriteAllBytes(pathH, badRemainder);
        SaveLoadResult refusedRemainder = SaveV1.LoadGame(dirH, new EmptyScene());
        Check(!refusedRemainder.Ok && refusedRemainder.Notice.Contains("tickRemainder"),
            "clock-remainder-refused");

        // Missing or unreachable scene objects reconcile in-progress actions
        // to failed with a note; the world still loads.
        SaveLoadResult noScene = SaveV1.LoadGame(dirA, new EmptyScene());
        Check(noScene.Ok && NotesContain(noScene.Game.Notes, "TargetMissing"),
            "missing-scene-reconciles");
        FixedScene downScene = new FixedScene();
        SceneObj downStove = new SceneObj();
        downStove.Id = stove;
        downStove.Poses = stovePoses;
        downStove.Reachable = false;
        downScene.Objs.Add(downStove);
        SaveLoadResult downLoad = SaveV1.LoadGame(dirA, downScene);
        Check(downLoad.Ok && NotesContain(downLoad.Game.Notes, "NoPath"),
            "unreachable-reconciles");

        // Empty world round-trips with no notes.
        Stack e = NewSeeded(14L, 5L, 0);
        string dirE = UniqueDir("e");
        Check(SaveV1.SaveGame(e.SaveStack(), dirE).Ok, "save-empty-ok");
        SaveLoadResult loadedE = SaveV1.LoadGame(dirE, new EmptyScene());
        Check(loadedE.Ok && loadedE.Game.World.ResidentCount == 0
            && (uint)loadedE.Game.World.Epoch == 1u
            && loadedE.Game.Notes.Length == 0, "roundtrip-empty");

        // Settings: separately versioned, survive restart and world load,
        // never mutate the world, and report unsupported fields.
        string dirS = UniqueDir("s");
        int worldHashBefore = k.World.ComputeStateHash();
        Check(SaveV1.SaveSettings(new SettingsRecord(1, 0.7f, 1.25f, true), dirS).Ok,
            "settings-save-ok");
        SettingsLoadResult settings = SaveV1.LoadSettings(dirS);
        Check(settings.Ok && settings.Settings.MixerLevel == 0.7f
            && settings.Settings.TextScale == 1.25f
            && settings.Settings.ReducedMotion
            && settings.UnsupportedFields.Length == 0, "settings-roundtrip");
        Check(settings.Settings.AmbienceLevel == 0.7f && settings.Settings.WorldLevel == 0.7f
            && settings.Settings.VoiceLevel == 0.7f && settings.Settings.InterfaceLevel == 0.7f,
            "settings-roundtrip-group-levels");
        Check(k.World.ComputeStateHash() == worldHashBefore, "settings-world-untouched");
        Stack other = NewSeeded(15L, 6L, 1);
        Step(other, 4);
        SettingsLoadResult settingsOther = SaveV1.LoadSettings(dirS);
        Check(settingsOther.Ok && settingsOther.Settings.MixerLevel == 0.7f,
            "settings-survive-world");
        JsonObject settingsDoc = SaveV1.ParseDocument(
            File.ReadAllBytes(Path.Combine(dirS, SaveV1.SettingsFileName)));
        JsonObject settingsPayload = settingsDoc.GetObject("payload", "forge");
        settingsPayload.Add("remap", new JsonString("newer-client"));
        string dirS2 = UniqueDir("s2");
        Directory.CreateDirectory(dirS2);
        File.WriteAllBytes(Path.Combine(dirS2, SaveV1.SettingsFileName),
            SaveV1.AssembleSettingsDocument(settingsPayload));
        SettingsLoadResult future = SaveV1.LoadSettings(dirS2);
        Check(future.Ok && future.Settings.TextScale == 1.25f
            && future.UnsupportedFields.Length == 1
            && future.UnsupportedFields[0].Equals("remap"), "settings-unsupported-field");
        JsonObject badVersion = new JsonObject();
        badVersion.Add("mixerLevel", 0.5f);
        badVersion.Add("textScale", 1.0f);
        badVersion.Add("reducedMotion", false);
        string badText = SaveV1.RenderJson(badVersion);
        string badHash = SaveV1.Sha256Hex(Encoding.UTF8.GetBytes(badText));
        StringBuilder badDoc = new StringBuilder();
        badDoc.Append("{\"header\":{\"schemaVersion\":7,\"sha256\":\"");
        badDoc.Append(badHash);
        badDoc.Append("\"},\"payload\":");
        badDoc.Append(badText);
        badDoc.Append("}");
        string dirS3 = UniqueDir("s3");
        Directory.CreateDirectory(dirS3);
        File.WriteAllBytes(Path.Combine(dirS3, SaveV1.SettingsFileName),
            Encoding.UTF8.GetBytes(badDoc.ToString()));
        SettingsLoadResult badSettings = SaveV1.LoadSettings(dirS3);
        Check(!badSettings.Ok && badSettings.Detail.Contains("7"), "settings-version-refused");
        Check(!SaveV1.LoadSettings(UniqueDir("missing")).Ok, "settings-missing");
        FaultStore settingsFault = new FaultStore();
        settingsFault.Mode = Fault.ThrowWrite;
        Check(!SaveV1.SaveSettings(new SettingsRecord(1, 0.1f, 1.0f, false), dirS,
            settingsFault).Ok, "settings-fault");
        SettingsLoadResult settingsKept = SaveV1.LoadSettings(dirS);
        Check(settingsKept.Ok && settingsKept.Settings.MixerLevel == 0.7f,
            "settings-fault-keeps-old");

        // ACCESS: the complete eleven-field record round-trips exactly.
        string dirAS = UniqueDir("a");
        Dictionary<string, string> accessRemaps = new Dictionary<string, string>();
        accessRemaps.Add("Pause", "P");
        accessRemaps.Add("OpenMap", "Tab");
        SettingsRecord access = new SettingsRecord(1, 0.7f, 1.5f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, accessRemaps);
        bool accessSaved = SaveV1.SaveSettings(access, dirAS).Ok;
        SettingsLoadResult accessLoaded = SaveV1.LoadSettings(dirAS);
        Check(accessSaved && accessLoaded.Ok
            && accessLoaded.UnsupportedFields.Length == 0
            && SameAccess(accessLoaded.Settings, access), "settings-access-roundtrip");

        // ACCESS: the complete record survives a real world save/load.
        Stack accessWorld = NewSeeded(25L, 9L, 2);
        Step(accessWorld, 4);
        string dirASW = UniqueDir("aw");
        bool accessWorldSaved = SaveV1.SaveGame(accessWorld.SaveStack(), dirASW).Ok;
        SaveLoadResult accessWorldLoaded = SaveV1.LoadGame(dirASW, new EmptyScene());
        int accessHash = accessWorldLoaded.Ok
            ? accessWorldLoaded.Game.World.ComputeStateHash() : 0;
        SettingsLoadResult accessAfterWorld = SaveV1.LoadSettings(dirAS);
        Check(accessWorldSaved && accessWorldLoaded.Ok && accessAfterWorld.Ok
            && SameAccess(accessAfterWorld.Settings, access)
            && accessWorldLoaded.Game.World.ComputeStateHash() == accessHash,
            "settings-access-survive-world");

        // ACCESS: invalid scales and a conflicting remap refuse with disk kept.
        string accessBytes = Convert.ToBase64String(
            File.ReadAllBytes(Path.Combine(dirAS, SaveV1.SettingsFileName)));
        SettingsRecord lowScale = new SettingsRecord(1, 0.7f, 0.99f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, accessRemaps);
        SettingsRecord highScale = new SettingsRecord(1, 0.7f, 1.51f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, accessRemaps);
        Dictionary<string, string> conflictRemaps = new Dictionary<string, string>();
        conflictRemaps.Add("Pause", "Tab");
        conflictRemaps.Add("OpenMap", "Tab");
        SettingsRecord accessConflict = new SettingsRecord(1, 0.7f, 1.5f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, conflictRemaps);
        bool accessRefused = !SaveV1.SaveSettings(lowScale, dirAS).Ok
            && !SaveV1.SaveSettings(highScale, dirAS).Ok
            && !SaveV1.SaveSettings(accessConflict, dirAS).Ok;
        SettingsLoadResult accessKept = SaveV1.LoadSettings(dirAS);
        Check(accessRefused
            && Convert.ToBase64String(
                File.ReadAllBytes(Path.Combine(dirAS, SaveV1.SettingsFileName))) == accessBytes
            && accessKept.Ok && SameAccess(accessKept.Settings, access),
            "settings-access-invalid-unchanged");

        // ACCESS: a legacy AUDIO-MIX file keeps its values, defaults the rest.
        string dirASL = UniqueDir("al");
        Directory.CreateDirectory(dirASL);
        File.WriteAllBytes(Path.Combine(dirASL, SaveV1.SettingsFileName),
            File.ReadAllBytes("tests/production/fixtures/ACCESS/audio-mix-v1.json"));
        SettingsLoadResult accessLegacy = SaveV1.LoadSettings(dirASL);
        Check(accessLegacy.Ok && accessLegacy.UnsupportedFields.Length == 0
            && accessLegacy.Settings.MixerLevel == 0.7f
            && accessLegacy.Settings.TextScale == 1.25f
            && accessLegacy.Settings.ReducedMotion
            && accessLegacy.Settings.AmbienceLevel == 0.2f
            && accessLegacy.Settings.WorldLevel == 0.4f
            && accessLegacy.Settings.VoiceLevel == 0.6f
            && accessLegacy.Settings.InterfaceLevel == 0.8f
            && accessLegacy.Settings.NonColorCues && accessLegacy.Settings.Captions
            && accessLegacy.Settings.InputRemaps.Count == 0,
            "settings-access-legacy-defaults");

        // Null inputs throw instead of corrupting state.
        bool nulls = false;
        try
        {
            SaveV1.SaveGame(null, dirA);
        }
        catch (ArgumentNullException)
        {
            try
            {
                SaveV1.LoadGame(null, new EmptyScene());
            }
            catch (ArgumentNullException)
            {
                try
                {
                    SaveV1.LoadGame(dirA, null);
                }
                catch (ArgumentNullException)
                {
                    try
                    {
                        new GameStack(null, b.Clock, b.Arbitrator, b.Executor, b.Appearances);
                    }
                    catch (ArgumentNullException)
                    {
                        nulls = true;
                    }
                }
            }
        }
        Check(nulls, "null-inputs-throw");

        Check(!HasStaticMutable(typeof(SaveV1)), "femp-savev1");
        Check(!HasStaticMutable(typeof(GameStack)), "femp-gamestack");
        Check(!HasStaticMutable(typeof(SaveFileStore)), "femp-store");
        Check(!HasStaticMutable(typeof(SaveWriteResult)), "femp-write-result");
        Check(!HasStaticMutable(typeof(SaveLoadResult)), "femp-load-result");
        Check(!HasStaticMutable(typeof(SettingsLoadResult)), "femp-settings-result");
        Check(!HasStaticMutable(typeof(LoadedGame)), "femp-loaded-game");
        Check(!HasStaticMutable(typeof(JsonObject)), "femp-json-object");
        Check(!HasStaticMutable(typeof(JsonArray)), "femp-json-array");
        Check(!HasStaticMutable(typeof(JsonString)), "femp-json-string");
        Check(!HasStaticMutable(typeof(JsonNumber)), "femp-json-number");
        Check(!HasStaticMutable(typeof(JsonBool)), "femp-json-bool");
        Check(!HasStaticMutable(typeof(JsonNull)), "femp-json-null");
        Check(!HasStaticMutable(typeof(JsonParser)), "femp-json-parser");

        string ocrRoot = Path.Combine(Path.GetTempPath(), "save-ocr-" + Guid.NewGuid().ToString("N"));
        try
        {
            FailedResumeFoodReservation(ocrRoot);
            SavedServiceActionHighWaterMark(ocrRoot);
        }
        finally
        {
            if (Directory.Exists(ocrRoot)) Directory.Delete(ocrRoot, true);
        }
        failures += M1Regressions.Run("SAVE");
        if (failures == 0)
        {
            Console.WriteLine("SAVE ALL PASS");
        }
        else
        {
            Console.WriteLine("SAVE FAILURES: " + failures.ToString());
        }
        return failures;
    }

    private static bool HasStaticMutable(Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!fields[i].IsInitOnly && !fields[i].IsLiteral)
            {
                return true;
            }
        }
        return false;
    }
}
