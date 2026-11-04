/* GATE-M1@1 receipt contract (C# 9/System.Text.Json; no engine/display/network).
 * Exact required M1.json schema, extra descriptive fields permitted:
 * {
 *   "schema":"blockparty-m1/1", "gate":"GATE-M1", "status":"PASS",
 *   "claim_scope":"M1 prototype; whole SEIT cases pending", "tree_hash":"<git tree SHA-1>",
 *   "build":{"tree_hash":"...","method":"BuildLinux","target":"StandaloneLinux64",
 *            "exit_code":0,"manifest":REF,"log":REF},
 *   "command_play":{"tree_hash":"...","binary_sha256":"...",
 *                   "bot_log":REF,"manual_log":REF,"save":REF,"reloaded_save":REF,
 *                   "before_state":REF,"after_state":REF},
 *   "manual_ui":{"tree_hash":"...","binary_sha256":"...","input_log":REF,
 *                "flows":[{"id":"title|new|load|inspect|map|follow|takeover","frame":REF},...]},
 *   "integration":{"tree_hash":"...","command":"python3 tests/production/run-standalone.py --unity <editor>",
 *                  "exit_code":0,"log":REF},
 *   "unity_compile":{"tree_hash":"...","command":"<Unity> -batchmode -projectPath <project> -quit ...",
 *                    "exit_code":0,"log":REF},
 *   "packages":[{"package":"<each RequiredPackages entry>","tree_hash":"...",
 *                "command":"python3 tools/verify.py --package <id> --seit docs/plans/issue-3-specification/seit.json ...",
 *                "exit_code":0,"log":REF},...]
 * }
 * REF = {"path":"<repo-relative file>","sha256":"<64 hex>"}; every REF is checked.
 * tree_hash identifies the merged source candidate used for build/play/checks,
 * before evidence-only commits (receipts cannot contain their own Git tree hash).
 * All evidence must be transported into the repo root with hashes preserved.
 * result.json = {"package":"GATE-M1","status":"PASS","tree_hash":"...",
 *   "milestone_sha256":"<M1.json hash>","started_utc":"...","ended_utc":"...","active_hours":number}.
 * Command log JSON = {"tree_hash":"...","binary_sha256":"...","source":"BOT|manual",
 *   "completions":[{"resident_id":"<stable ID>","activity_id":"ACT-FOOD-01|ACT-FOOD-04",
 *     "outcome":"Completed","money_before":number,"money_after":number,
 *     "hunger_before":number,"hunger_after":number},...]}; the two logs together cover eight IDs.
 * These are normalized observations from player command/action/effect logs, not
 * standalone simulation output. Keep raw trace files as additional REFs in the receipt.
 * before_state/after_state are normalized logical state exports around save/reload;
 * equal hashes prove continuity after documented SAVE epoch/action-id normalization.
 * Input log JSON = {"tree_hash":"...","binary_sha256":"...",
 *   "events":[{"flow":"<flow id>","device":"mouse|keyboard","input":"<real input>",
 *              "frame":"<matching frame path>"},...]}; every flow requires an event.
 * UI files (input + frames) must be separate from all command-play files.
 *
 * Local traceability only: AC-20/VER-03-P1 bind play to a build; SYS-01-N1/N2,
 * SUB-05/VER-03-N2 keep prototype claims scoped; SPEC-03/04/07, SYS-01-P1,
 * VER-01-P1/N1/N2 bind the complete package receipt list, not whole spec acceptance.
 * VER-03-N1 covers absent/tampered/shared evidence. No submission, recognition,
 * enjoyment, performance, ten-class completeness or independent assurance claimed.
 * Fixtures are receipt-parser test inputs, never real build/UI acceptance evidence.
 * Self-checks run on fresh GUID temp copies and always clean up. Resource group:
 * isolated-csharp; no shared engine starts, so engine repetition is not applicable.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

public static class GateM1Checks
{
    private const string Milestone = "evidence/milestones/M1.json";
    private const string Result = "evidence/production/GATE-M1/result.json";
    private const string Scope = "M1 prototype; whole SEIT cases pending";
    private static readonly string[] RequiredPackages = { "SEIT-AMEND", "APPEARANCE-AMEND", "TIERS", "BOT",
        "ECONOMY", "RELATION", "FOOD", "UI-TITLE", "UI-INSPECT", "UI-NAV", "GREYBOX", "GATE-M1", "UNITY-COMPILE" };
    private static readonly string[] Flows = { "title", "new", "load", "inspect", "map", "follow", "takeover" };
    private static int failures;

    private static void Require(bool ok, string cases, string detail)
    {
        if (!ok) throw new InvalidDataException(cases + " " + detail);
    }

    private static string Text(JsonElement obj, string key) { return obj.GetProperty(key).GetString(); }
    private static bool Hex(string value, int length)
    {
        if (value == null || value.Length != length) return false;
        foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static JsonElement Read(string root, string path)
    {
        Require(File.Exists(Path.Combine(root, path)), "SEIT-VER-03-N1", "missing " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return doc.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement reference)
    {
        string path = Text(reference, "path"), digest = Text(reference, "sha256");
        Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            && Path.GetFullPath(Path.Combine(root, path)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "SEIT-VER-03-N1", "reference must be repo-relative");
        Require(File.Exists(Path.Combine(root, path)), "SEIT-VER-03-N1", "missing referenced file " + path);
        Require(Hex(digest, 64) && Hash(Path.Combine(root, path)) == digest.ToLowerInvariant(),
            "SEIT-VER-03-N1", "sha256 mismatch " + path);
        return Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, path)));
    }
    private static void AllReferences(string root, JsonElement node, HashSet<string> paths = null)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("path", out _) || node.TryGetProperty("sha256", out _))
            {
                string path = Reference(root, node);
                if (paths != null) paths.Add(path);
            }
            foreach (var p in node.EnumerateObject()) AllReferences(root, p.Value, paths);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) AllReferences(root, item, paths);
    }
    private static void Binding(JsonElement obj, string tree, string binary = null)
    {
        Require(Text(obj, "tree_hash") == tree, "SEIT-AC-20 SEIT-VER-03-P1", "tree hash differs");
        if (binary != null) Require(Text(obj, "binary_sha256") == binary,
            "SEIT-VER-03-P1", "player binary hash differs");
    }
    private static void RunReceipt(string root, JsonElement obj, string tree, string command)
    {
        Binding(obj, tree);
        string actual = Text(obj, "command");
        Require(actual == command || actual.StartsWith(command + " ", StringComparison.Ordinal),
            "SEIT-SPEC-07", "wrong command: " + command);
        Require(obj.GetProperty("exit_code").GetInt32() == 0, "SEIT-SPEC-07", "nonzero exit: " + command);
        Reference(root, obj.GetProperty("log"));
    }
    private static HashSet<string> Commands(string root, JsonElement play, string key, string source, string tree, string binary, HashSet<string> paths)
    {
        var log = Read(root, Reference(root, play.GetProperty(key)));
        AllReferences(root, log, paths);
        Binding(log, tree, binary);
        Require(Text(log, "source") == source, "SEIT-AC-20", "missing " + source + " command evidence");
        var ids = new HashSet<string>();
        foreach (var row in log.GetProperty("completions").EnumerateArray())
        {
            string id = Text(row, "resident_id"), activity = Text(row, "activity_id");
            Require(!string.IsNullOrWhiteSpace(id) && Text(row, "outcome") == "Completed"
                && (activity == "ACT-FOOD-01" || activity == "ACT-FOOD-04"),
                "SEIT-AC-20", "missing completed food routine");
            decimal money = row.GetProperty("money_after").GetDecimal() - row.GetProperty("money_before").GetDecimal();
            decimal hunger = row.GetProperty("hunger_after").GetDecimal() - row.GetProperty("hunger_before").GetDecimal();
            // BP-ACT-03/SEC-05 catalog; fixtures use unclamped starting needs.
            Require((activity == "ACT-FOOD-01" ? money == 0 : money == -18)
                && hunger > 0 && hunger <= (activity == "ACT-FOOD-01" ? 45 : 35),
                "SEIT-AC-20", "food money/needs delta missing or inconsistent");
            ids.Add(id);
        }
        Require(ids.Count > 0, "SEIT-AC-20", source + " completed food evidence missing");
        return ids;
    }
    private static void Validate(string root)
    {
        var m = Read(root, Milestone);
        var r = Read(root, Result);
        Require(Text(m, "schema") == "blockparty-m1/1" && Text(m, "gate") == "GATE-M1"
            && Text(m, "status") == "PASS" && Text(r, "package") == "GATE-M1" && Text(r, "status") == "PASS",
            "SEIT-VER-03-P1", "milestone/result identity or status");
        Require(Text(m, "claim_scope") == Scope, "SEIT-SYS-01-N1 SEIT-SYS-01-N2 SEIT-SUB-05 SEIT-VER-03-N2", "prototype claim scope required");
        string tree = Text(m, "tree_hash");
        Require(Hex(tree, 40), "SEIT-VER-03-P1", "Git source tree hash required");
        Binding(r, tree);
        Require(Text(r, "milestone_sha256") == Hash(Path.Combine(root, Milestone)),
            "SEIT-VER-03-N1", "result does not bind M1.json sha256");
        Require(!string.IsNullOrWhiteSpace(Text(r, "started_utc")) && !string.IsNullOrWhiteSpace(Text(r, "ended_utc"))
            && r.GetProperty("active_hours").GetDecimal() >= 0, "SEIT-SPEC-07", "result timing fields required");
        AllReferences(root, m);
        AllReferences(root, r);
        var build = m.GetProperty("build");
        Binding(build, tree);
        Require(Text(build, "method") == "BuildLinux" && Text(build, "target") == "StandaloneLinux64"
            && build.GetProperty("exit_code").GetInt32() == 0, "SEIT-VER-03-P1", "successful BuildLinux required");
        string manifestPath = Reference(root, build.GetProperty("manifest"));
        string binary = Text(build.GetProperty("manifest"), "sha256");
        var manifest = Read(root, manifestPath);
        Binding(manifest, tree);
        Require(Text(manifest, "command").Contains("-executeMethod ToolchainBuild.BuildLinux"),
            "SEIT-VER-03-P1", "exact BuildLinux command required");
        var payloadPaths = new HashSet<string>(StringComparer.Ordinal);
        bool gameData = false;
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            string path = Text(file, "relative_path");
            Require(!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(path)
                && Path.GetFullPath(Path.Combine(root, path)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Hex(Text(file, "sha256"), 64) && payloadPaths.Add(path),
                "SEIT-VER-03-N1", "invalid payload manifest entry");
            gameData |= path.Contains("_Data/");
        }
        Require(gameData && payloadPaths.Count > 1, "SEIT-VER-03-P1", "complete payload manifest required");
        string payloadRoot = Path.Combine(root, "Build", "ToolchainProof");
        if (Directory.Exists(payloadRoot))
        {
            Require(Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories).Length == payloadPaths.Count,
                "SEIT-VER-03-N1", "payload file list differs from manifest");
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                string path = Path.Combine(payloadRoot, Text(file, "relative_path"));
                Require(File.Exists(path) && Hash(path) == Text(file, "sha256").ToLowerInvariant(),
                    "SEIT-VER-03-N1", "payload sha256 mismatch " + Text(file, "relative_path"));
            }
        }
        Reference(root, build.GetProperty("log"));
        var play = m.GetProperty("command_play");
        Binding(play, tree, binary);
        var commandFiles = new HashSet<string>(StringComparer.Ordinal);
        AllReferences(root, play, commandFiles);
        var bot = Commands(root, play, "bot_log", "BOT", tree, binary, commandFiles);
        var manual = Commands(root, play, "manual_log", "manual", tree, binary, commandFiles);
        bot.UnionWith(manual);
        Require(bot.Count == 8, "SEIT-AC-20", "command logs require eight distinct residents with completed food routines");
        foreach (string key in new[] { "bot_log", "manual_log", "save", "reloaded_save", "before_state", "after_state" })
            commandFiles.Add(Reference(root, play.GetProperty(key)));
        Require(Text(play.GetProperty("bot_log"), "path") != Text(play.GetProperty("manual_log"), "path"),
            "SEIT-AC-20", "BOT/manual require separate logs");
        Require(Text(play.GetProperty("before_state"), "sha256") == Text(play.GetProperty("after_state"), "sha256"),
            "SEIT-AC-20", "save/reload continuity differs");
        Require(Text(play.GetProperty("save"), "path") != Text(play.GetProperty("reloaded_save"), "path")
            && Text(play.GetProperty("before_state"), "path") != Text(play.GetProperty("after_state"), "path"),
            "SEIT-AC-20", "save/reload requires separate before/after files");
        var ui = m.GetProperty("manual_ui");
        Binding(ui, tree, binary);
        string inputPath = Reference(root, ui.GetProperty("input_log"));
        Require(!commandFiles.Contains(inputPath), "SEIT-VER-03-N1", "command and UI evidence share file");
        var input = Read(root, inputPath);
        Binding(input, tree, binary);
        AllReferences(root, input);
        var seenFlows = new HashSet<string>();
        foreach (var flow in ui.GetProperty("flows").EnumerateArray())
        {
            string id = Text(flow, "id"), frame = Reference(root, flow.GetProperty("frame"));
            Require(!commandFiles.Contains(frame) && frame != inputPath, "SEIT-VER-03-N1", "command and UI evidence share file");
            bool observed = false;
            foreach (var e in input.GetProperty("events").EnumerateArray())
                observed |= Text(e, "flow") == id && Text(e, "frame") == frame
                    && (Text(e, "device") == "mouse" || Text(e, "device") == "keyboard")
                    && !string.IsNullOrWhiteSpace(Text(e, "input"));
            Require(observed && seenFlows.Add(id), "SEIT-AC-20", "missing real-input record or duplicate UI flow " + id);
        }
        Require(seenFlows.SetEquals(Flows), "SEIT-AC-20", "title/new/load/inspect/map/follow/takeover UI evidence required");
        RunReceipt(root, m.GetProperty("integration"), tree, "python3 tests/production/run-standalone.py --unity");
        var compile = m.GetProperty("unity_compile");
        Binding(compile, tree);
        string compileCommand = Text(compile, "command");
        Require(compile.GetProperty("exit_code").GetInt32() == 0 && compileCommand.Contains(" -batchmode")
            && compileCommand.Contains(" -projectPath ") && compileCommand.Contains(" -quit"),
            "SEIT-SPEC-07", "clean Unity batchmode compile required");
        Reference(root, compile.GetProperty("log"));
        var packages = new HashSet<string>();
        foreach (var p in m.GetProperty("packages").EnumerateArray())
        {
            string id = Text(p, "package");
            Require(packages.Add(id), "SEIT-SPEC-03 SEIT-VER-01-N2", "duplicate package " + id);
            RunReceipt(root, p, tree, "python3 tools/verify.py --package " + id + " --seit docs/plans/issue-3-specification/seit.json");
        }
        Require(packages.SetEquals(RequiredPackages), "SEIT-SPEC-03 SEIT-SPEC-04 SEIT-SPEC-07 SEIT-SYS-01-P1 SEIT-VER-01-P1 SEIT-VER-01-N1 SEIT-VER-01-N2",
            "13-package gate list incomplete (including UNITY-COMPILE)");
    }
    private static void Report(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static void SelfCheck(string fixture, string name, Action<string> mutate, string expected = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "blockparty-gate-m1-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            foreach (string file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(temp, Path.GetRelativePath(fixture, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination);
            }
            mutate(temp);
            try { Validate(temp); Report(expected == null, name + (expected == null ? "" : " accepted invalid receipt")); }
            catch (Exception e) { Report(expected != null && e is InvalidDataException && e.Message.Contains(expected), name + " " + e.Message); }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
    private static void Rebind(string root)
    {
        string resultPath = Path.Combine(root, Result);
        var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(resultPath));
        result["milestone_sha256"] = JsonSerializer.SerializeToElement(Hash(Path.Combine(root, Milestone)));
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
    }
    public static int Main()
    {
        string fixture = Path.Combine("tests", "production", "fixtures", "GATE-M1");
        SelfCheck(fixture, "SEIT-VER-03-P1 receipt-schema positive (fixture only)", _ => { });
        SelfCheck(fixture, "SEIT-VER-03-N1 missing-file rejected", root => File.Delete(Path.Combine(root, "payload/build.log")), "missing referenced file");
        SelfCheck(fixture, "SEIT-VER-03-N1 sha-mismatch rejected", root => File.AppendAllText(Path.Combine(root, "payload/build.log"), "tamper"), "sha256 mismatch");
        SelfCheck(fixture, "SEIT-AC-20 seven-residents rejected", root =>
        {
            string path = Path.Combine(root, Milestone);
            // Same-size ID substitution preserves a valid log; rebind the modified artifact
            // so this negative reaches the resident oracle instead of failing on its hash.
            foreach (string source in new[] { "bot", "manual" })
            {
                string log = Path.Combine(root, "payload/" + source + ".json");
                string old = Hash(log);
                File.WriteAllText(log, File.ReadAllText(log).Replace("\"resident_id\": \"8\"", "\"resident_id\": \"7\""));
                File.WriteAllText(path, File.ReadAllText(path).Replace(old, Hash(log)));
            }
            Rebind(root);
        }, "eight distinct residents");
        SelfCheck(fixture, "SEIT-VER-03-N1 shared-command-ui-file rejected", root =>
        {
            string path = Path.Combine(root, Milestone);
            var milestone = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
            var ui = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(milestone["manual_ui"].GetRawText());
            var flows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(ui["flows"].GetRawText());
            flows[0]["frame"] = milestone["command_play"].GetProperty("bot_log");
            ui["flows"] = JsonSerializer.SerializeToElement(flows);
            milestone["manual_ui"] = JsonSerializer.SerializeToElement(ui);
            File.WriteAllText(path, JsonSerializer.Serialize(milestone));
            Rebind(root);
        }, "command and UI evidence share file");
        SelfCheck(fixture, "R01 missing-manifest rejected", root => File.Delete(Path.Combine(root, "payload/build-manifest.json")), "missing referenced file");
        SelfCheck(fixture, "R02 manifest-hash-mismatch rejected", root => File.AppendAllText(Path.Combine(root, "payload/build-manifest.json"), " "), "sha256 mismatch");
        SelfCheck(fixture, "R02 game-data-hash-mismatch rejected", root =>
        {
            var manifest = Read(root, "payload/build-manifest.json");
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                string path = Path.Combine(root, "Build/ToolchainProof", Text(file, "relative_path"));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(Path.Combine(root, "payload/player-payload.fixture"), path);
            }
            File.AppendAllText(Path.Combine(root, "Build/ToolchainProof/ToolchainProof_Data/Managed/Assembly-CSharp.dll"), "tamper");
        }, "payload sha256 mismatch");
        SelfCheck(fixture, "R02 manifest-tree-mismatch rejected", root =>
        {
            string path = Path.Combine(root, "payload/build-manifest.json");
            string old = Hash(path);
            var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
            manifest["tree_hash"] = JsonSerializer.SerializeToElement(new string('f', 40));
            File.WriteAllText(path, JsonSerializer.Serialize(manifest));
            string milestone = Path.Combine(root, Milestone);
            File.WriteAllText(milestone, File.ReadAllText(milestone).Replace(old, Hash(path)));
            Rebind(root);
        }, "tree hash differs");
        try { Validate("."); Report(true, "SEIT-AC-20 M1 evidence receipt"); }
        catch (Exception e) { Report(false, "SEIT-AC-20 M1 evidence: " + e.Message); }
        if (failures == 0) Console.WriteLine("GATE-M1 ALL PASS");
        else Console.WriteLine("GATE-M1 FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
