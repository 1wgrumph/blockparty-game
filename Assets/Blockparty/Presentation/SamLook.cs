// SAM-LOOK (owner, 2026-11-04): "Look" finds what the player names in the
// current view. SAM@1 (CaptureSearch, CONTRACT-BP-07) still returns image
// outlines only; per the owner amendment of 2026-11-04 the engine maps the
// best outline's centre to the world object under it (the director's raycast,
// passed in as resolve), so SAM never names a resident or a place. A resident
// is selected and followed by the engine camera (no per-frame model calls); a
// place shows its name and what can be done there (BLOCK-SITES slot
// activities and the activity stages); no outline or no service gives a
// friendly reply. Plain C# 9, no UnityEngine, so the SAM-LOOK probe compiles it
// with the simulation sources.
using System;
using System.Collections.Generic;
using Blockparty.AI;
using Blockparty.Contracts;

namespace Blockparty.Presentation
{
    public enum LookKind { None, Resident, Place, Unknown, Unavailable }

    // A spot the engine can resolve a ground point to: the nearest within its radius wins.
    public sealed class LookPlace
    {
        public readonly string Name;
        public readonly float X, Z, Radius;
        public readonly string[] Actions;

        public LookPlace(string name, float x, float z, float radius, string[] actions)
        {
            Name = name ?? string.Empty;
            X = x;
            Z = z;
            Radius = radius;
            Actions = actions ?? new string[0];
        }
    }

    // What the engine found under an image point: a resident, or a ground point.
    public struct LookHit
    {
        public ResidentId? Resident;
        public string ResidentName;
        public bool HasGround;
        public float X, Z;
    }

    public sealed class SamLook
    {
        // Voice wiring entry: SamLook.Find("the woman in the red shirt") queues one look
        // in the running game. False when no game is bound, a look is running, or the phrase is empty.
        public static SamLook Active { get; set; }
        public static bool Find(string phrase) { return Active != null && Active.Ask(phrase); }

        public const string Disclosure = "Look sends a picture of your view to Meta SAM, which outlines what you name.";
        const string Unavailable = "I can't look right now (image search is unavailable). You can still pick residents by name.";
        static readonly IReadOnlyList<SamOutline> NoOutlines = new List<SamOutline>().AsReadOnly();

        readonly CaptureSearch search;
        readonly CaptureSearchBridge bridge;
        GatewayRequest request;
        string pending;
        long nextCapture = 1L;
        bool hidden;               // from Ask until Settle, so no stale outline reaches the capture

        public SamLook(ProposalBoundary boundary)
        {
            search = new CaptureSearch(boundary);
            bridge = new CaptureSearchBridge(search);
            Clear();
            Draft = string.Empty;
        }

        public bool Open;          // the typed Look box has the keyboard
        public string Draft;
        public CaptureSearch Search { get { return search; } }
        public GatewayRequest Request { get { return request; } }
        public bool Busy { get { return request != null || pending != null; } }
        public CaptureId Capture { get; private set; }
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public string Phrase { get; private set; }
        public string Reply { get; private set; }
        public LookKind Kind { get; private set; }
        public int Best { get; private set; }
        public ResidentId? Resident { get; private set; }
        public string TargetName { get; private set; }
        public string[] Actions { get; private set; }
        public IReadOnlyList<SamOutline> Outlines
        {
            get { return bridge.Result == null || hidden ? NoOutlines : bridge.Result.Outlines; }
        }

        // Queues a look; the director captures the next clean frame and calls Begin.
        public bool Ask(string phrase)
        {
            string p = (phrase ?? string.Empty).Trim();
            if (p.Length == 0) { Reply = "Type what to look for, like \"a bike\"."; return false; }
            if (Busy) { Reply = "Still looking for \"" + Phrase + "\"."; return false; }
            Clear();
            hidden = true;
            pending = p;
            Phrase = p;
            Open = false;
            Reply = "Looking for \"" + p + "\"...";
            return true;
        }

        public bool TakePending(out string phrase)
        {
            phrase = pending;
            pending = null;
            return phrase != null;
        }

        // Starts SAM on a fresh capture. Null when the service refuses locally (Reply says so).
        // disclosureGiven: the Look box and button tooltip show Disclosure before any send.
        public GatewayRequest Begin(string phrase, byte[] rgba, int width, int height,
            ServiceEndpoint endpoint, bool hasCredential)
        {
            Phrase = phrase ?? string.Empty;
            Capture = (CaptureId)nextCapture++;
            ImageWidth = width;
            ImageHeight = height;
            search.SetCapture(Capture, rgba, width, height);
            request = search.Request(Phrase, endpoint, hasCredential, true);
            if (request == null) { Kind = LookKind.Unavailable; Reply = Unavailable; hidden = false; }
            return request;
        }

        public void Tick(double elapsedRealSeconds)
        {
            if (request != null) search.Tick(elapsedRealSeconds);
        }

