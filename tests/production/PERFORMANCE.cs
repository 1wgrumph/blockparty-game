/* PERFORMANCE oracle only: #92, full-game-rev2-m5-1, M5-OD-9 / M5-OD-16.
 * Deliverable contract for the Product code (no measurements exist yet):
 * Extend E1-IGPU PerfRecorder and implement tools/perf-sample.py. On an idle
 * bto-build-hosti7 (SSH alias build-host), UHD 770, DISPLAY=:0,
 * build a StandaloneLinux64 RELEASE player
 * with pinned Unity 6000.3.10f1 in an isolated m5-copy-*; never measure Editor.
 * Run: python3 tools/perf-sample.py --build <standalone-archive> --commit <sha>
 *   --gate-m4-commit <accepted-main-merge> --residents 8,24 --warmup-seconds 10
 *   --seconds 600 --width 1920 --height 1080 --output evidence/production/PERFORMANCE/runs
 * This is the producer CLI contract to implement, not an existing command.
 * The measured commit must contain and descend from the accepted GATE-M4 merge
 * (resolve at green time, not today's content). Its M4.json must be PASS and
 * the gate commit must be on local origin/main. Record commit + tree separately.
 * Rebind UNITY-COMPILE for the recorder extension; generic verify.py checks
 * PERFORMANCE/result.json {package,status:PASS,started_utc,ended_utc,active_hours}.
 * Verify with BLOCKPARTY_UNITY=<pinned root> python3 tools/verify.py --package
 * PERFORMANCE --seit docs/plans/issue-3-specification/seit.json.
 *
 * Produce evidence/production/performance.json:
 * {schema:"blockparty-perf/1",commit,tree_hash,gate_m4_commit,
 *  synthetic:false,build:{archive_sha256,development:false,platform:"StandaloneLinux64"},
 *  host:{hostname,lscpu_model,gpu,mem_bytes,os,machine_profile_match,compat_gap},
 *  hardware:REF, idle_threshold:{load1_max,owner_decision},envelope_qualified:bool,
 *  workloads:[{residents:8|24,run_id,compat_gap,frames:REF,rss:REF,recorder:REF,
 *   idle_start:REF,idle_end:REF,save_ms:[10],load_ms:[10],command_ms:[20],
 *   api:[{purpose,requests,failures,adapter,model_id,connected,typed_gap}],
 *   summary:{frame_p95_ms,rss_peak_bytes,save_max_ms,load_max_ms,command_p95_ms,
 *     command_max_ms,verdicts:{frame,rss,save,load,command:"MET"|"FAIL"}}}]}.
 * REF={path,sha256}, only package-owned files under PERFORMANCE/runs/;
 * no shared-file REFs. Shared machine profile and runtime sources bind at commit.
 * hardware JSON: hostname,gpu,mem_bytes,os plus lscpu output from lscpu --json.
 * lscpu.lscpu contains {field:"Model name:",data:<observed CPU>}.
 * idle JSON: run_id,phase:"start"|"end",utc,load1,unity_editor_pids:[],
 * other_player_pids:[]; records must bracket recorder's started_utc/ended_utc.
 * M5-OD-16 binds load1_max to exactly 1.0 (M5-OD-12).
 * M5-OD-14: start load1 must be below that threshold.
 * End is process-only; load1 remains recorded, not threshold-gated.
 *
 * recorder JSON: source:"PerfRecorder",synthetic:false,run_id,commit,tree_hash,
 * archive_sha256,residents,player:"standalone",development:false,width:1920,
 * height:1080,render_scale:1,warmup_s:10,window_start_s:>=10,window_end_s:start+600,
 * started_utc,ended_utc (600 s apart),rss_source:"/proc/<pid>/status:VmRSS",
 * rss_sample_hz:1,api_source:"LiveTransport/LiveWire",
 * save:[{start_s,end_s,path:"real-save"}] (10),
 * load:[{start_s,end_s,path:"title-to-playable"}] (10),
 * commands:[{accepted_s,visible_s,marker:"first-rendered-response",
 *   viewport:{x,y,depth_m}}] (20); viewport is at visible_s, camera-forward
 * depth_m > 0, x and y each in inclusive [0,1] (M5-OD-16 / R1).
 * api:[{elapsed_s,purpose,request_id,wire_sent,outcome,adapter,model_id}].
 * Times are monotonic real seconds on the same run clock, within the window.
 * Frames CSV begins elapsed_s,frame_ms (E1's extra counter columns accepted);
 * RSS CSV is elapsed_s,rss_bytes. Both bracket the complete window.
 * M5-OD-16 coverage: in-window frame endpoints within 1 s of each edge,
 * consecutive in-window frame timestamps at most 1 s apart; no FPS floor.
 * RSS: >=600 in-window rows, endpoints within 1.1 s of each edge, every
 * consecutive interval 0.9..1.1 s for the declared 1 Hz cadence. These
 * tolerances admit both committed 888eb5a5 captures; budgets are unchanged.
 * RSS is external process VmRSS converted to bytes, NOT Unity texture-memory counters.
 * Save/load/command arrays, summary values and API totals MUST be derived from
 * real raw captures of this standalone build, never hand-written numbers.
 * All timers enclose the real operation; visible_s is a rendered-response marker.
 * API has all four GatewayPurpose values below, same window, unique request ids;
 * requests count actual wire sends, failures count their non-Applied outcomes.
 * No-key events have wire_sent:false,outcome:"NoKey": zero calls/failures,
 * connected:false,typed_gap:"no-key". Never estimate usage. Connected runs
 * require separate owner authority; this offline oracle reads sanitized records.
 *
 * P-1 Binding/Refs/Build -> schema/common.ref_rule, M5-OD-9, PERF-02-N2.
 * P-2 Hardware/Idle -> named hardware/environment, PERF-02-N2.
 * P-3 Window/Frame -> nearest-rank p95, PERF-02-P1.
 * P-4 RSS -> decimal 2,000,000,000-byte peak, PERF-02-P2.
 * P-5 Save/Load/Command -> raw timers + both p95/max command (pass on MAX),
 *   PERF-02-P3/P4/P5; budget table resolves suite_checks' p95-only ambiguity.
 * P-6 API -> real path/per-purpose usage + no-key gap, PERF-02-P6/N3, M5-D4.
 * P-7 Summary/Envelope -> truthful verdicts, complete ten cells, PERF-02-N1.
 * These are local SEIT support, not whole-case/AC/independent-review acceptance.
 * Package PASS means measurement complete and truthful, even for measured FAIL
 * budgets; envelope qualification requires every bound at both workloads.
 * GATE-M5 repeats measurements on PACKAGE and binds the second-person launch.
 *
 * negative-fixture uses only labelled SYNTHETIC parser/contract examples,
 * never hardware evidence. Each case starts with a passing control in a fresh
 * GUID temp git root, corrupts one input, requires its named rejection, and
 * deletes the root in finally. Includes all red_before (a)-(f) plus arithmetic,
 * ten missing budget/workload cells, provenance/API and false envelope controls.
 * Resource group isolated-csharp: owned files and git, no engine/display/network.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class PerformanceChecks
{
    private const string Record = "evidence/production/performance.json";
    private const string Runs = "evidence/production/PERFORMANCE/runs/";
    private const string Profile = "docs/plans/issue-3-specification/inputs/machine-profile.json";
    private const string M4 = "evidence/milestones/M4.json";
    private static readonly string[] Purposes = { "ResidentDecision", "ImageGeneration", "VoiceTranscription", "ImageSearch" };
    private static readonly string[] Budgets = { "frame", "rss", "save", "load", "command" };
    private static readonly double[] Bounds = { 16.7, 2000000000, 2000, 10000, 250 };
    private sealed class Rejection : Exception
    {
        public readonly string Id;
        public Rejection(string id) : base(id) { Id = id; }
    }
    private static void Need(bool ok, string id) { if (!ok) throw new Rejection(id); }
    private static string Text(JsonElement e, string key) { return e.GetProperty(key).GetString(); }
    private static double Num(JsonElement e, string key) { return Number(e.GetProperty(key)); }
    private static double Number(JsonElement e)
    {
        double n = e.GetDouble(); Need(!double.IsNaN(n) && !double.IsInfinity(n) && n >= 0, "P-1.Number"); return n;
    }
    private static bool Hex(string s, int n)
    {
        if (s == null || s.Length != n) return false;
        foreach (char c in s) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }
    private static JsonElement Parse(string s)
    { using (var doc = JsonDocument.Parse(s)) return doc.RootElement.Clone(); }
    private static JsonElement Read(string path) { return Parse(File.ReadAllText(path)); }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using (var p = Process.Start(start))
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { p.Kill(); throw new Rejection("P-1.GitTimeout"); }
            stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
            Need(p.ExitCode == 0, "P-1.GitBinding"); return stdout.Result.Trim();
        }
    }
    private static string Ref(string root, JsonElement r)
    {
        string path = Text(r, "path"), sha = Text(r, "sha256");
        Need(path != null && path.StartsWith(Runs, StringComparison.Ordinal)
            && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0, "P-1.RefScope");
        string full = root;
        foreach (string part in path.Split('/'))
        {
            Need(part != "" && part != "." && part != "..", "P-1.RefScope"); full = Path.Combine(full, part);
            Need(File.Exists(full) || Directory.Exists(full), "P-1.RefPresent");
            Need((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "P-1.RefScope");
        }
        Need(File.Exists(full) && Hex(sha, 64) && Hash(full) == sha, "P-1.RefHash"); return full;
    }
    private static DateTimeOffset Utc(JsonElement e, string key)
    {
        Need(DateTimeOffset.TryParse(Text(e, key), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value.Offset == TimeSpan.Zero, "P-2.Utc"); return value;
    }
    private static double Max(List<double> a) { a.Sort(); return a[a.Count - 1]; }
    private static double P95(List<double> a) { a.Sort(); return a[(int)Math.Ceiling(a.Count * .95) - 1]; }
    private static void Equal(double a, double b, string id) { Need(Math.Abs(a - b) <= .000001, id); }
    private static List<double> Csv(string path, string column, double start, double end, string id)
    {
        using (var reader = new StreamReader(path))
        {
            string header = reader.ReadLine(); Need(header != null, id + ".Header");
            string[] cols = header.Split(','); int ti = Array.IndexOf(cols, "elapsed_s"), vi = Array.IndexOf(cols, column);
            Need(ti >= 0 && vi >= 0, id + ".Header");
            double first = -1, last = -1, firstIn = -1, lastIn = -1;
            bool isRss = column == "rss_bytes"; var values = new List<double>(); string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] row = line.Split(',');
                Need(row.Length == cols.Length && double.TryParse(row[ti], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    && double.TryParse(row[vi], NumberStyles.Float, CultureInfo.InvariantCulture, out _), id + ".Row");
                double t = double.Parse(row[ti], CultureInfo.InvariantCulture), v = double.Parse(row[vi], CultureInfo.InvariantCulture);
                Need(double.IsFinite(t) && double.IsFinite(v) && t >= 0 && t > last && v > 0, id + ".Row");
                if (first < 0) first = t; last = t;
                if (column == "rss_bytes") Need(v == Math.Truncate(v), id + ".Bytes");
                if (t >= start && t <= end)
                {
                    if (firstIn < 0) firstIn = t;
                    if (lastIn >= 0) Need(isRss ? t - lastIn >= .9 && t - lastIn <= 1.1 : t - lastIn <= 1,
                        id + (isRss ? ".Cadence" : ".Window"));
                    lastIn = t; values.Add(v);
                }
            }
            Need(first >= 0 && first <= start && last >= end && values.Count >= (column == "rss_bytes" ? 600 : 2), id + ".Window");
            double edge = isRss ? 1.1 : 1;
            Need(firstIn >= start && firstIn - start <= edge && end - lastIn <= edge, id + ".Window");
            return values;
        }
    }
    private static void Idle(string root, JsonElement reference, string run, string phase, double threshold, DateTimeOffset boundary)
    {
        var idle = Read(Ref(root, reference));
        Need(Text(idle, "run_id") == run && Text(idle, "phase") == phase, "P-2.IdleBinding");
        double load = Num(idle, "load1");
        Need(idle.GetProperty("unity_editor_pids").GetArrayLength() == 0
            && idle.GetProperty("other_player_pids").GetArrayLength() == 0
            && (phase != "start" || load < threshold), "P-2.IdleBusy");
        var utc = Utc(idle, "utc");
        Need(phase == "start" ? utc <= boundary : utc >= boundary, "P-2.IdleTime");
    }
    private static List<double> Timers(JsonElement capture, JsonElement workload, string rawKey, string summaryKey,
        int count, string startKey, string endKey, string markerKey, string marker, double start, double end)
    {
        Need(capture.TryGetProperty(rawKey, out var events) && events.GetArrayLength() == count
            && workload.TryGetProperty(summaryKey, out var summary) && summary.GetArrayLength() == count, "P-5.TrialCount NOT RUN");
        var result = new List<double>(); int i = 0;
        foreach (var trial in events.EnumerateArray())
        {
            double a = Num(trial, startKey), b = Num(trial, endKey);
            Need(a >= start && b >= a && b <= end && Text(trial, markerKey) == marker, "P-5.RealOperation");
            if (rawKey == "commands")
            {
                Need(trial.TryGetProperty("viewport", out var viewport) && viewport.ValueKind == JsonValueKind.Object, "P-5.CommandViewport");
                double x = viewport.GetProperty("x").GetDouble(), y = viewport.GetProperty("y").GetDouble(),
                    depth = viewport.GetProperty("depth_m").GetDouble();
                Need(x >= 0 && x <= 1 && y >= 0 && y <= 1 && double.IsFinite(depth) && depth > 0, "P-5.CommandViewport");
            }
            double ms = (b - a) * 1000;
            Equal(ms, Number(workload.GetProperty(summaryKey)[i++]), "P-5.TimerSummary"); result.Add(ms);
        }
        return result;
    }
    private static void Api(JsonElement capture, JsonElement workload, double start, double end)
    {
        Need(Text(capture, "api_source") == "LiveTransport/LiveWire", "P-6.Source");
        var summaries = workload.GetProperty("api"); Need(summaries.GetArrayLength() == Purposes.Length, "P-6.Purposes");
        var seen = new HashSet<string>(); var ids = new HashSet<string>();
        var requests = new int[Purposes.Length]; var failures = new int[Purposes.Length]; var events = new int[Purposes.Length];
        var adapters = new string[Purposes.Length]; var models = new string[Purposes.Length]; var noKey = new bool[Purposes.Length];
        foreach (var row in capture.GetProperty("api").EnumerateArray())
        {
            int p = Array.IndexOf(Purposes, Text(row, "purpose"));
            Need(p >= 0 && Num(row, "elapsed_s") >= start && Num(row, "elapsed_s") <= end, "P-6.WindowPurpose");
            string adapter = Text(row, "adapter"), model = Text(row, "model_id"), id = Text(row, "request_id");
            Need(!string.IsNullOrWhiteSpace(adapter) && !string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(id)
                && ids.Add(Purposes[p] + ":" + id), "P-6.EventIdentity");
            if (events[p]++ > 0) Need(adapters[p] == adapter && models[p] == model, "P-6.ModelIdentity");
            adapters[p] = adapter; models[p] = model;
            string outcome = Text(row, "outcome");
            if (row.GetProperty("wire_sent").GetBoolean())
            {
                Need(Enum.TryParse<Blockparty.Contracts.GatewayOutcome>(outcome, out var parsed)
                    && Enum.IsDefined(typeof(Blockparty.Contracts.GatewayOutcome), parsed), "P-6.Outcome");
                requests[p]++; if (outcome != "Applied") failures[p]++;
            }
            else { Need(outcome == "NoKey", "P-6.NoKey"); noKey[p] = true; }
        }
        foreach (var row in summaries.EnumerateArray())
        {
            int p = Array.IndexOf(Purposes, Text(row, "purpose"));
            Need(p >= 0 && seen.Add(Purposes[p]) && events[p] > 0, "P-6.Purposes");
            Need(row.GetProperty("requests").GetInt32() == requests[p] && row.GetProperty("failures").GetInt32() == failures[p]
                && Text(row, "adapter") == adapters[p] && Text(row, "model_id") == models[p]
                && row.GetProperty("connected").GetBoolean() == (requests[p] > 0)
                && Text(row, "typed_gap") == (noKey[p] ? "no-key" : ""), "P-6.Totals");
        }
    }
    private static void Validate(string root, bool synthetic = false)
    {
        Need(File.Exists(Path.Combine(root, Record)), "P-0.MissingEvidence: evidence/production/performance.json NOT RUN");
        var record = Read(Path.Combine(root, Record));
        Need(Text(record, "schema") == "blockparty-perf/1" && record.GetProperty("synthetic").GetBoolean() == synthetic, "P-1.Schema");
        string commit = Text(record, "commit"), tree = Text(record, "tree_hash"), gate = Text(record, "gate_m4_commit");
        Need(Hex(commit, 40) && Hex(tree, 40) && Hex(gate, 40), "P-1.Identity");
        Need(Git(root, "rev-parse", commit + "^{tree}") == tree, "P-1.Tree");
        Git(root, "merge-base", "--is-ancestor", gate, commit);
        Git(root, "merge-base", "--is-ancestor", gate, "origin/main");
        Need(Text(Parse(Git(root, "show", gate + ":" + M4)), "status") == "PASS", "P-1.GateM4");
        Need(Text(Parse(Git(root, "show", commit + ":" + M4)), "status") == "PASS", "P-1.GateM4");
        var build = record.GetProperty("build"); string archive = Text(build, "archive_sha256");
        Need(Hex(archive, 64) && !build.GetProperty("development").GetBoolean()
            && Text(build, "platform") == "StandaloneLinux64", "P-1.ReleaseStandalone");
        var host = record.GetProperty("host"); var hardware = Read(Ref(root, record.GetProperty("hardware")));
        string model = null;
        foreach (var field in hardware.GetProperty("lscpu").GetProperty("lscpu").EnumerateArray())
            if (Text(field, "field") == "Model name:") model = Text(field, "data");
        Need(!string.IsNullOrWhiteSpace(model) && Text(host, "lscpu_model") == model, "P-2.Lscpu");
        foreach (string key in new[] { "hostname", "gpu", "os" })
            Need(!string.IsNullOrWhiteSpace(Text(host, key)) && Text(host, key) == Text(hardware, key), "P-2.Hardware");
        Need(Text(host, "hostname") == "bto-build-hosti7" && Text(host, "gpu") == "Intel UHD Graphics 770"
            && Num(host, "mem_bytes") > 0 && Num(host, "mem_bytes") == Num(hardware, "mem_bytes"), "P-2.NamedHost");
        var profile = Parse(Git(root, "show", commit + ":" + Profile));
        bool match = model == Text(profile.GetProperty("cpu"), "Model name")
            && Num(host, "mem_bytes") == Num(profile, "memory_bytes_kernel_visible")
            && Text(host, "gpu") == Text(profile.GetProperty("gpu"), "name")
            && Text(host, "os") == Text(profile.GetProperty("os"), "PRETTY_NAME");
        string gap = Text(host, "compat_gap");
        Need(host.GetProperty("machine_profile_match").GetBoolean() == match && !string.IsNullOrWhiteSpace(gap), "P-2.HostCompatibilityGap");
        var threshold = record.GetProperty("idle_threshold"); double loadMax = Num(threshold, "load1_max");
        Need(loadMax == 1.0 && !string.IsNullOrWhiteSpace(Text(threshold, "owner_decision")), "P-2.IdleThreshold");
        var workloads = record.GetProperty("workloads"); Need(workloads.GetArrayLength() == 2, "P-7.Workloads NOT RUN");
        var residents = new HashSet<int>(); var runIds = new HashSet<string>(); bool qualified = true;
        foreach (var work in workloads.EnumerateArray())
        {
            int n = work.GetProperty("residents").GetInt32(); string run = Text(work, "run_id");
            Need((n == 8 || n == 24) && residents.Add(n) && !string.IsNullOrWhiteSpace(run) && runIds.Add(run), "P-7.Workloads NOT RUN");
            Need(Text(work, "compat_gap") == gap, "P-2.ResultGap");
            var capture = Read(Ref(root, work.GetProperty("recorder")));
            Need(Text(capture, "source") == "PerfRecorder" && capture.GetProperty("synthetic").GetBoolean() == synthetic
                && Text(capture, "commit") == commit && Text(capture, "tree_hash") == tree
                && Text(capture, "archive_sha256") == archive && Text(capture, "run_id") == run
                && capture.GetProperty("residents").GetInt32() == n, "P-1.CaptureBinding");
            Need(Text(capture, "player") == "standalone" && !capture.GetProperty("development").GetBoolean()
                && Num(capture, "width") == 1920 && Num(capture, "height") == 1080
                && Num(capture, "render_scale") == 1, "P-1.ReleaseStandalone");
            double start = Num(capture, "window_start_s"), end = Num(capture, "window_end_s");
            Need(Num(capture, "warmup_s") == 10 && start >= 10 && end - start == 600, "P-3.Window");
            var started = Utc(capture, "started_utc"); var ended = Utc(capture, "ended_utc");
            Need((ended - started).TotalSeconds == 600, "P-3.WallWindow");
            Idle(root, work.GetProperty("idle_start"), run, "start", loadMax, started);
            Idle(root, work.GetProperty("idle_end"), run, "end", loadMax, ended);
            Need(work.TryGetProperty("frames", out _) && work.TryGetProperty("rss", out _), "P-7.MissingBudget NOT RUN");
            var frames = Csv(Ref(root, work.GetProperty("frames")), "frame_ms", start, end, "P-3.Frames");
            Need(Text(capture, "rss_source") == "/proc/<pid>/status:VmRSS" && Num(capture, "rss_sample_hz") == 1, "P-4.Source");
            var rss = Csv(Ref(root, work.GetProperty("rss")), "rss_bytes", start, end, "P-4.Rss");
            var saves = Timers(capture, work, "save", "save_ms", 10, "start_s", "end_s", "path", "real-save", start, end);
            var loads = Timers(capture, work, "load", "load_ms", 10, "start_s", "end_s", "path", "title-to-playable", start, end);
            var commands = Timers(capture, work, "commands", "command_ms", 20, "accepted_s", "visible_s", "marker", "first-rendered-response", start, end);
            Api(capture, work, start, end);
            var summary = work.GetProperty("summary");
            var measured = new[] { P95(frames), Max(rss), Max(saves), Max(loads), Max(commands) };
            var names = new[] { "frame_p95_ms", "rss_peak_bytes", "save_max_ms", "load_max_ms", "command_max_ms" };
            Equal(Num(summary, "command_p95_ms"), P95(commands), "P-5.CommandP95");
            for (int b = 0; b < Budgets.Length; b++)
            {
                Equal(Num(summary, names[b]), measured[b], "P-7.Value." + Budgets[b]);
                bool met = measured[b] <= Bounds[b]; qualified &= met;
                Need(Text(summary.GetProperty("verdicts"), Budgets[b]) == (met ? "MET" : "FAIL"), "P-7.Verdict." + Budgets[b]);
            }
        }
        Need(record.GetProperty("envelope_qualified").GetBoolean() == qualified, "P-7.Envelope");
    }
    private static Dictionary<string, object> Obj(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    private static void Write(string root, string path, object value)
    {
        string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, JsonSerializer.Serialize(value) + "\n");
    }
    private static Dictionary<string, object> Reference(string root, string path)
    { return Obj("path", path, "sha256", Hash(Path.Combine(root, path))); }
    private static Dictionary<string, object> Child(Dictionary<string, object> o, string key)
    { return (Dictionary<string, object>)o[key]; }
    private static Dictionary<string, object> Work(Dictionary<string, object> r, int n = 0)
    { return ((List<Dictionary<string, object>>)r["workloads"])[n]; }
    private static void EditRaw(string root, Dictionary<string, object> work, string field, Action<Dictionary<string, object>> edit)
    {
        var reference = Child(work, field); string path = (string)reference["path"];
        var raw = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, path)));
        edit(raw); Write(root, path, raw); work[field] = Reference(root, path);
    }
    private static void EditCommand(string root, Dictionary<string, object> record, Action<Dictionary<string, object>> edit)
    {
        EditRaw(root, Work(record), "recorder", o =>
        {
            var commands = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(((JsonElement)o["commands"]).GetRawText());
            edit(commands[commands.Count - 1]); o["commands"] = commands;
        });
    }
    private static Dictionary<string, object> Synthetic(string root)
    {
        Directory.CreateDirectory(root); Git(root, "init", "--quiet");
        Write(root, Profile, Obj("synthetic", true, "cpu", Obj("Model name", "Intel(R) Core(TM) i5-14500"),
            "memory_bytes_kernel_visible", 32782192640L, "gpu", Obj("name", "Intel UHD Graphics 770"),
            "os", Obj("PRETTY_NAME", "SYNTHETIC OS")));
        Write(root, M4, Obj("synthetic", true, "status", "PASS"));
        Git(root, "add", "--", Profile, M4);
        Git(root, "-c", "user.name=Synthetic", "-c", "user.email=synthetic@example.invalid", "-c", "commit.gpgsign=false",
            "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "SYNTHETIC PERFORMANCE fixture, not measurement evidence");
        string commit = Git(root, "rev-parse", "HEAD"), tree = Git(root, "rev-parse", "HEAD^{tree}");
        Git(root, "update-ref", "refs/remotes/origin/main", commit);
        string archive = new string('a', 64), gap = "SYNTHETIC parser example; host compatibility unqualified";
        Write(root, Runs + "hardware.json", Obj("synthetic", true, "hostname", "bto-build-hosti7", "gpu", "Intel UHD Graphics 770",
            "mem_bytes", 32782192640L, "os", "SYNTHETIC OS", "lscpu", Obj("lscpu", new[] {
                Obj("field", "Model name:", "data", "Intel(R) Core(TM) i5-14500") })));
        var workloads = new List<Dictionary<string, object>>();
        foreach (int n in new[] { 8, 24 })
        {
            string lane = Runs + "synthetic-" + n + "-";
            var frames = new StringBuilder("elapsed_s,frame_ms,draw_calls,setpass_calls,texture_memory_bytes\n");
            for (int i = 1000; i <= 61000; i++) frames.Append((i / 100.0).ToString("F2", CultureInfo.InvariantCulture)).Append(",10,0,0,0\n");
            File.WriteAllText(Path.Combine(root, lane + "frames.csv"), frames.ToString());
            var rss = new StringBuilder("elapsed_s,rss_bytes\n");
            for (int i = 10; i <= 610; i++) rss.Append(i).Append(",2000000000\n");
            File.WriteAllText(Path.Combine(root, lane + "rss.csv"), rss.ToString());
            var saves = new List<object>(); var loads = new List<object>(); var commands = new List<object>();
            var saveMs = new List<double>(); var loadMs = new List<double>(); var commandMs = new List<double>();
            for (int i = 0; i < 10; i++)
            {
                saves.Add(Obj("start_s", 20 + i * 15, "end_s", 22 + i * 15, "path", "real-save")); saveMs.Add(2000);
                loads.Add(Obj("start_s", 200 + i * 15, "end_s", 210 + i * 15, "path", "title-to-playable")); loadMs.Add(10000);
            }
            for (int i = 0; i < 20; i++)
            {
                commands.Add(Obj("accepted_s", 400 + i, "visible_s", 400.25 + i, "marker", "first-rendered-response",
                    "viewport", Obj("x", .5, "y", .5, "depth_m", 1))); commandMs.Add(250);
            }
            var rawApi = new List<object>(); var api = new List<Dictionary<string, object>>();
            foreach (string purpose in Purposes)
            {
                rawApi.Add(Obj("elapsed_s", 15, "purpose", purpose, "request_id", "synthetic-1", "wire_sent", false,
                    "outcome", "NoKey", "adapter", "synthetic-adapter", "model_id", "synthetic-model"));
                api.Add(Obj("purpose", purpose, "requests", 0, "failures", 0, "adapter", "synthetic-adapter",
                    "model_id", "synthetic-model", "connected", false, "typed_gap", "no-key"));
            }
            string run = "SYNTHETIC-" + n;
            Write(root, lane + "recorder.json", Obj("source", "PerfRecorder", "synthetic", true, "run_id", run, "commit", commit,
                "tree_hash", tree, "archive_sha256", archive, "residents", n, "player", "standalone", "development", false,
                "width", 1920, "height", 1080, "render_scale", 1, "warmup_s", 10, "window_start_s", 10, "window_end_s", 610,
                "started_utc", "2026-11-02T12:00:00Z", "ended_utc", "2026-11-02T12:10:00Z",
                "rss_source", "/proc/<pid>/status:VmRSS", "rss_sample_hz", 1, "api_source", "LiveTransport/LiveWire",
                "save", saves, "load", loads, "commands", commands, "api", rawApi));
            foreach (string phase in new[] { "start", "end" })
                Write(root, lane + phase + ".json", Obj("synthetic", true, "run_id", run, "phase", phase,
                    "utc", phase == "start" ? "2026-11-02T11:59:59Z" : "2026-11-02T12:10:01Z", "load1", .1,
                    "unity_editor_pids", new int[0], "other_player_pids", new int[0]));
            workloads.Add(Obj("residents", n, "run_id", run, "compat_gap", gap, "frames", Reference(root, lane + "frames.csv"),
                "rss", Reference(root, lane + "rss.csv"), "recorder", Reference(root, lane + "recorder.json"),
                "idle_start", Reference(root, lane + "start.json"), "idle_end", Reference(root, lane + "end.json"),
                "save_ms", saveMs, "load_ms", loadMs, "command_ms", commandMs, "api", api,
                "summary", Obj("frame_p95_ms", 10, "rss_peak_bytes", 2000000000, "save_max_ms", 2000,
                    "load_max_ms", 10000, "command_p95_ms", 250, "command_max_ms", 250,
                    "verdicts", Obj("frame", "MET", "rss", "MET", "save", "MET", "load", "MET", "command", "MET"))));
        }
        return Obj("schema", "blockparty-perf/1", "synthetic", true, "commit", commit, "tree_hash", tree, "gate_m4_commit", commit,
            "build", Obj("archive_sha256", archive, "development", false, "platform", "StandaloneLinux64"),
            "host", Obj("hostname", "bto-build-hosti7", "lscpu_model", "Intel(R) Core(TM) i5-14500", "gpu", "Intel UHD Graphics 770",
                "mem_bytes", 32782192640L, "os", "SYNTHETIC OS", "machine_profile_match", true, "compat_gap", gap),
            "hardware", Reference(root, Runs + "hardware.json"), "idle_threshold", Obj("load1_max", 1, "owner_decision", "SYNTHETIC ONLY"),
            "envelope_qualified", true, "workloads", workloads);
    }
    private static void TruthfulFailure(string root, Dictionary<string, object> r, string budget)
    {
        var w = Work(r); var summary = Child(w, "summary");
        Child(summary, "verdicts")[budget] = "FAIL"; r["envelope_qualified"] = false;
        if (budget == "frame" || budget == "rss")
        {
            var reference = Child(w, budget == "frame" ? "frames" : "rss"); string path = (string)reference["path"];
            string text = File.ReadAllText(Path.Combine(root, path));
            File.WriteAllText(Path.Combine(root, path), budget == "frame" ? text.Replace(",10,0,0,0", ",20,0,0,0") : text.Replace(",2000000000", ",2000000001"));
            w[budget == "frame" ? "frames" : "rss"] = Reference(root, path);
            summary[budget == "frame" ? "frame_p95_ms" : "rss_peak_bytes"] = budget == "frame" ? 20 : 2000000001;
        }
        else
        {
            string raw = budget == "command" ? "commands" : budget;
            double value = budget == "save" ? 2001 : budget == "load" ? 10001 : 251;
            var timings = new List<object>(); var values = new List<double>(); int count = budget == "command" ? 20 : 10;
            for (int i = 0; i < count; i++)
            {
                double begin = 20 + i * 20;
                timings.Add(budget == "command"
                    ? Obj("accepted_s", begin, "visible_s", begin + value / 1000, "marker", "first-rendered-response",
                        "viewport", Obj("x", .5, "y", .5, "depth_m", 1))
                    : Obj("start_s", begin, "end_s", begin + value / 1000, "path", budget == "save" ? "real-save" : "title-to-playable"));
                values.Add(value);
            }
            EditRaw(root, w, "recorder", o => o[raw] = timings); w[budget + "_ms"] = values;
            summary[budget + "_max_ms"] = value;
            if (budget == "command") summary["command_p95_ms"] = value;
        }
    }
    private static void Case(string name, string expected, Action<string, Dictionary<string, object>> mutate)
    {
        string root = Path.Combine(Path.GetTempPath(), "blockparty-PERFORMANCE-" + Guid.NewGuid().ToString("N"));
        try
        {
            var record = Synthetic(root); Write(root, Record, record); Validate(root, true);
            mutate(root, record); Write(root, Record, record);
            string actual = null;
            try { Validate(root, true); } catch (Rejection e) { actual = e.Id; }
            Need(actual == expected, "FIXTURE " + name + " expected " + (expected ?? "PASS") + ", got " + (actual ?? "PASS"));
            Console.WriteLine((expected == null ? "PASS" : "FAIL") + " negative-fixture " + name
                + " [SYNTHETIC " + (expected == null ? "positive control" : "expected rejection: " + actual) + "]");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static int Negatives()
    {
        Equal(P95(new List<double> { 3, 1, 2 }), 3, "FIXTURE NearestRank");
        var outlier = new List<double>(); for (int i = 0; i < 19; i++) outlier.Add(250); outlier.Add(251);
        Equal(P95(outlier), 250, "FIXTURE P95NotMax"); Equal(Max(outlier), 251, "FIXTURE MaxNotP95");
        Case("valid-control", null, (root, r) => { });
        Case("review-raised-threshold-load50", "P-2.IdleThreshold", (root, r) =>
        {
            Child(r, "idle_threshold")["load1_max"] = 100;
            EditRaw(root, Work(r), "idle_start", o => o["load1"] = 50);
        });
        Case("review-frame-data-only-10ms", "P-3.Frames.Window", (root, r) =>
        {
            var w = Work(r); string path = (string)Child(w, "frames")["path"];
            File.WriteAllText(Path.Combine(root, path), "elapsed_s,frame_ms\n10,10\n10.01,10\n611,10\n");
            w["frames"] = Reference(root, path);
        });
        Case("frame-interior-gap", "P-3.Frames.Window", (root, r) =>
        {
            var w = Work(r); string path = (string)Child(w, "frames")["path"];
            var rows = new List<string>();
            foreach (string row in File.ReadAllLines(Path.Combine(root, path)))
                if (row.StartsWith("elapsed_s,") || double.Parse(row.Split(',')[0], CultureInfo.InvariantCulture) <= 300
                    || double.Parse(row.Split(',')[0], CultureInfo.InvariantCulture) >= 302) rows.Add(row);
            File.WriteAllLines(Path.Combine(root, path), rows); w["frames"] = Reference(root, path);
        });
        Case("review-rss-600-samples-in-599ms", "P-4.Rss.Cadence", (root, r) =>
        {
            var w = Work(r); string path = (string)Child(w, "rss")["path"];
            var rows = new StringBuilder("elapsed_s,rss_bytes\n");
            for (int i = 0; i < 600; i++) rows.Append((10 + i * .001).ToString("F3", CultureInfo.InvariantCulture)).Append(",2000000000\n");
            rows.Append("611,2000000000\n"); File.WriteAllText(Path.Combine(root, path), rows.ToString());
            w["rss"] = Reference(root, path);
        });
        Case("rss-interior-gap", "P-4.Rss.Cadence", (root, r) =>
        {
            var w = Work(r); string path = (string)Child(w, "rss")["path"];
            File.WriteAllText(Path.Combine(root, path), File.ReadAllText(Path.Combine(root, path)).Replace("300,2000000000\n", ""));
            w["rss"] = Reference(root, path);
        });
        Case("review-command-behind-camera", "P-5.CommandViewport", (root, r) =>
            EditCommand(root, r, o => o["viewport"] = Obj("x", .5, "y", .5, "depth_m", -7.89)));
        Case("command-missing-viewport", "P-5.CommandViewport", (root, r) => EditCommand(root, r, o => o.Remove("viewport")));
        foreach (string key in new[] { "x", "y", "depth_m" })
        foreach (double value in key == "depth_m" ? new[] { 0.0 } : new[] { -.01, 1.01 })
        {
            string axis = key; double bad = value;
            Case("command-viewport-" + axis + "-" + bad.ToString(CultureInfo.InvariantCulture), "P-5.CommandViewport", (root, r) =>
                EditCommand(root, r, o => { var viewport = Obj("x", .5, "y", .5, "depth_m", 1); viewport[axis] = bad; o["viewport"] = viewport; }));
        }
        Case("command-viewport-inclusive-edges", null, (root, r) =>
            EditCommand(root, r, o => o["viewport"] = Obj("x", 0, "y", 1, "depth_m", .01)));
        Case("wrong-host", "P-2.NamedHost", (root, r) =>
        {
            Child(r, "host")["hostname"] = "other-host";
            EditRaw(root, r, "hardware", o => o["hostname"] = "other-host");
        });
        Case("a-short-frames", "P-3.Frames.Window", (root, r) =>
        {
            var w = Work(r); string path = (string)Child(w, "frames")["path"];
            File.WriteAllText(Path.Combine(root, path), "elapsed_s,frame_ms\n10,10\n609.99,10\n"); w["frames"] = Reference(root, path);
        });
        foreach (int workload in new[] { 0, 1 }) foreach (string budget in Budgets)
        {
            int index = workload; string key = budget;
            Case("b-missing-" + (index == 0 ? 8 : 24) + "-" + key,
                key == "frame" || key == "rss" ? "P-7.MissingBudget NOT RUN" : "P-5.TrialCount NOT RUN",
                (root, r) => Work(r, index).Remove(key == "frame" ? "frames" : key == "rss" ? "rss" : key + "_ms"));
        }
        Case("b-missing-workload", "P-7.Workloads NOT RUN", (root, r) => ((List<Dictionary<string, object>>)r["workloads"]).RemoveAt(1));
        Case("c-p95-lie", "P-7.Value.frame", (root, r) =>
        { TruthfulFailure(root, r, "frame"); Child(Work(r), "summary")["frame_p95_ms"] = 10; });
        foreach (string phase in new[] { "start", "end" }) foreach (string busy in new[] { "editor", "player", "load" })
        {
            string end = phase, kind = busy;
            Case("d-busy-" + end + "-" + kind, end == "end" && kind == "load" ? null : "P-2.IdleBusy", (root, r) => EditRaw(root, Work(r), "idle_" + end,
                o => o[kind == "load" ? "load1" : kind == "editor" ? "unity_editor_pids" : "other_player_pids"] =
                    kind == "load" ? (object)1 : new[] { 12345 }));
        }
        Case("e-editor", "P-1.ReleaseStandalone", (root, r) => EditRaw(root, Work(r), "recorder", o => o["player"] = "editor"));
        Case("e-development", "P-1.ReleaseStandalone", (root, r) => Child(r, "build")["development"] = true);
        Case("f-host-mismatch-no-gap", "P-2.HostCompatibilityGap", (root, r) =>
        {
            Child(r, "host")["lscpu_model"] = "SYNTHETIC mismatched CPU"; Child(r, "host")["machine_profile_match"] = false;
            Child(r, "host")["compat_gap"] = "";
            EditRaw(root, r, "hardware", o => o["lscpu"] = Obj("lscpu", new[] { Obj("field", "Model name:", "data", "SYNTHETIC mismatched CPU") }));
        });
        foreach (string budget in Budgets)
        {
            string key = budget;
            Case("truthful-failed-" + key, null, (root, r) => TruthfulFailure(root, r, key));
            Case("false-envelope-" + key, "P-7.Envelope", (root, r) => { TruthfulFailure(root, r, key); r["envelope_qualified"] = true; });
        }
        Case("command-p95-met-max-fails", null, (root, r) =>
        {
            var w = Work(r); var commands = new List<object>(); var values = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                double ms = i == 19 ? 251 : 250;
                commands.Add(Obj("accepted_s", 20 + i, "visible_s", 20 + i + ms / 1000, "marker", "first-rendered-response",
                    "viewport", Obj("x", .5, "y", .5, "depth_m", 1)));
                values.Add(ms);
            }
            EditRaw(root, w, "recorder", o => o["commands"] = commands); w["command_ms"] = values;
            Child(w, "summary")["command_max_ms"] = 251; Child(Child(w, "summary"), "verdicts")["command"] = "FAIL";
            r["envelope_qualified"] = false;
        });
        Case("connected-api-counts-separate-failures", null, (root, r) =>
        {
            var w = Work(r); var api = new List<object>();
            foreach (string purpose in Purposes) foreach (string outcome in new[] { "Applied", "Timeout" })
                api.Add(Obj("elapsed_s", 15, "purpose", purpose, "request_id", "synthetic-" + outcome,
                    "wire_sent", true, "outcome", outcome, "adapter", "synthetic-adapter", "model_id", "synthetic-model"));
            EditRaw(root, w, "recorder", o => o["api"] = api);
            foreach (var row in (List<Dictionary<string, object>>)w["api"])
            { row["requests"] = 2; row["failures"] = 1; row["connected"] = true; row["typed_gap"] = ""; }
        });
        Case("wrong-tree", "P-1.Tree", (root, r) => r["tree_hash"] = new string('0', 40));
        Case("missing-gate", "P-1.GitBinding", (root, r) => r["gate_m4_commit"] = new string('0', 40));
        Case("wrong-capture-build", "P-1.CaptureBinding", (root, r) => EditRaw(root, Work(r), "recorder", o => o["archive_sha256"] = new string('b', 64)));
        Case("shared-ref", "P-1.RefScope", (root, r) => Child(Work(r), "frames")["path"] = "Assets/Blockparty/Presentation/PerfRecorder.cs");
        Case("tampered-ref", "P-1.RefHash", (root, r) => Child(Work(r), "frames")["sha256"] = new string('0', 64));
        Case("no-key-estimate", "P-6.Totals", (root, r) => ((List<Dictionary<string, object>>)Work(r)["api"])[0]["requests"] = 1);
        Case("missing-api-purpose", "P-6.Purposes", (root, r) => ((List<Dictionary<string, object>>)Work(r)["api"]).RemoveAt(0));
        Case("command-accept-only", "P-5.RealOperation", (root, r) => EditRaw(root, Work(r), "recorder", o =>
        {
            var commands = new List<object>(); for (int i = 0; i < 20; i++) commands.Add(Obj("accepted_s", 20 + i, "visible_s", 20 + i, "marker", "accepted"));
            o["commands"] = commands;
        }));
        Console.WriteLine("PERFORMANCE SYNTHETIC FIXTURES PASS (not measurement evidence)"); return 0;
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "negative-fixture") return Negatives();
            Need(args.Length == 0, "P-0.Arguments"); Validate(Directory.GetCurrentDirectory());
            Console.WriteLine("PERFORMANCE ALL PASS"); return 0;
        }
        catch (Rejection e) { Console.WriteLine("FAIL " + e.Id); return 1; }
        catch (Exception e) when (e is IOException || e is JsonException || e is KeyNotFoundException
            || e is InvalidOperationException || e is FormatException || e is OverflowException || e is ArgumentException)
        { Console.WriteLine("FAIL P-0.MalformedEvidence " + e.GetType().Name); return 1; }
    }
}
