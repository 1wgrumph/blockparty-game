/* ALPHA@1 recorded-evidence contract. Resource group: isolated-csharp; no
 * editor/display/network. Local portions of the SEIT rows below only; these
 * checks do not claim whole-case acceptance or independent visual review.
 * alpha.json: {tree_hash,binary_sha256,gate_m2:REF,build:REF,classes:REF,
 * events:REF,ai_fallback:REF,two_household:REF,frames:[{id,frame:REF}]}.
 * REF={path,sha256}; every referenced artifact is hashed. All JSON logs repeat
 * tree_hash/binary_sha256. Build log: {method:"BuildLinux",target:
 * "StandaloneLinux64",exit_code:0,manifest:REF}; manifest follows GATE-M1's
 * {tree_hash,command,files:[{relative_path,sha256}]}. binary_sha256 is the
 * Linux executable entry's hash. Build log must be tracked; source tree must
 * descend from the M2 tree (resolved through Git commits, not lexical hashes).
 * classes log: {source:"player-command",completions:[{resident_id,activity_id,
 * outcome:"Completed",effects:{<effect>:<numeric delta>},site_id?}]}.
 * events log: {events:[{kind,gate_values:{...}}]}; exact normalized observations
 * and numeric oracles are in Events(), from PROGRESSION/AGING/ESTATE/STORM
 * contracts. ai_fallback log: {entries:[{service,outcome:"local-fallback",
 * reason,connected_success:false}]}; reasons use fixed boundary reason codes.
 * two_household log: {contexts:[{household_id,steps:["save","quit","reload",
 * "continue"],before_pid,after_pid,save:REF,reloaded_save:REF,before_state:REF,
 * after_state:REF,continued_action:{resident_id,activity_id,outcome}}]}.
 * State snapshots are separate, equal normalized exports under SAVE's epoch /
 * action-id normalization; hashes prove continuity, not screenshot similarity.
 * Frame ids: the ten package class names below and every site in the recorded
 * registry (M4 adds apartment/common-room to cafe/workshop/office/school).
 * PNG pixels are decoded by the installed Pillow dependency already
 * used by CONTROLS-PLAY. No threshold beyond at least one non-black RGB pixel.
 * Self-check schema examples reuse committed GATE-M1 payload/state/PNG input;
 * they are parser fixtures, never real play evidence. Each gets a fresh temp
 * copy and cleanup. negative-fixture [missing-class|tampered-hash|missing-m2|
 * connected-success|missing-site|duplicate-site] sends that invalid example
 * through the unchanged oracle and must print FAIL/exit 1. Normal mode proves
 * all rejections first.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class AlphaChecks
{
    private const string Receipt = "evidence/production/ALPHA/alpha.json";
    private const string M2 = "evidence/milestones/M2.json";
    private static readonly string[] Classes = { "FOOD", "WORK", "EDUCATION", "SOCIAL", "PARTY", "MINIGOLF", "HEALTH", "CLUB", "HOME", "CREATIVE" };
    // Catalog prefixes, not arbitrary ids: BP-ACT-03..12 / acceptance-traceability.
    private static readonly string[] Prefixes = { "ACT-FOOD-", "ACT-WORK-", "ACT-EDU-", "ACT-REL-", "ACT-PARTY-", "ACT-SPORT-", "ACT-HEALTH-", "ACT-CLUB-", "ACT-HOME-", "ACT-CREA-" };
    private static readonly string[] CheckNames = { "gate_m2", "build", "classes", "events", "ai_fallback", "two_household", "frames" };
    private static readonly string[] Cases = {
        "SEIT-SPEC-03 SEIT-SPEC-04 SEIT-VER-01-P1 SEIT-VER-01-N1 SEIT-VER-01-N2",
        "SEIT-VER-01-P1", "SEIT-SYS-01-P1 SEIT-SPEC-03 SEIT-SPEC-04",
        "SEIT-AC-05 SEIT-SYS-01-P1", "SEIT-SYS-01-N1 SEIT-SYS-01-N2",
        "SEIT-AC-01 SEIT-AC-05 SEIT-AC-14 SEIT-AC-20 SEIT-IF-SAVE-P1 SEIT-IF-SAVE-P2 SEIT-IF-SAVE-P3 SEIT-IF-SAVE-N5 SEIT-SAVE-01-P1 SEIT-SAVE-01-P2 SEIT-SAVE-01-N1 SEIT-SAVE-01-N2 SEIT-TB-06-P1 SEIT-TB-06-N4",
        "SEIT-SYS-01-P1" };
    private static int failures;
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    private static string Text(JsonElement o, string key) { return o.GetProperty(key).GetString(); }
    private static double Number(JsonElement o, string key) { return o.GetProperty(key).GetDouble(); }
    private static bool Hex(string s, int n)
    {
        if (s == null || s.Length != n) return false;
        foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
    private static JsonElement Read(string root, string path)
    {
        Require(File.Exists(Path.Combine(root, path)), "missing ALPHA evidence " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return doc.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement r)
    {
        string p = Text(r, "path");
        Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p)
            && Path.GetFullPath(Path.Combine(root, p)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "reference must be repo-relative");
        Require(File.Exists(Path.Combine(root, p)), "missing referenced file " + p);
        string digest = Text(r, "sha256");
        Require(Hex(digest, 64) && Hash(Path.Combine(root, p)) == digest.ToLowerInvariant(), "sha256 mismatch " + p);
        return Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, p)));
    }
    private static void References(string root, JsonElement o)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("path", out _) || o.TryGetProperty("sha256", out _)) Reference(root, o);
            foreach (var p in o.EnumerateObject()) References(root, p.Value);
        }
        else if (o.ValueKind == JsonValueKind.Array) foreach (var v in o.EnumerateArray()) References(root, v);
    }
    private static void Binding(JsonElement log, JsonElement receipt)
    {
        Require(Text(log, "tree_hash") == Text(receipt, "tree_hash")
            && Text(log, "binary_sha256") == Text(receipt, "binary_sha256"), "source/player binding differs");
    }
    private static JsonElement Log(string root, JsonElement receipt, string key)
    {
        var log = Read(root, Reference(root, receipt.GetProperty(key)));
        Binding(log, receipt); References(root, log); return log;
    }
    private static int Process(string program, string root, string[] args, out string output)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = Path.GetFullPath(root), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using (var p = System.Diagnostics.Process.Start(start))
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { p.Kill(); p.WaitForExit(); throw new InvalidDataException(program + " timed out"); }
            output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(); return p.ExitCode;
        }
    }
    private static void GateM2(string root, JsonElement r, bool actual)
    {
        Require(Reference(root, r.GetProperty("gate_m2")) == M2, "gate_m2 must reference " + M2);
        var m = Read(root, M2);
        Require(Text(m, "status") == "PASS", "GATE-M2 status must be PASS");
        string previous = Text(m, "tree_hash"); Require(Hex(previous, 40), "M2 tree hash missing");
        if (!actual) return; // Parser fixtures carry example trees, never Git ancestry evidence.
        string output;
        Require(Process("git", root, new[] { "rev-parse", "--verify", previous + "^{tree}" }, out output) == 0, "ALPHA build tree must contain the GATE-M2 tree");
        previous = output.Trim();
        Require(Process("git", root, new[] { "log", "--all", "--format=%H %T" }, out output) == 0, "cannot resolve source trees: " + output);
        var parents = new List<string>(); var candidates = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string[] bits = line.Trim().Split(' ');
            if (bits.Length != 2) continue;
            if (bits[1] == previous) parents.Add(bits[0]);
            if (bits[1] == Text(r, "tree_hash")) candidates.Add(bits[0]);
        }
        foreach (string parent in parents) foreach (string candidate in candidates)
            if (Process("git", root, new[] { "merge-base", "--is-ancestor", parent, candidate }, out output) == 0) return;
        Require(false, "ALPHA build tree must contain the GATE-M2 tree");
    }
    private static void Build(string root, JsonElement r, bool actual)
    {
        var b = Log(root, r, "build");
        Require(Text(b, "method") == "BuildLinux" && Text(b, "target") == "StandaloneLinux64" && Number(b, "exit_code") == 0, "successful BuildLinux StandaloneLinux64 required");
        var manifest = Read(root, Reference(root, b.GetProperty("manifest")));
        Require(Text(manifest, "tree_hash") == Text(r, "tree_hash")
            && Text(manifest, "command").Contains("-executeMethod ToolchainBuild.BuildLinux"), "manifest source/command differs");
        bool binary = false, data = false; var paths = new HashSet<string>();
        foreach (var f in manifest.GetProperty("files").EnumerateArray())
        {
            string p = Text(f, "relative_path"), hash = Text(f, "sha256");
            Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p) && !p.Contains("..") && paths.Add(p) && Hex(hash, 64), "invalid payload manifest entry");
            binary |= p.EndsWith(".x86_64", StringComparison.Ordinal) && hash == Text(r, "binary_sha256");
            data |= p.Contains("_Data/");
        }
        Require(binary && data, "manifest must bind Linux binary_sha256 and data payload");
        if (actual)
        {
            string output;
            Require(Process("git", root, new[] { "ls-files", "--error-unmatch", "--", Text(r.GetProperty("build"), "path") }, out output) == 0, "build log must be tracked: " + output);
        }
    }
    private static IReadOnlyList<BlockSite> Sites()
    {
        var w = WorldState.CreateSeeded(1L, 7L, 0, null); var clock = new Clock(w);
        return new BlockSites(w, new ActivityExecutor(w, new CommandArbitrator(w, clock), clock)).Sites;
    }
    // Judge a receipt by the registry at its recorded source tree, not the checkout.
    private static string RegistrySource(string root, JsonElement r)
    {
        string tree = Text(r, "tree_hash"), output;
        Require(Hex(tree, 40), "source tree hash required");
        Require(Process("git", root, new[] { "rev-parse", "--verify", tree + "^{tree}" }, out output) == 0, "recorded source tree must resolve");
        tree = output.Trim();
        Require(Process("git", root, new[] { "log", "--all", "--format=%H %T" }, out output) == 0, "cannot resolve source trees: " + output);
        foreach (string line in output.Split('\n'))
        {
            string[] bits = line.Trim().Split(' ');
            if (bits.Length != 2 || bits[1] != tree) continue;
            Require(Process("git", root, new[] { "show", bits[0] + ":Assets/Blockparty/Simulation/BlockSites.cs" }, out output) == 0, "recorded BlockSites source missing");
            return output;
        }
        Require(false, "recorded source commit missing");
        return null;
    }
    // C# allows whitespace/newlines between tokens; compare a token after skipped spacing.
    private static int After(string source, int at, string token)
    {
        while (at < source.Length && char.IsWhiteSpace(source[at])) at++;
        return at + token.Length <= source.Length && string.CompareOrdinal(source, at, token, 0, token.Length) == 0 ? at + token.Length : -1;
    }
    // shortcut: recorded ids resolve to live poses/slots; BLOCK-SITES ids and
    // geometry are append-only, so parse poses too if a recorded site moves.
    private static IReadOnlyList<BlockSite> Sites(string root, JsonElement r, bool actual)
    {
        if (!actual) return Sites();
        string source = RegistrySource(root, r);
        var live = new Dictionary<string, BlockSite>();
        foreach (var site in Sites()) live.Add(site.Id, site);
        var recorded = new List<BlockSite>(); var seen = new HashSet<string>();
        string[] prefix = { "(", "world", ",", "executor", ",", "\"" };
        for (int at = 0; (at = source.IndexOf("Add", at, StringComparison.Ordinal)) >= 0; at++)
        {
            if (at > 0 && (char.IsLetterOrDigit(source[at - 1]) || source[at - 1] == '_')) continue;
            int cursor = at + 3;
            foreach (string token in prefix) if ((cursor = After(source, cursor, token)) < 0) break;
            if (cursor < 0) continue;
            int end = source.IndexOf('"', cursor);
            Require(end > cursor, "malformed recorded BlockSites source");
            string id = source.Substring(cursor, end - cursor);
            Require(seen.Add(id) && live.ContainsKey(id), "recorded site missing from live registry " + id);
            recorded.Add(live[id]); at = end;
        }
        Require(recorded.Count > 0, "recorded BlockSites source empty");
        return recorded;
    }
    // IDs from the landed CommandArbitrator catalog; no reflection over private state.
    private static readonly string[] Activities = {
        "ACT-FOOD-01", "ACT-FOOD-02", "ACT-FOOD-03", "ACT-FOOD-04", "ACT-WORK-01",
        "ACT-WORK-02", "ACT-WORK-03", "ACT-WORK-04", "ACT-EDU-01", "ACT-EDU-02",
        "ACT-EDU-03", "ACT-EDU-04", "ACT-REL-01", "ACT-REL-02", "ACT-REL-03",
        "ACT-REL-04", "ACT-REL-05", "ACT-REL-06", "ACT-PARTY-01", "ACT-PARTY-02",
        "ACT-SPORT-01", "ACT-SPORT-02", "ACT-HEALTH-01", "ACT-HEALTH-02", "ACT-HEALTH-03",
        "ACT-HEALTH-04", "ACT-CLUB-01", "ACT-CLUB-02", "ACT-HOME-01", "ACT-HOME-02",
        "ACT-HOME-03", "ACT-HOME-04", "ACT-CREA-01", "ACT-CREA-02", "ACT-CREA-03" };
    private static bool KnownActivity(string id) { return Array.IndexOf(Activities, id) >= 0; }
    private static void ClassesLog(string root, JsonElement r, bool actual)
    {
        var log = Log(root, r, "classes"); Require(Text(log, "source") == "player-command", "player-command log required");
        var seen = new HashSet<string>();
        foreach (var row in log.GetProperty("completions").EnumerateArray())
        {
            string id = Text(row, "activity_id");
            Require(!string.IsNullOrWhiteSpace(Text(row, "resident_id")) && KnownActivity(id)
                && Text(row, "outcome") == "Completed", "catalog activity/resident/Completed missing");
            bool changed = false;
            foreach (var e in row.GetProperty("effects").EnumerateObject()) changed |= e.Value.GetDouble() != 0;
            Require(changed, "effect deltas missing " + id);
            for (int i = 0; i < Classes.Length; i++) if (id.StartsWith(Prefixes[i], StringComparison.Ordinal)) seen.Add(Classes[i]);
            if (id.StartsWith("ACT-WORK-", StringComparison.Ordinal) || id.StartsWith("ACT-EDU-", StringComparison.Ordinal))
            {
                bool site = false;
                foreach (var s in Sites(root, r, actual)) foreach (var slot in s.Slots)
                    site |= s.Id == Text(row, "site_id") && slot.ActivityCode == id;
                Require(site, "in-block site missing or incompatible " + id);
            }
        }
        foreach (string id in Classes) Require(seen.Contains(id), "missing class " + id);
    }
    private static JsonElement Event(JsonElement log, string kind)
    {
        foreach (var e in log.GetProperty("events").EnumerateArray())
            if (Text(e, "kind") == kind) return e.GetProperty("gate_values");
        throw new InvalidDataException("missing event " + kind);
    }
    private static void Events(string root, JsonElement r)
    {
        var log = Log(root, r, "events");
        var p = Event(log, "ProgressionApplied");
        Require(Number(p, "cooking_delta") == 10 && Number(p, "career_delta") == 20 && Number(p, "course_sessions") == 8
            && p.GetProperty("certificate_unlocked").GetBoolean(), "progression completion/course gate values");
        var life = Event(log, "Birthday");
        // BP-LIFE-01 Moderate preset days, in LifeStage order; independent oracle.
        double[] stageDays = { 2.25, 11.25, 15.75, 31.5, 47.25, 63, 94.5 };
        int before = (int)Enum.Parse<LifeStage>(Text(life, "before"));
        Require(before >= 0 && before < stageDays.Length && Number(life, "stage_duration_days") == stageDays[before]
            && Number(life, "stage_days_before") >= stageDays[before]
            && (int)Enum.Parse<LifeStage>(Text(life, "after")) == before + 1, "life stage gate values");
        var birth = Event(log, "Birth");
        Require(Number(birth, "gestation_seconds") >= 259200 && Number(birth, "birth_count") == 1
            && !string.IsNullOrWhiteSpace(Text(birth, "child_id")) && !string.IsNullOrWhiteSpace(Text(birth, "carrier_id"))
            && !string.IsNullOrWhiteSpace(Text(birth, "other_parent_id")), "birth duration/identity/once-only gate");
        var estate = Event(log, "Death");
        Require(Number(estate, "estate_total") >= 0 && Number(estate, "distributed_total") == Number(estate, "estate_total")
            && Number(estate, "settlement_count") == 1, "estate totals / once-only settlement");
        var care = Event(log, "CareTransfer");
        Require(!string.IsNullOrWhiteSpace(Text(care, "dependent_id")) && !string.IsNullOrWhiteSpace(Text(care, "guardian_id"))
            && Number(care, "available_places") >= 1 && Number(care, "transfer_count") == 1, "care capacity/guardian/once-only gate");
        var warning = Event(log, "SevereStormWarning"); var storm = Event(log, "SevereStorm");
        Require(Number(warning, "population") >= 16 && Number(warning, "world_age_days") >= 30
            && Number(warning, "cooldown_remaining") == 0 && Number(warning, "warning_lead_seconds") == 86400,
            "storm eligibility/warning gate values");
        Require(Number(storm, "duration_seconds") == 21600 && Number(storm, "cooldown_seconds") == 1728000
            && Number(storm, "exposure_health_delta") == -20 && storm.GetProperty("unsafe_weather").GetBoolean()
            && Number(storm, "damaged_common") == Math.Min(2, Number(storm, "available_common"))
            && Number(storm, "damaged_roofs") == Math.Min(1, Number(storm, "occupied_roofs")), "storm consequence gate values");
    }
    private static void AiFallback(string root, JsonElement r)
    {
        var seen = new HashSet<string>();
        string[] services = { "IMAGE", "VOICE", "SAM", "D1U" };
        string[] reasons = { "Unprovisioned:MissingCredential", "Unprovisioned:AdapterNotProvisioned", "Denied:FirstSendDisclosureMissing", "Denied:ProhibitedRuntimeModel", "ServiceTimeoutRealSeconds:10", "UpstreamError", "BadFrame", "UpstreamTimeout" };
        foreach (var e in Log(root, r, "ai_fallback").GetProperty("entries").EnumerateArray())
        {
            Require(!e.GetProperty("connected_success").GetBoolean() && Text(e, "outcome") == "local-fallback", "connected success forbidden for AI fallback");
            Require(Array.IndexOf(reasons, Text(e, "reason")) >= 0, "AI reason must be fixed boundary code");
            Require(Array.IndexOf(services, Text(e, "service")) >= 0, "unknown AI service"); seen.Add(Text(e, "service"));
        }
        Require(seen.SetEquals(services), "IMAGE VOICE SAM D1U fallback entries required");
    }
    private static void TwoHousehold(string root, JsonElement r)
    {
        var seen = new HashSet<string>(); var files = new HashSet<string>();
        foreach (var h in Log(root, r, "two_household").GetProperty("contexts").EnumerateArray())
        {
            string id = Text(h, "household_id"); Require(!string.IsNullOrWhiteSpace(id) && seen.Add(id), "distinct household id required");
            var steps = h.GetProperty("steps"); string[] order = { "save", "quit", "reload", "continue" };
            Require(steps.GetArrayLength() == order.Length, "save quit reload continue required");
            for (int i = 0; i < order.Length; i++) Require(steps[i].GetString() == order[i], "household lifecycle order");
            Require(Number(h, "before_pid") > 0 && Number(h, "after_pid") > 0 && Number(h, "before_pid") != Number(h, "after_pid"), "process restart required");
            Require(Reference(root, h.GetProperty("save")) != Reference(root, h.GetProperty("reloaded_save")), "distinct save/reloaded save snapshots required");
            var before = h.GetProperty("before_state"); var after = h.GetProperty("after_state");
            Require(Reference(root, before) != Reference(root, after) && Text(before, "sha256") == Text(after, "sha256"), "normalized state continuity differs");
            foreach (string key in new[] { "save", "reloaded_save", "before_state", "after_state" })
                Require(files.Add(Reference(root, h.GetProperty(key))), "household contexts must have separate snapshots");
            var action = h.GetProperty("continued_action");
            Require(!string.IsNullOrWhiteSpace(Text(action, "resident_id")) && KnownActivity(Text(action, "activity_id"))
                && Text(action, "outcome") == "Completed", "continued play completion required");
        }
        Require(seen.Count == 2, "two household contexts required");
    }
    private static void Frames(string root, JsonElement r, bool actual)
    {
        var required = new HashSet<string>(Classes); foreach (var s in Sites(root, r, actual)) required.Add("site:" + s.Id);
        var seen = new HashSet<string>(); var files = new HashSet<string>(); var args = new List<string> { "-c",
            "import sys\nfrom PIL import Image\nfor p in sys.argv[1:]:\n with Image.open(p) as im:\n  assert im.format == 'PNG', 'not PNG: '+p\n  im.load()\n  assert any(hi > 0 for lo,hi in im.convert('RGB').getextrema()), 'black frame: '+p\n" };
        foreach (var f in r.GetProperty("frames").EnumerateArray())
        {
            string id = Text(f, "id"); Require(required.Contains(id) && seen.Add(id), "duplicate/unknown frame id " + id);
            string path = Reference(root, f.GetProperty("frame"));
            Require(files.Add(path), "separate PNG per class/site required");
            args.Add(Path.GetFullPath(Path.Combine(root, path)));
        }
        Require(seen.SetEquals(required), "PNG per class and per site required");
        string output; Require(Process("python3", root, args.ToArray(), out output) == 0, "non-black PNG decode failed: " + output);
    }
    private static void Validate(string root, int check, bool actual = false)
    {
        var r = Read(root, Receipt);
        Require(Hex(Text(r, "tree_hash"), 40) && Hex(Text(r, "binary_sha256"), 64), "source/player hash required");
        switch (check)
        {
            case 0: GateM2(root, r, actual); break;
            case 1: Build(root, r, actual); break;
            case 2: ClassesLog(root, r, actual); break;
            case 3: Events(root, r); break;
            case 4: AiFallback(root, r); break;
            case 5: TwoHousehold(root, r); break;
            case 6: Frames(root, r, actual); break;
        }
    }
    private static void Report(bool ok, string name)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    private static void Run(string name, Action action)
    { try { action(); Report(true, name); } catch (Exception e) { Report(false, name + " " + e.Message); } }
    private static object Ref(string root, string path) { return new { path, sha256 = Hash(Path.Combine(root, path)) }; }
    private static void Write(string root, string path, object value)
    {
        string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, JsonSerializer.Serialize(value));
    }
    private static void Fixture(string root, string mutation)
    {
        string tree = "1234567890abcdef1234567890abcdef12345678";
        string corpus = Path.GetFullPath("tests/production/fixtures/GATE-M1/payload");
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        foreach (string name in new[] { "capture.png", "before-state.json", "after-state.json", "save.json", "reloaded-save.json", "player-payload.fixture", "build-manifest.json" })
            File.Copy(Path.Combine(corpus, name), Path.Combine(root, "payload", name));
        string binary = Hash(Path.Combine(root, "payload/player-payload.fixture"));
        Write(root, M2, new { status = "PASS", tree_hash = tree });
        Write(root, "payload/build.json", new { tree_hash = tree, binary_sha256 = binary, method = "BuildLinux", target = "StandaloneLinux64", exit_code = 0, manifest = Ref(root, "payload/build-manifest.json") });
        string[] ids = { "ACT-FOOD-01", "ACT-WORK-01", "ACT-EDU-01", "ACT-REL-01", "ACT-PARTY-01", "ACT-SPORT-02", "ACT-HEALTH-01", "ACT-CLUB-01", "ACT-HOME-01", "ACT-CREA-01" };
        var completions = new List<object>();
        for (int i = 0; i < ids.Length; i++)
            if (mutation != "missing-class" || i != 9) completions.Add(new { resident_id = "1", activity_id = ids[i], outcome = "Completed", effects = new { gain = 10 }, site_id = i == 1 ? "cafe" : "school" });
        Write(root, "payload/classes.json", new { tree_hash = tree, binary_sha256 = binary, source = "player-command", completions });
        var events = new object[] {
            new { kind = "ProgressionApplied", gate_values = (object)new { cooking_delta = 10, career_delta = 20, course_sessions = 8, certificate_unlocked = true } },
            new { kind = "Birthday", gate_values = (object)new { before = "Child", after = "Teen", stage_days_before = 31.5, stage_duration_days = 31.5 } },
            new { kind = "Birth", gate_values = (object)new { gestation_seconds = 259200, birth_count = 1, child_id = "3", carrier_id = "1", other_parent_id = "2" } },
            new { kind = "Death", gate_values = (object)new { estate_total = 101, distributed_total = 101, settlement_count = 1 } },
            new { kind = "CareTransfer", gate_values = (object)new { dependent_id = "3", guardian_id = "2", available_places = 1, transfer_count = 1 } },
            new { kind = "SevereStormWarning", gate_values = (object)new { population = 16, world_age_days = 30, cooldown_remaining = 0, warning_lead_seconds = 86400 } },
            new { kind = "SevereStorm", gate_values = (object)new { duration_seconds = 21600, cooldown_seconds = 1728000, exposure_health_delta = -20, unsafe_weather = true, available_common = 2, damaged_common = 2, occupied_roofs = 1, damaged_roofs = 1 } } };
        Write(root, "payload/events.json", new { tree_hash = tree, binary_sha256 = binary, events });
        var entries = new List<object>(); foreach (string service in new[] { "IMAGE", "VOICE", "SAM", "D1U" })
            entries.Add(new { service, outcome = "local-fallback", reason = "Unprovisioned:MissingCredential", connected_success = mutation == "connected-success" && service == "IMAGE" });
        Write(root, "payload/ai.json", new { tree_hash = tree, binary_sha256 = binary, entries });
        var contexts = new List<object>();
        for (int i = 1; i <= 2; i++)
        {
            foreach (string name in new[] { "save", "reloaded-save", "before-state", "after-state" })
                File.Copy(Path.Combine(corpus, name + ".json"), Path.Combine(root, "payload", i + "-" + name + ".json"));
            contexts.Add(new { household_id = i.ToString(), steps = new[] { "save", "quit", "reload", "continue" }, before_pid = 10, after_pid = 20,
                save = Ref(root, "payload/" + i + "-save.json"), reloaded_save = Ref(root, "payload/" + i + "-reloaded-save.json"), before_state = Ref(root, "payload/" + i + "-before-state.json"), after_state = Ref(root, "payload/" + i + "-after-state.json"),
                continued_action = new { resident_id = i.ToString(), activity_id = "ACT-FOOD-01", outcome = "Completed" } });
        }
        Write(root, "payload/households.json", new { tree_hash = tree, binary_sha256 = binary, contexts });
        var frames = new List<object>(); var frameIds = new List<string>(Classes);
        var siteIds = new HashSet<string>();
        foreach (var site in Sites()) { Require(siteIds.Add(site.Id), "fixture duplicate registered site"); frameIds.Add("site:" + site.Id); }
        Require(siteIds.SetEquals(new[] { "apartment", "common-room", "cafe", "workshop", "office", "school" }), "fixture requires planned six-site registry");
        if (mutation == "missing-site") frameIds.Remove("site:common-room");
        if (mutation == "duplicate-site") frameIds[frameIds.IndexOf("site:common-room")] = "site:apartment";
        for (int i = 0; i < frameIds.Count; i++)
        {
            string path = "payload/frame-" + i + ".png";
            File.Copy(Path.Combine(corpus, "capture.png"), Path.Combine(root, path));
            frames.Add(new { id = frameIds[i], frame = Ref(root, path) });
        }
        Write(root, Receipt, new { tree_hash = tree, binary_sha256 = binary, gate_m2 = Ref(root, M2), build = Ref(root, "payload/build.json"),
            classes = Ref(root, "payload/classes.json"), events = Ref(root, "payload/events.json"), ai_fallback = Ref(root, "payload/ai.json"), two_household = Ref(root, "payload/households.json"), frames });
        if (mutation == "tampered-hash") File.AppendAllText(Path.Combine(root, "payload/classes.json"), " ");
        if (mutation == "missing-m2") File.Delete(Path.Combine(root, M2));
        if (mutation.StartsWith("m2-", StringComparison.Ordinal))
        {
            string output;
            Require(Process("git", root, new[] { "init", "--quiet" }, out output) == 0, "fixture git init: " + output);
            Require(Process("git", root, new[] { "add", "payload/player-payload.fixture" }, out output) == 0, "fixture git add: " + output);
            Require(Process("git", root, new[] { "-c", "user.name=ALPHA fixture", "-c", "user.email=alpha@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "M2 fixture" }, out output) == 0, "fixture git commit: " + output);
            Require(Process("git", root, new[] { "rev-parse", "HEAD" }, out output) == 0, "fixture commit hash: " + output);
            string commit = output.Trim();
            Require(Process("git", root, new[] { "rev-parse", "HEAD^{tree}" }, out output) == 0, "fixture tree hash: " + output);
            tree = output.Trim();
            Write(root, M2, new { status = "PASS", tree_hash = mutation == "m2-commit" ? commit : mutation == "m2-tree" ? tree : new string('0', 40) });
            Write(root, Receipt, new { tree_hash = tree, binary_sha256 = binary, gate_m2 = Ref(root, M2) });
        }
    }
    private static void SelfCheck(string mutation, int check, string expected, bool expose = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-alpha-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture(root, mutation);
            try
            {
                if (check < 0) for (int i = 0; i < CheckNames.Length; i++) Validate(root, i);
                else Validate(root, check, mutation.StartsWith("m2-", StringComparison.Ordinal));
                Report(expected == null, "fixture " + mutation + (expected == null ? "" : " accepted invalid receipt"));
            }
            catch (Exception e)
            { Report(!expose && expected != null && e is InvalidDataException && e.Message.Contains(expected), "fixture " + mutation + " " + e.Message); }
        }
        catch (Exception e) { Report(false, "fixture setup " + mutation + " " + e.Message); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static int Main(string[] args)
    {
        failures = 0;
        string[] negatives = { "missing-class", "tampered-hash", "missing-m2", "connected-success", "missing-site", "duplicate-site" };
        int[] checks = { 2, 2, 0, 4, 6, 6 };
        string[] messages = { "missing class CREATIVE", "sha256 mismatch", "missing referenced file " + M2, "connected success forbidden", "PNG per class and per site required", "duplicate/unknown frame id site:apartment" };
        bool expose = Array.IndexOf(args, "negative-fixture") >= 0;
        if (expose)
        {
            string selected = args.Length > 1 ? args[1] : negatives[0]; int index = Array.IndexOf(negatives, selected);
            if (index < 0) Report(false, "unknown negative fixture " + selected);
            else SelfCheck(selected, checks[index], messages[index], true);
        }
        else
        {
            foreach (bool missingSite in new[] { false, true })
            {
                string name = "fixture recorded-sites-" + (missingSite ? "missing-site" : "after-addition");
                string root = Path.Combine(Path.GetTempPath(), "blockparty-alpha-recorded-" + Guid.NewGuid().ToString("N"));
                Run(name, () =>
                {
                    try
                    {
                        Require(Process("git", ".", new[] { "clone", "--quiet", "--shared", "--no-checkout", Path.GetFullPath("."), root }, out string output) == 0, "fixture clone: " + output);
                        var receipt = Read(".", Receipt);
                        string source = RegistrySource(".", receipt);
                        var sites = Sites(".", receipt, true);
                        var recorded = new HashSet<string>();
                        foreach (var site in sites) Require(recorded.Add(site.Id), "fixture duplicate recorded site");
                        Require(recorded.Count > 1, "fixture needs multiple recorded sites");
                        string removedSite = sites[0].Id;
                        var seen = new HashSet<string>(); var frames = new List<JsonElement>();
                        foreach (var frame in receipt.GetProperty("frames").EnumerateArray())
                        {
                            string id = Text(frame, "id");
                            if (id.StartsWith("site:", StringComparison.Ordinal)) seen.Add(id.Substring(5));
                            string path = Reference(".", frame.GetProperty("frame")), target = Path.Combine(root, path);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target);
                            if (id != "site:" + removedSite) frames.Add(frame.Clone());
                        }
                        Require(seen.SetEquals(recorded), "fixture must contain exactly its recorded sites");
                        var fixture = new Dictionary<string, object>();
                        foreach (var property in receipt.EnumerateObject()) fixture.Add(property.Name, property.Value.Clone());
                        if (!missingSite)
                        {
                            // Make a smaller recorded registry even when the committed receipt already matches HEAD.
                            int idAt = source.IndexOf("\"" + removedSite + "\"", StringComparison.Ordinal);
                            Require(idAt >= 0, "fixture registration id missing");
                            int start = source.LastIndexOf("Add", idAt, StringComparison.Ordinal);
                            int end = source.IndexOf(");", idAt, StringComparison.Ordinal);
                            Require(start >= 0 && end >= 0, "fixture registration statement missing");
                            const string registry = "Assets/Blockparty/Simulation/BlockSites.cs";
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, registry)));
                            File.WriteAllText(Path.Combine(root, registry), source.Remove(start, end + 2 - start));
                            Require(Process("git", root, new[] { "read-tree", Text(receipt, "tree_hash") }, out output) == 0, "fixture index: " + output);
                            Require(Process("git", root, new[] { "add", registry }, out output) == 0, "fixture registry: " + output);
                            Require(Process("git", root, new[] { "-c", "user.name=ALPHA fixture", "-c", "user.email=alpha@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "recorded registry before site addition" }, out output) == 0, "fixture commit: " + output);
                            Require(Process("git", root, new[] { "rev-parse", "HEAD^{tree}" }, out output) == 0, "fixture tree: " + output);
                            fixture["tree_hash"] = output.Trim(); fixture["frames"] = frames;
                            recorded.Remove(removedSite);
                            var current = new HashSet<string>(); foreach (var site in Sites()) current.Add(site.Id);
                            Require(current.IsSupersetOf(recorded) && current.Count > recorded.Count, "fixture needs later registered sites");
                        }
                        Write(root, Receipt, fixture);
                        Validate(root, 6, true); // Prove the complete receipt works before testing a missing site.
                        if (missingSite)
                        {
                            fixture["frames"] = frames; Write(root, Receipt, fixture);
                            bool rejected = false;
                            try { Validate(root, 6, true); }
                            catch (InvalidDataException e) { Require(e.Message.Contains("PNG per class and per site required"), e.Message); rejected = true; }
                            Require(rejected, "accepted receipt missing recorded site frame " + removedSite);
                        }
                    }
                    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
                });
            }
            Run("fixture recorded-sites-whitespace-registration", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "blockparty-alpha-whitespace-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Fixture(root, "valid");
                    string Git(params string[] command)
                    {
                        Require(Process("git", root, command, out string output) == 0, "fixture git: " + output);
                        return output.Trim();
                    }
                    Git("init", "-q", "--initial-branch=fixture");
                    const string registry = "Assets/Blockparty/Simulation/BlockSites.cs";
                    string source = File.ReadAllText(registry);
                    string formatted = source.Replace("Add(world, executor, \"school\",", "Add(world, executor,\n                \"school\",");
                    Require(source != formatted, "fixture must reformat the real school registration");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, registry)));
                    File.WriteAllText(Path.Combine(root, registry), source); Git("add", registry);
                    Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "real six-site registry");
                    var receipt = new Dictionary<string, object>();
                    foreach (var property in Read(root, Receipt).EnumerateObject()) receipt.Add(property.Name, property.Value.Clone());
                    receipt["tree_hash"] = Git("rev-parse", "HEAD^{tree}"); Write(root, Receipt, receipt);
                    Validate(root, 6, true);
                    File.WriteAllText(Path.Combine(root, registry), formatted); Git("add", registry);
                    Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "format school registration");
                    receipt["tree_hash"] = Git("rev-parse", "HEAD^{tree}"); Write(root, Receipt, receipt);
                    var errors = new List<string>();
                    try { Validate(root, 6, true); }
                    catch (InvalidDataException e) { errors.Add("complete evidence rejected: " + e.Message); }
                    var frames = new List<JsonElement>(); bool removed = false;
                    foreach (var frame in Read(root, Receipt).GetProperty("frames").EnumerateArray())
                        if (Text(frame, "id") == "site:school") removed = true; else frames.Add(frame.Clone());
                    Require(removed, "fixture must remove recorded school evidence");
                    receipt["frames"] = frames; Write(root, Receipt, receipt);
                    bool rejected = false;
                    try { Validate(root, 6, true); }
                    catch (InvalidDataException e) { Require(e.Message.Contains("PNG per class and per site required"), e.Message); rejected = true; }
                    if (!rejected) errors.Add("accepted evidence missing recorded school frame");
                    Require(errors.Count == 0, string.Join("; ", errors));
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
            SelfCheck("valid", -1, null);
            for (int i = 0; i < negatives.Length; i++) SelfCheck(negatives[i], checks[i], messages[i]);
            SelfCheck("m2-commit", 0, null);
            SelfCheck("m2-tree", 0, null);
            SelfCheck("m2-unknown", 0, "ALPHA build tree must contain the GATE-M2 tree");
            for (int i = 0; i < CheckNames.Length; i++)
            { int check = i; Run(CheckNames[i] + " " + Cases[i], () => Validate(".", check, true)); }
        }
        if (failures == 0) Console.WriteLine("ALPHA ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
