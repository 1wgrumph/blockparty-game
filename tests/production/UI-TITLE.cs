/*
UI-TITLE@1 red contract, Assets/Blockparty/UI/TitleFlow.cs, namespace Blockparty.UI.
Plain C# 9; no UnityEngine. Scene/view wiring and packaged walkthroughs are GREYBOX.

public enum TitleScreen { Title, Creator, Playing, Pause, Load, Settings, Credits, Quit }
public enum TitleAction { NewGame, Continue, Load, Settings, Credits, Quit,
                          Pause, Resume, Save, ReturnToTitle, Back }
public sealed class TitleFlowResult { public bool Ok { get; } public string Reason { get; } }
public sealed class TitleFlow {
  public TitleFlow(string directory, ISceneContent scene);
  public TitleScreen Screen { get; }             // initially Title
  public GameStack Game { get; }                 // null until prepared/loaded
  public bool ContinueEnabled { get; }           // validates real save, not existence alone
  public string ContinueReason { get; }          // nonblank when disabled
  public SaveHeader? SavedHeader { get; }         // valid current save metadata, else null
  public SettingsRecord Settings { get; }        // loaded separately from world
  public string CreditsText { get; }             // actual credits, no placeholder
  public TitleFlowResult Activate(TitleAction action);
  public TitleFlowResult PrepareNewGame(long worldId, long seed, string[] names);
  public TitleFlowResult BeginPlay();
  public TitleFlowResult LoadSavedGame();         // from Load screen, SaveV1.LoadGame
  public TitleFlowResult ApplySettings(SettingsRecord settings); // SaveV1.SaveSettings
}
NewGame opens Creator; PrepareNewGame uses ResidentFactory, exposing its editable
GameStack while staying in Creator; BeginPlay enters Playing. Pause pauses Clock;
Resume resumes it. Load/Settings open their named screens from Title or Pause;
Back returns to that origin. Save uses SaveV1 and remains in Pause. Continue
loads the real current save into Playing. ReturnToTitle opens Title. Quit exposes
the Quit effect for the host. Refusals return !Ok and a nonblank Reason, preserving
the active world, screen and input save bytes. All state belongs to the instance.

Resource group: none (Roslyn/runtime only, no editor/display/server). Each test
owns one pid+GUID temp directory and removes it in finally; no shared files.
Local projections only: these checks do not close whole SEIT acceptance cases.
N3 checks exposed model strings; packaged string/control inspection stays pending.
Rename below exercises existing SIM-CORE + persistence, not the later rename UI.
Preset tuples cover the shared SkinTone/Face/Accessory/outfit API; amended hair
and eye catalogs and their full persistence matrix belong to APPEARANCE-AMEND.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Activities;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class TitleChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;

    // Real fresh worlds have no registered scene objects or in-progress actions.
    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static string NewDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static TitleFlow NewFlow(string directory)
    {
        return new TitleFlow(directory, new EmptyScene());
    }

    private static void Accepted(TitleFlowResult result, string name)
    {
        Check(result.Ok, name + " " + result.Reason);
    }

    private static bool Refused(TitleFlowResult result)
    {
        return !result.Ok && !string.IsNullOrWhiteSpace(result.Reason);
    }

    // ACCESS: the same full record the ACCESS suite persists.
    private static SettingsRecord AccessRecord()
    {
        Dictionary<string, string> remaps = new Dictionary<string, string>();
        remaps.Add("Pause", "P");
        remaps.Add("OpenMap", "Tab");
        return new SettingsRecord(1, 0.7f, 1.5f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, remaps);
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

    private static TitleFlow Create(string directory, long seed, string[] names)
    {
        TitleFlow flow = NewFlow(directory);
        Accepted(flow.Activate(TitleAction.NewGame), "SEIT-UI-01-N2-new-game-control");
        Check(flow.Screen == TitleScreen.Creator, "SEIT-UI-01-N2-new-game-destination");
        Accepted(flow.PrepareNewGame(7L, seed, names), "SEIT-CHR-01-P1-prepare-eight");
        Check(flow.Screen == TitleScreen.Creator && flow.Game != null,
            "SEIT-CHR-01-P1-editable-before-play");
        return flow;
    }

    private static ResidentRecord[] Residents(TitleFlow flow)
    {
        ResidentRecord[] rows = flow.Game.World.ResidentsSnapshot();
        Array.Sort(rows, CompareResidents);
        return rows;
    }

    private static int CompareResidents(ResidentRecord left, ResidentRecord right)
    {
        return ((long)left.Id).CompareTo((long)right.Id);
    }

    // Named tuples deliberately omit the epoch, which SAVE advances on load.
    private static string Tuples(TitleFlow flow)
    {
        string value = "";
        foreach (ResidentRecord r in Residents(flow))
        {
            AppearanceRecord a;
            if (!flow.Game.Appearances.TryGet(r.Id, out a)) return "missing-appearance";
            value += ((long)r.Id).ToString() + ":" + r.DisplayName + ":"
                + a.SkinTone.ToString() + ":" + a.Face.ToString() + ":"
                + a.Accessory.ToString() + ":"
                + a.GetOutfit(OutfitCategory.Everyday).ToString() + ":"
                + a.GetOutfit(OutfitCategory.Sleep).ToString() + ":"
                + a.GetOutfit(OutfitCategory.Exercise).ToString();
            for (int i = 0; i < StateSchema.TraitCount; i++)
                value += ":" + r.GetTrait((TraitAxis)i).ToString();
            value += "\n";
        }
        return value;
    }

    private static bool NoPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] markers = { "TODO", "Coming soon", "Not implemented", "lorem ipsum" };
        foreach (string marker in markers)
            if (value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    private static void MenusAndSaves(string root)
    {
        string directory = NewDirectory(root, "menus");
        TitleFlow flow = NewFlow(directory);
        Check(flow.Screen == TitleScreen.Title && flow.Game == null,
            "SEIT-UI-01-P1-initial-title");
        Check(!flow.ContinueEnabled && !string.IsNullOrWhiteSpace(flow.ContinueReason)
            && !flow.SavedHeader.HasValue, "SEIT-UI-01-N1-missing-save-disabled-with-reason");
        TitleFlowResult missing = flow.Activate(TitleAction.Continue);
        Check(Refused(missing) && flow.Screen == TitleScreen.Title && flow.Game == null
            && !File.Exists(Path.Combine(directory, SaveFormat.FileName)),
            "SEIT-UI-01-N1-missing-continue-refused-unchanged");

        Accepted(flow.Activate(TitleAction.Credits), "SEIT-UI-01-N2-title-credits");
        Check(flow.Screen == TitleScreen.Credits && NoPlaceholder(flow.CreditsText),
            "SEIT-UI-01-N3-credits-real-content");
        Accepted(flow.Activate(TitleAction.Back), "SEIT-UI-01-N2-credits-back");
        Check(flow.Screen == TitleScreen.Title, "SEIT-UI-01-N2-credits-return-origin");
        Accepted(flow.Activate(TitleAction.Settings), "SEIT-UI-01-N2-title-settings");
        Check(flow.Screen == TitleScreen.Settings, "SEIT-UI-01-N2-title-settings-destination");
        SettingsRecord settings = new SettingsRecord(1, 0.35f, 1.25f, true);
        Accepted(flow.ApplySettings(settings), "SEIT-AC-12-title-settings-write");
        SettingsLoadResult diskSettings = SaveV1.LoadSettings(directory);
        Check(diskSettings.Ok && diskSettings.Settings.MixerLevel == 0.35f
            && diskSettings.Settings.TextScale == 1.25f && diskSettings.Settings.ReducedMotion,
            "SEIT-AC-12-title-settings-real-file");
        Check(diskSettings.Settings.AmbienceLevel == 0.35f && diskSettings.Settings.WorldLevel == 0.35f
            && diskSettings.Settings.VoiceLevel == 0.35f && diskSettings.Settings.InterfaceLevel == 0.35f,
            "SEIT-AC-12-title-settings-group-levels");
        Accepted(flow.Activate(TitleAction.Back), "SEIT-UI-01-N2-title-settings-back");
        Check(flow.Screen == TitleScreen.Title, "SEIT-UI-01-N2-title-settings-origin");

        string[] names = { "One", "Two", "Alex", "Alex", "Five", "Six", "Seven", "Eight" };
        flow = Create(directory, 4242L, names);
        ResidentRecord[] residents = Residents(flow);
        HashSet<ResidentId> ids = new HashSet<ResidentId>();
        bool named = residents.Length == 8;
        for (int i = 0; i < residents.Length; i++)
        {
            ids.Add(residents[i].Id);
            named &= residents[i].DisplayName == names[i];
            flow.Game.Appearances.SetPreset(Core, residents[i].Id, AppearanceDimension.SkinTone,
                (i + 1) % ResidentFactory.OptionCount(AppearanceDimension.SkinTone));
            flow.Game.World.SetTrait(Core, residents[i].Id, TraitAxis.Sociable, i - 4);
        }
        Check(named && ids.Count == 8, "SEIT-AC-01-SEIT-CHR-01-P1-eight-named-distinct-including-duplicates");
        string before = Tuples(flow);
        Accepted(flow.BeginPlay(), "SEIT-CHR-01-P1-begin-play");
        Check(flow.Screen == TitleScreen.Playing, "SEIT-UI-01-N2-begin-play-destination");
        Check(Tuples(flow) == before, "SEIT-CHR-01-P1-chosen-tuples-enter-play-unchanged");
        flow.Game.World.SetGameSeconds(Core, 12345L);
        Accepted(flow.Activate(TitleAction.Pause), "SEIT-UI-01-N2-open-pause");
        Check(flow.Screen == TitleScreen.Pause && flow.Game.Clock.Paused,
            "SEIT-UI-01-P1-pause-stops-clock");
        Accepted(flow.Activate(TitleAction.Settings), "SEIT-UI-01-N2-pause-settings");
        Check(flow.Screen == TitleScreen.Settings && flow.Game.Clock.Paused,
            "SEIT-UI-01-N2-pause-settings-destination");
        int hash = flow.Game.World.ComputeStateHash();
        Accepted(flow.ApplySettings(settings), "SEIT-AC-12-pause-settings-write");
        Check(hash == flow.Game.World.ComputeStateHash() && flow.Game.Clock.Paused,
            "SEIT-AC-12-settings-preserve-paused-world");
        Accepted(flow.Activate(TitleAction.Back), "SEIT-UI-01-N2-pause-settings-back");
        Check(flow.Screen == TitleScreen.Pause, "SEIT-UI-01-N2-pause-settings-origin");
        Accepted(flow.Activate(TitleAction.Resume), "SEIT-UI-01-N2-pause-resume");
        Check(flow.Screen == TitleScreen.Playing && !flow.Game.Clock.Paused,
            "SEIT-UI-01-P1-resume-runs-clock");
        Accepted(flow.Activate(TitleAction.Pause), "SEIT-UI-01-N2-pause-before-save");
        Accepted(flow.Activate(TitleAction.Save), "SEIT-UI-01-N2-pause-save");
        SaveLoadResult disk = SaveV1.LoadGame(directory, new EmptyScene());
        Check(flow.Screen == TitleScreen.Pause && disk.Ok && disk.Game.World.ResidentCount == 8
            && disk.Game.World.GameSeconds == 12345L, "SEIT-UI-01-P1-save-real-valid-document");
        Check(flow.SavedHeader.HasValue && flow.SavedHeader.Value.WorldId == 7L
            && flow.SavedHeader.Value.SavedAtGameSeconds == 12345L,
            "SEIT-UI-01-P1-save-metadata");

        // Hold an owned directory at the real temporary filename: real I/O fails.
        string bytes = File.ReadAllText(Path.Combine(directory, SaveFormat.FileName));
        Directory.CreateDirectory(Path.Combine(directory, SaveFormat.TempName));
        hash = flow.Game.World.ComputeStateHash();
        TitleFlowResult failedSave = flow.Activate(TitleAction.Save);
        Check(Refused(failedSave) && flow.Screen == TitleScreen.Pause
            && flow.Game.World.ComputeStateHash() == hash
            && bytes == File.ReadAllText(Path.Combine(directory, SaveFormat.FileName))
            && SaveV1.LoadGame(directory, new EmptyScene()).Ok,
            "SEIT-UI-01-N2-save-io-failure-retains-atomic-valid-save");
        Directory.Delete(Path.Combine(directory, SaveFormat.TempName));

        flow.Game.World.RenameResident(Core, residents[0].Id, "Unsaved change");
        Accepted(flow.Activate(TitleAction.Load), "SEIT-UI-01-N2-pause-load");
        Check(flow.Screen == TitleScreen.Load, "SEIT-UI-01-N2-pause-load-destination");
        Accepted(flow.LoadSavedGame(), "SEIT-UI-01-N2-pause-load-confirm");
        Check(flow.Screen == TitleScreen.Playing && Tuples(flow) == before,
            "SEIT-AC-01-SEIT-CHR-01-P1-pause-load-restores-edited-tuples");
        Accepted(flow.Activate(TitleAction.Pause), "SEIT-UI-01-N2-pause-before-return");
        Accepted(flow.Activate(TitleAction.ReturnToTitle), "SEIT-UI-01-N2-pause-return-title");
        Check(flow.Screen == TitleScreen.Title && flow.ContinueEnabled,
            "SEIT-UI-01-P1-return-title-enables-continue");

        TitleFlow restarted = NewFlow(directory);
        Check(restarted.ContinueEnabled && restarted.Settings.MixerLevel == settings.MixerLevel
            && restarted.Settings.TextScale == settings.TextScale && restarted.Settings.ReducedMotion,
            "SEIT-AC-12-settings-survive-new-controller");
        Check(restarted.Settings.AmbienceLevel == settings.AmbienceLevel
            && restarted.Settings.WorldLevel == settings.WorldLevel
            && restarted.Settings.VoiceLevel == settings.VoiceLevel
            && restarted.Settings.InterfaceLevel == settings.InterfaceLevel,
            "SEIT-AC-12-settings-group-levels-survive-new-controller");
        Accepted(restarted.Activate(TitleAction.Continue), "SEIT-UI-01-N2-title-continue");
        Check(restarted.Screen == TitleScreen.Playing && Tuples(restarted) == before,
            "SEIT-AC-01-SEIT-CHR-01-P1-continue-restores-eight-tuples");
        TitleFlow titleLoad = NewFlow(directory);
        Accepted(titleLoad.Activate(TitleAction.Load), "SEIT-UI-01-N2-title-load");
        Check(titleLoad.Screen == TitleScreen.Load, "SEIT-UI-01-N2-title-load-destination");
        Accepted(titleLoad.LoadSavedGame(), "SEIT-UI-01-N2-title-load-confirm");
        Check(titleLoad.Screen == TitleScreen.Playing && Tuples(titleLoad) == before,
            "SEIT-CHR-01-P1-title-load-restores-tuples");
        TitleFlow quit = NewFlow(directory);
        Accepted(quit.Activate(TitleAction.Quit), "SEIT-UI-01-N2-title-quit");
        Check(quit.Screen == TitleScreen.Quit, "SEIT-UI-01-P1-quit-host-effect");
    }

    private static void ServiceContinuity(string root)
    {
        // F3: use the real Continue/load -> Pause -> Save consumer of LoadedGame.
        foreach (bool useContinue in new[] { true, false })
        {
            string directory = NewDirectory(root, useContinue ? "services-continue" : "services-load");
            TitleFlow initial = Create(directory, 4242L, null);
            GameStack stack = initial.Game;
            ResidentRecord resident = Residents(initial)[0];
            Accounts accounts = new Accounts(stack.World);
            Food food = new Food(stack.World, stack.Arbitrator, stack.Executor, accounts);
            accounts.SetSharedBalance(Core, resident.Household, 75L);
            food.SetHomeStock(Core, resident.Home, 3, 2);
            ActionInstanceId paid = stack.World.AllocateAction(Core);
            accounts.Transfer(Core, paid, AccountRef.Shared(resident.Household), AccountRef.Expense("Food"), 5L);
            SaveWriteResult written = SaveV1.SaveGame(new GameStack(stack.World, stack.Clock,
                stack.Arbitrator, stack.Executor, stack.Appearances, accounts, food), directory);
            if (!written.Ok) throw new Exception(written.Detail);
            TitleFlow loaded = NewFlow(directory);
            if (useContinue) Accepted(loaded.Activate(TitleAction.Continue), "F3 continue-populated-services");
            else
            {
                Accepted(loaded.Activate(TitleAction.Load), "F3 open-load-populated-services");
                Accepted(loaded.LoadSavedGame(), "F3 load-populated-services");
            }
            Check(loaded.Game.Accounts != null && loaded.Game.Food != null,
                "F3 active-stack-retains-restored-services-" + useContinue);
            Accepted(loaded.Activate(TitleAction.Pause), "F3 pause-before-resave");
            Accepted(loaded.Activate(TitleAction.Save), "F3 resave-populated-services");
            SaveLoadResult disk = SaveV1.LoadGame(directory, new EmptyScene());
            if (!disk.Ok) throw new Exception(disk.Notice);
            Accounts restored = disk.Game.Accounts;
            Check(restored.Balance(AccountRef.Shared(resident.Household)) == 70L
                && restored.Ledger.Count == 1 && restored.Ledger[0].Action == paid
                && restored.Ledger[0].Amount == 5L
                && disk.Game.Food.GroceryUnits(resident.Home) == 3
                && disk.Game.Food.PreparedMeals(resident.Home) == 2,
                "F3 resave-preserves-balances-ledger-stock-" + useContinue);
            restored.Transfer(Core, paid, AccountRef.Shared(resident.Household), AccountRef.Expense("Food"), 5L);
            Check(restored.Balance(AccountRef.Shared(resident.Household)) == 70L && restored.Ledger.Count == 1,
                "F3 resave-preserves-once-only-payment-" + useContinue);
        }
    }

    private static void CorruptSave(string root)
    {
        string directory = NewDirectory(root, "corrupt");
        TitleFlow flow = Create(directory, 4242L, null);
        Accepted(flow.BeginPlay(), "SEIT-UI-01-N1-corrupt-fixture-play");
        Accepted(flow.Activate(TitleAction.Pause), "SEIT-UI-01-N1-corrupt-fixture-pause");
        Accepted(flow.Activate(TitleAction.Save), "SEIT-UI-01-N1-corrupt-fixture-save");
        string file = Path.Combine(directory, SaveFormat.FileName);
        File.WriteAllText(file, "{not valid JSON");
        TitleFlow title = NewFlow(directory);
        Check(!title.ContinueEnabled && !string.IsNullOrWhiteSpace(title.ContinueReason)
            && !title.SavedHeader.HasValue, "SEIT-UI-01-N1-corrupt-save-disabled-with-reason");
        Check(Refused(title.Activate(TitleAction.Continue)) && title.Screen == TitleScreen.Title
            && title.Game == null && File.ReadAllText(file) == "{not valid JSON",
            "SEIT-UI-01-N1-corrupt-continue-refused-unchanged");
        GameStack active = flow.Game;
        int hash = active.World.ComputeStateHash();
        Accepted(flow.Activate(TitleAction.Load), "SEIT-UI-01-N2-corrupt-load-screen");
        TitleFlowResult result = flow.LoadSavedGame();
        Check(Refused(result) && flow.Screen == TitleScreen.Load && ReferenceEquals(active, flow.Game)
            && hash == flow.Game.World.ComputeStateHash() && active.Clock.Paused
            && File.ReadAllText(file) == "{not valid JSON",
            "SEIT-UI-01-N2-corrupt-load-refused-active-world-and-bytes-unchanged");
    }

    private static void DefaultsAndRepeatedEntry(string root)
    {
        TitleFlow reference = Create(NewDirectory(root, "reference"), 4242L, null);
        string expected = Tuples(reference);
        ResidentRecord defaultFive = Residents(reference)[4];
        string[] blanks = { "", "   " };
        for (int run = 0; run < blanks.Length; run++)
        {
            string directory = NewDirectory(root, "blank-" + run.ToString());
            string[] names = new string[8];
            names[4] = blanks[run];
            names[5] = defaultFive.DisplayName;
            TitleFlow flow = Create(directory, 4242L, names);
            ResidentRecord[] rows = Residents(flow);
            HashSet<ResidentId> ids = new HashSet<ResidentId>();
            bool nonblank = rows.Length == 8;
            foreach (ResidentRecord r in rows)
            {
                ids.Add(r.Id);
                nonblank &= !string.IsNullOrWhiteSpace(r.DisplayName);
            }
            Check(nonblank && ids.Count == 8 && rows[4].Id == defaultFive.Id
                && rows[4].DisplayName == defaultFive.DisplayName
                && rows[5].DisplayName == defaultFive.DisplayName && rows[4].Id != rows[5].Id,
                "SEIT-CHR-01-N1-SEIT-CHR-01-P4-SEIT-IF-RESOLVE-N5-default-no-identity-merge-" + run.ToString());
            string normalized = Tuples(flow);
            Accepted(flow.BeginPlay(), "SEIT-CHR-01-P4-blank-begin-play-" + run.ToString());
            Check(flow.Screen == TitleScreen.Playing && Tuples(flow) == normalized,
                "SEIT-CHR-01-N1-SEIT-CHR-01-P4-normalized-names-enter-play-" + run.ToString());
            flow.Game.World.RenameResident(Core, rows[4].Id, "Jordan");
            Accepted(flow.Activate(TitleAction.Pause), "SEIT-CHR-01-P4-rename-pause-" + run.ToString());
            Accepted(flow.Activate(TitleAction.Save), "SEIT-CHR-01-P4-rename-save-" + run.ToString());
            TitleFlow loaded = NewFlow(directory);
            Accepted(loaded.Activate(TitleAction.Continue), "SEIT-CHR-01-P4-rename-continue-" + run.ToString());
            ResidentRecord renamed;
            Check(loaded.Game.World.TryGetResident(rows[4].Id, out renamed)
                && renamed.DisplayName == "Jordan", "SEIT-CHR-01-P4-default-edit-persists-" + run.ToString());
        }
        for (int run = 0; run < 3; run++)
        {
            TitleFlow repeated = Create(NewDirectory(root, "repeat-" + run.ToString()), 4242L, null);
            Accepted(repeated.BeginPlay(), "SEIT-CHR-01-P2-repeated-begin-play-" + run.ToString());
            bool onList = true;
            foreach (ResidentRecord resident in Residents(repeated))
                onList &= Array.IndexOf(WorldState.DefaultNames, resident.DisplayName) >= 0;
            Console.WriteLine("seed=4242 name-list-version=" + ResidentFactory.NameListVersion.ToString());
            Check(repeated.Screen == TitleScreen.Playing && Tuples(repeated) == expected && onList,
                "SEIT-CHR-01-P2-seed-4242-name-list-repeated-entry-" + run.ToString());
        }
    }

    private static void AccessSettings(string root)
    {
        // Apply the full record: flow, file and a fresh controller agree.
        string directory = NewDirectory(root, "access-file");
        TitleFlow flow = NewFlow(directory);
        SettingsRecord access = AccessRecord();
        bool applied = flow.Activate(TitleAction.Settings).Ok
            && flow.ApplySettings(access).Ok;
        SettingsLoadResult disk = SaveV1.LoadSettings(directory);
        TitleFlow restarted = NewFlow(directory);
        Check(applied && SameAccess(flow.Settings, access) && disk.Ok
            && SameAccess(disk.Settings, access)
            && SameAccess(restarted.Settings, access),
            "SEIT-PRES-05-N2-title-access-file-and-restart");

        // The full record and remaps survive a real world transition.
        foreach (bool useContinue in new[] { true, false })
        {
            string dir = NewDirectory(root, useContinue ? "access-continue" : "access-load");
            TitleFlow setup = NewFlow(dir);
            bool ready = setup.Activate(TitleAction.NewGame).Ok
                && setup.PrepareNewGame(7L, 4242L, null).Ok
                && setup.BeginPlay().Ok
                && setup.Activate(TitleAction.Pause).Ok
                && setup.Activate(TitleAction.Settings).Ok
                && setup.ApplySettings(access).Ok
                && setup.Activate(TitleAction.Back).Ok
                && setup.Activate(TitleAction.Save).Ok;
            TitleFlow loaded = NewFlow(dir);
            bool entered = useContinue
                ? loaded.Activate(TitleAction.Continue).Ok
                : (loaded.Activate(TitleAction.Load).Ok && loaded.LoadSavedGame().Ok);
            Check(ready && entered && loaded.Screen == TitleScreen.Playing
                && loaded.Game.World.ResidentCount == 8
                && SameAccess(loaded.Settings, access),
                "SEIT-PRES-05-N2-title-access-world-load-"
                    + (useContinue ? "continue" : "load"));
        }

        // Invalid scale/remap refuse with settings, screen, disk and world kept.
        string dirI = NewDirectory(root, "access-invalid");
        TitleFlow paused = NewFlow(dirI);
        bool prepared = paused.Activate(TitleAction.NewGame).Ok
            && paused.PrepareNewGame(7L, 4242L, null).Ok
            && paused.BeginPlay().Ok
            && paused.Activate(TitleAction.Pause).Ok
            && paused.Activate(TitleAction.Settings).Ok
            && paused.ApplySettings(access).Ok;
        string before = Convert.ToBase64String(
            File.ReadAllBytes(Path.Combine(dirI, SaveV1.SettingsFileName)));
        int hash = paused.Game.World.ComputeStateHash();
        Dictionary<string, string> remaps = new Dictionary<string, string>();
        remaps.Add("Pause", "P");
        remaps.Add("OpenMap", "Tab");
        SettingsRecord badScale = new SettingsRecord(1, 0.7f, 0.99f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, remaps);
        Dictionary<string, string> conflict = new Dictionary<string, string>();
        conflict.Add("Pause", "Tab");
        conflict.Add("OpenMap", "Tab");
        SettingsRecord badRemap = new SettingsRecord(1, 0.7f, 1.5f, true,
            0.2f, 0.4f, 0.6f, 0.8f, false, false, conflict);
        TitleFlowResult refusedScale = paused.ApplySettings(badScale);
        TitleFlowResult refusedRemap = paused.ApplySettings(badRemap);
        Check(prepared && Refused(refusedScale) && Refused(refusedRemap)
            && SameAccess(paused.Settings, access)
            && paused.Screen == TitleScreen.Settings
            && Convert.ToBase64String(
                File.ReadAllBytes(Path.Combine(dirI, SaveV1.SettingsFileName))) == before
            && paused.Game.World.ComputeStateHash() == hash
            && paused.Game.Clock.Paused,
            "SEIT-PRES-05-N1-title-invalid-access-unchanged");

        // A valid change applies and still preserves the paused world.
        Dictionary<string, string> kept = new Dictionary<string, string>();
        kept.Add("Pause", "P");
        kept.Add("OpenMap", "Tab");
        SettingsRecord valid = new SettingsRecord(1, 0.7f, 1.25f, true,
            0.2f, 0.4f, 0.6f, 0.8f, true, true, kept);
        bool validApplied = paused.ApplySettings(valid).Ok;
        Check(validApplied && SameAccess(paused.Settings, valid)
            && paused.Game.World.ComputeStateHash() == hash
            && paused.Game.Clock.Paused,
            "SEIT-PRES-05-N1-title-valid-access-preserves-paused-world");
    }

    private static bool HasStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsLiteral && !field.IsInitOnly) return true;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (HasStaticMutable(nested)) return true;
        return false;
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-ui-title-"
            + Process.GetCurrentProcess().Id.ToString() + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            MenusAndSaves(root);
            ServiceContinuity(root);
            CorruptSave(root);
            DefaultsAndRepeatedEntry(root);
            AccessSettings(root);
            Check(!HasStaticMutable(typeof(TitleFlow)) && !HasStaticMutable(typeof(TitleFlowResult)),
                "SEIT-CHR-01-P2-no-static-mutable-state");
        }
        catch (Exception error)
        {
            Check(false, "SEIT-UI-01-N2-unexpected-exception " + error.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
        failures += M1Regressions.Run("UI-TITLE");
        Console.WriteLine(failures == 0 ? "UI-TITLE ALL PASS" : "UI-TITLE FAILURES: " + failures.ToString());
        return failures == 0 ? 0 : 1;
    }
}
