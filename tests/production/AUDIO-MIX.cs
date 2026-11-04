/*
AUDIO-MIX@1 test contract (plain C# 9, no UnityEngine).
Assets/Blockparty/Audio/MixSettings.cs, namespace Blockparty.Audio:
  public enum AudioGroup { Ambience, World, Voice, Interface }
  public sealed class MixSettings {
    public MixSettings(Blockparty.Contracts.SettingsRecord settings);
    public SettingsRecord Settings { get; }
    public bool TrySetLevel(AudioGroup group, float level);
    public bool TrySetMasterLevel(float level);
    public float EffectiveLevel(AudioGroup group);
  }
Levels are linear [0,1]; effective = MixerLevel * the group's stored level.
Refuse out-of-range/NaN/infinite input with false and all state unchanged.
SettingsRecord retains its existing members and adds readonly float fields
AmbienceLevel, WorldLevel, VoiceLevel, InterfaceLevel. Constructor overloads:
  (int schemaVersion, float mixerLevel, float textScale, bool reducedMotion)
    delegates with every group level = mixerLevel (the planned v1 fallback);
  (int schemaVersion, float mixerLevel, float textScale, bool reducedMotion,
   float ambienceLevel, float worldLevel, float voiceLevel, float interfaceLevel).
SaveV1 persists those four fields; legacy v1 payloads missing all four keys use
MixerLevel for each. Existing SAVE.cs/UI-TITLE.cs require no signature changes.
Developer should extend their settings round-trip/restart assertions to compare
all four fields; retain the four-argument constructor compatibility coverage.

Reflection binds only this not-yet-built public API, so baseline red is an
executable missing-capability FAIL, not a compiler/collection error. All state,
serialization, integrity checks and TitleFlow calls use real product code.
Local projections of SEIT-PRES-04-P1/N1/N2/N3 only. Invalid levels and corrupt
files are task-required extensions, not literal stimuli in those SEIT rows.
Unity bus snapshots, audible output, actual process restart and SEIT-AC-13's
player demonstration remain pending; a fresh instance proves model reload only.
Resource group: isolated-csharp (no editor/display/server); one GUID temp root
per run, separate fault directories, cleanup in finally. No engine qualification.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class AudioMixChecks
{
    private static int failures;
    private static readonly string[] Groups = { "Ambience", "World", "Voice", "Interface" };
    private static readonly float[] Levels = { 0.2f, 0.4f, 0.6f, 0.8f };

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void Run(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            failures++;
            Exception cause = error is TargetInvocationException && error.InnerException != null
                ? error.InnerException : error;
            Console.WriteLine("FAIL " + name + ": " + cause.GetType().Name + ": " + cause.Message);
        }
    }

    private static Type ProductType(string name)
    {
        Type type = typeof(SettingsRecord).Assembly.GetType("Blockparty.Audio." + name);
        Check(type != null, "missing planned Blockparty.Audio." + name);
        return type;
    }

    private static object NewMix(SettingsRecord settings)
    {
        return Activator.CreateInstance(ProductType("MixSettings"), new object[] { settings });
    }

    private static object Group(int index)
    {
        return Enum.Parse(ProductType("AudioGroup"), Groups[index]);
    }

    private static bool Set(object mix, int group, float value)
    {
        return (bool)mix.GetType().GetMethod("TrySetLevel").Invoke(mix, new object[] { Group(group), value });
    }

    private static float Effective(object mix, int group)
    {
        return (float)mix.GetType().GetMethod("EffectiveLevel").Invoke(mix, new object[] { Group(group) });
    }

    private static SettingsRecord Record(float master, float[] levels)
    {
        ConstructorInfo constructor = typeof(SettingsRecord).GetConstructor(new Type[] {
            typeof(int), typeof(float), typeof(float), typeof(bool),
            typeof(float), typeof(float), typeof(float), typeof(float) });
        Check(constructor != null, "missing planned eight-argument SettingsRecord constructor");
        return (SettingsRecord)constructor.Invoke(new object[] {
            SaveV1.SettingsSchemaVersion, master, 1.25f, true,
            levels[0], levels[1], levels[2], levels[3] });
    }

    private static float Stored(SettingsRecord record, int group)
    {
        FieldInfo field = typeof(SettingsRecord).GetField(Groups[group] + "Level");
        Check(field != null, "missing planned SettingsRecord." + Groups[group] + "Level");
        return (float)field.GetValue(record);
    }

    private static SettingsRecord Settings(object mix)
    {
        return (SettingsRecord)mix.GetType().GetProperty("Settings").GetValue(mix);
    }

    private static void SameRecord(SettingsRecord actual, SettingsRecord expected)
    {
        Check(actual.SchemaVersion == expected.SchemaVersion && actual.MixerLevel == expected.MixerLevel
            && actual.TextScale == expected.TextScale && actual.ReducedMotion == expected.ReducedMotion,
            "master or non-audio settings differ");
        for (int i = 0; i < Groups.Length; i++)
            Check(Stored(actual, i) == Stored(expected, i), Groups[i] + " stored level differs");
    }

    private static void LevelsEqual(SettingsRecord record, float value)
    {
        for (int i = 0; i < Groups.Length; i++)
            Check(Stored(record, i) == value, Groups[i] + " did not inherit MixerLevel");
    }

    private static bool SetMaster(object mix, float value)
    {
        return (bool)mix.GetType().GetMethod("TrySetMasterLevel").Invoke(mix, new object[] { value });
    }

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private static string DirectoryFor(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SettingsBytes(string directory)
    {
        return Convert.ToBase64String(File.ReadAllBytes(Path.Combine(directory, SaveV1.SettingsFileName)));
    }

    private static JsonObject LegacyPayload()
    {
        JsonObject payload = new JsonObject();
        payload.Add("mixerLevel", 0.7f);
        payload.Add("textScale", 1.25f);
        payload.Add("reducedMotion", true);
        return payload;
    }

    private static void IndependentGroup(int changed)
    {
        object mix = NewMix(new SettingsRecord(1, 1f, 1f, false));
        Check(Set(mix, changed, 0.25f), "valid group level refused");
        for (int i = 0; i < Groups.Length; i++)
        {
            Check(Math.Abs(Effective(mix, i) - (i == changed ? 0.25f : 1f)) < 0.000001f,
                Groups[i] + " effective level changed incorrectly");
            Check(Stored(Settings(mix), i) == (i == changed ? 0.25f : 1f),
                Groups[i] + " stored level changed incorrectly");
        }
        Check(Settings(mix).MixerLevel == 1f, "group changed master");
    }

    private static void MasterScaling()
    {
        SettingsRecord initial = Record(1f, Levels);
        object mix = NewMix(initial);
        foreach (float master in new float[] { 0.5f, 0f, 1f })
        {
            Check(SetMaster(mix, master), "valid master level refused");
            SameRecord(Settings(mix), Record(master, Levels));
            for (int i = 0; i < Groups.Length; i++)
                Check(Math.Abs(Effective(mix, i) - master * Levels[i]) < 0.000001f,
                    Groups[i] + " does not scale with master");
        }
    }

    private static void RefuseInvalid(string root, int target, float value, string name)
    {
        SettingsRecord initial = Record(0.5f, Levels);
        object mix = NewMix(initial);
        bool accepted = target == 4 ? SetMaster(mix, value) : Set(mix, target, value);
        Check(!accepted, "invalid level accepted");
        SameRecord(Settings(mix), initial);
        for (int i = 0; i < Groups.Length; i++)
            Check(Math.Abs(Effective(mix, i) - 0.5f * Levels[i]) < 0.000001f,
                Groups[i] + " effective level changed after refusal");

        string directory = DirectoryFor(root, name);
        Check(SaveV1.SaveSettings(initial, directory).Ok, "initial settings write failed");
        string before = SettingsBytes(directory);
        float[] invalidLevels = (float[])Levels.Clone();
        if (target < 4) invalidLevels[target] = value;
        SettingsRecord invalid = Record(target == 4 ? value : 0.5f, invalidLevels);
        Check(!SaveV1.SaveSettings(invalid, directory).Ok, "invalid persisted level accepted");
        Check(SettingsBytes(directory) == before, "refused write changed settings bytes");
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(loaded.Ok, "refused write damaged previous settings");
        SameRecord(loaded.Settings, initial);
    }

    private static void Persistence(string root)
    {
        string directory = DirectoryFor(root, "restart");
        SettingsRecord expected = Record(0.5f, Levels);
        TitleFlow flow = new TitleFlow(directory, new EmptyScene());
        Check(flow.Activate(TitleAction.Settings).Ok && flow.ApplySettings(expected).Ok,
            "TitleFlow settings apply failed");
        SameRecord(flow.Settings, expected);
        SettingsLoadResult disk = SaveV1.LoadSettings(directory);
        Check(disk.Ok && disk.UnsupportedFields.Length == 0, "four level keys not recognized");
        SameRecord(disk.Settings, expected);
        TitleFlow restarted = new TitleFlow(directory, new EmptyScene());
        SameRecord(restarted.Settings, expected);
        object restored = NewMix(restarted.Settings);
        for (int i = 0; i < Groups.Length; i++)
            Check(Math.Abs(Effective(restored, i) - 0.5f * Levels[i]) < 0.000001f,
                Groups[i] + " effective level reset on reload");
    }

    private static void LegacyMigration(string root, bool unknown)
    {
        string directory = DirectoryFor(root, unknown ? "unsupported" : "legacy");
        JsonObject payload = LegacyPayload();
        if (unknown) payload.Add("futureAudioMode", new JsonString("newer-client"));
        // Real v1 document envelope/hash around a payload with no new keys.
        JsonObject document = SaveV1.ParseDocument(SaveV1.AssembleSettingsDocument(payload));
        document.GetObject("header", "settings").Set("schemaVersion", new JsonNumber("1"));
        File.WriteAllText(Path.Combine(directory, SaveV1.SettingsFileName), SaveV1.RenderJson(document));
        string before = SettingsBytes(directory);
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(loaded.Ok && loaded.Settings.MixerLevel == 0.7f
            && loaded.Settings.TextScale == 1.25f && loaded.Settings.ReducedMotion,
            "recognized v1 values lost");
        Check(loaded.UnsupportedFields.Length == (unknown ? 1 : 0), "wrong unsupported field report");
        if (unknown) Check(loaded.UnsupportedFields[0] == "futureAudioMode"
            && !string.IsNullOrWhiteSpace(loaded.Detail), "unsupported field not named/reported");
        LevelsEqual(loaded.Settings, 0.7f);
        Check(SettingsBytes(directory) == before, "load rewrote legacy/unsupported bytes");
    }

    private static void CorruptFallback(string root, bool integrity)
    {
        string directory = DirectoryFor(root, integrity ? "bad-hash" : "bad-json");
        Check(SaveV1.SaveSettings(new SettingsRecord(1, 0.7f, 1.25f, true), directory).Ok,
            "valid settings setup failed");
        string path = Path.Combine(directory, SaveV1.SettingsFileName);
        if (integrity)
        {
            JsonObject document = SaveV1.ParseDocument(File.ReadAllBytes(path));
            document.GetObject("payload", "settings").Set("mixerLevel", new JsonNumber("0.3"));
            File.WriteAllText(path, SaveV1.RenderJson(document)); // retain stale hash
        }
        else File.WriteAllText(path, "{truncated");
        string before = SettingsBytes(directory);
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(!loaded.Ok && !string.IsNullOrWhiteSpace(loaded.Detail), "corruption not reported");
        if (integrity) Check(loaded.Detail.Contains("hash"), "wrong failure: expected hash mismatch");
        TitleFlow restarted = new TitleFlow(directory, new EmptyScene());
        Check(restarted.Settings.MixerLevel == 1f && restarted.Settings.TextScale == 1f
            && !restarted.Settings.ReducedMotion, "TitleFlow did not fall back to defaults");
        Check(SettingsBytes(directory) == before, "corrupt input was overwritten");
        LevelsEqual(restarted.Settings, 1f);
    }

    private static void PausedWorld(string root)
    {
        TitleFlow flow = new TitleFlow(DirectoryFor(root, "paused"), new EmptyScene());
        Check(flow.Activate(TitleAction.NewGame).Ok && flow.PrepareNewGame(7L, 4242L,
            new string[] { "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight" }).Ok
            && flow.BeginPlay().Ok && flow.Activate(TitleAction.Pause).Ok, "paused world setup failed");
        int before = flow.Game.World.ComputeStateHash();
        Check(flow.Activate(TitleAction.Settings).Ok, "paused settings screen refused");
        SettingsRecord expected = Record(0.5f, Levels);
        Check(flow.ApplySettings(expected).Ok, "paused settings apply failed");
        SameRecord(flow.Settings, expected);
        Check(flow.Game.Clock.Paused && flow.Game.World.ComputeStateHash() == before,
            "settings mutated/resumed paused world");
    }

    private static void RefuseOutOfRangeLoad(string root, string key, float value, string name)
    {
        // M2-B1-R2: a correctly hashed document carrying an out-of-domain
        // level must fail to load through the existing failure path, exactly
        // as SaveSettings refuses to write it.
        string directory = DirectoryFor(root, name);
        JsonObject payload = new JsonObject();
        payload.Add("mixerLevel", key == "mixerLevel" ? value : 1f);
        payload.Add("textScale", 1.25f);
        payload.Add("reducedMotion", false);
        payload.Add("ambienceLevel", key == "ambienceLevel" ? value : 1f);
        payload.Add("worldLevel", key == "worldLevel" ? value : 1f);
        payload.Add("voiceLevel", key == "voiceLevel" ? value : 1f);
        payload.Add("interfaceLevel", key == "interfaceLevel" ? value : 1f);
        File.WriteAllBytes(Path.Combine(directory, SaveV1.SettingsFileName),
            SaveV1.AssembleSettingsDocument(payload));
        string before = SettingsBytes(directory);
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(!loaded.Ok, "hashed out-of-range " + key + " loaded");
        Check(loaded.Detail.Contains("within [0, 1]"), "wrong failure: expected level domain refusal");
        TitleFlow restarted = new TitleFlow(directory, new EmptyScene());
        Check(restarted.Settings.MixerLevel == 1f && restarted.Settings.TextScale == 1f
            && !restarted.Settings.ReducedMotion, "TitleFlow did not fall back to defaults");
        LevelsEqual(restarted.Settings, 1f);
        Check(SettingsBytes(directory) == before, "refused load rewrote settings bytes");
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-audio-mix-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < Groups.Length; i++)
            {
                int group = i;
                Run("SEIT-PRES-04-P1-independent-" + Groups[group], () => IndependentGroup(group));
            }
            Run("SEIT-PRES-04-N1-voice-zero-others-full", () => {
                object mix = NewMix(new SettingsRecord(1, 1f, 1f, false));
                Check(Set(mix, 2, 0f), "voice mute refused");
                for (int i = 0; i < Groups.Length; i++)
                    Check(Effective(mix, i) == (i == 2 ? 0f : 1f), "mute affected " + Groups[i]);
            });
            Run("SEIT-PRES-04-P1-master-scales-all-groups", MasterScaling);
            Run("SEIT-PRES-04-P1-settings-and-fresh-instance-roundtrip", () => Persistence(root));
            Run("SEIT-PRES-04-P1-legacy-constructor-delegates", () =>
                LevelsEqual(new SettingsRecord(1, 0.7f, 1.25f, true), 0.7f));
            Run("SEIT-PRES-04-N2-v1-missing-keys-inherit-master", () => LegacyMigration(root, false));
            Run("SEIT-PRES-04-N2-unsupported-field-reported-recognized-values-preserved",
                () => LegacyMigration(root, true));
            Run("SEIT-PRES-04-N2-corrupt-json-defaults-by-existing-rule", () => CorruptFallback(root, false));
            Run("SEIT-PRES-04-N2-corrupt-hash-defaults-by-existing-rule", () => CorruptFallback(root, true));
            Run("SEIT-PRES-04-N3-settings-preserve-paused-world-hash", () => PausedWorld(root));
            float[] invalid = { -0.01f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            string[] invalidNames = { "below-zero", "above-one", "nan", "positive-infinity", "negative-infinity" };
            for (int target = 0; target <= Groups.Length; target++)
                for (int value = 0; value < invalid.Length; value++)
                {
                    int t = target, v = value;
                    string name = (t == 4 ? "Master" : Groups[t]) + "-" + invalidNames[v];
                    Run("SEIT-PRES-04-P1-invalid-level-refused-unchanged-" + name,
                        () => RefuseInvalid(root, t, invalid[v], name));
                }
            string[] loadKeys = { "mixerLevel", "ambienceLevel", "worldLevel",
                "voiceLevel", "interfaceLevel" };
            float[] loadValues = { 2f, -1f };
            string[] loadNames = { "above-one", "below-zero" };
            for (int key = 0; key < loadKeys.Length; key++)
                for (int value = 0; value < loadValues.Length; value++)
                {
                    int k = key, v = value;
                    string name = loadKeys[k] + "-" + loadNames[v];
                    Run("SEIT-PRES-04-N2-hashed-out-of-range-level-refused-on-load-" + name,
                        () => RefuseOutOfRangeLoad(root, loadKeys[k], loadValues[v], name));
                }
        }
        finally { Directory.Delete(root, true); }
        if (failures == 0) Console.WriteLine("AUDIO-MIX ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
