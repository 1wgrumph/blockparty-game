// PROBE-SAM local inspection; isolated-csharp, no editor/network/credential I/O.
// Vendor record follows PROBE-D1U: model (nonempty provider id), provider_facts
// {endpoint, request_schema, response_schema, terms, quota, incremental_cost},
// account_access {status, owner, reason}, live_verdict, local_fallback, cost_status.
// Facts carry {status, value, source_url, retrieved_on, reason}; recorded facts
// have a value, unknown facts have null plus a reason. Account coverage is
// separate from these sourced facts and never establishes connected success.
// All frames below are SYNTHETIC CONTRACT-BP-07 inputs, not vendor wire samples.
// Their capture/outlines/polygon fields serialize the existing SamResult type.
// This harness qualifies local shapes through the real ProposalBoundary only;
// vendor parser, UI, miss logging and live acceptance remain with later SAM work.
// negative-fixture misreports the denied frame as connected success: exit 1.
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Simulation;

public static class ProbeSamChecks
{
    private static int failures;
    private static readonly string[] FactNames =
        { "endpoint", "request_schema", "response_schema", "terms", "quota", "incremental_cost" };

    // SYNTHETIC: same capture, zero/one/two image-coordinate outlines.
    private const string Zero = "{\"capture\":41,\"outlines\":[]}";
    private const string One = "{\"capture\":41,\"outlines\":[{\"polygon\":[[10,20],[30,20],[30,40]]}]}";
    private const string Multiple = "{\"capture\":41,\"outlines\":[{\"polygon\":[[10,20],[30,20],[30,40]]},{\"polygon\":[[50,60],[70,60],[70,80]]}]}";
    private const string Denied = "{\"outcome\":\"denied\",\"connected_success\":false}";

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static string Text(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool Nonempty(string value) { return !string.IsNullOrWhiteSpace(value); }

    private static bool Fact(JsonElement fact)
    {
        string source = Text(fact, "source_url");
        if (source == null || !source.StartsWith("https://", StringComparison.Ordinal) || source.Length <= 8
            || !Nonempty(Text(fact, "reason"))
            || !DateTime.TryParseExact(Text(fact, "retrieved_on"), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                out DateTime date) || date.Date > DateTime.UtcNow.Date
            || !fact.TryGetProperty("value", out JsonElement value)) return false;
        if (Text(fact, "status") == "unknown") return value.ValueKind == JsonValueKind.Null;
        return Text(fact, "status") == "recorded"
            && (value.ValueKind == JsonValueKind.String && Nonempty(value.GetString())
                || value.ValueKind == JsonValueKind.Object && value.EnumerateObject().MoveNext());
    }

    private static bool Record(JsonElement root)
    {
        if (!Nonempty(Text(root, "model")) || Text(root, "live_verdict") != "not_run"
            || !Nonempty(Text(root, "local_fallback")) || Text(root, "cost_status") != "unpriced"
            || !root.TryGetProperty("provider_facts", out JsonElement facts) || facts.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("account_access", out JsonElement access)) return false;
        foreach (string name in FactNames)
            if (!facts.TryGetProperty(name, out JsonElement fact) || !Fact(fact)) return false;
        string status = Text(access, "status");
        return Text(access, "owner") == "William" && Nonempty(Text(access, "reason"))
            && (status == "owner-pending" || status == "owner-recorded" || status == "not_run");
    }

    private static bool Record(string json)
    {
        try { using (JsonDocument doc = JsonDocument.Parse(json)) return Record(doc.RootElement); }
        catch (JsonException) { return false; }
    }

    private static string RecordControl()
    {
        // SYNTHETIC record control: citations are not provider evidence.
        var facts = new List<string>();
        foreach (string name in FactNames)
            facts.Add("\"" + name + "\":{\"status\":\"unknown\",\"value\":null,"
                + "\"source_url\":\"https://example.invalid/synthetic-only\",\"retrieved_on\":\"2026-10-28\","
                + "\"reason\":\"SYNTHETIC unknown fact control\"}");
        return "{\"model\":\"SAM 3.1\",\"provider_facts\":{" + string.Join(",", facts)
            + "},\"account_access\":{\"owner\":\"William\",\"status\":\"owner-recorded\","
            + "\"reason\":\"SYNTHETIC account coverage control; wiring untested\"},"
            + "\"live_verdict\":\"not_run\",\"cost_status\":\"unpriced\","
            + "\"local_fallback\":\"No outlines; authoritative game-data search remains available\"}";
    }

    private static bool ParseFrame(string frame, long capture, out SamResult result)
    {
        result = null;
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(frame))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("capture", out JsonElement id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long actual) || actual != capture
                    || !root.TryGetProperty("outlines", out JsonElement outlines) || outlines.ValueKind != JsonValueKind.Array)
                    return false;
                var parsed = new List<SamOutline>();
                foreach (JsonElement outline in outlines.EnumerateArray())
                {
                    if (outline.ValueKind != JsonValueKind.Object || !outline.TryGetProperty("polygon", out JsonElement polygon)
                        || polygon.ValueKind != JsonValueKind.Array) return false;
                    var points = new List<ImagePoint>();
                    foreach (JsonElement point in polygon.EnumerateArray())
                    {
                        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2
                            || point[0].ValueKind != JsonValueKind.Number || point[1].ValueKind != JsonValueKind.Number
                            || !point[0].TryGetSingle(out float x) || !point[1].TryGetSingle(out float y)
                            || float.IsInfinity(x) || float.IsInfinity(y) || float.IsNaN(x) || float.IsNaN(y)) return false;
                        points.Add(new ImagePoint(x, y));
                    }
                    if (points.Count < 3) return false;
                    parsed.Add(new SamOutline(new CaptureId(actual), points));
                }
                result = new SamResult(new CaptureId(actual), parsed);
                return true;
            }
        }
        catch (JsonException) { return false; }
    }

    private static ProposalBoundary Boundary()
    {
        WorldState world = WorldState.CreateSeeded(7L, 301L, 0, null);
        Clock clock = new Clock(world);
        var arbitrator = new CommandArbitrator(world, clock);
        return new ProposalBoundary(world, clock, arbitrator, new Autonomy(world, arbitrator));
    }

    private static ServiceEndpoint Endpoint(bool provisioned = true)
    {
        // SYNTHETIC configuration, never contacted; no vendor endpoint claim.
        return new ServiceEndpoint("sam", "https://example.invalid/synthetic-sam", "SAM 3.1", provisioned);
    }

    private static bool Logged(ProposalBoundary boundary, GatewayOutcome outcome)
    {
        foreach (ProposalLogEntry entry in boundary.DecisionLog)
            if (entry.Purpose == GatewayPurpose.ImageSearch && entry.Outcome == outcome) return true;
        return false;
    }

    private static void Shape(string frame, int count, string name)
    {
        bool parsed = ParseFrame(frame, 41L, out SamResult result);
        Check(parsed && result.Outlines.Count == count && (long)result.Capture == 41L, name + ".Shape");
        if (parsed)
            foreach (SamOutline outline in result.Outlines)
                Check((long)outline.Capture == 41L && outline.Polygon[0].Y == (count == 2
                    && outline == result.Outlines[1] ? 60f : 20f), name + ".ImageCoordinates");
        ProposalBoundary boundary = Boundary();
        GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageSearch, Endpoint(), true, true);
        Check(request != null && boundary.CompleteService(request.Id, parsed, "BadFrame") == ProposalVerdict.Applied
            && Logged(boundary, GatewayOutcome.Applied) && boundary.PendingCount == 0, name + ".BoundaryAppliedLocalOnly");
    }

    public static int Main(string[] args)
    {
        string control = RecordControl();
        Check(Record(control), "Record.SyntheticControl");
        Check(Record(control.Replace("\"unknown\"", "\"recorded\"").Replace("\"value\":null", "\"value\":\"SYNTHETIC fact\"")),
            "Record.RecordedFactControl");
        Check(!Record(control.Replace("\"source_url\"", "\"missing_source\"")), "Record.MissingSourceRejected");
        Check(!Record(control.Replace("\"retrieved_on\"", "\"missing_date\"")), "Record.MissingDateRejected");
        Check(!Record(control.Replace("SYNTHETIC unknown fact control", "")), "Record.UnknownNeedsReason");
        Check(!Record(control.Replace("\"not_run\"", "\"PASS\"")), "Record.AccountCoverageNotConnectedSuccess");
        Check(!Record(control.Replace("\"unpriced\"", "\"free\"")), "Record.AccountCoverageNotFreeCost");
        Check(!Record(control.Replace("\"account_access\"", "\"missing_account\"")), "Record.AccountSeparateFromFacts");
        foreach (string name in FactNames)
            Check(!Record(control.Replace("\"" + name + "\":", "\"missing_" + name + "\":")),
                "Record.RequiredFact." + name);

        const string path = "docs/production/vendors/sam.json";
        Check(File.Exists(path), "PROBE-SAM.VendorRecordExists.MissingEndpointSchemaTermsQuotaCostCapability");
        if (File.Exists(path))
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    Check(Record(doc.RootElement), "Record.SourcedFactsSeparateFromAccountAccess");
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("provider_facts", out JsonElement facts)
                        && facts.ValueKind == JsonValueKind.Object)
                        foreach (string name in FactNames)
                            Check(facts.TryGetProperty(name, out JsonElement fact) && Fact(fact), "Record.SourcedFact." + name);
                }
            }
            catch (JsonException) { Check(false, "Record.ValidJson"); }
            catch (IOException) { Check(false, "Record.Readable"); }
        }

        Shape(Zero, 0, "Frames.ZeroMatches");
        Shape(One, 1, "Frames.OneMatch");
        Shape(Multiple, 2, "Frames.MultipleMatches");
        string[] unusable = { "{", One.Replace("10,20", "\"bad\",20"), One.Replace("polygon", "missing_polygon"), Denied };
        string[] unusableNames = { "MalformedJson", "WrongCoordinateType", "MissingPolygon", "DeniedFrame" };
        for (int i = 0; i < unusable.Length; i++)
        {
            bool parsed = ParseFrame(unusable[i], 41L, out SamResult result);
            ProposalBoundary boundary = Boundary();
            GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageSearch, Endpoint(), true, true);
            Check(!parsed && result == null && boundary.CompleteService(request.Id, parsed, "BadFrame") == ProposalVerdict.DiscardedInvalid
                && Logged(boundary, GatewayOutcome.Malformed) && !Logged(boundary, GatewayOutcome.Applied)
                && boundary.PendingCount == 0, "Fallback." + unusableNames[i] + ".NoOutlinesNoSuccess");
        }
        Check(!ParseFrame(Multiple, 42L, out _), "Frames.NewerCaptureRejectsOldOutlines.LocalOnly");

        foreach (GatewayOutcome outcome in new[] { GatewayOutcome.Denied, GatewayOutcome.Unprovisioned, GatewayOutcome.Timeout })
        {
            ProposalBoundary boundary = Boundary();
            GatewayRequest request = boundary.RequestService(null, GatewayPurpose.ImageSearch, Endpoint(), true, true);
            Check(boundary.CompleteFailure(request.Id, outcome) == ProposalVerdict.Refused && Logged(boundary, outcome)
                && !Logged(boundary, GatewayOutcome.Applied) && boundary.PendingCount == 0, "Fallback." + outcome + ".NoConnectedSuccess");
        }
        foreach (bool missingCredential in new[] { true, false })
        {
            ProposalBoundary boundary = Boundary();
            Check(boundary.RequestService(null, GatewayPurpose.ImageSearch, Endpoint(missingCredential), !missingCredential, true) == null
                && Logged(boundary, GatewayOutcome.Unprovisioned) && !Logged(boundary, GatewayOutcome.Applied)
                && boundary.PendingCount == 0, missingCredential ? "Fallback.NoKey" : "Fallback.Unavailable");
        }

        string denied = args.Length == 1 && args[0] == "negative-fixture"
            ? Denied.Replace("false", "true") : Denied;
        using (JsonDocument doc = JsonDocument.Parse(denied))
            Check(Text(doc.RootElement, "outcome") == "denied"
                && doc.RootElement.GetProperty("connected_success").ValueKind == JsonValueKind.False,
                "NegativeFixture.DeniedFrameNeverConnectedSuccess");
        if (failures == 0) Console.WriteLine("PROBE-SAM ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
