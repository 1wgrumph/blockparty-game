/* LIVE-AI oracle only, issue #91, full-game-rev2-m5-1; baseline 0a71d334.
 * Deliverable contract (all REF = {path,sha256}, package-owned repo-relative
 * files under evidence/production/LIVE-AI/, never Assets/tests/shared files):
 * evidence/production/live-ai.json = {schema:"blockparty-live-ai/1",commit,
 * tree_hash,build:{archive_sha256,platform},decision:REF,services:[{service,
 * purpose,verdict,rows:[REF],typed_gap?,model_id,endpoint_class}],offline:[{
 * service,fault,verdict:"injected-local",log:REF}]}.
 * decision is a byte-for-byte copy of docs/production/service-access-decision.json
 * at commit (git show), NOT a hash of today's working copy. Its services array
 * contains service names or {service:...} entries; open_decisions:["OD-4"]
 * explicitly permits d1-ultra deferral. OWNER-SERVICES has not yet produced
 * its JSON; a different eventual decision shape needs an explicit oracle rebind.
 * commit/tree_hash bind the recorded source, never the evolving worktree HEAD.
 *
 * Developer produces offline logs by compiling this suite with the pinned
 * run-standalone.py, then invoking the resulting binary with netcorerun:
 *   LIVE-AI.exe offline-injection "<service>" <fault> > <package>/offline/<id>.log
 * for every service below x timeout,malformed,denied,late,unprovisioned.
 * Capture exit 0; hash each complete stdout log into live-ai.json. This mode
 * runs real LiveWire parsers, adapters, deadline/fallback logic; only external
 * wire bodies/I/O are synthetic. Do not manufacture verdict JSON or PASS lines.
 * Normal verification replays these same injections and compares the complete
 * log, including the observed before/after state of the late local alternative.
 * Fresh world/adapter per pair; no timers, sockets, credentials or engine.
 *
 * William produces OG-LIVE-AI on his own machine/key and standalone build:
 * exported sitting/<session>.jsonl with exactly the 13 allow-listed fields
 * below, plus sitting/sitting.json {operator_role:"owner",machine:{os,cpu,gpu},
 * started_utc,ended_utc,services_attempted:[service names]}. Agents receive only
 * this sanitized export, never settings, keys or raw provider logs. Connected
 * rows need live transport, provider id, status/outcome/latency, model, purpose,
 * matching archive hash and a build commit ancestor-or-equal to commit.
 * connected-pass = Applied; connected-fail = non-Applied except Denied and
 * Unprovisioned (these are typed gaps). Every matching row is checked, not just
 * the first. Typed gaps are labelled evidence, never endpoint qualification.
 * result.json remains developer work, checked by the generic verify.py gate.
 *
 * Oracle -> suite_checks -> LOCAL SEIT support (whole cases pending GATE-M5):
 * LA-1 Shape/Refs/Decision: (1), common.ref_rule; SEIT-IF-D1U-N3/IF-IMG-N1.
 * LA-2 ServiceSet/Verdict/Deferral: (2); SEIT-AI-04-N1/N4, IF-D1U-N3.
 * LA-3 Sitting/Owner/Rows: (3), OG-LIVE-AI; IF-IMG-N1, AI-07-N1/N2.
 * LA-4 Offline/Replay and LA-INJECT-{IMAGE,VOICE,SAM,D1}-{fault}:
 *   (4); AI-04-N1/N4/N6, AI-07-N1/N2, IF-D1U-N3, IF-IMG-N1, SM-AI-I6.
 * LA-5 SecretScan/Fields: (5); SEIT-SECU-01-N1 (local evidence only).
 * LA-6 Tree/BuildCommit/Archive: (6); local PROC-VENDOR-CONNECTED support.
 * negative-fixture: (a) fake AND injected connected rows, (b) missing service,
 * (c) secret shapes in an otherwise unreferenced nested file, (d) valid but
 * unrelated build commit, (e) altered late fallback state. Each corruption gets
 * a fresh GUID temp git/evidence root, first passes all unchanged positive
 * oracles, then MUST fail its named oracle. Roots removed in finally, including
 * preparation failures. All generated records explicitly SYNTHETIC; they
 * cannot establish an owner sitting. No real evidence is generated here.
 * Resource group isolated-csharp + owned temp files/git processes; no shared
 * engine/display/ports; no expensive engine qualification applies.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class LiveAiChecks
{
    private const string Package = "evidence/production/LIVE-AI/";
    private const string Record = "evidence/production/live-ai.json";
    private const string DecisionPath = "docs/production/service-access-decision.json";
    private static readonly string[] Services = { "Muse Image", "Muse Voice Transcribe", "SAM 3.1", "d1-ultra" };
    private static readonly string[] Codes = { "IMAGE", "VOICE", "SAM", "D1" };
    private static readonly string[] Purposes = { "ImageGeneration", "VoiceTranscription", "ImageSearch", "ResidentDecision" };
    private static readonly string[] Faults = { "timeout", "malformed", "denied", "late", "unprovisioned" };
    private static readonly string[] RowFields = { "session_id", "utc", "service", "purpose", "transport_kind", "request_id", "provider_request_id", "status", "outcome", "latency_ms", "model_id", "build_commit", "archive_sha256" };
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
    private static readonly string[] Bodies = {
        "{\"data\":[{\"b64_json\":\"" + Png + "\"}]}",
        "{\"type\":\"transcript\",\"transcript\":\"pause\",\"final\":true,\"audioProcessedMs\":3600}",
        "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"}]}]}",
        "{\"model\":\"d1-ultra\",\"answers\":{\"activity\":{\"choice\":\"ACT-FOOD-02\"}}}"
    };

    private sealed class OracleFailure : Exception
    {
        public readonly string Id;
        public OracleFailure(string id) { Id = id; }
    }
    private static void Need(bool ok, string id) { if (!ok) throw new OracleFailure(id); }
    private static string Text(JsonElement e, string name) { return e.GetProperty(name).GetString(); }
    private static bool Hex(string text, int length)
    {
        if (text == null || text.Length != length) return false;
        foreach (char c in text) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }
    private static string Hash(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static string HashFile(string path) { return Hash(File.ReadAllBytes(path)); }
    private static JsonElement Read(string path)
    { using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(path))) return d.RootElement.Clone(); }
    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root,
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using (Process p = Process.Start(start))
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { p.Kill(); throw new OracleFailure("LA-6.GitTimeout"); }
            stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
            Need(p.ExitCode == 0, "LA-6.GitObjectOrAncestry");
            return stdout.Result;
        }
    }
    private static string Ref(string root, JsonElement reference, string lane = "")
    {
        string path = Text(reference, "path"), sha = Text(reference, "sha256");
        Need(path != null && path.StartsWith(Package + lane, StringComparison.Ordinal)
            && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0, "LA-1.RefScope");
        foreach (string part in path.Split('/')) Need(part != ".." && part != "." && part != "", "LA-1.RefScope");
        string absolute = root;
        foreach (string part in path.Split('/'))
        {
            absolute = Path.Combine(absolute, part);
            Need(File.Exists(absolute) || Directory.Exists(absolute), "LA-1.RefPresent");
            Need((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0, "LA-1.RefScope");
        }
        Need(File.Exists(absolute) && Hex(sha, 64) && HashFile(absolute) == sha, "LA-1.RefHash");
        return absolute;
    }
    private static bool Token(char c)
    { return char.IsLetterOrDigit(c) || "._~+/=-".IndexOf(c) >= 0; }
    private static bool Secret(string text)
    {
        string lower = text.ToLowerInvariant();
        for (int i = 0; i < lower.Length; i++)
        {
            if (i + 3 <= lower.Length && string.CompareOrdinal(lower, i, "sk-", 0, 3) == 0)
            {
                int end = i + 3; while (end < text.Length && Token(text[end])) end++;
                if (end - i >= 11) return true;
            }
            foreach (string marker in new[] { "bearer", "model_api_key", "api_key", "api-key", "access_token", "access-token", "authorization", "secret", "token", "key" })
            {
                if (i > 0 && Token(lower[i - 1])) continue;
                if (i + marker.Length > lower.Length || string.CompareOrdinal(lower, i, marker, 0, marker.Length) != 0) continue;
                int end = i + marker.Length;
                if (end < lower.Length && (char.IsLetterOrDigit(lower[end]) || lower[end] == '_')) continue;
                if (marker == "bearer")
                {
                    if (end < text.Length && char.IsWhiteSpace(text[end]))
                    {
                        while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                        int start = end; while (end < text.Length && Token(text[end])) end++;
                        if (end - start >= 16) return true;
                    }
                    continue;
                }
                while (end < text.Length && (char.IsWhiteSpace(text[end]) || text[end] == '"' || text[end] == '\'')) end++;
                if (marker == "key")
                {
                    int run = end;
                    while (run < text.Length && Token(text[run])) run++;
                    string adjacent = text.Substring(end, run - end);
                    if (Hex(adjacent.ToLowerInvariant(), adjacent.Length) && adjacent.Length >= 32 || adjacent.Length >= 48) return true;
                }
                if (end >= text.Length || (text[end] != ':' && text[end] != '=')) continue;
                end++;
                while (end < text.Length && (char.IsWhiteSpace(text[end]) || text[end] == '"' || text[end] == '\'')) end++;
                if (end < text.Length && Token(text[end])) return true;
            }
        }
        return false;
    }
    private static bool JsonSecret(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Secret(value.GetString());
        if (value.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in value.EnumerateObject())
                if (Secret(p.Name + "=" + p.Value.ToString()) || JsonSecret(p.Value)) return true;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) if (JsonSecret(item)) return true;
        return false;
    }
    private static void Scan(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string entry in Directory.GetFileSystemEntries(dir))
        {
            Need((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "LA-5.SecretScanLink");
            if (Directory.Exists(entry)) { Scan(entry); continue; }
            string text = Encoding.UTF8.GetString(File.ReadAllBytes(entry));
            Need(!Secret(text), "LA-5.SecretScan");
            if (Path.GetExtension(entry) == ".json") Need(!JsonSecret(Read(entry)), "LA-5.SecretScan");
            if (Path.GetExtension(entry) == ".jsonl")
                foreach (string line in File.ReadAllLines(entry))
                    if (!string.IsNullOrWhiteSpace(line))
                        using (JsonDocument d = JsonDocument.Parse(line)) Need(!JsonSecret(d.RootElement), "LA-5.SecretScan");
        }
    }
    private static DateTimeOffset Utc(string text)
    {
        Need(text != null && text.EndsWith("Z", StringComparison.Ordinal) || text != null && text.EndsWith("+00:00", StringComparison.Ordinal), "LA-3.Utc");
        Need(DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset utc), "LA-3.Utc");
        return utc;
    }
    private static bool SafeId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        foreach (char c in value)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && "_.:-".IndexOf(c) < 0) return false;
        return !Secret(value);
    }
    private static HashSet<string> Names(JsonElement items)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string name = item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "service");
            Need(Array.IndexOf(Services, name) >= 0 && names.Add(name), "LA-2.ServiceSet");
        }
        return names;
    }
    private static void Validate(string root)
    {
        Scan(Path.Combine(root, Package));
        Need(File.Exists(Path.Combine(root, Record)), "LA-1.RecordPresent.MissingEvidence");
        JsonElement record = Read(Path.Combine(root, Record));
        Need(Text(record, "schema") == "blockparty-live-ai/1", "LA-1.Schema");
        string commit = Text(record, "commit"), tree = Text(record, "tree_hash");
        Need(Hex(commit, 40) && Hex(tree, 40), "LA-6.SourceShape");
        Need(Git(root, "rev-parse", commit + "^{tree}").Trim() == tree, "LA-6.Tree");
        JsonElement build = record.GetProperty("build");
        string archive = Text(build, "archive_sha256");
        Need(Hex(archive, 64) && !string.IsNullOrWhiteSpace(Text(build, "platform")), "LA-6.Archive");
        string decisionCopy = Ref(root, record.GetProperty("decision"));
        string decisionSource = Git(root, "show", commit + ":" + DecisionPath);
        Need(Hash(Encoding.UTF8.GetBytes(decisionSource)) == HashFile(decisionCopy), "LA-1.DecisionAtCommit");
        JsonElement decision = Read(decisionCopy);
        HashSet<string> expectedServices = Names(decision.GetProperty("services"));
        Need(expectedServices.Count == Services.Length, "LA-2.ServiceSet");
        JsonElement services = record.GetProperty("services");
        Need(Names(services).SetEquals(expectedServices), "LA-2.ServiceSet");
        bool od4Open = false;
        if (decision.TryGetProperty("open_decisions", out JsonElement open))
            foreach (JsonElement item in open.EnumerateArray()) od4Open |= item.GetString() == "OD-4";

        string sittingPath = Path.Combine(root, Package + "sitting/sitting.json");
        Need(File.Exists(sittingPath), "LA-3.OwnerRecordPresent");
        JsonElement sitting = Read(sittingPath);
        Need(Text(sitting, "operator_role") == "owner", "LA-3.Owner");
        foreach (string field in new[] { "os", "cpu", "gpu" }) Need(!string.IsNullOrWhiteSpace(Text(sitting.GetProperty("machine"), field)), "LA-3.Machine");
        DateTimeOffset start = Utc(Text(sitting, "started_utc")), end = Utc(Text(sitting, "ended_utc"));
        Need(start <= end, "LA-3.Times");
        HashSet<string> attempted = Names(sitting.GetProperty("services_attempted"));
        var mustAttempt = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement service in services.EnumerateArray())
        {
            string name = Text(service, "service"), verdict = Text(service, "verdict");
            int index = Array.IndexOf(Services, name);
            string purpose = Text(service, "purpose"), model = Text(service, "model_id");
            Need(purpose == Purposes[index] && SafeId(model) && !string.IsNullOrWhiteSpace(Text(service, "endpoint_class")), "LA-1.ServiceShape");
            bool connected = verdict == "connected-pass" || verdict == "connected-fail";
            bool deferred = verdict == "deferred-local-fallback";
            bool gap = Array.IndexOf(new[] { "typed-gap:denied", "typed-gap:unprovisioned", "typed-gap:no-key", "typed-gap:unavailable" }, verdict) >= 0;
            Need(connected || deferred || gap, "LA-2.Verdict");
            if (gap) Need(Text(service, "typed_gap") == verdict.Substring(10), "LA-2.TypedGap");
            if (deferred) Need(name == "d1-ultra" && od4Open, "LA-2.Deferral");
            else mustAttempt.Add(name);
            int matching = 0;
            var rowRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement reference in service.GetProperty("rows").EnumerateArray())
            {
                string path = Ref(root, reference, "sitting/");
                Need(Path.GetExtension(path) == ".jsonl" && rowRefs.Add(path), "LA-3.Rows");
                int count = 0;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    count++;
                    using (JsonDocument d = JsonDocument.Parse(line))
                    {
                        JsonElement row = d.RootElement;
                        var fields = new HashSet<string>(StringComparer.Ordinal);
                        foreach (JsonProperty p in row.EnumerateObject())
                            Need(Array.IndexOf(RowFields, p.Name) >= 0 && fields.Add(p.Name), "LA-5.Fields");
                        Need(fields.Count == RowFields.Length, "LA-3.RowShape");
                        string rowService = Text(row, "service");
                        int rowIndex = Array.IndexOf(Services, rowService);
                        Need(rowIndex >= 0 && Text(row, "purpose") == Purposes[rowIndex], "LA-3.RowService");
                        Need(Text(row, "transport_kind") == "live", "LA-3.LiveTransport");
                        Need(Guid.TryParseExact(Text(row, "session_id"), "D", out Guid session), "LA-3.Session");
                        DateTimeOffset time = Utc(Text(row, "utc"));
                        Need(time >= start && time <= end, "LA-3.Times");
                        JsonElement request = row.GetProperty("request_id");
                        Need((request.ValueKind == JsonValueKind.String || request.ValueKind == JsonValueKind.Number) && SafeId(request.ToString()), "LA-3.Request");
                        Need(request.ValueKind != JsonValueKind.Number || request.TryGetInt64(out long requestNumber), "LA-3.Request");
                        Need(row.GetProperty("status").TryGetInt32(out int status) && status >= 0 && status <= 999
                            && row.GetProperty("latency_ms").TryGetInt64(out long latency) && latency >= 0, "LA-3.StatusLatency");
                        string outcomeText = Text(row, "outcome");
                        Need(Enum.TryParse(outcomeText, out GatewayOutcome outcome) && Enum.IsDefined(typeof(GatewayOutcome), outcome)
                            && outcome.ToString() == outcomeText, "LA-3.Outcome");
                        Need(Text(row, "archive_sha256") == archive, "LA-6.Archive");
                        string buildCommit = Text(row, "build_commit");
                        Need(Hex(buildCommit, 40), "LA-6.BuildCommit");
                        Git(root, "merge-base", "--is-ancestor", buildCommit, commit);
                        Need(SafeId(Text(row, "model_id")), "LA-3.Model");
                        if (rowService != name) continue;
                        matching++;
                        Need(Text(row, "purpose") == purpose && Text(row, "model_id") == model, "LA-3.ModelPurpose");
                        if (connected)
                        {
                            Need(SafeId(Text(row, "provider_request_id")) && status > 0, "LA-3.ProviderReceipt");
                            Need(verdict == "connected-pass" ? outcome == GatewayOutcome.Applied
                                : outcome != GatewayOutcome.Applied && outcome != GatewayOutcome.Denied && outcome != GatewayOutcome.Unprovisioned,
                                "LA-3.VerdictOutcome");
                        }
                        else if (gap)
                            Need(verdict == "typed-gap:denied" ? outcome == GatewayOutcome.Denied
                                : verdict == "typed-gap:unprovisioned" || verdict == "typed-gap:no-key" ? outcome == GatewayOutcome.Unprovisioned
                                : outcome != GatewayOutcome.Applied, "LA-3.VerdictOutcome");
                    }
                }
                Need(count > 0, "LA-3.EmptyRows");
            }
            Need(!connected || matching > 0, "LA-3.ConnectedRows");
        }
        Need(attempted.SetEquals(mustAttempt), "LA-3.ServiceAttempts");
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in record.GetProperty("offline").EnumerateArray())
        {
            int service = Array.IndexOf(Services, Text(item, "service"));
            string fault = Text(item, "fault");
            Need(service >= 0 && Array.IndexOf(Faults, fault) >= 0 && pairs.Add(service + ":" + fault)
                && Text(item, "verdict") == "injected-local", "LA-4.OfflineMatrix");
            string log = File.ReadAllText(Ref(root, item.GetProperty("log"), "offline/"));
            Need(log.Replace("\r\n", "\n") == Inject(service, fault), "LA-4.Replay." + Codes[service] + "." + fault);
        }
        Need(pairs.Count == Services.Length * Faults.Length, "LA-4.OfflineMatrix");
    }

    private sealed class World
    {
        public readonly WorldState State = WorldState.CreateSeeded(7L, 42L, 1, new[] { "Ada" });
        public readonly ResidentRecord Resident;
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly Autonomy Autonomy;
        public readonly ProposalBoundary Boundary;
        public World()
        {
            Resident = State.ResidentsSnapshot()[0];
            for (int i = 0; i < StateSchema.NeedCount; i++) State.SetNeed(StateWriter.SimCore, Resident.Id, (Need)i, 80f);
            State.SetNeed(StateWriter.SimCore, Resident.Id, Blockparty.Simulation.Need.Hunger, 8f);
            Clock = new Clock(State); Commands = new CommandArbitrator(State, Clock);
            Autonomy = new Autonomy(State, Commands);
            Boundary = new ProposalBoundary(State, Clock, Commands, Autonomy);
        }
    }
    private sealed class Inbox : IDecisionTransport
    {
        public DecisionResponse Reply;
        public void Send(GatewayRequest request, DecisionPayload payload) { }
        public bool TryReceive(out DecisionResponse response)
        { response = Reply; Reply = null; return response != null; }
    }
    private static WorkItem Art(World w, Creative creative)
    {
        w.State.SetMoney(StateWriter.SimCore, w.Resident.Id, 100L);
        w.State.AddSkillPoints(StateWriter.SimCore, w.Resident.Id, "Creativity", 1000);
        WorldEvent memory = w.State.AppendEvent(StateWriter.SimCore, "help", w.Resident.Id, "carried a box");
        w.State.AddMemory(StateWriter.SimCore, w.Resident.Id, new MemoryEntry(memory.Event, "help", 0L,
            w.Resident.Home, new[] { w.Resident.Id }, "carried a box", "participant"));
        var executor = new ActivityExecutor(w.State, w.Commands, w.Clock);
        ObjectId station;
        do { station = w.State.AllocateObject(StateWriter.SimCore); } while ((long)station <= 2L);
        executor.RegisterObject(station, new[] { new ApproachPose(0f, 0f, 0f) });
        executor.SetResidentPosition(w.Resident.Id, new ApproachPose(0f, 0f, 0f));
        CommandResult command = w.Commands.Submit(new CommandEnvelope(w.State.AllocateCommand(StateWriter.SimCore),
            w.State.Epoch, w.State.GameSeconds, CommandSource.PlayerHuman, w.Resident.ResidentSequence + 1u,
            CommandKind.DirectActivity, w.Resident.Id, new ActivityId(CreativeWork.MakeWallArtActivity),
            (long)station, new CommandParameters.Direct(QueueMode.Immediate)));
        Need(command.Accepted, "LA-INJECT.ImagePreparation");
        ActionInstanceId action = w.Commands.GetCurrent(w.Resident.Id).Action;
        executor.Tick(StateWriter.ActivityExecutor); executor.Tick(StateWriter.ActivityExecutor);
        w.Clock.AdvanceRealSeconds(StateWriter.SimCore, 90.0); // Real MakeWallArt duration, IMAGE.cs fixture.
        executor.Tick(StateWriter.ActivityExecutor);
        Need(executor.GetExecution(action).Outcome == TerminalOutcome.Completed
            && creative.Complete(StateWriter.ActivityExecutor, action, new ActivityId(CreativeWork.MakeWallArtActivity),
                w.Resident.Id, memory.Event), "LA-INJECT.ImagePreparation");
        return creative.WorkItems[0];
    }
    private static string StateOf(World w, int service, ImageAdapter image, WorkItem art, VoiceAdapter voice, CaptureSearch sam)
    {
        if (service == 0)
        { ImageContent c = image.GetContent(art.Reference.Item); return c == null ? "pending" : c.Path + ":" + Hash(Encoding.UTF8.GetBytes(c.ImageBase64 ?? "")); }
        // A late transcript may be displayed; it must never execute a command.
        if (service == 1) return w.Commands.CommandLog.Count + ":" + w.Clock.Paused;
        if (service == 2) return (sam.Result == null ? "no-outlines" : "result") + ":" + sam.Misses.Count;
        ActionEntry[] actions = w.Commands.GetPending(w.Resident.Id);
        return actions.Length == 1 ? actions[0].Action + ":" + actions[0].Owner + ":" + actions[0].Activity.Code + ":" + actions[0].TargetId : "count:" + actions.Length;
    }
    private static string Inject(int service, string fault)
    {
        var w = new World(); var inbox = new Inbox();
        ImageAdapter image = null; VoiceAdapter voice = null; CaptureSearch sam = null; DecisionAdapter d1 = null; WorkItem art = null;
        var endpoint = new ServiceEndpoint(Services[service], "https://example.invalid/synthetic", "synthetic-model", true);
        GatewayRequest request;
        if (service == 0)
        {
            var creative = new Creative(w.State); art = Art(w, creative);
            image = new ImageAdapter(w.State, w.Boundary, creative);
            request = image.Request(art.Reference.Item, endpoint, true, true);
        }
        else if (service == 1)
        { voice = new VoiceAdapter(w.State, w.Boundary, w.Commands); request = voice.Request(endpoint, true, true, true); }
        else if (service == 2)
        { sam = new CaptureSearch(w.Boundary); sam.SetCapture((CaptureId)41L, new byte[100 * 100 * 4], 100, 100); request = sam.Request("chair", endpoint, true, true); }
        else
        { d1 = new DecisionAdapter(w.Boundary, inbox); request = d1.RequestDecision(w.Resident.Id, endpoint, true, true); }
        Need(request != null, "LA-INJECT.Request");
        if (fault == "late")
        {
            if (service == 0)
            {
                image.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
                Need(w.Boundary.IsPending(request.Id) && LiveWire.SittingOutcome(w.Boundary, request.Id) != GatewayOutcome.Timeout, "LA-INJECT.ImagePendingAt10");
                image.Tick(LiveWire.ImageGenerationTimeoutRealSeconds - GatewayLimits.ServiceTimeoutRealSeconds);
            }
            else if (service == 2) sam.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
            else if (service == 3) d1.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
            else LiveWire.AdvanceSitting(w.Boundary, GatewayLimits.ServiceTimeoutRealSeconds);
            Need(LiveWire.SittingOutcome(w.Boundary, request.Id) == GatewayOutcome.Timeout, "LA-INJECT.LateDeadline");
        }
        string before = StateOf(w, service, image, art, voice, sam);
        int status = fault == "timeout" ? 408 : fault == "denied" ? 403 : fault == "unprovisioned" ? 404 : 200;
        string body = fault == "malformed" ? "{" : Bodies[service];
        string frame = null;
        GatewayOutcome mapped;
        DecisionResponse reply = null;
        if (service == 0) mapped = LiveWire.ParseImage(status, body, out frame);
        else if (service == 1) mapped = LiveWire.ParseVoice(status, body, out frame);
        else if (service == 2) mapped = LiveWire.ParseSam(status, body, (CaptureId)41L, out frame);
        else { reply = LiveWire.ParseDecision(request.Id, status, body); mapped = reply.Outcome; }
        GatewayOutcome expected = fault == "timeout" ? GatewayOutcome.Timeout : fault == "denied" ? GatewayOutcome.Denied
            : fault == "unprovisioned" ? GatewayOutcome.Unprovisioned : fault == "malformed" ? GatewayOutcome.Malformed : GatewayOutcome.Applied;
        string id = "LA-INJECT-" + Codes[service] + "-" + fault;
        Need(mapped == expected, id + ".Parser");
        ProposalVerdict verdict = ProposalVerdict.DiscardedInvalid;
        if (service == 0) verdict = mapped == GatewayOutcome.Applied ? image.Complete(request.Id, frame) : image.Fail(request.Id, mapped);
        else if (service == 1) verdict = LiveWire.DeliverVoice(voice, w.Boundary, request.Id, mapped, frame);
        else if (service == 2) verdict = mapped == GatewayOutcome.Applied ? sam.Complete(request.Id, frame) : sam.Fail(request.Id, mapped);
        else { inbox.Reply = reply; d1.Tick(0.0); }
        string after = StateOf(w, service, image, art, voice, sam);
        GatewayOutcome terminal = fault == "late" ? GatewayOutcome.Timeout : expected;
        Need(w.Boundary.PendingCount == 0 && LiveWire.SittingOutcome(w.Boundary, request.Id) == terminal, id + ".Terminal");
        foreach (ProposalLogEntry row in w.Boundary.DecisionLog) Need(row.Outcome != GatewayOutcome.Applied, id + ".NoApplied");
        if (service == 0) Need(image.GetContent(art.Reference.Item).Path == "local-alternative", id + ".Local");
        if (service == 1) Need(!w.Clock.Paused && w.Commands.CommandLog.Count == 0, id + ".Manual");
        if (service == 2) Need(sam.Result == null && sam.Misses.Count == 0, id + ".NoOutlines");
        if (service == 3)
        {
            ActionEntry[] queue = w.Commands.GetPending(w.Resident.Id);
            Need(queue.Length == 1 && queue[0].Owner == ActionOwner.Autonomous && w.Autonomy.Decisions.Count == 1, id + ".LocalDecision");
        }
        if (fault == "late") Need(before == after && (service == 3 || verdict != ProposalVerdict.Applied), id + ".FallbackPreserved");
        var output = new StringBuilder("SYNTHETIC injected-local\n");
        output.Append("PASS ").Append(id).Append(".Parser\nPASS ").Append(id).Append(".TerminalAndLocal\n");
        if (fault == "late") output.Append("fallback=").Append(before).Append("\nafter=").Append(after)
            .Append("\nPASS ").Append(id).Append(".FallbackPreserved\n");
        return output.ToString();
    }

    private static Dictionary<string, object> Reference(string root, string relative)
    { return new Dictionary<string, object> { ["path"] = relative, ["sha256"] = HashFile(Path.Combine(root, relative)) }; }
    private static void Write(string root, string relative, object value)
    {
        string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(value) + "\n");
    }
    private static Dictionary<string, object> Synthetic(string root)
    {
        Directory.CreateDirectory(root);
        Git(root, "init", "--quiet");
        Write(root, DecisionPath, new { synthetic = true, services = Services, open_decisions = new[] { "OD-4" } });
        Git(root, "add", "--", DecisionPath);
        Git(root, "-c", "user.name=Synthetic", "-c", "user.email=synthetic@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "SYNTHETIC oracle fixture");
        string commit = Git(root, "rev-parse", "HEAD").Trim(), tree = Git(root, "rev-parse", "HEAD^{tree}").Trim();
        Directory.CreateDirectory(Path.Combine(root, Package + "offline"));
        Directory.CreateDirectory(Path.Combine(root, Package + "sitting"));
        File.WriteAllText(Path.Combine(root, Package + "synthetic-control.log"), "SYNTHETIC label c-bearer\nPASS control\n");
        File.Copy(Path.Combine(root, DecisionPath), Path.Combine(root, Package + "decision.json"));
        string archive = new string('b', 64);
        var services = new List<Dictionary<string, object>>(); var offline = new List<object>();
        for (int s = 0; s < Services.Length; s++)
        {
            string path = Package + "sitting/synthetic-" + Codes[s] + ".jsonl";
            Write(root, path, new { session_id = "11111111-2222-3333-4444-555555555555", utc = "2026-10-30T12:00:00Z",
                service = Services[s], purpose = Purposes[s], transport_kind = "live", request_id = 41,
                provider_request_id = "synthetic-receipt", status = 200, outcome = "Applied", latency_ms = 0,
                model_id = "synthetic-model", build_commit = commit, archive_sha256 = archive });
            services.Add(new Dictionary<string, object> { ["service"] = Services[s], ["purpose"] = Purposes[s],
                ["verdict"] = "connected-pass", ["rows"] = new[] { Reference(root, path) },
                ["model_id"] = "synthetic-model", ["endpoint_class"] = "synthetic" });
            foreach (string fault in Faults)
            {
                string log = Package + "offline/" + Codes[s] + "-" + fault + ".log";
                File.WriteAllText(Path.Combine(root, log), Inject(s, fault));
                offline.Add(new Dictionary<string, object> { ["service"] = Services[s], ["fault"] = fault,
                    ["verdict"] = "injected-local", ["log"] = Reference(root, log) });
            }
        }
        Write(root, Package + "sitting/sitting.json", new { synthetic = true, operator_role = "owner",
            machine = new { os = "synthetic", cpu = "synthetic", gpu = "synthetic" },
            started_utc = "2026-10-30T11:59:00Z", ended_utc = "2026-10-30T12:01:00Z", services_attempted = Services });
        var record = new Dictionary<string, object> { ["schema"] = "blockparty-live-ai/1", ["commit"] = commit,
            ["tree_hash"] = tree, ["build"] = new { archive_sha256 = archive, platform = "synthetic" },
            ["decision"] = Reference(root, Package + "decision.json"), ["services"] = services, ["offline"] = offline };
        Write(root, Record, record); return record;
    }
    private static int Negatives()
    {
        string[] cases = { "a-fake", "a-injected", "b", "c-bearer", "c-bearer-16", "c-key", "c-key-run", "c-adjacent-hex", "c-adjacent-base64", "c-escaped", "d", "e",
            "positive-bearer-prose", "positive-ancestor", "positive-gaps", "positive-deferral" };
        foreach (string test in cases)
        {
            string root = Path.Combine(Path.GetTempPath(), "blockparty-live-ai-" + Guid.NewGuid().ToString("N"));
            try
            {
                Dictionary<string, object> record = Synthetic(root);
                Validate(root); Console.WriteLine("PASS synthetic positive control " + test);
                var services = (List<Dictionary<string, object>>)record["services"];
                if (test.StartsWith("positive-", StringComparison.Ordinal))
                {
                    if (test == "positive-bearer-prose")
                        File.WriteAllText(Path.Combine(root, Package + "synthetic-control.log"), "sent as Bearer authentication to https://api.meta.ai/v1; Authorization Bearer header over HTTPS");
                    else if (test == "positive-ancestor")
                        record["commit"] = Git(root, "-c", "user.name=Synthetic", "-c", "user.email=synthetic@example.invalid",
                            "-c", "commit.gpgsign=false", "commit-tree", (string)record["tree_hash"], "-p", (string)record["commit"], "-m", "SYNTHETIC descendant").Trim();
                    else if (test == "positive-gaps")
                    {
                        string[] gaps = { "denied", "unprovisioned", "no-key", "unavailable" };
                        for (int s = 0; s < Services.Length; s++)
                        {
                            services[s]["verdict"] = "typed-gap:" + gaps[s]; services[s]["typed_gap"] = gaps[s];
                            if (s >= 2) { services[s]["rows"] = new object[0]; continue; }
                            string path = Package + "sitting/synthetic-" + Codes[s] + ".jsonl";
                            string text = File.ReadAllText(Path.Combine(root, path)).Replace("\"Applied\"", s == 0 ? "\"Denied\"" : "\"Unprovisioned\"")
                                .Replace("\"status\":200", s == 0 ? "\"status\":403" : "\"status\":404");
                            File.WriteAllText(Path.Combine(root, path), text); services[s]["rows"] = new[] { Reference(root, path) };
                        }
                    }
                    else
                    {
                        services[3]["verdict"] = "deferred-local-fallback"; services[3]["rows"] = new object[0];
                        string path = Path.Combine(root, Package + "sitting/sitting.json");
                        File.WriteAllText(path, File.ReadAllText(path).Replace(",\"d1-ultra\"", ""));
                    }
                    Write(root, Record, record); Validate(root);
                    Console.WriteLine("PASS synthetic compatibility " + test); continue;
                }
                string expected;
                if (test.StartsWith("a-", StringComparison.Ordinal) || test == "d")
                {
                    string path = Package + "sitting/synthetic-IMAGE.jsonl";
                    string text = File.ReadAllText(Path.Combine(root, path));
                    expected = "LA-3.LiveTransport";
                    if (test == "d")
                    {
                        string unrelated = Git(root, "-c", "user.name=Synthetic", "-c", "user.email=synthetic@example.invalid",
                            "-c", "commit.gpgsign=false", "commit-tree", (string)record["tree_hash"], "-m", "SYNTHETIC unrelated").Trim();
                        Need(unrelated != (string)record["commit"], "LA-Fixture.UnrelatedCommit");
                        text = text.Replace((string)record["commit"], unrelated); expected = "LA-6.GitObjectOrAncestry";
                    }
                    else text = text.Replace("\"transport_kind\":\"live\"", "\"transport_kind\":\"" + test.Substring(2) + "\"");
                    File.WriteAllText(Path.Combine(root, path), text);
                    services[0]["rows"] = new[] { Reference(root, path) };
                }
                else if (test == "b") { services.RemoveAt(0); expected = "LA-2.ServiceSet"; }
                else if (test.StartsWith("c-", StringComparison.Ordinal))
                {
                    string value = test == "c-bearer" ? "Bearer " + new string('X', 32)
                        : test == "c-bearer-16" ? "Bearer " + new string('X', 16)
                        : test == "c-key" ? "MODEL_API_KEY=" + new string('X', 32)
                        : test == "c-key-run" ? "key: " + new string('a', 64)
                        : test == "c-adjacent-hex" ? "key " + new string('a', 32)
                        : test == "c-adjacent-base64" ? "key " + new string('X', 48)
                        : "{\"debug\":\"\\u0042earer " + new string('X', 32) + "\"}";
                    string relative = Package + "unreferenced/deep/synthetic." + (test == "c-escaped" ? "json" : "dat");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative)));
                    File.WriteAllText(Path.Combine(root, relative), value); expected = "LA-5.SecretScan";
                }
                else
                {
                    string path = Package + "offline/D1-late.log";
                    string text = File.ReadAllText(Path.Combine(root, path));
                    int from = text.IndexOf("after=", StringComparison.Ordinal), to = text.IndexOf('\n', from);
                    File.WriteAllText(Path.Combine(root, path), text.Substring(0, from) + "after=synthetic-replaced-by-model" + text.Substring(to));
                    foreach (Dictionary<string, object> item in (List<object>)record["offline"])
                        if ((string)item["service"] == "d1-ultra" && (string)item["fault"] == "late") item["log"] = Reference(root, path);
                    expected = "LA-4.Replay.D1.late";
                }
                Write(root, Record, record);
                string actual = null;
                try { Validate(root); } catch (OracleFailure failure) { actual = failure.Id; }
                Need(actual == expected, "LA-Fixture.TargetedRejection." + test);
                Console.WriteLine("FAIL negative-fixture (" + test + ") " + actual + " [expected synthetic rejection]");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine("PASS all synthetic controls and targeted negative rejections");
        return 1; // Deliberately red; never emit the package success marker here.
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "negative-fixture") return Negatives();
            if (args.Length == 3 && args[0] == "offline-injection")
            {
                int service = Array.IndexOf(Services, args[1]);
                Need(service >= 0 && Array.IndexOf(Faults, args[2]) >= 0, "LA-INJECT.Arguments");
                Console.Write(Inject(service, args[2])); return 0;
            }
            Need(args.Length == 0, "LA.Arguments");
            Validate(Directory.GetCurrentDirectory());
            Console.WriteLine("LIVE-AI ALL PASS"); return 0;
        }
        catch (OracleFailure failure) { Console.WriteLine("FAIL " + failure.Id); return 1; }
        // Evidence and exception messages may contain secrets: report the type only.
        catch (Exception failure) { Console.WriteLine("FAIL LA.EvidenceOrHarness." + failure.GetType().Name); return 1; }
    }
}
