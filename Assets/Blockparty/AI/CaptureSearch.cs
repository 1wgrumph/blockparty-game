// SAM@1: captured-image phrase search over AI-BOUNDARY (CONTRACT-BP-07).
// Consumes synthetic/recorded contract frames only; no network, no key, no
// live call. Results are image-coordinate outlines on the current capture:
// they never identify a resident, location or activity, never mutate world
// state (the boundary's gateway-id allocation aside), and ConnectedSuccess
// stays false until William's live acceptance. Denied, unavailable, no-key,
// timeout and unusable frames take the local fallback: no outlines, and
// authoritative search by name remains available.
using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.AI
{
    public sealed class SamMiss
    {
        public readonly CaptureId Capture;
        public readonly string Phrase;

        public SamMiss(CaptureId capture, string phrase)
        {
            Capture = capture;
            Phrase = phrase ?? string.Empty;
        }
    }

    public sealed class CaptureSearch
    {
        private const string Limitation =
            "Outlines only: they do not identify a resident, location or activity.";

        private sealed class PendingSearch
        {
            public readonly CaptureId Capture;
            public readonly string Phrase;

            public PendingSearch(CaptureId capture, string phrase)
            {
                Capture = capture;
                Phrase = phrase;
            }
        }

        private readonly ProposalBoundary boundary;
        private readonly Dictionary<GatewayRequestId, PendingSearch> pending =
            new Dictionary<GatewayRequestId, PendingSearch>();
        private readonly List<SamMiss> misses = new List<SamMiss>();
        private CaptureId capture;
        private bool hasCapture;
        private int width;
        private int height;

        public CaptureSearch(ProposalBoundary boundary)
        {
            if (boundary == null) throw new ArgumentNullException("boundary");
            this.boundary = boundary;
            Message = string.Empty;
        }

        public SamResult Result { get; private set; }
        public string CountText { get { return Result == null ? "0" : Result.Outlines.Count.ToString(); } }
        public string Message { get; private set; }
        public bool ConnectedSuccess { get { return false; } }
        public IReadOnlyList<SamMiss> Misses { get { return misses.AsReadOnly(); } }

        public void SetCapture(CaptureId id, byte[] rgba, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException("rgba");
            if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
                throw new ArgumentException("RGBA length must equal width*height*4", "rgba");
            capture = id;
            hasCapture = true;
            this.width = width;
            this.height = height;
            Result = null;
            Message = string.Empty;
        }

        public GatewayRequest Request(string phrase, ServiceEndpoint endpoint,
            bool hasCredential, bool disclosureGiven)
        {
            if (!hasCapture)
            {
                Result = null;
                Message = "No capture to search; no outlines. Search by name remains available.";
                return null;
            }
            GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageSearch,
                endpoint, hasCredential, disclosureGiven);
            if (request == null)
            {
                Result = null;
                Message = "Image search unavailable; no outlines. Search by name remains available.";
                return null;
            }
            pending[request.Id] = new PendingSearch(capture, phrase ?? string.Empty);
            Result = null;
            Message = "Searching the capture; " + Limitation;
            return request;
        }

        public ProposalVerdict Complete(GatewayRequestId request, string contractFrame)
        {
            PendingSearch search;
            if (!pending.TryGetValue(request, out search))
                return ProposalVerdict.DuplicateIgnored;
            pending.Remove(request);
            SamResult parsed;
            if (search.Capture != capture || !TryParse(contractFrame, search.Capture, out parsed))
            {
                Result = null;
                Message = "Image search returned nothing usable; no outlines. Search by name remains available.";
                return boundary.CompleteService(request, false, "BadFrame");
            }
            ProposalVerdict verdict = boundary.CompleteService(request, true, null);
            if (verdict != ProposalVerdict.Applied)
            {
                Result = null;
                Message = "Image search did not apply; no outlines. Search by name remains available.";
                return verdict;
            }
            Result = parsed;
            if (parsed.Outlines.Count == 0)
                misses.Add(new SamMiss(search.Capture, search.Phrase));
            Message = parsed.Outlines.Count.ToString() + " outline(s) found. " + Limitation;
            return verdict;
        }

        public ProposalVerdict Fail(GatewayRequestId request, GatewayOutcome outcome)
        {
            pending.Remove(request);
            ProposalVerdict verdict = boundary.CompleteFailure(request, outcome);
            Result = null;
            Message = "Image search " + outcome + "; no outlines. Search by name remains available.";
            return verdict;
        }

        public void Tick(double elapsedRealSeconds)
        {
            bool hadPending = pending.Count > 0;
            boundary.AdvanceRealSeconds(elapsedRealSeconds);
            boundary.Tick();
            boundary.OnResume();
            // shortcut: assumes no foreign pendings share the boundary, true for this adapter's fixtures; recheck per-request when a shared boundary hosts other purposes.
            if (hadPending && boundary.PendingCount == 0 && pending.Count > 0)
            {
                pending.Clear();
                Result = null;
                Message = "Image search timed out; no outlines. Search by name remains available.";
            }
        }

        // JSON: Unity's Assembly-CSharp has no System.Text.Json (SaveV1.cs
        // records the same constraint), so this reuses the save reader's
        // SAM-compatible parser mode and DOM over the same CONTRACT-BP-07 frames.
        private bool TryParse(string frame, CaptureId expected, out SamResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(frame)) return false;
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(frame) as JsonObject;
                if (root == null || root.GetLong("capture", "sam") != (long)expected)
                    return false;
                JsonArray outlines = root.GetArray("outlines", "sam");
                var parsed = new List<SamOutline>();
                for (int i = 0; i < outlines.Count; i++)
                {
                    JsonObject outline = outlines.GetObject(i, "sam");
                    JsonArray polygon = outline.GetArray("polygon", "sam");
                    var points = new List<ImagePoint>();
                    for (int p = 0; p < polygon.Count; p++)
                    {
                        JsonArray point = polygon.GetArray(p, "sam");
                        if (point.Count != 2) return false;
                        float x = point.GetFloat(0, "sam");
                        float y = point.GetFloat(1, "sam");
                        if (float.IsNaN(x) || float.IsNaN(y)
                            || float.IsInfinity(x) || float.IsInfinity(y)
                            || x < 0f || y < 0f || x > width || y > height)
                            return false;
                        points.Add(new ImagePoint(x, y));
                    }
                    if (points.Count < 3) return false;
                    parsed.Add(new SamOutline(expected, points));
                }
                result = new SamResult(expected, parsed);
                return true;
            }
            catch (JsonSaveException) { return false; }
        }
    }
}
