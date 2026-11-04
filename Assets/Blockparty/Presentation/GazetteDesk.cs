using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Presentation;
using Blockparty.Services;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;
using UnityEngine.Networking;

// GAZETTE: the Block Gazette, a daily newspaper of the block, kept out of
// AlphaDirector.cs so the HUD/director lanes only meet one-line hooks
// (LiveTick notices, SocialCompleted, Update keys/tick, DrawHud).
// The engine collects yesterday's facts from the live day log (party, crisis
// and argument notices, Friendship gains, Birth events, hire notices) and picks
// the top stories. Each in-game morning at 7:00, on Y or the HUD Gazette button, the page opens with
// the deterministic template and the raw front-page capture, then upgrades as
// the Meta pieces return: SAM ("person") finds the lead resident in the
// capture and the box is cut out; one Muse Image Responses call rewrites the
// facts (and illustrates the cut-out when SAM ran). Fallbacks: no Muse image
// keeps the SAM cut-out; no SAM keeps the raw capture in a newsprint frame;
// no key or offline keeps the template. The page never pauses the clock (the
// boundary holds replies while paused); any key or click closes it.
// Opt-in evidence session (throwaway HOME, no MODEL_API_KEY):
//   --gazette-demo=<out dir>   run Ultra until the morning edition opens, capture
//                              the finished page, close it, write session.json, quit
//   --gazette-fake=<full|noimage|nosam|nokey> --gazette-fixture=<dir>
//       recorded provider bodies (sam.json, muse-full.json, muse-text.json)
//       through the same LiveWire.ParseSam / GazetteWriter parsers; never a
//       network call. muse-*.json may carry {LEAD} for the lead resident's name.
public sealed partial class AlphaDirector
{
    const long GazetteHour = 7L * 3600L;
    // Y: unused by InputActions defaults, ControlsRebind.FixedKeys, the voice talk key (V) and SAM Look (G).
    const KeyCode GazetteKey = KeyCode.Y;
    const int GazetteWidth = 640, GazetteHeight = 360;

    readonly List<GazetteFact> gazetteFacts = new List<GazetteFact>();
    Sim gazetteSim;
    long gazetteDay = -1L;
    long gazetteEventCursor;
    bool gazetteOpen, gazetteBusy, gazetteDemoStarted;
    int gazetteEdition;
    GazettePage gazettePage;
    List<GazetteFact> gazetteTop;
    string gazetteCredit = "";
    Texture2D gazettePicture;
    string gazettePictureSource = "";
    ProposalBoundary gazetteBoundary;
    GazetteWriter gazetteWriter;
    CaptureSearch gazetteSearch;
    readonly GazetteView gazetteView = new GazetteView();
    // BP-SECU-02: shown on the page before any send; the run waits until the player has seen it.
    const string GazetteDisclosure = "Sending this snapshot to Meta SAM and the stories to Meta Muse Image (api.meta.ai)…";
    bool gazetteRestore;
    int gazetteDisclosedEdition = -1;   // the edition whose notice the player has seen
    readonly ServiceEndpoint gazetteMuse = new ServiceEndpoint(LiveTransport.ImageService,
        "https://api.meta.ai/v1/responses", "muse-image-1.0", true);
    readonly ServiceEndpoint gazetteSam = new ServiceEndpoint(LiveTransport.SamService,
        "https://api.meta.ai/v1/responses", "sam-3.1", true);

    // ---------- hooks ----------

