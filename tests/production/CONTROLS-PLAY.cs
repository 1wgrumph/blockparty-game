/* CONTROLS-PLAY@1 receipt checks, isolated-csharp; no editor, display or network.
 * Local coverage only: SEIT-AC-12, SEIT-PRES-05-P1, SEIT-UI-07-P1.
 * controls.json contract (all paths repo-relative, REF={path,sha256}):
 * {tree_hash,binary_sha256,build:REF,runs:{keyboard_only:REF,mouse_only:REF,
 * remapped:REF},settings_before:REF,settings_after:REF,restart:{before_pid,
 * after_pid},measurements:REF,narration:REF,narration_audio:{status,...}}.
 * build REF -> {tree_hash,binary_sha256,method:"BuildLinux",
 * target:"StandaloneLinux64",exit_code:0,log:REF}; its tracked JSON log
 * repeats that binding and exit. Each run repeats the binding and has events:
 * {id,flow,device,input,action,outcome:"Completed",text_scale,frame:REF,
 * narration_kind:"focus|selection|action-result",label,...}. Extra fields:
 * moving_resident for snap/keyboard selection; state,paused for pause-anytime;
 * process_id,cursor_size,settings_text_scale,binding for restart settings.
 * Measurements repeat the binding, width=1920,height=1080, elements[] with
 * {id,type,interactive}, samples[] with {element,text_scale,width,height,
 * contrast,focus_contrast,frame:REF}, greyscale[] with {status,frame:REF},
 * cursors[] with {state,size,high_contrast,px,contrast,glyph_sha256,frame:REF}.
 * glyph_sha256 hashes the px*px render region at the recorded pointer; distinct
 * states, and both contrast options of one state, must differ.
 * Element types: menu,overlay,on-screen,world-resident,world-object. Cursor
 * samples cover min/default/max size and both contrast options in every state.
 * Narration repeats binding and has utterances[{event_id,flow,kind,text}].
 * narration_audio is {status:"RECORDED",log:REF} or
 * {status:"GAP",id:"G-7",reason:<nonempty>}; text is never spoken-audio proof.
 * G-6: measurements are recorded render readback, not independent pixel checks.
 * Frames are hash-bound PNGs; maintainer spot-check remains required.
 * In-memory schema examples below reuse committed GATE-M1 opaque payload/PNG
 * and real SaveV1 settings output. They are NOT running-build evidence.
 * Negative mutations rehash the changed artifact so they reach their oracle.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.UI;

public static class ControlsPlayChecks
{
    private const string Receipt = "evidence/production/CONTROLS-PLAY/controls.json";
    private const string Alpha = "evidence/production/ALPHA/alpha.json";
    private static readonly string[] Modes = { "keyboard_only", "mouse_only", "remapped" };
    private static readonly string[] Flows = { "camera", "select-click", "select-snap", "select-keyboard",
        "action-menu-immediate", "action-menu-append", "hud-read", "cursor-states", "pause-anytime", "settings-persist-after-restart" };
    private static readonly string[] PauseStates = { "Camera", "Follow", "Selection", "Map", "ActionMenu", "HudFocus", "MinigolfAim" };
    private static readonly string[] ElementTypes = { "menu", "overlay", "on-screen", "world-resident", "world-object" };
    private static readonly double[] Scales = { 1.0, 1.5 };
    private static readonly double[] CursorSizes = { Accessibility.CursorSizeMin, 1.0, Accessibility.CursorSizeMax };
    private static int failures;

    private static void Require(bool ok, string check, string detail)
    { if (!ok) throw new InvalidDataException(check + ": " + detail); }
    private static string Text(JsonElement o, string key) { return o.GetProperty(key).GetString(); }
    private static double Number(JsonElement o, string key) { return o.GetProperty(key).GetDouble(); }
    private static bool Flag(JsonElement o, string key) { return o.GetProperty(key).GetBoolean(); }
    private static bool Hex(string s, int length)
    {
        if (s == null || s.Length != length) return false;
        foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static JsonElement Read(string root, string path)
    {
        Require(File.Exists(Path.Combine(root, path)), "CP-Receipt", "missing CONTROLS-PLAY running-build evidence " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return doc.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement r, HashSet<string> paths = null)
    {
        string p = Text(r, "path"), digest = Text(r, "sha256");
        Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p)
            && Path.GetFullPath(Path.Combine(root, p)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "CP-Reference", "reference must be repo-relative");
        p = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, p)));
        Require(File.Exists(Path.Combine(root, p)), "CP-Reference", "missing referenced file " + p);
        Require(Hex(digest, 64) && Hash(Path.Combine(root, p)) == digest.ToLowerInvariant(), "CP-Reference", "sha256 mismatch " + p);
        if (paths != null) paths.Add(p);
        return p;
    }
    private static void References(string root, JsonElement o, HashSet<string> paths)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("path", out _) || o.TryGetProperty("sha256", out _)) Reference(root, o, paths);
            foreach (var p in o.EnumerateObject()) References(root, p.Value, paths);
        }
        else if (o.ValueKind == JsonValueKind.Array) foreach (var v in o.EnumerateArray()) References(root, v, paths);
    }
    private static void Binding(JsonElement o, string tree, string binary)
    { Require(Text(o, "tree_hash") == tree && Text(o, "binary_sha256") == binary, "CP-Build", "source/player binding differs"); }
    private static string Frame(string root, JsonElement r, HashSet<string> paths)
    {
        string p = Reference(root, r, paths);
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, p));
        byte[] magic = { 137, 80, 78, 71, 13, 10, 26, 10 };
        Require(bytes.Length > magic.Length, "CP-Frame", "empty PNG " + p);
        for (int i = 0; i < magic.Length; i++) Require(bytes[i] == magic[i], "CP-Frame", "not PNG " + p);
        return p;
    }
    private static SettingsRecord Settings(string root, JsonElement r)
    {
        string p = Reference(root, r);
        string temp = Path.Combine(Path.GetTempPath(), "controls-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            File.Copy(Path.Combine(root, p), Path.Combine(temp, SaveV1.SettingsFileName));
            var loaded = SaveV1.LoadSettings(temp);
            Require(loaded.Ok, "CP-Settings", "public settings codec rejected snapshot: " + loaded.Detail);
            Require(SaveV1.SaveSettings(loaded.Settings, temp).Ok && SaveV1.LoadSettings(temp).Ok,
                "CP-Settings", "public settings round-trip failed");
            return loaded.Settings;
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
    private static void Validate(string root, bool trackedBuildLog = false)
    {
        var c = Read(root, Receipt);
        string tree = Text(c, "tree_hash"), binary = Text(c, "binary_sha256");
        Require(Hex(tree, 40) && Hex(binary, 64), "CP-Build", "invalid tree/player hash");
        var build = Read(root, Reference(root, c.GetProperty("build")));
        Binding(build, tree, binary);
        Require(Text(build, "method") == "BuildLinux" && Text(build, "target") == "StandaloneLinux64"
            && Number(build, "exit_code") == 0, "CP-Build", "successful Linux build required");
        string buildLog = Reference(root, build.GetProperty("log"));
        var log = Read(root, buildLog);
        Binding(log, tree, binary);
        Require(Number(log, "exit_code") == 0, "CP-Build", "build log exit");
        if (trackedBuildLog)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Path.GetFullPath(root), UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "ls-files", "--error-unmatch", "--", buildLog }) start.ArgumentList.Add(a);
            using (var process = Process.Start(start))
            { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
                Require(process.ExitCode == 0, "CP-Build", "build log must be tracked"); }
        }
        var before = c.GetProperty("settings_before"); var after = c.GetProperty("settings_after");
        string beforePath = Reference(root, before), afterPath = Reference(root, after);
        var settings = Settings(root, before); var reloaded = Settings(root, after);
        Require(beforePath != afterPath && Text(before, "sha256") == Text(after, "sha256"),
            "CP-Settings", "separate equal settings snapshots required across restart");
        var restart = c.GetProperty("restart");
        Require(Number(restart, "before_pid") > 0 && Number(restart, "after_pid") > 0
            && Number(restart, "before_pid") != Number(restart, "after_pid"), "CP-Settings", "process restart missing");
        var uiFiles = new HashSet<string>(StringComparer.Ordinal) { beforePath, afterPath };
        var inputPaths = new HashSet<string>(); var eventIds = new HashSet<string>();
        var expectedNarration = new Dictionary<string, string[]>();
        foreach (string mode in Modes)
        {
            string path = Reference(root, c.GetProperty("runs").GetProperty(mode), uiFiles);
            Require(inputPaths.Add(path), "CP-Inputs", "input runs must be separate files");
            var run = Read(root, path); Binding(run, tree, binary);
            var covered = new HashSet<string>(); var pauses = new HashSet<string>(); var remapped = new HashSet<string>();
            var narrations = new HashSet<string>();
            foreach (var e in run.GetProperty("events").EnumerateArray())
            {
                string flow = Text(e, "flow"), device = Text(e, "device"), input = Text(e, "input"), id = Text(e, "id");
                Require(Array.IndexOf(Flows, flow) >= 0 && eventIds.Add(id) && !string.IsNullOrWhiteSpace(id)
                    && Text(e, "outcome") == "Completed", "CP-Flows", "completed unique flow event required");
                Require((device == "keyboard" || device == "mouse") && !string.IsNullOrWhiteSpace(input)
                    && (mode != "keyboard_only" || device == "keyboard") && (mode != "mouse_only" || device == "mouse"),
                    "CP-Inputs", "wrong input device in " + mode);
                if (mode == "remapped" && device == "keyboard")
                {
                    string action = Text(e, "action"), binding;
                    Require(settings.InputRemaps.TryGetValue(action, out binding) && input == binding
                        && binding != InputActions.DefaultBinding(action), "CP-Remap", "key event is not the saved remap");
                    remapped.Add(flow + "/" + Number(e, "text_scale"));
                }
                Frame(root, e.GetProperty("frame"), uiFiles);
                double scale = Number(e, "text_scale");
                Require(Array.IndexOf(Scales, scale) >= 0, "CP-Scales", "frame not at endpoint");
                covered.Add(flow + "/" + scale);
                if (flow == "select-snap" || flow == "select-keyboard")
                    Require(Flag(e, "moving_resident"), "CP-MovingSelection", "moving resident not exercised");
                if (flow == "pause-anytime")
                { Require(Flag(e, "paused"), "CP-Pause", "frame must show paused"); pauses.Add(Text(e, "state") + "/" + scale); }
                if (flow == "settings-persist-after-restart")
                    Require(Number(e, "process_id") == Number(restart, "after_pid")
                        && Number(e, "cursor_size") == reloaded.CursorSize && Number(e, "settings_text_scale") == reloaded.TextScale
                        && Text(e, "binding") == new Accessibility(reloaded).BindingFor(Text(e, "action")),
                        "CP-Settings", "restart frame settings differ");
                string kind = Text(e, "narration_kind"), label = Text(e, "label");
                Require((kind == "focus" || kind == "selection" || kind == "action-result") && !string.IsNullOrWhiteSpace(label),
                    "CP-Narration", "event label/kind missing");
                narrations.Add(kind); expectedNarration.Add(id, new[] { flow, kind, label });
            }
            foreach (string flow in Flows) foreach (double scale in Scales)
            {
                Require(covered.Contains(flow + "/" + scale), "CP-Scales", mode + " missing flow/endpoint " + flow + "/" + scale);
                Require(mode != "remapped" || remapped.Contains(flow + "/" + scale), "CP-Remap", "flow has no remapped key event " + flow);
            }
            foreach (string state in PauseStates) foreach (double scale in Scales)
                Require(pauses.Contains(state + "/" + scale), "CP-Pause", mode + " missing pause state " + state);
            Require(narrations.SetEquals(new[] { "focus", "selection", "action-result" }), "CP-Narration", "missing event kind");
        }
        var m = Read(root, Reference(root, c.GetProperty("measurements"), uiFiles)); Binding(m, tree, binary);
        Require(Number(m, "width") == 1920 && Number(m, "height") == 1080, "CP-Targets", "measurement display must be 1920x1080");
        var types = new HashSet<string>(); var elements = new HashSet<string>();
        foreach (var element in m.GetProperty("elements").EnumerateArray())
        {
            string id = Text(element, "id"), type = Text(element, "type"); types.Add(type);
            Require(!string.IsNullOrWhiteSpace(id) && elements.Add(id), "CP-Measurements", "duplicate/blank element");
            foreach (double scale in Scales)
            {
                bool found = false;
                foreach (var s in m.GetProperty("samples").EnumerateArray())
                {
                    if (Text(s, "element") != id || Number(s, "text_scale") != scale) continue;
                    found = true; Frame(root, s.GetProperty("frame"), uiFiles);
                    Require(!Flag(element, "interactive") || (Number(s, "width") >= 44 && Number(s, "height") >= 44),
                        "CP-Targets", "target below 44 px " + id);
                    Require(Number(s, "contrast") >= 4.5, "CP-Contrast", "text/icon contrast below 4.5:1 " + id);
                    Require(Number(s, "focus_contrast") >= 3, "CP-Focus", "focus contrast below 3:1 " + id);
                }
                Require(found, "CP-Measurements", "missing measured element/scale " + id);
            }
        }
        Require(types.IsSupersetOf(ElementTypes), "CP-Focus", "missing focus element type");
        var statuses = new HashSet<string>();
        foreach (var s in m.GetProperty("greyscale").EnumerateArray())
        { statuses.Add(Text(s, "status")); Frame(root, s.GetProperty("frame"), uiFiles); }
        Require(statuses.IsSupersetOf(new[] { "needs", "mood", "selection", "blocked" }), "CP-Greyscale", "status greyscale frame missing");
        foreach (string state in Enum.GetNames(typeof(CursorKind))) foreach (bool high in new[] { false, true })
        {
            var sizes = new HashSet<double>();
            foreach (double size in CursorSizes)
            {
                bool found = false;
                foreach (var cursor in m.GetProperty("cursors").EnumerateArray())
                {
                    if (Text(cursor, "state") != state || Number(cursor, "size") != size || Flag(cursor, "high_contrast") != high) continue;
                    Frame(root, cursor.GetProperty("frame"), uiFiles); found = true;
                    Require(Number(cursor, "px") > 0 && sizes.Add(Number(cursor, "px")), "CP-Cursor", "cursor sizes must render distinctly");
                    Require(Number(cursor, "contrast") >= 4.5, "CP-Cursor", "cursor contrast below 4.5:1");
                }
                Require(found, "CP-Cursor", "cursor state/size/contrast frame missing");
            }
        }
        var glyphs = new Dictionary<double, HashSet<string>>();
        var highGlyphs = new Dictionary<double, HashSet<string>>();
        foreach (double size in CursorSizes)
        { glyphs.Add(size, new HashSet<string>()); highGlyphs.Add(size, new HashSet<string>()); }
        foreach (string state in Enum.GetNames(typeof(CursorKind))) foreach (double size in CursorSizes)
        {
            string standard = null, high = null;
            foreach (var cursor in m.GetProperty("cursors").EnumerateArray())
            {
                if (Text(cursor, "state") != state || Number(cursor, "size") != size) continue;
                JsonElement digest;
                Require(cursor.TryGetProperty("glyph_sha256", out digest) && digest.ValueKind == JsonValueKind.String
                    && Hex(digest.GetString(), 64), "CP-CursorGlyph", "cursor glyph-region hash missing " + state);
                if (Flag(cursor, "high_contrast")) high = digest.GetString(); else standard = digest.GetString();
            }
            Require(standard != null && high != null && standard != high, "CP-CursorGlyph",
                "high-contrast glyph must differ from standard for " + state + "/" + size);
            glyphs[size].Add(standard);
            highGlyphs[size].Add(high);
        }
        foreach (double size in CursorSizes)
            Require(glyphs[size].Count == Enum.GetNames(typeof(CursorKind)).Length
                && highGlyphs[size].Count == Enum.GetNames(typeof(CursorKind)).Length, "CP-CursorGlyph",
                "cursor states must render distinct glyphs at size " + size);
        var n = Read(root, Reference(root, c.GetProperty("narration"), uiFiles)); Binding(n, tree, binary);
        var spoken = new HashSet<string>();
        foreach (var u in n.GetProperty("utterances").EnumerateArray())
        {
            string id = Text(u, "event_id"); string[] expected;
            Require(expectedNarration.TryGetValue(id, out expected) && Text(u, "flow") == expected[0]
                && Text(u, "kind") == expected[1] && !string.IsNullOrWhiteSpace(Text(u, "text"))
                && Text(u, "text").Contains(expected[2]), "CP-Narration", "utterance not bound to event/label");
            spoken.Add(id);
        }
        Require(spoken.SetEquals(expectedNarration.Keys), "CP-Narration", "event without utterance");
        var audio = c.GetProperty("narration_audio");
        if (Text(audio, "status") == "GAP")
            Require(Text(audio, "id") == "G-7" && !string.IsNullOrWhiteSpace(Text(audio, "reason")), "CP-AudioGap", "G-7 needs reason");
        else
        { Require(Text(audio, "status") == "RECORDED", "CP-AudioGap", "audio must be recorded or named gap"); Reference(root, audio.GetProperty("log"), uiFiles); }
        if (File.Exists(Path.Combine(root, Alpha)))
        {
            var botFiles = new HashSet<string>(); var alpha = Read(root, Alpha);
            foreach (var p in alpha.EnumerateObject())
                if (p.Name != "build" && p.Name != "gate_m2") References(root, p.Value, botFiles);
            Require(!uiFiles.Overlaps(botFiles), "CP-Separation", "UI evidence shares file with ALPHA bot logs");
        }
    }

    private static Dictionary<string, object> Obj(params object[] pairs)
    { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d; }
    private static object Ref(string root, string path) { return Obj("path", path, "sha256", Hash(Path.Combine(root, path))); }
    private static void Write(string root, string path, object o)
    { string p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true })); }
    private static Dictionary<string, object> Bound(string tree, string binary)
    { return Obj("tree_hash", tree, "binary_sha256", binary); }
    private static void Fixture(string root)
    {
        const string prefix = "evidence/production/CONTROLS-PLAY/";
        Directory.CreateDirectory(Path.Combine(root, prefix));
        string tree = new string('a', 40);
        string corpus = "tests/production/fixtures/GATE-M1/payload/";
        File.Copy(corpus + "capture.png", Path.Combine(root, prefix + "frame.png"));
        string binary = Hash(corpus + "player-payload.fixture");
        var frame = Ref(root, prefix + "frame.png");
        var build = Bound(tree, binary); build.Add("exit_code", 0);
        Write(root, prefix + "build-log.json", build);
        build.Add("method", "BuildLinux"); build.Add("target", "StandaloneLinux64"); build.Add("log", Ref(root, prefix + "build-log.json"));
        Write(root, prefix + "build.json", build);
        var remaps = new Dictionary<string, string>(); int key = 0;
        foreach (string action in InputActions.Names) remaps.Add(action, "FixtureKey" + key++);
        var settings = new SettingsRecord(SaveV1.SettingsSchemaVersion, 1f, 1.5f, true, 1f, 1f, 1f, 1f, true, true, remaps,
            2f, 2f, 2f, 2f, true, false, false, false, true, true, true);
        Require(SaveV1.SaveSettings(settings, Path.Combine(root, prefix)).Ok, "Fixture", "settings producer failed");
        File.Copy(Path.Combine(root, prefix, SaveV1.SettingsFileName), Path.Combine(root, prefix + "settings-before.json"));
        File.Copy(Path.Combine(root, prefix, SaveV1.SettingsFileName), Path.Combine(root, prefix + "settings-after.json"));
        var c = Bound(tree, binary); c.Add("build", Ref(root, prefix + "build.json"));
        c.Add("settings_before", Ref(root, prefix + "settings-before.json")); c.Add("settings_after", Ref(root, prefix + "settings-after.json"));
        c.Add("restart", Obj("before_pid", 101, "after_pid", 202));
        var runs = new Dictionary<string, object>(); var utterances = new List<object>(); int serial = 0;
        foreach (string mode in Modes)
        {
            var events = new List<object>();
            foreach (string flow in Flows) foreach (double scale in Scales)
            foreach (string state in flow == "pause-anytime" ? PauseStates : new[] { "Camera" })
            {
                string kind = flow.StartsWith("select-") ? "selection" : flow.StartsWith("action-") ? "action-result" : "focus";
                string id = "event-" + serial++; string action = flow == "pause-anytime" ? "Pause" : "SelectNext";
                var e = Obj("id", id, "flow", flow, "device", mode == "mouse_only" ? "mouse" : "keyboard",
                    "input", mode == "mouse_only" ? "Mouse0" : mode == "remapped" ? remaps[action] : InputActions.DefaultBinding(action),
                    "action", action, "outcome", "Completed", "text_scale", scale, "frame", frame,
                    "narration_kind", kind, "label", "Fixture label", "moving_resident", true, "state", state, "paused", true,
                    "process_id", 202, "cursor_size", 2, "settings_text_scale", 1.5, "binding", remaps[action]);
                events.Add(e); utterances.Add(Obj("event_id", id, "flow", flow, "kind", kind, "text", "Fixture label"));
            }
            var run = Bound(tree, binary); run.Add("events", events); Write(root, prefix + mode + ".json", run);
            runs.Add(mode, Ref(root, prefix + mode + ".json"));
        }
        c.Add("runs", runs);
        var elements = new List<object>(); var samples = new List<object>(); var greyscale = new List<object>(); var cursors = new List<object>();
        foreach (string type in ElementTypes)
        {
            elements.Add(Obj("id", type, "type", type, "interactive", true));
            foreach (double scale in Scales) samples.Add(Obj("element", type, "text_scale", scale,
                "width", 44, "height", 44, "contrast", 4.5, "focus_contrast", 3, "frame", frame));
        }
        foreach (string status in new[] { "needs", "mood", "selection", "blocked" }) greyscale.Add(Obj("status", status, "frame", frame));
        var stateNames = Enum.GetNames(typeof(CursorKind));
        foreach (string state in stateNames)
        {
            int stateIndex = Array.IndexOf(stateNames, state);
            foreach (double size in CursorSizes)
            {
                int sizeIndex = Array.IndexOf(CursorSizes, size);
                foreach (bool high in new[] { false, true })
                {
                    int glyphIndex = stateIndex * 100 + sizeIndex * 10 + (high ? 1 : 0);
                    cursors.Add(Obj("state", state, "size", size, "high_contrast", high, "px", size * 32,
                        "contrast", high ? 21 : 4.5, "glyph_sha256", glyphIndex.ToString("x64"), "frame", frame));
                }
            }
        }
        var m = Bound(tree, binary); m.Add("width", 1920); m.Add("height", 1080); m.Add("elements", elements);
        m.Add("samples", samples); m.Add("greyscale", greyscale); m.Add("cursors", cursors); Write(root, prefix + "measurements.json", m);
        c.Add("measurements", Ref(root, prefix + "measurements.json"));
        var n = Bound(tree, binary); n.Add("utterances", utterances); Write(root, prefix + "narration.json", n);
        c.Add("narration", Ref(root, prefix + "narration.json"));
        c.Add("narration_audio", Obj("status", "GAP", "id", "G-7", "reason", "Fixture only: no TTS audio"));
        Write(root, Receipt, c);
        Write(root, "evidence/production/ALPHA/bot.json", Obj("source", "BOT"));
        Write(root, Alpha, Obj("bot_log", Ref(root, "evidence/production/ALPHA/bot.json")));
    }
    private static void Change(string root, string file, string from, string to)
    {
        string path = Path.Combine(root, file), old = Hash(path), contents = File.ReadAllText(path);
        Require(contents.Contains(from), "Fixture", "mutation did not match " + from);
        File.WriteAllText(path, contents.Replace(from, to));
        if (file != Receipt) File.WriteAllText(Path.Combine(root, Receipt), File.ReadAllText(Path.Combine(root, Receipt)).Replace(old, Hash(path)));
    }
    private static void Report(bool ok, string name)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    private static void SelfCheck(string name, string expected, Action<string> mutate)
    {
        string root = Path.Combine(Path.GetTempPath(), "controls-play-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture(root); mutate(root);
            try { Validate(root); Report(expected == null, name + (expected == null ? " (fixture only)" : " accepted invalid fixture")); }
            catch (InvalidDataException e) { Report(expected != null && e.Message.StartsWith(expected + ":", StringComparison.Ordinal), name + " " + e.Message); }
        }
        catch (Exception e) { Report(false, name + " fixture/harness error: " + e.Message); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void CursorPixelCheck(string script)
    {
        var start = new ProcessStartInfo("python3", "tests/production/fixtures/CONTROLS-PLAY/" + script)
        { UseShellExecute = false };
        using (var process = Process.Start(start))
        {
            process.WaitForExit();
            Require(process.ExitCode == 0, "CP-CursorPixels", script + " rejected cursor evidence");
        }
    }
    public static int Main(string[] args)
    {
        const string p = "evidence/production/CONTROLS-PLAY/";
        SelfCheck("CP-Positive SEIT-AC-12(local)/PRES-05-P1/UI-07-P1", null, _ => { });
        SelfCheck("CP-Inputs keyboard mouse event", "CP-Inputs", r => Change(r, p + "keyboard_only.json", "\"device\": \"keyboard\"", "\"device\": \"mouse\""));
        SelfCheck("CP-Inputs mouse keyboard event", "CP-Inputs", r => Change(r, p + "mouse_only.json", "\"device\": \"mouse\"", "\"device\": \"keyboard\""));
        SelfCheck("CP-Remap A1 stale binding", "CP-Remap", r => Change(r, p + "remapped.json", "\"input\": \"FixtureKey", "\"input\": \"WrongKey"));
        SelfCheck("CP-Remap A1 no remapped keys", "CP-Remap", r => Change(r, p + "remapped.json", "\"device\": \"keyboard\"", "\"device\": \"mouse\""));
        SelfCheck("CP-Targets A5 43px width", "CP-Targets", r => Change(r, p + "measurements.json", "\"width\": 44", "\"width\": 43"));
        SelfCheck("CP-Targets A5 43px height", "CP-Targets", r => Change(r, p + "measurements.json", "\"height\": 44", "\"height\": 43"));
        SelfCheck("CP-Contrast A6 4.4:1", "CP-Contrast", r => Change(r, p + "measurements.json", "\"contrast\": 4.5", "\"contrast\": 4.4"));
        SelfCheck("CP-Focus A3 2.9:1", "CP-Focus", r => Change(r, p + "measurements.json", "\"focus_contrast\": 3", "\"focus_contrast\": 2.9"));
        SelfCheck("CP-Scales missing max-scale frame", "CP-Scales", r => Change(r, p + "keyboard_only.json", "\"text_scale\": 1.5", "\"text_scale\": 1"));
        SelfCheck("CP-Pause A2 unpaused frame", "CP-Pause", r => Change(r, p + "mouse_only.json", "\"paused\": true", "\"paused\": false"));
        SelfCheck("CP-Pause A2 missing state", "CP-Pause", r => Change(r, p + "remapped.json", "\"state\": \"MinigolfAim\"", "\"state\": \"Camera\""));
        SelfCheck("CP-MovingSelection A8 stationary", "CP-MovingSelection", r => Change(r, p + "keyboard_only.json", "\"moving_resident\": true", "\"moving_resident\": false"));
        SelfCheck("CP-Settings A9 no restart", "CP-Settings", r => Change(r, Receipt, "\"after_pid\": 202", "\"after_pid\": 101"));
        SelfCheck("CP-Settings A9 visible settings lost", "CP-Settings", r => Change(r, p + "keyboard_only.json", "\"cursor_size\": 2", "\"cursor_size\": 1"));
        SelfCheck("CP-Measurements A5 missing endpoint", "CP-Measurements", r => Change(r, p + "measurements.json", "\"text_scale\": 1.5", "\"text_scale\": 1"));
        SelfCheck("CP-Cursor A4 missing size", "CP-Cursor", r => Change(r, p + "measurements.json", "\"size\": 2", "\"size\": 1.75"));
        SelfCheck("CP-Cursor A4 indistinct px", "CP-Cursor", r => Change(r, p + "measurements.json", "\"px\": 64", "\"px\": 32"));
        SelfCheck("CP-Cursor A4 missing high contrast", "CP-Cursor", r => Change(r, p + "measurements.json", "\"high_contrast\": true", "\"high_contrast\": false"));
        SelfCheck("CP-CursorGlyph A4 identical state glyphs", "CP-CursorGlyph", r => Change(r, p + "measurements.json",
            "\"glyph_sha256\": \"" + 10.ToString("x64") + "\"", "\"glyph_sha256\": \"" + 310.ToString("x64") + "\""));
        SelfCheck("CP-CursorGlyph A4 identical high-contrast state glyphs", "CP-CursorGlyph", r => Change(r, p + "measurements.json",
            "\"glyph_sha256\": \"" + 11.ToString("x64") + "\"", "\"glyph_sha256\": \"" + 311.ToString("x64") + "\""));
        SelfCheck("CP-Greyscale A6 missing status", "CP-Greyscale", r => Change(r, p + "measurements.json", "\"status\": \"blocked\"", "\"status\": \"needs\""));
        SelfCheck("CP-Narration A7 empty utterance", "CP-Narration", r => Change(r, p + "narration.json", "\"text\": \"Fixture label\"", "\"text\": \"\""));
        SelfCheck("CP-AudioGap A7 unnamed gap", "CP-AudioGap", r => Change(r, Receipt, "\"id\": \"G-7\"", "\"id\": \"unknown\""));
        SelfCheck("CP-Reference tampered capture", "CP-Reference", r => File.AppendAllText(Path.Combine(r, p + "frame.png"), "tamper"));
        SelfCheck("CP-Build unrelated player", "CP-Build", r => Change(r, p + "remapped.json", "\"binary_sha256\": \"", "\"binary_sha256\": \"0"));
        SelfCheck("CP-Separation shared ALPHA bot file", "CP-Separation", r => Write(r, Alpha, Obj("bot_log", Ref(r, p + "keyboard_only.json"))));
        if (args.Length == 1 && args[0] == "self-check-only") return failures == 0 ? 0 : 1;
        if (args.Length == 1 && args[0] == "negative-fixture")
            SelfCheck("negative-fixture keyboard mouse event (expected exit 1)", null, r => Change(r, p + "keyboard_only.json", "\"device\": \"keyboard\"", "\"device\": \"mouse\""));
        else
        {
            try { CursorPixelCheck("test_cursor_pixels.py"); Report(true, "CP-CursorPixels oracle self-checks"); }
            catch (Exception e) { Report(false, e.Message); }
            try { CursorPixelCheck("cursor_pixels.py"); Report(true, "CP-CursorPixels running-build evidence"); }
            catch (Exception e) { Report(false, e.Message); }
            try { Validate(".", true); Report(true, "CP-Receipt running-build evidence (local SEIT coverage)");
                var audio = Read(".", Receipt).GetProperty("narration_audio");
                if (Text(audio, "status") == "GAP") Console.WriteLine("GAP G-7 " + Text(audio, "reason"));
                Console.WriteLine("GAP G-6 independent screenshot pixel re-measurement requires maintainer spot-check"); }
            catch (Exception e) { Report(false, "CP-Receipt SEIT-AC-12(local)/SEIT-PRES-05-P1/SEIT-UI-07-P1 " + e.Message); }
        }
        if (failures == 0) Console.WriteLine("CONTROLS-PLAY ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
