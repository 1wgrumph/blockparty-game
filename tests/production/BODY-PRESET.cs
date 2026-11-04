// BODY-PRESET: TDD contract over the real creator, appearance store and SaveV1.
// Required API in ResidentFactory.cs: enum BaseBody { Male = 0, Female = 1 };
// AppearanceRecord.BaseBody and AppearanceEntry.BaseBody (BaseBody), plus
// TitleFlow.SetBaseBody(ResidentId, BaseBody) -> TitleFlowResult in Creator.
// SaveV1 persists integer "baseBody"; absent means Male without a random draw.
// Keep the existing eight-dimension API intact; base body is a separate choice.
// Fixtures are independent d43deab producer output; see fixtures/BODY-PRESET/.
// Resource group isolated-csharp; no editor, display, fixed port or network.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class BodyPresetChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }

    private static string Fixtures()
    {
        // run-standalone inherits its caller's working directory.
        DirectoryInfo dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string path = Path.Combine(dir.FullName, "tests/production/fixtures/BODY-PRESET");
            if (File.Exists(Path.Combine(path, "legacy-v1-save.json"))) return path;
            dir = dir.Parent;
        }
        throw new Exception("BODY-PRESET fixtures not found from working directory");
    }

    private static string NewDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static TitleFlow Creator(string directory, long seed)
    {
        TitleFlow flow = new TitleFlow(directory, new EmptyScene());
        Require(flow.Activate(TitleAction.NewGame).Ok, "open creator");
        Require(flow.PrepareNewGame(71L, seed, null).Ok, "prepare creator");
        return flow;
    }

    private static AppearanceRecord Look(AppearanceStore store, ResidentId id)
    {
        AppearanceRecord record;
        Require(store.TryGet(id, out record), "missing appearance " + (long)id);
        return record;
    }

    private static string OtherPresets(AppearanceRecord r)
    {
        return string.Join(",", new int[] { r.SkinTone, r.Face, r.EyeColor,
            r.HairType, r.HairStyle, r.HairColor, r.GetOutfit(OutfitCategory.Everyday),
            r.Accessory, r.GetOutfit(OutfitCategory.Sleep), r.GetOutfit(OutfitCategory.Exercise),
            r.OverrideCategory });
    }

    private static string Bodies(AppearanceStore store)
    {
        List<AppearanceEntry> rows = store.Export();
        rows.Sort((a, b) => a.ResidentId.CompareTo(b.ResidentId));
        string result = "";
        foreach (AppearanceEntry row in rows)
            result += row.ResidentId.ToString() + ":" + ((int)row.BaseBody).ToString() + ";";
        return result;
    }

    private static GameStack Stack(LoadedGame loaded)
    {
        return new GameStack(loaded.World, loaded.Clock, loaded.Arbitrator,
            loaded.Executor, loaded.Appearances, loaded.Accounts, loaded.Food);
    }

    private static void CreatorSelect(string root)
    {
        Check(Enum.GetNames(typeof(BaseBody)).Length == 2
            && (int)BaseBody.Male == 0 && (int)BaseBody.Female == 1,
            "BODY-PRESET-creator-select-exactly-female-male");
        TitleFlow flow = Creator(NewDirectory(root, "creator"), 4242L);
        ResidentRecord[] residents = flow.Game.World.ResidentsSnapshot();
        Check(residents.Length == 8 && flow.Screen == TitleScreen.Creator,
            "BODY-PRESET-creator-select-eight-before-play");
        int hash = flow.Game.World.ComputeStateHash();
        foreach (BaseBody body in new BaseBody[] { BaseBody.Female, BaseBody.Male })
        {
            foreach (ResidentRecord resident in residents)
            {
                string before = OtherPresets(Look(flow.Game.Appearances, resident.Id));
                List<AppearanceEntry> all = flow.Game.Appearances.Export();
                Dictionary<long, string> priorPresets = new Dictionary<long, string>();
                foreach (AppearanceEntry row in all)
                    priorPresets.Add(row.ResidentId, OtherPresets(Look(flow.Game.Appearances, (ResidentId)row.ResidentId)));
                TitleFlowResult set = flow.SetBaseBody(resident.Id, body);
                AppearanceRecord after = Look(flow.Game.Appearances, resident.Id);
                bool othersKept = true;
                foreach (AppearanceEntry row in all)
                {
                    AppearanceRecord current = Look(flow.Game.Appearances, (ResidentId)row.ResidentId);
                    othersKept &= OtherPresets(current) == priorPresets[row.ResidentId];
                    if (row.ResidentId != (long)resident.Id) othersKept &= current.BaseBody == row.BaseBody;
                }
                Check(set.Ok && after.BaseBody == body && before == OtherPresets(after)
                    && othersKept && hash == flow.Game.World.ComputeStateHash()
                    && flow.Screen == TitleScreen.Creator,
                    "BODY-PRESET-creator-select-" + resident.Id + "-" + body);
            }
        }
        // A mixed household catches serialization that stores one global body.
        for (int i = 0; i < residents.Length; i++)
            Require(flow.SetBaseBody(residents[i].Id, i % 2 == 0 ? BaseBody.Female : BaseBody.Male).Ok,
                "set mixed household");
        string chosen = Bodies(flow.Game.Appearances);
        AppearanceStore imported = new AppearanceStore();
        imported.Import(Core, flow.Game.Appearances.Export());
        Check(Bodies(imported) == chosen, "BODY-PRESET-export-import");
        Require(flow.BeginPlay().Ok, "begin play");
        Check(Bodies(flow.Game.Appearances) == chosen, "BODY-PRESET-creator-select-enters-play");
        Require(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.Save).Ok, "save choices");
        FreshProcessLoad(Path.Combine(root, "creator"), chosen, "BODY-PRESET-export-import-fresh-process-save-load");
    }

    private static void RandomizeAndBaselines(string root)
    {
        using (JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "baseline-presets.json"))))
        {
            bool female = false, male = false;
            string firstBodies = null;
            bool seedsDiffer = false;
            foreach (JsonElement sample in corpus.RootElement.GetProperty("seeds").EnumerateArray())
            {
                long seed = sample.GetProperty("seed").GetInt64();
                TitleFlow first = Creator(NewDirectory(root, "seed-" + seed), seed);
                TitleFlow second = Creator(NewDirectory(root, "repeat-" + seed), seed);
                string bodies = Bodies(first.Game.Appearances);
                Check(bodies == Bodies(second.Game.Appearances), "BODY-PRESET-randomize-draws-repeat-" + seed);
                if (firstBodies == null) firstBodies = bodies;
                else seedsDiffer |= firstBodies != bodies;
                int count = 0;
                foreach (JsonElement row in sample.GetProperty("residents").EnumerateArray())
                {
                    ResidentId id = (ResidentId)row.GetProperty("resident").GetInt64();
                    AppearanceRecord r = Look(first.Game.Appearances, id);
                    female |= r.BaseBody == BaseBody.Female; male |= r.BaseBody == BaseBody.Male;
                    Check(r.BaseBody == BaseBody.Female || r.BaseBody == BaseBody.Male,
                        "BODY-PRESET-randomize-draws-valid-" + seed + "-" + id);
                    int[] actual = new int[] { r.SkinTone, r.Face, r.EyeColor, r.HairType,
                        r.HairStyle, r.HairColor, r.GetOutfit(OutfitCategory.Everyday), r.Accessory };
                    bool equal = true;
                    JsonElement values = row.GetProperty("values");
                    for (int i = 0; i < actual.Length; i++) equal &= actual[i] == values[i].GetInt32();
                    Check(equal, "BODY-PRESET-other-presets-unchanged-" + seed + "-" + id);
                    count++;
                }
                Check(count == 8, "BODY-PRESET-other-presets-unchanged-eight-" + seed);
            }
            Check(female && male && seedsDiffer, "BODY-PRESET-randomize-draws-seeded-variation");
        }
    }

    private static void FreshProcessLoad(string directory, string expected, string name)
    {
        ProcessStartInfo start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.ArgumentList.Add(Environment.GetCommandLineArgs()[1]);
        start.ArgumentList.Add("--reload");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(expected);
        using (Process child = Process.Start(start))
        {
            string output = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            Console.Write(output);
            Check(child.ExitCode == 0, name + " child-exit=" + child.ExitCode);
        }
    }

    private static int Reload(string directory, string expected)
    {
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
        Require(loaded.Ok && loaded.Game != null, "child load: " + loaded.Notice);
        Require(Bodies(loaded.Game.Appearances) == expected, "child body values changed");
        JsonObject payload = SaveV1.ParseDocument(File.ReadAllBytes(Path.Combine(directory, SaveFormat.FileName)))
            .GetObject("payload", "test");
        JsonArray rows = payload.GetArray("appearances", "test");
        Require(rows.Count == 8, "child appearance count");
        for (int i = 0; i < rows.Count; i++)
        {
            JsonObject row = rows.GetObject(i, "test");
            ResidentId id = (ResidentId)row.GetLong("resident", "test");
            Require(row.GetInt("baseBody", "test") == (int)Look(loaded.Game.Appearances, id).BaseBody,
                "saved baseBody missing or changed");
        }
        Console.WriteLine("PASS BODY-PRESET-export-import-child-load");
        return 0;
    }

    private static bool SameOtherPayload(byte[] legacy, byte[] saved, int loads)
    {
        JsonObject original = SaveV1.ParseDocument(legacy).GetObject("payload", "test");
        JsonObject world = original.GetObject("world", "test");
        // SAVE@1 deliberately advances epoch on every load; no other difference is exempt.
        world.Set("epoch", new JsonNumber((world.GetUInt("epoch", "test") + (uint)loads).ToString()));
        using (JsonDocument expected = JsonDocument.Parse(SaveV1.RenderJson(original)))
        using (JsonDocument actual = JsonDocument.Parse(saved))
        {
            JsonElement payload = actual.RootElement.GetProperty("payload");
            int sections = 0;
            foreach (JsonProperty section in payload.EnumerateObject()) sections++;
            if (sections != original.Keys().Length) return false;
            foreach (JsonProperty section in expected.RootElement.EnumerateObject())
            {
                JsonElement value;
                if (!payload.TryGetProperty(section.Name, out value)) return false;
                if (section.Name != "appearances")
                {
                    if (section.Value.GetRawText() != value.GetRawText()) return false;
                    continue;
                }
                if (value.GetArrayLength() != section.Value.GetArrayLength()) return false;
                for (int i = 0; i < value.GetArrayLength(); i++)
                {
                    int oldCount = 0, newCount = 0;
                    foreach (JsonProperty preset in section.Value[i].EnumerateObject())
                    {
                        JsonElement found;
                        if (!value[i].TryGetProperty(preset.Name, out found)
                            || preset.Value.GetRawText() != found.GetRawText()) return false;
                        oldCount++;
                    }
                    foreach (JsonProperty preset in value[i].EnumerateObject()) newCount++;
                    if (newCount != oldCount + 1 || value[i].GetProperty("baseBody").GetInt32() != (int)BaseBody.Male)
                        return false;
                }
            }
            return true;
        }
    }

    private static void LegacySave(string root)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures(), "legacy-v1-save.json"));
        JsonObject payload = SaveV1.ParseDocument(bytes).GetObject("payload", "test");
        ulong rng = payload.GetObject("world", "test").GetULong("rngState", "test");
        string directory = NewDirectory(root, "legacy");
        string path = Path.Combine(directory, SaveFormat.FileName);
        File.WriteAllBytes(path, bytes);
        string expected = null;
        for (int loads = 1; loads <= 2; loads++)
        {
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok && loaded.Game != null, "legacy load: " + loaded.Notice);
            bool male = loaded.Game.Appearances.Count == 8;
            foreach (ResidentRecord resident in loaded.Game.World.ResidentsSnapshot())
                male &= Look(loaded.Game.Appearances, resident.Id).BaseBody == BaseBody.Male;
            Check(male && loaded.Game.World.RngState == rng, "BODY-PRESET-legacy-save-male-no-rng-" + loads);
            if (expected == null) expected = Bodies(loaded.Game.Appearances);
            Check(Bodies(loaded.Game.Appearances) == expected, "BODY-PRESET-legacy-save-stored-stable-" + loads);
            Require(SaveV1.SaveGame(Stack(loaded.Game), directory).Ok, "legacy resave");
            Check(SameOtherPayload(bytes, File.ReadAllBytes(path), loads),
                "BODY-PRESET-legacy-save-all-other-payload-" + loads);
        }
        FreshProcessLoad(directory, expected, "BODY-PRESET-legacy-save-fresh-process");
    }

    private static void InvalidSaves(string root)
    {
        byte[] good = File.ReadAllBytes(Path.Combine(Fixtures(), "legacy-v1-save.json"));
        string[] fields = new string[] { "baseBody", "baseBody", "build", "height" };
        int[] values = new int[] { -1, 2, 1, 1 };
        for (int fault = 0; fault < fields.Length; fault++)
        {
            string directory = NewDirectory(root, "invalid-" + fault);
            string path = Path.Combine(directory, SaveFormat.FileName);
            string backup = Path.Combine(directory, SaveFormat.BackupName);
            File.WriteAllBytes(backup, good);
            JsonObject document = SaveV1.ParseDocument(good);
            JsonObject payload = document.GetObject("payload", "test");
            payload.GetArray("appearances", "test").GetObject(0, "test").Add(fields[fault], values[fault]);
            JsonObject header = document.GetObject("header", "test");
            byte[] invalid = SaveV1.AssembleDocument(payload, SaveFormat.SchemaVersion,
                header.GetLong("worldId", "test"), header.GetUInt("worldEpoch", "test"),
                header.GetLong("savedAtGameSeconds", "test"));
            File.WriteAllBytes(path, invalid);
            SaveLoadResult result = SaveV1.LoadGame(directory, new EmptyScene());
            Check(!result.Ok && result.Game == null && result.BackupAvailable
                && result.Notice.Contains("payload is invalid")
                && Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(invalid)
                && Convert.ToBase64String(File.ReadAllBytes(backup)) == Convert.ToBase64String(good)
                && SaveV1.LoadBackupGame(directory, new EmptyScene()).Ok,
                (fault < 2 ? "BODY-PRESET-out-of-range-negative-" : "BODY-PRESET-build-height-negative-")
                    + fields[fault] + "=" + values[fault] + " " + result.Notice);
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--reload")
        {
            try { return Reload(args[1], args[2]); }
            catch (Exception e) { Console.WriteLine("FAIL BODY-PRESET-child " + e); return 1; }
        }
        string root = Path.Combine(Path.GetTempPath(), "blockparty-body-preset-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CreatorSelect(root);
            RandomizeAndBaselines(root);
            LegacySave(root);
            InvalidSaves(root);
        }
        catch (Exception e) { Check(false, "BODY-PRESET-unexpected-exception " + e); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "BODY-PRESET ALL PASS" : "BODY-PRESET FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
