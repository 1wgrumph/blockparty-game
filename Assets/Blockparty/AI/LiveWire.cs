// LIVE-WIRE@1: plain-C# provider response mapping for IMAGE, VOICE, SAM and
// D1U. This file performs no I/O, reads no credential and keeps no mutable
// static state. Every success frame is rebuilt from the adapter-required
// fields only, so provider metadata and error text never reach a consumer.
// JSON uses SaveV1's strict reader because Unity's Assembly-CSharp has no
// System.Text.Json reference.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.AI
{
    public static class LiveWire
    {
        // Owner A1: image generation only; every other service keeps the
        // 10-second shared boundary timeout.
        public const int ImageGenerationTimeoutRealSeconds = 30;

        public static DecisionResponse ParseDecision(GatewayRequestId requestId,
            int status, string body)
        {
            GatewayOutcome outcome = StatusOutcome(status);
            if (outcome != GatewayOutcome.Applied)
            {
                return new DecisionResponse(requestId, outcome, null, null);
            }
            try
            {
                JsonObject root = ParseObject(body);
                if (root == null || TryError(root, out outcome))
                {
                    return new DecisionResponse(requestId,
                        root == null ? GatewayOutcome.Malformed : outcome, null, null);
                }
                JsonObject answers = root.GetObject("answers", "decision");
                JsonObject activity = answers.GetObject("activity", "decision.answers");
                string choice = activity.GetString("choice", "decision.answers.activity");
                if (!IsActivityCode(choice) || LooksSecret(choice))
                {
                    return new DecisionResponse(requestId, GatewayOutcome.Malformed, null, null);
                }
                return new DecisionResponse(requestId, GatewayOutcome.Applied, choice, null);
            }
            catch (Exception)
            {
                return new DecisionResponse(requestId, GatewayOutcome.Malformed, null, null);
            }
        }

        public static GatewayOutcome ParseImage(int status, string body, out string frame)
        {
            frame = null;
            GatewayOutcome outcome = StatusOutcome(status);
            if (outcome != GatewayOutcome.Applied)
            {
                return outcome;
            }
            try
            {
                JsonObject root = ParseObject(body);
                if (root == null || TryError(root, out outcome))
                {
                    return root == null ? GatewayOutcome.Malformed : outcome;
                }
                JsonArray data = root.GetArray("data", "image");
                if (data.Count == 0)
                {
                    return GatewayOutcome.Malformed;
                }
                string image = data.GetObject(0, "image.data").GetString("b64_json", "image.data");
                if (!IsPng(image))
                {
                    return GatewayOutcome.Malformed;
                }
                JsonObject closed = new JsonObject();
                JsonArray closedData = new JsonArray();
                JsonObject row = new JsonObject();
                row.Add("b64_json", image);
                closedData.Add(row);
                closed.Add("data", closedData);
                frame = Render(closed);
                return GatewayOutcome.Applied;
            }
            catch (Exception)
            {
                return GatewayOutcome.Malformed;
            }
        }

        public static GatewayOutcome ParseVoice(int status, string body, out string frame)
        {
            frame = null;
            GatewayOutcome outcome = StatusOutcome(status);
            if (outcome != GatewayOutcome.Applied)
            {
                frame = ErrorFrame(outcome);
                return outcome;
            }
            try
            {
                JsonObject root = ParseObject(body);
                if (root == null)
                {
                    outcome = GatewayOutcome.Malformed;
                    frame = ErrorFrame(outcome);
                    return outcome;
                }
                if (TryError(root, out outcome))
                {
                    frame = ErrorFrame(outcome);
                    return outcome;
                }
                string type = root.GetString("type", "voice");
                if (type == "error")
                {
                    outcome = ErrorOutcome(root);
                    frame = ErrorFrame(outcome);
                    return outcome;
                }
                string transcript;
                if (type == "speechComplete")
                {
                    transcript = root.GetString("transcript", "voice");
                }
                else if (type == "transcript")
                {
                    if (!root.Has("final") || !root.GetBool("final", "voice"))
                    {
                        outcome = GatewayOutcome.Malformed;
                        frame = ErrorFrame(outcome);
                        return outcome;
                    }
                    transcript = root.GetString("transcript", "voice");
                }
                else
                {
                    outcome = GatewayOutcome.Malformed;
                    frame = ErrorFrame(outcome);
                    return outcome;
                }
                if (LooksSecret(transcript))
                {
                    outcome = GatewayOutcome.Malformed;
                    frame = ErrorFrame(outcome);
                    return outcome;
                }
                JsonObject closed = new JsonObject();
                closed.Add("type", "transcript");
                closed.Add("transcript", transcript);
                closed.Add("final", true);
                frame = Render(closed);
                return GatewayOutcome.Applied;
            }
            catch (Exception)
            {
                outcome = GatewayOutcome.Malformed;
                frame = ErrorFrame(outcome);
                return outcome;
            }
        }

        public static GatewayOutcome ParseSam(int status, string body, CaptureId capture,
            out string frame)
        {
            frame = null;
            GatewayOutcome outcome = StatusOutcome(status);
            if (outcome != GatewayOutcome.Applied)
            {
                return outcome;
            }
            try
            {
                JsonObject root = ParseObject(body);
                if (root == null || TryError(root, out outcome))
                {
                    return root == null ? GatewayOutcome.Malformed : outcome;
                }
                if (root.GetString("status", "sam") != "completed")
                {
                    return GatewayOutcome.Malformed;
                }
                JsonArray output = root.GetArray("output", "sam");
                JsonArray outlines = new JsonArray();
                bool lane = false;
                for (int i = 0; i < output.Count; i++)
                {
                    JsonObject item = output.GetObject(i, "sam.output");
                    if (item.GetString("type", "sam.output") != "message")
                    {
                        continue;
                    }
                    JsonArray content = item.GetArray("content", "sam.output.message");
                    for (int c = 0; c < content.Count; c++)
                    {
                        JsonObject part = content.GetObject(c, "sam.output.message.content");
                        if (part.GetString("type", "sam.output.message.content") != "output_text")
                        {
                            continue;
                        }
                        string text = part.GetString("text", "sam.output.message.content");
                        lane = true;
                        if (text.Length == 0)
                        {
                            continue;
                        }
                        if (!AppendSamOutlines(text, outlines))
                        {
                            return GatewayOutcome.Malformed;
                        }
                    }
                }
                if (!lane)
                {
                    return GatewayOutcome.Malformed;
                }
                JsonObject closed = new JsonObject();
                closed.Add("capture", (long)capture);
                closed.Add("outlines", outlines);
                frame = Render(closed);
                return GatewayOutcome.Applied;
            }
            catch (Exception)
            {
                return GatewayOutcome.Malformed;
            }
        }

        // Documented Responses request body for a SAM still image: one user
        // message pairing one noun phrase with the caller's image URL.
        public static string BuildSamRequest(string modelId, string phrase, string imageUrl)
        {
            JsonObject text = new JsonObject();
            text.Add("type", "input_text");
            text.Add("text", phrase ?? string.Empty);
            JsonObject image = new JsonObject();
            image.Add("type", "input_image");
            image.Add("image_url", imageUrl ?? string.Empty);
            JsonArray content = new JsonArray();
            content.Add(text);
            content.Add(image);
            JsonObject message = new JsonObject();
            message.Add("type", "message");
            message.Add("role", "user");
            message.Add("content", content);
            JsonArray input = new JsonArray();
            input.Add(message);
            JsonObject root = new JsonObject();
            root.Add("model", modelId ?? string.Empty);
            root.Add("input", input);
            return Render(root);
        }

        // Documented realtime handshake. The caller drops its key reference
        // immediately after this returns and never logs the result.
        public static string VoiceHandshake(string key, string modelId)
        {
            JsonObject authorization = new JsonObject();
            authorization.Add("accessToken", "Bearer " + (key ?? string.Empty));
            JsonObject root = new JsonObject();
            root.Add("authorization", authorization);
            root.Add("audioEncoding", "PCM_24KHZ");
            root.Add("model", modelId ?? string.Empty);
            root.Add("mode", "PUSH_TO_TALK");
            return Render(root);
        }

        // Provider correlation IDs are external metadata. Reject a credential
        // shape on the raw text before removing anything, so stripping a
        // delimiter can never turn secret material into an accepted id.
        public static string SanitizeProviderId(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0 || LooksSecret(value))
            {
                return string.Empty;
            }
            StringBuilder text = new StringBuilder(Math.Min(value.Length, maxLength));
            for (int i = 0; i < value.Length && text.Length < maxLength; i++)
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

        // Sitting diagnostics keep fixed service labels readable while still
        // rejecting credential-shaped text before anything is logged.
        public static string SanitizeDiagnosticValue(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0 || LooksSecret(value))
            {
                return string.Empty;
            }
            StringBuilder text = new StringBuilder(Math.Min(value.Length, maxLength));
            for (int i = 0; i < value.Length && text.Length < maxLength; i++)
            {
                char c = value[i];
                if (c >= ' ' && c < 127)
                {
                    text.Append(c);
                }
            }
            return text.ToString();
        }

        // Diagnostics may name a documented provider error type or code, but
        // never the provider message, a header or any part of the body.
        public static string ProviderErrorId(string body)
        {
            JsonObject root = ParseObject(body);
            if (root == null)
            {
                return string.Empty;
            }
            try
            {
                if (root.Has("error") && !root.IsNull("error", "provider.error"))
                {
                    JsonObject error = root.GetObject("error", "provider.error");
                    string value = SanitizeProviderId(OptionalString(error, "type"), 64);
                    if (value.Length == 0)
                    {
                        value = SanitizeProviderId(OptionalString(error, "code"), 64);
                    }
                    if (value.Length != 0)
                    {
                        return value;
                    }
                }
                string typed = SanitizeProviderId(OptionalString(root, "errorType"), 64);
                if (typed.Length != 0)
                {
                    return typed;
                }
                return SanitizeProviderId(OptionalString(root, "errorCode"), 64);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string VoiceEventType(string body)
        {
            JsonObject root = ParseObject(body);
            return root == null ? string.Empty
                : SanitizeProviderId(OptionalString(root, "type"), 64);
        }

        // The sitting row reports the boundary's settled record for this request.
        // The first terminal row wins, so a late or duplicate reply cannot
        // relabel a request that already timed out or fell back.
        public static GatewayOutcome? SittingOutcome(ProposalBoundary boundary,
            GatewayRequestId id)
        {
            if (boundary == null)
            {
                return null;
            }
            IReadOnlyList<ProposalLogEntry> rows = boundary.DecisionLog;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].RequestId == (long)id)
                {
                    return rows[i].Outcome;
                }
            }
            return null;
        }

        // One measured real-time delta for the shared boundary, then expiry and
        // fallback before any reply is delivered. Call once per frame.
        public static void AdvanceSitting(ProposalBoundary boundary, double elapsedRealSeconds)
        {
            if (boundary == null)
            {
                throw new ArgumentNullException("boundary");
            }
            boundary.AdvanceRealSeconds(elapsedRealSeconds);
            boundary.Tick();
        }

        public static void AdvanceSitting(ProposalBoundary boundary, ImageAdapter image,
            double elapsedRealSeconds)
        {
            AdvanceSitting(boundary, elapsedRealSeconds);
            if (image != null)
            {
                image.SettleExpiredPending();
            }
        }

        public static string[] SittingServices(bool od4Open)
        {
            return od4Open
                ? new[] { "Muse Image", "Muse Voice Transcribe", "SAM 3.1" }
                : new[] { "Muse Image", "Muse Voice Transcribe", "SAM 3.1", "d1-ultra" };
        }

        public static bool BeginSittingDecision(bool od4Open, Action send,
            Action<string> reportDeferred)
        {
            if (od4Open)
            {
                if (reportDeferred != null) reportDeferred("deferred-local-fallback");
                return false;
            }
            if (send != null) send();
            return true;
        }

        // The handshake acknowledgement is the only server frame without a type
        // field. A terminal error (or any untyped frame without a session id)
        // must stop audio and settle from the sanitized frame instead.
        public static bool VoiceCanSendAudio(string firstFrame, out string terminalFrame)
        {
            return VoiceCanSendAudio(firstFrame, false, null, out terminalFrame);
        }

        public static bool VoiceCanSendAudio(string firstFrame, bool sitting,
            Action<string, string, string> reportDiagnostic, out string terminalFrame)
        {
            terminalFrame = null;
            JsonObject root = ParseObject(firstFrame);
            if (root == null)
            {
                return false;
            }
            string type = OptionalString(root, "type");
            if (type.Length != 0)
            {
                GatewayOutcome failure = type == "error"
                    ? ErrorOutcome(root) : GatewayOutcome.Malformed;
                if (sitting && reportDiagnostic != null)
                {
                    reportDiagnostic(SanitizeProviderId(type, 64),
                        SanitizeProviderId(OptionalString(root, "errorType"), 64),
                        SanitizeProviderId(OptionalString(root, "errorCode"), 64));
                }
                terminalFrame = ErrorFrame(failure);
                return false;
            }
            if (OptionalString(root, "sessionId").Length == 0)
            {
                terminalFrame = ErrorFrame(GatewayOutcome.Malformed);
                return false;
            }
            return true;
        }

        // A local deadline is Timeout in every connection phase. Only content
        // that actually arrived without a cancellation can be Malformed.
        public static GatewayOutcome VoiceTerminal(bool connected, bool deadlineCancelled,
            string body, out string frame)
        {
            if (deadlineCancelled)
            {
                frame = ErrorFrame(GatewayOutcome.Timeout);
                return GatewayOutcome.Timeout;
            }
            return ParseVoice(connected ? 200 : 0, body, out frame);
        }

        // Deliver a typed parser/socket outcome once through the real consumer.
        // Unprovisioned has no error-frame vocabulary, so it settles the real
        // boundary directly instead of being re-encoded as Malformed.
        public static ProposalVerdict DeliverVoice(VoiceAdapter adapter,
            ProposalBoundary boundary, GatewayRequestId id, GatewayOutcome outcome,
            string frame)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException("adapter");
            }
            if (boundary == null)
            {
                throw new ArgumentNullException("boundary");
            }
            if (outcome == GatewayOutcome.Unprovisioned)
            {
                return boundary.CompleteFailure(id, outcome);
            }
            return adapter.Complete(id, frame);
        }

        // Send-time credential gate for the realtime sitting. An absent key
        // settles Unprovisioned once, reports once, and sends nothing; the
        // caller must stop without a sitting row.
        public static bool BeginVoice(VoiceAdapter adapter, ProposalBoundary boundary,
            GatewayRequestId id, bool hasCredential, Action send, Action reportNoKey)
        {
            if (boundary == null)
            {
                throw new ArgumentNullException("boundary");
            }
            if (!hasCredential)
            {
                boundary.CompleteFailure(id, GatewayOutcome.Unprovisioned);
                if (reportNoKey != null)
                {
                    reportNoKey();
                }
                return false;
            }
            if (send != null)
            {
                send();
            }
            return true;
        }

        private static GatewayOutcome StatusOutcome(int status)
        {
            if (status >= 200 && status < 300)
            {
                return GatewayOutcome.Applied;
            }
            if (status == 401 || status == 403 || status == 429)
            {
                return GatewayOutcome.Denied;
            }
            if (status == 408 || status == 504 || status == 0)
            {
                return GatewayOutcome.Timeout;
            }
            if (status == 404)
            {
                return GatewayOutcome.Unprovisioned;
            }
            return GatewayOutcome.Malformed;
        }

        private static JsonObject ParseObject(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }
            try
            {
                return JsonParser.ParseSamFrame(body) as JsonObject;
            }
            catch (JsonSaveException)
            {
                return null;
            }
        }

        // An error envelope is terminal even when the HTTP status is 200.
        // Only fixed outcome classes leave this method; provider text is never
        // copied into a response, frame, decision log or console.
        private static bool TryError(JsonObject root, out GatewayOutcome outcome)
        {
            outcome = GatewayOutcome.Malformed;
            // Responses requires a nullable error field; null means success.
            if (!root.Has("error") || root.IsNull("error", "error"))
            {
                return false;
            }
            try
            {
                JsonObject error = root.GetObject("error", "error");
                string code = OptionalString(error, "code");
                string type = OptionalString(error, "type");
                string message = OptionalString(error, "message");
                string all = (code + " " + type + " " + message).ToLowerInvariant();
                if (all.Contains("permission_denied") || all.Contains("permission denied")
                    || all.Contains("unauthorized") || all.Contains("forbidden")
                    || all.Contains("authentication"))
                {
                    outcome = GatewayOutcome.Denied;
                }
                else if (all.Contains("timeout") || all.Contains("timed out"))
                {
                    outcome = GatewayOutcome.Timeout;
                }
                else if (all.Contains("not found") || all.Contains("unavailable"))
                {
                    outcome = GatewayOutcome.Unprovisioned;
                }
            }
            catch (Exception)
            {
                outcome = GatewayOutcome.Malformed;
            }
            return true;
        }

        private static GatewayOutcome ErrorOutcome(JsonObject root)
        {
            try
            {
                string message = OptionalString(root, "message").ToLowerInvariant();
                if (message.Contains("permission") || message.Contains("denied")
                    || message.Contains("unauthorized") || message.Contains("forbidden"))
                {
                    return GatewayOutcome.Denied;
                }
                if (message.Contains("timeout") || message.Contains("timed out"))
                {
                    return GatewayOutcome.Timeout;
                }
            }
            catch (Exception)
            {
            }
            return GatewayOutcome.Malformed;
        }

        private static string OptionalString(JsonObject root, string key)
        {
            try
            {
                return root.Has(key) ? root.GetString(key, "wire") : string.Empty;
            }
            catch (JsonSaveException)
            {
                return string.Empty;
            }
        }

        private static string ErrorFrame(GatewayOutcome outcome)
        {
            string message = outcome == GatewayOutcome.Denied ? "permission denied"
                : outcome == GatewayOutcome.Timeout ? "timeout"
                : "malformed response";
            JsonObject frame = new JsonObject();
            frame.Add("type", "error");
            frame.Add("message", message);
            return Render(frame);
        }

        private static string Render(JsonObject value)
        {
            StringBuilder builder = new StringBuilder();
            value.Render(builder);
            return builder.ToString();
        }

        // SAM 3.1 segmentation grammar: <Nf> then id, box and mask tokens
        // (https://dev.meta.ai/docs/sam/reading-segmentation).
        // shortcut: an outline is the documented source-pixel box, not decoded
        // mask pixels; upgrade when pixel-precise cutouts are required.
        private const string SamDigitAlphabet =
            "!#$%&'()*+-./0123456789:=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{~";

        private static bool AppendSamOutlines(string text, JsonArray outlines)
        {
            int before = outlines.Count;
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    end = text.Length;
                }
                string line = text.Substring(start, end - start).Trim();
                start = end + 1;
                if (line.Length == 0)
                {
                    continue;
                }
                if (!AppendSamLine(line, outlines))
                {
                    return false;
                }
            }
            return outlines.Count > before;
        }

        private static bool AppendSamLine(string line, JsonArray outlines)
        {
            int markerEnd = line.IndexOf('>');
            if (line.Length < 3 || line[0] != '<' || markerEnd < 2
                || line[markerEnd - 1] != 'f')
            {
                return false;
            }
            int before = outlines.Count;
            int cursor = markerEnd + 1;
            while (cursor < line.Length)
            {
                if (line[cursor] == ',')
                {
                    cursor++;
                    continue;
                }
                int idStart = cursor;
                while (cursor < line.Length && line[cursor] >= '0' && line[cursor] <= '9')
                {
                    cursor++;
                }
                if (cursor == idStart || cursor >= line.Length || line[cursor] != '<')
                {
                    return false;
                }
                int boxEnd = line.IndexOf("|>", cursor, StringComparison.Ordinal);
                if (boxEnd < 0)
                {
                    return false;
                }
                string box = line.Substring(cursor, boxEnd + 2 - cursor);
                long x1;
                long y1;
                long x2;
                long y2;
                if (!TrySamBox(box, out x1, out y1, out x2, out y2))
                {
                    return false;
                }
                cursor = boxEnd + 2;
                int maskEnd = line.IndexOf("|>", cursor, StringComparison.Ordinal);
                if (maskEnd < 0)
                {
                    return false;
                }
                string mask = line.Substring(cursor, maskEnd + 2 - cursor);
                if (!TrySamMask(mask, x2 - x1 + 1L, y2 - y1 + 1L))
                {
                    return false;
                }
                cursor = maskEnd + 2;
                JsonArray polygon = new JsonArray();
                AppendPoint(polygon, x1, y1);
                AppendPoint(polygon, x2, y1);
                AppendPoint(polygon, x2, y2);
                AppendPoint(polygon, x1, y2);
                JsonObject outline = new JsonObject();
                outline.Add("polygon", polygon);
                outlines.Add(outline);
            }
            return outlines.Count > before;
        }

        private static bool TrySamBox(string token, out long x1, out long y1,
            out long x2, out long y2)
        {
            x1 = y1 = x2 = y2 = 0L;
            long w = 0L;
            long h = 0L;
            bool hasX1 = false;
            bool hasY1 = false;
            bool hasX2 = false;
            bool hasY2 = false;
            bool hasW = false;
            bool hasH = false;
            if (!token.StartsWith("<|box;", StringComparison.Ordinal)
                || !token.EndsWith("|>", StringComparison.Ordinal))
            {
                return false;
            }
            string[] fields = token.Substring(6, token.Length - 8).Split(';');
            for (int i = 0; i < fields.Length; i++)
            {
                int equals = fields[i].IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }
                string key = fields[i].Substring(0, equals).Trim();
                string value = fields[i].Substring(equals + 1).Trim();
                switch (key)
                {
                    case "x1":
                        if (!TrySamLong(value, out x1)) return false;
                        hasX1 = true;
                        break;
                    case "y1":
                        if (!TrySamLong(value, out y1)) return false;
                        hasY1 = true;
                        break;
                    case "x2":
                        if (!TrySamLong(value, out x2)) return false;
                        hasX2 = true;
                        break;
                    case "y2":
                        if (!TrySamLong(value, out y2)) return false;
                        hasY2 = true;
                        break;
                    case "w":
                        if (!TrySamLong(value, out w)) return false;
                        hasW = true;
                        break;
                    case "h":
                        if (!TrySamLong(value, out h)) return false;
                        hasH = true;
                        break;
                }
            }
            return hasX1 && hasY1 && hasX2 && hasY2 && hasW && hasH
                && w > 0L && h > 0L && x1 >= 0L && y1 >= 0L && x2 >= x1 && y2 >= y1
                && x2 < w && y2 < h;
        }

        private static bool TrySamMask(string token, long boxWidth, long boxHeight)
        {
            long x = 0L;
            long y = 0L;
            bool hasX = false;
            bool hasY = false;
            bool hasData = false;
            string data = null;
            if (!token.StartsWith("<|mask;", StringComparison.Ordinal)
                || !token.EndsWith("|>", StringComparison.Ordinal))
            {
                return false;
            }
            string[] fields = token.Substring(7, token.Length - 9).Split(';');
            for (int i = 0; i < fields.Length; i++)
            {
                int equals = fields[i].IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }
                string key = fields[i].Substring(0, equals).Trim();
                string value = fields[i].Substring(equals + 1).Trim();
                if (key == "x")
                {
                    if (!TrySamLong(value, out x)) return false;
                    hasX = true;
                }
                else if (key == "y")
                {
                    if (!TrySamLong(value, out y)) return false;
                    hasY = true;
                }
                else if (key == "data")
                {
                    data = value;
                    hasData = true;
                }
            }
            if (!hasX || !hasY || !hasData || x != 0L || y != 0L)
            {
                return false;
            }
            string[] parts = data.Split(',');
            if (parts.Length != 3)
            {
                return false;
            }
            long height;
            long width;
            if (!TrySamLong(parts[0], out height) || !TrySamLong(parts[1], out width)
                || height <= 0L || width <= 0L
                || height != boxHeight || width != boxWidth)
            {
                return false;
            }
            string payload = parts[2];
            if (payload.Length < 6)
            {
                return false;
            }
            char encoding = payload[0];
            if (encoding != '~' && encoding != '!')
            {
                return false;
            }
            for (int i = 1; i < payload.Length; i++)
            {
                if (SamDigitAlphabet.IndexOf(payload[i]) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static void AppendPoint(JsonArray polygon, long x, long y)
        {
            JsonArray point = new JsonArray();
            point.Add(new JsonNumber(x.ToString(CultureInfo.InvariantCulture)));
            point.Add(new JsonNumber(y.ToString(CultureInfo.InvariantCulture)));
            polygon.Add(point);
        }

        private static bool TrySamLong(string value, out long parsed)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out parsed);
        }

        private static bool IsPng(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return false;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return bytes.Length > 8
                    && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
                    && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsActivityCode(string code)
        {
            if (string.IsNullOrEmpty(code) || !code.StartsWith("ACT-", StringComparison.Ordinal))
            {
                return false;
            }
            int separator = code.IndexOf('-', 4);
            if (separator <= 4 || separator >= code.Length - 2)
            {
                return false;
            }
            for (int i = 4; i < separator; i++)
            {
                if (code[i] < 'A' || code[i] > 'Z')
                {
                    return false;
                }
            }
            int digits = code.Length - separator - 1;
            if (digits < 2 || digits > 4)
            {
                return false;
            }
            for (int i = separator + 1; i < code.Length; i++)
            {
                if (code[i] < '0' || code[i] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        // Reject content that looks like credential material. An observed
        // secret-shaped value is treated as malformed and never echoed.
        private static bool LooksSecret(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string lower = text.ToLowerInvariant();
            if (lower.Contains("bearer ") || lower.Contains("authorization")
                || lower.Contains("api_key") || lower.Contains("model_api_key=")
                || lower.Contains("access_token"))
            {
                return true;
            }
            for (int i = 0; i + 3 <= text.Length; i++)
            {
                if ((text[i] == 's' || text[i] == 'S') && text[i + 1] == 'k'
                    && text[i + 2] == '-' && i + 10 <= text.Length)
                {
                    return true;
                }
            }
            int hexRun = 0;
            int tokenRun = 0;
            bool tokenHasPunctuation = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                hexRun = hex ? hexRun + 1 : 0;
                if (hexRun >= 32)
                {
                    return true;
                }
                bool token = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z') || c == '+' || c == '/' || c == '='
                    || c == '_' || c == '-';
                if (token)
                {
                    tokenRun++;
                    tokenHasPunctuation |= c == '+' || c == '/' || c == '=' || c == '_' || c == '-';
                }
                else
                {
                    tokenRun = 0;
                    tokenHasPunctuation = false;
                }
                if (tokenRun >= 64 && tokenHasPunctuation)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