    // LiveTick: one live notice (party answers and outcomes, crises, arguments, hires).
    void GazetteNotice(string text)
    {
        if (string.IsNullOrEmpty(text) || sim == null) return;
        string kind = null;
        int weight = 0;
        if (text.Contains("'s party was a hit")) { kind = "Party"; weight = 60; }
        else if (text.Contains("'s party ended early")) { kind = "Party"; weight = 45; }
        else if (text.Contains("party called off")) { kind = "Party"; weight = 40; }
        else if (text.Contains(" is critical!")) { kind = "Crisis"; weight = 35; }
        else if (text.Contains("started an argument")) { kind = "Argument"; weight = 30; }
        else if (text.IndexOf(" hired", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("new job", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "Hire"; weight = 50; }
        if (kind == null) return;
        ResidentRecord who = GazetteNamed(text);
        // "called off" notices name only the guests; the picture must not frame a guest as the host.
        bool frames = who != null && !text.Contains("party called off");
        gazetteFacts.Add(new GazetteFact(kind, frames ? (long?)(long)who.Id : null, frames ? who.DisplayName : "",
            text.Trim(), weight));
    }

    // SocialCompleted: a Friendship gain between two residents.
    void GazetteSocial(ResidentRecord a, ResidentRecord b, float before, float after)
    {
        if (after <= before || a == null || b == null) return;
        string key = a.DisplayName + " and " + b.DisplayName;
        for (int i = 0; i < gazetteFacts.Count; i++)
            if (gazetteFacts[i].Kind == "Friendship" && gazetteFacts[i].Text.StartsWith(key, StringComparison.Ordinal)) return;
        gazetteFacts.Add(new GazetteFact("Friendship", (long)a.Id, a.DisplayName,
            key + " grew closer (Friendship " + Mathf.RoundToInt(before) + " to " + Mathf.RoundToInt(after) + ").", after >= 50f && before < 50f ? 32 : 20));
    }

    // Update, while live and not under the title: true while the page owns the input.
    bool GazetteKeys()
    {
        if (sim == null || failed) return false;
        if (gazetteOpen)
        {
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) GazetteClose();
            return true;
        }
        // A player binding (an action or the talk key) remapped to Y keeps Y; the HUD Gazette button still opens the page.
        bool bound = access != null && !string.IsNullOrEmpty(access.ResolveInput(GazetteKey.ToString()))
            || voice != null && voice.Settings.TalkKey == GazetteKey.ToString();
        if (!bound && !settingsOpen && !input.MenuOpen && Input.GetKeyDown(GazetteKey)) { GazetteOpen("key"); return true; }
        return false;
    }

    // Update, after LiveTick: the morning edition and the event cursor.
    void GazetteTick()
    {
        if (!GazetteBind()) return;
        GazetteEvents();
        long day = (sim.World.GameSeconds - GazetteHour) / Day;
        if (day > gazetteDay)
        {
            gazetteDay = day;
            GazettePublish();
            if (!gazetteOpen && !titleOpen) GazetteOpen("morning");
        }
    }

    // Per world (start, reload): fresh facts, cursor, boundary and adapters.
    bool GazetteBind()
    {
        if (sim == null || failed) return false;
        if (gazetteSim != sim)
        {
            gazetteSim = sim;
            gazetteFacts.Clear();
            gazetteDay = (sim.World.GameSeconds - GazetteHour) / Day;
            gazetteEventCursor = sim.World.Events.Count;
            // A new world drops the old timeline's edition; ++ also strands any run still in flight.
            gazettePage = null;
            gazetteTop = null;
            gazetteBusy = false;
            GazetteReplacePicture(null, "");
            gazetteEdition++;
            if (gazetteRestore) GazetteRestore();
            gazetteBoundary = new ProposalBoundary(sim.World, sim.Clock, sim.Commands, new Autonomy(sim.World, sim.Commands));
            gazetteWriter = new GazetteWriter(gazetteBoundary);
            gazetteSearch = new CaptureSearch(gazetteBoundary);
            string demo = Arg(Environment.GetCommandLineArgs(), "--gazette-demo");
            if (demo.Length > 0 && !gazetteDemoStarted) { gazetteDemoStarted = true; StartCoroutine(GazetteDemo(demo)); }
        }
        return true;
    }

    void GazetteOnGUI()
    {
        GazetteBind();
        if (sim == null || titleOpen || failed) return;
        if (!gazetteOpen)
        {
            if (gazetteWriter != null && gazetteView.Button(alphaHud.LookSlotLeft)) GazetteOpen("button");
            return;
        }
        if (gazettePage == null) return;
        if (gazetteCredit == GazetteDisclosure && Event.current.type == EventType.Repaint) gazetteDisclosedEdition = gazetteEdition;
        gazetteView.Draw(gazettePage, gazetteTop, gazettePicture, gazettePictureSource, gazetteCredit,
            "Day " + (sim.World.GameSeconds / Day + 1L) + " · Morning Edition No. " + gazetteEdition, gazetteBusy,
            access != null ? access.Settings.TextScale : 1f);
    }

    // ---------- facts ----------

    void GazetteEvents()
    {
        IReadOnlyList<WorldEvent> events = sim.World.Events;
        for (long i = gazetteEventCursor; i < events.Count; i++)
        {
            WorldEvent e = events[(int)i];
            ResidentRecord who;
            if (!e.Actor.HasValue || !sim.World.TryGetResident(e.Actor.Value, out who)) continue;
            GazetteFact f = e.Kind == "Birth"
                ? new GazetteFact("Birth", (long)who.Id, who.DisplayName, who.DisplayName + " welcomed a new baby" + GazetteChild(e.Detail) + ".", 70)
                : e.Kind.IndexOf("Hire", StringComparison.OrdinalIgnoreCase) >= 0
                ? new GazetteFact("Hire", (long)who.Id, who.DisplayName, who.DisplayName + " started a new job.", 50) : null;
            if (f != null && !gazetteFacts.Exists(g => g.Text == f.Text)) gazetteFacts.Add(f);
        }
        gazetteEventCursor = events.Count;
    }

    string GazetteChild(string detail)
    {
        long id;
        ResidentRecord child;
        string digits = (detail ?? "").Replace("child", "").Trim();
        return long.TryParse(digits, out id) && sim.World.TryGetResident((ResidentId)id, out child)
            ? ", " + child.DisplayName : "";
    }

    ResidentRecord GazetteNamed(string text)
    {
        ResidentRecord best = null;
        int at = int.MaxValue;
        foreach (ResidentRecord r in sim.World.ResidentsSnapshot())
        {
            int i = text.IndexOf(r.DisplayName, StringComparison.Ordinal);
            if (i >= 0 && i < at) { at = i; best = r; }
        }
        return best;
    }

    // Crises still open at press time make the paper even without a notice today.
    void GazetteCrises()
    {
        foreach (ResidentRecord r in sim.World.ResidentsSnapshot())
        {
            if (!r.IsAlive) continue;
            for (int n = 0; n < StateSchema.NeedCount; n++)
            {
                if (r.GetNeed((Need)n) > 10f) continue;   // LiveSimulation.CriticalNeed
                string text = r.DisplayName + "'s " + (Need)n + " is critical!";
                if (!gazetteFacts.Exists(f => f.Text == text))
                    gazetteFacts.Add(new GazetteFact("Crisis", (long)r.Id, r.DisplayName, text, 35));
                break;
            }
        }
    }

    // ---------- save / reload ----------
    // The journal and current edition beside the save (gazette.txt), so F9 /
    // Continue restore them with the world; tabs and newlines never occur in fact text.

    void GazetteSave(string dir, bool ok)
    {
        if (!ok || sim == null) return;
        StringBuilder sb = new StringBuilder();
        sb.Append("edition\t").Append(gazetteEdition).Append('\t').Append(gazetteDay).Append('\t')
            .Append(sim.World.GameSeconds).Append('\t').Append(gazettePage == null ? 0 : 1).Append('\n');
        if (gazettePage != null && gazetteTop != null) foreach (GazetteFact f in gazetteTop) GazetteLine(sb, "E", f);
        foreach (GazetteFact f in gazetteFacts) GazetteLine(sb, "J", f);
        try { File.WriteAllText(Path.Combine(dir, "gazette.txt"), sb.ToString()); }
        catch (Exception e) { Log("gazette save failed " + e.GetType().Name); }
    }

    static void GazetteLine(StringBuilder sb, string list, GazetteFact f)
    {
        sb.Append(list).Append('\t').Append(f.Kind).Append('\t').Append(f.Resident.HasValue ? f.Resident.Value.ToString() : "-")
            .Append('\t').Append(f.Name).Append('\t').Append(f.Weight).Append('\t')
            .Append(f.Text.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
    }

    // On the replacement Sim after F9 / Continue: the saved journal and edition,
    // then the durable events logged since the save (GazetteEvents dedupes).
    void GazetteRestore()
    {
        gazetteRestore = false;
        string path = Path.Combine(saveDirectory, "gazette.txt");
        long savedAt = -1L;
        List<GazetteFact> edition = new List<GazetteFact>();
        bool hasEdition = false;
        if (File.Exists(path))
            foreach (string line in File.ReadAllLines(path))
            {
                string[] c = line.Split('\t');
                long n;
                if (c[0] == "edition" && c.Length >= 5)
                {
                    int saved;
                    if (int.TryParse(c[1], out saved) && saved > gazetteEdition) gazetteEdition = saved;
                    if (long.TryParse(c[3], out n)) savedAt = n;
                    long day;
                    hasEdition = c[4] == "1" && long.TryParse(c[2], out day) && day == gazetteDay;
                }
                else if (c.Length >= 6 && (c[0] == "E" || c[0] == "J"))
                {
                    int w;
                    int.TryParse(c[4], out w);
                    GazetteFact f = new GazetteFact(c[1], long.TryParse(c[2], out n) ? (long?)n : null, c[3], c[5], w);
                    (c[0] == "E" ? edition : gazetteFacts).Add(f);
                }
            }
        // shortcut: a gazette.txt not matching this save's time is ignored, as when an older build saved.
        if (savedAt != sim.World.GameSeconds) { gazetteFacts.Clear(); edition.Clear(); hasEdition = false; savedAt = -1L; }
        if (hasEdition)
        {
            gazetteTop = edition;
            gazettePage = GazetteWriter.Template(gazetteTop);
            gazetteCredit = GazetteWriter.Credit(false, false, false);
        }
        // Durable events of the current day: resume from the window start, not past every restored event.
        long windowStart = gazetteDay * Day + GazetteHour;
        IReadOnlyList<WorldEvent> events = sim.World.Events;
        long cursor = events.Count;
        while (cursor > 0 && events[(int)(cursor - 1)].GameSeconds >= windowStart) cursor--;
        gazetteEventCursor = cursor;
        Log("gazette restore journal=" + gazetteFacts.Count + " edition=" + B(hasEdition) + " cursor=" + cursor);
    }

    // ---------- the edition ----------

    // Y, the HUD button and the morning show the current edition; only the
    // morning (GazettePublish) consumes the journal and starts a new one.
    void GazetteOpen(string trigger)
    {
        if (gazetteOpen || gazetteWriter == null) return;
        if (gazettePage == null)
        {
            // Before the first morning there is no edition yet; the journal stays for it.
            gazetteTop = new List<GazetteFact>();
            gazettePage = GazetteWriter.Template(gazetteTop);
            gazetteCredit = GazetteWriter.Credit(false, false, false);
        }
        gazetteOpen = true;
        Log("gazette open trigger=" + trigger + " edition=" + gazetteEdition + " stories=" + gazetteTop.Count
            + " paused=" + B(sim.Clock.Paused));
    }

    void GazettePublish()
    {
        if (gazetteWriter == null) return;
        GazetteEvents();
        GazetteCrises();
        gazetteTop = GazetteWriter.Pick(gazetteFacts);
        gazetteFacts.Clear();
        gazettePage = GazetteWriter.Template(gazetteTop);
        gazetteCredit = GazetteDisclosure;
        gazetteEdition++;
        // The clock keeps running: ProposalBoundary holds every reply while paused,
        // so pausing here would also hold the SAM and Muse Image replies.
        GazetteFact lead = GazetteWriter.Lead(gazetteTop);
        Vector2 point;
        byte[] rgba = GazetteCapture(lead, out point);
        GazetteSetPicture(rgba, GazetteWidth, GazetteHeight, "capture");
        Log("gazette publish edition=" + gazetteEdition + " stories=" + gazetteTop.Count
            + " lead=" + (lead == null ? "none" : lead.Name));
        gazetteBusy = true;
        StartCoroutine(GazetteRun(lead, rgba, point, gazetteEdition));
    }

    void GazetteClose()
    {
        if (!gazetteOpen) return;
        gazetteOpen = false;
        Log("gazette close edition=" + gazetteEdition + " paused=" + B(sim.Clock.Paused));
    }

    // Frames the camera on the lead (or keeps the player's view), renders one
    // HUD-free frame, and returns top-down RGBA plus the lead's image point.
    byte[] GazetteCapture(GazetteFact lead, out Vector2 point)
    {
        point = new Vector2(GazetteWidth * 0.5f, GazetteHeight * 0.5f);
        if (alphaCamera == null) return new byte[GazetteWidth * GazetteHeight * 4];
        Vector3 position = alphaCamera.transform.position;
        Quaternion rotation = alphaCamera.transform.rotation;
        float fov = alphaCamera.fieldOfView;
        RenderTexture target = new RenderTexture(GazetteWidth, GazetteHeight, 24);
        Texture2D texture = new Texture2D(GazetteWidth, GazetteHeight, TextureFormat.RGBA32, false);
        RenderTexture previousActive = RenderTexture.active, previousTarget = alphaCamera.targetTexture;
        try
        {
            alphaCamera.targetTexture = target;
            if (lead != null)
            {
                ApproachPose pose = sim.Executor.GetResidentPosition((ResidentId)lead.Resident.Value);
                if (!float.IsNaN(pose.X) && !float.IsNaN(pose.Z))
                {
                    Vector3 look = new Vector3(pose.X, 1.0f, pose.Z);
                    alphaCamera.transform.position = look + new Vector3(0f, 1.4f, -3.6f);
                    alphaCamera.transform.LookAt(look);
                    alphaCamera.fieldOfView = 40f;
                    Vector3 screen = alphaCamera.WorldToScreenPoint(look);
                    if (screen.z > 0f) point = new Vector2(screen.x, GazetteHeight - screen.y);
                }
            }
            alphaCamera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0f, 0f, GazetteWidth, GazetteHeight), 0, 0);
            texture.Apply();
            Color32[] pixels = texture.GetPixels32();
            byte[] rgba = new byte[GazetteWidth * GazetteHeight * 4];
            for (int y = 0; y < GazetteHeight; y++)
                for (int x = 0; x < GazetteWidth; x++)
                {
                    Color32 c = pixels[(GazetteHeight - 1 - y) * GazetteWidth + x];
                    int o = (y * GazetteWidth + x) * 4;
                    rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = 255;
                }
            return rgba;
        }
        finally
        {
            alphaCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            alphaCamera.transform.position = position;
            alphaCamera.transform.rotation = rotation;
            alphaCamera.fieldOfView = fov;
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(texture);
        }
    }

