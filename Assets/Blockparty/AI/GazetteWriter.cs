// GAZETTE: the Block Gazette's words and front-page picture over AI-BOUNDARY.
// The engine picks yesterday's stories (Pick); the model only rewrites those
// facts. One Muse Image Responses call per edition (purpose ImageGeneration)
// returns the written page and, when SAM cut the lead resident out of the
// capture, a newspaper illustration of that cut-out. A reply adding any word,
// person or event beyond the supplied facts, missing the lead, or not in the
// three-line form is refused
// and the deterministic Template stands, as it does with no key or offline.
// Plain C# 9, no engine dependency, no network, no credential material.
using System;
using System.Collections.Generic;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Persistence;

namespace Blockparty.AI
{
    public sealed class GazetteFact
    {
        public readonly string Kind;      // Party, Birth, Hire, Friendship, Crisis, Argument
        public readonly long? Resident;   // the resident the picture frames, when the fact has one
        public readonly string Name;      // that resident's display name ("" when none)
        public readonly string Text;      // one engine-written factual sentence
        public readonly int Weight;

        public GazetteFact(string kind, long? resident, string name, string text, int weight)
        {
            Kind = kind ?? string.Empty;
            Resident = resident;
            Name = name ?? string.Empty;
            Text = text ?? string.Empty;
            Weight = weight;
        }
    }

    public sealed class GazettePage
    {
        public string Headline = string.Empty;
        public string Story = string.Empty;
        public readonly List<string> Gossip = new List<string>();
        public bool ModelWritten;
    }

    public sealed class GazetteWriter
    {
        public const int MaxStories = 4;
        public const string SamPhrase = "person";
        const int MaxHeadline = 90, MaxStory = 480, MaxGossip = 160;

        readonly ProposalBoundary boundary;
        readonly Dictionary<GatewayRequestId, List<GazetteFact>> pending =
            new Dictionary<GatewayRequestId, List<GazetteFact>>();

        public GazetteWriter(ProposalBoundary boundary)
        {
            if (boundary == null) throw new ArgumentNullException("boundary");
            this.boundary = boundary;
        }

