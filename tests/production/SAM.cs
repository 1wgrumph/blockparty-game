/*
SAM red contract, isolated-csharp: no editor, network, keys or settings access.
New product seam in Blockparty.AI / CaptureSearch.cs (no test implementation):
  CaptureSearch(ProposalBoundary boundary)
  void SetCapture(CaptureId id, byte[] rgba, int width, int height)
  GatewayRequest Request(string phrase, ServiceEndpoint endpoint,
                         bool hasCredential, bool disclosureGiven)
  ProposalVerdict Complete(GatewayRequestId request, string contractFrame)
  ProposalVerdict Fail(GatewayRequestId request, GatewayOutcome outcome)
  void Tick(double elapsedRealSeconds)
  readable SamResult Result; string CountText, Message; bool ConnectedSuccess;
  readable IReadOnlyList<...> Misses; each entry has CaptureId Capture, string Phrase.
The Complete seam consumes SYNTHETIC CONTRACT-BP-07 JSON from the committed
PROBE-SAM corpus, NOT vendor wire JSON. Vendor grammar is recorded separately
in docs/production/vendors/sam.json; these checks establish no live compatibility.
SetCapture replaces the displayed capture; completion must bind to its request.

One suite is required by the package gate; PROBE-SAM only checks its own parser,
not this product consumer. Oracles: BP-AI-11/12, CONTRACT-BP-07-SAM, TB-BP-05.
One/Multiple: local subclaims of SEIT-AI-11-P1/P2, SEIT-IF-SAM-P1.
Zero: SEIT-AI-12-P1/P2, SEIT-IF-SAM-N1; Stale: SEIT-AI-11-N2/IF-SAM-N2.
Fallback: SEIT-AI-12-N1/IF-SAM-N3; Isolation: AI-11-N1, AI-12-N2,
IF-SAM-N4, TB-05-N1/N2/N3. AC-17/SUB-01, actual UI drawing, consumer inspection,
actual game art and connected acceptance remain NOT RUN at their own gates.
negative-fixture substitutes a denied frame falsely claiming connected success
for the one-match input: the normal one-match assertion must print FAIL.
*/
using System;
using System.IO;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class SamChecks
{
    // Exact synthetic contract frames from tests/production/PROBE-SAM.cs.
    private const string Zero = "{\"capture\":41,\"outlines\":[]}";
    private const string One = "{\"capture\":41,\"outlines\":[{\"polygon\":[[10,20],[30,20],[30,40]]}]}";
    private const string Multiple = "{\"capture\":41,\"outlines\":[{\"polygon\":[[10,20],[30,20],[30,40]]},{\"polygon\":[[50,60],[70,60],[70,80]]}]}";
    private const string Denied = "{\"outcome\":\"denied\",\"connected_success\":false}";
    private static int failures;

    private sealed class EmptyScene : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor) { }
    }

    private sealed class Fixture
    {
        public GameStack Game;
        public ProposalBoundary Boundary;
        public WorldNavigation Navigation;
        public CaptureSearch Search;
        public ResidentId Resident;
    }

    private static Fixture NewFixture()
    {
        WorldState world = WorldState.CreateSeeded(7L, 301L, 1, new[] { "Orion" });
        Clock clock = new Clock(world);
        var commands = new CommandArbitrator(world, clock);
        var executor = new ActivityExecutor(world, commands, clock);
        var boundary = new ProposalBoundary(world, clock, commands, new Autonomy(world, commands));
        var search = new CaptureSearch(boundary);
        // Synthetic 100x100 RGBA capture; all corpus coordinates lie inside it.
        search.SetCapture((CaptureId)41L, new byte[100 * 100 * 4], 100, 100);
        var navigation = new WorldNavigation(world, commands, executor);
        ResidentId resident = world.ResidentsSnapshot()[0].Id;
        navigation.SelectResident(resident);
        return new Fixture
        {
            Game = new GameStack(world, clock, commands, executor, new AppearanceStore()),
            Boundary = boundary, Navigation = navigation, Search = search, Resident = resident,
        };
    }

    private static ServiceEndpoint Endpoint(bool provisioned = true)
    {
        return new ServiceEndpoint("sam", "https://example.invalid/synthetic-sam", "sam-3.1", provisioned);
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception e) { Check(false, name + " " + e); }
    }

    private static bool NoOutlines(Fixture f)
    {
        return f.Search.Result == null || f.Search.Result.Outlines.Count == 0;
    }

    private static bool Logged(Fixture f, GatewayOutcome outcome)
    {
        foreach (ProposalLogEntry entry in f.Boundary.DecisionLog)
            if (entry.Purpose == GatewayPurpose.ImageSearch && entry.Outcome == outcome) return true;
        return false;
    }

    private static string DecisionContext(Fixture f)
    {
        DecisionPayload p = f.Boundary.BuildDecisionPayload(f.Resident);
        return string.Join(",", p.Needs) + "|" + string.Join(",", p.AvailableActivityIds)
            + "|" + string.Join(",", p.KnownHistoryEventIds) + "|" + p.Constraints;
    }

    private static string Save(Fixture f, string directory)
    {
        SaveWriteResult saved = SaveV1.SaveGame(f.Game, directory);
        if (!saved.Ok) throw new Exception("save failed: " + saved.Detail);
        SaveLoadResult loaded = SaveV1.LoadGame(directory, new EmptyScene());
        if (!loaded.Ok) throw new Exception("load failed: " + loaded.Notice);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, SaveFormat.FileName))))
            return doc.RootElement.GetProperty("payload").GetRawText();
    }

    private static bool SameFacts(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind == JsonValueKind.Object)
        {
            int left = 0, right = 0;
            foreach (JsonProperty p in a.EnumerateObject())
            {
                // Gateway request allocation is bookkeeping, not a SAM world fact.
                if (p.Name == "nextGatewayId") continue;
                left++;
                if (!b.TryGetProperty(p.Name, out JsonElement other) || !SameFacts(p.Value, other)) return false;
            }
            foreach (JsonProperty p in b.EnumerateObject()) if (p.Name != "nextGatewayId") right++;
            return left == right;
        }
        if (a.ValueKind == JsonValueKind.Array)
        {
            if (a.GetArrayLength() != b.GetArrayLength()) return false;
            for (int i = 0; i < a.GetArrayLength(); i++) if (!SameFacts(a[i], b[i])) return false;
            return true;
        }
        return a.GetRawText() == b.GetRawText();
    }

    private static void Matches(string frame, string phrase, int count, string name)
    {
        Fixture f = NewFixture();
        string context = DecisionContext(f);
        string root = Path.Combine(Path.GetTempPath(), "blockparty-sam-" + Guid.NewGuid().ToString("N"));
        try
        {
            string before = Save(f, root);
            GatewayRequest request = f.Search.Request(phrase, Endpoint(), true, true);
            Check(request != null && request.Purpose == GatewayPurpose.ImageSearch, name + ".ImagePhraseRequest");
            if (request == null) return;
            ProposalVerdict verdict = f.Search.Complete(request.Id, frame);
            SamResult result = f.Search.Result;
            Check(verdict == ProposalVerdict.Applied && result != null && result.Capture == (CaptureId)41L
                && result.Outlines.Count == count && f.Search.CountText == count.ToString()
                && !f.Search.ConnectedSuccess && f.Boundary.PendingCount == 0,
                name + ".SameCaptureOutlinesAndHonestCount");
            if (result != null && result.Outlines.Count == count)
                for (int i = 0; i < count; i++)
                {
                    SamOutline o = result.Outlines[i];
                    float x = i == 0 ? 10f : 50f, y = i == 0 ? 20f : 60f;
                    Check(o.Capture == (CaptureId)41L && o.Polygon.Count == 3
                        && o.Polygon[0].X == x && o.Polygon[0].Y == y
                        && o.Polygon[1].X == x + 20f && o.Polygon[1].Y == y
                        && o.Polygon[2].X == x + 20f && o.Polygon[2].Y == y + 20f,
                        name + ".ImageCoordinates." + i);
                }
            string message = (f.Search.Message ?? "").ToLowerInvariant();
            Check(message.Contains("not") && message.Contains("ident") && message.Contains("resident")
                && message.Contains("location") && message.Contains("activit")
                && !message.Contains("orion"), name + ".NoIdentityLimitation");
            if (count == 0)
            {
                bool miss = false;
                foreach (var entry in f.Search.Misses)
                    if (entry.Capture == (CaptureId)41L && entry.Phrase == phrase) miss = true;
                Check(miss, name + ".MissLoggedWithCaptureAndPhrase");
            }
            Check(context == DecisionContext(f) && f.Navigation.SelectedResident == f.Resident
                && f.Navigation.SearchByName("Orion").Count == 1
                && f.Navigation.SearchByName(phrase).Count == 0,
                name + ".NoDecisionContextSelectionOrAuthoritativeSearchMutation");
            string after = Save(f, root);
            using (JsonDocument a = JsonDocument.Parse(before))
            using (JsonDocument b = JsonDocument.Parse(after))
                Check(SameFacts(a.RootElement, b.RootElement), name + ".PublicSaveRoundTripNoWorldFact");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Stale()
    {
        Fixture f = NewFixture();
        GatewayRequest request = f.Search.Request("chair", Endpoint(), true, true);
        f.Search.SetCapture((CaptureId)42L, new byte[100 * 100 * 4], 100, 100);
        Check(NoOutlines(f), "Stale.NewCaptureClearsOverlay");
        Check(f.Search.Complete(request.Id, Multiple) != ProposalVerdict.Applied && NoOutlines(f)
            && f.Search.CountText == "0", "Stale.OldCaptureNeverOverlaidOnNewCapture");
    }

    private static void Fallbacks()
    {
        string[] frames = { "{", One.Replace("10,20", "\"bad\",20"), One.Replace("polygon", "missing_polygon"), Denied };
        string[] names = { "MalformedJson", "WrongCoordinateType", "MissingPolygon", "DeniedFrame" };
        for (int i = 0; i < frames.Length; i++)
        {
            Fixture f = NewFixture();
            GatewayRequest request = f.Search.Request("easel", Endpoint(), true, true);
            Check(f.Search.Complete(request.Id, frames[i]) != ProposalVerdict.Applied
                && NoOutlines(f) && f.Search.CountText == "0" && !f.Search.ConnectedSuccess
                && !string.IsNullOrWhiteSpace(f.Search.Message) && !Logged(f, GatewayOutcome.Applied)
                && f.Boundary.PendingCount == 0, "Fallback." + names[i] + ".NoInventedMatchOrConnectedSuccess");
        }
        foreach (GatewayOutcome outcome in new[] { GatewayOutcome.Denied, GatewayOutcome.Unprovisioned, GatewayOutcome.Timeout })
        {
            Fixture f = NewFixture();
            GatewayRequest request = f.Search.Request("chair", Endpoint(), true, true);
            f.Search.Fail(request.Id, outcome);
            Check(NoOutlines(f) && f.Search.CountText == "0" && !f.Search.ConnectedSuccess
                && !string.IsNullOrWhiteSpace(f.Search.Message) && Logged(f, outcome)
                && !Logged(f, GatewayOutcome.Applied) && f.Boundary.PendingCount == 0
                && f.Navigation.SearchByName("Orion").Count == 1,
                "Fallback." + outcome + ".OutcomeShownAuthoritativeSearchAvailable");
        }
        foreach (bool missingKey in new[] { true, false })
        {
            Fixture f = NewFixture();
            Check(f.Search.Request("chair", Endpoint(missingKey), !missingKey, true) == null
                && NoOutlines(f) && f.Search.CountText == "0" && !f.Search.ConnectedSuccess
                && !string.IsNullOrWhiteSpace(f.Search.Message) && !Logged(f, GatewayOutcome.Applied)
                && f.Boundary.PendingCount == 0, missingKey ? "Fallback.NoKey" : "Fallback.Unavailable");
        }
        Fixture prior = NewFixture();
        GatewayRequest found = prior.Search.Request("chair", Endpoint(), true, true);
        prior.Search.Complete(found.Id, Multiple);
        Check(prior.Search.Result != null && prior.Search.Result.Outlines.Count == 2,
            "Fallback.PriorOverlayEstablished");
        GatewayRequest denied = prior.Search.Request("chair", Endpoint(), true, true);
        prior.Search.Fail(denied.Id, GatewayOutcome.Denied);
        Check(NoOutlines(prior) && prior.Search.CountText == "0" && !prior.Search.ConnectedSuccess,
            "Fallback.DeniedClearsPriorSuccessfulOverlay");

        Fixture timeout = NewFixture();
        timeout.Search.Request("chair", Endpoint(), true, true);
        timeout.Search.Tick(10.0); // Existing AI-BOUNDARY service deadline, not a wall-clock wait.
        Check(NoOutlines(timeout) && !timeout.Search.ConnectedSuccess && Logged(timeout, GatewayOutcome.Timeout)
            && timeout.Boundary.PendingCount == 0, "Fallback.ElapsedDeadlineNoSuccess");
    }

    private static void JsonDocumentCompatibility()
    {
        string[] frames =
        {
            One.Replace("41", "041"),
            One.Replace("10,20", "010,20"),
            One.Replace("10,20", ".5,20"),
            One.Replace("10,20", "10.,20"),
            One.Substring(0, One.Length - 1) + ",\"note\":\"raw\ncontrol\"}",
        };
        string[] names = { "LeadingZeroCapture", "LeadingZeroCoordinate", "MissingIntegerPart",
            "MissingFractionPart", "RawControlInIgnoredString" };
        for (int i = 0; i < frames.Length; i++)
        {
            Fixture f = NewFixture();
            GatewayRequest request = f.Search.Request("chair", Endpoint(), true, true);
            Check(f.Search.Complete(request.Id, frames[i]) == ProposalVerdict.DiscardedInvalid
                && f.Search.Result == null && f.Search.CountText == "0" && f.Search.Misses.Count == 0
                && !f.Search.ConnectedSuccess && !Logged(f, GatewayOutcome.Applied)
                && f.Boundary.PendingCount == 0 && f.Navigation.SearchByName("Orion").Count == 1,
                "JsonCompatibility." + names[i] + ".DiscardedInvalid");
        }
        Matches(One.Substring(0, One.Length - 1) + ",\"note\":0,\"note\":1}",
            "chair", 1, "JsonCompatibility.DuplicateIgnoredField");
        Matches(One.Replace("\"capture\":41", "\"capture\":0,\"capture\":41"),
            "chair", 1, "JsonCompatibility.DuplicateCaptureLastWins");
    }

    public static int Main(string[] args)
    {
        bool negative = args.Length == 1 && args[0] == "negative-fixture";
        Run("One", () => Matches(negative ? Denied.Replace("false", "true") : One, "easel", 1, "One"));
        Run("Zero", () => Matches(Zero, "piano", 0, "Zero"));
        Run("Multiple", () => Matches(Multiple, "chair", 2, "Multiple"));
        Run("Stale", Stale);
        Run("Fallback", Fallbacks);
        Run("JsonCompatibility", JsonDocumentCompatibility);
        if (failures == 0) Console.WriteLine("SAM ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