    IEnumerator GazetteRun(GazetteFact lead, byte[] rgba, Vector2 point, int edition)
    {
        IGazetteTransport transport = GazetteTransportFor();
        while (gazetteDisclosedEdition != edition && edition == gazetteEdition) yield return null;
        if (edition != gazetteEdition) yield break;
        bool samCut = false, written = false, illustrated = false;
        byte[] cut = null;
        int cw = 0, ch = 0;
        // 1. SAM on the real capture. disclosureGiven: GazetteDisclosure was drawn
        // on the page above; hasCredential: the key resolves only inside the send.
        CaptureId capture = (CaptureId)(long)edition;
        gazetteSearch.SetCapture(capture, rgba, GazetteWidth, GazetteHeight);
        GatewayRequest samRequest = lead == null ? null : gazetteSearch.Request(GazetteWriter.SamPhrase, gazetteSam, true, true);
        if (samRequest != null)
        {
            string png = Convert.ToBase64String(GazetteEncode(rgba, GazetteWidth, GazetteHeight));
            int status = 0;
            string body = null;
            float samStart = Time.realtimeSinceStartup;
            yield return transport.Post("sam", LiveWire.BuildSamRequest(samRequest.ModelId, GazetteWriter.SamPhrase,
                "data:image/png;base64," + png), lead, (s, b) => { status = s; body = b; });
            string frame = null;
            GatewayOutcome outcome = status == 0 ? GatewayOutcome.Unprovisioned : LiveWire.ParseSam(status, body, capture, out frame);
            if (status != 0 && outcome == GatewayOutcome.Applied) gazetteSearch.Complete(samRequest.Id, frame);
            else gazetteSearch.Fail(samRequest.Id, outcome);
            int x, y;
            if (GazetteWriter.TryCut(gazetteSearch.Result, GazetteWidth, GazetteHeight, point.x, point.y, out x, out y, out cw, out ch))
            {
                cut = GazetteCrop(rgba, x, y, cw, ch);
                samCut = true;
                if (edition == gazetteEdition) GazetteSetPicture(cut, cw, ch, "sam");
            }
            Log("gazette sam status=" + status + " ms=" + (int)((Time.realtimeSinceStartup - samStart) * 1000f) + " outlines="
                + gazetteSearch.CountText + " cut=" + B(samCut) + " transport=" + transport.Mode + GazetteWhy(status, body, false));
        }
        // 2. The words from the live Meta text model, then, when SAM gave a
        // cut-out, the Muse Image illustration of the lead story.
        GatewayRequest museRequest = gazetteWriter.Request(gazetteTop, gazetteMuse, true, true);
        GazettePage page = null;
        string image = null;
        if (museRequest != null)
        {
            int status = 0;
            string body = null;
            float museStart = Time.realtimeSinceStartup;
            yield return transport.Post("muse-text",
                GazetteWriter.BuildRequest(GazetteWriter.TextModel, GazetteWriter.Prompt(gazetteTop, false), null),
                lead, (s, b) => { status = s; body = b; });
            string ignored;
            if (status == 0) gazetteWriter.Fail(museRequest.Id, GatewayOutcome.Unprovisioned);
            else gazetteWriter.Complete(museRequest.Id, status, body, GazetteNames(), out page, out ignored);
            Log("gazette muse status=" + status + " ms=" + (int)((Time.realtimeSinceStartup - museStart) * 1000f) + " written=" + B(page != null)
                + " model=" + GazetteWriter.TextModel + " transport=" + transport.Mode + GazetteWhy(status, body, page == null));
            if (samCut && status != 0 && edition == gazetteEdition)
            {
                museStart = Time.realtimeSinceStartup;
                yield return transport.Post("muse-full", GazetteWriter.BuildImageRequest(museRequest.ModelId, gazetteTop),
                    lead, (s, b) => { status = s; body = b; });
                image = GazetteWriter.TryReadImage(status, body);
                Log("gazette image status=" + status + " ms=" + (int)((Time.realtimeSinceStartup - museStart) * 1000f)
                    + " image=" + B(image != null) + " transport=" + transport.Mode + GazetteWhy(status, body, false));
            }
        }
        if (edition != gazetteEdition) yield break;
        if (page != null) { gazettePage = page; written = true; }
        if (samCut && image != null)
        {
            Texture2D decoded = new Texture2D(2, 2);
            if (decoded.LoadImage(Convert.FromBase64String(image)))
            {
                GazetteReplacePicture(decoded, "muse");
                illustrated = true;
            }
            else UnityEngine.Object.Destroy(decoded);
        }
        gazetteCredit = GazetteWriter.Credit(written, samCut, illustrated);
        gazetteBusy = false;
        Log("gazette ready edition=" + edition + " picture=" + gazettePictureSource + " credit=" + gazetteCredit
            + " headline=" + gazettePage.Headline);
    }

