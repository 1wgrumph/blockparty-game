// PROBE-D1U record contract (local inspection only; shared resource group: none).
// docs/production/vendors/d1u.json is an object with exactly these fields:
// model: "d1-ultra"; provider_facts: {endpoint, request_schema, response_schema,
// terms, quota, incremental_cost}; account_access: {status, owner, reason};
// live_verdict: "not_run"; local_fallback: a nonempty name; cost_status: "unpriced".
// Each provider fact has exactly {status, value, source_url, retrieved_on, reason}.
// status "recorded" requires nonempty value (string/object); "unknown" requires
// null value and a nonempty reason. Every fact requires an HTTPS source URL and
// a YYYY-MM-DD retrieval date. Unknown does not mean free, zero quota or access.
// Account status is "owner-pending" or "not_run", owner "William", reason nonempty.
// Account/cost acceptance, key entry and first live call remain owner-only.
// Fixture record is SYNTHETIC per the task/SEIT negative oracles: its .invalid
// citations and unknown values are validator inputs, never provider evidence.
// Checks qualify only the local record, not whole SEIT cases, provider wire
// parsing, physical execution or connected acceptance. No network/credential I/O.
// "negative-fixture" corrupts the positive schema control and must exit 1.
using System;
using System.IO;
using System.Text.Json;

public static class ProbeD1UChecks
{
    private static int failures;
    private static readonly string[] FactNames =
        { "endpoint", "request_schema", "response_schema", "terms", "quota", "incremental_cost" };

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

