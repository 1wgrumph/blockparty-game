/* IMAGE@1 TDD red contract: BP-AI-06/07, CONTRACT-BP-05-IMG.
New seam in Blockparty.AI.ImageAdapter, following CaptureSearch's injected boundary:
  ImageAdapter(WorldState, ProposalBoundary, Creative)
  GatewayRequest Request(ObjectId item, ServiceEndpoint, bool hasCredential, bool disclosureGiven)
  string Payload (outbound JSON body for the last issued request)
  ProposalVerdict Complete(GatewayRequestId, string imagesResponse)
  ProposalVerdict Fail(GatewayRequestId, GatewayOutcome)
  void Tick(double elapsedRealSeconds)
  GetContent(ObjectId) -> readable Path, ImageBase64, Disclosure strings (null if absent).
Path is generated or local-alternative. Content belongs to the existing CREATIVE
item, never a second item. GameStack's existing nine-argument creative overload
accepts ImageAdapter as argument ten; LoadedGame.Image restores its content.

Fixture provenance: PROBE-IMG.cs supplies synthetic gateway outcomes. The JSON
success/error shapes are recorded in docs/production/vendors/img.json (response_schema,
2026-10-28); image bytes and error text below are SYNTHETIC, never a live receipt.
There is no network, settings or credential access; hasCredential is presence only.
Negative-fixture misreports Denied as parsed success, preserving the fallback and
no-Applied oracles. It must exit 1 after implementation, not print ALL PASS.

Local mapping: FallbackAndReload -> SEIT-AI-07-P1/N1/N2, IF-IMG-N1,
ACT-12-N1; SuccessAndPayload -> AI-06-P1/N1, IF-IMG-P1, SECU-02-P1
(local parts); duplicate deliveries -> AI-07-N4, IF-IMG-N3. AC-04/15 and SUB-01 are
traceability only. Full UI, interruption/load cancellation (AI-07-N3/N5, IF-IMG-N2/N4),
provider compatibility, connected calls and acceptance remain at later gates.
This new module is required by the IMAGE package selector. Reuses real COMMAND,
NAV, CREATIVE, AI-BOUNDARY and SAVE-V2; only external responses are fixtures.
Resource group: isolated-csharp; no editor/display/port; owned save roots cleaned.
*/
using System;
using System.IO;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class ImageChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const StateWriter Exec = StateWriter.ActivityExecutor;
    private const string MemoryText = "helped the viewer carry a box";
    private const string Unrelated = "UNRELATED_RESIDENT_PRIVATE_MEMORY";
    // SYNTHETIC 1x1 PNG, in the recorded images-response b64_json envelope.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
    private const string Success = "{\"created\":1793232000,\"data\":[{\"b64_json\":\"" + Png
        + "\"}],\"output_format\":\"png\",\"background\":\"opaque\"}";
    // SYNTHETIC error values in the recorded OpenAI-style error envelope.
    private const string Denied = "{\"error\":{\"type\":\"invalid_request_error\","
        + "\"code\":\"content_policy_violation\",\"param\":\"prompt\",\"message\":\"SYNTHETIC denied\"}}";
    private static int failures;

    private sealed class Scene : ISceneContent
    {
        public ObjectId Station;
        public void RegisterObjects(ActivityExecutor executor)
        { executor.RegisterObject(Station, new[] { new ApproachPose(0f, 0f, 0f) }); }
    }

    private sealed class Fixture
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public ActivityExecutor Executor;
        public Accounts Accounts;
        public Food Food;
        public Creative Creative;
        public ProposalBoundary Boundary;
        public ImageAdapter Image;
        public ResidentId Author;
        public EventId Memory;
        public ObjectId Station;
        public WorkItem Item;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Require(bool ok, string detail)
    { if (!ok) throw new InvalidOperationException(detail); }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static ResidentRecord Author(Fixture f)
    {
        Require(f.World.TryGetResident(f.Author, out ResidentRecord record), "fixture author missing");
        return record;
    }

    private static Fixture NewFixture()
    {
        var f = new Fixture();
        f.World = WorldState.CreateSeeded(701L, 4242L, 2, new[] { "Author", Unrelated });
        ResidentRecord[] people = f.World.ResidentsSnapshot();
        f.Author = people[0].Id;
        f.World.SetMoney(Core, f.Author, 100L);
        f.World.AddSkillPoints(Core, f.Author, "Creativity", 1000);
        WorldEvent memory = f.World.AppendEvent(Core, "help", f.Author, MemoryText);
        f.Memory = memory.Event;
        f.World.AddMemory(Core, f.Author, new MemoryEntry(memory.Event, "help", 0L,
            people[0].Home, new[] { f.Author }, MemoryText, "participant"));
        WorldEvent privateMemory = f.World.AppendEvent(Core, "private", people[1].Id, Unrelated);
        f.World.AddMemory(Core, people[1].Id, new MemoryEntry(privateMemory.Event, "private", 0L,
            people[1].Home, new[] { people[1].Id }, Unrelated, "participant"));
        f.Clock = new Clock(f.World);
        f.Clock.SetPreset(AgingPreset.Off);
        f.Arbitrator = new CommandArbitrator(f.World, f.Clock);
        f.Executor = new ActivityExecutor(f.World, f.Arbitrator, f.Clock);
        f.Accounts = new Accounts(f.World);
        f.Food = new Food(f.World, f.Arbitrator, f.Executor, f.Accounts);
        f.Creative = new Creative(f.World);
        f.Boundary = new ProposalBoundary(f.World, f.Clock, f.Arbitrator,
            new Autonomy(f.World, f.Arbitrator));
        f.Image = new ImageAdapter(f.World, f.Boundary, f.Creative);
        do { f.Station = f.World.AllocateObject(Core); } while ((long)f.Station <= 2L);
        new Scene { Station = f.Station }.RegisterObjects(f.Executor);
        f.Executor.SetResidentPosition(f.Author, new ApproachPose(0f, 0f, 0f));
        ResidentRecord author = Author(f);
        CommandResult submitted = f.Arbitrator.Submit(new CommandEnvelope(
            f.World.AllocateCommand(Core), f.World.Epoch, f.World.GameSeconds,
            CommandSource.PlayerHuman, author.ResidentSequence + 1u,
            CommandKind.DirectActivity, f.Author, new ActivityId(CreativeWork.MakeWallArtActivity),
            (long)f.Station, new CommandParameters.Direct(QueueMode.Immediate)));
        Require(submitted.Accepted, "MakeWallArt command: " + submitted.Reason);
        ActionInstanceId action = f.Arbitrator.GetCurrent(f.Author).Action;
        f.Executor.Tick(Exec);
        f.Executor.Tick(Exec);
        Require(f.Executor.GetExecution(action).Stage == ExecutionStage.Perform, "MakeWallArt Perform");
        f.Clock.AdvanceRealSeconds(Core, 90.0);
        f.Executor.Tick(Exec);
        Require(f.Executor.GetExecution(action).Outcome == TerminalOutcome.Completed, "MakeWallArt completion");
        Require(f.Creative.Complete(Exec, action, new ActivityId(CreativeWork.MakeWallArtActivity),
            f.Author, f.Memory), "real creative completion");
        Require(f.Creative.WorkItems.Count == 1, "one real WorkItem required");
        f.Item = f.Creative.WorkItems[0];
        return f;
    }

    private static ServiceEndpoint Endpoint(bool provisioned = true)
    { return new ServiceEndpoint("meta-image", "https://example.invalid/synthetic-image", "muse-image-1.0", provisioned); }

    private static bool Logged(Fixture f, GatewayOutcome outcome)
    {
        foreach (var entry in f.Boundary.DecisionLog)
            if (entry.Purpose == GatewayPurpose.ImageGeneration && entry.Outcome == outcome) return true;
        return false;
    }

    private static bool IsPng(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value ?? "");
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length <= signature.Length) return false;
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) return false;
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool DisclosesLocal(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && (text.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("pre-authored", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void OneItem(Fixture f, string name)
    {
        int points = -1;
        foreach (var skill in Author(f).Skills) if (skill.SkillId == "Creativity") points = skill.Points;
        Check(f.Creative.WorkItems.Count == 1 && f.Creative.WorkItems[0].Reference.Item == f.Item.Reference.Item
            && f.Item.Reference.Author == f.Author && f.Item.Reference.MemoryEvent == f.Memory
            && !string.IsNullOrWhiteSpace(f.Item.Reference.PersonalitySummary)
            && f.Item.QualityTier == "plain" && Author(f).Money == 90L && points == 15,
            name + ".SameWorkItemAndSingleEffects");
    }

    private static void RoundTrip(Fixture f, string name)
    {
        var before = f.Image.GetContent(f.Item.Reference.Item);
        Require(before != null, "IMAGE content missing before public save");
        string directory = Path.Combine(Path.GetTempPath(), "image-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stack = new GameStack(f.World, f.Clock, f.Arbitrator, f.Executor,
                new AppearanceStore(), f.Accounts, f.Food, new Expenses(f.World, f.Accounts), f.Creative, f.Image);
            SaveWriteResult saved = SaveV1.SaveGame(stack, directory);
            Require(saved.Ok, "public save: " + saved.Detail);
            SaveLoadResult loaded = SaveV1.LoadGame(directory, new Scene { Station = f.Station });
            Require(loaded.Ok, "public load: " + loaded.Notice);
            Require(loaded.Game.Creative.WorkItems.Count == 1, "IMAGE reload must retain one WorkItem");
            WorkItem restored = loaded.Game.Creative.WorkItems[0];
            var after = loaded.Game.Image.GetContent(restored.Reference.Item);
            Check(after != null && after.Path == before.Path && after.ImageBase64 == before.ImageBase64
                && after.Disclosure == before.Disclosure && restored.Reference.Item == f.Item.Reference.Item
                && restored.Reference.Author == f.Author && restored.Reference.MemoryEvent == f.Memory
                && restored.Reference.PersonalitySummary == f.Item.Reference.PersonalitySummary
                && restored.QualityTier == "plain", name + ".PublicSaveRoundTripContentAndProvenance");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void FallbackAndReload(string fault, bool negativeFixture)
    {
        Fixture f = NewFixture();
        GatewayRequest request = f.Image.Request(f.Item.Reference.Item, Endpoint(fault != "Unavailable"),
            fault != "NoKey", true);
        GatewayOutcome expected = GatewayOutcome.Unprovisioned;
        if (fault == "Unavailable" || fault == "NoKey") Require(request == null, fault + " must not send");
        else
        {
            Require(request != null, fault + " synthetic request missing");
            if (fault == "Denied")
            {
                expected = GatewayOutcome.Denied;
                if (negativeFixture) f.Image.Complete(request.Id, Success);
                else f.Image.Fail(request.Id, GatewayOutcome.Denied);
            }
            else if (fault == "Timeout")
            {
                expected = GatewayOutcome.Timeout;
                f.Image.Tick(10.0);
                Check(f.Boundary.PendingCount == 1 && !Logged(f, GatewayOutcome.Timeout),
                    "FallbackAndReload.Timeout.PendingAt10");
                f.Image.Tick(30.0 - 10.0);
            }
            else { expected = GatewayOutcome.Malformed; f.Image.Complete(request.Id, "{\"data\":[]}"); }
        }
        var content = f.Image.GetContent(f.Item.Reference.Item);
        string name = "FallbackAndReload." + fault;
        Check(content != null && content.Path == "local-alternative" && IsPng(content.ImageBase64)
            && DisclosesLocal(content.Disclosure), name + ".DisclosedLocalVariant");
        Check(Logged(f, expected) && !Logged(f, GatewayOutcome.Applied)
            && f.Boundary.PendingCount == 0, name + ".FailureNeverConnectedSuccess");
        if (request != null)
        {
            string retained = content == null ? null : content.ImageBase64;
            f.Image.Complete(request.Id, Success);
            var late = f.Image.GetContent(f.Item.Reference.Item);
            Check(late != null && late.Path == "local-alternative" && late.ImageBase64 == retained,
                name + ".LateSuccessDoesNotReplaceFallback");
        }
        OneItem(f, name);
        RoundTrip(f, name);
    }

    private static void SuccessAndPayload()
    {
        Fixture f = NewFixture();
        GatewayRequest request = f.Image.Request(f.Item.Reference.Item, Endpoint(), true, true);
        Require(request != null, "IMAGE request must issue on synthetic presence");
        Check(request.Purpose == GatewayPurpose.ImageGeneration && request.ModelId == "muse-image-1.0",
            "SuccessAndPayload.TypedGateway");
        using (JsonDocument doc = JsonDocument.Parse(f.Image.Payload))
        {
            JsonElement body = doc.RootElement;
            string prompt = body.GetProperty("prompt").GetString();
            bool minimal = body.GetProperty("model").GetString() == "muse-image-1.0"
                && prompt.Contains(f.Item.Reference.PersonalitySummary) && prompt.Contains(MemoryText)
                && !f.Image.Payload.Contains(Unrelated);
            foreach (JsonProperty field in body.EnumerateObject())
                if (field.Name != "model" && field.Name != "prompt" && field.Name != "n"
                    && field.Name != "size" && field.Name != "response_format" && field.Name != "output_format"
                    && field.Name != "background") minimal = false;
            foreach (string forbidden in new[] { "MODEL_API_KEY", "Authorization", "Bearer", "accessToken", "api_key" })
                if (f.Image.Payload.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0) minimal = false;
            Check(minimal, "SuccessAndPayload.SEIT-SECU-02-P1-MinimalBodyNoKeyOrUnrelatedData");
        }
        Check(f.Image.Complete(request.Id, Success) == ProposalVerdict.Applied, "SuccessAndPayload.FixtureAcceptedLocally");
        var content = f.Image.GetContent(f.Item.Reference.Item);
        Check(content != null && content.Path == "generated" && content.ImageBase64 == Png,
            "SuccessAndPayload.ImageAttachedToExistingWorkItem");
        f.Image.Complete(request.Id, Success);
        OneItem(f, "SuccessAndPayload.DuplicateDelivery");
        RoundTrip(f, "SuccessAndPayload");
    }

    private static void DeniedFrame()
    {
        Fixture f = NewFixture();
        GatewayRequest request = f.Image.Request(f.Item.Reference.Item, Endpoint(), true, true);
        Require(request != null, "denied-frame synthetic request");
        f.Image.Complete(request.Id, Denied);
        var content = f.Image.GetContent(f.Item.Reference.Item);
        Check(content != null && content.Path == "local-alternative" && DisclosesLocal(content.Disclosure)
            && !Logged(f, GatewayOutcome.Applied), "DeniedFrame.ErrorEnvelopeNeverSuccess");
    }

    public static int Main(string[] args)
    {
        bool negativeFixture = Array.IndexOf(args, "negative-fixture") >= 0;
        if (negativeFixture) Console.WriteLine("IMAGE negative-fixture: Denied deliberately misreported as parsed success");
        foreach (string fault in new[] { "Denied", "Unavailable", "NoKey", "Timeout", "Malformed" })
            Run("FallbackAndReload." + fault, () => FallbackAndReload(fault, negativeFixture && fault == "Denied"));
        Run("SuccessAndPayload", SuccessAndPayload);
        Run("DeniedFrame", DeniedFrame);
        if (failures != 0) return 1;
        Console.WriteLine("IMAGE ALL PASS");
        return 0;
    }
}