        // After the transport settled the request: pick the largest outline, ask the
        // engine what is under its centre (image pixels, y down) and decide the reply.
        public LookKind Settle(Func<float, float, LookHit> resolve, IList<LookPlace> places)
        {
            request = null;
            hidden = false;
            SamResult result = bridge.Result;
            if (result == null)
            {
                Kind = LookKind.Unavailable;
                Reply = Unavailable;
                return Kind;
            }
            if (result.Outlines.Count == 0)
            {
                Kind = LookKind.None;
                Reply = "I don't see \"" + Phrase + "\" in this view. Try moving the camera or other words.";
                return Kind;
            }
            // Largest outline the engine can see into; one over the HUD resolves to nothing and is skipped.
            Best = Largest(result.Outlines);
            LookHit hit = At(result.Outlines[Best], resolve);
            if (!hit.Resident.HasValue && !hit.HasGround)
            {
                double area = -1.0;
                for (int i = 0; i < result.Outlines.Count; i++)
                {
                    double a = Area(result.Outlines[i].Polygon);
                    if (a <= area) continue;
                    LookHit h = At(result.Outlines[i], resolve);
                    if (h.Resident.HasValue || h.HasGround) { area = a; Best = i; hit = h; }
                }
            }
            LookPlace place;
            if (hit.Resident.HasValue)
            {
                Kind = LookKind.Resident;
                Resident = hit.Resident;
                TargetName = hit.ResidentName ?? string.Empty;
                Reply = "That's " + TargetName + ". Following them.";
            }
            else if (hit.HasGround && (place = Nearest(places, hit.X, hit.Z)) != null)
            {
                Kind = LookKind.Place;
                TargetName = place.Name;
                Actions = place.Actions;
                Reply = place.Actions.Length == 0 ? place.Name + ". Nothing to do there yet."
                    : place.Name + ". Here you can " + string.Join(", ", place.Actions) + ".";
            }
            else
            {
                Kind = LookKind.Unknown;
                TargetName = Phrase;
                Reply = "I outlined \"" + Phrase + "\", but it isn't a resident or a place you can use.";
            }
            return Kind;
        }

        static LookHit At(SamOutline outline, Func<float, float, LookHit> resolve)
        {
            float cx, cy;
            Centre(outline, out cx, out cy);
            return resolve(cx, cy);
        }

        void Clear()
        {
            Kind = LookKind.None;
            Best = -1;
            Resident = null;
            TargetName = string.Empty;
            Actions = new string[0];
            Phrase = string.Empty;
            Reply = string.Empty;
        }

        public static int Largest(IReadOnlyList<SamOutline> outlines)
        {
            int best = 0;
            double area = -1.0;
            for (int i = 0; i < outlines.Count; i++)
            {
                double a = Area(outlines[i].Polygon);
                if (a > area) { area = a; best = i; }
            }
            return best;
        }

        static double Area(IReadOnlyList<ImagePoint> p)
        {
            double sum = 0.0;
            for (int i = 0; i < p.Count; i++)
            {
                ImagePoint a = p[i], b = p[(i + 1) % p.Count];
                sum += (double)a.X * b.Y - (double)b.X * a.Y;
            }
            return Math.Abs(sum) / 2.0;
        }

        // Bounding-box centre: LIVE-WIRE outlines are boxes, and it stays inside convex shapes.
        public static void Centre(SamOutline outline, out float x, out float y)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (ImagePoint p in outline.Polygon)
            {
                x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
                y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
            }
            x = (x0 + x1) / 2f;
            y = (y0 + y1) / 2f;
        }

        // Nearest place relative to its radius, so a slot beats the building around it.
        public static LookPlace Nearest(IList<LookPlace> places, float x, float z)
        {
            LookPlace best = null;
            double score = 1.0;
            if (places == null) return null;
            foreach (LookPlace p in places)
            {
                double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z)) / Math.Max(0.01f, p.Radius);
                if (d <= score) { score = d; best = p; }
            }
            return best;
        }

        // Player words for the activity codes BLOCK-SITES slots and the activity tray use.
        public static string ActivityName(string code)
        {
            switch (code)
            {
                case "ACT-FOOD-01": return "eat";
                case "ACT-WORK-01": return "work a barista shift";
                case "ACT-WORK-02": return "work as a technician";
                case "ACT-WORK-03": return "look for a job";
                case "ACT-WORK-04": return "work as an AI engineer";
                case "ACT-EDU-01": return "go to school";
                case "ACT-REL-01": return "chat with someone";
                case "ACT-PARTY-01": return "throw a party";
                case "ACT-SPORT-02": return "play minigolf";
                case "ACT-HEALTH-02": return "rest";
                case "ACT-CLUB-01": return "join the club";
                case "ACT-HOME-01": return "relax at home";
                case "ACT-CREA-02": return "make crafts";
                default: return code ?? string.Empty;
            }
        }

        // "common-room" -> "Common room".
        public static string Pretty(string id)
        {
            string s = (id ?? string.Empty).Replace('-', ' ').ToLowerInvariant();
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
