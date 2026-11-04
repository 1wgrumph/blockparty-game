/*
ACCESS@1 test contract, Assets/Blockparty/UI/Accessibility.cs:
namespace Blockparty.UI, plain C# 9, no UnityEngine:
  public sealed class Accessibility {
    public Accessibility(Blockparty.Contracts.SettingsRecord settings);
    public SettingsRecord Settings { get; }
    public bool TrySetTextScale(float scale);          // inclusive [1, 1.5]
    public float ScaledTextSize(float unscaledSize);   // size * TextScale
    public bool TryRemap(string action, string key);   // reject conflict/invalid
    public string BindingFor(string action);
    public string ResolveInput(string key);           // action or empty string
    public void SetNonColorCues(bool enabled);
    public string NonColorIndicator(string status);   // additive marker, not label
    public void SetCaptions(bool enabled);
    public string CaptionFor(string catalogCaption);  // projects real cue caption
    public void SetReducedMotion(bool enabled);
  }
Default bindings exercised here: Pause=Escape, OpenMap=M; overrides Pause=P,
OpenMap=Tab. Unknown actions and empty/blank keys are invalid. A key already
bound to another action is refused (M2 TE contract), without changing any state.
ResolveInput must use the active bindings, including removal of the old key.
NonColorIndicator is an additional marker: empty when off, contains the supplied
status name when on. Existing status labels remain the consumer's responsibility.
CaptionFor returns the catalog caption when enabled, empty when disabled.
No plain-C# easing/shake model exists at ff856fb: reduced motion is checked in
the setting, as authorized by the task; SLICE owns in-scene effect evidence.

SettingsRecord retains ALL existing members and both constructors, adding:
  public readonly bool NonColorCues, Captions;
  public readonly System.Collections.Generic.IReadOnlyDictionary<string,string> InputRemaps;
New overload (existing eight arguments followed by the three new fields):
  (int schemaVersion, float mixerLevel, float textScale, bool reducedMotion,
   float ambienceLevel, float worldLevel, float voiceLevel, float interfaceLevel,
   bool nonColorCues, bool captions, IReadOnlyDictionary<string,string> inputRemaps).
The four-argument constructor still supplies MixerLevel for every audio group;
the eight-argument one preserves explicit group levels. Both delegate to the
new overload with NonColorCues=true, Captions=true, and an empty remap map.
SDoc BP-PRES-05 supplies the candidate 100..150% range, not new-flag defaults or
binding names: enabled essential cues and the bindings above are this task's
additive developer contract, not a claim of additional normative SDoc text.
TextScale and ReducedMotion already exist: reuse them, do not add parallel fields.
SaveV1 writes/recognizes nonColorCues, captions and inputRemaps (JSON object of
action:key overrides); absent keys use the constructor defaults above. Keep all
recognized old values, four AUDIO-MIX levels, unknown-field reports and schema 1.
Invalid settings writes refuse before changing disk; corrupted bytes remain,
LoadSettings reports failure and TitleFlow uses its existing default fallback.

Required SAVE.cs additions by Product code (do not edit in this task):
1. Retain existing four-argument settings-roundtrip/group-level assertions.
2. Add settings-access-roundtrip using the 11-argument record (master .7,
   scale 1.5, reduced=true, groups .2/.4/.6/.8, flags false/false, Pause=P,
   OpenMap=Tab); assert every field/map entry and zero UnsupportedFields.
3. Add settings-access-survive-world: load another real world, reload settings,
   assert the SAME complete record and unchanged world hash.
4. Add settings-access-invalid-unchanged: .99/1.51 scales and Pause=Tab refuse,
   retain exact file bytes and previously loadable complete settings.
5. Add settings-access-legacy-defaults using fixtures/ACCESS/audio-mix-v1.json:
   preserve .7/1.25/true and all four distinct audio levels, default new fields.
   Existing unsupported "remap" fixture stays unsupported (new key inputRemaps).
Required UI-TITLE.cs additions by Product code:
1. Retain existing four-argument title/pause/restart checks.
2. Add SEIT-PRES-05-N2-title-access-file-and-restart: ApplySettings the same full
   record, compare flow.Settings, LoadSettings and a fresh TitleFlow.Settings.
3. Add SEIT-PRES-05-N2-title-access-world-load for both Continue and explicit
   LoadSavedGame: all settings and remaps survive the real world transition.
4. Add SEIT-PRES-05-N1-title-invalid-access-unchanged: invalid scale/remap
   returns !Ok with nonblank Reason; same complete Settings, screen, disk bytes,
   paused world hash and Clock.Paused. Valid changes also preserve paused world.
Other callers inventoried: SaveV1; TitleFlow.ApplySettings/DefaultSettings;
MixSettings.TrySetMasterLevel/With rebuild records through the eight-argument
overload (preserving future ACCESS fields during audio edits needs product-side
caller reconciliation); Presentation.AudioMixer only reads settings. UI-INSPECT,
UI-NAV and AUDIO-MIX remain unmodified witnesses, not proof of that future edit.

Local SEIT projections only: N1 effect, N2 persistence, N3 non-color model;
P2 caption catalog. Invalid inputs/corrupt files are task-required extensions,
not literal stimuli of those SEIT rows. Whole PRES-02/PRES-05/AC-13 cases still
require packaged rendering, actual process restart, player/observer evidence and
the full activity/feeling catalog. No such acceptance is claimed here.
Reflection only binds the planned API, following AUDIO-MIX.cs: missing capability
is a named runtime FAIL, not a compilation failure. Everything else uses real
SaveV1, TitleFlow, worlds and cues.json. The legacy fixture was emitted by the
baseline SaveV1 writer. No stubs. Resource group isolated-csharp; pid+GUID owned
temp root, independent fault directories, cleanup in finally; no editor/display.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Blockparty.Audio;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class AccessChecks
{
    private static int failures;

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

    private static object Model(SettingsRecord settings)
    {
        Type type = typeof(SettingsRecord).Assembly.GetType("Blockparty.UI.Accessibility");
        Check(type != null, "missing planned Blockparty.UI.Accessibility");
        return Activator.CreateInstance(type, new object[] { settings });
    }

    private static object Call(object model, string name, params object[] args)
    {
        MethodInfo method = model.GetType().GetMethod(name);
        Check(method != null, "missing planned Accessibility." + name);
        return method.Invoke(model, args);
    }

    private static SettingsRecord Settings(object model)
    {
        PropertyInfo property = model.GetType().GetProperty("Settings");
        Check(property != null, "missing planned Accessibility.Settings");
        return (SettingsRecord)property.GetValue(model);
    }

    private static T Field<T>(SettingsRecord settings, string name)
    {
        FieldInfo field = typeof(SettingsRecord).GetField(name);
        Check(field != null, "missing planned SettingsRecord." + name);
        return (T)field.GetValue(settings);
    }

    private static IReadOnlyDictionary<string, string> Remaps(SettingsRecord settings)
    {
        var remaps = Field<IReadOnlyDictionary<string, string>>(settings, "InputRemaps");
        Check(remaps != null, "InputRemaps must be an empty map, not null, when no overrides exist");
        return remaps;
    }

    private static SettingsRecord Record(bool enabled, IReadOnlyDictionary<string, string> remaps = null)
    {
        ConstructorInfo constructor = typeof(SettingsRecord).GetConstructor(new Type[] {
            typeof(int), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(float),
            typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(IReadOnlyDictionary<string, string>) });
        Check(constructor != null, "missing planned eleven-argument SettingsRecord constructor");
        return (SettingsRecord)constructor.Invoke(new object[] { 1, 0.7f, 1.5f, true,
            0.2f, 0.4f, 0.6f, 0.8f, enabled, enabled,
            remaps ?? new Dictionary<string, string> { { "Pause", "P" }, { "OpenMap", "Tab" } } });
    }

    private static void Same(SettingsRecord actual, SettingsRecord expected)
    {
        Check(actual.SchemaVersion == expected.SchemaVersion && actual.MixerLevel == expected.MixerLevel
            && actual.TextScale == expected.TextScale && actual.ReducedMotion == expected.ReducedMotion
            && actual.AmbienceLevel == expected.AmbienceLevel && actual.WorldLevel == expected.WorldLevel
            && actual.VoiceLevel == expected.VoiceLevel && actual.InterfaceLevel == expected.InterfaceLevel,
            "existing settings or AUDIO-MIX levels changed");
        Check(Field<bool>(actual, "NonColorCues") == Field<bool>(expected, "NonColorCues")
            && Field<bool>(actual, "Captions") == Field<bool>(expected, "Captions"), "cue flags changed");
        var a = Remaps(actual); var e = Remaps(expected);
        Check(a.Count == e.Count, "remap count changed");
        foreach (var pair in e)
            Check(a.TryGetValue(pair.Key, out string key) && key == pair.Value, "remap changed: " + pair.Key);
    }

    private static SettingsRecord Legacy()
    {
        return new SettingsRecord(1, 0.7f, 1.25f, true, 0.2f, 0.4f, 0.6f, 0.8f);
    }

    private static void NewDefaults(SettingsRecord settings)
    {
        Check(Field<bool>(settings, "NonColorCues") && Field<bool>(settings, "Captions"),
            "new essential cue defaults must be enabled");
        Check(Remaps(settings).Count == 0, "default remap map must have no overrides");
    }

    private static string DirectoryFor(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Bytes(string directory)
    {
        return Convert.ToBase64String(File.ReadAllBytes(Path.Combine(directory, SaveV1.SettingsFileName)));
    }

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private static TitleFlow Paused(string directory)
    {
        TitleFlow flow = new TitleFlow(directory, new EmptyScene());
        Check(flow.Activate(TitleAction.NewGame).Ok && flow.PrepareNewGame(7L, 4242L, null).Ok
            && flow.BeginPlay().Ok && flow.Activate(TitleAction.Pause).Ok, "real paused world setup failed");
        return flow;
    }

    private static void TextScale(float scale)
    {
        object model = Model(Legacy());
        Check((bool)Call(model, "TrySetTextScale", scale), "in-range text scale refused");
        Check(Settings(model).TextScale == scale && (float)Call(model, "ScaledTextSize", 16f) == 16f * scale,
            "scale stored without changing text-size output");
        object original = Model(Legacy());
        if (scale != 1.25f) Check((float)Call(model, "ScaledTextSize", 16f)
            != (float)Call(original, "ScaledTextSize", 16f), "text-size output did not change");
    }

    private static void InvalidScaleModel(float scale)
    {
        SettingsRecord initial = Legacy();
        object model = Model(initial);
        Check(!(bool)Call(model, "TrySetTextScale", scale), "invalid text scale accepted");
        Same(Settings(model), initial);
        Check((float)Call(model, "ScaledTextSize", 16f) == 20f, "refusal changed text output");
    }

    private static void InvalidScaleDisk(string root, float scale, string name)
    {
        string directory = DirectoryFor(root, "invalid-scale-" + name);
        TitleFlow flow = Paused(directory);
        Check(flow.Activate(TitleAction.Settings).Ok && flow.ApplySettings(Legacy()).Ok, "settings setup failed");
        string before = Bytes(directory); int hash = flow.Game.World.ComputeStateHash();
        TitleFlowResult result = flow.ApplySettings(new SettingsRecord(1, 0.7f, scale, true,
            0.2f, 0.4f, 0.6f, 0.8f));
        Check(!result.Ok && !string.IsNullOrWhiteSpace(result.Reason), "invalid text scale persisted");
        Check(Bytes(directory) == before && flow.Settings.TextScale == 1.25f
            && flow.Screen == TitleScreen.Settings && flow.Game.Clock.Paused
            && flow.Game.World.ComputeStateHash() == hash, "refusal changed disk, settings or paused world");
        Check(SaveV1.LoadSettings(directory).Ok, "refusal damaged previous settings");
    }

    private static void InputRemap()
    {
        object model = Model(Legacy());
        Check((string)Call(model, "BindingFor", "Pause") == "Escape"
            && (string)Call(model, "ResolveInput", "Escape") == "Pause", "default Pause input not active");
        Check((bool)Call(model, "TryRemap", "Pause", "P"), "valid remap refused");
        Check((string)Call(model, "BindingFor", "Pause") == "P"
            && (string)Call(model, "ResolveInput", "P") == "Pause"
            && (string)Call(model, "ResolveInput", "Escape") == string.Empty,
            "remap did not replace the effective input binding");
        Check(Remaps(Settings(model)).TryGetValue("Pause", out string key) && key == "P", "remap not recorded");
        Check((string)Call(model, "ResolveInput", "M") == "OpenMap", "remap changed another essential input");
    }

    private static void InvalidRemap(string root, string action, string key, string name)
    {
        object model = Model(Record(false));
        Check(!(bool)Call(model, "TryRemap", action, key), "conflicting/invalid remap accepted");
        // A separately constructed oracle catches in-place mutation of the remap map.
        Same(Settings(model), Record(false));
        Check((string)Call(model, "ResolveInput", "P") == "Pause"
            && (string)Call(model, "ResolveInput", "Tab") == "OpenMap", "refusal changed effective bindings");
        string directory = DirectoryFor(root, "invalid-remap-" + name);
        SettingsRecord initial = Record(false);
        Check(SaveV1.SaveSettings(initial, directory).Ok, "settings setup failed");
        string before = Bytes(directory);
        var invalid = new Dictionary<string, string> { { "Pause", "P" }, { "OpenMap", "Tab" } };
        invalid[action] = key;
        Check(!SaveV1.SaveSettings(Record(false, invalid), directory).Ok, "invalid remap persisted");
        Check(Bytes(directory) == before, "invalid remap write changed bytes");
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(loaded.Ok, "invalid remap write damaged previous settings");
        Same(loaded.Settings, initial);
    }

    private static void NonColorCues()
    {
        object model = Model(Legacy());
        foreach (Need need in Enum.GetValues(typeof(Need)))
        {
            string status = need.ToString();
            Call(model, "SetNonColorCues", false);
            Check((string)Call(model, "NonColorIndicator", status) == string.Empty, "additional marker did not turn off");
            Call(model, "SetNonColorCues", true);
            string indicator = (string)Call(model, "NonColorIndicator", status);
            Check(!string.IsNullOrWhiteSpace(indicator) && indicator.Contains(status),
                "need has no named non-color indicator: " + status);
        }
        Check(Field<bool>(Settings(model), "NonColorCues"), "non-color toggle not recorded");
    }

    private static void Captions(bool project)
    {
        object model = project ? Model(Legacy()) : null;
        using (JsonDocument catalog = JsonDocument.Parse(File.ReadAllBytes("Assets/Blockparty/Audio/cues.json")))
        {
            int count = 0;
            // Catalog has no essential flag: conservatively cover every real entry.
            foreach (JsonProperty cue in catalog.RootElement.GetProperty("cues").EnumerateObject())
            {
                count++;
                string caption = cue.Value.GetProperty("visual").GetProperty("caption").GetString();
                Check(!string.IsNullOrWhiteSpace(caption), "missing essential caption: " + cue.Name);
                if (!project) continue;
                Call(model, "SetCaptions", false);
                Check((string)Call(model, "CaptionFor", caption) == string.Empty, "caption did not turn off: " + cue.Name);
                Call(model, "SetCaptions", true);
                Check((string)Call(model, "CaptionFor", caption) == caption, "caption did not take effect: " + cue.Name);
            }
            Check(count > 0, "empty cue catalog cannot prove essential captions");
        }
        if (project) Check(Field<bool>(Settings(model), "Captions"), "caption toggle not recorded");
    }

    private static void ReducedMotion()
    {
        object model = Model(new SettingsRecord(1, 0.7f, 1f, false));
        Call(model, "SetReducedMotion", true);
        Check(Settings(model).ReducedMotion, "reduced motion did not turn on");
        Call(model, "SetReducedMotion", false);
        Check(!Settings(model).ReducedMotion, "reduced motion did not turn off");
    }

    private static void Persistence(string root, bool enabled, bool useContinue)
    {
        string directory = DirectoryFor(root, "restart-" + enabled + "-" + useContinue);
        TitleFlow flow = Paused(directory);
        int hash = flow.Game.World.ComputeStateHash();
        SettingsRecord expected = Record(enabled);
        object changed = Model(Legacy());
        Check((bool)Call(changed, "TrySetTextScale", 1.5f)
            && (bool)Call(changed, "TryRemap", "Pause", "P")
            && (bool)Call(changed, "TryRemap", "OpenMap", "Tab"), "valid control changes refused");
        Call(changed, "SetNonColorCues", enabled);
        Call(changed, "SetCaptions", enabled);
        Call(changed, "SetReducedMotion", true);
        Same(Settings(changed), expected);
        Check(flow.Activate(TitleAction.Settings).Ok && flow.ApplySettings(Settings(changed)).Ok, "settings apply failed");
        Same(flow.Settings, expected);
        Check(flow.Game.Clock.Paused && flow.Game.World.ComputeStateHash() == hash, "settings changed paused world");
        SettingsLoadResult disk = SaveV1.LoadSettings(directory);
        Check(disk.Ok && disk.UnsupportedFields.Length == 0, "ACCESS keys not recognized on reload");
        Same(disk.Settings, expected);
        Check(flow.Activate(TitleAction.Back).Ok && flow.Activate(TitleAction.Save).Ok, "world save failed");
        TitleFlow restarted = new TitleFlow(directory, new EmptyScene());
        Same(restarted.Settings, expected);
        if (useContinue) Check(restarted.Activate(TitleAction.Continue).Ok, "real Continue failed");
        else Check(restarted.Activate(TitleAction.Load).Ok && restarted.LoadSavedGame().Ok, "real world load failed");
        Check(restarted.Screen == TitleScreen.Playing && restarted.Game.World.ResidentCount == 8, "world not loaded");
        Same(restarted.Settings, expected);
        object restored = Model(restarted.Settings);
        Check((float)Call(restored, "ScaledTextSize", 16f) == 24f
            && (string)Call(restored, "ResolveInput", "P") == "Pause"
            && (string)Call(restored, "ResolveInput", "Tab") == "OpenMap", "effect lost on world load");
        string indicator = (string)Call(restored, "NonColorIndicator", "Hunger");
        Check(enabled ? indicator.Contains("Hunger") : indicator == string.Empty, "non-color effect lost on world load");
        Check((string)Call(restored, "CaptionFor", "Meal finished") == (enabled ? "Meal finished" : string.Empty),
            "caption effect lost on world load");
        Check(Settings(restored).ReducedMotion, "reduced motion reset on world load");
    }

    private static void LegacyMigration(string root, bool unsupported)
    {
        string directory = DirectoryFor(root, "legacy-" + unsupported);
        byte[] fixture = File.ReadAllBytes("tests/production/fixtures/ACCESS/audio-mix-v1.json");
        if (unsupported)
        {
            JsonObject document = SaveV1.ParseDocument(fixture);
            JsonObject payload = document.GetObject("payload", "legacy");
            payload.Add("futureAccessMode", new JsonString("newer-client"));
            fixture = SaveV1.AssembleSettingsDocument(payload);
        }
        File.WriteAllBytes(Path.Combine(directory, SaveV1.SettingsFileName), fixture);
        string before = Bytes(directory);
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(loaded.Ok, "baseline-produced settings refused: " + loaded.Detail);
        Check(loaded.Settings.MixerLevel == 0.7f && loaded.Settings.TextScale == 1.25f
            && loaded.Settings.ReducedMotion && loaded.Settings.AmbienceLevel == 0.2f
            && loaded.Settings.WorldLevel == 0.4f && loaded.Settings.VoiceLevel == 0.6f
            && loaded.Settings.InterfaceLevel == 0.8f, "migration changed recognized values or AUDIO-MIX levels");
        Check(loaded.UnsupportedFields.Length == (unsupported ? 1 : 0), "wrong unsupported field count");
        if (unsupported) Check(loaded.UnsupportedFields[0] == "futureAccessMode"
            && !string.IsNullOrWhiteSpace(loaded.Detail), "unsupported key not reported");
        Check(Bytes(directory) == before, "migration rewrote original file");
        NewDefaults(loaded.Settings);
        Same(new TitleFlow(directory, new EmptyScene()).Settings, loaded.Settings);
    }

    private static void CorruptSettings(string root, bool hashMismatch)
    {
        string directory = DirectoryFor(root, "corrupt-" + hashMismatch);
        Check(SaveV1.SaveSettings(Legacy(), directory).Ok, "valid settings setup failed");
        TitleFlow active = new TitleFlow(directory, new EmptyScene());
        string path = Path.Combine(directory, SaveV1.SettingsFileName);
        if (hashMismatch)
        {
            JsonObject document = SaveV1.ParseDocument(File.ReadAllBytes(path));
            document.GetObject("payload", "settings").Set("textScale", new JsonNumber("1.5"));
            File.WriteAllText(path, SaveV1.RenderJson(document)); // Keep stale integrity hash.
        }
        else File.WriteAllText(path, "{truncated");
        string before = Bytes(directory);
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(!loaded.Ok && !string.IsNullOrWhiteSpace(loaded.Detail), "corruption not reported");
        if (hashMismatch) Check(loaded.Detail.Contains("hash"), "wrong failure: expected hash mismatch");
        TitleFlow restarted = new TitleFlow(directory, new EmptyScene());
        Check(restarted.Settings.SchemaVersion == 1 && restarted.Settings.MixerLevel == 1f
            && restarted.Settings.TextScale == 1f && !restarted.Settings.ReducedMotion
            && restarted.Settings.AmbienceLevel == 1f && restarted.Settings.WorldLevel == 1f
            && restarted.Settings.VoiceLevel == 1f && restarted.Settings.InterfaceLevel == 1f,
            "existing corrupt-file default fallback changed");
        Check(active.Settings.TextScale == 1.25f && Bytes(directory) == before, "corrupt input or active settings changed");
        NewDefaults(restarted.Settings);
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-access-"
            + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f })
            {
                float value = scale;
                Run("SEIT-PRES-05-N1-text-scale-effect-" + value, () => TextScale(value));
            }
            Run("SEIT-PRES-05-N1-remap-changes-effective-input", InputRemap);
            Run("SEIT-PRES-05-N3-SEIT-PRES-02-N1-named-indicator-every-need", NonColorCues);
            Run("SEIT-PRES-05-P2-every-catalog-cue-has-caption", () => Captions(false));
            Run("SEIT-PRES-05-N1-essential-caption-toggle-effect", () => Captions(true));
            Run("SEIT-PRES-05-N1-reduced-motion-setting-effect", ReducedMotion);
            foreach (bool enabled in new[] { false, true })
                foreach (bool useContinue in new[] { false, true })
                {
                    bool e = enabled, c = useContinue;
                    Run("SEIT-PRES-05-N2-all-settings-restart-world-" + (c ? "continue-" : "load-") + e,
                        () => Persistence(root, e, c));
                }
            Run("SEIT-PRES-05-N2-four-argument-constructor-defaults", () => {
                var record = new SettingsRecord(1, 0.7f, 1.25f, true);
                Check(record.MixerLevel == 0.7f && record.TextScale == 1.25f && record.ReducedMotion
                    && record.AmbienceLevel == 0.7f && record.WorldLevel == 0.7f
                    && record.VoiceLevel == 0.7f && record.InterfaceLevel == 0.7f, "old constructor changed");
                NewDefaults(record);
            });
            Run("SEIT-PRES-05-N2-eight-argument-constructor-defaults", () => NewDefaults(Legacy()));
            Run("SEIT-PRES-05-N2-volume-change-preserves-access", () => {
                MixSettings mix = new MixSettings(Record(false));
                Check(mix.TrySetMasterLevel(0.1f), "valid master change refused");
                Check(mix.TrySetLevel(AudioGroup.Voice, 0.9f), "valid group change refused");
                SettingsRecord after = mix.Settings;
                Check(after.MixerLevel == 0.1f && after.VoiceLevel == 0.9f, "volume change not applied");
                Check(Field<bool>(after, "NonColorCues") == false
                    && Field<bool>(after, "Captions") == false, "volume change reset essential cue flags");
                var remaps = Remaps(after);
                string pause, openMap;
                Check(remaps.Count == 2 && remaps.TryGetValue("Pause", out pause) && pause == "P"
                    && remaps.TryGetValue("OpenMap", out openMap) && openMap == "Tab",
                    "volume change reset input remaps");
            });
            Run("SEIT-PRES-05-N2-legacy-missing-new-keys-audio-intact", () => LegacyMigration(root, false));
            Run("SEIT-PRES-05-N2-legacy-unknown-field-reported", () => LegacyMigration(root, true));
            Run("SEIT-PRES-05-N2-corrupt-json-existing-fallback", () => CorruptSettings(root, false));
            Run("SEIT-PRES-05-N2-corrupt-hash-existing-fallback", () => CorruptSettings(root, true));
            float[] invalid = { 0.99f, 1.51f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            string[] names = { "below-100", "above-150", "nan", "positive-infinity", "negative-infinity" };
            for (int i = 0; i < invalid.Length; i++)
            {
                float value = invalid[i]; string name = names[i];
                Run("SEIT-PRES-05-N1-invalid-scale-model-unchanged-" + name, () => InvalidScaleModel(value));
                Run("SEIT-PRES-05-N2-invalid-scale-file-unchanged-" + name, () => InvalidScaleDisk(root, value, name));
            }
            Run("SEIT-PRES-05-N1-SEIT-PRES-05-N2-conflicting-remap-unchanged", () => InvalidRemap(root, "Pause", "Tab", "conflict"));
            Run("SEIT-PRES-05-N1-SEIT-PRES-05-N2-unknown-action-remap-unchanged", () => InvalidRemap(root, "UnknownAction", "F1", "unknown"));
            Run("SEIT-PRES-05-N1-SEIT-PRES-05-N2-empty-key-remap-unchanged", () => InvalidRemap(root, "Pause", "", "empty"));
            Run("SEIT-PRES-05-N1-SEIT-PRES-05-N2-blank-key-remap-unchanged", () => InvalidRemap(root, "Pause", " ", "blank"));
        }
        finally { Directory.Delete(root, true); }
        if (failures == 0) Console.WriteLine("ACCESS ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
