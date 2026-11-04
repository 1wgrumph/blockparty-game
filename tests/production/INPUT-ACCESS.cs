/*
INPUT-ACCESS@1 test contract (plain C#; isolated-csharp, no editor).
Source: plan.json INPUT-ACCESS; m3/test-engineer.json A1, A2, A4(settings), A9;
SDoc BP-PRES-05 PRES05-REMAP/PAUSE/POINTER/PERSIST. Extends ACCESS coverage in
this separate module because the task authorizes only INPUT-ACCESS.cs.

Planned public API, bound reflectively like ACCESS.cs so absent capabilities
produce named runtime FAILs, never an unrelated compilation failure:
  InputMap(Accessibility accessibility, Clock clock)
  IEnumerable<string> Actions, HoldActions; string ControlState; bool MenuOpen
  bool TryActivate(string activeBinding); void EnterState(string state)
  void AdvanceGameSeconds(double seconds); string HintFor(string action)
  bool RequiresHold(string action); string ToggleAlternativeFor(string holdAction)
  bool IsActive(string action)
Accessibility remains the ONE active binding map: BindingFor/ResolveInput/
TryRemap apply to every InputMap.Actions entry. Each binding is a single key
or Mouse0..Mouse4; no '+' chord. Mouse remaps are tested one action at a time
(no requirement to assign more simultaneous distinct mouse buttons than exist).
InputMap uses the same Accessibility instance, so remaps update routing/hints.
States: Camera, Follow, Selection, MapOpen, ActionMenuOpen, MinigolfAim, HudFocus.
Menu actions are OpenActionMenu/CloseActionMenu; menus have no expiry.
Optional hold actions, if any, name a remappable toggle action with identical
active effect; all ordinary actions work without a required hold.

New SettingsRecord public fields and Accessibility setters:
  float CursorSize, CameraPanSpeed, CameraZoomSpeed, CameraRotateSpeed;
  bool HighContrastCursor, EdgeScroll, InvertX, InvertY, SnapSelection,
       NarrationEnabled, ToggleControls.
  bool TrySetCursorSize(float), TrySetCameraPanSpeed(float),
       TrySetCameraZoomSpeed(float), TrySetCameraRotateSpeed(float);
  void SetHighContrastCursor(bool), SetEdgeScroll(bool), SetInvertX(bool),
       SetInvertY(bool), SetSnapSelection(bool), SetNarrationEnabled(bool),
       SetToggleControls(bool).
Accessibility exposes public const float CursorSizeMin/Max, CameraSpeedMin/Max.
No numeric range/default is invented here: finite nondegenerate declared ranges
must include defaults; endpoints are valid, values beyond them and NaN/Infinity
are invalid without state change. Nondefault fixtures use the other endpoint or
invert the actual legacy default. Old SettingsRecord overloads remain supported.

Local SEIT projections (NOT whole-case acceptance):
 A1-* / A2-* / A4-* -> SEIT-PRES-05-P1(local), SEIT-PRES-05-N1;
 A1-inventory -> SEIT-UI-07-P1(action-list only);
 A9-* / edit-* -> SEIT-PRES-05-N2(local persistence).
A3/A5/A6/A7/A8 belong to HUD/SELECT-ACTION/CAMERA/CONTROLS-PLAY. Camera
motion ratios, rendered cursor sizes, real input devices, process restart,
spoken audio, screenshots and complete SEIT system cases remain downstream.
New TitleFlow instances prove settings reload, not an actual process restart.
Negative-fixture corrupts a real SaveSettings output, then applies the SAME
load-success oracle as normal roundtrip; must emit FAIL and return nonzero.
No stubs; EmptyScene is the existing no-scene-content test convention.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Blockparty.Audio;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class InputAccessChecks
{
    private static int failures;
    private static string root;
    private static readonly string[] RequiredActions = {
        "CameraPanUp", "CameraPanDown", "CameraPanLeft", "CameraPanRight",
        "CameraZoomIn", "CameraZoomOut", "CameraRotateLeft", "CameraRotateRight",
        "SelectNext", "SelectPrevious", "OpenActionMenu", "CloseActionMenu",
        "Confirm", "Cancel", "SpeedUp", "SpeedDown", "Pause", "OpenMap",
        "OpenSettings", "ImmediateCommand", "AppendCommand", "Inspect"
    };
    private static readonly string[] FloatFields = {
        "CursorSize", "CameraPanSpeed", "CameraZoomSpeed", "CameraRotateSpeed"
    };
    private static readonly string[] BoolFields = {
        "HighContrastCursor", "EdgeScroll", "InvertX", "InvertY",
        "SnapSelection", "NarrationEnabled", "ToggleControls"
    };

    private static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException(why);
    }
    private static void Run(string id, Action body)
    {
        try { body(); Console.WriteLine("PASS " + id); }
        catch (Exception error)
        {
            failures++;
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Console.WriteLine("FAIL " + id + ": " + error.Message);
        }
    }
    private static object Call(object obj, string method, params object[] args)
    {
        MethodInfo info = obj.GetType().GetMethod(method);
        Check(info != null, "missing INPUT-ACCESS " + obj.GetType().Name + "." + method);
        return info.Invoke(obj, args);
    }
    private static T Prop<T>(object obj, string name)
    {
        PropertyInfo info = obj.GetType().GetProperty(name);
        Check(info != null, "missing INPUT-ACCESS " + obj.GetType().Name + "." + name);
        return (T)info.GetValue(obj);
    }
    private static T Field<T>(SettingsRecord settings, string name)
    {
        FieldInfo info = typeof(SettingsRecord).GetField(name);
        Check(info != null, "missing INPUT-ACCESS SettingsRecord." + name);
        return (T)info.GetValue(settings);
    }
    private static float Limit(string name)
    {
        FieldInfo info = typeof(Accessibility).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Check(info != null, "missing INPUT-ACCESS Accessibility." + name + " range declaration");
        return (float)info.GetValue(null);
    }
    private static SettingsRecord Legacy()
    {
        return new SettingsRecord(SaveV1.SettingsSchemaVersion, .7f, 1.25f, true,
            .2f, .4f, .6f, .8f, false, false, new Dictionary<string,string>());
    }
    private static object Map(Accessibility access, Clock clock)
    {
        Type type = typeof(Accessibility).Assembly.GetType("Blockparty.UI.InputMap");
        Check(type != null, "missing INPUT-ACCESS Blockparty.UI.InputMap");
        return Activator.CreateInstance(type, new object[] { access, clock });
    }
    private static List<string> Names(object map, string property)
    {
        var names = new List<string>();
        foreach (object entry in Prop<IEnumerable>(map, property))
        {
            Check(entry is string && !string.IsNullOrWhiteSpace((string)entry), property + " has an unnamed action");
            Check(!names.Contains((string)entry), property + " contains duplicate " + entry);
            names.Add((string)entry);
        }
        return names;
    }
    private static Clock NewClock()
    {
        return new Clock(new WorldState(7L, 4242L));
    }
    private static string DirectoryFor(string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void Same(SettingsRecord actual, SettingsRecord expected)
    {
        Check(actual.SchemaVersion == expected.SchemaVersion && actual.MixerLevel == expected.MixerLevel
            && actual.TextScale == expected.TextScale && actual.ReducedMotion == expected.ReducedMotion
            && actual.AmbienceLevel == expected.AmbienceLevel && actual.WorldLevel == expected.WorldLevel
            && actual.VoiceLevel == expected.VoiceLevel && actual.InterfaceLevel == expected.InterfaceLevel
            && actual.NonColorCues == expected.NonColorCues && actual.Captions == expected.Captions,
            "existing settings were lost");
        Check(actual.InputRemaps.Count == expected.InputRemaps.Count, "remap count changed");
        foreach (var pair in expected.InputRemaps)
            Check(actual.InputRemaps.ContainsKey(pair.Key) && actual.InputRemaps[pair.Key] == pair.Value,
                "lost remap " + pair.Key);
        foreach (string name in FloatFields) Check(Field<float>(actual, name) == Field<float>(expected, name), "lost " + name);
        foreach (string name in BoolFields) Check(Field<bool>(actual, name) == Field<bool>(expected, name), "lost " + name);
    }
    private static void SameRemaps(SettingsRecord actual, SettingsRecord expected, string except = null)
    {
        foreach (var pair in expected.InputRemaps)
            if (pair.Key != except)
                Check(actual.InputRemaps.ContainsKey(pair.Key) && actual.InputRemaps[pair.Key] == pair.Value,
                    "settings edit lost remap " + pair.Key);
    }
    private static string Snapshot(Accessibility access, IEnumerable<string> actions)
    {
        string result = "";
        foreach (string action in actions) result += action + "=" + access.BindingFor(action) + ";";
        return result;
    }
    private static void Single(string binding)
    {
        Check(!string.IsNullOrWhiteSpace(binding) && !binding.Contains("+")
            && !binding.Contains(","), "requires a chord/sequence instead of a single input: " + binding);
    }
    private static void Remap(string action)
    {
        var access = new Accessibility(Legacy());
        string old = access.BindingFor(action);
        Check(access.TryRemap(action, "F24"), "missing INPUT-ACCESS remapping for " + action);
        Check(access.BindingFor(action) == "F24" && access.ResolveInput("F24") == action, "keyboard remap has no effect");
        if (old != "F24" && old.Length != 0) Check(access.ResolveInput(old) != action, "old key still resolves");
        for (int mouse = 0; mouse < 5; mouse++)
        {
            string input = "Mouse" + mouse;
            Check(access.TryRemap(action, input), "mouse remap refused for " + action + " -> " + input);
            Single(access.BindingFor(action));
            Check(access.ResolveInput(input) == action, "mouse binding has no effect");
        }
    }
    private static void Inventory()
    {
        var access = new Accessibility(Legacy());
        object map = Map(access, NewClock());
        List<string> actions = Names(map, "Actions");
        foreach (string required in RequiredActions) Check(actions.Contains(required), "missing action " + required);
        var used = new HashSet<string>();
        foreach (string action in actions)
        {
            string binding = access.BindingFor(action);
            Single(binding);
            Check(used.Add(binding), "conflicting default " + binding);
            Check(access.ResolveInput(binding) == action, "unresolvable default " + action);
            Check(!(bool)Call(map, "RequiresHold", action), "required hold on " + action);
            Check((string)Call(map,"HintFor",action) == binding, "incorrect default hint");
            Check(access.TryRemap(action,"F24"), "live remap refused");
            Check((string)Call(map,"HintFor",action) == "F24", "hint did not follow remap");
            Check(access.TryRemap(action,binding), "restore default refused");
            Remap(action);
        }
        string before = Snapshot(access, actions);
        for (int i = 0; i < actions.Count; i++)
        {
            Check(!access.TryRemap(actions[i], access.BindingFor(actions[(i+1)%actions.Count])),
                "conflict accepted for " + actions[i]);
            Check(Snapshot(access, actions) == before, "conflict changed binding map");
        }
        foreach (string invalid in new[] { "", " ", "Ctrl+P" })
        {
            Check(!access.TryRemap(actions[0], invalid), "invalid/chord binding accepted");
            Check(Snapshot(access, actions) == before, "refused binding changed map");
        }
        Check(!access.TryRemap("NotAnAction", "F24"), "unknown action accepted");
        Check(Snapshot(access, actions) == before, "unknown action changed map");
    }
    private static void Range(string field)
    {
        var access = new Accessibility(Legacy());
        string prefix = field == "CursorSize" ? "CursorSize" : "CameraSpeed";
        float min = Limit(prefix + "Min"), max = Limit(prefix + "Max");
        Check(!float.IsNaN(min) && !float.IsInfinity(min) && !float.IsNaN(max)
            && !float.IsInfinity(max) && min < max, "invalid declared range");
        float original = Field<float>(access.Settings, field);
        Check(original >= min && original <= max, "default outside declared range");
        foreach (float valid in new[] { min, max })
        {
            Check((bool)Call(access, "TrySet" + field, valid), "range endpoint refused");
            Check(Field<float>(access.Settings, field) == valid, "setter has no effect");
        }
        float step = Math.Max(1f, Math.Abs(max - min));
        foreach (float invalid in new[] { min-step, max+step, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            SettingsRecord before = access.Settings;
            Check(!(bool)Call(access, "TrySet" + field, invalid), "invalid setting accepted: " + invalid);
            Same(access.Settings, before);
        }
    }
    private static Accessibility Changed()
    {
        var access = new Accessibility(Legacy());
        foreach (string field in FloatFields)
        {
            string prefix = field == "CursorSize" ? "CursorSize" : "CameraSpeed";
            float min = Limit(prefix + "Min"), max = Limit(prefix + "Max");
            float value = Field<float>(access.Settings, field) == min ? max : min;
            Check((bool)Call(access, "TrySet" + field, value), "nondefault setting refused");
        }
        foreach (string field in BoolFields)
        {
            bool value = !Field<bool>(access.Settings, field);
            Call(access, "Set" + field, value);
            Check(Field<bool>(access.Settings, field) == value, "setter has no effect: " + field);
        }
        foreach (string action in RequiredActions)
        {
            string unused = null;
            for (char key = 'A'; key <= 'Z'; key++)
                if (access.ResolveInput(key.ToString()) == "") { unused = key.ToString(); break; }
            if (unused == null)
                for (int key = 1; key <= 24; key++)
                    if (access.ResolveInput("F"+key) == "") { unused = "F"+key; break; }
            Check(unused != null && access.TryRemap(action,unused), "remap refused: " + action);
        }
        return access;
    }
    private static void Roundtrip(SettingsRecord expected, string directory, bool corrupt = false)
    {
        SaveWriteResult written = SaveV1.SaveSettings(expected, directory);
        Check(written.Ok, "SaveSettings refused: " + written.Detail);
        if (corrupt)
        {
            Check(SaveV1.LoadSettings(directory).Ok, "negative fixture setup did not roundtrip before corruption");
            File.AppendAllText(Path.Combine(directory, SaveV1.SettingsFileName), "broken-json");
        }
        SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
        Check(loaded.Ok, "SaveSettings/LoadSettings roundtrip failed: " + loaded.Detail);
        Check(loaded.UnsupportedFields.Length == 0, "new settings treated as unknown");
        if (!corrupt) Same(loaded.Settings, expected);
    }
    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }
    private static TitleFlow Paused(string directory)
    {
        var flow = new TitleFlow(directory, new EmptyScene());
        Check(flow.Activate(TitleAction.NewGame).Ok && flow.PrepareNewGame(7L,4242L,null).Ok
            && flow.BeginPlay().Ok && flow.Activate(TitleAction.Pause).Ok, "paused world setup failed");
        return flow;
    }
    private static void Reload(bool useContinue)
    {
        string directory = DirectoryFor(useContinue ? "continue" : "load");
        SettingsRecord expected = Changed().Settings;
        var flow = Paused(directory);
        int hash = flow.Game.World.ComputeStateHash();
        Check(flow.Activate(TitleAction.Settings).Ok && flow.ApplySettings(expected).Ok, "ApplySettings refused");
        Same(flow.Settings, expected);
        Check(flow.Game.Clock.Paused && flow.Game.World.ComputeStateHash() == hash, "settings mutated paused world");
        Roundtrip(expected, directory);
        Check(flow.Activate(TitleAction.Back).Ok && flow.Activate(TitleAction.Save).Ok, "world save failed");
        var restarted = new TitleFlow(directory, new EmptyScene());
        Same(restarted.Settings, expected);
        Check(useContinue ? restarted.Activate(TitleAction.Continue).Ok
            : restarted.Activate(TitleAction.Load).Ok && restarted.LoadSavedGame().Ok, "world load failed");
        Same(restarted.Settings, expected);
        Same(SaveV1.LoadSettings(directory).Settings, expected);
    }
    private static void Pause(string state)
    {
        var access = new Accessibility(Legacy());
        Clock clock = NewClock();
        object map = Map(access, clock);
        Call(map, "EnterState", state);
        Check(Prop<string>(map, "ControlState") == state, "state setup failed");
        string old = access.BindingFor("Pause");
        Check(access.TryRemap("Pause", "F24"), "pause remap refused");
        Check((string)Call(map, "HintFor", "Pause") == "F24", "stale pause hint");
        Check(!(bool)Call(map, "TryActivate", old) && !clock.Paused, "old pause binding still active");
        Check((bool)Call(map, "TryActivate", "F24") && clock.Paused, "Pause refused in " + state);
        int hash = clock.World.ComputeStateHash();
        clock.AdvanceRealSeconds(StateWriter.SimCore, 10d);
        Check(clock.World.ComputeStateHash() == hash, "paused simulation advanced");
    }
    private static void Menu()
    {
        var access = new Accessibility(Legacy());
        Clock clock = NewClock();
        object map = Map(access, clock);
        Check((bool)Call(map, "TryActivate", access.BindingFor("OpenActionMenu")) && Prop<bool>(map,"MenuOpen"), "menu did not open");
        clock.AdvanceRealSeconds(StateWriter.SimCore, 10d);
        Check(clock.World.GameSeconds == 600L, "600-game-second fixture did not advance");
        Call(map, "AdvanceGameSeconds", 600d);
        Check(Prop<bool>(map, "MenuOpen"), "menu expired without dismissal after 600 game seconds");
        Check((bool)Call(map, "TryActivate", access.BindingFor("CloseActionMenu")) && !Prop<bool>(map,"MenuOpen"), "explicit dismissal failed");
        object fresh = Map(new Accessibility(Legacy()), NewClock());
        Check(!Prop<bool>(fresh,"MenuOpen"), "menu state leaked across instances/play entries");
    }
    private static void Toggles()
    {
        var access = new Accessibility(Legacy());
        object map = Map(access, NewClock());
        List<string> actions = Names(map,"Actions");
        foreach (string hold in Names(map,"HoldActions"))
        {
            Check(actions.Contains(hold), "undeclared optional hold action");
            string toggle = (string)Call(map,"ToggleAlternativeFor",hold);
            Check(actions.Contains(toggle), "hold lacks remappable toggle alternative: " + hold);
            string previous = access.BindingFor(toggle);
            Check(access.TryRemap(toggle,"F24"), "toggle remap refused");
            Check((bool)Call(map,"TryActivate","F24") && (bool)Call(map,"IsActive",hold), "toggle activation has no hold-equivalent effect");
            Check((bool)Call(map,"TryActivate","F24") && !(bool)Call(map,"IsActive",hold), "toggle cannot release hold");
            Check(access.TryRemap(toggle,previous), "toggle binding restore failed");
        }
    }
    private static void Edits()
    {
        SettingsRecord expected = Changed().Settings;
        foreach (string edit in new[] { "TextScale", "NonColorCues", "Captions", "ReducedMotion", "Remap" })
        {
            var access = new Accessibility(expected);
            if (edit == "TextScale") Check(access.TrySetTextScale(1.5f), "text edit refused");
            else if (edit == "Remap") Check(access.TryRemap("Pause","F24"), "remap edit refused");
            else Call(access, "Set" + edit, edit == "ReducedMotion" ? !expected.ReducedMotion : true);
            foreach (string field in FloatFields) Check(Field<float>(access.Settings,field) == Field<float>(expected,field), "accessibility edit lost " + field);
            foreach (string field in BoolFields) Check(Field<bool>(access.Settings,field) == Field<bool>(expected,field), "accessibility edit lost " + field);
            SameRemaps(access.Settings,expected,edit == "Remap" ? "Pause" : null);
            Roundtrip(access.Settings,DirectoryFor("edit-"+edit));
        }
        foreach (string field in FloatFields)
        {
            var access = new Accessibility(expected);
            Check((bool)Call(access,"TrySet"+field,Field<float>(Legacy(),field)), "numeric edit refused");
            var changed = new List<string>(FloatFields); changed.Remove(field);
            foreach (string other in changed) Check(Field<float>(access.Settings,other) == Field<float>(expected,other), "numeric edit lost "+other);
            foreach (string other in BoolFields) Check(Field<bool>(access.Settings,other) == Field<bool>(expected,other), "numeric edit lost "+other);
            SameRemaps(access.Settings,expected);
            Roundtrip(access.Settings,DirectoryFor("edit-"+field));
        }
        foreach (string field in BoolFields)
        {
            var access = new Accessibility(expected);
            Call(access,"Set"+field,Field<bool>(Legacy(),field));
            foreach (string other in FloatFields) Check(Field<float>(access.Settings,other) == Field<float>(expected,other), "boolean edit lost "+other);
            foreach (string other in BoolFields) if (other != field) Check(Field<bool>(access.Settings,other) == Field<bool>(expected,other), "boolean edit lost "+other);
            SameRemaps(access.Settings,expected);
            Roundtrip(access.Settings,DirectoryFor("edit-"+field));
        }
        for (int edit = -1; edit < 4; edit++)
        {
            var mix = new MixSettings(expected);
            Check(edit < 0 ? mix.TrySetMasterLevel(.9f) : mix.TrySetLevel((AudioGroup)edit,.9f), "audio edit refused");
            foreach (string field in FloatFields) Check(Field<float>(mix.Settings,field) == Field<float>(expected,field), "audio edit lost " + field);
            foreach (string field in BoolFields) Check(Field<bool>(mix.Settings,field) == Field<bool>(expected,field), "audio edit lost " + field);
            SameRemaps(mix.Settings,expected);
            Roundtrip(mix.Settings,DirectoryFor("audio-"+edit));
        }
    }
    public static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "blockparty-input-access-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (Array.IndexOf(args,"negative-fixture") >= 0)
            {
                Run("A9-negative-fixture", () => Roundtrip(Legacy(),DirectoryFor("negative"),true));
            }
            else
            {
                foreach (string action in RequiredActions) Run("A1-remap-"+action, () => Remap(action));
                Run("A1-inventory-single-input-conflict", Inventory);
                foreach (string field in FloatFields) Run("A4-range-"+field, () => Range(field));
                foreach (string field in BoolFields) Run("A4-toggle-"+field, () => {
                    var access = new Accessibility(Legacy());
                    bool before = Field<bool>(access.Settings,field);
                    Call(access,"Set"+field,!before);
                    Check(Field<bool>(access.Settings,field) == !before,"setting has no effect");
                    Call(access,"Set"+field,before);
                    Check(Field<bool>(access.Settings,field) == before,"setting cannot be restored");
                });
                foreach (string state in new[] { "Camera", "Follow", "Selection", "MapOpen", "ActionMenuOpen", "MinigolfAim", "HudFocus" })
                    Run("A2-pause-"+state, () => Pause(state));
                Run("A2-menu-600-seconds", Menu);
                Run("A2-hold-toggle-alternatives", Toggles);
                Run("A9-roundtrip-all", () => Roundtrip(Changed().Settings,DirectoryFor("roundtrip")));
                Run("A9-legacy-defaults", () => {
                    string directory = DirectoryFor("legacy");
                    File.Copy("tests/production/fixtures/ACCESS/audio-mix-v1.json",Path.Combine(directory,SaveV1.SettingsFileName));
                    SettingsLoadResult loaded = SaveV1.LoadSettings(directory);
                    Check(loaded.Ok && loaded.UnsupportedFields.Length == 0,"legacy settings no longer load");
                    var expected = new SettingsRecord(SaveV1.SettingsSchemaVersion,.7f,1.25f,true,.2f,.4f,.6f,.8f);
                    Same(loaded.Settings,expected);
                });
                Run("A9-independent-instances-deterministic-save", () => {
                    SettingsRecord baseline = Legacy();
                    SettingsRecord changed = Changed().Settings;
                    Same(new Accessibility(Legacy()).Settings,baseline);
                    string first = DirectoryFor("deterministic-first"), second = DirectoryFor("deterministic-second");
                    Roundtrip(changed,first);
                    Roundtrip(Changed().Settings,second);
                    Check(File.ReadAllText(Path.Combine(first,SaveV1.SettingsFileName))
                        == File.ReadAllText(Path.Combine(second,SaveV1.SettingsFileName)), "identical inputs produced different saved settings");
                });
                Run("A9-title-settings-apply", () => {
                    string directory = DirectoryFor("title-apply");
                    SettingsRecord expected = Changed().Settings;
                    var flow = new TitleFlow(directory,new EmptyScene());
                    Check(flow.Activate(TitleAction.Settings).Ok && flow.ApplySettings(expected).Ok, "title settings apply failed");
                    Same(flow.Settings,expected);
                    Same(SaveV1.LoadSettings(directory).Settings,expected);
                    Same(new TitleFlow(directory,new EmptyScene()).Settings,expected);
                });
                Run("A9-title-continue-reload", () => Reload(true));
                Run("A9-title-load-reload", () => Reload(false));
                Run("edit-all-settings-paths", Edits);
            }
        }
        finally { Directory.Delete(root,true); }
        if (failures != 0) return 1;
        Console.WriteLine("INPUT-ACCESS ALL PASS");
        return 0;
    }
}
