// LIVE-WIRE@1: the Unity I/O side of the Meta provider boundary. IMAGE, SAM
// and decisions use UnityWebRequest; VOICE uses the documented realtime
// WebSocket. Raw responses parse only through Blockparty.AI.LiveWire, and the
// real adapters still own request issuance and Complete/Fail. The key is read
// from the existing KeyProvider path inside each send, used immediately, and
// never copied to a field, log, frame, row or exception message.
//
// Opt-in owner sitting (packaged player only, off by default in the editor):
//   <player> --live-ai-sitting --live-ai-build-commit=<40-hex>
//            --live-ai-archive-sha256=<64-hex>
// appends one row per service to
//   <Application.persistentDataPath>/live-ai/sitting/<session>.jsonl
// for tools/live-ai-export.py. It sends one request per service through the
// real adapters. Without a key no rows are written and local fallbacks remain.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blockparty.Activities;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Services;
using Blockparty.Simulation;
using UnityEngine;
using UnityEngine.Networking;

namespace Blockparty.Presentation
{
    public sealed class LiveTransport : IDecisionTransport
    {
        private const string ImageUrl = "https://api.meta.ai/v1/images/generations";
        private const string SamUrl = "https://api.meta.ai/v1/responses";
        internal const string DecisionUrl = "https://api.meta.ai/v1/responses";
        private const string VoiceUrl = "wss://api.meta.ai/v1/asr/realtime";
        internal const string ImageService = "Muse Image";
        internal const string VoiceService = "Muse Voice Transcribe";
        internal const string SamService = "SAM 3.1";
        internal const string DecisionService = "d1-ultra";
        private const string ImageModel = "muse-image-1.0";
        private const string VoiceModel = "muse-voice-transcribe-1.0";
        private const string SamModel = "sam-3.1";
        internal const string DecisionModel = "muse-spark-1.3";
        private const string OnePixelPng =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
        private const string SamImageUrl = "data:image/png;base64," + OnePixelPng;
        private const int ImageLateWindowSeconds = 90;
        // Docs: PCM_24KHZ is signed 16-bit LE mono at 48000 bytes/s and the
        // realtime example sends 80 ms chunks in real time
        // (https://dev.meta.ai/docs/api-reference/voice/realtime).
        private const int VoiceBytesPerSecond = 48000;
        private const int VoiceFrameBytes = VoiceBytesPerSecond * 80 / 1000;
        private const int VoiceSilenceBytes = VoiceBytesPerSecond * 2;

        private sealed class Reply
        {
            public int Status;
            public string Body;
            public string ProviderRequestId;
            public long LatencyMs;
            public bool HadKey;
        }

        private readonly MonoBehaviour host;
        private readonly Queue<DecisionResponse> replies = new Queue<DecisionResponse>();
        private readonly bool sitting;
        private readonly string sessionId;
        private readonly string buildCommit;
        private readonly string archiveSha256;
        private readonly string sittingFile;
        private int inFlight;
        private bool noKeyLogged;
        private Reply decisionReply;
        private string decisionModelId;

        public LiveTransport(MonoBehaviour host)
            : this(host, false, null, null, null)
        {
        }

        internal LiveTransport(MonoBehaviour host, bool sitting, string sessionId,
            string buildCommit, string archiveSha256)
        {
            if (host == null)
            {
                throw new ArgumentNullException("host");
            }
            this.host = host;
            this.sitting = sitting;
            this.sessionId = sessionId ?? string.Empty;
            this.buildCommit = buildCommit ?? string.Empty;
            this.archiveSha256 = archiveSha256 ?? string.Empty;
            if (sitting)
            {
                sittingFile = Path.Combine(Application.persistentDataPath, "live-ai",
                    "sitting", this.sessionId + ".jsonl");
            }
        }

        public bool IsIdle { get { return inFlight == 0; } }

        public bool NoKeyReported { get; private set; }

        public int RowsWritten { get; private set; }

        // The boundary/adapter side of a decision. A missing key is a typed
        // Unprovisioned reply, so DecisionAdapter still runs the local fallback.
        public void Send(GatewayRequest request, DecisionPayload payload)
        {
            if (request == null)
            {
                return;
            }
            inFlight++;
            try
            {
                host.StartCoroutine(SendDecision(request, payload));
            }
            catch (Exception)
            {
                inFlight--;
                replies.Enqueue(new DecisionResponse(request.Id,
                    GatewayOutcome.Malformed, null, null));
            }
        }

        public bool TryReceive(out DecisionResponse response)
        {
            if (replies.Count == 0)
            {
                response = null;
                return false;
            }
            response = replies.Dequeue();
            return true;
        }

