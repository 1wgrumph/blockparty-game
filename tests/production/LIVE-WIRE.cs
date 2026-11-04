/* Review-repair 2: F1/F2/F3 and owner A1/A2, baseline 1e248496.
New public static contracts required on Blockparty.AI.LiveWire (plain C#):
 void AdvanceSitting(ProposalBoundary boundary, ImageAdapter image, double elapsedRealSeconds)
   Same once-per-frame shared clock as the existing overload, plus Image pending
   cleanup at its own 30 real-second deadline while HTTP is still pending.
   Never tick the shared clock twice. Unity PumpElapsed must call this overload.
 bool VoiceCanSendAudio(string firstFrame, bool sitting,
     Action<string,string,string> reportDiagnostic, out string terminalFrame)
   Callback arguments: sanitized event type, errorType, errorCode, extracted from
   the original first frame before closing it. Exactly one callback for a sitting
   first-frame error; none outside sitting. Credential-shaped fields are empty.
   Preserve the existing no-audio bool and sanitized terminal frame. Unity must
   use this overload and route its callback to the sitting diagnostic sink.
 string[] SittingServices(bool od4Open)
   With OD-4 open, exactly Muse Image, Muse Voice Transcribe, SAM 3.1; no d1-ultra.
 bool BeginSittingDecision(bool od4Open, Action send, Action<string> reportDeferred)
   With OD-4 open: false, zero sends, one deferred-local-fallback record callback.
   Unity sitting dispatch must consume this plan/gate, not a parallel hardcoded list.
No closed-OD4 activation contract is asserted. Missing extractions are typed
MissingContract, never a behavioral PASS. F1 falls back ONLY to the existing
AdvanceSitting(boundary, delta) to expose its current real cleanup defect.
Image pending cleanup is observed through late Complete returning DuplicateIgnored
and preserving the exact already-installed content object, not private state.
A1 also exercises the real boundary/adapters, independent of the new pump seam.
Only old Image Late and R2-LateImageRow deadline INPUTS change (10 -> 30 s);
all pre-existing assertions are unchanged. IMAGE.cs's 10 s timeout case is outside
this write set and will need A1 alignment. No fake parser/consumer or live I/O.
Resource group: isolated plain C#, fixture reads and existing owned exporter temp.
Standalone excludes Presentation: these contracts require subsequent Unity wiring
verification by the product author; these oracles cannot certify that wiring.
*/
/* LW-LIVE-* documentation-contract amendment against 6755785d.
Fixture provenance/parameter substitutions: fixtures/LIVE-WIRE/README.md.
LW-LIVE-SAM-Nonempty and NullError are behavioral reds: successful Responses
error=null must not be terminal, and the complete two-object line must become
two capture-bound outlines, not Malformed. Masks are not elided.
Image PNG request/success, Voice session/final/speechComplete and HTTP 400 cases
are compatibility controls; a passing contract must not be made artificially red.
Meta docs define no d1-ultra success shape: NEEDS_EVIDENCE, no invented fixture.
Presentation-only builders cannot run here. Required extraction signatures:
 public static string BuildSamRequest(string modelId, string phrase, string imageUrl)
 public static string VoiceHandshake(string key, string modelId)
Both belong to LiveWire; preserve caller-supplied capture URL and the documented
Voice handshake fields. Never pass a real key to this suite. No decision builder
contract is asserted until the provider/endpoint conflict is resolved. Image's
existing plain-C# ImageAdapter.Payload already exposes its real request builder.
Resource group unchanged: offline plain C#, fixture reads, no live transport.
All pre-existing checks remain unchanged; these cases run after their summaries.
*/
/* Review-repair oracle amendment against 692038ea; synthetic/offline only.
New case -> finding mapping:
 LW-R1-ProviderId -> R1; LW-R1-Exporter -> R1
 LW-R2-DecisionRow, LW-R2-LateImageRow -> R2
 LW-R3-SittingDeadline -> R3
 LW-R4-FirstFrameDenied -> R4
 LW-R5-Cancel-{BeforeConnect,Acknowledgement,FinalTranscript} -> R5
 LW-R6-NoKey, LW-R6-TypedUnprovisioned -> R6
 LW-R7-EvidencePaths -> R7

Required NEW public static members of Blockparty.AI.LiveWire (plain C#):
 string SanitizeProviderId(string value, int maxLength)
   Reject credential-shaped IDs entirely; retain ordinary correlation IDs.
 GatewayOutcome? SittingOutcome(ProposalBoundary boundary, GatewayRequestId id)
   Null until settled; use this request's terminal boundary record, never parser
   success or a different request's record. Late/duplicate delivery cannot relabel it.
 void AdvanceSitting(ProposalBoundary boundary, double elapsedRealSeconds)
   Advance measured real-time DELTA once for the shared boundary and tick expiry
   before delivering replies. Must not advance separately for each adapter.
 bool VoiceCanSendAudio(string firstFrame, out string terminalFrame)
   Acknowledge session frames only; terminal first frames return false and a
   sanitized terminal frame. Unity I/O must honor false and send no audio.
 GatewayOutcome VoiceTerminal(bool connected, bool deadlineCancelled,
     string body, out string frame)
   Deadline cancellation is Timeout in every connection phase; otherwise parse
   actual content, retaining Denied/Unprovisioned rather than an empty success.
 ProposalVerdict DeliverVoice(VoiceAdapter adapter, ProposalBoundary boundary,
     GatewayRequestId id, GatewayOutcome outcome, string frame)
   Deliver the typed outcome through the real adapter/boundary exactly once;
   Unprovisioned must not become Malformed via the old error-message vocabulary.
 bool BeginVoice(VoiceAdapter adapter, ProposalBoundary boundary,
     GatewayRequestId id, bool hasCredential, Action send, Action reportNoKey)
   With absent key: settle Unprovisioned, fixed NoKey callback once, no send,
   false (caller must stop without a sitting row). With a key: send once, true.
Presentation/LiveTransport.cs must call these members; this suite cannot prove
that Unity wiring because run-standalone excludes Presentation. Missing members
are reported explicitly as MissingContract, never misreported as an observed
behavior failure. No substitute implementation is embedded in this oracle.
R6 additionally exercises today's ParseVoice(404) through the real adapter.
Exporter runs the actual CLI against a committed synthetic corpus, in an owned
unique temp directory; both process streams and output file are checked without
printing their contents. Evidence scanning reads package text only, and prints
no matched paths. Resource group: isolated-csharp + owned temporary files/process;
no sockets, credentials, Unity engine, wall-clock sleeps or live calls.
Existing checks below are unchanged. Review cases run AFTER their summary so an
expected review red cannot make the pre-existing FreshSessions check fail.
*/
/* LIVE-WIRE@1 oracle-only contract (issue #246, full-game-rev2-m5-1).
Product: Assets/Blockparty/AI/LiveWire.cs, namespace Blockparty.AI:
 public static class LiveWire {
   public static DecisionResponse ParseDecision(GatewayRequestId requestId,
       int status, string body);
   public static GatewayOutcome ParseImage(int status, string body, out string frame);
   public static GatewayOutcome ParseVoice(int status, string body, out string frame);
   public static GatewayOutcome ParseSam(int status, string body, CaptureId capture,
       out string frame);
 }
No I/O, credentials or mutable static state. Reflection binds these exact public
signatures, so the oracle itself compiles before the product exists, without a
stub parser. Only external response bodies are synthetic; all consumers are real.
Reach: this suite and red.log only; no registration/config/export/product edits.

Fixture mapping table (all bodies below SYNTHETIC, never provider receipts):
 service   status/body                                  outcome / consumer
 Image     200 data[0].b64_json PNG                      Applied / ImageAdapter.Complete
 Voice     200 final transcript "pause"                 Applied / VoiceAdapter.Complete
 SAM       200 completed Responses output_text ""        Applied / CaptureSearch.Complete
 d1-ultra  200 answers.activity.choice ACT-FOOD-02       Applied / DecisionResponse
 All       200 broken JSON or unusable success shape    Malformed / local alternative
 All       403 error body; 200 permission_denied error  Denied / local alternative
 All       valid body AFTER local deadline              parses Applied, consumer discards
Image/SAM failure frame is null. Voice failure frame is a closed, payload-free
error frame consumable by VoiceAdapter: Denied -> permission error, Malformed ->
malformed error (its message must not contain permission/timeout). Success frames
contain only adapter-required fields, never a copy of the body/unknown metadata.
SAM success binds the caller's capture into CONTRACT-BP-07 {capture,outlines:[]}.
d1 choice is a local synthetic named-question contract, not an observed provider
schema; TargetId is null here. Invalid choices never escape in ActivityCode.
Success queues one ModelProposed ACT-FOOD-02; faults retain Autonomous local work.
A canary in a success metadata/error field must not appear in any parser output,
consumer output, boundary log or console. A canary as recognized content is
Malformed with no echoed value. No fixture contains an actual key.

Provenance: docs/production/vendors/{img,voice,sam,d1u}.json, PROBE-VOICE.cs,
IMAGE.cs, SAM.cs and D1U.cs. SAM's vendor record explicitly permits a completed
empty output_text lane; no fabricated polygons/undocumented mask encoding.
Nonempty SAM mask decoding, other d1 answer shapes/targets, provider compatibility,
partial/stream assembly, live I/O and key handling are NOT proved here. Presentation
I/O belongs to UNITY-COMPILE rebind; export script and connected sittings remain
product/later-gate work. No result.json is authored in this TDD red step.

Local oracle IDs -> SEIT (parents AC-21, BP-IFC-01, BP-VER-01):
 LW-IMAGE-{Success,Malformed,Denied,Late}: SEIT-IF-IMG-P1/N1/N3,
   SEIT-AI-07-N1/N2/N4 (local content/disclosure only).
 LW-VOICE-{Success,Malformed,Denied,Late}: SEIT-IF-VOICE-P1/N2,
   SEIT-AI-10-P1/N1 (local command/failure only, not ten manual/UI paths).
 LW-SAM-{Success,Malformed,Denied,Late}: SEIT-AI-12-P1, SEIT-IF-SAM-N1/N3
   (empty result and failure, not nonempty outlines or rendered UI).
 LW-D1-{Success,Malformed,Denied,Late}: SEIT-AI-02-P1, SEIT-AI-04-N1/N4/N6,
   SEIT-IF-D1U-N3, SEIT-SM-AI-I6 (real fallback queue preserved).
 LW-*-Secret: local support for SEIT-SECU-01-N1; NOT repository scanner acceptance.
 LW-Repeat: deterministic repeated fresh sessions; no persistent String.GetHashCode.
Whole SEIT procedures/acceptance remain pending. New module is required by the
package's generic selector (M5-D1/D-1), rather than extending another package gate.
Resource isolated-csharp; no engine/display/ports/files/sleeps. Timeout uses
GatewayLimits, not a chosen timing threshold. Seeds, capture size and IDs are
fixture data, not numeric acceptance limits. Two fresh passes exercise repeated
entry at minimal cost; this is not a Unity domain-reload qualification.
negative-fixture replaces the positive Image body with its malformed twin; the
unchanged Applied/content oracles must print FAIL and suppress ALL PASS.
*/
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Simulation;

