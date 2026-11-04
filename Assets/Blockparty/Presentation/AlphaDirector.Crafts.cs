using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blockparty.Activities;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Presentation;
using Blockparty.Simulation;
using UnityEngine;

// CRAFTS@1 (M5-OD-42) live wiring for the Alpha director, kept out of
// AlphaDirector.cs so the HUD/director lanes only meet one-line hooks.
// The Creative card's Crafts rows queue a craft kind bound to the CommandId of
// its own ACT-CREA-02 command (Append, PlayerHuman) at the craft spot: one
// object with four approach poses around the CREATIVE stage, so up to four
// residents craft at once (the stage object itself has one slot and abandons a
// second crafter as ObjectOccupied). The spot is allocated at live start and
// re-registered on reload. LiveTick hands every completed execution to
// CraftsCompleted after Creative.Complete; only the request's own carrier makes
// a piece (charging the material cost), which is placed in the author's home,
// announced in a toast, and queued for Meta image generation through
// ImageAdapter.RequestPrompt and the LIVE-WIRE transport (key resolved inside
// each send; none means the procedural image stays). One request runs at a
// time; an image over the M5-OD-46 cap or one Unity cannot decode keeps the
// procedural picture with the reason. CraftsUpdate, after each LiveTick, drops
// requests whose carrier was cancelled and moves pieces with their household.
// F5 saves crafts and world as one pair (CraftsSave.Save); F9 restores crafts
// bound to that save.
// Opt-in evidence session (never touches the real settings file when run
// with a throwaway HOME):
//   --crafts-demo=<out dir>         scripted portrait + pottery (and the four
//                                   other kinds), captures, save/reload, session.json
//   --crafts-fake-image=<image|refuse|nokey> --crafts-fake-fixture=<dir>
//       replaces LiveTransport with recorded images-response frames
//       (<kind>.json per craft kind; a kind without a frame gets a 400 refusal),
//       parsed by the same LiveWire.ParseImage path.
public sealed partial class AlphaDirector
{
    interface ICraftImageTransport
    {
        string Mode { get; }
        string FailureReason { get; }
        IEnumerator Generate(ImageAdapter adapter, ProposalBoundary boundary, GatewayRequest request,
            string body, CraftKind kind);
    }

    sealed class LiveCraftTransport : ICraftImageTransport
    {
        readonly LiveTransport live;
        public LiveCraftTransport(MonoBehaviour host) { live = new LiveTransport(host); }
        public string Mode { get { return "live"; } }
        public string FailureReason
        {
            get { return live.NoKeyReported ? "no Meta key is set" : "the image service gave no usable image"; }
        }
        public IEnumerator Generate(ImageAdapter adapter, ProposalBoundary boundary, GatewayRequest request,
            string body, CraftKind kind)
        {
            return live.GenerateImage(adapter, boundary, request, body);
        }
    }

