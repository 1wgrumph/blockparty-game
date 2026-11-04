/* SOAK evidence oracle, issue #96, full-game-rev2-m5-1, TDD red step.
 * M5-OD-18 REPLACES M5-OD-8: exactly one continuous >=3600 s live-play session,
 * covering storm, birth, estate, care, scarcity, growth, relocation and recovery.
 * No rule-22 5+2 reservation. M6 RECORD-SESSION still needs its own uncut 90 min.
 * M5-OD-9 allows this oracle before GATE-M4/REGRESSION. M5-OD-22 runs SOAK
 * in parallel with REGRESSION: GATE-M5 owns REGRESSION ancestry, PASS receipts
 * and the regression REF byte copy, checking both packages on landed main.
 * SOAK still binds its build commit/tree and the landed GATE-M4 receipt.
 *
 * DEVELOPER DELIVERABLE CONTRACT (all REF = {path,sha256}, owned regular files
 * below evidence/production/SOAK/, no symlinks or shared-file references):
 * On build-host, DISPLAY=:0, run the full standalone player with RulePlayer
 * through ordinary commands, using prepared real transitions. Capture each
 * complete session with a qualified recorder (GAP-CAPTURE: none qualified yet).
 * Developer supplies the producer entrypoint evidence/production/SOAK/run.sh;
 * on the i7 invoke `bash evidence/production/SOAK/run.sh` to build, run the single
 * continuous session, capture and export the records described here. It must
 * stop with GAP-CAPTURE until recorder qualification is established, and record
 * its exact game/recorder/export commands with the captured run artifacts.
 * Build with pinned Unity 6000.3.10f1; record player:"standalone", platform:
 * "StandaloneLinux64", development:false and archive_sha256 in build.
 * Produce evidence/production/soak.json with schema:"blockparty-soak/1",
 * synthetic:false, commit,tree_hash,gate_m4_commit,build,
 * dispositions:REF (normalized {cases:[{id,disposition,log?:REF}]}),
 * sessions:[{id,target_s,started_utc,ended_utc,start_save:REF,seed,control,
 * log:REF,input:REF,snapshots:REF,prepared:REF,video,counts}], obligations.
 * commit/tree_hash describe the captured source. gate_m4_commit must be an
 * ancestor of commit AND origin/main and contain its passing receipt.
 * build: {archive_sha256,player,platform,development}; sessions share that build.
 * Each JSONL row in log/input/snapshots, and capture.json, repeats session_id,
 * build_commit,tree_hash,archive_sha256,start_save_sha256,seed,control and utc.
 * control is automated|mixed (session policy); input rows also name controller
 * human|automated and command_id, outcome. Even while idle/restarting, input
 * and snapshots heartbeat <=5 s apart; all three streams bracket the session.
 * log rows: utc,game_time,event,resident,command_id,outcome, plus above binding.
 * Every actual applied command has event:"command-applied", outcome:"Applied",
 * command_id and a matching input row. No synthetic state edits/rewards allowed.
 * birth/estate rows carry effect_id; unique across reloads, not reset on reload.
 * prepared = {transitions:[{id,event}]} committed BEFORE session start in commit;
 * every id must occur in log with transition_id and outcome:"Completed".
 * Required events: storm,birth,estate,care,scarcity,reload,kill-during-save,
 * growth,relocation,recovery,travel,speed,player-intervention. Snapshots contain
 * residents:[{id,living}], initially eight living; final membership/liveness
 * must match birth/death and save-proven arrival records. Arrival rows carry
 * event:"arrival", resident (new id string), game_time, reason:"vacancy-arrival",
 * outcome:"Completed", command_id:"", plus the common provenance and UTC.
 * The last save-validated at/before game_time must lack that id; the first
 * strictly after must contain it alive with positive home/household assignments
 * and a higher resident count. Both saves are hash-bound REFs; optional
 * before_save/after_save REFs must name that exact adjacent pair. Save payload
 * world.gameSeconds must match its validated row. A batch may add several ids.
 * Birth rows require a hash-bound validated save at/after game_time containing
 * the game's kind:"Birth", detail:"child <resident>" (actor is the parent),
 * with event gameSeconds <= row game_time. Resident progress events are block-onset
 * (reason nonempty), reevaluate, action-completed, accepted-command-progress,
 * death. Only living residents with a reason + reevaluation and NO completed
 * action/accepted-command progress since onset count permanently blocked.
 * Each reload carries saved_state:REF,loaded_state:REF (canonical persisted
 * state exports; equal hashes). save-validated rows carry generation and save:
 * REF. kill-during-save carries inflight_generation, strictly after the last
 * validated generation; the next reload carries generation and saved_state
 * equal to that last validated save, and is followed by >=600 s continued play.
 * The intentional kill is not an unexpected crash; record both accurately.
 * counts = {span_s,snapshot_gap_s,input_gap_s,crash,lost_save,blocked,duplicates,
 * events:{<each required event>:actual count}}; all recomputed, never estimated.
 * video = {container:"mp4"|"matroska",sha256,bytes,duration_s,ffprobe:REF,
 * tasks:REF,capture:REF}. Native ffprobe JSON has format{format_name,duration,
 * size} and one video stream{codec_type,avg_frame_rate}. tasks is sorted video
 * presentation timestamps CSV headed pts_s, covering 0..duration within two
 * frames; max gap <=2 frame intervals. capture repeats provenance and adds
 * container,sha256,bytes,duration_s,storage,uncut:true,exit_code:0. Recorder must
 * hash the actual closed video and log it here. Large video stays outside git;
 * oracle crosschecks recorder hash/size, NOT the external video bytes themselves.
 * ffprobe duration must match game-log span within 2 s; wall duration >= target.
 * All UTCs, PTS, bytes/hashes, input/action/state rows, saves, process outcomes,
 * counts and capture values MUST come from real recorder/game/exporter logs,
 * never hand-written. Timestamp continuity alone cannot attest honest capture.
 * Dispositions cover exactly the 606 ids from seit.json AT commit, once each:
 * component-pass requires exact 'PASS <id>' in hashed log, with no FAIL/SKIP/
 * NOT RUN for that id; component-fail,unrun,pending:<gate>,spec-evidence are
 * distinct. spec-evidence only SPEC-01/02/06/08 and must cite its delivery log.
 * obligations: exactly complete-build:GATE-M5, soak:GATE-M5,
 * recording:RECORD-SESSION, submission:GATE-M6, each {kind,gate,status:"pending"}.
 * Whole PERF-04, AUTO-01/02, AC-21/23/24, recording/submission/complete-build
 * cases stay pending; local component soak never closes those whole cases.
 * result.json: {package:"SOAK",status:"PASS",synthetic:false,started_utc,
 * ended_utc,active_hours,commit,tree_hash,soak_sha256,gate_seconds,
 * resource_groups:["isolated-csharp","i7-editor","standalone-player-display","capture"]}.
 * Hash the finished soak.json last. Generic gate command on devnode:
 *   flock "$HOME/.cache/tmp/blockparty-devnode-unity.lock" env
 *   BLOCKPARTY_UNITY=<Unity 6000.3.10f1 root> python3 tools/verify.py
 *   --package SOAK --seit docs/plans/issue-3-specification/seit.json
 * It invokes this suite via run-standalone.py. Run serially on the i7; the
 * package runner reports actual gate timing and resource groups below.
 *
 * Oracle -> suite_checks -> LOCAL SEIT support (not whole-case acceptance):
 * S-1 Evidence/Binding/Ref/Result -> schema/build/save/control/commit/tree, OD-9;
 * S-2 Duration/Streams/Serial -> span, <=5s snapshots/input, OD-18, PERF-04-P1/P2;
 * S-3 Transitions/Commands -> prepared real events, AUTO-01-P1, AUTO-02-P1;
 * S-4 Recovery/Blocked/Duplicates/Counts -> PERF-04-P1/N1/N2/N3;
 * S-5 Video -> duration/container/hash/PTS, PERF-04-P1/P2;
 * S-6 Dispositions/Obligations -> all 606 inventory, AC-21/23/24 separation.
 * negative-fixture creates only explicitly SYNTHETIC controls, each in a fresh
 * GUID temp git root, validates the positive control, changes one defect,
 * requires that exact oracle rejection and removes the root on EVERY path.
 * These are parser/contract tests, not fabricated gameplay or SEIT passes.
 * Resource: isolated-csharp + owned temp files/git, no engine/display/network.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class SoakChecks
{
    private const string Record = "evidence/production/soak.json", Package = "evidence/production/SOAK/";
    private const string Seit = "docs/plans/issue-3-specification/seit.json";
    private static readonly string[] Events = { "storm", "birth", "estate", "care", "scarcity", "reload", "kill-during-save", "growth", "relocation", "recovery", "travel", "speed", "player-intervention" };
    private static readonly string[] Groups = { "isolated-csharp", "i7-editor", "standalone-player-display", "capture" };
    private sealed class Rejection : Exception { public readonly string Id; public Rejection(string id) { Id = id; } }
    private static void Need(bool yes, string id) { if (!yes) throw new Rejection(id); }
    private static string T(JsonElement e, string k) { return e.GetProperty(k).GetString(); }
    private static double N(JsonElement e, string k) { double n = e.GetProperty(k).GetDouble(); Need(double.IsFinite(n) && n >= 0, "S-1.Number"); return n; }
    private static JsonElement Node(object o) { return Parse(JsonSerializer.Serialize(o)); }
    private static JsonElement Parse(string s) { using (var d = JsonDocument.Parse(s)) return d.RootElement.Clone(); }
    private static JsonElement Read(string p) { return Parse(File.ReadAllText(p)); }
    private static string Hash(string p) { using (var h = SHA256.Create()) using (var s = File.OpenRead(p)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    private static bool Hex(string s, int n) { if (s == null || s.Length != n) return false; foreach (char c in s) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
    private static void Eq(double a, double b, string id) { Need(Math.Abs(a - b) <= .000001, id); }
    private static DateTimeOffset Utc(JsonElement e, string k)
    {
        string t = T(e, k);
        Need(t.EndsWith("Z", StringComparison.Ordinal) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "S-1.Utc");
        return DateTimeOffset.Parse(t, CultureInfo.InvariantCulture);
    }
    private static string Git(string root, params string[] args)
    {
        var si = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) si.ArgumentList.Add(arg);
        using (var p = Process.Start(si))
        {
            p.StandardInput.Close(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { p.Kill(); throw new Rejection("S-1.GitTimeout"); }
            stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
            Need(p.ExitCode == 0, "S-1.CommitAncestry"); return stdout.Result.TrimEnd('\r', '\n');
        }
    }
    private static string Ref(string root, JsonElement r)
    {
        string path = T(r, "path"), hash = T(r, "sha256"), full = root;
        Need(path.StartsWith(Package, StringComparison.Ordinal) && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0, "S-1.RefScope");
        foreach (string part in path.Split('/'))
        {
            Need(part != "" && part != "." && part != "..", "S-1.RefScope"); full = Path.Combine(full, part);
            Need(File.Exists(full) || Directory.Exists(full), "S-1.RefPresent");
            Need((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "S-1.RefScope");
        }
        Need(File.Exists(full) && Hex(hash, 64) && Hash(full) == hash, "S-1.RefHash"); return full;
    }
    private static void Binding(JsonElement row, JsonElement record, JsonElement session)
    {
        Need(!row.TryGetProperty("synthetic", out var marker) || marker.GetBoolean() == record.GetProperty("synthetic").GetBoolean(), "S-1.SyntheticProvenance");
        Need(T(row, "session_id") == T(session, "id") && T(row, "build_commit") == T(record, "commit")
            && T(row, "tree_hash") == T(record, "tree_hash") && T(row, "archive_sha256") == T(record.GetProperty("build"), "archive_sha256")
            && T(row, "start_save_sha256") == T(session.GetProperty("start_save"), "sha256")
            && row.GetProperty("seed").GetInt64() == session.GetProperty("seed").GetInt64()
            && T(row, "control") == T(session, "control"), "S-1.Provenance");
    }
    private static List<JsonElement> Rows(string root, JsonElement r, JsonElement record, JsonElement session, string id, double maxGap, out double gap)
    {
        var rows = new List<JsonElement>(); DateTimeOffset start = Utc(session, "started_utc"), end = Utc(session, "ended_utc"), last = start;
        gap = 0;
        foreach (string line in File.ReadLines(Ref(root, r)))
        {
            Need(!string.IsNullOrWhiteSpace(line), id); var row = Parse(line); Binding(row, record, session); var time = Utc(row, "utc");
            Need(time >= start && time <= end && (rows.Count == 0 || time >= last), id);
            gap = Math.Max(gap, (time - last).TotalSeconds); last = time; rows.Add(row);
        }
        Need(rows.Count >= 2 && Utc(rows[0], "utc") == start && last == end && gap <= maxGap, id);
        return rows;
    }
    private sealed class Resident { public bool Living, Blocked, Reeval, Progress; }
    private static Dictionary<string, bool> Residents(JsonElement snapshot)
    {
        var result = new Dictionary<string, bool>();
        foreach (var r in snapshot.GetProperty("residents").EnumerateArray())
        { string id = T(r, "id"); Need(!string.IsNullOrWhiteSpace(id) && !result.ContainsKey(id), "S-4.ResidentInventory"); result.Add(id, r.GetProperty("living").GetBoolean()); }
        Need(result.Count > 0, "S-4.ResidentInventory"); return result;
    }
    private static JsonElement SavePayload(string root, JsonElement validated)
    {
        var payload = Read(Ref(root, validated.GetProperty("save"))).GetProperty("payload");
        Eq(N(payload.GetProperty("world"), "gameSeconds"), N(validated, "game_time"), "S-4.SaveGameTime");
        return payload;
    }
    private static Dictionary<string, JsonElement> SavedResidents(JsonElement payload)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var r in payload.GetProperty("residents").EnumerateArray())
        {
            string id = r.GetProperty("id").ToString();
            Need(!string.IsNullOrWhiteSpace(id) && !result.ContainsKey(id), "S-4.ResidentInventory"); result.Add(id, r);
        }
        return result;
    }
    private static void Arrival(string root, List<JsonElement> saves, JsonElement row)
    {
        double time = N(row, "game_time"); JsonElement before = default, after = default;
        foreach (var save in saves)
        {
            double t = N(save, "game_time");
            if (t <= time && (before.ValueKind == JsonValueKind.Undefined || t >= N(before, "game_time"))) before = save;
            if (t > time && (after.ValueKind == JsonValueKind.Undefined || t < N(after, "game_time"))) after = save;
        }
        Need(before.ValueKind != JsonValueKind.Undefined && after.ValueKind != JsonValueKind.Undefined, "S-4.ArrivalSaveProof");
        foreach (string name in new[] { "before_save", "after_save" }) if (row.TryGetProperty(name, out var named))
        {
            Ref(root, named); var expected = (name == "before_save" ? before : after).GetProperty("save");
            Need(T(named, "path") == T(expected, "path") && T(named, "sha256") == T(expected, "sha256"), "S-4.ArrivalSavePair");
        }
        var old = SavedResidents(SavePayload(root, before)); var current = SavedResidents(SavePayload(root, after));
        string id = T(row, "resident");
        Need(!old.ContainsKey(id) && current.TryGetValue(id, out _), "S-4.ArrivalSaveProof");
        var resident = current[id];
        Need(resident.GetProperty("alive").GetBoolean() && N(resident, "home") > 0 && N(resident, "household") > 0
            && current.Count > old.Count, "S-4.ArrivalSaveProof");
        Need(T(row, "reason") == "vacancy-arrival" && T(row, "outcome") == "Completed" && T(row, "command_id") == "", "S-4.ArrivalSaveProof");
    }
    private static void Birth(string root, List<JsonElement> saves, JsonElement row)
    {
        JsonElement first = default;
        foreach (var save in saves)
            if (N(save, "game_time") >= N(row, "game_time") && (first.ValueKind == JsonValueKind.Undefined || N(save, "game_time") < N(first, "game_time"))) first = save;
        Need(first.ValueKind != JsonValueKind.Undefined, "S-4.BirthGameEvent");
        foreach (var ev in SavePayload(root, first).GetProperty("events").EnumerateArray())
            if (T(ev, "kind") == "Birth" && T(ev, "detail") == "child " + T(row, "resident") && N(ev, "gameSeconds") <= N(row, "game_time")) return;
        throw new Rejection("S-4.BirthGameEvent");
    }
    private static void Video(string root, JsonElement record, JsonElement session, double span)
    {
        var v = session.GetProperty("video"); var probe = Read(Ref(root, v.GetProperty("ffprobe"))); var capture = Read(Ref(root, v.GetProperty("capture")));
        Binding(capture, record, session); Need(Utc(capture, "utc") >= Utc(session, "ended_utc"), "S-5.CaptureTime");
        string container = T(v, "container");
        Need(container == "mp4" || container == "matroska", "S-5.Container");
        Need(Hex(T(v, "sha256"), 64) && N(v, "bytes") > 0 && v.GetProperty("bytes").TryGetInt64(out _)
            && T(capture, "sha256") == T(v, "sha256") && N(capture, "bytes") == N(v, "bytes"), "S-5.CaptureHash");
        Need(T(capture, "container") == container && capture.GetProperty("uncut").GetBoolean()
            && N(capture, "exit_code") == 0 && !string.IsNullOrWhiteSpace(T(capture, "storage")), "S-5.Uncut");
        double duration = N(v, "duration_s");
        Need(Math.Abs(duration - span) <= 2 && duration >= N(session, "target_s"), "S-5.Duration"); Eq(N(capture, "duration_s"), duration, "S-5.Duration");
        var format = probe.GetProperty("format"); string fmt = T(format, "format_name");
        Need(container == "mp4" ? Array.IndexOf(fmt.Split(','), "mp4") >= 0 : Array.IndexOf(fmt.Split(','), "matroska") >= 0, "S-5.Container");
        Eq(double.Parse(T(format, "size"), CultureInfo.InvariantCulture), N(v, "bytes"), "S-5.CaptureHash");
        Need(Math.Abs(double.Parse(T(format, "duration"), CultureInfo.InvariantCulture) - duration) <= .000001, "S-5.Duration");
        double fps = 0; int videoStreams = 0;
        foreach (var stream in probe.GetProperty("streams").EnumerateArray()) if (T(stream, "codec_type") == "video")
        {
            string[] rate = T(stream, "avg_frame_rate").Split('/'); Need(rate.Length == 2, "S-5.FrameRate");
            fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture); videoStreams++;
        }
        Need(videoStreams == 1 && double.IsFinite(fps) && fps > 0, "S-5.FrameRate"); double limit = 2 / fps;
        using (var reader = new StreamReader(Ref(root, v.GetProperty("tasks"))))
        {
            Need(reader.ReadLine() == "pts_s", "S-5.Tasks"); string line; double first = -1, last = -1; int count = 0;
            while ((line = reader.ReadLine()) != null)
            {
                double t = double.Parse(line, CultureInfo.InvariantCulture);
                Need(double.IsFinite(t) && t >= 0 && t <= duration && (count == 0 || (t > last && t - last <= limit + .000001)), "S-5.Continuity");
                if (count++ == 0) first = t; last = t;
            }
            Need(count >= 2 && first <= limit && duration - last <= limit + .000001, "S-5.Continuity");
        }
    }
    private static void Session(string root, JsonElement record, JsonElement s)
    {
        Need(T(s, "control") == "automated" || T(s, "control") == "mixed", "S-1.Control"); Ref(root, s.GetProperty("start_save"));
        double span = (Utc(s, "ended_utc") - Utc(s, "started_utc")).TotalSeconds;
        Need(span >= N(s, "target_s"), "S-2.Duration");
        var log = Rows(root, s.GetProperty("log"), record, s, "S-2.LogSpan", double.MaxValue, out _);
        var input = Rows(root, s.GetProperty("input"), record, s, "S-2.InputContinuity", 5, out double inputGap);
        var snapshots = Rows(root, s.GetProperty("snapshots"), record, s, "S-2.SnapshotContinuity", 5, out double snapshotGap);
        var saves = log.FindAll(row => T(row, "event") == "save-validated");
        var inputs = new HashSet<string>(); bool human = false;
        foreach (var row in input)
        {
            string control = T(row, "controller"); Need(control == "human" || control == "automated", "S-3.InputControl");
            if (control == "human") human = true;
            Need(T(s, "control") == "mixed" || control == "automated", "S-3.InputControl");
            T(row, "outcome");
            string cmd = T(row, "command_id"); if (cmd != "") Need(inputs.Add(cmd), "S-3.InputCommandIdentity");
        }
        Need(T(s, "control") != "mixed" || human, "S-3.InputControl");
        var initial = Residents(snapshots[0]); Need(initial.Count == 8 && !initial.ContainsValue(false), "S-4.EightResidentStart");
        var residents = new Dictionary<string, Resident>(); foreach (var pair in initial) residents.Add(pair.Key, new Resident { Living = pair.Value });
        var preparedRef = s.GetProperty("prepared"); var prepared = Read(Ref(root, preparedRef));
        Need(Git(root, "show", T(record, "commit") + ":" + T(preparedRef, "path")) == File.ReadAllText(Ref(root, preparedRef)).TrimEnd('\r', '\n'), "S-3.PreparedAtBuild");
        var transitions = new Dictionary<string, string>(); var observed = new HashSet<string>();
        foreach (var t in prepared.GetProperty("transitions").EnumerateArray())
        { string id = T(t, "id"), ev = T(t, "event"); Need(!string.IsNullOrWhiteSpace(id) && !transitions.ContainsKey(id) && Array.IndexOf(Events, ev) >= 0, "S-3.Prepared"); transitions.Add(id, ev); }
        var counts = new Dictionary<string, int>(); foreach (string e in Events) counts.Add(e, 0);
        int crash = 0, lost = 0, duplicates = 0; var effects = new HashSet<string>(); var commands = new HashSet<string>();
        long validated = -1, inflight = -1; string validatedHash = null; bool killed = false; DateTimeOffset? recovered = null;
        foreach (var row in log)
        {
            N(row, "game_time");
            string ev = T(row, "event"), resident = T(row, "resident"), cmd = T(row, "command_id"); T(row, "outcome");
            if (counts.ContainsKey(ev))
            {
                counts[ev]++; Need(row.TryGetProperty("transition_id", out _), "S-3.PreparedTransition"); var tid = row.GetProperty("transition_id");
                Need(T(row, "outcome") == "Completed" && transitions.TryGetValue(tid.GetString(), out var expected) && expected == ev, "S-3.PreparedTransition");
                observed.Add(tid.GetString());
            }
            if (ev == "direct-state-edit" || ev == "invented-reward") throw new Rejection("S-3.OrdinaryCommands");
            if (ev == "command-applied")
            {
                Need(cmd != "" && inputs.Contains(cmd) && T(row, "outcome") == "Applied", "S-3.OrdinaryCommands");
                if (!commands.Add(cmd)) duplicates++;
            }
            if (ev == "birth" || ev == "estate")
            { string effect = T(row, "effect_id"); Need(!string.IsNullOrWhiteSpace(effect), "S-4.EffectIdentity"); if (!effects.Add(effect)) duplicates++; }
            if (ev == "birth")
            {
                Birth(root, saves, row);
                if (!residents.ContainsKey(resident)) residents.Add(resident, new Resident { Living = true });
            }
            if (ev == "arrival")
            {
                Arrival(root, saves, row); Need(!residents.ContainsKey(resident), "S-4.ArrivalSaveProof");
                residents.Add(resident, new Resident { Living = true });
            }
            if (resident != "")
            {
                Need(residents.ContainsKey(resident), "S-4.ResidentInventory"); var r = residents[resident];
                if (ev == "block-onset") { Need(!string.IsNullOrWhiteSpace(T(row, "reason")), "S-4.BlockReason"); r.Blocked = true; r.Reeval = false; r.Progress = false; }
                if (ev == "reevaluate" && r.Blocked) r.Reeval = true;
                if ((ev == "action-completed" || ev == "accepted-command-progress") && r.Blocked) r.Progress = true;
                if (ev == "death") r.Living = false;
            }
            if (ev == "crash") crash++;
            if (ev == "save-validated")
            {
                Need(!killed, "S-4.KillRecovery"); long g = row.GetProperty("generation").GetInt64(); Need(g > validated, "S-4.SaveGeneration");
                validated = g; validatedHash = Hash(Ref(root, row.GetProperty("save")));
            }
            if (ev == "kill-during-save")
            {
                Need(!killed && validated >= 0, "S-4.KillRecovery"); inflight = row.GetProperty("inflight_generation").GetInt64();
                Need(inflight == validated + 1, "S-4.KillRecovery"); killed = true;
            }
            if (ev == "reload")
            {
                string saved = Hash(Ref(root, row.GetProperty("saved_state"))), loaded = Hash(Ref(root, row.GetProperty("loaded_state")));
                if (saved != loaded) lost++;
                if (killed)
                {
                    long generation = row.GetProperty("generation").GetInt64();
                    if (saved != validatedHash || generation != validated || inflight - generation > 1) lost++;
                    killed = false; recovered = Utc(row, "utc");
                }
            }
        }
        Need(commands.Count > 0, "S-3.OrdinaryCommands");
        foreach (string ev in Events) Need(counts[ev] > 0 && transitions.ContainsValue(ev), "S-3.MissingTransition." + ev);
        Need(observed.Count == transitions.Count, "S-3.PreparedCoverage");
        Need(!killed && recovered.HasValue && (Utc(s, "ended_utc") - recovered.Value).TotalSeconds >= 600, "S-4.KillRecovery");
        var final = Residents(snapshots[snapshots.Count - 1]); Need(final.Count == residents.Count, "S-4.ResidentInventory"); int blocked = 0;
        foreach (var pair in residents)
        {
            Need(final.TryGetValue(pair.Key, out bool living) && living == pair.Value.Living, "S-4.ResidentInventory"); var r = pair.Value;
            if (r.Living && r.Blocked && r.Reeval && !r.Progress) blocked++;
        }
        Need(crash == 0, "S-4.Crash"); Need(lost == 0, "S-4.LostSave"); Need(blocked == 0, "S-4.PermanentBlockage"); Need(duplicates == 0, "S-4.DuplicateEffects");
        var c = s.GetProperty("counts"); Eq(N(c, "span_s"), span, "S-4.Counts"); Eq(N(c, "snapshot_gap_s"), snapshotGap, "S-4.Counts"); Eq(N(c, "input_gap_s"), inputGap, "S-4.Counts");
        Eq(N(c, "crash"), crash, "S-4.Counts"); Eq(N(c, "lost_save"), lost, "S-4.Counts"); Eq(N(c, "blocked"), blocked, "S-4.Counts"); Eq(N(c, "duplicates"), duplicates, "S-4.Counts");
        foreach (var pair in counts) Eq(N(c.GetProperty("events"), pair.Key), pair.Value, "S-4.Counts");
        Video(root, record, s, span);
    }
    private static void Dispositions(string root, JsonElement record)
    {
        var ids = new HashSet<string>(); var pending = new HashSet<string>();
        var seit = Parse(Git(root, "show", T(record, "commit") + ":" + Seit));
        foreach (var c in seit.GetProperty("proof_cases").EnumerateArray()) Need(ids.Add(T(c, "id")), "S-6.SeitInventory");
        Need(ids.Count == 606, "S-6.SeitInventory");
        var plan = Parse(Git(root, "show", T(record, "commit") + ":docs/plans/full-game-rev2/plan.json"));
        foreach (var task in plan.GetProperty("tasks").EnumerateArray())
            if (Array.IndexOf(new[] { "SOAK", "PACKAGE", "GATE-M5", "GATE-M6", "RECORD-SESSION", "RECORD-EDIT" }, T(task, "id")) >= 0)
                foreach (var id in task.GetProperty("verification").GetProperty("traceability_case_ids").EnumerateArray()) pending.Add(id.GetString());
        var seen = new HashSet<string>(); var data = Read(Ref(root, record.GetProperty("dispositions")));
        foreach (var row in data.GetProperty("cases").EnumerateArray())
        {
            string id = T(row, "id"), disposition = T(row, "disposition"); Need(ids.Contains(id) && seen.Add(id), "S-6.ExactlyOnce");
            if (pending.Contains(id)) Need(disposition.StartsWith("pending:", StringComparison.Ordinal), "S-6.LaterGatePending");
            if (Array.IndexOf(new[] { "SEIT-SPEC-01", "SEIT-SPEC-02", "SEIT-SPEC-06", "SEIT-SPEC-08" }, id) >= 0)
                Need(disposition != "component-pass", "S-6.SpecSeparation");
            if (disposition == "component-pass")
            {
                bool pass = false;
                foreach (string line in File.ReadLines(Ref(root, row.GetProperty("log"))))
                {
                    if (line == "PASS " + id) pass = true;
                    if (line.Contains(id, StringComparison.Ordinal)) Need(!(line.Contains("SKIP", StringComparison.OrdinalIgnoreCase)
                        || line.Replace('_', ' ').Replace('-', ' ').Contains("NOT RUN", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("UNRUN", StringComparison.OrdinalIgnoreCase) || line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)), "S-6.UnrunNotPass");
                }
                Need(pass, "S-6.ExactPassLine");
            }
            else if (disposition == "spec-evidence")
            {
                Need(Array.IndexOf(new[] { "SEIT-SPEC-01", "SEIT-SPEC-02", "SEIT-SPEC-06", "SEIT-SPEC-08" }, id) >= 0, "S-6.SpecSeparation");
                Need(new FileInfo(Ref(root, row.GetProperty("log"))).Length > 0, "S-6.SpecEvidence");
            }
            else if (disposition.StartsWith("pending:", StringComparison.Ordinal))
                Need(Array.IndexOf(new[] { "SOAK", "PACKAGE", "PERFORMANCE", "HUMAN-PLAY", "LIVE-AI", "RECORD-SESSION", "RECORD-EDIT", "GATE-M5", "GATE-M6", "OWNER" }, disposition.Substring(8)) >= 0, "S-6.PendingGate");
            else Need(disposition == "component-fail" || disposition == "unrun", "S-6.Disposition");
        }
        Need(seen.SetEquals(ids), "S-6.Complete606");
        var obligations = new Dictionary<string, string> { { "complete-build", "GATE-M5" }, { "soak", "GATE-M5" }, { "recording", "RECORD-SESSION" }, { "submission", "GATE-M6" } };
        var covered = new HashSet<string>();
        foreach (var o in record.GetProperty("obligations").EnumerateArray())
            Need(obligations.TryGetValue(T(o, "kind"), out string gate) && covered.Add(T(o, "kind")) && T(o, "gate") == gate && T(o, "status") == "pending", "S-6.ObligationsPending");
        Need(covered.Count == obligations.Count, "S-6.ObligationsPending");
    }
    private static void Validate(string root, bool synthetic = false)
    {
        Need(File.Exists(Path.Combine(root, Record)), "S-1.MissingEvidence: evidence/production/soak.json NOT RUN");
        var record = Read(Path.Combine(root, Record));
        Need(T(record, "schema") == "blockparty-soak/1" && record.GetProperty("synthetic").GetBoolean() == synthetic, "S-1.SchemaSynthetic");
        string commit = T(record, "commit"), tree = T(record, "tree_hash");
        Need(Hex(commit, 40) && Hex(tree, 40), "S-1.Identity");
        Need(Git(root, "rev-parse", commit + "^{tree}") == tree, "S-1.Tree");
        string ancestor = T(record, "gate_m4_commit"); Need(Hex(ancestor, 40), "S-1.Identity");
        Git(root, "merge-base", "--is-ancestor", ancestor, commit); Git(root, "merge-base", "--is-ancestor", ancestor, "origin/main");
        const string receipt = "evidence/milestones/M4.json";
        Need(T(Parse(Git(root, "show", ancestor + ":" + receipt)), "status") == "PASS"
            && T(Parse(Git(root, "show", commit + ":" + receipt)), "status") == "PASS", "S-1.DependencyReceipt");
        var build = record.GetProperty("build");
        Need(Hex(T(build, "archive_sha256"), 64) && T(build, "player") == "standalone" && T(build, "platform") == "StandaloneLinux64"
            && !build.GetProperty("development").GetBoolean(), "S-1.StandaloneBuild");
        var sessions = record.GetProperty("sessions"); Need(sessions.GetArrayLength() == 1, "S-2.OneSession");
        var names = new HashSet<string>();
        foreach (var s in sessions.EnumerateArray())
        {
            Need(!string.IsNullOrWhiteSpace(T(s, "id")) && names.Add(T(s, "id")), "S-2.SessionIdentity");
            Need(N(s, "target_s") == 3600, "S-2.TargetsOD18");
            foreach (var other in sessions.EnumerateArray()) if (T(s, "id") != T(other, "id"))
                Need(Utc(s, "ended_utc") <= Utc(other, "started_utc") || Utc(other, "ended_utc") <= Utc(s, "started_utc"), "S-2.NoConcurrentPair");
        }
        foreach (var s in sessions.EnumerateArray()) Session(root, record, s);
        Dispositions(root, record);
        Need(File.Exists(Path.Combine(root, Package, "result.json")), "S-1.MissingResult"); var result = Read(Path.Combine(root, Package, "result.json"));
        Need(T(result, "package") == "SOAK" && T(result, "status") == "PASS" && result.GetProperty("synthetic").GetBoolean() == synthetic, "S-1.Result");
        Need(T(result, "commit") == commit && T(result, "tree_hash") == tree && T(result, "soak_sha256") == Hash(Path.Combine(root, Record)), "S-1.ResultBinding");
        Need(Utc(result, "ended_utc") >= Utc(result, "started_utc"), "S-1.ResultTime"); N(result, "active_hours"); N(result, "gate_seconds");
        var groups = new HashSet<string>(); foreach (var g in result.GetProperty("resource_groups").EnumerateArray()) Need(groups.Add(g.GetString()), "S-1.ResourceGroups");
        Need(groups.SetEquals(Groups), "S-1.ResourceGroups");
    }
    private static void Write(string root, string path, object data)
    {
        string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, JsonSerializer.Serialize(data));
    }
    private static object Reference(string root, string path) { return new { path, sha256 = Hash(Path.Combine(root, path)) }; }
    private static void Edit(string root, string path, Action<Dictionary<string, JsonElement>> edit)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(root, path))); edit(d); Write(root, path, d);
    }
    private static string Stamp(double seconds) { return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
    private static List<Dictionary<string, object>> FixtureLog(double offset, double span)
    {
        var rows = new List<Dictionary<string, object>>();
        void Add(double t, string ev, string resident = "", string cmd = "", Dictionary<string, object> extra = null)
        {
            var r = new Dictionary<string, object> { { "utc", Stamp(offset + t) }, { "game_time", t }, { "event", ev }, { "resident", resident }, { "command_id", cmd }, { "outcome", ev == "command-applied" ? "Applied" : "Completed" } };
            if (extra != null) foreach (var p in extra) r[p.Key] = p.Value; rows.Add(r);
        }
        Add(0, "session-start");
        Add(1, "save-validated", extra: new Dictionary<string, object> { { "generation", 10 } });
        Add(80, "kill-during-save", extra: new Dictionary<string, object> { { "transition_id", "t-kill-during-save" }, { "inflight_generation", 13 } });
        Add(90, "reload", extra: new Dictionary<string, object> { { "transition_id", "t-reload" }, { "generation", 12 } });
        for (int r = 9; r <= 16; r++)
            Add(25, "arrival", r.ToString(CultureInfo.InvariantCulture), extra: new Dictionary<string, object> { { "reason", "vacancy-arrival" } });
        Add(30, "save-validated", extra: new Dictionary<string, object> { { "generation", 11 } });
        Add(40, "save-validated", extra: new Dictionary<string, object> { { "generation", 12 } });
        int n = 0;
        foreach (string ev in Events) if (ev != "kill-during-save" && ev != "reload")
        {
            var extra = new Dictionary<string, object> { { "transition_id", "t-" + ev } }; if (ev == "birth" || ev == "estate") extra.Add("effect_id", "SYNTHETIC-" + ev);
            Add(30 + n++ * 5, ev, ev == "birth" ? "18" : "", extra: extra);
        }
        Add(100, "command-applied", "1", "cmd-100");
        foreach (string resident in new[] { "1", "2", "3" })
        {
            Add(110, "block-onset", resident, extra: new Dictionary<string, object> { { "reason", "SYNTHETIC-path-unavailable" } });
            Add(115, "reevaluate", resident);
        }
        Add(120, "action-completed", "1"); Add(120, "accepted-command-progress", "2"); Add(120, "death", "3");
        Add(span, "session-end"); rows.Sort((a, b) => ((double)a["game_time"]).CompareTo((double)b["game_time"])); return rows;
    }
    private static void Fixture(string root, double span = 3600)
    {
        // Synthetic contract corpus only; the authoritative real SEIT inventory is copied, never invented.
        Git(root, "init", "-q"); Git(root, "config", "user.name", "Synthetic SOAK Fixture"); Git(root, "config", "user.email", "synthetic@example.invalid");
        Write(root, "evidence/milestones/M4.json", new { status = "PASS", synthetic = true });
        Git(root, "add", "evidence/milestones/M4.json"); Git(root, "commit", "-qm", "SYNTHETIC gate fixture"); string gate = Git(root, "rev-parse", "HEAD");
        foreach (string file in new[] { Seit, "docs/plans/full-game-rev2/plan.json" })
        { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))); File.Copy(Path.Combine(Directory.GetCurrentDirectory(), file), Path.Combine(root, file)); }
        var ts = new List<object>(); foreach (string ev in Events) ts.Add(new { id = "t-" + ev, @event = ev });
        Write(root, Package + "prepared.json", new { synthetic = true, transitions = ts });
        Git(root, "add", Seit, "docs/plans/full-game-rev2/plan.json", Package + "prepared.json"); Git(root, "commit", "-qm", "SYNTHETIC prepared source");
        string commit = Git(root, "rev-parse", "HEAD"), tree = Git(root, "rev-parse", "HEAD^{tree}"); Git(root, "update-ref", "refs/remotes/origin/main", commit);
        var archive = new string('a', 64); var sessions = new List<object>();
        for (int i = 0; i < 1; i++)
        {
            string run = Package + "synthetic-" + i + "/", id = "SYNTHETIC-" + i; double offset = 0;
            foreach (string name in new[] { "before", "arrivals", "birth" })
            {
                string path = run + (name == "before" ? "save" : "save-" + name) + ".json";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path)));
                File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "tests/production/fixtures/SOAK/save-" + name + ".json"), Path.Combine(root, path));
            }
            var save = Reference(root, run + "save.json"); string saveHash = Hash(Path.Combine(root, run + "save.json"));
            var provenance = new Dictionary<string, object> { { "session_id", id }, { "build_commit", commit }, { "tree_hash", tree }, { "archive_sha256", archive }, { "start_save_sha256", saveHash }, { "seed", 42 }, { "control", "automated" } };
            void RowsOut(string name, List<Dictionary<string, object>> rows)
            {
                var text = new StringBuilder(); foreach (var row in rows) { foreach (var p in provenance) row[p.Key] = p.Value; row["synthetic"] = true; text.AppendLine(JsonSerializer.Serialize(row)); }
                File.WriteAllText(Path.Combine(root, run + name), text.ToString());
            }
            var logs = FixtureLog(offset, span);
            foreach (var row in logs)
            {
                if ((string)row["event"] == "save-validated")
                    row["save"] = (int)row["generation"] == 10 ? save : Reference(root, run + ((int)row["generation"] == 11 ? "save-arrivals.json" : "save-birth.json"));
                if ((string)row["event"] == "reload") { row["saved_state"] = Reference(root, run + "save-birth.json"); row["loaded_state"] = row["saved_state"]; }
            }
            var namedArrival = logs.Find(r => (string)r["event"] == "arrival" && (string)r["resident"] == "10");
            namedArrival["before_save"] = save; namedArrival["after_save"] = Reference(root, run + "save-arrivals.json");
            RowsOut("log.jsonl", logs);
            var inputs = new List<Dictionary<string, object>>(); var snapshots = new List<Dictionary<string, object>>();
            for (double t = 0; t <= span; t += 5)
            {
                inputs.Add(new Dictionary<string, object> { { "utc", Stamp(offset + t) }, { "controller", "automated" }, { "command_id", t == 100 ? "cmd-100" : "" }, { "outcome", t == 100 ? "Applied" : "Idle" } });
                var residents = new List<object>();
                for (int r = 1; r <= (t >= 25 ? 16 : 8); r++) residents.Add(new { id = r.ToString(CultureInfo.InvariantCulture), living = r != 3 || t < 120 });
                if (t >= 35) residents.Add(new { id = "18", living = true });
                snapshots.Add(new Dictionary<string, object> { { "utc", Stamp(offset + t) }, { "residents", residents } });
            }
            RowsOut("input.jsonl", inputs); RowsOut("snapshots.jsonl", snapshots);
            var capture = new Dictionary<string, object>(provenance) { { "utc", Stamp(offset + span + 1) }, { "synthetic", true }, { "container", "mp4" }, { "sha256", new string('b', 64) }, { "bytes", 123456 }, { "duration_s", span }, { "storage", "SYNTHETIC-no-video" }, { "uncut", true }, { "exit_code", 0 } };
            Write(root, run + "capture.json", capture);
            Write(root, run + "ffprobe.json", new { synthetic = true, format = new { format_name = "mov,mp4,m4a,3gp,3g2,mj2", duration = span.ToString(CultureInfo.InvariantCulture), size = "123456" }, streams = new[] { new { codec_type = "video", avg_frame_rate = "1/1" } } });
            var tasks = new StringBuilder("pts_s\n"); for (int t = 0; t <= span; t++) tasks.AppendLine(t.ToString(CultureInfo.InvariantCulture)); File.WriteAllText(Path.Combine(root, run + "tasks.csv"), tasks.ToString());
            var eventCounts = new Dictionary<string, int>(); foreach (string ev in Events) eventCounts.Add(ev, 1);
            sessions.Add(new { id, target_s = span, started_utc = Stamp(offset), ended_utc = Stamp(offset + span), start_save = save, seed = 42, control = "automated",
                log = Reference(root, run + "log.jsonl"), input = Reference(root, run + "input.jsonl"), snapshots = Reference(root, run + "snapshots.jsonl"), prepared = Reference(root, Package + "prepared.json"),
                video = new { container = "mp4", sha256 = new string('b', 64), bytes = 123456, duration_s = span, ffprobe = Reference(root, run + "ffprobe.json"), tasks = Reference(root, run + "tasks.csv"), capture = Reference(root, run + "capture.json") },
                counts = new { span_s = span, snapshot_gap_s = 5, input_gap_s = 5, crash = 0, lost_save = 0, blocked = 0, duplicates = 0, events = eventCounts } });
        }
        var cases = new List<object>(); foreach (var c in Read(Path.Combine(root, Seit)).GetProperty("proof_cases").EnumerateArray()) cases.Add(new { id = T(c, "id"), disposition = "pending:GATE-M5" });
        Write(root, Package + "dispositions.json", new { synthetic = true, cases });
        var obligations = new List<object>(); foreach (var p in new Dictionary<string, string> { { "complete-build", "GATE-M5" }, { "soak", "GATE-M5" }, { "recording", "RECORD-SESSION" }, { "submission", "GATE-M6" } }) obligations.Add(new { kind = p.Key, gate = p.Value, status = "pending" });
        Write(root, Record, new { schema = "blockparty-soak/1", synthetic = true, commit, tree_hash = tree, gate_m4_commit = gate,
            build = new { archive_sha256 = archive, player = "standalone", platform = "StandaloneLinux64", development = false }, dispositions = Reference(root, Package + "dispositions.json"), sessions, obligations });
        Write(root, Package + "result.json", new { package = "SOAK", status = "PASS", synthetic = true, started_utc = Stamp(0), ended_utc = Stamp(11000), active_hours = 0, commit, tree_hash = tree, soak_sha256 = Hash(Path.Combine(root, Record)), gate_seconds = 0, resource_groups = Groups });
    }
    private static void EditSession(string root, Action<Dictionary<string, JsonElement>> edit)
    {
        Edit(root, Record, d => {
            var sessions = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(d["sessions"].GetRawText());
            edit(sessions[0]); d["sessions"] = Node(sessions);
        });
    }
    private static void EditRows(string root, string kind, Action<List<Dictionary<string, JsonElement>>> edit)
    {
        string path = Package + "synthetic-0/" + kind + ".jsonl"; var rows = new List<Dictionary<string, JsonElement>>();
        foreach (string line in File.ReadLines(Path.Combine(root, path))) rows.Add(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line));
        edit(rows); var text = new StringBuilder(); foreach (var row in rows) text.AppendLine(JsonSerializer.Serialize(row)); File.WriteAllText(Path.Combine(root, path), text.ToString());
        EditSession(root, s => s[kind] = Node(Reference(root, path)));
    }
    private static void EditVideo(string root, Action<Dictionary<string, JsonElement>> edit)
    {
        EditSession(root, s => { var v = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s["video"].GetRawText()); edit(v); s["video"] = Node(v); });
    }
    private static void EditCases(string root, Action<List<Dictionary<string, JsonElement>>> edit)
    {
        string path = Package + "dispositions.json";
        Edit(root, path, d => { var cases = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(d["cases"].GetRawText()); edit(cases); d["cases"] = Node(cases); });
        Edit(root, Record, d => d["dispositions"] = Node(Reference(root, path)));
    }
    private static int Negatives()
    {
        var cases = new Dictionary<string, string> {
            { "arrival-no-save-proof", "S-4.ArrivalSaveProof" }, { "arrival-existing-resident", "S-4.ArrivalSaveProof" },
            { "arrival-at-after-save", "S-4.ArrivalSaveProof" }, { "arrival-no-home", "S-4.ArrivalSaveProof" },
            { "arrival-no-count-rise", "S-4.ArrivalSaveProof" }, { "arrival-wrong-save-pair", "S-4.ArrivalSavePair" },
            { "arrival-save-hash", "S-1.RefHash" }, { "birth-no-game-event", "S-4.BirthGameEvent" },
            { "short-session", "S-2.Duration" }, { "concurrent-pair", "S-2.OneSession" }, { "single-1800-session", "S-2.TargetsOD18" },
            { "input-cut", "S-2.InputContinuity" }, { "snapshot-gap", "S-2.SnapshotContinuity" }, { "video-cut", "S-5.Continuity" },
            { "video-hash", "S-5.CaptureHash" }, { "video-duration", "S-5.Duration" }, { "video-container", "S-5.Container" }, { "edited-video", "S-5.Uncut" },
            { "duplicate-command", "S-4.DuplicateEffects" }, { "duplicate-birth", "S-4.DuplicateEffects" }, { "duplicate-estate", "S-4.DuplicateEffects" },
            { "permanent-blockage", "S-4.PermanentBlockage" }, { "crash", "S-4.Crash" }, { "lost-save", "S-4.LostSave" }, { "rollback-two-saves", "S-4.LostSave" },
            { "stale-commit", "S-1.CommitAncestry" }, { "stale-tree", "S-1.Tree" }, { "stale-save", "S-1.Provenance" }, { "stale-archive", "S-1.Provenance" },
            { "stale-result", "S-1.ResultBinding" }, { "missing-result", "S-1.MissingResult" },
            { "wrong-counts", "S-4.Counts" }, { "wrong-target", "S-2.TargetsOD18" }, { "missing-session", "S-2.OneSession" },
            { "ordinary-path-bypass", "S-3.OrdinaryCommands" }, { "missing-606-case", "S-6.Complete606" }, { "duplicate-case", "S-6.ExactlyOnce" },
            { "unrun-as-pass", "S-6.ExactPassLine" }, { "skipped-as-pass", "S-6.UnrunNotPass" }, { "not-run-as-pass", "S-6.UnrunNotPass" }, { "explicit-unrun-as-pass", "S-6.UnrunNotPass" }, { "later-gate-as-pass", "S-6.LaterGatePending" },
            { "false-spec-evidence", "S-6.SpecSeparation" }, { "spec-as-component", "S-6.SpecSeparation" }, { "laundered-synthetic", "S-1.SyntheticProvenance" }, { "obligation-as-pass", "S-6.ObligationsPending" }, { "synthetic-as-real", "S-1.SchemaSynthetic" },
            { "ref-escape", "S-1.RefScope" }, { "ref-hash", "S-1.RefHash" }, { "new-block-after-recovery", "S-4.PermanentBlockage" }
        };
        foreach (string ev in Events) cases.Add("missing-" + ev, "S-3.MissingTransition." + ev);
        foreach (var test in cases)
        {
            string root = Path.Combine(Path.GetTempPath(), "blockparty-soak-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root); Fixture(root); Validate(root, true);
                // Positive controls: actual 606 inventory, recovered/dead residents, save recovery.
                // Disposition kinds also have explicit positive controls, not rejection-only tests.
                string logPath = Package + "component.log";
                File.WriteAllText(Path.Combine(root, logPath), "SYNTHETIC parser control\nPASS SEIT-CHR-01-P1\n");
                string local = null;
                foreach (var c in Read(Path.Combine(root, Seit)).GetProperty("proof_cases").EnumerateArray())
                    if (T(c, "id") == "SEIT-CHR-01-P1") local = T(c, "id");
                Need(local != null, "S-Fixture.RealInventoryCase");
                EditCases(root, rows => {
                    foreach (var row in rows)
                    {
                        string id = row["id"].GetString();
                        if (id == local) { row["disposition"] = Node("component-pass"); row["log"] = Node(Reference(root, logPath)); }
                        if (id == "SEIT-SPEC-01") { row["disposition"] = Node("spec-evidence"); row["log"] = Node(Reference(root, logPath)); }
                        if (id == "SEIT-SPEC-03") row["disposition"] = Node("unrun");
                        if (id == "SEIT-SPEC-04") row["disposition"] = Node("component-fail");
                    }
                });
                Edit(root, Package + "result.json", r => r["soak_sha256"] = Node(Hash(Path.Combine(root, Record)))); Validate(root, true);
                if (test.Key.StartsWith("missing-", StringComparison.Ordinal) && Array.IndexOf(Events, test.Key.Substring(8)) >= 0)
                    EditRows(root, "log", rows => rows.RemoveAll(r => r["event"].GetString() == test.Key.Substring(8)));
                else switch (test.Key)
                {
                    case "arrival-no-save-proof":
                        EditRows(root, "log", rows => rows.RemoveAll(r => r["event"].GetString() == "save-validated" && r["generation"].GetInt64() > 10)); break;
                    case "arrival-existing-resident":
                        EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "arrival")["resident"] = Node("1")); break;
                    case "arrival-at-after-save":
                        EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "arrival")["game_time"] = Node(30)); break;
                    case "arrival-no-home": case "arrival-no-count-rise": case "birth-no-game-event":
                        string changedSave = Package + "synthetic-0/" + (test.Key == "birth-no-game-event" ? "save-birth.json" : "save-arrivals.json");
                        Edit(root, changedSave, d => {
                            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(d["payload"].GetRawText());
                            var rs = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(payload["residents"].GetRawText());
                            if (test.Key == "arrival-no-home") rs.Find(r => r["id"].GetInt64() == 9)["home"] = Node(0);
                            if (test.Key == "arrival-no-count-rise") rs.RemoveAll(r => r["id"].GetInt64() >= 10 || r["id"].GetInt64() == 8);
                            payload["residents"] = Node(rs);
                            if (test.Key == "birth-no-game-event") payload["events"] = Node(new object[0]);
                            d["payload"] = Node(payload);
                        });
                        EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "save-validated"
                            && r["generation"].GetInt64() == (test.Key == "birth-no-game-event" ? 12 : 11))["save"] = Node(Reference(root, changedSave))); break;
                    case "arrival-wrong-save-pair":
                        EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "arrival")["after_save"] = Node(Reference(root, Package + "synthetic-0/save-birth.json"))); break;
                    case "arrival-save-hash":
                        File.AppendAllText(Path.Combine(root, Package + "synthetic-0/save-arrivals.json"), " "); break;
                    case "short-session": EditSession(root, s => s["ended_utc"] = Node(Stamp(3599))); break;
                    case "concurrent-pair": Edit(root, Record, r => r["sessions"] = Node(new[] { r["sessions"][0], r["sessions"][0] })); break;
                    case "single-1800-session":
                        Directory.Delete(root, true); Directory.CreateDirectory(root); Fixture(root, 1800); break;
                    case "input-cut": EditRows(root, "input", rows => rows.RemoveAt(2)); break;
                    case "snapshot-gap": EditRows(root, "snapshots", rows => rows.RemoveAt(2)); break;
                    case "video-cut":
                        string pts = Package + "synthetic-0/packets.csv";
                        File.WriteAllText(Path.Combine(root, pts), File.ReadAllText(Path.Combine(root, pts)).Replace("\n2\n3\n", "\n"));
                        EditVideo(root, v => v["tasks"] = Node(Reference(root, pts))); break;
                    case "video-hash": EditVideo(root, v => v["sha256"] = Node(new string('c', 64))); break;
                    case "video-duration": EditVideo(root, v => v["duration_s"] = Node(3603)); break;
                    case "video-container": EditVideo(root, v => v["container"] = Node("avi")); break;
                    case "edited-video":
                        string cap = Package + "synthetic-0/capture.json"; Edit(root, cap, c => c["uncut"] = Node(false)); EditVideo(root, v => v["capture"] = Node(Reference(root, cap))); break;
                    case "duplicate-command": case "duplicate-birth": case "duplicate-estate":
                        string ev = test.Key == "duplicate-command" ? "command-applied" : test.Key.Substring(10);
                        EditRows(root, "log", rows => { int at = rows.FindIndex(r => r["event"].GetString() == ev); rows.Insert(at + 1, new Dictionary<string, JsonElement>(rows[at])); }); break;
                    case "permanent-blockage": EditRows(root, "log", rows => rows.RemoveAll(r => r["event"].GetString() == "action-completed")); break;
                    case "new-block-after-recovery":
                        EditRows(root, "log", rows => {
                            int at = rows.FindIndex(r => r["event"].GetString() == "session-end");
                            var onset = new Dictionary<string, JsonElement>(rows.Find(r => r["event"].GetString() == "block-onset"));
                            onset["utc"] = Node(Stamp(150)); onset["game_time"] = Node(150); rows.Insert(at++, onset);
                            var reeval = new Dictionary<string, JsonElement>(onset); reeval["event"] = Node("reevaluate"); reeval["utc"] = Node(Stamp(155)); reeval["game_time"] = Node(155); rows.Insert(at, reeval);
                        }); break;
                    case "crash": EditRows(root, "log", rows => rows[rows.Count - 1]["event"] = Node("crash")); break;
                    case "lost-save":
                        string bad = Package + "synthetic-0/lost.json"; Write(root, bad, new { synthetic = true, generation = 9 });
                        EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "reload")["loaded_state"] = Node(Reference(root, bad))); break;
                    case "rollback-two-saves": EditRows(root, "log", rows => rows.Find(r => r["event"].GetString() == "reload")["generation"] = Node(8)); break;
                    case "stale-commit":
                        string orphan = Git(root, "commit-tree", T(Read(Path.Combine(root, Record)), "tree_hash"), "-m", "SYNTHETIC unrelated source");
                        Edit(root, Record, r => r["commit"] = Node(orphan)); break;
                    case "stale-tree": Edit(root, Record, r => r["tree_hash"] = Node(new string('0', 40))); break;
                    case "stale-save": EditRows(root, "input", rows => rows[0]["start_save_sha256"] = Node(new string('0', 64))); break;
                    case "stale-archive": EditRows(root, "log", rows => rows[0]["archive_sha256"] = Node(new string('0', 64))); break;
                    case "missing-result": File.Delete(Path.Combine(root, Package + "result.json")); break;
                    case "stale-result": break;
                    case "wrong-counts": EditSession(root, s => { var c = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s["counts"].GetRawText()); c["blocked"] = Node(1); s["counts"] = Node(c); }); break;
                    case "wrong-target": EditSession(root, s => s["target_s"] = Node(5400)); break;
                    case "missing-session": Edit(root, Record, r => r["sessions"] = Node(new object[0])); break;
                    case "ordinary-path-bypass": EditRows(root, "log", rows => rows[rows.Count - 1]["event"] = Node("direct-state-edit")); break;
                    case "missing-606-case": EditCases(root, rows => rows.RemoveAt(0)); break;
                    case "duplicate-case": EditCases(root, rows => rows.Add(rows[0])); break;
                    case "unrun-as-pass":
                        File.WriteAllText(Path.Combine(root, logPath), "SYNTHETIC traceability only: " + local + "\n"); EditCases(root, rows => rows.Find(r => r["id"].GetString() == local)["log"] = Node(Reference(root, logPath))); break;
                    case "skipped-as-pass": case "not-run-as-pass": case "explicit-unrun-as-pass":
                        File.AppendAllText(Path.Combine(root, logPath), (test.Key == "skipped-as-pass" ? "SKIP " : test.Key == "not-run-as-pass" ? "NOT_RUN " : "UNRUN ") + local + "\n"); EditCases(root, rows => rows.Find(r => r["id"].GetString() == local)["log"] = Node(Reference(root, logPath))); break;
                    case "later-gate-as-pass": EditCases(root, rows => rows.Find(r => r["id"].GetString() == "SEIT-PERF-04-P1")["disposition"] = Node("component-pass")); break;
                    case "false-spec-evidence": EditCases(root, rows => { var row = rows.Find(r => r["id"].GetString() == local); row["disposition"] = Node("spec-evidence"); }); break;
                    case "spec-as-component": EditCases(root, rows => rows.Find(r => r["id"].GetString() == "SEIT-SPEC-01")["disposition"] = Node("component-pass")); break;
                    case "laundered-synthetic":
                        Edit(root, Record, r => r["synthetic"] = Node(false)); Edit(root, Package + "result.json", r => r["synthetic"] = Node(false)); break;
                    case "obligation-as-pass": Edit(root, Record, r => r["obligations"] = Node(new[] { new { kind = "soak", gate = "GATE-M5", status = "PASS" } })); break;
                    case "synthetic-as-real": break;
                    case "ref-escape": EditSession(root, s => s["start_save"] = Node(new { path = Package + "../soak.json", sha256 = new string('0', 64) })); break;
                    case "ref-hash": EditSession(root, s => s["start_save"] = Node(new { path = Package + "synthetic-0/save.json", sha256 = new string('0', 64) })); break;
                }
                if (test.Key != "missing-result") Edit(root, Package + "result.json", r => r["soak_sha256"] = Node(test.Key == "stale-result" ? new string('0', 64) : Hash(Path.Combine(root, Record))));
                string actual = null;
                try { Validate(root, test.Key != "synthetic-as-real" && test.Key != "laundered-synthetic"); } catch (Rejection rejected) { actual = rejected.Id; }
                Need(actual == test.Value, "S-Fixture.TargetedRejection." + test.Key + ".Actual." + (actual ?? "NONE"));
                Console.WriteLine("FAIL negative-fixture (" + test.Key + ") " + actual + " [expected SYNTHETIC rejection]");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine("PASS " + cases.Count + " SYNTHETIC positive controls and targeted negative rejections; no gameplay evidence");
        return 1;
    }
    public static int Main(string[] args)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (args.Length == 1 && args[0] == "negative-fixture") return Negatives();
            Need(args.Length == 0, "S-1.Arguments"); Validate(Directory.GetCurrentDirectory()); Console.WriteLine("SOAK ALL PASS"); return 0;
        }
        catch (Rejection failure) { Console.WriteLine("FAIL " + failure.Id); return 1; }
        catch (Exception failure) { Console.WriteLine("FAIL S-1.EvidenceOrHarness." + failure.GetType().Name); return 1; }
        finally { Console.WriteLine("SOAK GATE seconds=" + watch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + " resource_groups=isolated-csharp production_resource_groups=i7-editor,standalone-player-display,capture"); }
    }
}