    // Why a step fell back: the provider's error, or the refused model text (game
    // facts only; the request key is never in a reply body).
    static string GazetteWhy(int status, string body, bool refused)
    {
        string text = null, image;
        if (status >= 200 && status <= 299 && refused) text = GazetteWriter.TryReadReply(body, out text, out image) ? text : "unreadable";
        else if (status != 0 && (status < 200 || status > 299)) text = body ?? "";
        if (text == null) return "";
        text = text.Replace('\n', '|').Replace('\r', ' ');
        return " why=\"" + (text.Length > 600 ? text.Substring(0, 600) : text) + "\"";
    }

    List<string> GazetteNames()
    {
        List<string> names = new List<string>();
        foreach (ResidentRecord r in sim.World.ResidentsSnapshot()) names.Add(r.DisplayName);
        return names;
    }

    static byte[] GazetteCrop(byte[] rgba, int x, int y, int w, int h)
    {
        byte[] cut = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(rgba, ((y + row) * GazetteWidth + x) * 4, cut, row * w * 4, w * 4);
        return cut;
    }

    // Top-down RGBA to PNG (Unity textures are bottom-up).
    static byte[] GazetteEncode(byte[] rgba, int w, int h)
    {
        Texture2D t = GazetteTexture(rgba, w, h);
        try { return t.EncodeToPNG(); }
        finally { UnityEngine.Object.Destroy(t); }
    }

