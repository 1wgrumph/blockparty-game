/* RULES oracle only: issue #101, full-game-rev2-m5-1, baseline e36bf4fa.
 * Deliverable contract (offline; no web, credentials, editor or shared resource):
 * docs/production/submission-rules.json = {
 *   schema:"blockparty-submission-rules/1", mode:"read-only", synthetic:false,
 *   rules:[{id,field,source_kind:"rules"|"form",source_url,retrieved_utc,
 *           quote?,value?,typed_gap?:{type:"not_stated"|"source_unavailable",reason}}],
 *   sub_mappings:{"SUB-01":[rule ids],...,"SUB-05":[rule ids]}}.
 * Event: Meta AI Developer Hackathon, run by Cerebral Valley.
 * Each rule needs an official HTTPS cerebralvalley.ai URL (including subdomains
 * such as events.cerebralvalley.ai), or an event-linked Meta URL on meta.com
 * (including ai.meta.com), and a UTC retrieval date.
 * Devpost is allowed only for a page the event itself links; none is established
 * in the current offline evidence, so devpost.com and its subdomains are rejected.
 * Retrieval dates use YYYY-MM-DD or ISO timestamps with Z/+00:00.
 * A stated rule needs a verbatim
 * quote and value; a gap needs its type/reason and cannot assert a value.
 * Every required field must have a rule or gap: eligibility, required_source,
 * required_build, required_video, video_length_limit, deadline,
 * contribution_terms, asset_terms. Additional fields support SUB-01/05.
 * Non-gap video_length_limit.value = {max_seconds:positive number};
 * deadline.value = {at:ISO timestamp with explicit offset/Z, timezone:name}.
 * Other values are nonempty strings describing the recorded obligation.
 * Both rules and form source kinds must be represented, even if inaccessible.
 * Each SUB mapping is nonempty and resolves to at least one recorded rule.
 * evidence/production/RULES/result.json = {package:"RULES",status:"PASS",
 *   started_utc,ended_utc,active_hours:nonnegative number,synthetic:false,
 *   rules_sha256:SHA256 of the exact submission-rules.json bytes}.
 * These objects have only the documented fields. No action, enrollment,
 * submission, contact or credential fields; all strings are scanned for
 * private path, contact and credential shapes. Rules may quote obligations
 * about submission; that is not a record of performing an action.
 * No fixed deadline/duration is inferred from the plan or synthetic corpus.
 *
 * RU-1 Files/Shape/Result -> local SEIT-SUB-01-P1; IF-BUILD-N6 support.
 * RU-2 SubMapping -> SEIT-SUB-01..05, SUB-01-P1/P2 (mapping only).
 * RU-3 Source/Date/Quote/Gap -> SEIT-IF-BUILD-N6, SUB-04, SUB-01-N2.
 * RU-4 RequiredFields/Limit/Deadline -> SEIT-SUB-01..05, SUB-01-P1/P2.
 * RU-5 ReadOnly/Privacy -> SEIT-SUB-01-N1/N2/N3, IF-BUILD-N3/P1,
 *                         SEIT-TB-08-N1/N2/N3 (local evidence only).
 * Whole SEIT acceptance, source authenticity/quote fidelity, currentness,
 * public access, build/video qualification, rights and owner receipts remain
 * manual/GATE-M6 obligations; structurally valid gaps never qualify them.
 *
 * negative-fixture loads the explicitly SYNTHETIC committed corpus through
 * the same file reader/validator. Each case first passes its positive control,
 * injects one requested defect and must fail its named oracle. It also proves
 * valid typed gaps pass. Each case owns a fresh GUID temp root, removed in
 * finally even on preparation failure. No command can bless synthetic files
 * as real evidence. This mode exits 1 and never prints the green marker.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

public static class RulesChecks
{
    private const string Record = "docs/production/submission-rules.json";
    private const string Result = "evidence/production/RULES/result.json";
    private static readonly string[] Required = { "eligibility", "required_source", "required_build", "required_video", "video_length_limit", "deadline", "contribution_terms", "asset_terms" };
    private sealed class OracleFailure : Exception
    {
        public readonly string Id;
        public OracleFailure(string id) { Id = id; }
    }
    private static void Need(bool ok, string id) { if (!ok) throw new OracleFailure(id); }
    private static bool Has(JsonElement e, string key) { return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out _); }
    private static string Text(JsonElement e, string key)
    { return Has(e, key) && e.GetProperty(key).ValueKind == JsonValueKind.String ? e.GetProperty(key).GetString() : null; }
    private static bool Nonempty(string s) { return !string.IsNullOrWhiteSpace(s); }
    private static JsonElement Node(object value)
    { using (JsonDocument d = JsonDocument.Parse(JsonSerializer.Serialize(value))) return d.RootElement.Clone(); }
    private static JsonElement Read(string root, string path)
    {
        Need(File.Exists(Path.Combine(root, path)), "RU-1.MissingEvidence." + (path == Record ? "Rules" : "Result"));
        using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return d.RootElement.Clone();
    }
    private static void Shape(JsonElement e, string id, params string[] allowed)
    {
        Need(e.ValueKind == JsonValueKind.Object, id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
            Need(Array.IndexOf(allowed, p.Name) >= 0 && seen.Add(p.Name), id);
    }
    private static bool OffsetTime(string s, bool utc)
    {
        if (s == null || s.IndexOf('T') != 10) return false;
        bool suffix = s.EndsWith("Z", StringComparison.Ordinal) || s.EndsWith("+00:00", StringComparison.Ordinal);
        if (!utc && s.Length >= 6)
        {
            int i = s.Length - 6;
            suffix |= (s[i] == '+' || s[i] == '-') && s[i + 3] == ':';
        }
        return suffix && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
    private static bool Retrieval(string s)
    { return OffsetTime(s, true) || DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _); }
    private static bool Source(string s, bool synthetic)
    {
        if (s == null || !s.StartsWith("https://", StringComparison.Ordinal)) return false;
        int end = s.IndexOf('/', 8);
        if (end < 0) end = s.Length;
        string host = s.Substring(8, end - 8).ToLowerInvariant();
        foreach (char c in host) if (!char.IsLetterOrDigit(c) && c != '.' && c != '-') return false;
        foreach (char c in s) if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        return host == "cerebralvalley.ai" || host.EndsWith(".cerebralvalley.ai", StringComparison.Ordinal)
            || host == "meta.com" || host.EndsWith(".meta.com", StringComparison.Ordinal)
            || synthetic && host == "rules.example.invalid";
    }
    private static bool PrivateText(string s)
    {
        string lower = s.ToLowerInvariant().Replace('\\', '/');
        foreach (string marker in new[] { "/home/", "/users/", "/root/", "file://", "mailto:", "tel:", "bearer ", "api_key=", "api_key:", "access_token=", "password=" })
            if (lower.Contains(marker)) return true;
        for (int i = 0; i < lower.Length; i++)
        {
            if (i + 2 < lower.Length && (i == 0 || !char.IsLetterOrDigit(lower[i - 1])) && char.IsLetter(lower[i]) && lower[i + 1] == ':' && lower[i + 2] == '/') return true;
            if ((i == 0 || !char.IsLetterOrDigit(lower[i - 1])) && lower.Substring(i).StartsWith("sk-", StringComparison.Ordinal)) return true;
            if (lower[i] == '@')
            {
                int end = i + 1;
                while (end < lower.Length && (char.IsLetterOrDigit(lower[end]) || lower[end] == '-' || lower[end] == '.')) end++;
                if (i > 0 && lower.Substring(i + 1, end - i - 1).TrimEnd('.').Contains(".")) return true;
            }
            if (!char.IsDigit(lower[i]) || i > 0 && char.IsLetterOrDigit(lower[i - 1])) continue;
            int digits = 0, last = i;
            while (last < lower.Length && (char.IsDigit(lower[last]) || " +-().".IndexOf(lower[last]) >= 0))
            { if (char.IsDigit(lower[last])) digits++; last++; }
            // ISO calendar dates are not phone numbers, including a following time.
            bool date = i + 10 <= lower.Length && DateTime.TryParseExact(lower.Substring(i, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if (digits >= 10 && !date && (last == lower.Length || !char.IsLetterOrDigit(lower[last]))) return true;
            i = last - 1;
        }
        return lower.StartsWith("//", StringComparison.Ordinal);
    }
    private static void Privacy(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) Need(!PrivateText(e.GetString()), "RU-5.PrivateData");
        if (e.ValueKind == JsonValueKind.Array) foreach (JsonElement v in e.EnumerateArray()) Privacy(v);
        if (e.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in e.EnumerateObject())
            {
                string key = p.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
                foreach (string banned in new[] { "action", "actions", "submission", "submitted", "enrollment", "enrolled", "credential", "credentials", "password", "apikey", "accesstoken", "authorization", "cookie", "email", "phone", "contact" })
                    Need(key != banned, "RU-5.ForbiddenField");
                Need(!PrivateText(p.Name), "RU-5.PrivateData"); Privacy(p.Value);
            }
    }
    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
    }
    private static void Validate(string root, bool synthetic = false)
    {
        JsonElement record = Read(root, Record), result = Read(root, Result);
        Privacy(record); Privacy(result);
        Shape(record, "RU-1.RecordShape", "schema", "mode", "synthetic", "rules", "sub_mappings");
        Shape(result, "RU-1.ResultShape", "package", "status", "started_utc", "ended_utc", "active_hours", "synthetic", "rules_sha256");
        Need(Text(record, "schema") == "blockparty-submission-rules/1", "RU-1.Schema");
        Need(Text(record, "mode") == "read-only", "RU-5.ReadOnly");
        foreach (JsonElement e in new[] { record, result })
            Need(Has(e, "synthetic") && e.GetProperty("synthetic").ValueKind == (synthetic ? JsonValueKind.True : JsonValueKind.False), "RU-1.SyntheticEvidence");
        Need(Text(result, "package") == "RULES" && Text(result, "status") == "PASS", "RU-1.ResultStatus");
        Need(OffsetTime(Text(result, "started_utc"), true) && OffsetTime(Text(result, "ended_utc"), true), "RU-1.ResultDates");
        Need(DateTimeOffset.Parse(Text(result, "ended_utc"), CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(Text(result, "started_utc"), CultureInfo.InvariantCulture), "RU-1.ResultDates");
        Need(Has(result, "active_hours") && result.GetProperty("active_hours").ValueKind == JsonValueKind.Number
            && result.GetProperty("active_hours").TryGetDouble(out double hours) && hours >= 0 && !double.IsInfinity(hours), "RU-1.ActiveHours");
        Need(Text(result, "rules_sha256") == Hash(Path.Combine(root, Record)), "RU-1.RulesHash");
        Need(Has(record, "rules") && record.GetProperty("rules").ValueKind == JsonValueKind.Array, "RU-1.RulesShape");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement rule in record.GetProperty("rules").EnumerateArray())
        {
            Shape(rule, "RU-3.RuleShape", "id", "field", "source_kind", "source_url", "retrieved_utc", "quote", "value", "typed_gap");
            Need(Nonempty(Text(rule, "id")) && ids.Add(Text(rule, "id")), "RU-3.RuleId");
            Need(Nonempty(Text(rule, "field")), "RU-3.Field"); fields.Add(Text(rule, "field"));
            string kind = Text(rule, "source_kind");
            Need(kind == "rules" || kind == "form", "RU-3.SourceKind"); sources.Add(kind);
            Need(Source(Text(rule, "source_url"), synthetic), "RU-3.SourceUrl");
            Need(Retrieval(Text(rule, "retrieved_utc")), "RU-3.RetrievedUtc");
            if (Has(rule, "typed_gap"))
            {
                JsonElement gap = rule.GetProperty("typed_gap");
                Shape(gap, "RU-3.TypedGap", "type", "reason");
                Need((Text(gap, "type") == "not_stated" || Text(gap, "type") == "source_unavailable") && Nonempty(Text(gap, "reason")) && !Has(rule, "value"), "RU-3.TypedGap");
                Need(!Has(rule, "quote") || Nonempty(Text(rule, "quote")), "RU-3.Quote");
                continue;
            }
            Need(Nonempty(Text(rule, "quote")), "RU-3.Quote");
            Need(Has(rule, "value"), "RU-4.ValueOrGap");
            JsonElement value = rule.GetProperty("value");
            if (Text(rule, "field") == "deadline")
            {
                Shape(value, "RU-4.DeadlineTimezone", "at", "timezone");
                Need(OffsetTime(Text(value, "at"), false) && Nonempty(Text(value, "timezone")), "RU-4.DeadlineTimezone");
            }
            else if (Text(rule, "field") == "video_length_limit")
            {
                Shape(value, "RU-4.VideoLimit", "max_seconds");
                Need(Has(value, "max_seconds") && value.GetProperty("max_seconds").ValueKind == JsonValueKind.Number
                    && value.GetProperty("max_seconds").TryGetDouble(out double seconds) && seconds > 0 && !double.IsInfinity(seconds), "RU-4.VideoLimit");
            }
            else Need(value.ValueKind == JsonValueKind.String && Nonempty(value.GetString()), "RU-4.ValueOrGap");
        }
        foreach (string field in Required) Need(fields.Contains(field), "RU-4.RequiredField." + field);
        Need(sources.Contains("rules") && sources.Contains("form"), "RU-3.RulesAndForm");
        Need(Has(record, "sub_mappings"), "RU-2.SubMapping");
        JsonElement mappings = record.GetProperty("sub_mappings");
        Shape(mappings, "RU-2.SubMapping", "SUB-01", "SUB-02", "SUB-03", "SUB-04", "SUB-05");
        for (int n = 1; n <= 5; n++)
        {
            string key = "SUB-0" + n;
            Need(Has(mappings, key) && mappings.GetProperty(key).ValueKind == JsonValueKind.Array && mappings.GetProperty(key).GetArrayLength() > 0, "RU-2.SubMapping");
            foreach (JsonElement id in mappings.GetProperty(key).EnumerateArray())
                Need(id.ValueKind == JsonValueKind.String && ids.Contains(id.GetString()), "RU-2.SubMapping");
        }
    }
    private static void Edit(string root, string path, Action<Dictionary<string, JsonElement>> edit)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(root, path)));
        edit(data); File.WriteAllText(Path.Combine(root, path), JsonSerializer.Serialize(data));
    }
    private static void EditRule(string root, string field, Action<Dictionary<string, JsonElement>> edit)
    {
        Edit(root, Record, data => {
            var rules = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(data["rules"].GetRawText());
            foreach (var rule in rules) if (rule["field"].GetString() == field) edit(rule);
            data["rules"] = Node(rules);
        });
    }
    private static void Bind(string root)
    { Edit(root, Result, data => data["rules_sha256"] = Node(Hash(Path.Combine(root, Record)))); }
    private static int Negatives()
    {
        var cases = new Dictionary<string, string> {
            {"missing-sub-mapping", "RU-2.SubMapping"}, {"undated-rule", "RU-3.RetrievedUtc"},
            {"missing-source-url", "RU-3.SourceUrl"}, {"absent-field-without-gap", "RU-4.ValueOrGap"},
            {"missing-required-field", "RU-4.RequiredField.eligibility"}, {"submission-action", "RU-5.ForbiddenField"},
            {"enrollment-action", "RU-5.ForbiddenField"}, {"empty-quote", "RU-3.Quote"},
            {"unofficial-source", "RU-3.SourceUrl"}, {"deadline-without-timezone", "RU-4.DeadlineTimezone"},
            {"wrong-event-source", "RU-3.SourceUrl"}, {"unlinked-devpost-source", "RU-3.SourceUrl"},
            {"invalid-video-limit", "RU-4.VideoLimit"}, {"untyped-gap", "RU-3.TypedGap"},
            {"dangling-sub-mapping", "RU-2.SubMapping"}, {"private-path", "RU-5.PrivateData"},
            {"personal-email", "RU-5.PrivateData"}, {"personal-phone", "RU-5.PrivateData"},
            {"credential-field", "RU-5.ForbiddenField"}, {"credential-text", "RU-5.PrivateData"},
            {"synthetic-as-live", "RU-1.SyntheticEvidence"}, {"missing-result", "RU-1.MissingEvidence.Result"},
            {"stale-result-hash", "RU-1.RulesHash"}
        };
        foreach (var test in cases)
        {
            string root = Path.Combine(Path.GetTempPath(), "blockparty-rules-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string path in new[] { Record, Result })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path)));
                    File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "tests/production/fixtures/RULES/positive", path), Path.Combine(root, path));
                }
                Validate(root, true);
                // Each required field independently accepts either documented gap type.
                foreach (string field in Required)
                {
                    EditRule(root, field, rule => { rule.Remove("value"); rule.Remove("quote"); rule["typed_gap"] = Node(new { type = "not_stated", reason = "SYNTHETIC source does not state this field." }); });
                }
                Bind(root); Validate(root, true);
                EditRule(root, "eligibility", rule => rule["typed_gap"] = Node(new { type = "source_unavailable", reason = "SYNTHETIC source inaccessible without sign-in; no sign-in attempted." }));
                Bind(root); Validate(root, true);
                File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "tests/production/fixtures/RULES/positive", Record), Path.Combine(root, Record), true);
                Bind(root); Validate(root, true);
                switch (test.Key)
                {
                    case "missing-sub-mapping":
                    case "dangling-sub-mapping":
                        Edit(root, Record, data => {
                            var mappings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data["sub_mappings"].GetRawText());
                            if (test.Key == "missing-sub-mapping") mappings.Remove("SUB-05");
                            else mappings["SUB-05"] = Node(new[] { "SYNTHETIC-missing-rule" });
                            data["sub_mappings"] = Node(mappings);
                        }); break;
                    case "undated-rule": EditRule(root, "eligibility", rule => rule.Remove("retrieved_utc")); break;
                    case "missing-source-url": EditRule(root, "eligibility", rule => rule.Remove("source_url")); break;
                    case "absent-field-without-gap": EditRule(root, "eligibility", rule => rule.Remove("value")); break;
                    case "missing-required-field": EditRule(root, "eligibility", rule => rule["field"] = Node("SYNTHETIC-unrelated")); break;
                    case "submission-action": Edit(root, Result, data => data["submission"] = Node(new { action = "submit", status = "SYNTHETIC-performed" })); break;
                    case "enrollment-action": Edit(root, Record, data => data["enrollment"] = Node(new { action = "enroll", status = "SYNTHETIC-performed" })); break;
                    case "empty-quote": EditRule(root, "eligibility", rule => rule["quote"] = Node(" ")); break;
                    case "unofficial-source": EditRule(root, "eligibility", rule => rule["source_url"] = Node("https://unofficial.example.invalid/rules")); break;
                    case "wrong-event-source": EditRule(root, "eligibility", rule => rule["source_url"] = Node("https://example.invalid/other-event")); break;
                    case "unlinked-devpost-source": EditRule(root, "eligibility", rule => rule["source_url"] = Node("https://example.invalid/other-event")); break;
                    case "deadline-without-timezone": EditRule(root, "deadline", rule => rule["value"] = Node(new { at = "2026-11-04T21:00:00", timezone = "" })); break;
                    case "invalid-video-limit": EditRule(root, "video_length_limit", rule => rule["value"] = Node(new { max_seconds = 0 })); break;
                    case "untyped-gap": EditRule(root, "eligibility", rule => { rule.Remove("value"); rule["typed_gap"] = Node(new { reason = "SYNTHETIC missing gap type" }); }); break;
                    case "private-path": EditRule(root, "eligibility", rule => rule["quote"] = Node("SYNTHETIC /home/example-user/private.txt")); break;
                    case "personal-email": EditRule(root, "eligibility", rule => rule["value"] = Node("SYNTHETIC person@example.invalid")); break;
                    case "personal-phone": EditRule(root, "eligibility", rule => rule["value"] = Node("SYNTHETIC +1 (202) 555-0100")); break;
                    case "credential-field": Edit(root, Result, data => data["credentials"] = Node("SYNTHETIC-NOT-A-CREDENTIAL")); break;
                    case "credential-text": EditRule(root, "eligibility", rule => rule["quote"] = Node("SYNTHETIC Bearer NOT-A-CREDENTIAL")); break;
                    case "missing-result": File.Delete(Path.Combine(root, Result)); break;
                    case "stale-result-hash": EditRule(root, "eligibility", rule => rule["quote"] = Node("SYNTHETIC altered quote")); break;
                }
                if (test.Key != "missing-result" && test.Key != "stale-result-hash") Bind(root);
                string actual = null;
                try { Validate(root, test.Key != "synthetic-as-live"); } catch (OracleFailure failure) { actual = failure.Id; }
                Need(actual == test.Value, "RU-Fixture.TargetedRejection." + test.Key + ".Actual." + (actual ?? "NONE"));
                Console.WriteLine("FAIL negative-fixture (" + test.Key + ") " + actual + " [expected SYNTHETIC rejection]");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine("PASS all SYNTHETIC controls, typed gaps and targeted negative rejections");
        return 1;
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "negative-fixture") return Negatives();
            Need(args.Length == 0, "RU.Arguments");
            Validate(Directory.GetCurrentDirectory());
            Console.WriteLine("RULES ALL PASS"); return 0;
        }
        catch (OracleFailure failure) { Console.WriteLine("FAIL " + failure.Id); return 1; }
        // Evidence/exception text may contain private data; report only the type.
        catch (Exception failure) { Console.WriteLine("FAIL RU.EvidenceOrHarness." + failure.GetType().Name); return 1; }
    }
}
