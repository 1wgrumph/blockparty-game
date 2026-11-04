/* GATE-M4 receipt contract (m4/test-engineer.json; GYM-TRAIL amendment).
 * Reuses GATE-M3 REF/run semantics. No engine/display/network; isolated-csharp.
 * packages = all M4 tasks except this gate and owner-deferred AUDIO-CATALOG.
 * sweep = done M0-M3 verify.py tasks; M3.parked_failures is the frozen named
 * baseline, never inferred from current failures. Missing baseline stays red.
 * content uses CONTENT-IMPORT variants/activities/frames and its PASS result.
 * M4 content holds REFs named imported, rights and look; m2/m3 are historical.
 * obligations keeps EMO-05/E3/E4/E6 and deferred AUDIO-CATALOG visible.
 * Fixtures are parser examples using committed payload bytes, not acceptance.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Simulation;

public static class GateM4Checks
{
    private const string Milestone = "evidence/milestones/M4.json";
    private const string Result = "evidence/production/GATE-M4/result.json";
    private const string Imported = "evidence/production/CONTENT-IMPORT/content.json";
    private const string ImportedResult = "evidence/production/CONTENT-IMPORT/result.json";
    private const string Rights = "evidence/production/asset-register-final.json";
    private const string Look = "docs/production/art-direction/look-final-decision.json";
    private static readonly string[] Pending = { "EMO-05", "E3", "E4", "E6", "AUDIO-CATALOG" };
    private static readonly string[] Choices = { "character", "world", "physical_readability", "behavioral_feel" };
    private static readonly Dictionary<string, string> Commands = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> Sweep = new Dictionary<string, string>();
    private static readonly HashSet<string> SeitIds = new HashSet<string>();
    private static readonly HashSet<string> Acts = new HashSet<string>();
    private static string CompileCommand;
    private static int failures;
    private static void Require(bool ok, string name, string detail)
    { if (!ok) throw new InvalidDataException(name + ": " + detail); }
    private static string Text(JsonElement o, string key) { return o.GetProperty(key).GetString(); }
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
        Require(File.Exists(Path.Combine(root, path)), "GM4-Receipt SEIT-VER-03-N1", "missing " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return doc.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement r)
    {
        string p = Text(r, "path"), digest = Text(r, "sha256");
        Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p)
            && Path.GetFullPath(Path.Combine(root, p)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "GM4-Reference SEIT-VER-03-N1", "reference must be repo-relative");
        p = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, p)));
        Require(File.Exists(Path.Combine(root, p)), "GM4-Reference SEIT-VER-03-N1", "missing referenced file " + p);
        Require(Hex(digest, 64) && Hash(Path.Combine(root, p)) == digest.ToLowerInvariant(),
            "GM4-Reference SEIT-VER-03-N1", "sha256 mismatch " + p);
        return p;
    }
    private static void References(string root, JsonElement o, HashSet<string> paths, int jsonDepth = 1)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("path", out _) && o.TryGetProperty("sha256", out _))
            {
                string p = Reference(root, o);
                if (paths.Add(p) && jsonDepth > 0 && Path.GetExtension(p) == ".json") References(root, Read(root, p), paths, jsonDepth - 1);
            }
            foreach (var p in o.EnumerateObject()) References(root, p.Value, paths, jsonDepth);
        }
        else if (o.ValueKind == JsonValueKind.Array) foreach (var v in o.EnumerateArray()) References(root, v, paths, jsonDepth);
    }
    private static void Binding(JsonElement o, string tree)
    { Require(Text(o, "tree_hash") == tree, "GM4-Tree SEIT-AC-20 SEIT-VER-03-P1", "tree hash differs"); }
    private static string CommandCore(string command)
    {
        int start = command.IndexOf("python3 ", StringComparison.Ordinal);
        Require(start == 0 || (start > 0 && command.StartsWith("BLOCKPARTY_UNITY=", StringComparison.Ordinal)),
            "GM4-Command SEIT-SPEC-07", "expected python3 verification command");
        return command.Substring(start);
    }
    private static void RunReceipt(string root, JsonElement r, string tree, string command)
    {
        Binding(r, tree);
        Require(CommandCore(Text(r, "command")) == CommandCore(command), "GM4-Command SEIT-SPEC-07", "wrong verification command");
        Require(r.GetProperty("exit_code").GetInt32() == 0, "GM4-Exit SEIT-SPEC-07", "nonzero exit " + command);
        Reference(root, r.GetProperty("log"));
    }
    private static void CollectIds(JsonElement o)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) SeitIds.Add(id.GetString());
            foreach (var p in o.EnumerateObject()) CollectIds(p.Value);
        }
        else if (o.ValueKind == JsonValueKind.Array) foreach (var v in o.EnumerateArray()) CollectIds(v);
    }
    private static JsonElement Evidence(string root, JsonElement r, string expected, string tree = null)
    {
        Require(Reference(root, r) == expected, "GM4-Evidence SEIT-VER-03-N1", "expected " + expected);
        var o = Read(root, expected);
        if (tree != null) Binding(o, tree);
        return o;
    }
    private static void LoadContract()
    {
        foreach (var t in Read(".", "docs/plans/full-game-rev2/plan.json").GetProperty("tasks").EnumerateArray())
        {
            string id = Text(t, "id"), milestone = Text(t, "milestone");
            string command = Text(t.GetProperty("verification"), "command");
            if (id == "UNITY-COMPILE") CompileCommand = command;
            if (milestone == "M4" && id != "GATE-M4" && id != "AUDIO-CATALOG") Commands.Add(id, command);
            if ((milestone == "M0" || milestone == "M1" || milestone == "M2" || milestone == "M3")
                && Text(t, "status") == "done" && command.Contains("tools/verify.py --package ")) Sweep.Add(id, command);
        }
        Require(Commands.Count == 23 && Commands.ContainsKey("GYM-TRAIL") && CompileCommand != null,
            "GM4-Plan", "expected 23 non-audio M4 packages including GYM-TRAIL");
        CollectIds(Read(".", "docs/plans/issue-3-specification/seit.json"));
        foreach (var row in Read(".", "Assets/Blockparty/Content/action-catalog.json").GetProperty("activities").EnumerateArray())
            Acts.Add(Text(row, "id"));
    }
    private static HashSet<string> Names(JsonElement array)
    {
        var names = new HashSet<string>();
        foreach (var item in array.EnumerateArray()) Require(names.Add(item.GetString()), "GM4-Names", "duplicate name");
        return names;
    }
    private static void Content(string root, JsonElement reference)
    {
        var imported = Evidence(root, reference, Imported);
        var result = Read(root, ImportedResult);
        Require(Text(result, "package") == "CONTENT-IMPORT" && Text(result, "status") == "PASS",
            "GM4-Content", "CONTENT-IMPORT result must PASS");
        var frames = new HashSet<string>();
        foreach (var frame in imported.GetProperty("frames").EnumerateArray())
        {
            Require(frames.Add(Text(frame, "id")), "GM4-Content", "duplicate frame id");
            Reference(root, frame.GetProperty("png"));
        }
        var pairs = new HashSet<string>();
        foreach (var variant in imported.GetProperty("variants").EnumerateArray())
        {
            string body = Text(variant, "body"), stage = Text(variant, "stage"), pair = body + "/" + stage;
            Require(Enum.IsDefined(typeof(BaseBody), body) && Enum.IsDefined(typeof(LifeStage), stage) && pairs.Add(pair),
                "GM4-Content", "invalid or duplicate body/stage");
            Require(frames.Contains(pair), "GM4-Content", "16/16 body-stage frames required; missing " + pair);
        }
        Require(pairs.Count == 16, "GM4-Content", "16/16 body-stage frames required");
        var actions = new HashSet<string>();
        foreach (var action in imported.GetProperty("activities").EnumerateArray())
        {
            string code = Text(action, "id");
            Require(actions.Add(code), "GM4-Content", "duplicate ACT code");
            Require(frames.Contains(code), "GM4-Content", "all ACT frames required; missing " + code);
        }
        Require(actions.SetEquals(Acts), "GM4-Content", "all ACT frames required");
        References(root, imported, new HashSet<string>(), 0);
    }
    private static void Validate(string root)
    {
        var m = Read(root, Milestone);
        Require(Text(m, "schema") == "blockparty-m4/1" && Text(m, "gate") == "GATE-M4" && Text(m, "status") == "PASS",
            "GM4-Identity", "M4 receipt identity/status");
        string tree = Text(m, "tree_hash");
        Require(Hex(tree, 40), "GM4-Tree", "Git source tree required");
        var packages = new HashSet<string>();
        foreach (var p in m.GetProperty("packages").EnumerateArray())
        {
            string id = Text(p, "package");
            Require(Commands.ContainsKey(id) && packages.Add(id), "GM4-Packages", "unexpected or duplicate package " + id);
            RunReceipt(root, p, tree, Commands[id]);
        }
        Require(packages.SetEquals(Commands.Keys), "GM4-Packages", "M4 package list incomplete");
        foreach (string prior in new[] { "m2", "m3" })
            Require(Text(Evidence(root, m.GetProperty(prior), "evidence/milestones/" + prior.ToUpperInvariant() + ".json"), "status") == "PASS",
                "GM4-History", prior + " must PASS");
        var m3 = Read(root, "evidence/milestones/M3.json");
        Require(m3.TryGetProperty("parked_failures", out var parkedList), "GM4-Sweep", "M3 named parked_failures baseline missing");
        var parked = Names(parkedList); var swept = new HashSet<string>(); var failed = new HashSet<string>();
        foreach (var row in m.GetProperty("sweep").EnumerateArray())
        {
            string id = Text(row, "package");
            Require(Sweep.ContainsKey(id) && swept.Add(id), "GM4-Sweep", "unexpected or duplicate sweep package " + id);
            Binding(row, tree);
            Require(CommandCore(Text(row, "command")) == CommandCore(Sweep[id]), "GM4-Command", "wrong sweep command");
            Reference(root, row.GetProperty("log"));
            if (row.GetProperty("exit_code").GetInt32() != 0) failed.Add(id);
        }
        Require(swept.SetEquals(Sweep.Keys) && parked.IsSupersetOf(failed), "GM4-Sweep", "missing sweep package or new sweep failure");
        Require(Names(m.GetProperty("parked_failures")).SetEquals(parked), "GM4-Sweep", "parked baseline differs from M3");
        var integration = m.GetProperty("integration");
        string[] args = CommandCore(Text(integration, "command")).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        Require((args.Length == 4 || args.Length == 6) && args[1] == "tests/production/run-standalone.py"
            && args[2] == "--unity" && !args[3].StartsWith("--", StringComparison.Ordinal)
            && (args.Length == 4 || (args[4] == "--output" && !args[5].StartsWith("--", StringComparison.Ordinal))),
            "GM4-Integration", "full run-standalone required (no --only)");
        RunReceipt(root, integration, tree, Text(integration, "command"));
        string log = File.ReadAllText(Path.Combine(root, Reference(root, integration.GetProperty("log"))));
        Require(Array.IndexOf(log.Replace("\r", "").Split('\n'), "FAILED []") >= 0, "GM4-Integration", "FAILED [] line required");
        RunReceipt(root, m.GetProperty("unity_compile"), tree, CompileCommand);
        var content = m.GetProperty("content");
        Content(root, content.GetProperty("imported"));
        Evidence(root, content.GetProperty("rights"), Rights);
        var look = Evidence(root, content.GetProperty("look"), Look);
        Require(Text(look, "owner") == "William Rumph" && Text(look, "package") == "LOOK-FINAL-APPROVE", "GM4-Look", "owner-ok required");
        foreach (string choice in Choices) Require(Text(look.GetProperty("choice"), choice) == "accept", "GM4-Look", "all four choices must accept");
        string lookPrefix = "evidence/production/LOOK-FINAL-APPROVE/";
        foreach (string capture in new[] { "character-sheet.png", "world-sheet.png", "player-play-m4.mp4" })
        {
            string path = lookPrefix + capture;
            Require(Text(look.GetProperty("bound"), path) == Hash(Path.Combine(root, path)), "GM4-Look", "owner-ok capture hash mismatch");
        }
        foreach (var bound in look.GetProperty("bound").EnumerateObject())
            Reference(root, JsonSerializer.SerializeToElement(Obj("path", bound.Name, "sha256", bound.Value.GetString())));
        var obligations = new HashSet<string>();
        foreach (var o in m.GetProperty("obligations").EnumerateArray())
        {
            Require(Text(o, "status") == "advisory" && Text(o, "claim") == "not-claimed" && SeitIds.Contains(Text(o, "seit_id")),
                "GM4-Obligations", "advisory not-claimed with known SEIT id required");
            obligations.Add(Text(o, "item"));
        }
        Require(obligations.IsSupersetOf(Pending), "GM4-Obligations", "EMO-05/E3/E4/E6/AUDIO obligations required");
        var r = Read(root, Result);
        Require(Text(r, "package") == "GATE-M4" && Text(r, "status") == "PASS", "GM4-Result", "result identity/status");
        Binding(r, tree);
        Require(Text(r, "milestone_sha256") == Hash(Path.Combine(root, Milestone)), "GM4-Result", "milestone sha256 mismatch");
        Require(!string.IsNullOrWhiteSpace(Text(r, "started_utc")) && !string.IsNullOrWhiteSpace(Text(r, "ended_utc"))
            && r.GetProperty("active_hours").GetDecimal() >= 0, "GM4-Result", "result timing required");
        References(root, m, new HashSet<string>(), 0);
        References(root, r, new HashSet<string>(), 0);
    }
    private static Dictionary<string, object> Obj(params object[] pairs)
    {
        var value = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) value.Add((string)pairs[i], pairs[i + 1]);
        return value;
    }
    private static void Write(string root, string path, object value)
    {
        string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, JsonSerializer.Serialize(value));
    }
    private static object Ref(string root, string path) { return Obj("path", path, "sha256", Hash(Path.Combine(root, path))); }
    private static object Run(string root, string tree, string command)
    { return Obj("tree_hash", tree, "command", command, "exit_code", 0, "log", Ref(root, "payload/checks.log")); }
    private static object FixtureContent(string root, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Keep the landed schema; bind fixture references to committed log bytes.
            if (value.TryGetProperty("path", out _) && value.TryGetProperty("sha256", out _))
                return Ref(root, "payload/checks.log");
            var fields = new Dictionary<string, object>();
            foreach (var field in value.EnumerateObject()) fields.Add(field.Name, FixtureContent(root, field.Value));
            return fields;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object>();
            foreach (var item in value.EnumerateArray()) items.Add(FixtureContent(root, item));
            return items;
        }
        return value;
    }
    private static Dictionary<string, object> Fixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        File.Copy("evidence/production/GATE-M3/integration.log", Path.Combine(root, "payload/checks.log"));
        File.Copy("evidence/production/GATE-M3/integration.log", Path.Combine(root, "payload/integration.log"));
        string tree = new string('a', 40);
        var packages = new List<object>(); var sweep = new List<object>();
        foreach (var command in Commands) { var row = (Dictionary<string, object>)Run(root, tree, command.Value); row.Add("package", command.Key); packages.Add(row); }
        foreach (var command in Sweep) { var row = (Dictionary<string, object>)Run(root, tree, command.Value); row.Add("package", command.Key); sweep.Add(row); }
        Write(root, "evidence/milestones/M2.json", Obj("status", "PASS"));
        Write(root, "evidence/milestones/M3.json", Obj("status", "PASS", "parked_failures", new[] { "RIGHTS" }));
        Write(root, Rights, Read(".", "Assets/Blockparty/Content/asset-register.json"));
        Write(root, Imported, FixtureContent(root, Read(".", Imported)));
        Write(root, ImportedResult, Read(".", ImportedResult));
        var choice = new Dictionary<string, object>(); foreach (string key in Choices) choice.Add(key, "accept");
        var bound = new Dictionary<string, object>();
        foreach (string capture in new[] { "character-sheet.png", "world-sheet.png", "player-play-m4.mp4" })
        {
            string path = "evidence/production/LOOK-FINAL-APPROVE/" + capture;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path)));
            File.Copy(capture.EndsWith(".mp4") ? "evidence/production/SLICE/player-play.mp4" : "evidence/production/M3-LOOK/buildings-sheet.png", Path.Combine(root, path));
            bound.Add(path, Hash(Path.Combine(root, path)));
        }
        Write(root, Look, Obj("owner", "William Rumph", "package", "LOOK-FINAL-APPROVE", "choice", choice, "bound", bound));
        var obligations = new List<object>();
        foreach (string item in Pending) obligations.Add(Obj("item", item, "seit_id", "SEIT-AC-20", "status", "advisory", "claim", "not-claimed"));
        var integration = (Dictionary<string, object>)Run(root, tree, "python3 tests/production/run-standalone.py --unity /fixture/unity");
        integration["log"] = Ref(root, "payload/integration.log");
        return Obj("schema", "blockparty-m4/1", "gate", "GATE-M4", "status", "PASS", "tree_hash", tree, "packages", packages, "sweep", sweep,
            "parked_failures", new[] { "RIGHTS" }, "integration", integration, "unity_compile", Run(root, tree, CompileCommand),
            "m2", Ref(root, "evidence/milestones/M2.json"), "m3", Ref(root, "evidence/milestones/M3.json"),
            "content", Obj("imported", Ref(root, Imported), "rights", Ref(root, Rights), "look", Ref(root, Look)), "obligations", obligations);
    }
    private static Dictionary<string, object> First(Dictionary<string, object> m, string key)
    { return (Dictionary<string, object>)((List<object>)m[key])[0]; }
    private static void Rebind(string root, Dictionary<string, object> m, string key, string path)
    { ((Dictionary<string, object>)m["content"])[key] = Ref(root, path); }
    private static void EditContent(string root, Dictionary<string, object> m, string key, Action<List<JsonElement>> mutate)
    {
        var content = new Dictionary<string, object>();
        foreach (var field in Read(root, Imported).EnumerateObject()) content.Add(field.Name, field.Value);
        var rows = new List<JsonElement>(((JsonElement)content[key]).EnumerateArray());
        mutate(rows); content[key] = rows;
        Write(root, Imported, content); Rebind(root, m, "imported", Imported);
    }
    private static void SelfCheck(string name, string expected, Action<string, Dictionary<string, object>> mutate)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-gate-m4-" + Guid.NewGuid().ToString("N"));
        try
        {
            var m = Fixture(root); mutate(root, m); Write(root, Milestone, m);
            Write(root, Result, Obj("package", "GATE-M4", "status", "PASS", "tree_hash", m["tree_hash"],
                "milestone_sha256", Hash(Path.Combine(root, Milestone)), "started_utc", "2026-11-02T00:00:00Z", "ended_utc", "2026-11-02T00:00:01Z", "active_hours", 0));
            try { Validate(root); Report(expected == null, name + " (parser fixture only)"); }
            catch (Exception e) { Report(expected != null && e is InvalidDataException && e.Message.Contains(expected), name + " " + e.Message); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Report(bool ok, string name)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    public static int Main(string[] args)
    {
        try
        {
            LoadContract();
            SelfCheck("GM4-positive-without-audio", null, (r, m) => { });
            foreach (string id in Commands.Keys)
                SelfCheck("GM4-drop-package-" + id, "GM4-Packages", (r, m) => ((List<object>)m["packages"]).RemoveAll(p => (string)((Dictionary<string, object>)p)["package"] == id));
            SelfCheck("GM4-package-exit", "GM4-Exit", (r, m) => First(m, "packages")["exit_code"] = 1);
            SelfCheck("GM4-package-tree", "GM4-Tree", (r, m) => First(m, "packages")["tree_hash"] = new string('b', 40));
            SelfCheck("GM4-package-command", "GM4-Command", (r, m) => First(m, "packages")["command"] = "python3 wrong.py");
            SelfCheck("GM4-tampered-log", "sha256 mismatch", (r, m) => File.AppendAllText(Path.Combine(r, "payload/checks.log"), "tampered"));
            SelfCheck("GM4-missing-log", "missing referenced file", (r, m) => File.Delete(Path.Combine(r, "payload/checks.log")));
            SelfCheck("GM4-external-ref", "repo-relative", (r, m) => First(m, "packages")["log"] = Obj("path", "../escape.log", "sha256", new string('0', 64)));
            SelfCheck("GM4-new-sweep-failure", "GM4-Sweep", (r, m) => First(m, "sweep")["exit_code"] = 1);
            SelfCheck("GM4-parked-sweep-failure", null, (r, m) =>
            { foreach (Dictionary<string, object> row in (List<object>)m["sweep"]) if ((string)row["package"] == "RIGHTS") row["exit_code"] = 1; });
            SelfCheck("GM4-missing-sweep", "GM4-Sweep", (r, m) => ((List<object>)m["sweep"]).RemoveAt(0));
            SelfCheck("GM4-sweep-tree", "GM4-Tree", (r, m) => First(m, "sweep")["tree_hash"] = new string('b', 40));
            SelfCheck("GM4-widen-parked", "GM4-Sweep", (r, m) => m["parked_failures"] = new[] { "RIGHTS", "new-failure" });
            SelfCheck("GM4-missing-parked-baseline", "GM4-Sweep", (r, m) =>
            { Write(r, "evidence/milestones/M3.json", Obj("status", "PASS")); m["m3"] = Ref(r, "evidence/milestones/M3.json"); });
            foreach (string prior in new[] { "m2", "m3" })
                SelfCheck("GM4-history-" + prior, "GM4-History", (r, m) =>
                { string path = "evidence/milestones/" + prior.ToUpperInvariant() + ".json"; Write(r, path, Obj("status", "FAIL")); m[prior] = Ref(r, path); });
            SelfCheck("GM4-integration-only", "GM4-Integration", (r, m) => ((Dictionary<string, object>)m["integration"])["command"] += " --only SAVE-V2");
            SelfCheck("GM4-integration-exit", "GM4-Exit", (r, m) => ((Dictionary<string, object>)m["integration"])["exit_code"] = 1);
            SelfCheck("GM4-integration-log", "GM4-Integration", (r, m) =>
            { File.WriteAllText(Path.Combine(r, "payload/integration.log"), "FAILED ['SAVE-V2']\n"); ((Dictionary<string, object>)m["integration"])["log"] = Ref(r, "payload/integration.log"); });
            SelfCheck("GM4-compile-exit", "GM4-Exit", (r, m) => ((Dictionary<string, object>)m["unity_compile"])["exit_code"] = 1);
            SelfCheck("GM4-content-body-stage", "GM4-Content", (r, m) =>
                EditContent(r, m, "variants", rows => rows.RemoveAt(0)));
            SelfCheck("GM4-content-ACT", "GM4-Content", (r, m) =>
                EditContent(r, m, "activities", rows => rows.RemoveAt(0)));
            foreach (string key in new[] { "variants", "activities", "frames" })
                SelfCheck("GM4-content-duplicate-" + key, "GM4-Content", (r, m) =>
                    EditContent(r, m, key, rows => rows.Add(rows[0])));
            foreach (string id in new[] { "Male/Newborn", "ACT-CLUB-01" })
                SelfCheck("GM4-content-missing-frame-" + id, "GM4-Content", (r, m) =>
                    EditContent(r, m, "frames", rows => rows.RemoveAll(row => Text(row, "id") == id)));
            SelfCheck("GM4-content-result", "GM4-Content", (r, m) =>
                Write(r, ImportedResult, Obj("package", "CONTENT-IMPORT", "status", "FAIL")));
            SelfCheck("GM4-content-hash", "sha256 mismatch", (r, m) => File.AppendAllText(Path.Combine(r, Imported), " "));
            SelfCheck("GM4-rights-hash", "sha256 mismatch", (r, m) => File.AppendAllText(Path.Combine(r, Rights), " "));
            SelfCheck("GM4-owner-revise", "GM4-Look", (r, m) =>
            {
                var look = Read(r, Look); var choices = new Dictionary<string, string>(); foreach (string key in Choices) choices.Add(key, key == "character" ? "revise" : "accept");
                Write(r, Look, Obj("owner", "William Rumph", "package", "LOOK-FINAL-APPROVE", "choice", choices, "bound", look.GetProperty("bound"))); Rebind(r, m, "look", Look);
            });
            SelfCheck("GM4-stale-owner-capture", "GM4-Look", (r, m) => File.AppendAllText(Path.Combine(r, "evidence/production/LOOK-FINAL-APPROVE/character-sheet.png"), "tampered"));
            SelfCheck("GM4-obligations", "GM4-Obligations", (r, m) => ((List<object>)m["obligations"]).Clear());
            SelfCheck("GM4-obligation-claimed", "GM4-Obligations", (r, m) => First(m, "obligations")["claim"] = "PASS");
            SelfCheck("GM4-obligation-unknown-seit", "GM4-Obligations", (r, m) => First(m, "obligations")["seit_id"] = "unknown");
            if (Array.IndexOf(args, "negative-fixture") >= 0)
                SelfCheck("GM4-negative-fixture", null, (r, m) => First(m, "packages")["exit_code"] = 1);
            else if (Array.IndexOf(args, "self-test") < 0) Validate(".");
        }
        catch (Exception e) { Report(false, "GM4-real-receipt " + e.Message); }
        if (failures == 0) Console.WriteLine("GATE-M4 ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