    static Texture2D GazetteTexture(byte[] rgba, int w, int h)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        byte[] flipped = new byte[rgba.Length];
        for (int row = 0; row < h; row++) Buffer.BlockCopy(rgba, row * w * 4, flipped, (h - 1 - row) * w * 4, w * 4);
        t.LoadRawTextureData(flipped);
        t.Apply();
        return t;
    }

    void GazetteSetPicture(byte[] rgba, int w, int h, string source)
    {
        GazetteReplacePicture(GazetteTexture(rgba, w, h), source);
    }

    void GazetteReplacePicture(Texture2D texture, string source)
    {
        if (gazettePicture != null) UnityEngine.Object.Destroy(gazettePicture);
        gazettePicture = texture;
        gazettePictureSource = source;
    }

    // ---------- transport ----------

    interface IGazetteTransport
    {
        string Mode { get; }
        IEnumerator Post(string step, string body, GazetteFact lead, Action<int, string> done);
    }

    IGazetteTransport gazetteTransport;

    IGazetteTransport GazetteTransportFor()
    {
        if (gazetteTransport != null) return gazetteTransport;
        string[] args = Environment.GetCommandLineArgs();
        string fake = Arg(args, "--gazette-fake");
        gazetteTransport = fake.Length > 0 ? (IGazetteTransport)new FakeGazetteTransport(fake, Arg(args, "--gazette-fixture"))
            : new LiveGazetteTransport();
        return gazetteTransport;
    }

    // Live: status 0 means nothing was sent (no key). The key is resolved per send,
    // goes only into the Authorization header and is never stored or logged.
    sealed class LiveGazetteTransport : IGazetteTransport
    {
        public string Mode { get { return "live"; } }
        public IEnumerator Post(string step, string body, GazetteFact lead, Action<int, string> done)
        {
            string key = null;
            try
            {
                KeyResolution resolution = KeyProvider.Resolve(KeySettings.DefaultPath());
                if (!resolution.TryGetKey(out key) || string.IsNullOrWhiteSpace(key)) key = null;
            }
            catch (Exception) { key = null; }
            if (key == null) { done(0, null); yield break; }
            using (UnityWebRequest request = new UnityWebRequest(step == "muse-full" ? GazetteWriter.ImageUrl : "https://api.meta.ai/v1/responses", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body ?? ""));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + key);
                key = null;
                request.timeout = step == "sam" ? GatewayLimits.ServiceTimeoutRealSeconds : LiveWire.ImageGenerationTimeoutRealSeconds;
                yield return request.SendWebRequest();
                int status = (int)request.responseCode;
                done(status <= 0 ? 504 : status, request.downloadHandler != null ? request.downloadHandler.text : null);
            }
        }
    }

    // Recorded provider bodies; never a network call.
    sealed class FakeGazetteTransport : IGazetteTransport
    {
        readonly string mode, fixtures;
        public FakeGazetteTransport(string mode, string fixtures) { this.mode = mode; this.fixtures = fixtures ?? ""; }
        public string Mode { get { return "fake-" + mode; } }
        public IEnumerator Post(string step, string body, GazetteFact lead, Action<int, string> done)
        {
            yield return new WaitForSecondsRealtime(0.4f);
            if (mode == "nokey") { done(0, null); yield break; }
            bool refuse = step == "sam" && mode == "nosam" || step == "muse-full" && mode == "noimage";
            string file = Path.Combine(fixtures, step + ".json");
            if (refuse || !File.Exists(file))
            {
                done(500, "{\"error\":{\"message\":\"fixture refusal\",\"type\":\"server_error\"}}");
                yield break;
            }
            done(200, File.ReadAllText(file).Replace("{LEAD}", lead != null ? lead.Name : "The Block"));
        }
    }

    // ---------- evidence session ----------

    IEnumerator GazetteDemo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        while (!splash.Done) yield return null;
        if (titleOpen) TitleChoose(TitleAction.NewGame);
        yield return new WaitForSecondsRealtime(2f);
        SetSpeed(SimulationSpeed.Ultra);
        bool wasPausedBefore = sim.Clock.Paused;
        float deadline = Time.realtimeSinceStartup + 600f;   // live residents slow Ultra; the 7:00 edition needs ~8 min
        while (!gazetteOpen && Time.realtimeSinceStartup < deadline) yield return null;
        string trigger = gazetteOpen ? "morning" : "key-fallback";
        if (!gazetteOpen) { GazettePublish(); GazetteOpen("demo-key"); }
        bool pausedWhileOpen = sim.Clock.Paused;
        deadline = Time.realtimeSinceStartup + 90f;
        while (gazetteBusy && Time.realtimeSinceStartup < deadline) yield return null;
        yield return new WaitForSecondsRealtime(0.5f);
        yield return new WaitForEndOfFrame();
        string mode = GazetteTransportFor().Mode;
        GazetteScreen(Path.Combine(outDir, "gazette-" + mode + ".png"));
        if (gazettePicture != null) File.WriteAllBytes(Path.Combine(outDir, "picture-" + mode + ".png"), gazettePicture.EncodeToPNG());
        GazettePage page = gazettePage;
        string credit = gazetteCredit, source = gazettePictureSource;
        GazetteClose();
        yield return new WaitForSecondsRealtime(0.5f);
        yield return new WaitForEndOfFrame();
        GazetteScreen(Path.Combine(outDir, "closed-" + mode + ".png"));
        JsonObject root = new JsonObject();
        root.Add("transport", mode);
        root.Add("trigger", trigger);
        root.Add("busy_at_capture", gazetteBusy);
        root.Add("paused_while_open", pausedWhileOpen);
        root.Add("paused_after_close", sim.Clock.Paused);
        root.Add("paused_before", wasPausedBefore);
        root.Add("model_written", page.ModelWritten);
        root.Add("headline", page.Headline);
        root.Add("story", page.Story);
        JsonArray gossip = new JsonArray();
        foreach (string g in page.Gossip) gossip.Add(new JsonString(g));
        root.Add("gossip", gossip);
        JsonArray facts = new JsonArray();
        foreach (GazetteFact f in gazetteTop) facts.Add(new JsonString(f.Kind + ": " + f.Text));
        root.Add("facts", facts);
        root.Add("picture", source);
        root.Add("credit", credit);
        StringBuilder sb = new StringBuilder();
        root.Render(sb);
        File.WriteAllText(Path.Combine(outDir, "session-" + mode + ".json"), sb.ToString());
        Log("gazette demo done transport=" + mode);
        yield return new WaitForSecondsRealtime(0.5f);
        Application.Quit(failed ? 1 : 0);
    }

    static void GazetteScreen(string path)
    {
        Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
        try { File.WriteAllBytes(path, shot.EncodeToPNG()); }
        finally { UnityEngine.Object.Destroy(shot); }
    }
}
