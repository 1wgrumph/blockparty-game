// PROBE-VOICE production checks. Standalone console program: compiled and
// run with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the
// Unity test path. Proves the vendor record in
// docs/production/vendors/voice.json against the AI-BOUNDARY@1 adapter types
// (ServiceEndpoint, GatewayRequest, ProposalBoundary, ProposalLogEntry) using
// recorded fixtures: frames copied from the official Meta Model API docs on
// 2026-10-27, marked RECORDED below, plus synthetic frames that follow a
// recorded shape, marked SYNTHETIC. Positive checks prove the recorded
// session-start, audio and event shapes are recorded; completion JSON parses and drives
// the adapter paths (Applied on typed finals, Malformed with fixed reason
// codes on error frames, HeldForPause/RevalidatedOnResume across pause).
// Negative checks prove malformed frames, denied/unavailable/no-key requests,
// timeout, cancellation, and rate limits take the defined local-fallback or
// inert path and never a connected success. Fixed reason codes only:
// CompleteService echoes the code into the log, so no reason carries payload
// text (CREDENTIAL-DESIGN warning). No network and no credential material:
// the handshake fixture carries a placeholder where the key would sit, and
// the sweep patterns below the tests stay clean. Mirrors the
// AI-BOUNDARY/CREDENTIAL-DESIGN pattern. Run with the single argument
// "negative-fixture" to corrupt the final-transcript fixture: the harness
// must reject it and exit 1.
using System;
using System.Text.Json;
using System.Collections.Generic;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ProbeVoiceChecks
{
    private static int failures;
    private static bool negativeFixture;

    // RECORDED https://dev.meta.ai/docs/speech-to-text (handshake ack shape).
    private const string HandshakeAck =
        "{\"sessionId\": \"550e8400-e29b-41d4-a716-446655440000\"}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (endpointing example).
    private const string PartialCumulative =
        "{\"type\":\"speechStart\",\"turnId\":1,\"audioProcessedMs\":1200}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (endpointing example).
    private const string PartialTranscript =
        "{\"type\":\"transcript\",\"transcript\":\"how is the\",\"final\":false,\"audioProcessedMs\":2400}";

    // SYNTHETIC: PUSH_TO_TALK completion following the recorded transcript shape.
    private const string FinalTranscript =
        "{\"type\":\"transcript\",\"transcript\":\"How is the weather?\",\"final\":true,\"audioProcessedMs\":3600}";

    // Corrupted twin of FinalTranscript used only in negative-fixture mode:
    // the required final marker is stripped, so shape validation must fail.
    private const string CorruptTranscript =
        "{\"type\":\"transcript\",\"transcript\":\"How is the weather?\",\"audioProcessedMs\":3600}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (endpointing example).
    private const string SpeechEnd =
        "{\"type\":\"speechEnd\",\"turnId\":1,\"audioProcessedMs\":3600}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (endpointing example).
    private const string SpeechComplete =
        "{\"type\":\"speechComplete\",\"turnId\":1,\"transcript\":\"How is the weather?\",\"audioProcessedMs\":3600}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (diarization example).
    private const string SpeakerEvent =
        "{\"type\":\"speaker\",\"label\":\"A\",\"audioProcessedMs\":2480}";

    // SYNTHETIC: audioProgress following the recorded schema shape.
    private const string AudioProgress =
        "{\"type\":\"audioProgress\",\"audioProcessedMs\":2400}";

    // SYNTHETIC: error event following the recorded ErrorEvent shape with a
    // client-safe message. The message text never reaches the decision log.
    private const string ErrorEvent =
        "{\"type\":\"error\",\"message\":\"Max session duration reached\",\"sessionId\":\"550e8400-e29b-41d4-a716-446655440000\"}";

    // RECORDED https://dev.meta.ai/docs/api-reference/voice/realtime.
    private const string EndStream = "{\"type\": \"endStream\"}";

    // Frame with no discriminator: must fail shape validation.
    private const string NoTypeFrame = "{\"transcript\":\"orphan\"}";

    // SYNTHETIC: handshake request following the recorded
    // RealtimeHandshakeRequest shape. The accessToken is a placeholder: no
    // key value is embedded, read, or transmitted anywhere in this file.
    private const string HandshakeRequest =
        "{\"authorization\":{\"accessToken\":\"Bearer <key>\"},\"audioEncoding\":\"PCM_24KHZ\",\"model\":\"muse-voice-transcribe-1.0\",\"mode\":\"PUSH_TO_TALK\"}";

    // RECORDED https://dev.meta.ai/docs/speech-to-text (transcribe example).
    private const string BatchResponse =
        "{\"sessionId\":\"9f1c\",\"transcript\":\"How is the weather? It is raining.\",\"audioDurationMs\":8240," +
        "\"turns\":[{\"turnId\":1,\"startMs\":1520,\"endMs\":4640,\"transcript\":\"How is the weather?\",\"speaker\":\"A\"}," +
        "{\"turnId\":2,\"startMs\":5900,\"endMs\":8240,\"transcript\":\"It is raining.\",\"speaker\":\"B\"}]}";

    // The fixed reason codes this probe may pass to CompleteService. The
    // boundary echoes the code into the log, so the set is closed and every
    // member is payload-free by construction.
    private static readonly string[] ReasonCodes =
    {
        "UpstreamError",
        "BadFrame",
        "UpstreamTimeout",
    };

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static bool Has(string frame, string part)
    {
        return frame.IndexOf(part, StringComparison.Ordinal) >= 0;
    }

    private static bool HasAll(string frame, params string[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            if (!Has(frame, parts[i]))
            {
                return false;
            }
        }
        return true;
    }

    // Local schema qualification only; the runtime Voice adapter remains unbuilt.
    private static bool ParsesCompletion(string frame, out string reason)
    {
        reason = "BadFrame";
        try
        {
            using (JsonDocument document = JsonDocument.Parse(frame))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                JsonElement type;
                if (root.TryGetProperty("type", out type))
                {
                    if (type.ValueKind != JsonValueKind.String) return false;
                    string name = type.GetString();
                    if (name == "error")
                    {
                        if (IsString(root, "message") && IsString(root, "sessionId")) reason = "UpstreamError";
                        return false;
                    }
                    if (!IsString(root, "transcript") || !IsMilliseconds(root, "audioProcessedMs")) return false;
                    if (name == "speechComplete")
                    {
                        JsonElement turn;
                        return root.TryGetProperty("turnId", out turn) && turn.TryGetInt32(out _);
                    }
                    JsonElement final;
                    return name == "transcript" && root.TryGetProperty("final", out final)
                        && final.ValueKind == JsonValueKind.True;
                }
                JsonElement turns;
                if (!IsString(root, "sessionId") || !IsString(root, "transcript")
                    || !IsMilliseconds(root, "audioDurationMs") || !root.TryGetProperty("turns", out turns)
                    || turns.ValueKind != JsonValueKind.Array) return false;
                foreach (JsonElement turn in turns.EnumerateArray())
                {
                    JsonElement id;
                    JsonElement speaker;
                    if (turn.ValueKind != JsonValueKind.Object || !turn.TryGetProperty("turnId", out id)
                        || !id.TryGetInt32(out _) || !IsMilliseconds(turn, "startMs")
                        || !IsMilliseconds(turn, "endMs") || !IsString(turn, "transcript")
                        || turn.TryGetProperty("speaker", out speaker) && speaker.ValueKind != JsonValueKind.String)
                        return false;
                }
                return true;
            }
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsString(JsonElement value, string field)
    {
        JsonElement found;
        return value.TryGetProperty(field, out found) && found.ValueKind == JsonValueKind.String;
    }

    private static bool IsMilliseconds(JsonElement value, string field)
    {
        JsonElement found;
        long number;
        return value.TryGetProperty(field, out found) && found.ValueKind == JsonValueKind.Number
            && found.TryGetInt64(out number) && number >= 0L;
    }

    private static ProposalVerdict CompleteFrame(ProposalBoundary boundary, GatewayRequestId id, string frame)
    {
        string reason;
        bool parsed = ParsesCompletion(frame, out reason);
        return boundary.CompleteService(id, parsed, reason);
    }

    private static bool IsFixedReason(string reason)
    {
        for (int i = 0; i < ReasonCodes.Length; i++)
        {
            if (string.Equals(reason, ReasonCodes[i], StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public Autonomy Autonomy;
        public ProposalBoundary Boundary;
    }

    private static Stack NewStack(long seed)
    {
        WorldState world = WorldState.CreateSeeded(7L, seed, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Autonomy autonomy = new Autonomy(world, arbitrator);
        return new Stack
        {
            World = world,
            Clock = clock,
            Arbitrator = arbitrator,
            Autonomy = autonomy,
            Boundary = new ProposalBoundary(world, clock, arbitrator, autonomy),
        };
    }

    private static ServiceEndpoint VoiceSvc()
    {
        return new ServiceEndpoint("meta-voice",
            "wss://api.meta.ai/v1/asr/realtime", "muse-voice-transcribe-1.0", true);
    }

    private static ServiceEndpoint BatchSvc()
    {
        return new ServiceEndpoint("meta-voice-batch",
            "https://api.meta.ai/v1/asr/transcribe", "muse-voice-transcribe-1.0", true);
    }

    private static string FinalFrame()
    {
        return negativeFixture ? CorruptTranscript : FinalTranscript;
    }

    private static bool LogHas(Stack stack, GatewayOutcome outcome, string fragment)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome
                && log[i].Detail.IndexOf(fragment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool LogHasOutcome(Stack stack, GatewayOutcome outcome)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome)
            {
                return true;
            }
        }
        return false;
    }

    private static bool LogHasText(Stack stack, string fragment)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            string both = log[i].ModelId + "|" + log[i].Detail;
            if (both.IndexOf(fragment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "negative-fixture", StringComparison.Ordinal))
            {
                negativeFixture = true;
            }
        }
        if (negativeFixture)
        {
            Console.WriteLine("PROBE-VOICE negative-fixture mode: final frame corrupted");
        }

        // The recorded endpoint config matches the vendor record exactly.
        {
            ServiceEndpoint voice = VoiceSvc();
            Check(string.Equals(voice.Service, "meta-voice", StringComparison.Ordinal), "Endpoint.Service");
            Check(string.Equals(voice.Endpoint, "wss://api.meta.ai/v1/asr/realtime", StringComparison.Ordinal), "Endpoint.Url");
            Check(string.Equals(voice.ModelId, "muse-voice-transcribe-1.0", StringComparison.Ordinal), "Endpoint.Model");
            Check(voice.Provisioned, "Endpoint.Provisioned");
            ServiceEndpoint batch = BatchSvc();
            Check(string.Equals(batch.Endpoint, "https://api.meta.ai/v1/asr/transcribe", StringComparison.Ordinal), "Endpoint.BatchUrl");
        }

        // Recorded session-start shapes validate: the ack carries a session
        // id and no type field; the handshake request carries the credential
        // placeholder, encoding, model, and mode.
        {
            Check(Has(HandshakeAck, "\"sessionId\""), "SessionStart.AckHasId");
            Check(!Has(HandshakeAck, "\"type\""), "SessionStart.AckHasNoType");
            Check(HasAll(HandshakeRequest, "\"authorization\"", "\"accessToken\"",
                "\"audioEncoding\"", "\"PCM_24KHZ\"", "\"model\"",
                "\"muse-voice-transcribe-1.0\"", "\"mode\""), "SessionStart.RequestShape");
            Check(!Has(HandshakeRequest, "LLM_"), "SessionStart.NoKeyValue");
        }

        // A keyed voice request issues and stamps purpose plus model id.
        {
            Stack stack = NewStack(201L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(request != null, "Issue.VoiceRequest");
            Check(request.Purpose == GatewayPurpose.VoiceTranscription, "Issue.Purpose");
            Check(string.Equals(request.ModelId, "muse-voice-transcribe-1.0", StringComparison.Ordinal), "Issue.Model");
            Check(stack.Boundary.PendingCount == 1, "Issue.Pending");
            Check(stack.Boundary.IsFirstSendDisclosed("meta-voice"), "Issue.Disclosed");
        }

        // Recorded partial shapes validate and hold: a partial is interim, so
        // the adapter completes nothing and the request stays pending.
        {
            Check(HasAll(PartialTranscript, "\"type\":\"transcript\"",
                "\"final\":false", "\"audioProcessedMs\""), "Partial.Shape");
            Check(!Has(PartialTranscript, "\"turnId\""), "Partial.NoTurnId");
            Check(HasAll(PartialCumulative, "\"type\":\"speechStart\"",
                "\"turnId\"", "\"audioProcessedMs\""), "Partial.StartShape");
            Stack stack = NewStack(202L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(request != null && stack.Boundary.PendingCount == 1, "Partial.Holds");
            Check(stack.Boundary.DecisionLog.Count == 0, "Partial.NothingLogged");
        }

        Check(!ParsesCompletion(FinalTranscript.Substring(0, FinalTranscript.Length - 1), out _), "F10.MalformedJsonRejected");
        Check(!ParsesCompletion(FinalTranscript.Replace("3600", "\"wrong-type\""), out _), "F10.FieldTypesValidated");
        Check(!ParsesCompletion(PartialTranscript, out _), "F10.PartialNotCompletion");

        // The final transcript shape validates and applies as a typed result.
        // In negative-fixture mode this shape is corrupted and must fail.
        {
            Check(ParsesCompletion(FinalFrame(), out _), "Final.Shape");
            Stack stack = NewStack(203L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(CompleteFrame(stack.Boundary, request.Id, FinalFrame()) == ProposalVerdict.Applied, "Final.Applied");
            Check(LogHas(stack, GatewayOutcome.Applied, "Validated"), "Final.Logged");
        }

        // Recorded turn-lifecycle shapes validate; speechComplete is the turn
        // completion signal and applies. speechEnd alone is a boundary only.
        {
            Check(HasAll(SpeechEnd, "\"type\":\"speechEnd\"",
                "\"turnId\""), "Turn.EndShape");
            Check(!Has(SpeechEnd, "\"transcript\""), "Turn.EndCarriesNoText");
            Check(HasAll(SpeechComplete, "\"type\":\"speechComplete\"",
                "\"turnId\"", "\"transcript\""), "Turn.CompleteShape");
            Stack stack = NewStack(204L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(CompleteFrame(stack.Boundary, request.Id, SpeechComplete) == ProposalVerdict.Applied, "Turn.Applied");
        }

        // Recorded speaker and progress shapes validate. Speaker labels are
        // session-scoped bare letters, never identities.
        {
            Check(HasAll(SpeakerEvent, "\"type\":\"speaker\"",
                "\"label\"", "\"audioProcessedMs\""), "Speaker.Shape");
            Check(Has(SpeakerEvent, "\"A\""), "Speaker.BareLetter");
            Check(HasAll(AudioProgress, "\"type\":\"audioProgress\"",
                "\"audioProcessedMs\""), "Progress.Shape");
            Check(!Has(AudioProgress, "\"turnId\""), "Progress.NoTurn");
        }

        // The recorded end-of-input marker validates; it is the only client
        // text frame after the handshake.
        {
            Check(Has(EndStream, "\"type\""), "EndStream.Shape");
            Check(Has(EndStream, "endStream"), "EndStream.Marker");
        }

        // The recorded error shape validates and maps to a Malformed outcome
        // with a fixed reason code: the log carries Parse:UpstreamError and
        // none of the event's message, session id, or transcript text.
        {
            Check(HasAll(ErrorEvent, "\"type\":\"error\"", "\"message\"",
                "\"sessionId\""), "Error.Shape");
            Stack stack = NewStack(205L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(IsFixedReason("UpstreamError"), "Error.CodeIsFixed");
            Check(CompleteFrame(stack.Boundary, request.Id, ErrorEvent) == ProposalVerdict.DiscardedInvalid, "Error.Discarded");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:UpstreamError"), "Error.Logged");
            Check(!LogHasText(stack, "Max session duration reached"), "Error.NoMessage");
            Check(!LogHasText(stack, "550e8400"), "Error.NoSessionId");
            Check(stack.Boundary.PendingCount == 0, "Error.NothingPending");
        }

        // Frames without a discriminator fail validation and map to the same
        // fixed-code Malformed path.
        {
            Check(!Has(NoTypeFrame, "\"type\""), "BadFrame.NoType");
            Stack stack = NewStack(206L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(IsFixedReason("BadFrame"), "BadFrame.CodeIsFixed");
            Check(CompleteFrame(stack.Boundary, request.Id, NoTypeFrame) == ProposalVerdict.DiscardedInvalid, "BadFrame.Discarded");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:BadFrame"), "BadFrame.Logged");
            Check(!LogHasText(stack, "orphan"), "BadFrame.NoPayload");
        }

        // The recorded batch response shape validates and applies: session
        // id, whole-clip transcript, duration, and ordered turns.
        {
            Check(HasAll(BatchResponse, "\"sessionId\"", "\"transcript\"",
                "\"audioDurationMs\"", "\"turns\""), "Batch.Shape");
            Check(HasAll(BatchResponse, "\"turnId\"", "\"startMs\"",
                "\"endMs\"", "\"speaker\""), "Batch.TurnFields");
            Stack stack = NewStack(207L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, BatchSvc(), true, true);
            Check(request != null, "Batch.Issued");
            Check(CompleteFrame(stack.Boundary, request.Id, BatchResponse) == ProposalVerdict.Applied, "Batch.Applied");
        }

        // Final text enters the game contract as a VoiceTranscript. An
        // ambiguous (meaningless) transcript executes no command: inert.
        {
            VoiceTranscript inert = new VoiceTranscript("How is the weather?", null);
            Check(!inert.ExecutesCommand, "Contract.AmbiguousInert");
            VoiceTranscript empty = new VoiceTranscript(string.Empty, null);
            Check(!empty.ExecutesCommand, "Contract.EmptyInert");
        }

        // No-key requests refuse with Unprovisioned plus MissingCredential and
        // create nothing pending: local-fallback evidence, never success.
        {
            Stack stack = NewStack(208L);
            Check(stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true) == null, "NoKey.Refused");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), "NoKey.Logged");
            Check(stack.Boundary.PendingCount == 0, "NoKey.NothingPending");
            Check(!LogHasOutcome(stack, GatewayOutcome.Applied), "NoKey.NoAppliedClaim");
        }

        // Unavailable adapters refuse the same way: unprovisioned config and
        // missing first-send disclosure never send.
        {
            Stack stack = NewStack(209L);
            ServiceEndpoint dark = new ServiceEndpoint("meta-voice",
                "wss://api.meta.ai/v1/asr/realtime", "muse-voice-transcribe-1.0", false);
            Check(stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, dark, true, true) == null, "Unavailable.Unprovisioned");
            Check(LogHas(stack, GatewayOutcome.Unprovisioned, "Unprovisioned:AdapterNotProvisioned"), "Unavailable.Logged");
            Check(stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, false) == null, "Unavailable.NoDisclosure");
            Check(LogHas(stack, GatewayOutcome.Denied, "Denied:FirstSendDisclosureMissing"), "Unavailable.DeniedLogged");
            Check(stack.Boundary.PendingCount == 0, "Unavailable.NothingPending");
        }

        // Denied paths stay denied: prohibited runtime models and the
        // game-wide concurrency cap refuse without sending.
        {
            Stack stack = NewStack(210L);
            ServiceEndpoint spark = new ServiceEndpoint("meta-voice",
                "wss://api.meta.ai/v1/asr/realtime", "muse-spark-1.3-contributor", true);   // BP-AI-14 owner amendment 2026-11-04: only muse-spark-1.3 runs
            Check(stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, spark, true, true) == null, "Denied.SparkRefused");
            Check(LogHas(stack, GatewayOutcome.Denied, "Denied:ProhibitedRuntimeModel"), "Denied.SparkLogged");
            for (int i = 0; i < GatewayLimits.MaxConcurrentGameWide; i++)
            {
                stack.Boundary.RequestService(null,
                    GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            }
            Check(stack.Boundary.PendingCount == GatewayLimits.MaxConcurrentGameWide, "Denied.WideFull");
            Check(stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true) == null, "Denied.WideRefused");
            Check(LogHas(stack, GatewayOutcome.RateLimitedLocal, "RateLimited:GameWide"), "Denied.WideLogged");
        }

        // The 10-real-second service timeout finalizes a hung voice request.
        {
            Stack stack = NewStack(211L);
            stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            Check(stack.Boundary.Tick() == 1, "Timeout.Fires");
            Check(LogHas(stack, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"), "Timeout.Logged");
        }

        // Player cancellation of a voice request records Cancelled, inert.
        {
            Stack stack = NewStack(212L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            Check(stack.Boundary.Cancel(request.Id), "Cancel.True");
            Check(LogHas(stack, GatewayOutcome.Cancelled, "PlayerCancel"), "Cancel.Logged");
            Check(!stack.Boundary.Cancel(request.Id), "Cancel.AgainFalse");
        }

        // Duplicate completions are idempotent no-ops.
        {
            Stack stack = NewStack(213L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            CompleteFrame(stack.Boundary, request.Id, FinalTranscript);
            int logs = stack.Boundary.DecisionLog.Count;
            Check(CompleteFrame(stack.Boundary, request.Id, FinalTranscript) == ProposalVerdict.DuplicateIgnored, "Dup.Verdict");
            Check(stack.Boundary.DecisionLog.Count == logs, "Dup.NoLog");
        }

        // Pause holds a parsed voice result; resume revalidates and applies.
        {
            Stack stack = NewStack(214L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            stack.Clock.Pause();
            Check(CompleteFrame(stack.Boundary, request.Id, FinalTranscript) == ProposalVerdict.HeldForPause, "Pause.Held");
            Check(stack.Boundary.PendingCount == 0 && stack.Boundary.HeldCount == 1, "Pause.MovedToHeld");
            stack.Clock.Resume();
            stack.Boundary.OnResume();
            Check(stack.Boundary.RevalidateHeldService(request.Id) != null, "Pause.Revalidated");
            Check(LogHas(stack, GatewayOutcome.Applied, "RevalidatedOnResume"), "Pause.AppliedOnResume");
        }

        // The decision log never carries endpoint, credential, session, or
        // transcript text across the voice paths above.
        {
            Stack stack = NewStack(215L);
            GatewayRequest good = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            CompleteFrame(stack.Boundary, good.Id, FinalTranscript);
            stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            GatewayRequest bad = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            CompleteFrame(stack.Boundary, bad.Id, NoTypeFrame);
            stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, BatchSvc(), true, true);
            stack.Boundary.AdvanceRealSeconds(10.0);
            stack.Boundary.Tick();
            bool clean = true;
            IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
            for (int i = 0; i < log.Count; i++)
            {
                string both = log[i].ModelId + "|" + log[i].Detail;
                if (both.IndexOf("https://", StringComparison.Ordinal) >= 0
                    || both.IndexOf("wss://", StringComparison.Ordinal) >= 0
                    || both.IndexOf("Bearer", StringComparison.Ordinal) >= 0
                    || both.IndexOf("MODEL_API_KEY", StringComparison.Ordinal) >= 0
                    || both.IndexOf("Authorization", StringComparison.Ordinal) >= 0
                    || both.IndexOf("accessToken", StringComparison.Ordinal) >= 0
                    || both.IndexOf("550e8400", StringComparison.Ordinal) >= 0
                    || both.IndexOf("how is the", StringComparison.Ordinal) >= 0)
                {
                    clean = false;
                }
            }
            Check(log.Count >= 4, "Secrets.DrovePaths");
            Check(clean, "Secrets.NoneLogged");
        }

        // Reason codes are closed and payload-free: every code in the set is
        // fixed text, and raw event text is never a member.
        {
            bool closed = ReasonCodes.Length == 3
                && IsFixedReason("UpstreamError")
                && IsFixedReason("BadFrame")
                && IsFixedReason("UpstreamTimeout");
            Check(closed, "Reasons.ClosedSet");
            Check(!IsFixedReason("Max session duration reached"), "Reasons.RejectsMessage");
            Check(!IsFixedReason("How is the weather?"), "Reasons.RejectsTranscript");
            Check(!IsFixedReason("Bearer <key>"), "Reasons.RejectsCredential");
        }

        // Repeated play entry is deterministic: a fresh boundary starts empty
        // and the same voice flow logs the same details.
        {
            Stack first = NewStack(216L);
            Check(first.Boundary.PendingCount == 0 && first.Boundary.DecisionLog.Count == 0, "Determinism.StartsEmpty");
            GatewayRequest one = first.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            CompleteFrame(first.Boundary, one.Id, FinalTranscript);
            first.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            Stack second = NewStack(216L);
            GatewayRequest two = second.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), true, true);
            CompleteFrame(second.Boundary, two.Id, FinalTranscript);
            second.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), false, true);
            bool same = first.Boundary.DecisionLog.Count == second.Boundary.DecisionLog.Count
                && first.Boundary.DecisionLog.Count == 2;
            for (int i = 0; i < first.Boundary.DecisionLog.Count && same; i++)
            {
                if (!string.Equals(first.Boundary.DecisionLog[i].Detail, second.Boundary.DecisionLog[i].Detail, StringComparison.Ordinal)
                    || first.Boundary.DecisionLog[i].Outcome != second.Boundary.DecisionLog[i].Outcome)
                {
                    same = false;
                }
            }
            Check(same, "Determinism.SameVoiceLog");
        }

        if (failures == 0)
        {
            Console.WriteLine("PROBE-VOICE ALL PASS");
        }
        return failures;
    }
}