        public IEnumerator GenerateImage(ImageAdapter adapter, ProposalBoundary boundary,
            GatewayRequest request, string body)
        {
            if (adapter == null || request == null)
            {
                yield break;
            }
            Reply reply = null;
            int timeout = LiveWire.ImageGenerationTimeoutRealSeconds
                + (sitting ? ImageLateWindowSeconds : 0);
            yield return Post(ImageUrl, body, timeout, value => reply = value);
            if (reply == null || !reply.HadKey)
            {
                adapter.Fail(request.Id, GatewayOutcome.Unprovisioned);
                ReportNoKey();
                yield break;
            }
            long gameplayDeadlineMs = LiveWire.ImageGenerationTimeoutRealSeconds * 1000L;
            if (sitting && reply.LatencyMs > gameplayDeadlineMs)
            {
                // The boundary already settled Timeout; this reply only feeds
                // the diagnostic and never replaces the local fallback.
                LogImageLate(reply.LatencyMs, reply.Status);
                AppendSettledRow(boundary, ImageService, GatewayPurpose.ImageGeneration,
                    request.Id, reply.Status, reply.LatencyMs, ImageModel,
                    reply.ProviderRequestId);
                yield break;
            }
            string frame;
            GatewayOutcome outcome = LiveWire.ParseImage(reply.Status, reply.Body, out frame);
            if (outcome == GatewayOutcome.Applied)
            {
                adapter.Complete(request.Id, frame);
            }
            else
            {
                adapter.Fail(request.Id, outcome);
                LogDiag(ImageService, reply.Status, outcome,
                    LiveWire.ProviderErrorId(reply.Body), string.Empty, reply.LatencyMs);
            }
            AppendSettledRow(boundary, ImageService, GatewayPurpose.ImageGeneration,
                request.Id, reply.Status, reply.LatencyMs, ImageModel,
                reply.ProviderRequestId);
        }

        public IEnumerator SearchSam(CaptureSearch adapter, ProposalBoundary boundary,
            GatewayRequest request, CaptureId capture, string phrase,
            byte[] rgba = null, int width = 0, int height = 0)
        {
            if (adapter == null || request == null)
            {
                yield break;
            }
            string body = SamRequestBody(request.ModelId, phrase, rgba, width, height);
            Reply reply = null;
            yield return Post(SamUrl, body, GatewayLimits.ServiceTimeoutRealSeconds,
                value => reply = value);
            if (reply == null || !reply.HadKey)
            {
                adapter.Fail(request.Id, GatewayOutcome.Unprovisioned);
                ReportNoKey();
                yield break;
            }
            string frame;
            GatewayOutcome outcome = LiveWire.ParseSam(reply.Status, reply.Body,
                capture, out frame);
            SamDump(body, reply, outcome, frame, width, height);
            if (outcome == GatewayOutcome.Applied)
            {
                adapter.Complete(request.Id, frame);
            }
            else
            {
                adapter.Fail(request.Id, outcome);
                LogDiag(SamService, reply.Status, outcome,
                    LiveWire.ProviderErrorId(reply.Body), string.Empty, reply.LatencyMs);
            }
            AppendSettledRow(boundary, SamService, GatewayPurpose.ImageSearch, request.Id,
                reply.Status, reply.LatencyMs, request.ModelId, reply.ProviderRequestId);
        }

        // Opt-in live evidence: --sam-look-dump=<dir> writes each SAM request (image bytes
        // replaced by their size) and the provider response with key-like strings redacted.
        static int samDumps;
        static void SamDump(string body, Reply reply, GatewayOutcome outcome, string frame,
            int width, int height)
        {
            string dir = null;
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--sam-look-dump=", StringComparison.Ordinal)) dir = a.Substring(16);
            UnityEngine.Debug.Log("LIVE-AI sam status=" + reply.Status.ToString(CultureInfo.InvariantCulture)
                + " outcome=" + outcome + " latency_ms=" + reply.LatencyMs.ToString(CultureInfo.InvariantCulture)
                + " image=" + width + "x" + height + " request_bytes=" + body.Length
                + " response_bytes=" + (reply.Body == null ? 0 : reply.Body.Length));
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            int n = ++samDumps;
            string shown = System.Text.RegularExpressions.Regex.Replace(body,
                "data:image/png;base64,[A-Za-z0-9+/=]+", m => "<png " + width + "x" + height
                + " base64_chars=" + (m.Length - 22) + ">");
            File.WriteAllText(Path.Combine(dir, n.ToString("D2") + "-request.json"), shown);
            string redacted = System.Text.RegularExpressions.Regex.Replace(reply.Body ?? string.Empty,
                "(?i)(bearer\\s+|\\b(?:sk|key|token|LLM)[-_|])[A-Za-z0-9_\\-|.]{12,}", "$1<redacted>");
            File.WriteAllText(Path.Combine(dir, n.ToString("D2") + "-response.json"), redacted);
            File.WriteAllText(Path.Combine(dir, n.ToString("D2") + "-meta.txt"),
                "status=" + reply.Status + " outcome=" + outcome + " latency_ms=" + reply.LatencyMs
                + " provider_request_id=" + LiveWire.SanitizeProviderId(reply.ProviderRequestId, 64)
                + "\nframe=" + (frame ?? "") + "\n");
        }