    // Recorded/synthetic images-response frames; never a network call.
    sealed class FakeCraftTransport : ICraftImageTransport
    {
        const string Refusal = "{\"error\":{\"message\":\"Request refused by the provider\",\"type\":\"invalid_request_error\"}}";
        readonly string mode;
        readonly string fixtures;
        string reason = "";
        public FakeCraftTransport(string mode, string fixtures) { this.mode = mode; this.fixtures = fixtures ?? ""; }
        public string Mode { get { return "fake-" + mode; } }
        public string FailureReason { get { return reason; } }
        public IEnumerator Generate(ImageAdapter adapter, ProposalBoundary boundary, GatewayRequest request,
            string body, CraftKind kind)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (mode == "nokey")
            {
                reason = "no Meta key is set";
                adapter.Fail(request.Id, GatewayOutcome.Unprovisioned);
                yield break;
            }
            string file = Path.Combine(fixtures, kind.ToString().ToLowerInvariant() + ".json");
            bool have = mode == "image" && File.Exists(file);
            string frame;
            GatewayOutcome outcome = LiveWire.ParseImage(have ? 200 : 400, have ? File.ReadAllText(file) : Refusal, out frame);
            if (outcome == GatewayOutcome.Applied) adapter.Complete(request.Id, frame);
            else
            {
                reason = "the image service refused the request";
                adapter.Fail(request.Id, outcome);
            }
        }
    }

    Crafts crafts;
    CraftsView craftsView;
    ProposalBoundary craftsBoundary;
    ImageAdapter craftsAdapter;
    ICraftImageTransport craftsTransport;
    readonly Queue<CraftPiece> craftsQueue = new Queue<CraftPiece>();
    Coroutine craftsWorker;
    int craftsHomeView = -1;          // home view: -1 off, 0 east wall, 1 room
    HomeId craftsViewHome;
    ObjectId? craftsPinned;
    CraftPiece craftsHover;
    bool craftsDemoStarted;
    ObjectId craftsSpot;
    readonly ServiceEndpoint craftsEndpoint = new ServiceEndpoint("Muse Image",
        "https://api.meta.ai/v1/images/generations", "muse-image-1.0", true);

    // StartLive / ReloadLive: bind crafts to the current world. A reload restores
    // crafts.json; its refusal notice replaces the reload notice.
    void CraftsBind(bool reload)
    {
        if (craftsWorker != null) { StopCoroutine(craftsWorker); craftsWorker = null; }
        craftsQueue.Clear();
        crafts = new Crafts(sim.World);
        craftsBoundary = new ProposalBoundary(sim.World, sim.Clock, sim.Commands, new Autonomy(sim.World, sim.Commands));
        craftsAdapter = new ImageAdapter(sim.World, craftsBoundary, sim.Creative);
        if (craftsTransport == null)
        {
            string[] args = Environment.GetCommandLineArgs();
            string fake = Arg(args, "--crafts-fake-image");
            craftsTransport = fake.Length > 0
                ? (ICraftImageTransport)new FakeCraftTransport(fake, Arg(args, "--crafts-fake-fixture"))
                : new LiveCraftTransport(this);
        }
        if (craftsView == null) craftsView = new CraftsView(transform);
        if (!reload)
        {
            craftsSpot = sim.World.AllocateObject(Core);
            CraftsRegister(sim.Executor);
        }
        if (reload)
        {
            string refused = CraftsSave.Read(saveDirectory, crafts, sim.World);
            crafts.Track(sim.Commands);
            if (refused != null) notice = refused;
            Log("crafts reload pieces=" + crafts.Pieces.Count + (refused == null ? "" : " refused=" + refused));
        }
        craftsHover = null;
        craftsPinned = null;
        craftsView.Rebuild(crafts, sim.World);
        string demo = Arg(Environment.GetCommandLineArgs(), "--crafts-demo");
        if (!reload && demo.Length > 0 && !craftsDemoStarted)
        {
            craftsDemoStarted = true;
            StartCoroutine(RunCraftsDemo(demo));
        }
    }

    // RegisterObjects (live start and every SaveV1 reload): the four craft poses.
    void CraftsRegister(ActivityExecutor executor)
    {
        if ((long)craftsSpot <= 0L) return;
        ClassStage stage = StageOf("CREATIVE");
        Vector3 a = stage == null ? Vector3.zero : stage.actor.position;
        executor.RegisterObject(craftsSpot, new[]
        {
            new ApproachPose(a.x, 0f, a.z), new ApproachPose(a.x - 0.7f, 0f, a.z - 1.3f),
            new ApproachPose(a.x + 0.7f, 0f, a.z - 1.3f), new ApproachPose(a.x, 0f, a.z - 2.3f),
        });
    }

    string[] CraftRows()
    {
        string[] rows = new string[Crafts.KindNames.Length + 1];
        Array.Copy(Crafts.KindNames, rows, Crafts.KindNames.Length);
        rows[rows.Length - 1] = craftsHomeView < 0 ? "See the pieces at home" : "Back to the block";
        return rows;
    }

    // HUD Crafts row: 0..5 craft a kind, last row toggles the home view.
    void CraftChoice(int index)
    {
        if (index >= 0 && index < Crafts.KindNames.Length) { StartCraft((CraftKind)index); return; }
        ToggleCraftsView();
    }

    void StartCraft(CraftKind kind)
    {
        if (!navigation.SelectedResident.HasValue) { notice = "Select a resident first (N)."; return; }
        ResidentId resident = navigation.SelectedResident.Value;
        ResidentRecord record = ResidentOf(sim, resident);
        CommandId command = sim.World.AllocateCommand(Core);
        if (!crafts.Request(resident, kind, command)) { notice = "Cannot start a craft for this resident now."; return; }
        ActivityId practice = new ActivityId(Crafts.CarrierActivity);
        CommandResult result = sim.Commands.Submit(new CommandEnvelope(command, sim.World.Epoch,
            sim.World.GameSeconds, CommandSource.PlayerHuman, record.ResidentSequence + 1u, CommandKind.DirectActivity,
            resident, practice, (long)craftsSpot, new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Append)));
        hud.ActionResult(resident, practice, result);
        if (!result.Accepted)
        {
            crafts.CancelRequest(command);
            notice = "Craft refused: " + result.Reason;
        }
        else
        {
            crafts.Track(sim.Commands);
            notice = record.DisplayName + " started their " + Crafts.KindNames[(int)kind].ToLowerInvariant()
                + " (queued crafts: " + crafts.PendingCount(resident) + ")";
        }
        Log("crafts request resident=" + (long)resident + " kind=" + kind + " accepted=" + B(result.Accepted));
    }

    // LiveTick: one completed execution, after Creative.Complete.
    void CraftsCompleted(ExecutionRecord execution)
    {
        CraftPiece piece = crafts.Complete(Exec, execution.Action, execution.Activity, execution.Resident, sim.Appearances);
        if (piece == null)
        {
            if (crafts.LastRefusal != null)
                notice = ResidentOf(sim, execution.Resident).DisplayName + " made no craft: " + crafts.LastRefusal + ".";
            return;
        }
        craftsView.Rebuild(crafts, sim.World);
        string name = ResidentOf(sim, piece.Author).DisplayName;
        notice = name + " finished their " + Crafts.KindNames[(int)piece.Kind].ToLowerInvariant() + ": it is now "
            + CraftsView.Placement(piece.Kind) + " at home.";
        Log("crafts finished item=" + (long)piece.Item + " kind=" + piece.Kind + " author=" + (long)piece.Author
            + " home=" + CraftsView.SceneHomeOf(sim.World, piece.Home) + " action=" + (long)piece.Action);
        craftsQueue.Enqueue(piece);
        if (craftsWorker == null) craftsWorker = StartCoroutine(CraftsGenerate());
    }

    IEnumerator CraftsGenerate()
    {
        while (craftsQueue.Count > 0)
        {
            CraftPiece piece = craftsQueue.Dequeue();
            // disclosureGiven: the player queued this craft from the Crafts rows, whose header discloses
            // Meta image generation. hasCredential: the key is resolved only inside the send
            // (LiveTransport.Post); without one nothing is sent and the procedural image stays.
            GatewayRequest request = craftsAdapter.RequestPrompt(piece.Item, piece.Prompt, craftsEndpoint, true, true);
            if (request != null)
                yield return craftsTransport.Generate(craftsAdapter, craftsBoundary, request, craftsAdapter.Payload, piece.Kind);
            ImageContent content = craftsAdapter.GetContent(piece.Item);
            string reason = request == null ? "the request was refused locally"
                : content == null || content.Path != "generated" ? craftsTransport.FailureReason
                : Crafts.ImageProblem(content.ImageBase64)
                    ?? (CraftsView.Decodes(content.ImageBase64) ? null : "the generated image could not be decoded");
            if (reason != null || !crafts.SetGenerated(piece.Item, content.ImageBase64)) crafts.SetProcedural(piece.Item, reason);
            craftsView.Refresh(piece);
            Log("crafts image item=" + (long)piece.Item + " path=" + piece.ImagePath + " transport=" + craftsTransport.Mode);
        }
        craftsWorker = null;
    }

    void CraftsUpdate()
    {
        if (craftsAdapter == null) return;
        int dropped = crafts.Track(sim.Commands);
        if (dropped > 0) Log("crafts dropped cancelled requests=" + dropped);
        if (crafts.FollowHouseholds()) craftsView.Rebuild(crafts, sim.World);
        if (craftsWorker != null) craftsAdapter.Tick(Time.unscaledDeltaTime);
        craftsHover = craftsHomeView >= 0 && alphaCamera != null && !alphaHud.Blocks(Input.mousePosition)
            ? craftsView.PieceAt(crafts, alphaCamera.ScreenPointToRay(Input.mousePosition)) : null;
    }

    void ToggleCraftsView()
    {
        if (craftsHomeView >= 0) { craftsHomeView = -1; notice = "Back to the block."; return; }
        ResidentRecord r;
        HouseholdRecord household;
        if (!navigation.SelectedResident.HasValue || !sim.World.TryGetResident(navigation.SelectedResident.Value, out r)
            || !sim.World.TryGetHousehold(r.Household, out household))
        {
            notice = "Select a resident first (N).";
            return;
        }
        craftsViewHome = household.Home;
        craftsHomeView = 1;
        notice = "Inside " + r.DisplayName + "'s home. Hover a piece to see how it was made.";
    }

    // ApplyCamera: the home view owns the camera while it is on.
    bool CraftsCamera()
    {
        Vector3 position, look;
        if (craftsHomeView < 0 || craftsView == null
            || !craftsView.TryViewPose(sim.World, craftsViewHome, craftsHomeView, out position, out look)) return false;
        alphaCamera.transform.position = position;
        alphaCamera.transform.LookAt(look);
        alphaCamera.fieldOfView = CraftsView.ViewFieldOfView(craftsHomeView);
        return true;
    }

    void CraftsOnGUI()
    {
        if (craftsView == null || crafts == null) return;
        CraftPiece piece = craftsHover ?? (craftsPinned.HasValue ? crafts.Find(craftsPinned.Value) : null);
        if (piece == null) return;
        craftsView.DrawInfo(piece, ResidentOf(sim, piece.Author).DisplayName + "'s "
            + Crafts.KindNames[(int)piece.Kind].ToLowerInvariant());
    }

    // ---------- opt-in evidence session ----------

    IEnumerator RunCraftsDemo(string dir)
    {
        string outPath = Path.GetFullPath(dir);
        Directory.CreateDirectory(outPath);
        List<string> captures = new List<string>();
        List<string> checks = new List<string>();
        bool ok = true;
        Action<string, bool, string> check = (id, pass, detail) =>
        {
            checks.Add("{\"id\":" + Q(id) + ",\"pass\":" + B(pass) + ",\"detail\":" + Q(detail) + "}");
            Log("crafts demo " + (pass ? "PASS " : "FAIL ") + id + " " + detail);
            if (!pass) ok = false;
        };
        yield return new WaitForSecondsRealtime(1f);
        ResidentId a = default(ResidentId), b = default(ResidentId);
        bool found = false;
        foreach (ResidentId first in sim.Residents)
        {
            ResidentRecord ra = ResidentOf(sim, first);
            HouseholdRecord household;
            if (!ra.IsAlive || ra.Stage < LifeStage.Child || !sim.World.TryGetHousehold(ra.Household, out household)
                || CraftsView.SceneHomeOf(sim.World, household.Home) != "HomeA") continue;
            foreach (ResidentId second in sim.Residents)
            {
                ResidentRecord rb = ResidentOf(sim, second);
                if (second != first && rb.IsAlive && rb.Stage >= LifeStage.Child && rb.Household.Equals(ra.Household))
                { a = first; b = second; found = true; break; }
            }
            if (found) break;
        }
        check("demo-residents", found, "two crafters in one HomeA household: " + (long)a + "," + (long)b);
        if (!found) { CraftsFinishDemo(outPath, captures, checks, false); yield break; }
        CraftsDemoBikesOff();
        // Live residents start with no money and crafts cost Make Wall Art materials: the demo funds its two crafters.
        sim.World.SetMoney(Core, a, 1000L);
        sim.World.SetMoney(Core, b, 1000L);
        Log("crafts demo funds a=1000 b=1000");

        Select(a);
        OpenActivity(9);
        yield return null;
        yield return null;
        yield return CraftsCapture(outPath, "01-creative-card-crafts", captures);
        hudFrame.Craft((int)CraftKind.Portrait);
        hudFrame.Craft((int)CraftKind.Pottery);
        Select(b);
        OpenActivity(9);
        for (int k = 1; k <= 4; k++) hudFrame.Craft(k);
        check("requests-queued", crafts.PendingCount(a) == 2 && crafts.PendingCount(b) == 4,
            "a=" + crafts.PendingCount(a) + " b=" + crafts.PendingCount(b));
        hudFrame.CloseMenu();
        Select(a);
        hudFrame.Craft(Crafts.KindNames.Length);       // the "See the pieces at home" row
        craftsHomeView = 0;
        SetSpeed(SimulationSpeed.Ultra);
        float deadline = Time.realtimeSinceStartup + 300f;
        while (crafts.Pieces.Count < 1 && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;
        yield return CraftsCapture(outPath, "02-portrait-finished-toast", captures);
        while (crafts.Pieces.Count < 6 && Time.realtimeSinceStartup < deadline) yield return null;
        while ((craftsWorker != null || craftsQueue.Count > 0) && Time.realtimeSinceStartup < deadline) yield return null;
        check("six-kinds-finished", crafts.Pieces.Count == 6, "pieces=" + crafts.Pieces.Count);
        Log("crafts demo money a=" + ResidentOf(sim, a).Money + " b=" + ResidentOf(sim, b).Money);
        CraftPiece portrait = null, pottery = null;
        int[] perKind = new int[6];
        foreach (CraftPiece p in crafts.Pieces)
        {
            perKind[(int)p.Kind]++;
            if (p.Kind == CraftKind.Portrait) portrait = p;
            if (p.Kind == CraftKind.Pottery) pottery = p;
        }
        check("each-kind-once", Array.TrueForAll(perKind, n => n == 1), string.Join(",", Array.ConvertAll(perKind, n => n.ToString())));
        check("portrait-and-pottery-by-a", portrait != null && pottery != null && portrait.Author == a && pottery.Author == a,
            "portrait=" + (portrait == null ? "-" : ((long)portrait.Author).ToString()) + " pottery=" + (pottery == null ? "-" : ((long)pottery.Author).ToString()));
        if (portrait != null) craftsPinned = portrait.Item;
        craftsHomeView = 0;
        yield return CraftsCapture(outPath, "03-home-east-portrait-pottery-prompt", captures);
        if (pottery != null) craftsPinned = pottery.Item;
        yield return CraftsCapture(outPath, "04-home-east-pottery-prompt", captures);
        craftsPinned = null;
        craftsHomeView = 1;
        yield return CraftsCapture(outPath, "05-home-room-six-kinds", captures);
        string before = CraftsDigest();
        SaveLive();
        yield return null;
        bool sidecar = File.Exists(Path.Combine(saveDirectory, CraftsSave.FileName));
        check("saved-with-crafts", sidecar, "crafts.json present=" + B(sidecar));
        ReloadLive();
        CraftsDemoBikesOff();
        yield return null;
        check("reload-restores-pieces", CraftsDigest() == before, "pieces=" + crafts.Pieces.Count);
        craftsViewHome = HomeOfResident(a);
        craftsHomeView = 1;
        yield return CraftsCapture(outPath, "06-home-room-after-reload", captures);
        SetSpeed(SimulationSpeed.Ultra);
        float until = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < until) yield return null;
        check("no-duplicate-after-reload", crafts.Pieces.Count == 6 && CraftsDigest() == before, "pieces=" + crafts.Pieces.Count);
        ReloadLive();
        CraftsDemoBikesOff();
        yield return null;
        check("second-reload-same", CraftsDigest() == before, "pieces=" + crafts.Pieces.Count);
        CraftsFinishDemo(outPath, captures, checks, ok);
    }

    // Known GYM-TRAIL defect on this base, fixed on impl/gym-trail-freeze (b19e7416b, 07f847fee) but
    // not in integrate/wave1: a bike trip from an off-paving start throws "No complete walking path"
    // in GymTrailDirector.AlphaWalk and stops LiveTick. The demo turns bicycles off for every
    // resident, as the Outing card's bicycle toggle does, so the crafts session is not cut short.
    void CraftsDemoBikesOff()
    {
        if (sim.Executor.Bicycles == null) return;
        foreach (ResidentId resident in sim.Residents) sim.Executor.Bicycles.PreferBicycle(resident, false);
        Log("crafts demo bicycles off residents=" + sim.Residents.Length);
    }

    HomeId HomeOfResident(ResidentId resident)
    {
        HouseholdRecord household;
        return sim.World.TryGetHousehold(ResidentOf(sim, resident).Household, out household) ? household.Home : craftsViewHome;
    }

    string CraftsDigest()
    {
        StringBuilder sb = new StringBuilder();
        foreach (CraftsSnapshot.PieceRow row in crafts.Export().Pieces)
            sb.Append(row.Item).Append('|').Append(row.Author).Append('|').Append(row.Kind).Append('|').Append(row.Home)
                .Append('|').Append(row.ImagePath).Append('|').Append(row.ImageBase64.Length).Append('|').Append(row.Prompt).Append('\n');
        return sb.ToString();
    }

    IEnumerator CraftsCapture(string dir, string id, List<string> captures)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
        string file = id + "-" + Screen.width + "x" + Screen.height + ".png";
        File.WriteAllBytes(Path.Combine(dir, file), texture.EncodeToPNG());
        UnityEngine.Object.Destroy(texture);
        captures.Add(file);
        Log("crafts capture " + file);
    }

    void CraftsFinishDemo(string dir, List<string> captures, List<string> checks, bool ok)
    {
        StringBuilder pieces = new StringBuilder();
        if (crafts != null)
            foreach (CraftPiece p in crafts.Pieces)
            {
                if (pieces.Length > 0) pieces.Append(',');
                pieces.Append("{\"item\":").Append((long)p.Item).Append(",\"author\":").Append((long)p.Author)
                    .Append(",\"author_name\":").Append(Q(ResidentOf(sim, p.Author).DisplayName))
                    .Append(",\"kind\":").Append(Q(p.Kind.ToString())).Append(",\"scene_home\":").Append(Q(CraftsView.SceneHomeOf(sim.World, p.Home)))
                    .Append(",\"placement\":").Append(Q(CraftsView.Placement(p.Kind))).Append(",\"image_path\":").Append(Q(p.ImagePath))
                    .Append(",\"disclosure\":").Append(Q(p.Disclosure)).Append(",\"prompt\":").Append(Q(p.Prompt)).Append('}');
            }
        string json = "{\"session\":\"CRAFTS fake-transport\",\"transport\":" + Q(craftsTransport == null ? "" : craftsTransport.Mode)
            + ",\"screen\":" + Q(Screen.width + "x" + Screen.height) + ",\"pass\":" + B(ok)
            + ",\"checks\":[" + string.Join(",", checks) + "],\"captures\":[" + string.Join(",", captures.ConvertAll(Q))
            + "],\"pieces\":[" + pieces + "]}";
        File.WriteAllText(Path.Combine(dir, "session.json"), json);
        Log("crafts demo done pass=" + B(ok));
        Application.Quit(ok ? 0 : 1);
    }
}