public sealed class LiveWireChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
    private const string ImageBody = "{\"data\":[{\"b64_json\":\"" + Png + "\"}]}";
    private const string VoiceBody = "{\"type\":\"transcript\",\"transcript\":\"pause\",\"final\":true,\"audioProcessedMs\":3600}";
    private const string SamBody = "{\"id\":\"synthetic-sam\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"\"}]}]}";
    private const string DecisionBody = "{\"model\":\"d1-ultra\",\"answers\":{\"activity\":{\"choice\":\"ACT-FOOD-02\"}}}";
    private int failures;
    private Type wire;
    private readonly string canary = "sk-" + new string('X', 32);

    private void Check(bool ok, string id)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + id);
        if (!ok) failures++;
    }
    private static void Require(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException(reason); }
    private void Run(string id, Action test)
    {
        try { test(); }
        // Never print exception messages: a parser exception can contain the raw body.
        catch (Exception) { Check(false, id + ".UnexpectedException"); }
    }
    private object Invoke(string name, Type returns, Type[] parameters, object[] args)
    {
        MethodInfo method = wire.GetMethod(name, BindingFlags.Public | BindingFlags.Static,
            null, parameters, null);
        Require(method != null && method.ReturnType == returns, "LIVE-WIRE signature missing");
        return method.Invoke(null, args);
    }
    private GatewayOutcome Parse(GatewayPurpose purpose, int status, string body, out string frame)
    {
        bool sam = purpose == GatewayPurpose.ImageSearch;
        string method = sam ? "ParseSam" : purpose == GatewayPurpose.ImageGeneration ? "ParseImage" : "ParseVoice";
        Type[] types = sam
            ? new[] { typeof(int), typeof(string), typeof(CaptureId), typeof(string).MakeByRefType() }
            : new[] { typeof(int), typeof(string), typeof(string).MakeByRefType() };
        object[] args = sam ? new object[] { status, body, (CaptureId)41L, null }
            : new object[] { status, body, null };
        GatewayOutcome outcome = (GatewayOutcome)Invoke(method, typeof(GatewayOutcome), types, args);
        frame = (string)args[args.Length - 1];
        return outcome;
    }
    private DecisionResponse ParseDecision(GatewayRequestId id, int status, string body)
    {
        return (DecisionResponse)Invoke("ParseDecision", typeof(DecisionResponse),
            new[] { typeof(GatewayRequestId), typeof(int), typeof(string) }, new object[] { id, status, body });
    }
    private bool Safe(string text)
    {
        if (text == null) return true;
        foreach (string marker in new[] { canary, "Authorization", "Bearer ", "MODEL_API_KEY=", "api_key" })
            if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }
    private string Metadata(string body)
    {
        return body.Substring(0, body.Length - 1) + ",\"Authorization\":\"Bearer " + canary
            + "\",\"api_key\":\"" + canary + "\",\"debug\":\"MODEL_API_KEY=" + canary + "\"}";
    }
    private string DeniedBody(bool voice)
    {
        return voice ? "{\"type\":\"error\",\"message\":\"Permission denied; Bearer " + canary + "\"}"
            : "{\"error\":{\"code\":\"permission_denied\",\"message\":\"Authorization: Bearer " + canary + "\"}}";
    }
    private static ServiceEndpoint Endpoint(string model)
    { return new ServiceEndpoint("synthetic-" + model, "https://example.invalid/offline", model, true); }

    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 42L, 1, new[] { "Ada" });
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly Autonomy Autonomy;
        public readonly ProposalBoundary Boundary;
        public readonly ResidentRecord Resident;
        public Fixture()
        {
            Resident = World.ResidentsSnapshot()[0];
            for (int i = 0; i < StateSchema.NeedCount; i++) World.SetNeed(Core, Resident.Id, (Need)i, 80f);
            World.SetNeed(Core, Resident.Id, Need.Hunger, 8f);
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Autonomy = new Autonomy(World, Commands);
            Boundary = new ProposalBoundary(World, Clock, Commands, Autonomy);
        }
    }
    private static bool Logged(Fixture f, GatewayOutcome outcome)
    {
        foreach (ProposalLogEntry entry in f.Boundary.DecisionLog)
            if (entry.Outcome == outcome) return true;
        return false;
    }
    private bool SafeLog(Fixture f)
    {
        foreach (ProposalLogEntry entry in f.Boundary.DecisionLog)
            if (!Safe(entry.Detail) || !Safe(entry.ModelId)) return false;
        return true;
    }
    private static WorkItem MakeArt(Fixture f, Creative creative)
    {
        f.World.SetMoney(Core, f.Resident.Id, 100L);
        f.World.AddSkillPoints(Core, f.Resident.Id, "Creativity", 1000);
        WorldEvent memory = f.World.AppendEvent(Core, "help", f.Resident.Id, "carried a box");
        f.World.AddMemory(Core, f.Resident.Id, new MemoryEntry(memory.Event, "help", 0L,
            f.Resident.Home, new[] { f.Resident.Id }, "carried a box", "participant"));
        var executor = new ActivityExecutor(f.World, f.Commands, f.Clock);
        ObjectId station;
        do { station = f.World.AllocateObject(Core); } while ((long)station <= 2L);
        executor.RegisterObject(station, new[] { new ApproachPose(0f, 0f, 0f) });
        executor.SetResidentPosition(f.Resident.Id, new ApproachPose(0f, 0f, 0f));
        CommandResult command = f.Commands.Submit(new CommandEnvelope(f.World.AllocateCommand(Core),
            f.World.Epoch, f.World.GameSeconds, CommandSource.PlayerHuman,
            f.Resident.ResidentSequence + 1u, CommandKind.DirectActivity, f.Resident.Id,
            new ActivityId(CreativeWork.MakeWallArtActivity), (long)station,
            new CommandParameters.Direct(QueueMode.Immediate)));
        Require(command.Accepted, "fixture art command");
        ActionInstanceId action = f.Commands.GetCurrent(f.Resident.Id).Action;
        executor.Tick(Exec); executor.Tick(Exec);
        Require(executor.GetExecution(action).Stage == ExecutionStage.Perform, "fixture art perform");
        f.Clock.AdvanceRealSeconds(Core, 90.0); // IMAGE.cs real MakeWallArt fixture duration.
        executor.Tick(Exec);
        Require(executor.GetExecution(action).Outcome == TerminalOutcome.Completed, "fixture art complete");
        Require(creative.Complete(Exec, action, new ActivityId(CreativeWork.MakeWallArtActivity),
            f.Resident.Id, memory.Event), "fixture real WorkItem");
        return creative.WorkItems[0];
    }
    private void Image(string fault, bool corrupt)
    {
        var f = new Fixture(); var creative = new Creative(f.World);
        WorkItem item = MakeArt(f, creative);
        var adapter = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest request = adapter.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(request != null, "image request");
        string body = fault == "Malformed" || corrupt ? "{\"data\":[]}" : ImageBody;
        int status = fault == "Denied" ? 403 : 200;
        if (fault == "Denied" || fault == "DeniedBody") body = DeniedBody(false);
        if (fault == "Late") adapter.Tick(30.0); // A1: Image alone has a 30 s deadline.
        ImageContent before = adapter.GetContent(item.Reference.Item);
        GatewayOutcome mapped = Parse(GatewayPurpose.ImageGeneration, status, Metadata(body), out string frame);
        GatewayOutcome expected = fault == "Malformed" ? GatewayOutcome.Malformed
            : fault.StartsWith("Denied", StringComparison.Ordinal) ? GatewayOutcome.Denied : GatewayOutcome.Applied;
        string id = "LW-IMAGE-" + fault;
        Check(mapped == expected, id + ".Outcome");
        Check(mapped == GatewayOutcome.Applied || frame == null, id + ".FailureFrame");
        ProposalVerdict verdict = mapped == GatewayOutcome.Applied ? adapter.Complete(request.Id, frame)
            : adapter.Fail(request.Id, mapped);
        ImageContent after = adapter.GetContent(item.Reference.Item);
        bool local = fault != "Success";
        Check(after != null && after.Path == (local ? "local-alternative" : "generated")
            && (local ? after.Disclosure.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0 : after.ImageBase64 == Png), id + ".Content");
        Check(f.Boundary.PendingCount == 0 && Logged(f, local ? fault == "Late" ? GatewayOutcome.Timeout : expected : GatewayOutcome.Applied)
            && (!local || !Logged(f, GatewayOutcome.Applied)), id + ".Boundary");
        if (fault == "Late") Check(before != null && object.ReferenceEquals(before, after)
            && verdict != ProposalVerdict.Applied, id + ".LocalContentPreserved");
        Check(Safe(frame) && SafeLog(f) && after != null && Safe(after.Disclosure), id + ".Secret");
    }
    private void Voice(string fault)
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice request");
        string body = fault == "Malformed" ? "{\"type\":\"transcript\",\"final\":true}" : VoiceBody;
        if (fault == "Denied" || fault == "DeniedBody") body = DeniedBody(true);
        if (fault == "Late")
        {
            f.Boundary.AdvanceRealSeconds(GatewayLimits.ServiceTimeoutRealSeconds);
            f.Boundary.Tick(); // Voice's manual alternative executes no command.
            Require(Logged(f, GatewayOutcome.Timeout), "voice timeout established");
        }
        int before = f.Commands.CommandLog.Count;
        GatewayOutcome mapped = Parse(GatewayPurpose.VoiceTranscription, fault == "Denied" ? 403 : 200,
            Metadata(body), out string frame);
        GatewayOutcome expected = fault == "Malformed" ? GatewayOutcome.Malformed
            : fault.StartsWith("Denied", StringComparison.Ordinal) ? GatewayOutcome.Denied : GatewayOutcome.Applied;
        string id = "LW-VOICE-" + fault;
        Check(mapped == expected, id + ".Outcome");
        ProposalVerdict verdict = adapter.Complete(request.Id, frame);
        bool success = fault == "Success";
        Check(success ? verdict == ProposalVerdict.Applied && f.Clock.Paused
            && f.Commands.CommandLog.Count == before + 1 && adapter.Transcript.Text == "pause"
            && adapter.LastCommand != null && adapter.LastCommand.Source == CommandSource.VoiceMapped
            : verdict != ProposalVerdict.Applied && !f.Clock.Paused && f.Commands.CommandLog.Count == before,
            id + ".CommandOrManualAlternative");
        Check(f.Boundary.PendingCount == 0 && Logged(f, fault == "Late" ? GatewayOutcome.Timeout : expected)
            && (success || !Logged(f, GatewayOutcome.Applied)), id + ".Boundary");
        Check(Safe(frame) && SafeLog(f) && Safe(adapter.Transcript.Text) && Safe(adapter.Meaning)
            && Safe(adapter.Prompt), id + ".Secret");
    }
    private void Sam(string fault)
    {
        var f = new Fixture(); var adapter = new CaptureSearch(f.Boundary);
        // Same synthetic 100x100 capture/41 id as SAM.cs; no acceptance size limit.
        adapter.SetCapture((CaptureId)41L, new byte[100 * 100 * 4], 100, 100);
        GatewayRequest request = adapter.Request("chair", Endpoint("sam-3.1"), true, true);
        Require(request != null, "sam request");
        string body = fault == "Malformed" ? "{\"status\":\"completed\",\"output\":42}" : SamBody;
        if (fault == "Denied" || fault == "DeniedBody") body = DeniedBody(false);
        if (fault == "Late") adapter.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
        int misses = adapter.Misses.Count;
        GatewayOutcome mapped = Parse(GatewayPurpose.ImageSearch, fault == "Denied" ? 403 : 200,
            Metadata(body), out string frame);
        GatewayOutcome expected = fault == "Malformed" ? GatewayOutcome.Malformed
            : fault.StartsWith("Denied", StringComparison.Ordinal) ? GatewayOutcome.Denied : GatewayOutcome.Applied;
        string id = "LW-SAM-" + fault;
        Check(mapped == expected, id + ".Outcome");
        Check(mapped == GatewayOutcome.Applied || frame == null, id + ".FailureFrame");
        ProposalVerdict verdict = mapped == GatewayOutcome.Applied ? adapter.Complete(request.Id, frame)
            : adapter.Fail(request.Id, mapped);
        Check(fault == "Success" ? verdict == ProposalVerdict.Applied && adapter.Result != null
            && adapter.Result.Outlines.Count == 0 && adapter.Misses.Count == misses + 1
            && adapter.Misses[misses].Capture == (CaptureId)41L
            : verdict != ProposalVerdict.Applied && adapter.Result == null && adapter.Misses.Count == misses,
            id + ".ResultOrLocalAlternative");
        Check(adapter.CountText == "0" && f.Boundary.PendingCount == 0
            && Logged(f, fault == "Late" ? GatewayOutcome.Timeout : expected)
            && (fault == "Success" || !Logged(f, GatewayOutcome.Applied)), id + ".Boundary");
        Check(Safe(frame) && SafeLog(f) && Safe(adapter.Message), id + ".Secret");
    }
    private sealed class Inbox : IDecisionTransport
    {
        // Only external I/O is replaced; parser/boundary/adapter/fallback are real.
        public DecisionResponse Reply;
        public void Send(GatewayRequest request, DecisionPayload payload) { }
        public bool TryReceive(out DecisionResponse response)
        { response = Reply; Reply = null; return response != null; }
    }
    private void Decision(string fault)
    {
        var f = new Fixture(); var inbox = new Inbox(); var adapter = new DecisionAdapter(f.Boundary, inbox);
        GatewayRequest request = adapter.RequestDecision(f.Resident.Id, Endpoint("d1-ultra"), true, true);
        Require(request != null, "decision request");
        if (fault == "Late") adapter.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
        ActionEntry[] before = f.Commands.GetPending(f.Resident.Id);
        if (fault == "Late") Require(f.Autonomy.Decisions.Count == 1 && before.Length == 1
            && before[0].Owner == ActionOwner.Autonomous, "real local fallback applied");
        string body = fault == "Malformed" ? "{\"answers\":{\"activity\":{\"choice\":42}}}" : DecisionBody;
        if (fault == "Denied" || fault == "DeniedBody") body = DeniedBody(false);
        DecisionResponse response = ParseDecision(request.Id, fault == "Denied" ? 403 : 200, Metadata(body));
        GatewayOutcome expected = fault == "Malformed" ? GatewayOutcome.Malformed
            : fault.StartsWith("Denied", StringComparison.Ordinal) ? GatewayOutcome.Denied : GatewayOutcome.Applied;
        string id = "LW-D1-" + fault;
        Check(response != null && response.RequestId == request.Id && response.Outcome == expected
            && (expected == GatewayOutcome.Applied ? response.ActivityCode == "ACT-FOOD-02"
                && response.TargetId == null : response.ActivityCode == null && response.TargetId == null), id + ".TypedResponse");
        Require(response != null, "decision response");
        inbox.Reply = response; adapter.Tick(0.0);
        ActionEntry[] after = f.Commands.GetPending(f.Resident.Id);
        Check(f.Boundary.PendingCount == 0 && Logged(f, fault == "Late" ? GatewayOutcome.Timeout : expected)
            && (fault == "Success" || !Logged(f, GatewayOutcome.Applied)), id + ".Boundary");
        Check(after.Length == 1
            && after[0].Owner == (fault == "Success" ? ActionOwner.ModelProposed : ActionOwner.Autonomous)
            && (fault != "Success" || after[0].Activity.Code == "ACT-FOOD-02")
            && f.Resident.GetNeed(Need.Hunger) == 8f && f.World.EffectLedger.Count == 0,
            id + ".OneAdvisoryActionNoDirectEffect");
        if (fault == "Late") Check(after.Length == before.Length && after[0].Action == before[0].Action
            && after[0].Activity == before[0].Activity && after[0].TargetId == before[0].TargetId
            && f.Autonomy.Decisions.Count == 1, id + ".AppliedLocalFallbackPreserved");
        Check(Safe(response.ActivityCode) && SafeLog(f), id + ".Secret");
    }
    private void PoisonAndBrokenJson()
    {
        foreach (GatewayPurpose purpose in new[] { GatewayPurpose.ImageGeneration,
            GatewayPurpose.VoiceTranscription, GatewayPurpose.ImageSearch })
        {
            string poison = purpose == GatewayPurpose.ImageGeneration ? ImageBody.Replace(Png, canary)
                : purpose == GatewayPurpose.VoiceTranscription ? VoiceBody.Replace("pause", "Bearer " + canary)
                : SamBody.Replace("\"text\":\"\"", "\"text\":\"Bearer " + canary + "\"");
            foreach (string body in new[] { "{", poison })
            {
                GatewayOutcome outcome = Parse(purpose, 200, body, out string frame);
                Check(outcome == GatewayOutcome.Malformed && Safe(frame), "LW-" + purpose + "-Secret.MalformedContent");
            }
        }
        foreach (string body in new[] { "{", DecisionBody.Replace("ACT-FOOD-02", "Bearer " + canary) })
        {
            DecisionResponse response = ParseDecision((GatewayRequestId)1L, 200, body);
            Check(response != null && response.Outcome == GatewayOutcome.Malformed
                && response.ActivityCode == null && response.TargetId == null,
                "LW-D1-Secret.MalformedContent");
        }
    }
    private void FixtureControls(bool negativeFixture)
    {
        // Qualify consumer expectations before binding the absent product; no fake parser.
        foreach (string body in new[] { ImageBody, VoiceBody, SamBody, DecisionBody })
            using (JsonDocument doc = JsonDocument.Parse(body))
                Require(doc.RootElement.ValueKind == JsonValueKind.Object, "synthetic body is JSON object");
        var image = new Fixture(); var creative = new Creative(image.World);
        WorkItem item = MakeArt(image, creative);
        var ia = new ImageAdapter(image.World, image.Boundary, creative);
        GatewayRequest ir = ia.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        bool validImage = ir != null && ia.Complete(ir.Id, negativeFixture ? "{\"data\":[]}" : ImageBody) == ProposalVerdict.Applied
            && ia.GetContent(item.Reference.Item).ImageBase64 == Png;
        Check(validImage, "LW-FixtureControls.ImagePositive");
        if (!validImage) return;
        var voice = new Fixture(); var va = new VoiceAdapter(voice.World, voice.Boundary, voice.Commands);
        GatewayRequest vr = va.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(vr != null && va.Complete(vr.Id, VoiceBody) == ProposalVerdict.Applied && voice.Clock.Paused,
            "real voice control");
        var sam = new Fixture(); var sa = new CaptureSearch(sam.Boundary);
        sa.SetCapture((CaptureId)41L, new byte[100 * 100 * 4], 100, 100);
        GatewayRequest sr = sa.Request("chair", Endpoint("sam-3.1"), true, true);
        Require(sr != null && sa.Complete(sr.Id, "{\"capture\":41,\"outlines\":[]}") == ProposalVerdict.Applied
            && sa.CountText == "0" && sa.Misses.Count == 1, "real sam control");
        var success = new Fixture(); var inbox = new Inbox();
        var positive = new DecisionAdapter(success.Boundary, inbox);
        GatewayRequest dr = positive.RequestDecision(success.Resident.Id, Endpoint("d1-ultra"), true, true);
        Require(dr != null && Array.IndexOf(success.Boundary.BuildDecisionPayload(success.Resident.Id)
            .AvailableActivityIds, "ACT-FOOD-02") >= 0, "real offered decision choice");
        inbox.Reply = new DecisionResponse(dr.Id, GatewayOutcome.Applied, "ACT-FOOD-02", null);
        positive.Tick(0.0);
        Require(Logged(success, GatewayOutcome.Applied)
            && success.Commands.GetPending(success.Resident.Id).Length == 1
            && success.Commands.GetPending(success.Resident.Id)[0].Activity.Code == "ACT-FOOD-02",
            "real decision positive control");
        var decision = new Fixture(); var adapter = new DecisionAdapter(decision.Boundary, new Inbox());
        Require(adapter.RequestDecision(decision.Resident.Id, Endpoint("d1-ultra"), true, true) != null,
            "real decision control");
        adapter.Tick(GatewayLimits.ServiceTimeoutRealSeconds);
        Require(decision.Autonomy.Decisions.Count == 1 && decision.Commands.GetPending(decision.Resident.Id).Length == 1,
            "real local fallback control");
        Check(true, "LW-FixtureControls.RealConsumersAndFallback");
    }
    private void Execute(bool negativeFixture)
    {
        Run("LW-FixtureControls", () => FixtureControls(negativeFixture));
        wire = typeof(ProposalBoundary).Assembly.GetType("Blockparty.AI.LiveWire");
        if (wire == null)
        {
            Check(false, "LW-Contract.MissingDeliverable.Blockparty.AI.LiveWire");
            return;
        }
        TextWriter original = Console.Out;
        TextWriter originalError = Console.Error;
        using (var captured = new StringWriter())
        {
            try
            {
                Console.SetOut(captured);
                Console.SetError(captured);
                // Fresh instances both times: deterministic IDs/fixtures, no static session state.
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (string fault in new[] { "Success", "Malformed", "Denied", "DeniedBody", "Late" })
                    {
                        Run("LW-IMAGE-" + fault, () => Image(fault, negativeFixture && fault == "Success"));
                        Run("LW-VOICE-" + fault, () => Voice(fault));
                        Run("LW-SAM-" + fault, () => Sam(fault));
                        Run("LW-D1-" + fault, () => Decision(fault));
                    }
                    Run("LW-Secret", PoisonAndBrokenJson);
                }
            }
            finally { Console.SetOut(original); Console.SetError(originalError); }
            string output = captured.ToString();
            // Do not publish captured parser output if it contains the synthetic secret.
            bool safe = Safe(output);
            if (safe) Console.Write(output);
            Check(safe, "LW-All-Secret.Console");
        }
        Check(failures == 0, "LW-Repeat.FreshSessions");
    }
    private object ReviewInvoke(string name, Type returns, Type[] types, params object[] args)
    {
        MethodInfo method = wire.GetMethod(name, BindingFlags.Public | BindingFlags.Static,
            null, types, null);
        if (method == null || method.ReturnType != returns) throw new MissingMethodException(name);
        return method.Invoke(null, args);
    }
    private void ReviewCase(string id, Action test)
    {
        try { test(); }
        catch (MissingMethodException e) { Check(false, id + ".MissingContract." + e.Message); }
        catch (Exception) { Check(false, id + ".UnexpectedException"); }
    }
    private GatewayOutcome? SittingOutcome(Fixture f, GatewayRequestId id)
    {
        return (GatewayOutcome?)ReviewInvoke("SittingOutcome", typeof(GatewayOutcome?),
            new[] { typeof(ProposalBoundary), typeof(GatewayRequestId) }, f.Boundary, id);
    }
    private void AdvanceSitting(Fixture f, double elapsed)
    {
        ReviewInvoke("AdvanceSitting", typeof(void),
            new[] { typeof(ProposalBoundary), typeof(double) }, f.Boundary, elapsed);
    }
    private ProposalVerdict DeliverVoice(Fixture f, VoiceAdapter adapter, GatewayRequestId id,
        GatewayOutcome outcome, string frame)
    {
        return (ProposalVerdict)ReviewInvoke("DeliverVoice", typeof(ProposalVerdict),
            new[] { typeof(VoiceAdapter), typeof(ProposalBoundary), typeof(GatewayRequestId),
                typeof(GatewayOutcome), typeof(string) }, adapter, f.Boundary, id, outcome, frame);
    }
    private static int TerminalCount(Fixture f, GatewayRequestId id)
    {
        int count = 0;
        foreach (ProposalLogEntry row in f.Boundary.DecisionLog)
            if (row.RequestId == (long)id) count++;
        return count;
    }
    private void ProviderIds()
    {
        foreach (string value in new[] { canary, "Bearer " + canary, "api_key=" + canary })
        {
            string safe = (string)ReviewInvoke("SanitizeProviderId", typeof(string),
                new[] { typeof(string), typeof(int) }, value, 128);
            Check(string.IsNullOrEmpty(safe), "LW-R1-ProviderId.RejectCredentialShape");
        }
        string ordinary = (string)ReviewInvoke("SanitizeProviderId", typeof(string),
            new[] { typeof(string), typeof(int) }, "req_abc123", 128);
        Check(ordinary == "req_abc123", "LW-R1-ProviderId.KeepCorrelation");
    }
    private void DecisionRow()
    {
        var f = new Fixture(); var inbox = new Inbox();
        var adapter = new DecisionAdapter(f.Boundary, inbox);
        GatewayRequest request = adapter.RequestDecision(f.Resident.Id, Endpoint("d1-ultra"), true, true);
        Require(request != null, "decision request");
        Require(Array.IndexOf(f.Boundary.BuildDecisionPayload(f.Resident.Id).AvailableActivityIds,
            "ACT-FOOD-99") < 0, "unoffered fixture");
        inbox.Reply = ParseDecision(request.Id, 200, DecisionBody.Replace("ACT-FOOD-02", "ACT-FOOD-99"));
        Require(inbox.Reply.Outcome == GatewayOutcome.Applied, "syntactically valid unoffered choice");
        adapter.Tick(0.0);
        Check(!Logged(f, GatewayOutcome.Applied) && f.Boundary.PendingCount == 0
            && f.Autonomy.Decisions.Count == 1, "LW-R2-DecisionRow.RealBoundaryRejects");
        GatewayOutcome? row = SittingOutcome(f, request.Id);
        ProposalLogEntry terminal = f.Boundary.DecisionLog[f.Boundary.DecisionLog.Count - 1];
        Check(row.HasValue && row.Value == terminal.Outcome && row != GatewayOutcome.Applied,
            "LW-R2-DecisionRow.SettledNotParserOutcome");
        Check(SittingOutcome(f, (GatewayRequestId)999999L) == null,
            "LW-R2-DecisionRow.UnknownHasNoRow");
    }
    private void LateImageRow()
    {
        var f = new Fixture(); var creative = new Creative(f.World);
        WorkItem item = MakeArt(f, creative);
        var adapter = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest request = adapter.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(request != null, "image request");
        adapter.Tick(30.0); // A1: retain the late-reply oracle at the amended deadline.
        ImageContent local = adapter.GetContent(item.Reference.Item);
        GatewayOutcome parsed = Parse(GatewayPurpose.ImageGeneration, 200, ImageBody, out string frame);
        Require(parsed == GatewayOutcome.Applied, "late image is valid");
        ProposalVerdict verdict = adapter.Complete(request.Id, frame);
        Check(verdict != ProposalVerdict.Applied && Logged(f, GatewayOutcome.Timeout)
            && object.ReferenceEquals(local, adapter.GetContent(item.Reference.Item)),
            "LW-R2-LateImageRow.LocalFallbackPreserved");
        Check(SittingOutcome(f, request.Id) == GatewayOutcome.Timeout,
            "LW-R2-LateImageRow.TimeoutNotParserApplied");
    }
    private void SittingDeadline()
    {
        var f = new Fixture(); var inbox = new Inbox();
        var adapter = new DecisionAdapter(f.Boundary, inbox);
        GatewayRequest request = adapter.RequestDecision(f.Resident.Id, Endpoint("d1-ultra"), true, true);
        Require(request != null, "sitting decision request");
        AdvanceSitting(f, GatewayLimits.ServiceTimeoutRealSeconds / 2.0);
        Check(f.Boundary.PendingCount == 1 && f.Autonomy.Decisions.Count == 0
            && SittingOutcome(f, request.Id) == null, "LW-R3-SittingDeadline.NotEarly");
        AdvanceSitting(f, GatewayLimits.ServiceTimeoutRealSeconds / 2.0);
        ActionEntry[] local = f.Commands.GetPending(f.Resident.Id);
        Check(Logged(f, GatewayOutcome.Timeout) && f.Boundary.PendingCount == 0
            && f.Autonomy.Decisions.Count == 1 && local.Length == 1
            && local[0].Owner == ActionOwner.Autonomous, "LW-R3-SittingDeadline.TimeoutOneFallback");
        inbox.Reply = ParseDecision(request.Id, 200, DecisionBody);
        adapter.Tick(0.0);
        AdvanceSitting(f, GatewayLimits.ServiceTimeoutRealSeconds);
        ActionEntry[] after = f.Commands.GetPending(f.Resident.Id);
        Check(after.Length == 1 && local.Length == 1 && after[0].Action == local[0].Action
            && f.Autonomy.Decisions.Count == 1 && !Logged(f, GatewayOutcome.Applied)
            && SittingOutcome(f, request.Id) == GatewayOutcome.Timeout,
            "LW-R3-SittingDeadline.LateReplyCannotReplaceFallback");
    }
    private void FirstFrameDenied()
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice request");
        object[] args = { "{\"type\":\"error\",\"message\":\"permission denied\",\"sessionId\":\"\"}", null };
        bool send = (bool)ReviewInvoke("VoiceCanSendAudio", typeof(bool),
            new[] { typeof(string), typeof(string).MakeByRefType() }, args);
        int audioSends = 0;
        if (send) audioSends++;
        string frame = (string)args[1];
        GatewayOutcome outcome = Parse(GatewayPurpose.VoiceTranscription, 200, frame, out string closed);
        DeliverVoice(f, adapter, request.Id, outcome, closed);
        DeliverVoice(f, adapter, request.Id, outcome, closed); // close/duplicate cannot settle twice.
        Check(audioSends == 0 && outcome == GatewayOutcome.Denied && Safe(frame)
            && Logged(f, GatewayOutcome.Denied) && SittingOutcome(f, request.Id) == GatewayOutcome.Denied
            && TerminalCount(f, request.Id) == 1 && f.Commands.CommandLog.Count == 0
            && !f.Clock.Paused && adapter.Transcript.Text == string.Empty,
            "LW-R4-FirstFrameDenied.NoAudioOneDeniedTerminal");
        object[] ack = { "{\"sessionId\":\"session_abc123\"}", null };
        Check((bool)ReviewInvoke("VoiceCanSendAudio", typeof(bool),
            new[] { typeof(string), typeof(string).MakeByRefType() }, ack)
            && ack[1] == null, "LW-R4-FirstFrameDenied.ValidAckCanSend");
    }
    private void CancelledVoice(string phase)
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice request");
        bool connected = phase != "BeforeConnect";
        object[] args = { connected, true, string.Empty, null };
        GatewayOutcome outcome = (GatewayOutcome)ReviewInvoke("VoiceTerminal", typeof(GatewayOutcome),
            new[] { typeof(bool), typeof(bool), typeof(string), typeof(string).MakeByRefType() }, args);
        DeliverVoice(f, adapter, request.Id, outcome, (string)args[3]);
        DeliverVoice(f, adapter, request.Id, outcome, (string)args[3]);
        Check(outcome == GatewayOutcome.Timeout && Logged(f, GatewayOutcome.Timeout)
            && !Logged(f, GatewayOutcome.Malformed) && SittingOutcome(f, request.Id) == GatewayOutcome.Timeout
            && TerminalCount(f, request.Id) == 1 && f.Boundary.PendingCount == 0
            && f.Commands.CommandLog.Count == 0 && !f.Clock.Paused && adapter.Transcript.Text == string.Empty,
            "LW-R5-Cancel-" + phase + ".TimeoutNotMalformed");
        object[] malformed = { connected, false, "{", null };
        Check((GatewayOutcome)ReviewInvoke("VoiceTerminal", typeof(GatewayOutcome),
            new[] { typeof(bool), typeof(bool), typeof(string), typeof(string).MakeByRefType() }, malformed)
            == (connected ? GatewayOutcome.Malformed : GatewayOutcome.Timeout),
            "LW-R5-Cancel-" + phase + ".NoCancellationControl");
    }
    private void TypedUnprovisioned()
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice request");
        GatewayOutcome outcome = Parse(GatewayPurpose.VoiceTranscription, 404, string.Empty, out string frame);
        Require(outcome == GatewayOutcome.Unprovisioned, "parser retains 404 class");
        // The existing consumer lane is an executable baseline reproduction.
        // Once the typed seam exists, both here and Unity use that same lane.
        MethodInfo delivery = wire.GetMethod("DeliverVoice", BindingFlags.Public | BindingFlags.Static);
        if (delivery == null) adapter.Complete(request.Id, frame);
        else DeliverVoice(f, adapter, request.Id, outcome, frame);
        Check(Logged(f, GatewayOutcome.Unprovisioned) && !Logged(f, GatewayOutcome.Malformed)
            && f.Boundary.PendingCount == 0 && f.Commands.CommandLog.Count == 0,
            "LW-R6-TypedUnprovisioned.RealConsumerRetainsType");
    }
    private void NoKey()
    {
        foreach (bool hasKey in new[] { false, true })
        {
            var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
            GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
            Require(request != null, "voice request before send-time key resolution");
            int sends = 0, rows = 0, warnings = 0;
            Action send = () => sends++;
            Action reportNoKey = () => warnings++;
            bool begun = (bool)ReviewInvoke("BeginVoice", typeof(bool),
                new[] { typeof(VoiceAdapter), typeof(ProposalBoundary), typeof(GatewayRequestId),
                    typeof(bool), typeof(Action), typeof(Action) },
                adapter, f.Boundary, request.Id, hasKey, send, reportNoKey);
            if (begun)
            {
                GatewayOutcome parsed = Parse(GatewayPurpose.VoiceTranscription, 200, VoiceBody, out string frame);
                DeliverVoice(f, adapter, request.Id, parsed, frame);
                if (SittingOutcome(f, request.Id).HasValue) rows++;
            }
            Check(hasKey ? begun && sends == 1 && warnings == 0 && rows == 1
                : !begun && sends == 0 && warnings == 1 && rows == 0
                    && Logged(f, GatewayOutcome.Unprovisioned) && !Logged(f, GatewayOutcome.Malformed)
                    && f.Boundary.PendingCount == 0 && f.Commands.CommandLog.Count == 0,
                "LW-R6-NoKey." + (hasKey ? "PresenceControl" : "NoSendNoRowUnprovisioned"));
        }
    }
    private void ExportSecretIds()
    {
        string root = Path.Combine(Path.GetTempPath(), "live-wire-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixture = "tests/production/fixtures/LIVE-WIRE/export-secret-ids.jsonl";
            string allowed = Path.Combine(root, "allowed.jsonl");
            File.WriteAllText(allowed, File.ReadAllText(fixture).Replace(canary, "req_abc123"));
            foreach (bool secret in new[] { false, true })
            {
                var start = new ProcessStartInfo("python3") { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                start.ArgumentList.Add("tools/live-ai-export.py");
                start.ArgumentList.Add("--input");
                start.ArgumentList.Add(secret ? fixture : allowed);
                start.ArgumentList.Add("--output");
                string output = Path.Combine(root, secret ? "rejected.jsonl" : "accepted.jsonl");
                start.ArgumentList.Add(output);
                start.Environment.Remove("MODEL_API_KEY");
                using (Process process = Process.Start(start))
                {
                    process.StandardInput.Close();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill();
                        Check(false, "LW-R1-Exporter.ProcessDeadline");
                        return;
                    }
                    string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                    if (!secret)
                    {
                        Require(process.ExitCode == 0 && File.Exists(output)
                            && File.ReadAllText(output).Contains("req_abc123"), "export positive control");
                        Check(true, "LW-R1-Exporter.ValidCorrelationControl");
                        continue;
                    }
                    Check(process.ExitCode != 0, "LW-R1-Exporter.RejectCredentialShapedId");
                    Check(Safe(text) && (!File.Exists(output) || Safe(File.ReadAllText(output))),
                        "LW-R1-Exporter.NoCanaryInOutput");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static bool PrivateHomePath(string text)
    {
        // Match username segments by shape; never maintain a real-user allow/deny list.
        foreach (string prefix in new[] { "/home/", "/Users/", "C:\\Users\\", "C:\\\\Users\\\\" })
        {
            int from = 0, at;
            while ((at = text.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int start = at + prefix.Length, end = start;
                while (end < text.Length && (char.IsLetterOrDigit(text[end])
                    || text[end] == '_' || text[end] == '-' || text[end] == '.')) end++;
                if (end > start && end < text.Length && (text[end] == '/' || text[end] == '\\')) return true;
                from = start;
            }
        }
        return false;
    }
    private void EvidencePaths()
    {
        foreach (string sample in new[] { "/home/" + "synthetic-user/project",
            "/Users/" + "synthetic-user/project", "C:\\Users\\" + "synthetic-user\\project",
            "C:\\\\Users\\\\" + "synthetic-user\\\\project" })
            Require(PrivateHomePath(sample), "scanner synthetic positive");
        Require(!PrivateHomePath("<PROJECT>/Assets") && !PrivateHomePath("/home/"), "scanner negative");
        int files = 0, leaks = 0;
        foreach (string package in new[] { "UNITY-COMPILE", "LIVE-WIRE" })
            foreach (string file in Directory.GetFiles(Path.Combine("evidence/production", package), "*",
                SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(new[] { ".json", ".jsonl", ".xml", ".log", ".txt", ".md", ".csv", ".yaml", ".yml" }, extension) < 0)
                    continue;
                files++;
                if (PrivateHomePath(File.ReadAllText(file))) leaks++;
            }
        Require(files > 0, "evidence corpus exists");
        Check(leaks == 0, "LW-R7-EvidencePaths.NoPrivateHomeShapes.filesWithLeaks=" + leaks);
    }
    private void ReviewRegressions()
    {
        ReviewCase("LW-R1-ProviderId", ProviderIds);
        ReviewCase("LW-R1-Exporter", ExportSecretIds);
        ReviewCase("LW-R2-DecisionRow", DecisionRow);
        ReviewCase("LW-R2-LateImageRow", LateImageRow);
        ReviewCase("LW-R3-SittingDeadline", SittingDeadline);
        ReviewCase("LW-R4-FirstFrameDenied", FirstFrameDenied);
        foreach (string phase in new[] { "BeforeConnect", "Acknowledgement", "FinalTranscript" })
            ReviewCase("LW-R5-Cancel-" + phase, () => CancelledVoice(phase));
        ReviewCase("LW-R6-NoKey", NoKey);
        ReviewCase("LW-R6-TypedUnprovisioned", TypedUnprovisioned);
        ReviewCase("LW-R7-EvidencePaths", EvidencePaths);
    }
    private static string DocFixture(string name)
    {
        return File.ReadAllText(Path.Combine("tests/production/fixtures/LIVE-WIRE", name + ".json"));
    }
    private void DocImage()
    {
        var f = new Fixture(); var creative = new Creative(f.World);
        WorkItem item = MakeArt(f, creative);
        var adapter = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest request = adapter.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(request != null, "image fixture request");
        using (JsonDocument doc = JsonDocument.Parse(adapter.Payload))
        {
            JsonElement body = doc.RootElement;
            Check(body.GetProperty("model").GetString() == "muse-image-1.0"
                && !string.IsNullOrWhiteSpace(body.GetProperty("prompt").GetString())
                && body.GetProperty("n").GetInt32() == 1
                && body.GetProperty("response_format").GetString() == "b64_json"
                && body.GetProperty("output_format").GetString() == "png", "LW-LIVE-IMAGE-Request");
        }
        GatewayOutcome outcome = Parse(GatewayPurpose.ImageGeneration, 200, DocFixture("image-png"), out string frame);
        Check(outcome == GatewayOutcome.Applied, "LW-LIVE-IMAGE-Png.Applied");
        Check(outcome == GatewayOutcome.Applied && adapter.Complete(request.Id, frame) == ProposalVerdict.Applied
            && adapter.GetContent(item.Reference.Item).ImageBase64 == Png, "LW-LIVE-IMAGE-Png.Content");
    }
    private void DocVoice(string fixture)
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice fixture request");
        GatewayOutcome outcome = Parse(GatewayPurpose.VoiceTranscription, 200, DocFixture(fixture), out string frame);
        Check(outcome == GatewayOutcome.Applied && adapter.Complete(request.Id, frame) == ProposalVerdict.Applied
            && adapter.Transcript.Text == "pause" && f.Clock.Paused, "LW-LIVE-VOICE-" + fixture + ".Content");
    }
    private void DocSam()
    {
        var f = new Fixture(); var adapter = new CaptureSearch(f.Boundary);
        adapter.SetCapture((CaptureId)41L, new byte[320 * 334 * 4], 320, 334);
        GatewayRequest request = adapter.Request("button", Endpoint("sam-3.1"), true, true);
        Require(request != null, "sam fixture request");
        GatewayOutcome outcome = Parse(GatewayPurpose.ImageSearch, 200, DocFixture("sam-nonempty"), out string frame);
        Check(outcome == GatewayOutcome.Applied, "LW-LIVE-SAM-Nonempty.Applied");
        if (outcome != GatewayOutcome.Applied) return;
        Check(adapter.Complete(request.Id, frame) == ProposalVerdict.Applied && adapter.Result != null
            && adapter.Result.Capture == (CaptureId)41L && adapter.Result.Outlines.Count == 2
            && adapter.CountText == "2", "LW-LIVE-SAM-Nonempty.CaptureAndCount");
        if (adapter.Result == null || adapter.Result.Outlines.Count != 2) return;
        // Each decoded outline stays inside its documented source-pixel box.
        for (int i = 0; i < 2; i++)
        {
            SamOutline outline = adapter.Result.Outlines[i];
            bool inside = outline.Capture == (CaptureId)41L && outline.Polygon.Count >= 3;
            foreach (ImagePoint point in outline.Polygon)
                inside &= point.X >= (i == 0 ? 211 : 155) && point.X <= (i == 0 ? 270 : 202)
                    && point.Y >= 228 && point.Y <= 254;
            Check(inside, "LW-LIVE-SAM-Nonempty.SourceBox" + i);
        }
    }
    private void DocBadRequest()
    {
        string body = DocFixture("bad-request");
        foreach (GatewayPurpose purpose in new[] { GatewayPurpose.ImageGeneration,
            GatewayPurpose.VoiceTranscription, GatewayPurpose.ImageSearch })
        {
            GatewayOutcome outcome = Parse(purpose, 400, body, out string frame);
            Check(outcome == GatewayOutcome.Malformed && Safe(frame)
                && (frame == null || !frame.Contains("top_p")), "LW-LIVE-400-" + purpose);
        }
        DecisionResponse response = ParseDecision((GatewayRequestId)1L, 400, body);
        Check(response.Outcome == GatewayOutcome.Malformed && response.ActivityCode == null
            && response.TargetId == null, "LW-LIVE-400-D1");
    }
    private void LiveContractRegressions()
    {
        Run("LW-LIVE-IMAGE", DocImage);
        Run("LW-LIVE-VOICE-Session", () =>
        {
            bool send = (bool)ReviewInvoke("VoiceCanSendAudio", typeof(bool),
                new[] { typeof(string), typeof(string).MakeByRefType() }, DocFixture("voice-session"), null);
            Check(send, "LW-LIVE-VOICE-Session.Acknowledged");
        });
        foreach (string fixture in new[] { "voice-final", "voice-complete" })
            Run("LW-LIVE-VOICE-" + fixture, () => DocVoice(fixture));
        Run("LW-LIVE-SAM-Nonempty", DocSam);
        Run("LW-LIVE-SAM-NullError", () =>
        {
            GatewayOutcome outcome = Parse(GatewayPurpose.ImageSearch, 200,
                DocFixture("sam-empty-null-error"), out string frame);
            Check(outcome == GatewayOutcome.Applied, "LW-LIVE-SAM-NullError.Applied");
            if (outcome != GatewayOutcome.Applied) return;
            using (JsonDocument doc = JsonDocument.Parse(frame))
                Check(doc.RootElement.GetProperty("capture").GetInt64() == 41
                    && doc.RootElement.GetProperty("outlines").GetArrayLength() == 0,
                    "LW-LIVE-SAM-NullError.EmptyCaptureResult");
        });
        Run("LW-LIVE-400", DocBadRequest);
    }
    private void SamPayloadTilde()
    {
        var f = new Fixture(); var adapter = new CaptureSearch(f.Boundary);
        adapter.SetCapture((CaptureId)41L, new byte[320 * 334 * 4], 320, 334);
        GatewayRequest request = adapter.Request("button", Endpoint("sam-3.1"), true, true);
        Require(request != null, "sam request");
        string body = DocFixture("sam-payload-tilde");
        Require(body.Contains("~!!!~"), "tilde after encoding marker");
        GatewayOutcome outcome = Parse(GatewayPurpose.ImageSearch, 200, body, out string frame);
        ProposalVerdict verdict = outcome == GatewayOutcome.Applied
            ? adapter.Complete(request.Id, frame) : adapter.Fail(request.Id, outcome);
        Check(outcome == GatewayOutcome.Applied && verdict == ProposalVerdict.Applied
            && adapter.Result != null && adapter.Result.Capture == (CaptureId)41L
            && adapter.Result.Outlines.Count == 2 && adapter.CountText == "2"
            && SittingOutcome(f, request.Id) == GatewayOutcome.Applied,
            "LW-RR2-F2-SamPayloadTilde.AppliedThroughCaptureSearch");
    }
    private void PumpImage(Fixture f, ImageAdapter image, double elapsed)
    {
        MethodInfo pump = wire.GetMethod("AdvanceSitting", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(ProposalBoundary), typeof(ImageAdapter), typeof(double) }, null);
        if (pump == null || pump.ReturnType != typeof(void)) AdvanceSitting(f, elapsed);
        else pump.Invoke(null, new object[] { f.Boundary, image, elapsed });
    }
    private void ImagePendingDeadline()
    {
        var f = new Fixture(); var creative = new Creative(f.World);
        WorkItem item = MakeArt(f, creative);
        var adapter = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest request = adapter.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(request != null && adapter.GetContent(item.Reference.Item) == null, "fresh image");
        MethodInfo pump = wire.GetMethod("AdvanceSitting", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(ProposalBoundary), typeof(ImageAdapter), typeof(double) }, null);
        Check(pump != null && pump.ReturnType == typeof(void),
            "LW-RR2-F1-ImagePendingDeadline.MissingContract.AdvanceSittingImage");
        // HTTP remains pending: no adapter.Tick, Fail or Complete before deadline.
        PumpImage(f, adapter, 30.0);
        ImageContent local = adapter.GetContent(item.Reference.Item);
        Check(local != null && local.Path == "local-alternative" && f.Boundary.PendingCount == 0
            && SittingOutcome(f, request.Id) == GatewayOutcome.Timeout && TerminalCount(f, request.Id) == 1,
            "LW-RR2-F1-ImagePendingDeadline.LocalInstalledOneTimeout");
        GatewayOutcome outcome = Parse(GatewayPurpose.ImageGeneration, 200, DocFixture("image-png"), out string frame);
        Require(outcome == GatewayOutcome.Applied, "late valid PNG control");
        ProposalVerdict verdict = adapter.Complete(request.Id, frame);
        Check(verdict == ProposalVerdict.DuplicateIgnored && local != null
            && object.ReferenceEquals(local, adapter.GetContent(item.Reference.Item))
            && TerminalCount(f, request.Id) == 1 && SittingOutcome(f, request.Id) == GatewayOutcome.Timeout,
            "LW-RR2-F1-ImagePendingDeadline.PendingClearedLateReplyInert");
    }
    private void ImageThirtySeconds(bool replyAtEleven)
    {
        var f = new Fixture(); var creative = new Creative(f.World);
        WorkItem item = MakeArt(f, creative);
        var adapter = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest request = adapter.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(request != null, "image request");
        string id = "LW-RR2-A1-ImageDeadline." + (replyAtEleven ? "Reply" : "PendingHTTP");
        AdvanceSitting(f, 10.0);
        Check(f.Boundary.PendingCount == 1 && SittingOutcome(f, request.Id) == null
            && adapter.GetContent(item.Reference.Item) == null, id + ".PendingAt10");
        AdvanceSitting(f, 1.3);
        Check(f.Boundary.PendingCount == 1 && SittingOutcome(f, request.Id) == null,
            id + ".PendingAt11p3");
        if (replyAtEleven)
        {
            GatewayOutcome parsed = Parse(GatewayPurpose.ImageGeneration, 200, DocFixture("image-png"), out string frame);
            Require(parsed == GatewayOutcome.Applied, "valid PNG at 11.3");
            ProposalVerdict verdict = adapter.Complete(request.Id, frame);
            ImageContent content = adapter.GetContent(item.Reference.Item);
            Check(verdict == ProposalVerdict.Applied && content != null && content.Path == "generated"
                && content.ImageBase64 == Png && SittingOutcome(f, request.Id) == GatewayOutcome.Applied
                && TerminalCount(f, request.Id) == 1, id + ".ReplyAt11p3Applies");
            return;
        }
        AdvanceSitting(f, 18.7);
        Check(f.Boundary.PendingCount == 0 && SittingOutcome(f, request.Id) == GatewayOutcome.Timeout
            && TerminalCount(f, request.Id) == 1, id + ".ExpiresAt30");
    }
    private void OtherTenSecondDeadlines()
    {
        Check(GatewayLimits.ServiceTimeoutRealSeconds == 10, "LW-RR2-A1-OtherDeadlines.ConstantRemains10");
        var f = new Fixture(); var inbox = new Inbox();
        var creative = new Creative(f.World); WorkItem item = MakeArt(f, creative);
        f.Commands.EnableAutomation(); // MakeArt's real human command disabled it.
        var image = new ImageAdapter(f.World, f.Boundary, creative);
        GatewayRequest imageRequest = image.Request(item.Reference.Item, Endpoint("muse-image-1.0"), true, true);
        Require(imageRequest != null, "shared image request");
        var decision = new DecisionAdapter(f.Boundary, inbox);
        var voice = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        var sam = new CaptureSearch(f.Boundary);
        sam.SetCapture((CaptureId)41L, new byte[16], 2, 2);
        GatewayRequest[] requests = {
            decision.RequestDecision(f.Resident.Id, Endpoint("d1-ultra"), true, true),
            voice.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true),
            sam.Request("chair", Endpoint("sam-3.1"), true, true) };
        foreach (GatewayRequest request in requests) Require(request != null, "other service request");
        PumpImage(f, image, 5.0);
        Check(f.Boundary.PendingCount == 4 && f.Boundary.DecisionLog.Count == 0,
            "LW-RR2-A1-OtherDeadlines.SharedClockNotAdvancedPerAdapter");
        PumpImage(f, image, 5.0);
        foreach (GatewayRequest request in requests)
            Check(SittingOutcome(f, request.Id) == GatewayOutcome.Timeout && TerminalCount(f, request.Id) == 1,
                "LW-RR2-A1-OtherDeadlines." + request.Purpose + "ExpiresAt10");
        Check(f.Boundary.PendingCount == 1 && SittingOutcome(f, imageRequest.Id) == null
            && image.GetContent(item.Reference.Item) == null && f.Autonomy.Decisions.Count == 1,
            "LW-RR2-A1-OtherDeadlines.ImageStillPendingOneDecisionFallback");
    }
    private void FirstErrorDiagnostics(bool sitting, bool credentialIdentifiers)
    {
        var f = new Fixture(); var adapter = new VoiceAdapter(f.World, f.Boundary, f.Commands);
        GatewayRequest request = adapter.Request(Endpoint("muse-voice-transcribe-1.0"), true, true, true);
        Require(request != null, "voice request");
        string first = DocFixture("voice-first-error");
        if (credentialIdentifiers) first = first.Replace("authentication_error", canary).Replace("invalid_key", "Bearer " + canary);
        int diagnostics = 0, audioSends = 0;
        string eventType = null, errorType = null, errorCode = null;
        Action<string, string, string> report = (evt, type, code) =>
        { diagnostics++; eventType = evt; errorType = type; errorCode = code; };
        object[] args = { first, sitting, report, null };
        bool send = (bool)ReviewInvoke("VoiceCanSendAudio", typeof(bool),
            new[] { typeof(string), typeof(bool), typeof(Action<string,string,string>), typeof(string).MakeByRefType() }, args);
        if (send) audioSends++;
        string terminal = (string)args[3];
        GatewayOutcome outcome = Parse(GatewayPurpose.VoiceTranscription, 200, terminal, out string frame);
        DeliverVoice(f, adapter, request.Id, outcome, frame);
        DeliverVoice(f, adapter, request.Id, outcome, frame);
        string id = "LW-RR2-F3-FirstErrorDiagnostics." + (!sitting ? "Disabled" : credentialIdentifiers ? "Credential" : "Safe");
        Check(audioSends == 0 && outcome == GatewayOutcome.Denied && TerminalCount(f, request.Id) == 1
            && SittingOutcome(f, request.Id) == GatewayOutcome.Denied && adapter.Transcript.Text == string.Empty
            && f.Commands.CommandLog.Count == 0 && !f.Clock.Paused, id + ".NoAudioOneDenied");
        Check(sitting ? diagnostics == 1 && eventType == "error" && (credentialIdentifiers
                ? string.IsNullOrEmpty(errorType) && string.IsNullOrEmpty(errorCode)
                : errorType == "authentication_error" && errorCode == "invalid_key")
            : diagnostics == 0, id + ".IdentifiersAndSuppression");
        Check(Safe(terminal) && Safe(eventType) && Safe(errorType) && Safe(errorCode)
            && terminal != null && !terminal.Contains("private provider message"), id + ".Sanitized");
    }
    private void DeferredDecisionPlan()
    {
        string[] services = (string[])ReviewInvoke("SittingServices", typeof(string[]), new[] { typeof(bool) }, true);
        Check(services != null && services.Length == 3
            && Array.IndexOf(services, "Muse Image") >= 0 && Array.IndexOf(services, "Muse Voice Transcribe") >= 0
            && Array.IndexOf(services, "SAM 3.1") >= 0 && Array.IndexOf(services, "d1-ultra") < 0,
            "LW-RR2-A2-DecisionPlan.ExcludesD1WhileOD4Open");
    }
    private void DeferredDecisionSend()
    {
        int sends = 0, records = 0; string disposition = null;
        Action send = () => sends++;
        Action<string> record = value => { records++; disposition = value; };
        bool begun = (bool)ReviewInvoke("BeginSittingDecision", typeof(bool),
            new[] { typeof(bool), typeof(Action), typeof(Action<string>) }, true, send, record);
        Check(!begun && sends == 0 && records == 1 && disposition == "deferred-local-fallback",
            "LW-RR2-A2-DecisionSend.NoSendOneDeferredRecord");
    }
    private void SecondReviewRegressions()
    {
        ReviewCase("LW-RR2-F1-ImagePendingDeadline", ImagePendingDeadline);
        ReviewCase("LW-RR2-F2-SamPayloadTilde", SamPayloadTilde);
        foreach (bool reply in new[] { false, true })
            ReviewCase("LW-RR2-A1-ImageDeadline", () => ImageThirtySeconds(reply));
        ReviewCase("LW-RR2-A1-OtherDeadlines", OtherTenSecondDeadlines);
        ReviewCase("LW-RR2-F3-FirstErrorDiagnostics.Safe", () => FirstErrorDiagnostics(true, false));
        ReviewCase("LW-RR2-F3-FirstErrorDiagnostics.Credential", () => FirstErrorDiagnostics(true, true));
        ReviewCase("LW-RR2-F3-FirstErrorDiagnostics.Disabled", () => FirstErrorDiagnostics(false, false));
        ReviewCase("LW-RR2-A2-DecisionPlan", DeferredDecisionPlan);
        // Independent case: a missing plan seam must not hide the no-send gate gap.
        ReviewCase("LW-RR2-A2-DecisionSend", DeferredDecisionSend);
    }
    public static int Main(string[] args)
    {
        var checks = new LiveWireChecks();
        checks.Execute(Array.IndexOf(args, "negative-fixture") >= 0);
        checks.ReviewRegressions();
        checks.LiveContractRegressions();
        checks.SecondReviewRegressions();
        if (checks.failures != 0) return 1;
        Console.WriteLine("LIVE-WIRE ALL PASS");
        return 0;
    }
}