    private static bool Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            int matches = 0;
            foreach (string name in names) if (property.Name == name) matches++;
            if (matches != 1) return false;
            count++;
        }
        if (count != names.Length) return false;
        foreach (string name in names) if (!value.TryGetProperty(name, out _)) return false;
        return true;
    }

    private static bool SecretShaped(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString();
            foreach (string marker in new[] { "sk-", "LLM_", "Bearer " })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string name = property.Name.ToLowerInvariant();
                if (name == "api_key" || name == "apikey" || name == "accesstoken"
                    || name == "access_token" || name == "secret" || name == "password") return true;
                if (SecretShaped(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) if (SecretShaped(item)) return true;
        return false;
    }

    private static bool Fact(JsonElement fact)
    {
        if (!Fields(fact, "status", "value", "source_url", "retrieved_on", "reason")) return false;
        string source = Text(fact, "source_url");
        // Parse no remote resources; URLs are evidence references only.
        if (source == null || !source.StartsWith("https://", StringComparison.Ordinal) || source.Length <= 8
            || source[8] == '/' || source.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '@', '?', '#' }) >= 0) return false;
        string date = Text(fact, "retrieved_on");
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out DateTime retrieved) || retrieved.Date > DateTime.UtcNow.Date)
            return false;
        string status = Text(fact, "status");
        JsonElement value = fact.GetProperty("value");
        if (status == "unknown") return value.ValueKind == JsonValueKind.Null && Nonempty(Text(fact, "reason"));
        return status == "recorded" && Text(fact, "reason") != null
            && (value.ValueKind == JsonValueKind.String && Nonempty(value.GetString())
                || value.ValueKind == JsonValueKind.Object && value.EnumerateObject().MoveNext());
    }

    private static bool Valid(JsonElement root)
    {
        if (!Fields(root, "model", "provider_facts", "account_access", "live_verdict", "local_fallback", "cost_status")
            || Text(root, "model") != "d1-ultra" || SecretShaped(root)) return false;
        JsonElement facts = root.GetProperty("provider_facts");
        if (!Fields(facts, FactNames)) return false;
        foreach (string name in FactNames) if (!Fact(facts.GetProperty(name))) return false;
        JsonElement access = root.GetProperty("account_access");
        return Fields(access, "status", "owner", "reason")
            && (Text(access, "status") == "owner-pending" || Text(access, "status") == "not_run")
            && Text(access, "owner") == "William" && Nonempty(Text(access, "reason"))
            && Text(root, "live_verdict") == "not_run" && Nonempty(Text(root, "local_fallback"))
            && Text(root, "cost_status") == "unpriced";
    }

    private static bool Valid(string json)
    {
        try { using (JsonDocument doc = JsonDocument.Parse(json)) return Valid(doc.RootElement); }
        catch (JsonException) { return false; }
    }

    public static int Main(string[] args)
    {
        string fixture = File.ReadAllText("tests/production/fixtures/PROBE-D1U/unknown-facts.json");
        Check(Valid(args.Length == 1 && args[0] == "negative-fixture"
            ? fixture.Replace("\"local_fallback\": \"Autonomy\"", "\"local_fallback\": \"\"") : fixture),
            "SEIT-IF-D1U-P1.SchemaControl.LocalOnly");
        Check(Valid(fixture.Replace("\"status\": \"unknown\"", "\"status\": \"recorded\"")
            .Replace("\"value\": null", "\"value\": \"SYNTHETIC schema value, not a provider fact\"")),
            "SEIT-AI-13-P1.RecordedFactSchemaControl.LocalOnly");
        Check(!Valid(fixture.Replace("\"live_verdict\": \"not_run\"", "\"live_verdict\": \"PASS\"")),
            "SEIT-TB-03-N4.ConnectedSuccessWithoutOwnerEvidenceRejected");
        Check(!Valid(fixture.Replace("\"status\": \"owner-pending\"", "\"status\": \"connected\"")),
            "SEIT-AI-13-N2.AccountSuccessWithoutOwnerEvidenceRejected");
        Check(!Valid(fixture.Replace("\"source_url\": \"https://example.invalid/synthetic-only\",", "")),
            "SEIT-AI-13-P1.MissingSourceRejected.LocalOnly");
        Check(!Valid(fixture.Replace("\"retrieved_on\": \"2026-10-28\",", "")),
            "SEIT-AI-13-P1.MissingRetrievalDateRejected.LocalOnly");
        // The canary is constructed at runtime; no credential value is stored.
        string canary = "sk-" + new string('X', 32);
        Check(!Valid(fixture.Replace("\"local_fallback\": \"Autonomy\"", "\"local_fallback\": \"" + canary + "\"")),
            "SEIT-SECU-01-N1.KeyShapedValueRejected.LocalOnly");
        Check(!Valid(fixture.Replace("\"reason\": \"SYNTHETIC validator fixture; no provider lookup\"", "\"reason\": \"\"")),
            "SEIT-AI-13-P1.UnknownWithoutReasonRejected.LocalOnly");

        const string path = "docs/production/vendors/d1u.json";
        Check(File.Exists(path), "SEIT-IF-D1U-P1.VendorRecordExists.LocalOnly");
        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    Check(Valid(root), "SEIT-AI-13-P1.ProviderFactsSeparateFromAccountAccess.LocalOnly");
                    Check(!SecretShaped(root), "SEIT-SECU-01-P2.VendorRecordNoSecrets.LocalOnly");
                    Check(Text(root, "live_verdict") == "not_run", "SEIT-TB-03-N4.LiveVerdictNotRun");
                    Check(Nonempty(Text(root, "local_fallback")), "SEIT-IF-D1U-N5.LocalFallbackNamed.LocalOnly");
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("provider_facts", out JsonElement facts))
                        foreach (string name in FactNames)
                            Check(facts.ValueKind == JsonValueKind.Object && facts.TryGetProperty(name, out JsonElement fact) && Fact(fact),
                                "SEIT-AI-13-P1.SourcedFact." + name + ".LocalOnly");
                }
            }
            catch (JsonException) { Check(false, "SEIT-IF-D1U-P1.VendorRecordJsonValid.LocalOnly"); }
        }
        if (failures == 0) Console.WriteLine("PROBE-D1U ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