        // Engine choice: heaviest facts first, ties keep log order.
        public static List<GazetteFact> Pick(IList<GazetteFact> facts)
        {
            List<GazetteFact> sorted = new List<GazetteFact>();
            if (facts == null) return sorted;
            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i] == null || facts[i].Text.Length == 0) continue;
                int at = sorted.Count;
                while (at > 0 && sorted[at - 1].Weight < facts[i].Weight) at--;
                sorted.Insert(at, facts[i]);
            }
            if (sorted.Count > MaxStories) sorted.RemoveRange(MaxStories, sorted.Count - MaxStories);
            return sorted;
        }

        // The resident the front-page picture frames: the first story that names one.
        public static GazetteFact Lead(IList<GazetteFact> top)
        {
            for (int i = 0; top != null && i < top.Count; i++)
                if (top[i].Resident.HasValue && top[i].Name.Length > 0) return top[i];
            return null;
        }

        public static GazettePage Template(IList<GazetteFact> top)
        {
            GazettePage page = new GazettePage();
            if (top == null || top.Count == 0)
            {
                page.Headline = "A Quiet Day on the Block";
                page.Story = "Nothing made the news yesterday. The neighbours kept to their routines.";
                page.Gossip.Add("Word is everyone slept well. Our reporters are waiting by the mailbox.");
                return page;
            }
            GazetteFact lead = top[0];
            page.Headline = Clip(HeadlineFor(lead), MaxHeadline);
            page.Story = lead.Text;
            for (int i = 1; i < top.Count && page.Gossip.Count < 3; i++)
                page.Gossip.Add("Overheard on the block: " + top[i].Text);
            if (page.Gossip.Count == 0) page.Gossip.Add("Gossip was thin yesterday. Tell your neighbours to try harder.");
            return page;
        }

        static string HeadlineFor(GazetteFact f)
        {
            string who = f.Name.Length > 0 ? f.Name : "The Block";
            switch (f.Kind)
            {
                case "Party": return f.Text.IndexOf("called off", StringComparison.Ordinal) >= 0 ? "A Party Is Called Off"
                    : f.Text.IndexOf("hit", StringComparison.Ordinal) >= 0 ? who + "'s Party Is the Talk of the Block"
                    : "Party News: " + who;
                case "Birth": return "Welcome, Little One! " + who + " Has a Baby";
                case "Hire": return who + " Lands a New Job";
                case "Friendship": return "New Friends on the Block";
                case "Crisis": return who + " Has a Rough Day";
                case "Argument": return "Raised Voices on the Block";
                default: return who + " in the News";
            }
        }

        // The facts, and nothing else, go to the model.
        public static string Prompt(IList<GazetteFact> top, bool illustrate)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("You write the Block Gazette, a cheerful one-page neighbourhood newspaper in a cosy life-sim game. ");
            sb.Append("Use ONLY these facts from yesterday. Retell them warmly in your own words, but never add events, people, places, objects or numbers that are not listed, ");
            sb.Append("and keep who did what exactly as written; any new event or person makes the page unusable.\n");
            for (int i = 0; top != null && i < top.Count; i++) sb.Append("- ").Append(top[i].Text).Append('\n');
            if (top == null || top.Count == 0) sb.Append("- Nothing notable happened.\n");
            sb.Append("Reply with exactly three lines:\nHEADLINE: <at most 10 words about the first fact>\n");
            sb.Append("STORY: <2-3 friendly sentences about the first fact>\n");
            sb.Append("GOSSIP: <2-3 short playful lines about the other facts, separated by |>");
            if (illustrate)
                sb.Append("\nAlso redraw the attached cut-out of the resident as a black-and-white newspaper ink illustration, friendly cartoon style, plain paper background.");
            return sb.ToString();
        }

        // Muse Image Responses API body: one user message, optional cut-out image;
        // the image_generation tool only when an illustration is wanted.
        public static string BuildRequest(string modelId, string prompt, string imageDataUrl)
        {
            JsonArray content = new JsonArray();
            JsonObject text = new JsonObject();
            text.Add("type", "input_text");
            text.Add("text", prompt ?? string.Empty);
            content.Add(text);
            if (!string.IsNullOrEmpty(imageDataUrl))
            {
                JsonObject image = new JsonObject();
                image.Add("type", "input_image");
                image.Add("image_url", imageDataUrl);
                content.Add(image);
            }
            JsonObject message = new JsonObject();
            message.Add("type", "message");
            message.Add("role", "user");
            message.Add("content", content);
            JsonArray input = new JsonArray();
            input.Add(message);
            JsonObject root = new JsonObject();
            root.Add("model", modelId ?? string.Empty);
            root.Add("input", input);
            if (!string.IsNullOrEmpty(imageDataUrl))
            {
                JsonObject tool = new JsonObject();
                tool.Add("type", "image_generation");
                JsonArray tools = new JsonArray();
                tools.Add(tool);
                root.Add("tools", tools);
            }
            StringBuilder sb = new StringBuilder();
            root.Render(sb);
            return sb.ToString();
        }

        // The words go to the live text model on /v1/responses; Muse Image itself
        // returns no text there, only pictures on /v1/images/generations.
        public const string TextModel = "muse-spark-1.3";   // live 2026-11-04: d1-ultra answered 404 model_not_found
        public const string ImageUrl = "https://api.meta.ai/v1/images/generations";

        // Muse Image illustration of the lead fact (the ImageAdapter payload shape).
        public static string BuildImageRequest(string modelId, IList<GazetteFact> top)
        {
            GazetteFact lead = Lead(top);
            JsonObject body = new JsonObject();
            body.Add("model", modelId ?? string.Empty);
            // Live 2026-11-04: an unguarded prompt drew a whole invented newspaper page
            // (addresses, phone number, dates); the picture carries no words at all.
            body.Add("prompt", "A single black-and-white ink drawing of one cartoon neighbour, friendly style, plain white background. "
                + "Absolutely no text, letters, words, numbers, signs, captions or newspaper layout anywhere in the image. "
                + "The mood: " + (lead != null ? lead.Text : "a quiet day on the block"));
            body.Add("n", 1);
            body.Add("size", "1024x1024");
            body.Add("response_format", "b64_json");
            body.Add("output_format", "png");
            StringBuilder sb = new StringBuilder();
            body.Render(sb);
            return sb.ToString();
        }

        // The illustration's b64 PNG from an images reply (data[0].b64_json) or a
        // Responses image_generation_call; null when there is none.
        public static string TryReadImage(int status, string body)
        {
            if (status < 200 || status > 299 || string.IsNullOrEmpty(body)) return null;
            string text, image;
            if (TryReadReply(body, out text, out image) || image != null) return image;
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(body) as JsonObject;
                JsonArray data = root == null ? null : root.GetArray("data", "gazette.image");
                string b64 = data != null && data.Count > 0 ? data.GetObject(0, "gazette.image.data").GetString("b64_json", "gazette.image.data") : null;
                return IsPng(b64) ? b64 : null;
            }
            catch (JsonSaveException) { return null; }
        }

        public GatewayRequest Request(IList<GazetteFact> top, ServiceEndpoint endpoint,
            bool hasCredential, bool disclosureGiven)
        {
            if (endpoint == null) throw new ArgumentNullException("endpoint");
            GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageGeneration,
                endpoint, hasCredential, disclosureGiven, LiveWire.ImageGenerationTimeoutRealSeconds);
            if (request != null) pending[request.Id] = new List<GazetteFact>(top ?? new List<GazetteFact>());
            return request;
        }

        // One provider reply. Null page means the template stands; image is the
        // illustration's b64 PNG or null.
        public ProposalVerdict Complete(GatewayRequestId request, int status, string body,
            ICollection<string> residentNames, out GazettePage page, out string imageBase64)
        {
            page = null;
            imageBase64 = null;
            List<GazetteFact> top;
            if (!pending.TryGetValue(request, out top)) return ProposalVerdict.DuplicateIgnored;
            pending.Remove(request);
            if (status < 200 || status > 299)
                return boundary.CompleteFailure(request, status == 401 || status == 403
                    ? GatewayOutcome.Denied : GatewayOutcome.Malformed);
            string text;
            bool ok = TryReadReply(body, out text, out imageBase64) && TryParsePage(text, top, residentNames, out page);
            if (!ok && imageBase64 == null)
            {
                page = null;
                return boundary.CompleteService(request, false, "BadFrame");
            }
            ProposalVerdict verdict = boundary.CompleteService(request, true, null);
            if (verdict != ProposalVerdict.Applied) { page = null; imageBase64 = null; }
            return verdict;
        }

        public ProposalVerdict Fail(GatewayRequestId request, GatewayOutcome outcome)
        {
            pending.Remove(request);
            return boundary.CompleteFailure(request, outcome);
        }

        // Responses output: message/output_text parts and image_generation_call results.
        public static bool TryReadReply(string body, out string text, out string imageBase64)
        {
            text = null;
            imageBase64 = null;
            if (string.IsNullOrEmpty(body)) return false;
            try
            {
                JsonObject root = JsonParser.ParseSamFrame(body) as JsonObject;
                if (root == null) return false;
                JsonArray output = root.GetArray("output", "gazette");
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < output.Count; i++)
                {
                    JsonObject item = output.GetObject(i, "gazette.output");
                    string type = item.GetString("type", "gazette.output");
                    if (type == "message")
                    {
                        JsonArray parts = item.GetArray("content", "gazette.output.message");
                        for (int c = 0; c < parts.Count; c++)
                        {
                            JsonObject part = parts.GetObject(c, "gazette.output.message.content");
                            if (part.GetString("type", "gazette.output.message.content") == "output_text")
                                sb.Append(part.GetString("text", "gazette.output.message.content")).Append('\n');
                        }
                    }
                    else if (type == "image_generation_call" && imageBase64 == null)
                    {
                        string result = item.GetString("result", "gazette.output.image");
                        if (IsPng(result)) imageBase64 = result;
                    }
                }
                text = sb.ToString();
                return text.Length > 0;
            }
            catch (JsonSaveException) { return false; }
        }

        // The fact boundary: three labelled lines, the lead named when it has a
        // name, and no resident named who is not in the facts.
        public static bool TryParsePage(string text, IList<GazetteFact> top, ICollection<string> residentNames,
            out GazettePage page)
        {
            page = null;
            if (string.IsNullOrEmpty(text)) return false;
            GazettePage parsed = new GazettePage();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimStart('*', '#', ' ');
                string value;
                if (TryLabel(line, "HEADLINE:", out value)) parsed.Headline = value.Trim('*', ' ', '"');
                else if (TryLabel(line, "STORY:", out value)) parsed.Story = value.Trim('*', ' ');
                else if (TryLabel(line, "GOSSIP:", out value))
                    foreach (string g in value.Split('|'))
                        if (g.Trim('*', ' ').Length > 0 && parsed.Gossip.Count < 3) parsed.Gossip.Add(Clip(g.Trim('*', ' '), MaxGossip));
            }
            if (parsed.Headline.Length == 0 || parsed.Story.Length == 0 || parsed.Gossip.Count == 0) return false;
            if (parsed.Headline.Length > MaxHeadline || parsed.Story.Length > MaxStory) return false;
            string all = parsed.Headline + "\n" + parsed.Story + "\n" + string.Join("\n", parsed.Gossip.ToArray());
            if (all.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string facts = string.Empty;
            for (int i = 0; top != null && i < top.Count; i++) facts += top[i].Text + "\n";
            if (residentNames != null)
                foreach (string name in residentNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    bool inFacts = ContainsWord(facts, name);
                    if (!inFacts && ContainsWord(all, name)) return false;
                }
            GazetteFact lead = top != null && top.Count > 0 ? top[0] : null;
            if (lead != null && lead.Name.Length > 0 && !ContainsWord(parsed.Headline + " " + parsed.Story, lead.Name))
                return false;
            // Facts only, in the model's own words: every resident named, event word
            // and number must come from the supplied facts, and a clause pairing a
            // resident with events must match one fact sentence (no role swaps).
            List<string> roster = new List<string>();
            if (residentNames != null) roster.AddRange(residentNames);
            for (int i = 0; top != null && i < top.Count; i++) if (top[i].Name.Length > 0) roster.Add(top[i].Name);
            if (!Faithful(parsed.Headline, top, roster, false) || !Faithful(parsed.Story, top, roster, true)) return false;
            foreach (string g in parsed.Gossip) if (!Faithful(g, top, roster, true)) return false;
            parsed.ModelWritten = true;
            page = parsed;
            return true;
        }

        // Credit line naming only the Meta pieces that actually ran.
        public static string Credit(bool written, bool samCut, bool illustrated)
        {
            List<string> parts = new List<string>();
            if (written) parts.Add("Written with Meta AI");
            if (samCut) parts.Add("Photo cut by SAM");
            if (illustrated) parts.Add("Illustrated by Muse Image");
            return parts.Count == 0 ? "Written and photographed by the Gazette desk" : string.Join(" · ", parts.ToArray());
        }

        // SAM cut-out: the outline nearest the lead's screen point, cropped from a
        // top-down RGBA capture. shortcut: CONTRACT-BP-07 outlines are the SAM
        // box (LiveWire keeps no mask raster), so the cut is the box; decode the
        // mask when a silhouette is wanted.
        public static bool TryCut(SamResult result, int width, int height, float leadX, float leadY,
            out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (result == null || result.Outlines.Count == 0) return false;
            double best = double.MaxValue;
            for (int i = 0; i < result.Outlines.Count; i++)
            {
                IReadOnlyList<ImagePoint> pts = result.Outlines[i].Polygon;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int p = 0; p < pts.Count; p++)
                {
                    minX = Math.Min(minX, pts[p].X); maxX = Math.Max(maxX, pts[p].X);
                    minY = Math.Min(minY, pts[p].Y); maxY = Math.Max(maxY, pts[p].Y);
                }
                bool inside = leadX >= minX && leadX <= maxX && leadY >= minY && leadY <= maxY;
                double dx = (minX + maxX) * 0.5 - leadX, dy = (minY + maxY) * 0.5 - leadY;
                double score = (inside ? 0.0 : 1e9) + dx * dx + dy * dy;
                if (score >= best) continue;
                int cx = Math.Max(0, (int)Math.Floor(minX)), cy = Math.Max(0, (int)Math.Floor(minY));
                int cw = Math.Min(width, (int)Math.Ceiling(maxX)) - cx, ch = Math.Min(height, (int)Math.Ceiling(maxY)) - cy;
                if (cw < 8 || ch < 8) continue;
                best = score; x = cx; y = cy; w = cw; h = ch;
            }
            return w > 0;
        }

        static bool TryLabel(string line, string label, out string value)
        {
            value = null;
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return false;
            value = line.Substring(label.Length).Trim();
            return true;
        }

        // Event words by meaning. A group may appear only when the supplied facts
        // use one of its words (or its kind); unknown words are free paraphrase.
        static readonly string[][] Events =
        {
            new[] { "party", "parties", "bash", "celebration", "celebrate", "celebrated", "shindig", "gathering", "host", "hosted", "threw", "invite", "invited", "guests" },
            new[] { "hit", "success", "successful", "smash", "triumph" },
            new[] { "cancelled", "canceled", "cancel" },
            new[] { "baby", "babies", "born", "birth", "pregnant", "newborn", "twins" },
            new[] { "job", "hired", "hire", "promotion", "promoted", "fired", "career", "boss", "lands", "landed", "employed" },
            new[] { "friend", "friends", "friendship", "closer", "bond", "bonded", "pals", "buddies" },
            new[] { "critical", "crisis", "exhausted", "starving", "collapsed", "emergency", "rough" },
            new[] { "argument", "argue", "argued", "fight", "fought", "quarrel", "spat", "feud", "shouting", "voices" },
            new[] { "married", "marry", "wedding", "engaged", "proposed", "proposal", "bride", "groom" },
            new[] { "died", "death", "dead", "funeral", "killed" },
            new[] { "lottery", "money", "prize", "rich", "cash", "bank", "robbed", "rob", "stole", "steal", "theft", "police", "arrested", "crime", "jail" },
            new[] { "fire", "burned", "flood", "hospital", "sick", "injured", "accident", "crashed", "romance", "kiss", "kissed", "dating" },
        };
        static readonly HashSet<string> Numbers = new HashSet<string>(StringComparer.Ordinal)
        { "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "dozen", "hundred", "thousand", "million" };

        static int Group(string word)
        {
            for (int g = 0; g < Events.Length; g++) if (Array.IndexOf(Events[g], word) >= 0) return g;
            return -1;
        }

        static bool Faithful(string line, IList<GazetteFact> top, IList<string> roster, bool checkCaps)
        {
            List<string> factWords = new List<string>();
            HashSet<int> allowed = new HashSet<int>();
            List<string> sentences = new List<string>();
            for (int i = 0; top != null && i < top.Count; i++)
            {
                factWords.AddRange(Words(top[i].Text));
                int kind = Group(top[i].Kind.ToLowerInvariant());
                if (kind >= 0) allowed.Add(kind);
                foreach (string s in top[i].Text.Split('.', '!', '?')) if (s.Trim().Length > 0) sentences.Add(s);
            }
            if (top == null || top.Count == 0) factWords.AddRange(Words("Nothing notable happened."));
            HashSet<string> known = new HashSet<string>(factWords, StringComparer.Ordinal);
            foreach (string w in known) { int g = Group(w); if (g >= 0) allowed.Add(g); }
            foreach (string w in Words(line))
            {
                if (known.Contains(w)) continue;
                if (Numbers.Contains(w) || char.IsDigit(w[0])) return false;
                int g = Group(w);
                if (g >= 0 && !allowed.Contains(g)) return false;
            }
            // A capitalised word inside a sentence is a name: it must be in the facts.
            if (checkCaps)
            {
                string[] tokens = line.Split(' ');
                for (int i = 1; i < tokens.Length; i++)
                {
                    string prev = tokens[i - 1].TrimEnd('"', '\'', ')');
                    if (prev.Length > 0 && ".!?:".IndexOf(prev[prev.Length - 1]) >= 0) continue;
                    string tok = tokens[i].TrimStart('"', '\'', '(');
                    List<string> t = Words(tok);
                    if (t.Count == 0 || !char.IsUpper(tok[0])) continue;
                    if (!known.Contains(t[0]) && !Plain.Contains(t[0]) && t[0] != "i") return false;
                }
            }
            // Role swaps: a clause naming residents and events must match one fact
            // sentence; a clause with events but no name answers for its sentence's names.
            foreach (string sentence in line.Split('.', '!', '?'))
            foreach (string clause in sentence.Split(new[] { ",", ";", " and ", " but ", " while ", " - ", " — " }, StringSplitOptions.None))
            {
                List<string> cw = Words(clause);
                List<int> groups = new List<int>();
                foreach (string w in cw) { int g = Group(w); if (g >= 0 && !groups.Contains(g)) groups.Add(g); }
                if (groups.Count == 0) continue;
                List<List<string>> names = Named(cw, roster);
                if (names.Count == 0) names = Named(Words(sentence), roster);
                if (names.Count == 0) continue;
                bool match = false;
                foreach (string s in sentences)
                {
                    List<string> sw = Words(s);
                    bool all = names.TrueForAll(n => Find(sw, n));
                    foreach (int g in groups) all = all && sw.Exists(w => Group(w) == g);
                    if (all) { match = true; break; }
                }
                if (!match) return false;
            }
            return true;
        }

        // Words a page may add that are capitalised mid-sentence without naming anyone.
        static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.Ordinal)
        { "gazette", "block", "the", "a", "our", "hooray", "wow", "yesterday", "today", "meta" };

        static List<List<string>> Named(List<string> words, IList<string> roster)
        {
            List<List<string>> names = new List<List<string>>();
            foreach (string name in roster) { List<string> n = Words(name); if (n.Count > 0 && Find(words, n)) names.Add(n); }
            return names;
        }

        static bool Find(List<string> words, List<string> name)
        {
            for (int i = 0; i + name.Count <= words.Count; i++)
            {
                int k = 0;
                while (k < name.Count && words[i + k] == name[k]) k++;
                if (k == name.Count) return true;
            }
            return false;
        }

        static List<string> Words(string text)
        {
            List<string> words = new List<string>();
            StringBuilder sb = new StringBuilder();
            foreach (char c in (text ?? string.Empty) + " ")
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
                if (sb.Length > 0) { words.Add(sb.ToString()); sb.Length = 0; }
            }
            return words;
        }

        static bool ContainsWord(string text, string word) { return IndexOfWord(text, word) >= 0; }

        static int IndexOfWord(string text, string word)
        {
            int at = 0;
            while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool left = at == 0 || !char.IsLetter(text[at - 1]);
                int end = at + word.Length;
                bool right = end >= text.Length || !char.IsLetter(text[end]);
                if (left && right) return at;
                at = end;
            }
            return -1;
        }

        static string Clip(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }

        static bool IsPng(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return false;
            try
            {
                byte[] b = Convert.FromBase64String(base64);
                return b.Length > 8 && b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71;
            }
            catch (FormatException) { return false; }
        }
    }
}
