// PROBE-IMG@1 local checks; shared resource group: isolated-csharp, no editor.
// Vendor record follows PROBE-D1U's provider_facts convention: endpoint,
// request_schema, response_schema, terms, quota, incremental_cost. Each fact
// has status recorded/unknown, value, source_url, retrieved_on, reason. Unknown
// requires null and an explanation; it establishes neither access nor free use.
// account_access is separate (status owner-pending/not_run, owner, reason),
// live_verdict is not_run, and local_fallback names the pre-authored alternative.
// Frames below are SYNTHETIC boundary inputs, NOT recorded vendor wire schemas.
// They use AI-BOUNDARY@1's committed outcome/presence contract; a true presence
// flag is not a credential. Applied on these inputs is local fixture acceptance,
// never connected evidence. Actual IMG parsing/content presentation belongs to
// IMAGE; here Refused/null plus the terminal log is the caller's fallback signal.
// No network, environment, settings, credential or image-file I/O.
// negative-fixture deliberately misreports a denied frame as parsed success;
// the same refusal/no-Applied assertions must detect it and exit 1.
using System;
using System.IO;
using System.Text.Json;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ProbeImgChecks
{
    private static int failures;
    private static readonly string[] FactNames =
        { "endpoint", "request_schema", "response_schema", "terms", "quota", "incremental_cost" };

    // SYNTHETIC: normalized adapter outcomes, sourced from GatewayOutcome and
    // service-provisioning.json's CompleteFailure/presence contract, not Meta.
    private static readonly string[] Frames =
    {
        "{\"provenance\":\"SYNTHETIC\",\"outcome\":\"Applied\"}",
        "{\"provenance\":\"SYNTHETIC\",\"outcome\":\"Denied\"}",
        "{\"provenance\":\"SYNTHETIC\",\"outcome\":\"Unprovisioned\"}",
        "{\"provenance\":\"SYNTHETIC\",\"outcome\":\"NoKey\"}",
        "{\"provenance\":\"SYNTHETIC\",\"outcome\":\"Timeout\"}"
    };

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static string Text(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }

    private static bool Nonempty(string value) { return !string.IsNullOrWhiteSpace(value); }

    private static bool Fact(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("value", out JsonElement fact)
            || !Nonempty(Text(value, "reason"))) return false;
        string source = Text(value, "source_url");
        if (source == null || !source.StartsWith("https://", StringComparison.Ordinal) || source.Length <= 8
            || source[8] == '/' || source.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '@', '?', '#' }) >= 0) return false;
        if (!DateTime.TryParseExact(Text(value, "retrieved_on"), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _)) return false;
        if (Text(value, "status") == "unknown") return fact.ValueKind == JsonValueKind.Null;
        return Text(value, "status") == "recorded"
            && (fact.ValueKind == JsonValueKind.String && Nonempty(fact.GetString())
                || fact.ValueKind == JsonValueKind.Object && fact.EnumerateObject().MoveNext());
    }

    private static void VendorRecord()
    {
        const string path = "docs/production/vendors/img.json";
        Check(File.Exists(path), "PROBE-IMG.VendorRecord.Exists");
        if (!File.Exists(path)) return;
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement root = doc.RootElement;
                Check(Nonempty(Text(root, "model")), "PROBE-IMG.VendorRecord.Model");
                JsonElement facts = default;
                bool hasFacts = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("provider_facts", out facts) && facts.ValueKind == JsonValueKind.Object;
                foreach (string name in FactNames)
                    Check(hasFacts && facts.TryGetProperty(name, out JsonElement fact) && Fact(fact),
                        "PROBE-IMG.VendorRecord.SourcedFact." + name);
                JsonElement access = default;
                bool hasAccess = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("account_access", out access);
                Check(hasAccess && (Text(access, "status") == "owner-pending" || Text(access, "status") == "not_run")
                    && Text(access, "owner") == "William" && Nonempty(Text(access, "reason")),
                    "PROBE-IMG.VendorRecord.AccountAccessSeparate");
                Check(Text(root, "live_verdict") == "not_run", "PROBE-IMG.VendorRecord.NoConnectedClaim");
                Check(Nonempty(Text(root, "local_fallback")), "PROBE-IMG.VendorRecord.LocalFallbackNamed");
            }
        }
        catch (JsonException) { Check(false, "PROBE-IMG.VendorRecord.ValidJson"); }
        catch (IOException) { Check(false, "PROBE-IMG.VendorRecord.Readable"); }
    }

    private static ProposalBoundary NewBoundary()
    {
        WorldState world = WorldState.CreateSeeded(7L, 301L, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        return new ProposalBoundary(world, clock, arbitrator, new Autonomy(world, arbitrator));
    }

    private static ServiceEndpoint Endpoint(bool provisioned)
    {
        // The committed AI-BOUNDARY fixture model; .invalid never gets contacted.
        return new ServiceEndpoint("meta-image", "https://example.invalid/synthetic-image", "muse-image-1", provisioned);
    }

    private static bool Logged(ProposalBoundary boundary, GatewayOutcome outcome, string detail)
    {
        foreach (ProposalLogEntry entry in boundary.DecisionLog)
            if (entry.Purpose == GatewayPurpose.ImageGeneration && entry.Outcome == outcome
                && entry.Detail == detail) return true;
        return false;
    }

    private static bool Applied(ProposalBoundary boundary)
    {
        foreach (ProposalLogEntry entry in boundary.DecisionLog)
            if (entry.Outcome == GatewayOutcome.Applied) return true;
        return false;
    }

    private static void FrameChecks(bool negativeFixture)
    {
        foreach (string frame in Frames)
        {
            using (JsonDocument doc = JsonDocument.Parse(frame))
            {
                JsonElement root = doc.RootElement;
                string outcome = Text(root, "outcome");
                string name = "PROBE-IMG.Synthetic." + outcome;
                Check(Text(root, "provenance") == "SYNTHETIC", name + ".Provenance");
                ProposalBoundary boundary = NewBoundary();
                GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageGeneration,
                    Endpoint(true), outcome != "NoKey", true);
                if (outcome == "NoKey")
                {
                    Check(request == null, name + ".FallbackSignal");
                    Check(Logged(boundary, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), name + ".Logged");
                }
                else
                {
                    Check(request != null, name + ".RequestIssued");
                    if (request == null) continue;
                    Check(request.Purpose == GatewayPurpose.ImageGeneration && request.ModelId == "muse-image-1",
                        name + ".TypedRequest");
                    if (outcome == "Applied")
                    {
                        Check(boundary.CompleteService(request.Id, true, null) == ProposalVerdict.Applied,
                            name + ".LocalFixtureAccepted");
                        Check(Logged(boundary, GatewayOutcome.Applied, "Validated"), name + ".Logged");
                    }
                    else if (outcome == "Timeout")
                    {
                        boundary.AdvanceRealSeconds(10.0);
                        Check(boundary.Tick() == 1, name + ".DeadlineFinalized");
                        Check(Logged(boundary, GatewayOutcome.Timeout, "ServiceTimeoutRealSeconds:10"), name + ".Logged");
                        Check(boundary.CompleteService(request.Id, true, null) == ProposalVerdict.DuplicateIgnored,
                            name + ".LateSuccessIgnored");
                    }
                    else
                    {
                        GatewayOutcome expected = outcome == "Denied" ? GatewayOutcome.Denied : GatewayOutcome.Unprovisioned;
                        ProposalVerdict verdict = negativeFixture && outcome == "Denied"
                            ? boundary.CompleteService(request.Id, true, null)
                            : boundary.CompleteFailure(request.Id, expected);
                        Check(verdict == ProposalVerdict.Refused, name + ".FallbackSignal");
                        Check(Logged(boundary, expected, "Provider:" + expected), name + ".Logged");
                        Check(boundary.CompleteService(request.Id, true, null) == ProposalVerdict.DuplicateIgnored,
                            name + ".LateSuccessIgnored");
                    }
                }
                Check(boundary.PendingCount == 0 && boundary.HeldCount == 0, name + ".NothingPending");
                if (outcome != "Applied") Check(!Applied(boundary), name + ".NeverConnectedSuccess");
            }
        }
        ProposalBoundary unavailable = NewBoundary();
        Check(unavailable.RequestService(null, GatewayPurpose.ImageGeneration, Endpoint(false), true, true) == null,
            "PROBE-IMG.UnavailableAdapter.FallbackSignal");
        Check(Logged(unavailable, GatewayOutcome.Unprovisioned, "Unprovisioned:AdapterNotProvisioned")
            && unavailable.PendingCount == 0 && !Applied(unavailable), "PROBE-IMG.UnavailableAdapter.NeverConnectedSuccess");
    }

    private static void FactControls()
    {
        // SYNTHETIC schema controls only; these URLs and values are not vendor facts.
        const string unknown = "{\"status\":\"unknown\",\"value\":null,\"source_url\":\"https://example.invalid/synthetic-only\","
            + "\"retrieved_on\":\"2026-10-28\",\"reason\":\"SYNTHETIC unknown, not account evidence\"}";
        string recorded = unknown.Replace("\"unknown\"", "\"recorded\"").Replace("null", "\"SYNTHETIC fact\"");
        string[] controls = { unknown, recorded, unknown.Replace("null", "0"),
            unknown.Replace("SYNTHETIC unknown, not account evidence", ""), recorded.Replace("\"SYNTHETIC fact\"", "null") };
        string[] names = { "UnknownExplicit", "RecordedSourced", "UnknownNotZero", "UnknownReasonRequired", "RecordedValueRequired" };
        for (int i = 0; i < controls.Length; i++)
            using (JsonDocument doc = JsonDocument.Parse(controls[i]))
                Check(Fact(doc.RootElement) == (i < 2), "PROBE-IMG.FactControl." + names[i]);
    }

    public static int Main(string[] args)
    {
        bool negativeFixture = args.Length == 1 && args[0] == "negative-fixture";
        if (negativeFixture) Console.WriteLine("PROBE-IMG negative-fixture: denied frame misreported as connected success");
        FactControls();
        FrameChecks(negativeFixture);
        VendorRecord();
        if (failures == 0) Console.WriteLine("PROBE-IMG ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
