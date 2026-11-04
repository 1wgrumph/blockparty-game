/* GATE-M2@1 executable receipt contract; local inspection, not whole-game acceptance.
 * Uses committed repository files only; no engine/display/network or evidence writes.
 * Resource group isolated-csharp. New module: M1 cannot qualify the M2 asset/rights join.
 *
 * Developer layout (REF = {path: repo-relative file, sha256: 64 hex}):
 * evidence/production/GATE-M2/result.json:
 *   package:"GATE-M2", status:"PASS", tree_hash, milestone_sha256,
 *   started_utc, ended_utc, active_hours (nonnegative number).
 * evidence/milestones/M2.json:
 *   schema:"blockparty-m2/1", gate:"GATE-M2", status:"PASS", tree_hash,
 *   claim_scope:"M2 representative corner; whole-game acceptance pending",
 *   spec:REF (current SDoc), package_verify:REF (path below),
 *   qualifications:{makehuman:[BODY-MH,RIG-MH,MATERIAL-MH,BODY-PRESET],
 *                   ual:[ANIM-UAL], recorded_cc0:[AUDIO-CC0]},
 *   historical_demo_receipts:[REF M0.json, REF M1.json],
 *   timings:{SECOND-CHAR:throughput, SECOND-CLOTH:throughput, SECOND-ANIM:throughput,
 *            SECOND-ENV:throughput, SECOND-PROP:throughput, SECOND-AUDIO:throughput},
 *   quality:{slice:REF slice.json, captures:[REF,...], player:REF player-play.mp4,
 *            decision:REF docs/production/style-feel-decision.json},
 *   live_service:{service:"d1-ultra", status:"not_run", counted_as_pass:false},
 *   incomplete_game_obligations:[{milestone:"M3|M4|M5|M6", seit_id:"SEIT-...",
 *                                 status:"pending", obligation:nonempty text},...],
 *   rights:[{export:REF, provenance:REF under production/provenance/},...].
 * Each later milestone needs an obligation; the M4 entries retain physical readability
 * and behavioral feel revisions when the bound owner decision says "revise".
 * Captures include slice.png, slice-room.png, slice-apartment.png and all six fit views.
 * Rights cover every assets[] export in the bound SLICE receipt, including owned world
 * exports; a glTF extras fallback is deliberately insufficient for this gate's contract.
 *
 * evidence/production/GATE-M2/package-verify.json:
 *   tree_hash, current:[{package, result:REF, files:{path:sha256,...}, tree_hash,
 *     command:"python3 tools/verify.py --package <ID> --seit docs/plans/issue-3-specification/seit.json",
 *     exit_code:0, log:REF},...],
 *   historical:[{package:"BLOCK-CHOICE|SEIT-AMEND", commit:"89b6073|95a87ec",
 *     command:"git archive ..." (includes --root), exit_code:0, log:REF,
 *     result:REF},...],
 *   gaps:[{package, status:"blocked-by-parked-lane", counted_as_pass:false, reason},...].
 * current covers all plan M2 packages except GATE-M2 and historical BLOCK-CHOICE.
 * files binds every path in each producer's files map to its CURRENT hash, not its
 * historical hash. New qualification can rebind evolved source without editing done
 * receipts. SPEC-M2's original SDoc is historical; spec above binds today's SDoc.
 * Any parked-lane gap is visible and prevents PASS. Historical/demo/live are never
 * substituted for current MakeHuman/UAL/recorded-CC0 qualification.
 * Package logs live under evidence/production/GATE-M2/; hashes are checked.
 * Each log names its command, ends with N/N checks passed and contains no FAIL output.
 * tree_hash resolves to a commit at HEAD or in its ancestry. Current producer hashes
 * detect stale qualified content while unrelated successor commits remain valid.
 * After committing the real evidence, run the full standalone suite externally;
 * requiring its own successful log here would create a circular first-pass dependency.
 * Self-checks use landed corpus inputs, in-memory mutations and disposable Git copies, never acceptance evidence.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class GateM2Checks
{
    private const string Result = "evidence/production/GATE-M2/result.json";
    private const string Milestone = "evidence/milestones/M2.json";
    private const string PackageVerify = "evidence/production/GATE-M2/package-verify.json";
    private const string Sdoc = "migrations/blockparty/game/blockparty-game-system.sdoc";
    private const string Slice = "evidence/production/SLICE/slice.json";
    private const string Decision = "docs/production/style-feel-decision.json";
    private static readonly string[] CurrentQualification = { "SPEC-M2", "BODY-MH", "RIG-MH", "MATERIAL-MH",
        "BODY-PRESET", "ANIM-UAL", "CLOTH-FIRST", "ENV-FIRST", "SECOND-CHAR", "SECOND-CLOTH",
        "SECOND-ANIM", "SECOND-ENV", "SECOND-PROP", "AUDIO-CC0", "SLICE", "STYLE-FEEL" };
    private static readonly Dictionary<string, string[]> TimingKeys = new Dictionary<string, string[]>
    {
        ["SECOND-CHAR"] = new[] { "rig_repair_hours", "uv_repair_hours" },
        ["SECOND-CLOTH"] = new[] { "skin_correction_hours", "clipping_correction_hours" },
        ["SECOND-ANIM"] = new[] { "retarget_hours", "pose_correction_hours", "held_prop_correction_hours" },
        ["SECOND-ENV"] = new[] { "export_import_hours", "nav_repair_hours", "lod_repair_hours" },
        ["SECOND-PROP"] = new[] { "mesh_hours", "material_hours", "slot_qualification_hours" },
        ["SECOND-AUDIO"] = new[] { "editing_hours", "loudness_hours", "import_hours", "editing_minutes", "import_seconds" }
    };
    private static readonly HashSet<string> SharedSource = new HashSet<string> { "tools/verify.py",
        "tools/check-sdoc-bindings.py", "docs/plans/issue-3-specification/seit.json", "docs/specification/placement.json",
        "Assets/Blockparty/Presentation/AudioMixer.cs", "Assets/Blockparty/Presentation/AudioMixer.cs.meta",
        "tests/production/UI-TITLE.cs" };
    private static readonly Dictionary<string, string> Committed = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> Hashes = new Dictionary<string, string>();
    private static readonly Dictionary<string, byte[]> Pinned = new Dictionary<string, byte[]>();
    private static string ReceiptTree;
    private static int failures;

    private static void Require(bool ok, string message)
    { if (!ok) throw new InvalidDataException(message); }
    private static string Text(JsonElement node, string key) { return node.GetProperty(key).GetString(); }
    private static bool Hex(string value, int length)
    {
        if (value == null || value.Length != length) return false;
        foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    private static string Digest(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    private static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "qualified source git check failed: " + error);
            return output.TrimEnd('\r', '\n');
        }
    }
    private static void QualifiedSource(string source)
    {
        Require(Hex(source, 40), "qualified source commit required");
        Git("cat-file", "-e", source + "^{commit}");
        Git("merge-base", "--is-ancestor", source, "HEAD");
    }
    private static void LoadCommitted()
    {
        using (var process = Process.Start(new ProcessStartInfo("git", "ls-tree -r HEAD")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
        {
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "cannot inventory committed files: " + error);
            foreach (string line in output.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                var info = line.Substring(0, tab).Split(' ');
                if (info[0] == "100644" || info[0] == "100755") Committed.Add(line.Substring(tab + 1), info[2]);
            }
        }
    }
    private static string Hash(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            && Path.GetRelativePath(".", path) == path && !path.StartsWith("../", StringComparison.Ordinal),
            "repo-relative path required: " + path);
        Require(File.Exists(path), "missing " + path);
        Require(Committed.ContainsKey(path), "uncommitted evidence " + path);
        if (Hashes.TryGetValue(path, out var cached)) return cached;
        using (var sha = SHA256.Create()) using (var blob = SHA1.Create()) using (var stream = File.OpenRead(path))
        {
            byte[] prefix = Encoding.UTF8.GetBytes("blob " + stream.Length + "\0"), buffer = new byte[65536];
            blob.TransformBlock(prefix, 0, prefix.Length, prefix, 0);
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, count, buffer, 0);
                blob.TransformBlock(buffer, 0, count, buffer, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); blob.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            Require(Digest(blob.Hash) == Committed[path], "file differs from committed content " + path);
            return Hashes[path] = Digest(sha.Hash);
        }
    }
    private static byte[] RecordedBytes(string path)
    {
        if (ReceiptTree == null) return null;
        var start = new ProcessStartInfo("git")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("cat-file");
        start.ArgumentList.Add("blob");
        start.ArgumentList.Add(ReceiptTree + ":" + path);
        using (var process = Process.Start(start))
        using (var output = new MemoryStream())
        {
            process.StandardOutput.BaseStream.CopyTo(output);
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
    }
    private static JsonElement Read(string path)
    {
        if (!Pinned.TryGetValue(path, out var bytes))
        {
            Hash(path);
            bytes = File.ReadAllBytes(path);
        }
        using (var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
        using (var doc = JsonDocument.Parse(text.ReadToEnd())) return doc.RootElement.Clone();
    }
    private static string Reference(JsonElement reference, string expected = null)
    {
        string path = Text(reference, "path"), digest = Text(reference, "sha256");
        Require(expected == null || path == expected, "wrong reference; expected " + expected);
        Require(Hex(digest, 64), "sha256 mismatch " + path);
        string expectedDigest = digest.ToLowerInvariant();
        Pinned.Remove(path);
        if (Hash(path) == expectedDigest) return path;
        Require(SharedSource.Contains(path), "sha256 mismatch " + path);
        byte[] recorded = RecordedBytes(path);
        bool matches = false;
        if (recorded != null) using (var sha = SHA256.Create()) matches = Digest(sha.ComputeHash(recorded)) == expectedDigest;
        Require(matches, "sha256 mismatch " + path);
        Pinned[path] = recorded; // Parse the bytes whose digest was just verified.
        return path;
    }
    private static void FileMap(JsonElement map)
    {
        Require(map.ValueKind == JsonValueKind.Object, "files must be a hash map");
        foreach (var file in map.EnumerateObject())
            Reference(JsonSerializer.SerializeToElement(new { path = file.Name, sha256 = file.Value.GetString() }));
    }
    private static void Duration(JsonElement value, string label)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0, "numeric duration required " + label);
    }
    private static HashSet<string> Strings(JsonElement array)
    {
        var values = new HashSet<string>();
        foreach (var value in array.EnumerateArray()) Require(values.Add(value.GetString()), "duplicate entry");
        return values;
    }
    private static void PassedResult(JsonElement result, string package)
    {
        Require(Text(result, "package") == package && Text(result, "status") == "PASS", "package result must PASS: " + package);
    }
    private static void VerifyLog(JsonElement row, bool historical, string tree)
    {
        Require(row.GetProperty("exit_code").GetInt32() == 0, "package verification did not pass " + Text(row, "package"));
        string path = Reference(row.GetProperty("log"));
        Require(path.StartsWith("evidence/production/GATE-M2/", StringComparison.Ordinal)
            && new FileInfo(path).Length > 0, "nonempty gate package log required");
        string command = Text(row, "command"), package = Text(row, "package");
        if (historical)
            Require(command.Contains("git archive") && command.Contains(Text(row, "commit"))
                && command.Contains("--root") && command.Contains("--package " + package), "historical archive command required");
        else
            Require(Text(row, "tree_hash") == tree && command == "python3 tools/verify.py --package " + package
                + " --seit docs/plans/issue-3-specification/seit.json", "current package command/tree mismatch " + package);
        string[] lines = File.ReadAllText(path).TrimEnd().Split('\n');
        string header = lines[0].TrimEnd('\r');
        if (historical)
            Require(header == "$ " + command, "package log command mismatch " + package);
        else
        {
            int script = header.IndexOf(" tools/verify.py ", StringComparison.Ordinal);
            Require(header.StartsWith("$ ", StringComparison.Ordinal) && script > 2
                && Path.GetFileName(header.Substring(2, script - 2)) == "python3"
                && header.Substring(script) == command.Substring("python3".Length), "package log command mismatch " + package);
        }
        foreach (string line in lines)
        {
            string output = line.Trim();
            Require(output != "FAIL" && !output.StartsWith("FAIL ", StringComparison.Ordinal)
                && !output.StartsWith("FAIL\t", StringComparison.Ordinal), "package log records FAIL " + package);
        }
        string terminal = lines[lines.Length - 1].Trim();
        string suffix = " checks passed";
        Require(terminal.EndsWith(suffix, StringComparison.Ordinal), "package log successful terminal required " + package);
        string[] counts = terminal.Substring(0, terminal.Length - suffix.Length).Split('/');
        Require(counts.Length == 2 && int.TryParse(counts[0], out int passed)
            && int.TryParse(counts[1], out int total) && passed > 0 && passed == total,
            "package log all checks must pass " + package);
    }
    private static void Packages(JsonElement m)
    {
        Reference(m.GetProperty("package_verify"), PackageVerify);
        var receipt = Read(PackageVerify);
        string tree = Text(m, "tree_hash");
        Require(Text(receipt, "tree_hash") == tree, "package receipt tree mismatch");
        foreach (var gap in receipt.GetProperty("gaps").EnumerateArray())
        {
            Require(Text(gap, "status") == "blocked-by-parked-lane" && !gap.GetProperty("counted_as_pass").GetBoolean()
                && !string.IsNullOrWhiteSpace(Text(gap, "reason")), "typed parked-lane gap required");
            throw new InvalidDataException("blocked-by-parked-lane: " + Text(gap, "package"));
        }
        var expected = new HashSet<string>();
        foreach (var task in Read("docs/plans/full-game-rev2/plan.json").GetProperty("tasks").EnumerateArray())
            if (Text(task, "milestone") == "M2" && Text(task, "id") != "GATE-M2" && Text(task, "id") != "BLOCK-CHOICE")
                expected.Add(Text(task, "id"));
        var seen = new HashSet<string>();
        foreach (var row in receipt.GetProperty("current").EnumerateArray())
        {
            string package = Text(row, "package");
            Require(expected.Contains(package) && seen.Add(package), "unexpected/duplicate current package " + package);
            string resultPath = Reference(row.GetProperty("result"), "evidence/production/" + package + "/result.json");
            var result = Read(resultPath); PassedResult(result, package); VerifyLog(row, false, tree);
            var files = row.GetProperty("files"); FileMap(files);
            if (result.TryGetProperty("files", out var original))
                foreach (var file in original.EnumerateObject())
                    if (file.Name != resultPath) Require(files.TryGetProperty(file.Name, out _), "current file binding missing " + file.Name);
        }
        Require(seen.SetEquals(expected) && seen.IsSupersetOf(CurrentQualification), "complete M2 current package list required");
        seen.Clear();
        foreach (var row in receipt.GetProperty("historical").EnumerateArray())
        {
            string package = Text(row, "package");
            Require((package == "BLOCK-CHOICE" || package == "SEIT-AMEND") && seen.Add(package), "unexpected/duplicate historical package");
            Require(Text(row, "commit") == (package == "BLOCK-CHOICE" ? "89b6073" : "95a87ec"), "wrong done commit");
            Reference(row.GetProperty("result"), "evidence/production/" + package + "/result.json");
            PassedResult(Read(Text(row.GetProperty("result"), "path")), package); VerifyLog(row, true, tree);
        }
        Require(seen.SetEquals(new[] { "BLOCK-CHOICE", "SEIT-AMEND" }), "both historical archive runs required");
    }
    private static void Timings(JsonElement m)
    {
        foreach (var entry in TimingKeys)
        {
            var source = Read("evidence/production/" + entry.Key + "/result.json").GetProperty("throughput");
            var copy = m.GetProperty("timings").GetProperty(entry.Key);
            foreach (string key in new[] { "hands_on_hours", "elapsed_hours" })
                Duration(source.GetProperty(key), entry.Key + "." + key);
            foreach (string key in entry.Value) Duration(source.GetProperty(key), entry.Key + "." + key);
            foreach (var field in source.EnumerateObject())
            {
                var value = copy.GetProperty(field.Name);
                if (field.Name == "hands_on_hours" || field.Name == "elapsed_hours" || Array.IndexOf(entry.Value, field.Name) >= 0)
                    Duration(value, field.Name);
                Require(field.Value.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number
                    && value.GetDouble() == field.Value.GetDouble(), "timing differs from measured producer " + entry.Key);
            }
        }
    }
    private static void Quality(JsonElement m)
    {
        var quality = m.GetProperty("quality");
        Reference(quality.GetProperty("slice"), Slice);
        Reference(quality.GetProperty("player"), "evidence/production/SLICE/player-play.mp4");
        Reference(quality.GetProperty("decision"), Decision);
        var captures = new HashSet<string>();
        foreach (var capture in quality.GetProperty("captures").EnumerateArray())
        {
            string path = Reference(capture);
            Require(path.StartsWith("evidence/production/SLICE/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal)
                && new FileInfo(path).Length > 0 && captures.Add(path), "distinct SLICE capture required");
        }
        foreach (string name in new[] { "slice", "slice-room", "slice-apartment", "fit-everyday-sit", "fit-everyday-walk",
            "fit-exercise-sit", "fit-exercise-walk", "fit-sleep-sit", "fit-sleep-walk" })
            Require(captures.Contains("evidence/production/SLICE/" + name + ".png"), "missing visible quality capture " + name);
        var decision = Read(Decision);
        Require(Text(decision, "package") == "STYLE-FEEL" && Text(decision, "owner") == "William Rumph", "owner decision identity required");
        Require(Text(Read("evidence/production/STYLE-FEEL/result.json"), "decision_sha256") == Hash(Decision), "STYLE-FEEL decision hash mismatch");
        FileMap(decision.GetProperty("bound"));
        foreach (string path in new[] { Slice, "evidence/production/SLICE/result.json", "evidence/production/SLICE/player-play.mp4" })
            Require(decision.GetProperty("bound").TryGetProperty(path, out _), "owner decision binding missing " + path);
        Require(Text(decision.GetProperty("choice"), "style") == "accept", "M2 style approval required");
    }
    private static void Qualifications(JsonElement m)
    {
        Reference(m.GetProperty("spec"), Sdoc);
        var q = m.GetProperty("qualifications");
        Require(Strings(q.GetProperty("makehuman")).SetEquals(new[] { "BODY-MH", "RIG-MH", "MATERIAL-MH", "BODY-PRESET" })
            && Strings(q.GetProperty("ual")).SetEquals(new[] { "ANIM-UAL" })
            && Strings(q.GetProperty("recorded_cc0")).SetEquals(new[] { "AUDIO-CC0" }), "separate current production qualifications required");
        var historical = new HashSet<string>();
        foreach (var item in m.GetProperty("historical_demo_receipts").EnumerateArray()) historical.Add(Reference(item));
        Require(historical.SetEquals(new[] { "evidence/milestones/M0.json", "evidence/milestones/M1.json" }), "separate historical demo receipts required");
    }
    private static void LiveService(JsonElement live)
    {
        Require(Text(live, "service") == "d1-ultra" && Text(live, "status") == "not_run"
            && !live.GetProperty("counted_as_pass").GetBoolean(), "d1-ultra must remain not_run and not counted PASS");
    }
    private static void Obligations(JsonElement m)
    {
        var milestones = new HashSet<string>();
        var items = m.GetProperty("incomplete_game_obligations");
        Require(items.GetArrayLength() > 0, "incomplete-game obligations must be nonempty");
        var seit = new HashSet<string>();
        foreach (var item in Read("docs/plans/issue-3-specification/seit.json").GetProperty("proof_cases").EnumerateArray()) seit.Add(Text(item, "id"));
        foreach (var item in items.EnumerateArray())
        {
            string milestone = Text(item, "milestone");
            Require((milestone == "M3" || milestone == "M4" || milestone == "M5" || milestone == "M6")
                && seit.Contains(Text(item, "seit_id")) && Text(item, "status") == "pending"
                && !string.IsNullOrWhiteSpace(Text(item, "obligation")), "pending later-milestone obligation with real SEIT id required");
            milestones.Add(milestone);
        }
        Require(milestones.SetEquals(new[] { "M3", "M4", "M5", "M6" }), "obligations must name M3, M4, M5 and M6");
        var choice = Read(Decision).GetProperty("choice");
        foreach (string topic in new[] { "physical_readability", "behavioral_feel" })
        {
            string status = Text(choice, topic);
            Require(status == "accept" || status == "revise", "unknown owner decision " + topic);
            if (status != "revise") continue;
            bool retained = false;
            foreach (var item in items.EnumerateArray())
                if (Text(item, "milestone") == "M4" && Text(item, "obligation").Contains(topic)) retained = true;
            Require(retained, "M4 owner revision obligation missing " + topic);
        }
    }
    private static bool HasBinding(JsonElement node, string path, string hash)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("sha256", out var digest)
                && (node.TryGetProperty("path", out var p) || node.TryGetProperty("file", out p))
                && p.GetString() == path && digest.GetString() == hash) return true;
            foreach (var property in node.EnumerateObject()) if (HasBinding(property.Value, path, hash)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) if (HasBinding(item, path, hash)) return true;
        return false;
    }
    private static void Rights(JsonElement m)
    {
        var consumed = new Dictionary<string, string>();
        foreach (var asset in Read(Slice).GetProperty("assets").EnumerateArray()) consumed.Add(Reference(asset), Text(asset, "sha256"));
        var joined = new HashSet<string>();
        foreach (var row in m.GetProperty("rights").EnumerateArray())
        {
            string export = Reference(row.GetProperty("export")), provenance = Reference(row.GetProperty("provenance"));
            Require(consumed.ContainsKey(export) && joined.Add(export), "unexpected/duplicate rights export " + export);
            Require(provenance.StartsWith("production/provenance/", StringComparison.Ordinal) && provenance.EndsWith(".json", StringComparison.Ordinal), "production provenance file required");
            Require(HasBinding(Read(provenance), export, consumed[export]), "rights hash join missing " + export);
        }
        Require(joined.SetEquals(consumed.Keys), "rights join required for every consumed SLICE export");
    }
    private static void Check(string label, Action action)
    {
        try { action(); Console.WriteLine("PASS " + label); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + label + ": " + e.Message); }
    }
    private static void Reject(string label, Action action, string expected)
    {
        Check(label, () =>
        {
            try { action(); }
            catch (InvalidDataException e) { Require(e.Message.Contains(expected), "wrong rejection: " + e.Message); return; }
            throw new InvalidDataException("invalid input accepted");
        });
    }
    private static void SharedFileReceipt()
    {
        string root = Directory.GetCurrentDirectory();
        string scratch = Path.Combine(Path.GetTempPath(), "gate-m2-shared-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        void Reload() { Committed.Clear(); Hashes.Clear(); LoadCommitted(); }
        void CommitFixture()
        {
            Git("add", "--", PackageVerify, Milestone, Result, "tools/verify.py");
            string commit = Git("-c", "user.name=Gate fixture", "-c", "user.email=gate@example.invalid",
                "commit-tree", Git("write-tree"), "-p", "HEAD", "-m", "GATE-M2 shared-file fixture");
            Git("update-ref", "HEAD", commit);
            Reload();
        }
        string FileDigest(string path)
        { using (var sha = SHA256.Create()) return Digest(sha.ComputeHash(File.ReadAllBytes(path))); }
        try
        {
            Git("clone", "--shared", "--quiet", "--", root, scratch);
            Directory.SetCurrentDirectory(scratch);
            Reload();
            Receipt();
            string packages = File.ReadAllText(PackageVerify), milestone = File.ReadAllText(Milestone), result = File.ReadAllText(Result);
            string packagesHash = FileDigest(PackageVerify), milestoneHash = FileDigest(Milestone);
            string sharedHash = null;
            foreach (var row in Read(PackageVerify).GetProperty("current").EnumerateArray())
                if (row.GetProperty("files").TryGetProperty("tools/verify.py", out var digest)) sharedHash = digest.GetString();
            Require(Hex(sharedHash, 64), "fixture must bind tools/verify.py");

            File.AppendAllText("tools/verify.py", "\n# Unrelated later shared-file change (B-3 fixture).\n");
            CommitFixture();
            Require(Hash("tools/verify.py") != sharedHash, "fixture must change the committed shared file");
            // Rebind the outer receipts so only the referenced source digest is invalid.
            File.WriteAllText(PackageVerify, packages.Replace(sharedHash, new string('0', 64)));
            File.WriteAllText(Milestone, milestone.Replace(packagesHash, FileDigest(PackageVerify)));
            File.WriteAllText(Result, result.Replace(milestoneHash, FileDigest(Milestone)));
            CommitFixture();
            bool rejected = false;
            try { Receipt(); }
            catch (InvalidDataException e)
            { Require(e.Message == "sha256 mismatch tools/verify.py", "wrong tampered digest rejection: " + e.Message); rejected = true; }
            Require(rejected, "tampered recorded-source digest accepted");
            Console.WriteLine("PASS shared-file fixture tampered recorded-source digest rejected");

            File.WriteAllText(PackageVerify, packages); File.WriteAllText(Milestone, milestone); File.WriteAllText(Result, result);
            CommitFixture();
            Receipt();
        }
        finally
        {
            Directory.SetCurrentDirectory(root);
            Reload();
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
    private static void SelfChecks()
    {
        string path = "evidence/production/SECOND-PROP/result.json";
        Check("corpus committed result/hash", () => { PassedResult(Read(path), "SECOND-PROP"); Reference(JsonSerializer.SerializeToElement(new { path, sha256 = Hash(path) })); });
        Reject("tampered sha256 rejected", () => Reference(JsonSerializer.SerializeToElement(new { path, sha256 = new string('0', 64) })), "sha256 mismatch");
        string missing = "evidence/production/GATE-M2/" + Guid.NewGuid().ToString("N") + ".missing";
        Reject("missing file rejected", () => Reference(JsonSerializer.SerializeToElement(new { path = missing, sha256 = new string('0', 64) })), "missing " + missing);
        Check("corpus numeric timing", () => Duration(Read(path).GetProperty("throughput").GetProperty("mesh_hours"), "mesh_hours"));
        Reject("text duration rejected", () => Duration(JsonSerializer.SerializeToElement("0.03"), "mesh_hours"), "numeric duration");
        Reject("negative duration rejected", () => Duration(JsonSerializer.SerializeToElement(-0.03), "mesh_hours"), "numeric duration");
        var timings = new Dictionary<string, JsonElement>();
        foreach (var entry in TimingKeys)
            timings.Add(entry.Key, Read("evidence/production/" + entry.Key + "/result.json").GetProperty("throughput"));
        Check("negative non-duration throughput accepted", () => Timings(JsonSerializer.SerializeToElement(new { timings })));
        var audio = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(timings["SECOND-AUDIO"].GetRawText());
        audio["loudness_hours"] = JsonSerializer.SerializeToElement(-0.03);
        timings["SECOND-AUDIO"] = JsonSerializer.SerializeToElement(audio);
        Reject("negative declared throughput duration rejected", () => Timings(JsonSerializer.SerializeToElement(new { timings })), "numeric duration");
        var failed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Read(path).GetRawText());
        failed["status"] = JsonSerializer.SerializeToElement("FAIL");
        Reject("non-PASS package rejected", () => PassedResult(JsonSerializer.SerializeToElement(failed), "SECOND-PROP"), "must PASS");
        Check("corpus rights path join", () => Require(HasBinding(Read("production/provenance/SECOND-CHAR.json"),
            "Assets/Blockparty/Art/Characters/second.gltf", Hash("Assets/Blockparty/Art/Characters/second.gltf")), "rights binding missing"));
        Check("corpus rights file join", () => Require(HasBinding(Read("production/provenance/AUDIO-CC0.json"),
            "Assets/Blockparty/Audio/Recorded/day-birds.wav", Hash("Assets/Blockparty/Audio/Recorded/day-birds.wav")), "audio rights binding missing"));
        Check("rights stale hash rejected", () => Require(!HasBinding(Read("production/provenance/SECOND-CHAR.json"),
            "Assets/Blockparty/Art/Characters/second.gltf", new string('0', 64)), "stale rights accepted"));
        Check("live-service not_run accepted", () => LiveService(JsonSerializer.SerializeToElement(new { service = "d1-ultra", status = "not_run", counted_as_pass = false })));
        Reject("live-service PASS rejected", () => LiveService(JsonSerializer.SerializeToElement(new { service = "d1-ultra", status = "PASS", counted_as_pass = true })), "must remain not_run");
        Reject("not_run counted as PASS rejected", () => LiveService(JsonSerializer.SerializeToElement(new { service = "d1-ultra", status = "not_run", counted_as_pass = true })), "must remain not_run");
        Reject("empty obligations rejected", () => Obligations(JsonSerializer.SerializeToElement(new { incomplete_game_obligations = Array.Empty<object>() })), "must be nonempty");
        Reject("missing rights joins rejected", () => Rights(JsonSerializer.SerializeToElement(new { rights = Array.Empty<object>() })), "rights join required");
    }
    private static void Receipt()
    {
            Pinned.Clear(); ReceiptTree = null;
            var result = Read(Result); PassedResult(result, "GATE-M2");
            var m = Read(Milestone);
            Require(Text(m, "schema") == "blockparty-m2/1" && Text(m, "gate") == "GATE-M2" && Text(m, "status") == "PASS", "M2 receipt identity/status required");
            Require(Text(m, "claim_scope") == "M2 representative corner; whole-game acceptance pending", "M2 scope must retain incomplete-game qualification");
            Require(Hex(Text(m, "tree_hash"), 40) && Text(result, "tree_hash") == Text(m, "tree_hash"), "candidate tree binding required");
            QualifiedSource(Text(m, "tree_hash"));
            ReceiptTree = Text(m, "tree_hash");
            Require(Text(result, "milestone_sha256") == Hash(Milestone), "result milestone hash mismatch");
            Require(DateTimeOffset.TryParse(Text(result, "started_utc"), out var start)
                && DateTimeOffset.TryParse(Text(result, "ended_utc"), out var end) && end >= start, "result timestamps required");
            Duration(result.GetProperty("active_hours"), "active_hours");
            Packages(m); Timings(m); Quality(m); Qualifications(m); LiveService(m.GetProperty("live_service")); Obligations(m); Rights(m);
    }
    public static int Main()
    {
        Check("committed corpus inventory", LoadCommitted);
        if (failures == 0) SelfChecks();
        Check("GATE-M2 receipt (SEIT-AC-20/SPEC-07/SUB-05/SYS-01/VER-03)", Receipt);
        Check("receipt pins shared-file digests across committed successors (B-3)", SharedFileReceipt);
        Check("receipt accepts committed successors for every M4 shared source (B-3b)", () =>
        {
            string root = Directory.GetCurrentDirectory();
            var rejected = new List<string>();
            foreach (string path in new[] { "tools/verify.py", "tools/check-sdoc-bindings.py",
                "docs/plans/issue-3-specification/seit.json", "docs/specification/placement.json",
                "Assets/Blockparty/Presentation/AudioMixer.cs", "Assets/Blockparty/Presentation/AudioMixer.cs.meta",
                "tests/production/UI-TITLE.cs" })
            {
                string scratch = Path.Combine(Path.GetTempPath(), "gate-m2-shared-m4-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Git("clone", "--shared", "--quiet", "--", root, scratch);
                    Directory.SetCurrentDirectory(scratch);
                    Committed.Clear(); Hashes.Clear(); LoadCommitted();
                    Receipt();
                    string original = Hash(path);
                    File.AppendAllText(path, "\n");
                    Git("add", "--", path);
                    string commit = Git("-c", "user.name=Gate fixture", "-c", "user.email=gate@example.invalid",
                        "commit-tree", Git("write-tree"), "-p", "HEAD", "-m", "GATE-M2 M4 shared-source fixture");
                    Git("update-ref", "HEAD", commit);
                    Committed.Clear(); Hashes.Clear(); LoadCommitted();
                    Require(Hash(path) != original, "fixture must change committed shared source " + path);
                    try { Receipt(); Console.WriteLine("PASS M4 shared-source successor " + path); }
                    catch (InvalidDataException e)
                    {
                        Require(e.Message == "sha256 mismatch " + path, "wrong shared-source rejection: " + e.Message);
                        rejected.Add(path);
                    }
                }
                finally
                {
                    Directory.SetCurrentDirectory(root);
                    Committed.Clear(); Hashes.Clear(); Pinned.Clear(); ReceiptTree = null; LoadCommitted();
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                }
            }
            Require(rejected.Count == 0, "sha256 mismatch for committed shared-source successors: " + string.Join(", ", rejected));
        });
        Check("receipt rejects replaced committed evidence despite recorded original (B-3 P1)", () =>
        {
            string root = Directory.GetCurrentDirectory();
            string scratch = Path.Combine(Path.GetTempPath(), "gate-m2-evidence-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            string capture = "evidence/production/SLICE/slice.png";
            try
            {
                Git("clone", "--shared", "--quiet", "--", root, scratch);
                Directory.SetCurrentDirectory(scratch);
                Committed.Clear(); Hashes.Clear(); LoadCommitted();
                Receipt();
                File.WriteAllText(capture, "This is not a PNG.\n");
                Git("add", "--", capture);
                string commit = Git("-c", "user.name=Gate fixture", "-c", "user.email=gate@example.invalid",
                    "commit-tree", Git("write-tree"), "-p", "HEAD", "-m", "GATE-M2 replaced evidence fixture");
                Git("update-ref", "HEAD", commit);
                Committed.Clear(); Hashes.Clear(); LoadCommitted();
                bool rejected = false;
                try { Receipt(); }
                catch (InvalidDataException e)
                { Require(e.Message == "sha256 mismatch " + capture, "wrong replaced evidence rejection: " + e.Message); rejected = true; }
                Require(rejected, "replaced committed evidence accepted via recorded original");
            }
            finally
            {
                Directory.SetCurrentDirectory(root);
                Committed.Clear(); Hashes.Clear(); Pinned.Clear(); ReceiptTree = null; LoadCommitted();
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }
        });
        Check("receipt accepts committed UTF-8 BOM JSON (B-3 P2)", () =>
        {
            string root = Directory.GetCurrentDirectory();
            string scratch = Path.Combine(Path.GetTempPath(), "gate-m2-bom-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            try
            {
                Git("clone", "--shared", "--quiet", "--", root, scratch);
                Directory.SetCurrentDirectory(scratch);
                Committed.Clear(); Hashes.Clear(); LoadCommitted();
                Receipt();
                File.WriteAllText(Result, "\uFEFF" + File.ReadAllText(Result), new UTF8Encoding(false));
                Git("add", "--", Result);
                string commit = Git("-c", "user.name=Gate fixture", "-c", "user.email=gate@example.invalid",
                    "commit-tree", Git("write-tree"), "-p", "HEAD", "-m", "GATE-M2 BOM receipt fixture");
                Git("update-ref", "HEAD", commit);
                Committed.Clear(); Hashes.Clear(); LoadCommitted();
                Receipt();
            }
            finally
            {
                Directory.SetCurrentDirectory(root);
                Committed.Clear(); Hashes.Clear(); Pinned.Clear(); ReceiptTree = null; LoadCommitted();
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }
        });
        if (failures == 0) Console.WriteLine("GATE-M2 ALL PASS");
        else Console.WriteLine("GATE-M2 FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
