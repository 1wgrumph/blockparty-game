// SAVE-V2 TDD: real SaveV1/TitleFlow entry points, isolated-csharp resource group.
// v1-save.json is the BODY-PRESET legacy producer fixture, copied byte-for-byte.
// v2-save.json declares that legacy baseBody default explicitly and advances the
// document version to 2; the world snapshot's independent version stays at 1.
// No editor, network, credentials, shared ports or external process is needed.
// "negative-fixture" substitutes the valid-hash v2 twin missing baseBody:
// the normal v2 acceptance assertion must then print FAIL and exit 1.
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class SaveV2Checks
{
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

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, "tests/production/fixtures/SAVE-V2", name);
            if (File.Exists(path)) return File.ReadAllBytes(path);
            directory = directory.Parent;
        }
        throw new Exception("SAVE-V2 fixture missing: " + name);
    }

    private static string Install(string root, string name, byte[] bytes)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, SaveFormat.FileName), bytes);
        return directory;
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        return Convert.ToBase64String(a) == Convert.ToBase64String(b);
    }

    private static bool SameJson(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
        {
            int count = 0, otherCount = 0;
            foreach (JsonProperty property in expected.EnumerateObject())
            {
                JsonElement found;
                if (!actual.TryGetProperty(property.Name, out found) || !SameJson(property.Value, found)) return false;
                count++;
            }
            foreach (JsonProperty property in actual.EnumerateObject()) otherCount++;
            return count == otherCount;
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength()) return false;
            for (int i = 0; i < expected.GetArrayLength(); i++)
                if (!SameJson(expected[i], actual[i])) return false;
            return true;
        }
        if (expected.ValueKind == JsonValueKind.String) return expected.GetString() == actual.GetString();
        if (expected.ValueKind == JsonValueKind.Number) return expected.GetDecimal() == actual.GetDecimal();
        return true;
    }

    private static void QualifyFixtures()
    {
        foreach (string name in new[] { "v1-save.json", "v2-save.json", "v2-missing-baseBody.json" })
        {
            using (JsonDocument document = JsonDocument.Parse(Fixture(name)))
            using (SHA256 sha = SHA256.Create())
            {
                JsonElement header = document.RootElement.GetProperty("header");
                JsonElement payload = document.RootElement.GetProperty("payload");
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload.GetRawText()));
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                Require(hex == header.GetProperty("sha256").GetString(), name + " independent hash qualification");
                Require(header.GetProperty("schemaVersion").GetInt32() == (name == "v1-save.json" ? 1 : 2), name + " version");
                int index = 0;
                foreach (JsonElement row in payload.GetProperty("appearances").EnumerateArray())
                {
                    JsonElement body;
                    bool present = row.TryGetProperty("baseBody", out body);
                    bool required = name != "v1-save.json" && (name != "v2-missing-baseBody.json" || index != 0);
                    Require(present == required && (!present || body.GetInt32() == 0), name + " migrated field shape");
                    index++;
                }
                Require(index == 8, name + " eight appearance rows");
                Check(true, "SAVE-V2-fixture-qualified-" + name);
            }
        }
    }

    private static GameStack Stack(LoadedGame game)
    {
        return new GameStack(game.World, game.Clock, game.Arbitrator, game.Executor,
            game.Appearances, game.Accounts, game.Food);
    }

    private static void CheckSavedPayload(byte[] saved, int loads, string name)
    {
        JsonObject expected = SaveV1.ParseDocument(Fixture("v2-save.json")).GetObject("payload", "test");
        // SAVE advances the freshness epoch on each load; all other state must survive.
        expected.GetObject("world", "test").Set("epoch", new JsonNumber(loads.ToString()));
        using (JsonDocument oracle = JsonDocument.Parse(SaveV1.RenderJson(expected)))
        using (JsonDocument actual = JsonDocument.Parse(saved))
        {
            Check(actual.RootElement.GetProperty("header").GetProperty("schemaVersion").GetInt32() == 2,
                name + "-writes-schema-v2-capability");
            Check(SameJson(oracle.RootElement, actual.RootElement.GetProperty("payload")), name + "-all-payload-preserved");
        }
    }

    private static void RoundTrip(string root, string name, string fixture)
    {
        byte[] input = Fixture(fixture);
        string directory = Install(root, name, input);
        string path = Path.Combine(directory, SaveFormat.FileName);
        for (int loads = 1; loads <= 2; loads++)
        {
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
            Require(loaded.Ok && loaded.Game != null, name + "-load-v2-migration-capability: " + loaded.Notice);
            Check(loaded.Game.Appearances.Count == 8, name + "-eight-appearances-" + loads);
            foreach (ResidentRecord resident in loaded.Game.World.ResidentsSnapshot())
            {
                AppearanceRecord appearance;
                Check(loaded.Game.Appearances.TryGet(resident.Id, out appearance) && appearance.BaseBody == BaseBody.Male,
                    name + "-migrated-baseBody-no-default-drift-" + (long)resident.Id + "-" + loads);
            }
            Require(SaveV1.SaveGame(Stack(loaded.Game), directory).Ok, name + " save");
            byte[] saved = File.ReadAllBytes(path);
            CheckSavedPayload(saved, loads, name + "-roundtrip-" + loads);
            Require(SaveV1.SaveGame(Stack(loaded.Game), directory).Ok, name + " repeat save");
            Check(SameBytes(saved, File.ReadAllBytes(path)), name + "-repeat-save-byte-stable-" + loads);
        }
    }

    private static void Refuse(string root, string name, byte[] invalid, params string[] noticeParts)
    {
        string directory = Install(root, name, invalid);
        string path = Path.Combine(directory, SaveFormat.FileName);
        byte[] backup = Fixture("v1-save.json");
        string backupPath = Path.Combine(directory, SaveFormat.BackupName);
        File.WriteAllBytes(backupPath, backup);
        SaveLoadResult result = SaveV1.LoadGame(directory, new EmptyScene());
        bool notice = true;
        foreach (string part in noticeParts) notice &= result.Notice.Contains(part);
        Check(!result.Ok && result.Game == null && result.BackupAvailable && notice,
            name + "-refusal-capability: " + result.Notice);
        Check(SameBytes(invalid, File.ReadAllBytes(path)) && SameBytes(backup, File.ReadAllBytes(backupPath))
            && !File.Exists(Path.Combine(directory, SaveFormat.TempName)), name + "-files-untouched");
        Check(SaveV1.LoadBackupGame(directory, new EmptyScene()).Ok, name + "-backup-recoverable");
    }

    private static void RepeatedPlayEntry(string root)
    {
        byte[] prior = null;
        for (int entry = 0; entry < 3; entry++)
        {
            string directory = Install(root, "play-entry-" + entry, Fixture("v1-save.json"));
            TitleFlow flow = new TitleFlow(directory, new EmptyScene());
            Check(flow.Screen == TitleScreen.Title && flow.Game == null && flow.ContinueEnabled,
                "SAVE-V2-play-entry-fresh-controller-" + entry);
            Require(flow.Activate(TitleAction.Continue).Ok && flow.Game != null, "play entry continue");
            Require(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.Save).Ok, "play entry save");
            byte[] saved = File.ReadAllBytes(Path.Combine(directory, SaveFormat.FileName));
            CheckSavedPayload(saved, 1, "SAVE-V2-play-entry-reset-" + entry);
            Check(prior == null || SameBytes(prior, saved), "SAVE-V2-play-entry-no-static-migration-or-id-rng-leak-" + entry);
            prior = saved;
            Require(flow.Activate(TitleAction.ReturnToTitle).Ok, "return to title");
            Check(flow.Screen == TitleScreen.Title, "SAVE-V2-play-entry-return-title-" + entry);
        }
    }

    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-save-v2-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("SAVE-V2-fixtures", QualifyFixtures);
            Run("SAVE-V2-v1-migrates-once", () => RoundTrip(root, "SAVE-V2-v1-migrates-once", "v1-save.json"));
            Run("SAVE-V2-v2-roundtrip", () => RoundTrip(root, "SAVE-V2-v2-roundtrip",
                args.Length == 1 && args[0] == "negative-fixture" ? "v2-missing-baseBody.json" : "v2-save.json"));
            Run("SAVE-V2-unknown-version", () => Refuse(root, "SAVE-V2-unknown-version",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("v2-save.json"))
                    .Replace("\"schemaVersion\":2", "\"schemaVersion\":3")), "version 3", "untouched"));
            Run("SAVE-V2-corrupt-sha", () => Refuse(root, "SAVE-V2-corrupt-sha",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("v2-save.json"))
                    .Replace("\"townFund\":765", "\"townFund\":766")), "sha256 mismatch", "kept"));
            Run("SAVE-V2-v2-missing-migrated-field", () => Refuse(root, "SAVE-V2-v2-missing-migrated-field",
                Fixture("v2-missing-baseBody.json"), "payload", "baseBody"));
            Run("SAVE-V2-repeated-play-entry", () => RepeatedPlayEntry(root));
        }
        catch (Exception e) { Check(false, "SAVE-V2-unexpected " + e); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "SAVE-V2 ALL PASS" : "SAVE-V2 FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
