/* GATE-M3@1 receipt contract, GATE-M1 pattern; isolated-csharp, no engine/display/network.
 * M3.json: {schema:"blockparty-m3/1",gate:"GATE-M3",status:"PASS",tree_hash,
 * parked_failures:[names from the plan-pinned historical M3 sweep],
 * packages:[{package,tree_hash,command,exit_code:0,log:REF}],
 * integration:{tree_hash,command:<full run-standalone>,exit_code:0,log:REF},
 * unity_compile:{tree_hash,command:<UNITY-COMPILE verification>,exit_code:0,log:REF},
 * alpha:REF,manual_ui:REF,m2:REF,
 * obligations:[{item,seit_id,status:"pending",claim:"not-claimed"}]}.
 * Obligation items: final content, PROBE-IMG, PROBE-SAM, IMAGE, VOICE, SAM.
 * REF={path:<repo-relative>,sha256}; alpha/controls/M2 use the task's exact paths.
 * ALPHA and controls receipts carry tree_hash; M2 is an earlier tree with status PASS.
 * Result follows GATE-M1: package,status,tree_hash,milestone_sha256,started_utc,
 * ended_utc,active_hours. Source tree precedes evidence-only commits, as in M1.
 * REFs in the gate documents and their direct JSON children are verified.
 * Historical JSON descendants are not reopened. Shared build/gate_m2
 * metadata is excluded from bot/UI separation, following CONTROLS-PLAY.
 * Fixtures are parser examples using committed GATE-M1 payload bytes, never play
 * or acceptance evidence. Every case gets a fresh GUID temp root, always removed.
 * Local SEIT mapping only; no whole-case, final-content, live-API or assurance PASS.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

public static class GateM3Checks
{
    private const string Milestone = "evidence/milestones/M3.json";
    private const string Result = "evidence/production/GATE-M3/result.json";
    private const string Alpha = "evidence/production/ALPHA/alpha.json";
    private const string Controls = "evidence/production/CONTROLS-PLAY/controls.json";
    private const string M2 = "evidence/milestones/M2.json";
    private const string ParkedSweep = "tests/production/fixtures/GATE-M3/parked-sweep.txt";
    private static readonly HashSet<string> Parked = new HashSet<string>(StringComparer.Ordinal);
    private static readonly string[] Pending = { "final content", "PROBE-IMG", "PROBE-SAM", "IMAGE", "VOICE", "SAM" };
    private static readonly Dictionary<string, string> Commands = new Dictionary<string, string>();
    private static readonly HashSet<string> SeitIds = new HashSet<string>();
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
        Require(File.Exists(Path.Combine(root, path)), "GM3-Receipt SEIT-VER-03-N1", "missing " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return doc.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement r)
    {
        string p = Text(r, "path"), digest = Text(r, "sha256");
        Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p)
            && Path.GetFullPath(Path.Combine(root, p)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "GM3-Reference SEIT-VER-03-N1", "reference must be repo-relative");
        p = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, p)));
        Require(File.Exists(Path.Combine(root, p)), "GM3-Reference SEIT-VER-03-N1", "missing referenced file " + p);
        Require(Hex(digest, 64) && Hash(Path.Combine(root, p)) == digest.ToLowerInvariant(),
            "GM3-Reference SEIT-VER-03-N1", "sha256 mismatch " + p);
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
    { Require(Text(o, "tree_hash") == tree, "GM3-Tree SEIT-AC-20 SEIT-VER-03-P1", "tree hash differs"); }
    private static void AncestorBinding(string root, JsonElement o, string tree)
    {
        const string name = "GM3-Tree SEIT-AC-20 SEIT-VER-03-P1";
        string previous = Text(o, "tree_hash"), output;
        Require(Hex(previous, 40), name, "Git receipt tree hash required");
        Require(Process(root, new[] { "rev-parse", "--verify", previous + "^{tree}" }, out output) == 0,
            name, "receipt tree must resolve");
        previous = output.Trim();
        Require(Process(root, new[] { "rev-parse", "--verify", tree + "^{tree}" }, out output) == 0, name, "M3 source tree must resolve");
        tree = output.Trim();
        string sources;
        Require(SourceTrees(root, previous, out sources), name, "receipt tree must be an ancestor of the M3 source");
        Require(Process(root, new[] { "log", "--all", "--format=%H %T" }, out output) == 0, name, "cannot resolve source trees: " + output);
        var parents = new List<string>(); var candidates = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string[] bits = line.Trim().Split(' ');
            if (bits.Length != 2) continue;
            if (bits[1] == previous) parents.Add(bits[0]);
            if (bits[1] == tree) candidates.Add(bits[0]);
        }
        foreach (string parent in parents) foreach (string candidate in candidates)
            if (Process(root, new[] { "merge-base", "--is-ancestor", parent, candidate }, out output) == 0) return;
        foreach (string candidate in candidates)
        {
            Require(Process(root, new[] { "rev-list", candidate }, out output) == 0, name, "cannot resolve source ancestors: " + output);
            foreach (string ancestor in output.Split('\n'))
                if (ancestor.Length > 0 && SourceTrees(root, ancestor.Trim(), out string ancestorSources) && sources == ancestorSources) return;
        }
        Require(false, name, "receipt tree must be an ancestor of the M3 source");
    }
    private static bool SourceTrees(string root, string tree, out string output)
    {
        return Process(root, new[] { "rev-parse", tree + ":Assets", tree + ":Packages", tree + ":ProjectSettings" }, out output) == 0;
    }
    private static string CommandCore(string command)
    {
        int start = command.IndexOf("python3 ", StringComparison.Ordinal);
        Require(start == 0 || (start > 0 && command.StartsWith("BLOCKPARTY_UNITY=", StringComparison.Ordinal)),
            "GM3-Command SEIT-SPEC-07", "expected python3 verification command");
        return command.Substring(start);
    }
    private static void RunReceipt(string root, JsonElement r, string tree, string command)
    {
        Binding(r, tree);
        Require(CommandCore(Text(r, "command")) == CommandCore(command), "GM3-Command SEIT-SPEC-07", "wrong verification command");
        Require(r.GetProperty("exit_code").GetInt32() == 0, "GM3-Exit SEIT-SPEC-07", "nonzero exit " + command);
        Reference(root, r.GetProperty("log"));
    }
    private static void LoadContract()
    {
        var plan = Read(".", "docs/plans/full-game-rev2/plan.json");
        // baseline_disposition parks only the nonzero exits of the plan-pinned M3 sweep.
        Require(Hash(ParkedSweep) == Text(plan.GetProperty("m3_revision").GetProperty("merged_tree_acceptance"), "baseline_sha256"),
            "GM3-Parked", "M3 sweep baseline hash differs from plan.json");
        var failed = new HashSet<string>();
        foreach (string line in File.ReadAllLines(ParkedSweep))
        {
            string[] row = line.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (int.Parse(row[1]) != 0) failed.Add(row[0]);
        }
        foreach (var t in plan.GetProperty("tasks").EnumerateArray())
        {
            string id = Text(t, "id");
            if (Array.IndexOf(new[] { "DEMO", "M0", "M1", "M2", "M3" }, Text(t, "milestone")) >= 0 && failed.Contains(id)) Parked.Add(id);
            if (id == "UNITY-COMPILE") CompileCommand = Text(t.GetProperty("verification"), "command");
            if (Text(t, "milestone") == "M3" && id != "GATE-M3") Commands.Add(id, Text(t.GetProperty("verification"), "command"));
        }
        Require(Commands.Count == 35 && CompileCommand != null, "GM3-Plan SEIT-SYS-01-P1", "expected 35 M3 predecessors and UNITY-COMPILE");
        CollectIds(Read(".", "docs/plans/issue-3-specification/seit.json"));
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
        Require(Reference(root, r) == expected, "GM3-Evidence SEIT-VER-03-N1", "expected " + expected);
        var o = Read(root, expected);
        if (tree != null) AncestorBinding(root, o, tree);
        return o;
    }
    private static HashSet<string> PlayFiles(string root, JsonElement receipt)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in receipt.EnumerateObject())
            if (p.Name != "build" && p.Name != "gate_m2") References(root, p.Value, files, int.MaxValue);
        Require(files.Count > 0, "GM3-Play SEIT-AC-20", "play evidence references required");
        return files;
    }
    private static void Validate(string root)
    {
        var m = Read(root, Milestone);
        Require(Text(m, "schema") == "blockparty-m3/1" && Text(m, "gate") == "GATE-M3" && Text(m, "status") == "PASS",
            "GM3-Identity SEIT-VER-03-P1", "M3 receipt identity/status");
        string tree = Text(m, "tree_hash");
        Require(Hex(tree, 40), "GM3-Tree SEIT-VER-03-P1", "Git source tree hash required");
        Require(m.TryGetProperty("parked_failures", out var parked) && parked.ValueKind == JsonValueKind.Array,
            "GM3-Parked", "parked_failures array required");
        foreach (var entry in parked.EnumerateArray())
            Require(entry.ValueKind == JsonValueKind.String && Parked.Contains(entry.GetString()),
                "GM3-Parked", "package is not parked by the plan-pinned M3 sweep: " + entry);
        var packages = new HashSet<string>();
        foreach (var p in m.GetProperty("packages").EnumerateArray())
        {
            string id = Text(p, "package");
            Require(Commands.ContainsKey(id) && packages.Add(id), "GM3-Packages SEIT-SPEC-07 SEIT-SYS-01-P1", "unexpected or duplicate package " + id);
            RunReceipt(root, p, tree, Commands[id]);
        }
        Require(packages.SetEquals(Commands.Keys), "GM3-Packages SEIT-SPEC-07 SEIT-SYS-01-P1", "M3 package list incomplete");
        var integration = m.GetProperty("integration");
        string[] args = CommandCore(Text(integration, "command")).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        Require((args.Length == 4 || args.Length == 6) && args[1] == "tests/production/run-standalone.py"
            && args[2] == "--unity" && !args[3].StartsWith("--", StringComparison.Ordinal)
            && (args.Length == 4 || (args[4] == "--output" && !args[5].StartsWith("--", StringComparison.Ordinal))
                // M3's build leaves out only the later GATE-M4 suite, which reads M3's own integration log.
                || (args[4] == "--skip" && args[5] == "GATE-M4")),
            "GM3-Integration SEIT-SPEC-07", "full run-standalone required (no --only)");
        RunReceipt(root, integration, tree, Text(integration, "command"));
        RunReceipt(root, m.GetProperty("unity_compile"), tree, CompileCommand);
        var alpha = Evidence(root, m.GetProperty("alpha"), Alpha, tree);
        var controls = Evidence(root, m.GetProperty("manual_ui"), Controls, tree);
        Require(!PlayFiles(root, controls).Overlaps(PlayFiles(root, alpha)),
            "GM3-Separation SEIT-AC-20 SEIT-VER-03-N1", "shared bot/UI file");
        Require(Text(Evidence(root, m.GetProperty("m2"), M2), "status") == "PASS", "GM3-M2 SEIT-SPEC-07", "M2 must PASS");
        var pending = new HashSet<string>();
        foreach (var o in m.GetProperty("obligations").EnumerateArray())
        {
            Require(Text(o, "status") == "pending" && Text(o, "claim") == "not-claimed" && SeitIds.Contains(Text(o, "seit_id")),
                "GM3-Obligations SEIT-SUB-05 SEIT-SYS-01-N1 SEIT-SYS-01-N2 SEIT-VER-03-N2", "not-claimed pending obligation with known SEIT id required");
            pending.Add(Text(o, "item"));
        }
        Require(pending.IsSupersetOf(Pending), "GM3-Obligations SEIT-SUB-05 SEIT-VER-03-N2", "final content and connected API obligations required");
        var r = Read(root, Result);
        Require(Text(r, "package") == "GATE-M3" && Text(r, "status") == "PASS", "GM3-Result SEIT-SPEC-07", "result identity/status");
        Binding(r, tree);
        Require(Text(r, "milestone_sha256") == Hash(Path.Combine(root, Milestone)), "GM3-Result SEIT-VER-03-N1", "result milestone hash differs");
        Require(!string.IsNullOrWhiteSpace(Text(r, "started_utc")) && !string.IsNullOrWhiteSpace(Text(r, "ended_utc"))
            && r.GetProperty("active_hours").GetDecimal() >= 0, "GM3-Result SEIT-SPEC-07", "result timing required");
        References(root, m, new HashSet<string>());
        References(root, r, new HashSet<string>());
    }
    private static int Process(string root, string[] args, out string output)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Path.GetFullPath(root), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using (var p = System.Diagnostics.Process.Start(start))
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { p.Kill(); p.WaitForExit(); throw new InvalidDataException("git timed out"); }
            output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(); return p.ExitCode;
        }
    }
    private static string Git(string root, params string[] args)
    {
        string output;
        Require(Process(root, args, out output) == 0, "GM3-Git", output);
        return output.Trim();
    }
    private static string FixtureCommit(string root, string parent = null)
    {
        string tree = Git(root, "write-tree");
        var args = new List<string> { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit-tree", tree, "-m", "parser fixture" };
        if (parent != null) { args.Add("-p"); args.Add(parent); }
        string commit = Git(root, args.ToArray());
        Git(root, "update-ref", "refs/heads/fixture", commit);
        return commit;
    }
    private static Dictionary<string, object> Obj(params object[] pairs)
    { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d; }
    private static void Write(string root, string path, object o)
    {
        string p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, JsonSerializer.Serialize(o));
    }
    private static object Ref(string root, string p) { return Obj("path", p, "sha256", Hash(Path.Combine(root, p))); }
    private static object Run(string root, string tree, string command)
    { return Obj("tree_hash", tree, "command", command, "exit_code", 0, "log", Ref(root, "payload/checks.log")); }
    private static Dictionary<string, object> Fixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        foreach (string file in new[] { "checks.log", "bot.json", "input.json", "capture.png" })
            File.Copy("tests/production/fixtures/GATE-M1/payload/" + file, Path.Combine(root, "payload", file));
        // Real Git history over committed payload bytes; no engine or repository refs are shared.
        Git(root, "init", "-q", "--initial-branch=fixture");
        foreach (string source in new[] { "Assets", "Packages", "ProjectSettings" })
            Write(root, source + "/source.json", Obj("fixture", source));
        Git(root, "add", "payload/checks.log", "Assets", "Packages", "ProjectSettings");
        string parent = FixtureCommit(root);
        Git(root, "add", "payload/bot.json");
        parent = FixtureCommit(root, parent);
        File.AppendAllText(Path.Combine(root, "Assets/source.json"), "\n");
        Git(root, "add", "payload/input.json", "payload/capture.png", "Assets");
        string tree = FixtureCommit(root, parent);
        Write(root, Alpha, Obj("tree_hash", tree, "bot_log", Ref(root, "payload/bot.json")));
        Write(root, Controls, Obj("tree_hash", tree, "input_log", Ref(root, "payload/input.json"), "frame", Ref(root, "payload/capture.png")));
        Write(root, M2, Obj("status", "PASS"));
        var packages = new List<object>();
        foreach (var p in Commands)
        {
            var row = (Dictionary<string, object>)Run(root, tree, p.Value.Replace("<pinned Unity 6000.3.10f1 install root>", "/fixture/unity"));
            row.Add("package", p.Key); packages.Add(row);
        }
        var obligations = new List<object>();
        foreach (string item in Pending) obligations.Add(Obj("item", item, "seit_id", "SEIT-SUB-05", "status", "pending", "claim", "not-claimed"));
        return Obj("schema", "blockparty-m3/1", "gate", "GATE-M3", "status", "PASS", "tree_hash", tree, "packages", packages,
            "parked_failures", new List<string>(Parked),
            "integration", Run(root, tree, "python3 tests/production/run-standalone.py --unity /fixture/unity"),
            "unity_compile", Run(root, tree, CompileCommand), "alpha", Ref(root, Alpha), "manual_ui", Ref(root, Controls),
            "m2", Ref(root, M2), "obligations", obligations);
    }
    private static void Report(bool ok, string name)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    private static void SelfCheck(string name, string expected, Action<string, Dictionary<string, object>> mutate)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-gate-m3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var m = Fixture(root); mutate(root, m); Write(root, Milestone, m);
            Write(root, Result, Obj("package", "GATE-M3", "status", "PASS", "tree_hash", m["tree_hash"],
                "milestone_sha256", Hash(Path.Combine(root, Milestone)), "started_utc", "2026-10-29T00:00:00Z", "ended_utc", "2026-10-29T00:00:01Z", "active_hours", 0));
            try { Validate(root); Report(expected == null, name + (expected == null ? " (parser fixture only)" : " accepted invalid receipt")); }
            catch (Exception e) { Report(expected != null && e is InvalidDataException && e.Message.Contains(expected), name + " " + e.Message); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static Dictionary<string, object> FirstPackage(Dictionary<string, object> m)
    { return (Dictionary<string, object>)((List<object>)m["packages"])[0]; }
    public static int Main(string[] args)
    {
        try
        {
            LoadContract();
            SelfCheck("GM3-positive", null, (r, m) => { });
            SelfCheck("GM3-empty-parked-failures", null, (r, m) => m["parked_failures"] = new string[0]);
            SelfCheck("GM3-missing-parked-failures", "GM3-Parked", (r, m) => m.Remove("parked_failures"));
            SelfCheck("GM3-non-parked-package", "GM3-Parked", (r, m) => m["parked_failures"] = new[] { "RIGHTS" });
            SelfCheck("GM3-ancestor-trees", null, (r, m) =>
            {
                Write(r, Alpha, Obj("tree_hash", Git(r, "rev-parse", "HEAD~2^{tree}"), "bot_log", Ref(r, "payload/bot.json")));
                Write(r, Controls, Obj("tree_hash", Git(r, "rev-parse", "HEAD~1^{tree}"), "input_log", Ref(r, "payload/input.json"), "frame", Ref(r, "payload/capture.png")));
                m["alpha"] = Ref(r, Alpha); m["manual_ui"] = Ref(r, Controls);
            });
            SelfCheck("GM3-unrelated-tree", "GM3-Tree", (r, m) =>
            {
                // Neither the orphan tree nor its changed source matches an ancestor.
                Write(r, "Assets/source.json", Obj("unrelated", true));
                Git(r, "add", Alpha, "Assets");
                string orphan = FixtureCommit(r);
                Git(r, "update-ref", "refs/heads/unrelated", orphan);
                Git(r, "update-ref", "refs/heads/fixture", (string)m["tree_hash"]);
                Write(r, Alpha, Obj("tree_hash", orphan, "bot_log", Ref(r, "payload/bot.json")));
                m["alpha"] = Ref(r, Alpha);
            });
            SelfCheck("GM3-uncommitted-source-trees", null, (r, m) =>
            {
                Git(r, "read-tree", "HEAD~1");
                Git(r, "add", Alpha, Controls);
                string uncommitted = Git(r, "write-tree");
                Require(!Git(r, "log", "--all", "--format=%T").Contains(uncommitted), "GM3-Fixture", "receipt tree must be uncommitted");
                Write(r, Alpha, Obj("tree_hash", uncommitted, "bot_log", Ref(r, "payload/bot.json")));
                Write(r, Controls, Obj("tree_hash", uncommitted, "input_log", Ref(r, "payload/input.json"), "frame", Ref(r, "payload/capture.png")));
                m["alpha"] = Ref(r, Alpha); m["manual_ui"] = Ref(r, Controls);
            });
            SelfCheck("GM3-changed-source-tree", "receipt tree must be an ancestor of the M3 source", (r, m) =>
            {
                Git(r, "read-tree", "HEAD~1");
                Write(r, "Assets/source.json", Obj("changed", true));
                Git(r, "add", Alpha, "Assets");
                Write(r, Alpha, Obj("tree_hash", Git(r, "write-tree"), "bot_log", Ref(r, "payload/bot.json")));
                m["alpha"] = Ref(r, Alpha);
            });
            foreach (string source in new[] { "Assets", "Packages", "ProjectSettings" })
                SelfCheck("GM3-missing-source-" + source, "receipt tree must be an ancestor of the M3 source", (r, m) =>
                {
                    Git(r, "rm", "--cached", "-r", source);
                    Write(r, Alpha, Obj("tree_hash", Git(r, "write-tree"), "bot_log", Ref(r, "payload/bot.json")));
                    m["alpha"] = Ref(r, Alpha);
                });
            SelfCheck("GM3-path-only-row", null, (r, m) => m["samples"] = Read(".", "evidence/production/ART-ITERATE/result.json").GetProperty("samples"));
            SelfCheck("GM3-sha-only-row", null, (r, m) => m["files"] = Read(".", "evidence/production/CONTROLS-PLAY/build-log.json").GetProperty("files"));
            SelfCheck("GM3-direct-bad-hash", "sha256 mismatch", (r, m) => m["extra"] = Obj("path", "payload/capture.png", "sha256", new string('0', 64)));
            SelfCheck("GM3-historical-stale-ref", null, (r, m) =>
            {
                Write(r, "payload/historical.json", Obj("sdoc", Obj("path", "payload/checks.log", "sha256", new string('0', 64))));
                Write(r, M2, Obj("status", "PASS", "package", Ref(r, "payload/historical.json")));
                m["m2"] = Ref(r, M2);
            });
            SelfCheck("GM3-one-level-bad-hash", "sha256 mismatch", (r, m) =>
            {
                Write(r, M2, Obj("status", "PASS", "package", Obj("path", "payload/bot.json", "sha256", new string('0', 64))));
                m["m2"] = Ref(r, M2);
            });
            SelfCheck("GM3-package-exit-one", "GM3-Exit", (r, m) => FirstPackage(m)["exit_code"] = 1);
            SelfCheck("GM3-missing-controls", "missing referenced file", (r, m) => File.Delete(Path.Combine(r, Controls)));
            SelfCheck("GM3-shared-bot-ui", "GM3-Separation", (r, m) =>
            {
                Write(r, Controls, Obj("tree_hash", m["tree_hash"], "input_log", Ref(r, "payload/bot.json")));
                m["manual_ui"] = Ref(r, Controls);
            });
            SelfCheck("GM3-empty-obligations", "GM3-Obligations", (r, m) => m["obligations"] = new object[0]);
            SelfCheck("GM3-wrong-tree", "GM3-Tree", (r, m) => FirstPackage(m)["tree_hash"] = new string('0', 40));
            SelfCheck("GM3-wrong-command", "GM3-Command", (r, m) => FirstPackage(m)["command"] = "python3 wrong.py");
            SelfCheck("GM3-missing-package", "GM3-Packages", (r, m) => ((List<object>)m["packages"]).RemoveAt(0));
            SelfCheck("GM3-duplicate-package", "GM3-Packages", (r, m) => ((List<object>)m["packages"]).Add(FirstPackage(m)));
            SelfCheck("GM3-tampered-log", "sha256 mismatch", (r, m) => File.AppendAllText(Path.Combine(r, "payload/checks.log"), "tamper"));
            SelfCheck("GM3-integration-only", "GM3-Integration", (r, m) => ((Dictionary<string, object>)m["integration"])["command"] += " --only SAVE-V2");
            SelfCheck("GM3-integration-skip-other", "GM3-Integration", (r, m) => ((Dictionary<string, object>)m["integration"])["command"] += " --skip SAVE-V2");
            SelfCheck("GM3-integration-exit-one", "GM3-Exit", (r, m) => ((Dictionary<string, object>)m["integration"])["exit_code"] = 1);
            SelfCheck("GM3-compile-exit-one", "GM3-Exit", (r, m) => ((Dictionary<string, object>)m["unity_compile"])["exit_code"] = 1);
            SelfCheck("GM3-missing-alpha", "missing referenced file", (r, m) => File.Delete(Path.Combine(r, Alpha)));
            SelfCheck("GM3-m2-not-pass", "GM3-M2", (r, m) => { Write(r, M2, Obj("status", "pending")); m["m2"] = Ref(r, M2); });
            SelfCheck("GM3-obligation-claimed", "GM3-Obligations", (r, m) => ((Dictionary<string, object>)((List<object>)m["obligations"])[0])["claim"] = "PASS");
            SelfCheck("GM3-obligation-unknown-seit", "GM3-Obligations", (r, m) => ((Dictionary<string, object>)((List<object>)m["obligations"])[0])["seit_id"] = "SEIT-unknown");
            if (Array.IndexOf(args, "negative-fixture") >= 0)
                SelfCheck("GM3-negative-fixture", null, (r, m) => FirstPackage(m)["exit_code"] = 1);
            else if (Array.IndexOf(args, "self-checks-only") < 0) Validate(".");
        }
        catch (Exception e) { Report(false, "GM3-real-receipt " + e.Message); }
        if (failures == 0) Console.WriteLine("GATE-M3 ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