        // SAM-BODY begin
        // The body SearchSam posts. rgba is the captured frame, rows top-down as
        // SamLookDirector.CaptureRgba returns them; null keeps the documented
        // 1x1 PNG the owner sitting sends.
        internal static string SamRequestBody(string modelId, string phrase,
            byte[] rgba, int width, int height)
        {
            string url = rgba == null ? SamImageUrl
                : "data:image/png;base64," + Convert.ToBase64String(EncodePng(rgba, width, height));
            return LiveWire.BuildSamRequest(modelId, phrase, url);
        }

        // RGBA8 PNG, filter 0 rows, so the bytes SAM sees are the capture's rows in order.
        static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            if (width <= 0 || height <= 0 || rgba.Length != stride * height)
                throw new ArgumentException("RGBA length must equal width*height*4", "rgba");
            byte[] raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
            uint a = 1, b = 0;
            foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
            var idat = new MemoryStream();
            idat.WriteByte(0x78); idat.WriteByte(0x01);
            using (var deflate = new System.IO.Compression.DeflateStream(idat,
                System.IO.Compression.CompressionLevel.Fastest, true))
                deflate.Write(raw, 0, raw.Length);
            PngUInt(idat, b << 16 | a);
            byte[] header = new byte[13];
            PngUInt(header, 0, (uint)width); PngUInt(header, 4, (uint)height);
            header[8] = 8; header[9] = 6;
            var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            PngChunk(png, "IHDR", header);
            PngChunk(png, "IDAT", idat.ToArray());
            PngChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        static void PngChunk(MemoryStream png, string type, byte[] data)
        {
            byte[] chunk = new byte[data.Length + 4];
            for (int i = 0; i < 4; i++) chunk[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, chunk, 4, data.Length);
            uint crc = 0xFFFFFFFF;
            foreach (byte v in chunk)
            {
                crc ^= v;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            PngUInt(png, (uint)data.Length);
            png.Write(chunk, 0, chunk.Length);
            PngUInt(png, ~crc);
        }

        static void PngUInt(MemoryStream s, uint v)
        {
            byte[] b = new byte[4];
            PngUInt(b, 0, v);
            s.Write(b, 0, 4);
        }

        static void PngUInt(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        // SAM-BODY end

        // VOICE-LIVE (M5-OD-38): audio is the player's PCM_24KHZ microphone
        // segment; null keeps the two seconds of silence the probes send.
        public IEnumerator TranscribeVoice(VoiceAdapter adapter, ProposalBoundary boundary,
            GatewayRequest request, byte[] audio = null)
        {
            if (adapter == null || request == null)
            {
                yield break;
            }
            Reply reply = new Reply();
            string body = string.Empty;
            string firstEvent = string.Empty;
            string firstError = string.Empty;
            string firstErrorCode = string.Empty;
            bool connected = false;
            bool opened = false;
            bool cancelled = false;
            Uri uri = VoiceUri(VoiceUrl, sessionId);
            if (uri.Scheme != "wss"
                || !string.Equals(uri.Host, "api.meta.ai", StringComparison.OrdinalIgnoreCase))
            {
                LiveWire.BeginVoice(adapter, boundary, request.Id, false, null, null);
                yield break;
            }
            string key;
            bool sendAuthorized = false;
            if (!LiveWire.BeginVoice(adapter, boundary, request.Id, TryGetKey(out key),
                () => sendAuthorized = true, ReportNoKey) || !sendAuthorized)
            {
                key = null;
                yield break;
            }
            string handshake = LiveWire.VoiceHandshake(key, request.ModelId);
            key = null;
            using (var socket = new ClientWebSocket())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.CancelAfter(GatewayLimits.ServiceTimeoutRealSeconds * 1000);
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                Task connect = socket.ConnectAsync(uri, cancel.Token);
                yield return Wait(connect, value => ok = value);
                if (ok)
                {
                    opened = true;
                    connected = true;
                    Task send = socket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(handshake)),
                        WebSocketMessageType.Text, true, cancel.Token);
                    handshake = null;
                    yield return Wait(send, value => ok = value);
                }
                else
                {
                    handshake = null;
                }
                if (ok)
                {
                    string acknowledgement = null;
                    yield return ReceiveMessage(socket, cancel.Token,
                        (text, success) => { acknowledgement = text; ok = success; });
                    if (ok && !LiveWire.VoiceCanSendAudio(acknowledgement, sitting,
                        (eventType, errorType, errorCode) =>
                        {
                            firstEvent = eventType;
                            firstError = errorType;
                            firstErrorCode = errorCode;
                        }, out string terminalFrame))
                    {
                        body = terminalFrame ?? string.Empty;
                        ok = false;
                    }
                    if (ok)
                    {
                        reply.ProviderRequestId = SessionId(acknowledgement);
                        byte[] pcm = audio ?? new byte[VoiceSilenceBytes];
                        double audioStart = watch.Elapsed.TotalSeconds;
                        for (int sent = 0; sent < pcm.Length && ok;
                            sent += VoiceFrameBytes)
                        {
                            Task chunk = socket.SendAsync(new ArraySegment<byte>(pcm, sent,
                                    Math.Min(VoiceFrameBytes, pcm.Length - sent)),
                                WebSocketMessageType.Binary, true, cancel.Token);
                            yield return Wait(chunk, value => ok = value);
                            double due = (sent + VoiceFrameBytes)
                                / (double)VoiceBytesPerSecond;
                            while (ok && !cancel.IsCancellationRequested
                                && watch.Elapsed.TotalSeconds - audioStart < due)
                            {
                                yield return null;
                            }
                        }
                    }
                }
                if (ok)
                {
                    Task end = socket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"endStream\"}")),
                        WebSocketMessageType.Text, true, cancel.Token);
                    yield return Wait(end, value => ok = value);
                }
                // Keep reading until the terminal event; progress and speech
                // lifecycle events are multiplexed and not an end-of-stream.
                while (ok)
                {
                    string received = null;
                    yield return ReceiveMessage(socket, cancel.Token,
                        (text, success) => { received = text; ok = success; });
                    if (!ok || received == null)
                    {
                        break;
                    }
                    if (firstEvent.Length == 0)
                    {
                        firstEvent = LiveWire.VoiceEventType(received);
                    }
                    if (IsTerminalVoiceEvent(received))
                    {
                        body = received;
                        break;
                    }
                }
                watch.Stop();
                reply.LatencyMs = watch.ElapsedMilliseconds;
                cancelled = cancel.IsCancellationRequested;
            }
            string frame;
            GatewayOutcome outcome = LiveWire.VoiceTerminal(connected, cancelled, body,
                out frame);
            LiveWire.DeliverVoice(adapter, boundary, request.Id, outcome, frame);
            reply.Status = opened ? 101 : 0;
            if (outcome != GatewayOutcome.Applied)
            {
                string providerError = firstError.Length != 0 ? firstError
                    : firstErrorCode.Length != 0 ? firstErrorCode
                    : LiveWire.ProviderErrorId(body);
                LogDiag(VoiceService, reply.Status, outcome, providerError, firstEvent,
                    reply.LatencyMs, firstErrorCode);
            }
            AppendSettledRow(boundary, VoiceService, GatewayPurpose.VoiceTranscription,
                request.Id, reply.Status, reply.LatencyMs, request.ModelId,
                reply.ProviderRequestId);
        }

        // VOICE-AI (owner 2026-11-04): the decision model picks one offered voice command
        // when the local grammar found none. Same Post, host, key handling and decision
        // model as SendDecision, with a short timeout; status -1 means no key.
        public const int VoiceInterpretTimeoutRealSeconds = 8;   // META-LIVE: a live /v1/responses text reply took 3.5 s, too close to 4 s

        public IEnumerator InterpretVoice(VoiceAdapter adapter, ProposalBoundary boundary, GatewayRequest request)
        {
            if (adapter == null || request == null) yield break;
            Reply reply = null;
            yield return Post(DecisionUrl, adapter.InterpretationBody(request.ModelId),
                VoiceInterpretTimeoutRealSeconds, value => reply = value);
            if (reply == null || !reply.HadKey) { ReportNoKey(); adapter.CompleteInterpretation(request.Id, -1, string.Empty); yield break; }
            adapter.CompleteInterpretation(request.Id, reply.Status, reply.Body);
            AppendSettledRow(boundary, DecisionService, GatewayPurpose.VoiceTranscription, request.Id,
                reply.Status, reply.LatencyMs, request.ModelId, reply.ProviderRequestId);
        }

        private IEnumerator SendDecision(GatewayRequest request, DecisionPayload payload)
        {
            bool settled = false;
            try
            {
                Reply reply = null;
                yield return Post(DecisionUrl, DecisionBody(request, payload),
                    GatewayLimits.ServiceTimeoutRealSeconds, value => reply = value);
                DecisionResponse response;
                if (reply == null || !reply.HadKey)
                {
                    response = new DecisionResponse(request.Id,
                        GatewayOutcome.Unprovisioned, null, null);
                    ReportNoKey();
                }
                else
                {
                    response = LiveWire.ParseDecision(request.Id, reply.Status,
                        DecisionAdapter.AnswersFromResponses(reply.Body));
                    decisionReply = reply;
                    decisionModelId = request.ModelId;
                    if (response.Outcome != GatewayOutcome.Applied)
                    {
                        LogDiag(DecisionService, reply.Status, response.Outcome,
                            LiveWire.ProviderErrorId(reply.Body), string.Empty,
                            reply.LatencyMs);
                    }
                }
                replies.Enqueue(response);
                settled = true;
            }
            finally
            {
                if (!settled)
                {
                    replies.Enqueue(new DecisionResponse(request.Id,
                        GatewayOutcome.Malformed, null, null));
                }
                inFlight--;
            }
        }

        // The decision row waits for DecisionAdapter.Tick, which is where the
        // real boundary settles. The sitting sends one decision at a time.
        internal void AppendDecisionRow(ProposalBoundary boundary, GatewayRequestId id)
        {
            Reply reply = decisionReply;
            string modelId = decisionModelId;
            decisionReply = null;
            decisionModelId = null;
            if (reply == null)
            {
                return;
            }
            AppendSettledRow(boundary, DecisionService, GatewayPurpose.ResidentDecision, id,
                reply.Status, reply.LatencyMs, modelId, reply.ProviderRequestId);
        }

        private IEnumerator Post(string url, string body, int timeoutSeconds,
            Action<Reply> done)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "api.meta.ai", StringComparison.OrdinalIgnoreCase))
            {
                done(new Reply());
                yield break;
            }
            string key;
            if (!TryGetKey(out key))
            {
                done(new Reply());
                yield break;
            }
            string providerRequestId = string.Empty;
            int status = 0;
            string responseBody = string.Empty;
            long latency;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var request = new UnityWebRequest(uri, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(
                        Encoding.UTF8.GetBytes(body ?? string.Empty));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + key);
                    key = null;
                    request.timeout = timeoutSeconds;
                    yield return request.SendWebRequest();
                    status = (int)request.responseCode;
                    if (status > 0 && request.downloadHandler != null)
                    {
                        responseBody = request.downloadHandler.text ?? string.Empty;
                    }
                    providerRequestId = ResponseId(request);
                }
            }
            finally
            {
                key = null;
                watch.Stop();
            }
            latency = watch.ElapsedMilliseconds;
            done(new Reply
            {
                Status = status,
                Body = responseBody,
                ProviderRequestId = providerRequestId,
                LatencyMs = latency,
                HadKey = true,
            });
        }

        // A sitting row reports the boundary's settled outcome, never the
        // parser's optimistic result. No settled record means no row.
        private void AppendSettledRow(ProposalBoundary boundary, string service,
            GatewayPurpose purpose, GatewayRequestId id, int status, long latencyMs,
            string modelId, string providerRequestId)
        {
            if (!sitting)
            {
                return;
            }
            GatewayOutcome? settled = LiveWire.SittingOutcome(boundary, id);
            if (!settled.HasValue)
            {
                return;
            }
            AppendRow(service, purpose, id, status, settled.Value, latencyMs, modelId,
                providerRequestId);
        }

        private void AppendRow(string service, GatewayPurpose purpose, GatewayRequestId id,
            int status, GatewayOutcome outcome, long latencyMs, string modelId,
            string providerRequestId)
        {
            if (!sitting || string.IsNullOrEmpty(sittingFile))
            {
                return;
            }
            try
            {
                JsonObject row = new JsonObject();
                row.Add("session_id", sessionId);
                row.Add("utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                row.Add("service", service);
                row.Add("purpose", purpose.ToString());
                row.Add("transport_kind", "live");
                row.Add("request_id", ((long)id).ToString(CultureInfo.InvariantCulture));
                row.Add("provider_request_id", LiveWire.SanitizeProviderId(providerRequestId, 128));
                row.Add("status", status);
                row.Add("outcome", outcome.ToString());
                row.Add("latency_ms", latencyMs);
                row.Add("model_id", SafeToken(modelId, 64));
                row.Add("build_commit", buildCommit);
                row.Add("archive_sha256", archiveSha256);
                StringBuilder text = new StringBuilder();
                row.Render(text);
                string directory = Path.GetDirectoryName(sittingFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.AppendAllText(sittingFile, text.ToString() + "\n");
                RowsWritten++;
            }
            catch (Exception)
            {
                UnityEngine.Debug.LogWarning("LIVE-WIRE sitting row could not be written.");
            }
        }

        // One credential-scrubbed line per non-Applied reply. Values are fixed
        // labels, enums or sanitized provider ids; bodies and headers never log.
        private void LogDiag(string service, int status, GatewayOutcome outcome,
            string providerError, string firstEvent, long latencyMs,
            string providerErrorCode = null)
        {
            if (!sitting)
            {
                return;
            }
            string code = LiveWire.SanitizeProviderId(providerErrorCode, 64);
            UnityEngine.Debug.Log("LIVE-AI diag "
                + LiveWire.SanitizeDiagnosticValue(service, 64)
                + " status=" + status.ToString(CultureInfo.InvariantCulture)
                + " outcome=" + outcome
                + " provider_error=" + LiveWire.SanitizeProviderId(providerError, 64)
                + " first_event=" + LiveWire.SanitizeProviderId(firstEvent, 64)
                + (code.Length == 0 ? string.Empty : " error_code=" + code)
                + " latency_ms=" + latencyMs.ToString(CultureInfo.InvariantCulture));
        }

        private void LogImageLate(long lateReplyMs, int status)
        {
            if (!sitting)
            {
                return;
            }
            UnityEngine.Debug.Log("LIVE-AI diag "
                + LiveWire.SanitizeDiagnosticValue(ImageService, 64)
                + " late_reply_ms=" + lateReplyMs.ToString(CultureInfo.InvariantCulture)
                + " status=" + status.ToString(CultureInfo.InvariantCulture));
        }

        private void ReportNoKey()
        {
            NoKeyReported = true;
            if (!noKeyLogged)
            {
                noKeyLogged = true;
                UnityEngine.Debug.LogWarning("LIVE-WIRE NoKey: local fallback remains.");
            }
        }

        private static bool TryGetKey(out string key)
        {
            key = null;
            try
            {
                KeyResolution resolution = KeyProvider.Resolve(KeySettings.DefaultPath());
                if (!resolution.TryGetKey(out key) || !UsableKey(key))
                {
                    key = null;
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                key = null;
                return false;
            }
        }

        // HTTP header and handshake text both accept this key form only.
        private static bool UsableKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 512)
            {
                return false;
            }
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c <= ' ' || c >= 127)
                {
                    return false;
                }
            }
            return true;
        }

        private static string DecisionBody(GatewayRequest request, DecisionPayload payload)
        {
            return DecisionAdapter.ResponsesBody(request.ModelId, payload);
        }

        private static Uri VoiceUri(string endpoint, string session)
        {
            UriBuilder builder = new UriBuilder(endpoint);
            string query = builder.Query;
            if (query.StartsWith("?", StringComparison.Ordinal))
            {
                query = query.Substring(1);
            }
            builder.Query = (query.Length == 0 ? string.Empty : query + "&")
                + "sessionId=" + Uri.EscapeDataString(session);
            return builder.Uri;
        }

        private static string SessionId(string acknowledgement)
        {
            if (string.IsNullOrEmpty(acknowledgement))
            {
                return string.Empty;
            }
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(acknowledgement) as JsonObject;
                if (root != null && root.Has("sessionId"))
                {
                    return root.GetString("sessionId", "voice.handshake");
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        private static bool IsTerminalVoiceEvent(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(message) as JsonObject;
                if (root == null || !root.Has("type"))
                {
                    return false;
                }
                string type = root.GetString("type", "voice");
                if (type == "error" || type == "speechComplete")
                {
                    return true;
                }
                return type == "transcript" && root.Has("final")
                    && root.GetBool("final", "voice");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerator Wait(Task task, Action<bool> done)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
            if (task.IsFaulted)
            {
                _ = task.Exception;
                done(false);
                yield break;
            }
            done(!task.IsCanceled);
        }

        private static IEnumerator ReceiveMessage(ClientWebSocket socket,
            CancellationToken token, Action<string, bool> done)
        {
            byte[] buffer = new byte[8192];
            var stream = new MemoryStream();
            while (true)
            {
                Task<WebSocketReceiveResult> receive = socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), token);
                WebSocketReceiveResult result = null;
                bool ok = false;
                yield return WaitReceive(receive, (value, success) =>
                {
                    result = value;
                    ok = success;
                });
                if (!ok || result == null || result.MessageType == WebSocketMessageType.Close)
                {
                    done(null, false);
                    yield break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    stream.SetLength(0);
                    continue;
                }
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 1048576L)
                {
                    done(null, false);
                    yield break;
                }
                if (!result.EndOfMessage)
                {
                    continue;
                }
                done(Encoding.UTF8.GetString(stream.ToArray()), true);
                yield break;
            }
        }

        private static IEnumerator WaitReceive(Task<WebSocketReceiveResult> task,
            Action<WebSocketReceiveResult, bool> done)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
            if (task.IsFaulted || task.IsCanceled)
            {
                _ = task.Exception;
                done(null, false);
                yield break;
            }
            WebSocketReceiveResult result;
            try
            {
                result = task.Result;
            }
            catch (Exception)
            {
                done(null, false);
                yield break;
            }
            done(result, result != null);
        }

        private static string ResponseId(UnityWebRequest request)
        {
            string[] names = { "x-request-id", "request-id", "openai-request-id",
                "x-meta-request-id", "x-amzn-requestid" };
            for (int i = 0; i < names.Length; i++)
            {
                string value = request.GetResponseHeader(names[i]);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private static string SafeToken(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            StringBuilder text = new StringBuilder(Math.Min(value.Length, max));
            for (int i = 0; i < value.Length && text.Length < max; i++)
            {
                char c = value[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.'
                    || c == ':')
                {
                    text.Append(c);
                }
            }
            return text.ToString();
        }

        internal static string Arg(string[] args, string key)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                {
                    return args[i].Substring(key.Length + 1);
                }
            }
            return string.Empty;
        }

        internal static string HashArg(string[] args, string key, int length)
        {
            string value = Arg(args, key);
            if (value.Length == 0)
            {
                value = Environment.GetEnvironmentVariable(key.Substring(2)
                    .Replace('-', '_').ToUpperInvariant()) ?? string.Empty;
            }
            if (value.Length != length)
            {
                return string.Empty;
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F')))
                {
                    return string.Empty;
                }
            }
            return value.ToLowerInvariant();
        }
    }

    internal sealed class LiveSittingRunner : MonoBehaviour
    {
        private const StateWriter Core = StateWriter.SimCore;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void MaybeStart()
        {
            if (Application.isEditor)
            {
                return;
            }
            string[] args = Environment.GetCommandLineArgs();
            bool enabled = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--live-ai-sitting"
                    || args[i].StartsWith("--live-ai-sitting=", StringComparison.Ordinal))
                {
                    enabled = true;
                    break;
                }
            }
            if (!enabled)
            {
                return;
            }
            GameObject root = new GameObject("LiveAiSitting");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.AddComponent<LiveSittingRunner>();
        }

        private IEnumerator Start()
        {
            yield return null;
            string[] args = Environment.GetCommandLineArgs();
            string build = LiveTransport.HashArg(args, "--live-ai-build-commit", 40);
            string archive = LiveTransport.HashArg(args, "--live-ai-archive-sha256", 64);
            if (build.Length == 0 || archive.Length == 0)
            {
                UnityEngine.Debug.LogWarning("LIVE-WIRE sitting needs build commit "
                    + "and archive hash arguments; no requests sent.");
                yield break;
            }
            string session = Guid.NewGuid().ToString("D");
            var transport = new LiveTransport(this, true, session, build, archive);
            UnityEngine.Debug.Log("LIVE-AI sitting " + session + " started.");

            WorldState world = WorldState.CreateSeeded(246L, 42L, 1, new[] { "Sitting" });
            ResidentRecord resident = world.ResidentsSnapshot()[0];
            for (int i = 0; i < StateSchema.NeedCount; i++)
            {
                world.SetNeed(Core, resident.Id, (Need)i, 80f);
            }
            world.SetNeed(Core, resident.Id, Need.Hunger, 8f);
            var clock = new Clock(world);
            var commands = new CommandArbitrator(world, clock);
            var autonomy = new Autonomy(world, commands);
            var boundary = new ProposalBoundary(world, clock, commands, autonomy);
            var creative = new Creative(world);
            ObjectId item = world.AllocateObject(Core);
            WorldEvent memory = world.AppendEvent(Core, "help", resident.Id, "carried a box");
            world.AddMemory(Core, resident.Id, new MemoryEntry(memory.Event, "help", 0L,
                resident.Home, new[] { resident.Id }, "carried a box", "participant"));
            CreativeSnapshot snapshot = new CreativeSnapshot();
            CreativeSnapshot.WorkRow work = new CreativeSnapshot.WorkRow();
            work.Item = (long)item;
            work.Author = (long)resident.Id;
            work.PersonalitySummary = "curious and practical";
            work.MemoryEvent = (long)memory.Event;
            work.QualityTier = "plain";
            snapshot.Works.Add(work);
            creative.Import(Core, snapshot);
            if (creative.WorkItems.Count == 0)
            {
                UnityEngine.Debug.LogWarning("LIVE-WIRE sitting fixture has no work item.");
                yield break;
            }

            var image = new ImageAdapter(world, boundary, creative);
            var voice = new VoiceAdapter(world, boundary, commands);
            var sam = new CaptureSearch(boundary);
            sam.SetCapture((CaptureId)41L, new byte[2 * 2 * 4], 2, 2);
            var decision = new DecisionAdapter(boundary, transport);
            bool od4Open = true;
            string[] services = LiveWire.SittingServices(od4Open);

            // One shared boundary; one measured unscaled-time delta per frame.
            Coroutine pump = StartCoroutine(PumpElapsed(boundary, image));
            try
            {
                if (Array.IndexOf(services, LiveTransport.ImageService) >= 0)
                {
                    ServiceEndpoint imageEndpoint = new ServiceEndpoint("Muse Image",
                        "https://api.meta.ai/v1/images/generations", "muse-image-1.0", true);
                    GatewayRequest imageRequest = image.Request(creative.WorkItems[0].Reference.Item,
                        imageEndpoint, true, true);
                    if (imageRequest != null)
                    {
                        yield return transport.GenerateImage(image, boundary, imageRequest,
                            image.Payload);
                    }
                    if (transport.NoKeyReported)
                    {
                        yield break;
                    }
                }

                if (Array.IndexOf(services, LiveTransport.VoiceService) >= 0)
                {
                    ServiceEndpoint voiceEndpoint = new ServiceEndpoint("Muse Voice Transcribe",
                        "wss://api.meta.ai/v1/asr/realtime", "muse-voice-transcribe-1.0", true);
                    GatewayRequest voiceRequest = voice.Request(voiceEndpoint, true, true, true);
                    if (voiceRequest != null)
                    {
                        yield return transport.TranscribeVoice(voice, boundary, voiceRequest);
                    }
                    if (transport.NoKeyReported)
                    {
                        yield break;
                    }
                }

                if (Array.IndexOf(services, LiveTransport.SamService) >= 0)
                {
                    ServiceEndpoint samEndpoint = new ServiceEndpoint("SAM 3.1",
                        "https://api.meta.ai/v1/responses", "sam-3.1", true);
                    GatewayRequest samRequest = sam.Request("chair", samEndpoint, true, true);
                    if (samRequest != null)
                    {
                        yield return transport.SearchSam(sam, boundary, samRequest,
                            (CaptureId)41L, "chair");
                    }
                    if (transport.NoKeyReported)
                    {
                        yield break;
                    }
                }

                GatewayRequest decisionRequest = null;
                if (LiveWire.BeginSittingDecision(od4Open,
                    () =>
                    {
                        ServiceEndpoint decisionEndpoint = new ServiceEndpoint(LiveTransport.DecisionService,
                            LiveTransport.DecisionUrl, LiveTransport.DecisionModel, true);
                        decisionRequest = decision.RequestDecision(resident.Id,
                            decisionEndpoint, true, true);
                    },
                    value => UnityEngine.Debug.Log("LIVE-AI sitting "
                        + LiveTransport.DecisionService + " " + value + ".")) && decisionRequest != null)
                {
                    while (!transport.IsIdle)
                    {
                        yield return null;
                    }
                    decision.Tick(0.0);
                    transport.AppendDecisionRow(boundary, decisionRequest.Id);
                }
            }
            finally
            {
                StopCoroutine(pump);
            }
            UnityEngine.Debug.Log("LIVE-AI sitting finished rows="
                + transport.RowsWritten.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static IEnumerator PumpElapsed(ProposalBoundary boundary, ImageAdapter image)
        {
            while (true)
            {
                LiveWire.AdvanceSitting(boundary, image, Time.unscaledDeltaTime);
                yield return null;
            }
        }
    }
}
