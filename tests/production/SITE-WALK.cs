/* SITE-WALK@1: issue #128 sampled Linux/i7 walk evidence, isolated-csharp.
 * No editor, display, network or player is launched. Fixtures check the receipt
 * oracle only; they are never player evidence or independent acceptance.
 * walk.json = {schema:"blockparty-site-walk/1",source:"player-command",
 * platform:"StandaloneLinux64",host:"i7",tree_hash,binary_sha256,build:REF,
 * walks:[{site_id,resident_id,command:{kind:"TravelTo"|"Activity",
 * resident_id,site_id,accepted:true,route:"WorldNavigation.TravelTo"|
 * "CommandArbitrator.Submit",activity_id?,slot_id?},samples:[{game_seconds,
 * position:[x,y,z],followed_resident_id,camera_position:[x,y,z]}],
 * frames:[{id:"start"|"mid"|"arrival",sample_index,frame:REF}]}]}.
 * REF={path:<repo-relative>,sha256}. Build/manifest follow ALPHA's schema;
 * build.tree_hash passes GATE-M3 ancestor/ancestor-equal source-tree binding.
 * Receipt tree similarly binds to HEAD. Build and receipt binary hashes agree
 * with the manifest's Linux executable; data payload must also be listed.
 * IDs, activity slots, entry and origin come from the real BlockSites registry.
 * It has no explicit bounds: use the AABB of origin/entry/slot poses, padded
 * by NAV's documented 0.05 m arrival tolerance, not receipt-provided bounds.
 * Times are GAME seconds (not real seconds or frame counts). Limit: 1 m/game
 * minute, plus one 6-second NAV tick and 0.05 m arrival snap per interval.
 * Sample gaps <=60 game seconds bound the otherwise invisible teleport window.
 * Follow radius: 25 m in 3D, allowing a raised/offset camera; the receipt cannot
 * raise it. Start outside bounds; >=10 samples, decreasing entry distance,
 * interior movement, arrival inside bounds. Frames bind samples 0, n/2, n-1.
 * PNGs are decoded with installed Pillow, as in ALPHA, and must be non-black.
 * Real capture provenance/visual quality still requires independent review.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class SiteWalkChecks
{
    private const string Receipt = "evidence/production/SITE-WALK/walk.json";
    private const double Speed = 1.0 / 60.0, StepSlack = 6.0 / 60.0 + 0.05;
    private const double FollowRadius = 25.0, BoundsPadding = 0.05;
    private static readonly string[] Negatives = { "teleport", "follow-lost", "missing-site", "arrival-outside-bounds", "tampered-frame-hash", "duplicate-site" };
    private static readonly string[] Errors = { "SW-Speed", "SW-Follow", "SW-Sites", "SW-Arrival", "SW-Hash", "SW-Sites unknown/duplicate site" };
    private static int failures;
    private static void Require(bool ok, string message)
    { if (!ok) throw new InvalidDataException(message); }
    private static string Text(JsonElement o, string key) { return o.GetProperty(key).GetString(); }
    private static bool Hex(string s, int n)
    {
        if (s == null || s.Length != n) return false;
        foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var f = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
    private static JsonElement Read(string root, string path)
    {
        Require(File.Exists(Path.Combine(root, path)), "SW-Receipt missing " + path);
        using (var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return d.RootElement.Clone();
    }
    private static string Reference(string root, JsonElement r)
    {
        string path = Text(r, "path"), hash = Text(r, "sha256");
        Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            && Path.GetFullPath(Path.Combine(root, path)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "SW-Reference repo-relative path required");
        Require(File.Exists(Path.Combine(root, path)), "SW-Reference missing " + path);
        Require(Hex(hash, 64) && Hash(Path.Combine(root, path)) == hash, "SW-Hash sha256 mismatch " + path);
        return path;
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
    private static string Git(string root, params string[] args)
    {
        string output; Require(Process("git", root, args, out output) == 0, "SW-Git " + output); return output.Trim();
    }
    private static bool SourceTrees(string root, string tree, out string output)
    { return Process("git", root, new[] { "rev-parse", tree + ":Assets", tree + ":Packages", tree + ":ProjectSettings" }, out output) == 0; }
    // Same two acceptance routes as GATE-M3, including uncommitted evidence trees.
    private static void AncestorBinding(string root, string previous, string tree)
    {
        Require(Hex(previous, 40) && Hex(tree, 40), "SW-Tree Git tree hash required");
        previous = Git(root, "rev-parse", "--verify", previous + "^{tree}");
        tree = Git(root, "rev-parse", "--verify", tree + "^{tree}");
        Require(SourceTrees(root, previous, out string sources), "SW-Tree source subtrees missing");
        var parents = new List<string>(); var candidates = new List<string>();
        foreach (string line in Git(root, "log", "--all", "--format=%H %T").Split('\n'))
        {
            string[] bits = line.Trim().Split(' ');
            if (bits.Length != 2) continue;
            if (bits[1] == previous) parents.Add(bits[0]);
            if (bits[1] == tree) candidates.Add(bits[0]);
        }
        foreach (string parent in parents) foreach (string candidate in candidates)
            if (Process("git", root, new[] { "merge-base", "--is-ancestor", parent, candidate }, out _) == 0) return;
        foreach (string candidate in candidates) foreach (string ancestor in Git(root, "rev-list", candidate).Split('\n'))
            if (SourceTrees(root, ancestor, out string other) && sources == other) return;
        Require(false, "SW-Tree not an ancestor or ancestor-equal Assets/Packages/ProjectSettings tree");
    }
    private static void Build(string root, JsonElement r)
    {
        string tree = Text(r, "tree_hash"), binary = Text(r, "binary_sha256");
        Require(Hex(tree, 40) && Hex(binary, 64), "SW-Build source/binary hashes required");
        AncestorBinding(root, tree, Git(root, "rev-parse", "HEAD^{tree}"));
        string path = Reference(root, r.GetProperty("build"));
        Git(root, "ls-files", "--error-unmatch", "--", path);
        var b = Read(root, path);
        Require(Text(b, "method") == "BuildLinux" && Text(b, "target") == "StandaloneLinux64"
            && b.GetProperty("exit_code").GetInt32() == 0 && Text(b, "binary_sha256") == binary, "SW-Build successful Linux build/binary binding required");
        AncestorBinding(root, Text(b, "tree_hash"), tree);
        var manifest = Read(root, Reference(root, b.GetProperty("manifest")));
        Require(Text(manifest, "tree_hash") == Text(b, "tree_hash")
            && Text(manifest, "command").Contains("-executeMethod ToolchainBuild.BuildLinux"), "SW-Build manifest tree/command mismatch");
        bool executable = false, data = false; var paths = new HashSet<string>();
        foreach (var f in manifest.GetProperty("files").EnumerateArray())
        {
            string p = Text(f, "relative_path"), hash = Text(f, "sha256");
            Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p) && !p.Contains("..") && paths.Add(p) && Hex(hash, 64), "SW-Build invalid manifest entry");
            executable |= p.EndsWith(".x86_64", StringComparison.Ordinal) && hash == binary;
            data |= p.Contains("_Data/");
        }
        Require(executable && data, "SW-Build executable hash and data payload required");
    }
    private static IReadOnlyList<BlockSite> Sites()
    {
        var w = WorldState.CreateSeeded(1L, 7L, 0, null); var clock = new Clock(w);
        return new BlockSites(w, new ActivityExecutor(w, new CommandArbitrator(w, clock), clock)).Sites;
    }
    // Judge a receipt by the registry at its recorded source tree, not the checkout.
    private static string RegistrySource(string root, string tree)
    {
        Require(Hex(tree, 40), "SW-Tree Git tree hash required");
        tree = Git(root, "rev-parse", "--verify", tree + "^{tree}");
        foreach (string line in Git(root, "log", "--all", "--format=%H %T").Split('\n'))
        {
            string[] bits = line.Trim().Split(' ');
            if (bits.Length == 2 && bits[1] == tree)
                return Git(root, "show", bits[0] + ":Assets/Blockparty/Simulation/BlockSites.cs");
        }
        Require(false, "SW-Sites recorded source commit missing");
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
    private static IReadOnlyList<BlockSite> Sites(string root, string tree)
    {
        string source = RegistrySource(root, tree);
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
            Require(end > cursor, "SW-Sites malformed recorded registry");
            string id = source.Substring(cursor, end - cursor);
            Require(seen.Add(id) && live.ContainsKey(id), "SW-Sites recorded site missing from live registry " + id);
            recorded.Add(live[id]); at = end;
        }
        Require(recorded.Count > 0, "SW-Sites empty recorded registry");
        return recorded;
    }
    private static double[] Pose(ApproachPose p) { return new[] { (double)p.X, p.Y, p.Z }; }
    private static double[] Vector(JsonElement a)
    {
        Require(a.GetArrayLength() == 3, "SW-Position three coordinates required");
        var p = new double[3];
        for (int i = 0; i < 3; i++) { p[i] = a[i].GetDouble(); Require(!double.IsNaN(p[i]) && !double.IsInfinity(p[i]), "SW-Position finite coordinates required"); }
        return p;
    }
    private static double Distance(double[] a, double[] b)
    { double sum = 0; for (int i = 0; i < 3; i++) sum += (a[i] - b[i]) * (a[i] - b[i]); return Math.Sqrt(sum); }
    private static bool Inside(BlockSite site, double[] p)
    {
        double[] lo = Pose(site.Origin), hi = Pose(site.Origin);
        var points = new List<ApproachPose> { site.Entry }; foreach (var slot in site.Slots) points.Add(slot.Pose);
        foreach (var point in points) for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], Pose(point)[i]); hi[i] = Math.Max(hi[i], Pose(point)[i]); }
        for (int i = 0; i < 3; i++) if (p[i] < lo[i] - BoundsPadding || p[i] > hi[i] + BoundsPadding) return false;
        return true;
    }
    private static void Walk(JsonElement w, BlockSite site)
    {
        string resident = Text(w, "resident_id"); var command = w.GetProperty("command");
        Require(!string.IsNullOrWhiteSpace(resident) && Text(command, "resident_id") == resident
            && Text(command, "site_id") == site.Id && command.GetProperty("accepted").GetBoolean(), "SW-Command accepted walker/site command required");
        string kind = Text(command, "kind"), route = Text(command, "route");
        bool valid = kind == "TravelTo" && route == "WorldNavigation.TravelTo";
        if (kind == "Activity" && route == "CommandArbitrator.Submit") foreach (var slot in site.Slots)
            valid |= slot.Id == Text(command, "slot_id") && slot.ActivityCode == Text(command, "activity_id")
                && (slot.ActivityCode.StartsWith("ACT-WORK-", StringComparison.Ordinal) || slot.ActivityCode.StartsWith("ACT-EDU-", StringComparison.Ordinal));
        Require(valid, "SW-Command normal TravelTo or site WORK/EDUCATION command required");
        var samples = w.GetProperty("samples"); int n = samples.GetArrayLength();
        Require(n >= 10, "SW-Samples at least 10 samples required " + site.Id);
        double[] first = Vector(samples[0].GetProperty("position")), last = Vector(samples[n - 1].GetProperty("position"));
        Require(!Inside(site, first), "SW-Start must begin outside registered site bounds " + site.Id);
        Require(Inside(site, last), "SW-Arrival outside registered site bounds " + site.Id);
        Require(Distance(last, Pose(site.Entry)) < Distance(first, Pose(site.Entry)), "SW-Progress entry distance must decrease " + site.Id);
        double previousTime = -1; double[] previous = null; bool interiorMovement = false;
        for (int i = 0; i < n; i++)
        {
            var sample = samples[i]; double time = sample.GetProperty("game_seconds").GetDouble();
            double[] position = Vector(sample.GetProperty("position")), camera = Vector(sample.GetProperty("camera_position"));
            Require(!double.IsNaN(time) && !double.IsInfinity(time) && time >= 0 && time > previousTime, "SW-Time strictly increasing finite game seconds required");
            Require(Text(sample, "followed_resident_id") == resident, "SW-Follow wrong followed resident " + site.Id + " sample " + i);
            Require(Distance(camera, position) <= FollowRadius, "SW-Camera outside 25 m follow radius " + site.Id);
            if (previous != null)
            {
                double elapsed = time - previousTime, step = Distance(previous, position);
                Require(elapsed <= 60, "SW-Time sample gap exceeds 60 game seconds");
                Require(step <= Speed * elapsed + StepSlack, "SW-Speed walking limit exceeded " + site.Id + " sample " + i);
                if (i < n - 1 && step > BoundsPadding) interiorMovement = true;
            }
            previous = position; previousTime = time;
        }
        Require(interiorMovement, "SW-Progress no sampled movement before arrival " + site.Id);
    }
    private static void Validate(string root)
    {
        var r = Read(root, Receipt);
        Require(Text(r, "schema") == "blockparty-site-walk/1" && Text(r, "source") == "player-command"
            && Text(r, "platform") == "StandaloneLinux64" && Text(r, "host") == "i7", "SW-Identity real Linux i7 player-command receipt required");
        Build(root, r);
        var sites = new Dictionary<string, BlockSite>(); foreach (var site in Sites(root, Text(r, "tree_hash"))) sites.Add(site.Id, site);
        var seen = new HashSet<string>(); var paths = new HashSet<string>();
        var png = new List<string> { "-c", "import sys\nfrom PIL import Image\nfor p in sys.argv[1:]:\n with Image.open(p) as im:\n  assert im.format == 'PNG', 'not PNG: '+p\n  im.load()\n  assert any(hi > 0 for lo,hi in im.convert('RGB').getextrema()), 'black frame: '+p\n" };
        foreach (var w in r.GetProperty("walks").EnumerateArray())
        {
            string id = Text(w, "site_id"); Require(sites.ContainsKey(id) && seen.Add(id), "SW-Sites unknown/duplicate site " + id);
            Walk(w, sites[id]); int count = w.GetProperty("samples").GetArrayLength(); var frames = new HashSet<string>();
            foreach (var f in w.GetProperty("frames").EnumerateArray())
            {
                string stage = Text(f, "id"); int index = stage == "start" ? 0 : stage == "mid" ? count / 2 : stage == "arrival" ? count - 1 : -1;
                Require(index >= 0 && frames.Add(stage) && f.GetProperty("sample_index").GetInt32() == index, "SW-Frames start/mid/arrival sample binding required");
                string path = Reference(root, f.GetProperty("frame")); Require(paths.Add(path), "SW-Frames separate captures required");
                png.Add(path);
            }
            Require(frames.Count == 3, "SW-Frames missing start/mid/arrival " + id);
        }
        Require(seen.SetEquals(sites.Keys), "SW-Sites missing site; all recorded BlockSites required");
        Require(Process("python3", root, png.ToArray(), out string output) == 0, "SW-PNG non-black PNG decode failed: " + output);
    }
    private static Dictionary<string, object> Obj(params object[] pairs)
    { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d; }
    private static void Write(string root, string path, object o)
    { string p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, JsonSerializer.Serialize(o)); }
    private static object Ref(string root, string path) { return Obj("path", path, "sha256", Hash(Path.Combine(root, path))); }
    private static string Commit(string root, string parent = null)
    {
        var args = new List<string> { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit-tree", Git(root, "write-tree"), "-m", "receipt parser fixture" };
        if (parent != null) { args.Add("-p"); args.Add(parent); }
        string commit = Git(root, args.ToArray()); Git(root, "update-ref", "refs/heads/fixture", commit); return commit;
    }
    private static void Fixture(string root, string mutation)
    {
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        File.Copy("tests/production/fixtures/GATE-M1/payload/player-payload.fixture", Path.Combine(root, "payload/player-payload.fixture"));
        Git(root, "init", "-q", "--initial-branch=fixture");
        foreach (string dir in new[] { "Assets", "Packages", "ProjectSettings" }) Write(root, dir + "/fixture.json", Obj("fixture", dir));
        // The fixture's recorded registry is the same one its walks are built from.
        Directory.CreateDirectory(Path.Combine(root, "Assets/Blockparty/Simulation"));
        var registry = new List<string>();
        var siteIds = new HashSet<string>();
        foreach (var site in Sites()) { Require(siteIds.Add(site.Id), "fixture duplicate registered site"); registry.Add("Add(world, executor, \"" + site.Id + "\", null);"); }
        Require(siteIds.SetEquals(new[] { "apartment", "common-room", "cafe", "workshop", "office", "school" }), "fixture requires planned six-site registry");
        File.WriteAllText(Path.Combine(root, "Assets/Blockparty/Simulation/BlockSites.cs"), string.Join("\n", registry));
        Git(root, "add", "Assets", "Packages", "ProjectSettings", "payload"); string parent = Commit(root);
        string tree = Git(root, "rev-parse", "HEAD^{tree}"), buildTree = tree, binary = Hash(Path.Combine(root, "payload/player-payload.fixture"));
        var walks = new List<object>();
        foreach (var site in Sites())
        {
            var samples = new List<object>();
            for (int i = 0; i < 10; i++)
            {
                var p = Pose(site.Entry); p[2] -= 3.0 * (9 - i) / 9;
                if (site.Id == "cafe" && mutation == "teleport" && i == 4) p[0] += 10;
                if (site.Id == "cafe" && mutation == "arrival-outside-bounds") p[0] += 1;
                samples.Add(Obj("game_seconds", i * 20, "position", p, "followed_resident_id", mutation == "follow-lost" && site.Id == "cafe" && i == 4 ? "other" : "walker",
                    "camera_position", new[] { p[0], p[1] + (mutation == "camera-away" ? 30 : 10), p[2] - 10 }));
            }
            if (mutation == "too-few-samples") samples.RemoveAt(4);
            var frames = new List<object>(); int f = 0;
            foreach (string stage in new[] { "start", "mid", "arrival" })
            {
                string path = "payload/" + site.Id + "-" + stage + ".png";
                File.Copy("tests/production/fixtures/GATE-M1/payload/capture.png", Path.Combine(root, path));
                frames.Add(Obj("id", stage, "sample_index", f == 0 ? 0 : f == 1 ? 5 : 9, "frame", Ref(root, path))); f++;
            }
            var command = Obj("kind", "TravelTo", "route", "WorldNavigation.TravelTo", "resident_id", "walker", "site_id", site.Id, "accepted", true);
            if (mutation == "activity" && site.Slots.Count > 0)
            { command["kind"] = "Activity"; command["route"] = "CommandArbitrator.Submit"; command["slot_id"] = site.Slots[0].Id; command["activity_id"] = site.Slots[0].ActivityCode; }
            walks.Add(Obj("site_id", site.Id, "resident_id", "walker", "command", command, "samples", samples, "frames", frames));
        }
        if (mutation == "missing-site") walks.RemoveAt(walks.Count - 1);
        if (mutation == "duplicate-site") walks[walks.Count - 1] = walks[0];
        if (mutation == "tampered-frame-hash") File.AppendAllText(Path.Combine(root, "payload/cafe-mid.png"), "tamper");
        if (mutation == "black-frame")
        {
            string siteId = (string)((Dictionary<string, object>)walks[0])["site_id"], black = "payload/" + siteId + "-mid.png";
            Require(Process("python3", root, new[] { "-c", "from PIL import Image; Image.new('RGB',(2,2)).save('" + black + "')" }, out string output) == 0, "fixture black PNG setup " + output);
            var frame = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)walks[0])["frames"])[1];
            frame["frame"] = Ref(root, black);
        }
        if (mutation == "changed-source")
        {
            Write(root, "Assets/fixture.json", Obj("changed", true)); Git(root, "add", "Assets");
            buildTree = Git(root, "write-tree"); Git(root, "read-tree", parent);
        }
        if (mutation == "source-equal" || mutation == "changed-source")
        {
            Write(root, "payload/uncommitted.json", Obj("example", true)); Git(root, "add", "payload/uncommitted.json");
            if (mutation == "source-equal") buildTree = Git(root, "write-tree"); Git(root, "read-tree", parent);
        }
        if (mutation == "ancestor") { Write(root, "Assets/fixture.json", Obj("new", true)); Git(root, "add", "Assets"); }
        Write(root, "payload/manifest.json", Obj("tree_hash", buildTree, "command", "Unity -executeMethod ToolchainBuild.BuildLinux", "files", new[] {
            Obj("relative_path", "Alpha.x86_64", "sha256", binary), Obj("relative_path", "Alpha_Data/Managed/Assembly-CSharp.dll", "sha256", binary) }));
        Write(root, "payload/build.json", Obj("tree_hash", buildTree, "binary_sha256", binary, "method", "BuildLinux", "target", "StandaloneLinux64", "exit_code", 0, "manifest", Ref(root, "payload/manifest.json")));
        Git(root, "add", "payload/build.json"); Commit(root, parent); tree = Git(root, "rev-parse", "HEAD^{tree}");
        Write(root, Receipt, Obj("schema", "blockparty-site-walk/1", "source", "player-command", "platform", "StandaloneLinux64", "host", "i7",
            "tree_hash", tree, "binary_sha256", binary, "build", Ref(root, "payload/build.json"), "walks", walks));
    }
    private static void Report(bool ok, string message)
    { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failures++; }
    private static void SelfCheck(string mutation, string expected, bool expose = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-site-walk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture(root, mutation);
            try { Validate(root); Report(expected == null, "fixture " + mutation + (expected == null ? " (parser only)" : " accepted invalid receipt")); }
            catch (Exception e) { Report(!expose && expected != null && e is InvalidDataException && e.Message.Contains(expected), "fixture " + mutation + " " + e.Message); }
        }
        catch (Exception e) { Report(false, "fixture setup " + mutation + " " + e.Message); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "negative-fixture")
        {
            int index = Array.IndexOf(Negatives, args.Length > 1 ? args[1] : Negatives[0]);
            if (index < 0) Report(false, "unknown negative fixture"); else SelfCheck(Negatives[index], Errors[index], true);
        }
        else
        {
            foreach (bool missingSite in new[] { false, true })
            {
                string name = "fixture recorded-sites-" + (missingSite ? "missing-site" : "after-addition");
                string root = Path.Combine(Path.GetTempPath(), "blockparty-site-walk-recorded-" + Guid.NewGuid().ToString("N"));
                try
                {
                    // Reuse the committed four-site capture and its real history, without a full checkout.
                    Git(".", "clone", "--quiet", "--shared", "--no-checkout", Path.GetFullPath("."), root);
                    Git(root, "read-tree", "HEAD");
                    var receipt = Read(".", Receipt);
                    Git(root, "cat-file", "-e", Text(receipt, "tree_hash") + ":Assets/Blockparty/Simulation/BlockSites.cs");
                    var recorded = new HashSet<string> { "cafe", "workshop", "office", "school" };
                    var current = new HashSet<string>(); foreach (var site in Sites()) current.Add(site.Id);
                    Require(current.IsSupersetOf(recorded) && current.Count > recorded.Count, "fixture needs later registered sites");
                    void Copy(string path)
                    {
                        string target = Path.Combine(root, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(path, target);
                    }
                    Copy(Receipt);
                    string build = Reference(".", receipt.GetProperty("build")); Copy(build);
                    Copy(Reference(".", Read(".", build).GetProperty("manifest")));
                    var seen = new HashSet<string>(); var walks = new List<JsonElement>();
                    foreach (var walk in receipt.GetProperty("walks").EnumerateArray())
                    {
                        string id = Text(walk, "site_id"); seen.Add(id);
                        foreach (var frame in walk.GetProperty("frames").EnumerateArray()) Copy(Reference(".", frame.GetProperty("frame")));
                        if (id != "school") walks.Add(walk.Clone());
                    }
                    Require(seen.SetEquals(recorded), "fixture must contain exactly its four recorded sites");
                    Validate(root); // Positive control prevents a live-registry mismatch from proving rejection.
                    if (missingSite)
                    {
                        var incomplete = new Dictionary<string, object>();
                        foreach (var property in receipt.EnumerateObject()) incomplete.Add(property.Name, property.Value.Clone());
                        incomplete["walks"] = walks; Write(root, Receipt, incomplete);
                        try { Validate(root); Report(false, name + " accepted receipt missing recorded school"); }
                        catch (InvalidDataException e) { Report(e.Message.Contains("SW-Sites"), name + " " + e.Message); }
                    }
                    else Report(true, name);
                }
                catch (Exception e) { Report(false, name + " " + e.Message); }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            }
            {
                string name = "fixture recorded-sites-whitespace-registration";
                string root = Path.Combine(Path.GetTempPath(), "blockparty-site-walk-whitespace-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Fixture(root, "valid");
                    const string registry = "Assets/Blockparty/Simulation/BlockSites.cs";
                    string source = File.ReadAllText(registry);
                    string formatted = source.Replace("Add(world, executor, \"school\",", "Add(world, executor,\n                \"school\",");
                    Require(source != formatted, "fixture must reformat the real school registration");
                    File.WriteAllText(Path.Combine(root, registry), source); Git(root, "add", registry);
                    Commit(root, Git(root, "rev-parse", "HEAD"));
                    var receipt = new Dictionary<string, object>();
                    foreach (var property in Read(root, Receipt).EnumerateObject()) receipt.Add(property.Name, property.Value.Clone());
                    receipt["tree_hash"] = Git(root, "rev-parse", "HEAD^{tree}"); Write(root, Receipt, receipt);
                    Validate(root);
                    File.WriteAllText(Path.Combine(root, registry), formatted); Git(root, "add", registry);
                    Commit(root, Git(root, "rev-parse", "HEAD"));
                    receipt["tree_hash"] = Git(root, "rev-parse", "HEAD^{tree}"); Write(root, Receipt, receipt);
                    var errors = new List<string>();
                    try { Validate(root); }
                    catch (InvalidDataException e) { errors.Add("complete evidence rejected: " + e.Message); }
                    var walks = new List<JsonElement>(); bool removed = false;
                    foreach (var walk in Read(root, Receipt).GetProperty("walks").EnumerateArray())
                        if (Text(walk, "site_id") == "school") removed = true; else walks.Add(walk.Clone());
                    Require(removed, "fixture must remove recorded school evidence");
                    receipt["walks"] = walks; Write(root, Receipt, receipt);
                    bool rejected = false;
                    try { Validate(root); }
                    catch (InvalidDataException e) { Require(e.Message.Contains("SW-Sites missing site"), e.Message); rejected = true; }
                    if (!rejected) errors.Add("accepted evidence missing recorded school");
                    Require(errors.Count == 0, string.Join("; ", errors));
                    Report(true, name);
                }
                catch (Exception e) { Report(false, name + " " + e.Message); }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            }
            foreach (string valid in new[] { "valid", "activity", "ancestor", "source-equal" }) SelfCheck(valid, null);
            SelfCheck("changed-source", "SW-Tree not an ancestor");
            for (int i = 0; i < Negatives.Length; i++) SelfCheck(Negatives[i], Errors[i]);
            SelfCheck("camera-away", "SW-Camera");
            SelfCheck("too-few-samples", "SW-Samples");
            SelfCheck("black-frame", "SW-PNG");
            try { Validate("."); } catch (Exception e) { Report(false, "real receipt " + e.Message); }
        }
        if (failures == 0) Console.WriteLine("SITE-WALK ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
