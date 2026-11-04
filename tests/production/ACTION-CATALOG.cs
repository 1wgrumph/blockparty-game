// ACTION-CATALOG: declarative flow coverage, not animation/readability acceptance.
// Resource group isolated-csharp; no engine, display, service or shared temp files.
// Contract (C1..C7, m4/test-engineer.json action_catalog_coverage):
// root {readability_catalog:{sha256,ids:[check id]}, activities:[], commands:[],
//       life_stages:[], reactions:[]}. Every row has id and the common flow below.
// activities: id=ACT code, min_stage/max_stage=LifeStage names.
// commands: id=CommandKind name, movement="movement"|"none", reason required
// for none; DirectActivity and TravelTo must be movement. Nonmovement commands
// are the only rows exempt from flow fields (C3).
// life_stages: id=LifeStage name, presentations:["carried","crib","held"] for
// Newborn/Infant (no self locomotion); Toddler includes "walk".
// reactions: id="Neutral" or a real FeelingKind name (7-emotion mapping).
// Common row: flow:{approach,start,loop,finish,interruption}, each phase
// {clips:[nonempty id],transition:nonempty description,self_locomotion:bool};
// props:[prop id] (empty explicitly means no prop),
// interaction_point:{slot_id,approach_pose} OR {slot_id:"none",reason};
// cues:{posture,gaze,icon OR caption}; readability_check_ids:[check id].
// approach_pose is a named pose, not a measured spatial-error claim.
// Clip/prop/slot ids are planned references; existence/playback belongs to the
// successor ANIM/PROP/CONTENT packages. Header ids are checked against the
// recorded checks.yaml corpus below; the maintainer confirms the pinned source.
// C1/C2: BP-ACT-01, action-flow SEIT amendment G-1 pending. C3/C4/C6/C7 and
// C5 cues: local support for SEIT-PRES-02-P1/N1, not human evaluation PASS.
// C5 approach_pose: local support for SEIT-PRES-01-P1, not <=0.15 m proof.
// negative-fixture corrupts copies of the real catalog (missing/duplicate ACT,
// empty phase); each must be rejected, then emits FAIL and exits 1.
// Review repair adds semantic declarations without duplicating ACT rows:
// eligibility_exceptions:[{stage,max_career_level}]; variants:[{id,stage,...flow
// fields}] for stage-specific performance, or {stage,participation,effect_multiplier}
// for a qualification. Shopping approach/finish phases name their destination.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

public static class ActionCatalogChecks
{
    private const string CatalogPath = "Assets/Blockparty/Content/action-catalog.json";
    private const string CheckHash = "d2bf09e9814ee2c9cf248d8f45d74a28ef440de6c003186f5cc8848a507d270e";
    // Recorded from checks.yaml at CheckHash; 59 ids, no catalogue thresholds invented.
    private const string CheckIds = "CHR-GEO-01 CHR-GEO-02 CHR-GEO-03 CHR-GEO-04 CHR-GEO-05 CHR-GEO-06 CHR-SHP-01 CHR-SHP-02 CHR-SHP-03 CHR-SKN-01 CHR-SKN-02 CHR-SKN-03 CHR-BODY-01 CHR-CLO-01 CHR-FAC-01 CHR-FAC-04 CHR-FAC-02 CHR-FAC-03 EMO-01 EMO-02 EMO-03 EMO-04 EMO-05 EMO-06 MAT-01 MAT-02 MAT-03 MAT-04 MAT-05 SCN-01 SCN-02 SCN-03 SCN-04 COMP-01 COMP-02 ACC-01 ACC-02 ACC-03 ANM-01 ANM-02 ANM-03 ANM-04 ANM-05 ANM-06 PRF-01 PRF-02 PRF-03 PRF-04 PRF-05 PRF-06 PRF-07 SIM-01 SIM-02 STY-01 CAP-01 LIC-01 LIC-02 LOOK-01 LOOK-02";
    private static readonly string[] Phases = { "approach", "start", "loop", "finish", "interruption" };
    private static readonly Dictionary<string, ActivityRule> Rules = new Dictionary<string, ActivityRule>(StringComparer.Ordinal);
    private static HashSet<string> sdocCodes;
    private static string sdoc;

    private static void Need(bool ok, string id, string detail)
    {
        if (!ok) throw new InvalidDataException(id + " " + detail);
    }

    private static string Text(JsonElement obj, string key)
    {
        Need(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out _), "Shape", "missing " + key);
        JsonElement value = obj.GetProperty(key);
        Need(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), "Shape", "empty/non-string " + key);
        return value.GetString();
    }

    private static JsonElement Array(JsonElement obj, string key)
    {
        Need(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out _), "Shape", "missing " + key);
        JsonElement value = obj.GetProperty(key);
        Need(value.ValueKind == JsonValueKind.Array, "Shape", key + " must be array");
        return value;
    }

    private static HashSet<string> Strings(JsonElement obj, string key, bool nonempty)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in Array(obj, key).EnumerateArray())
        {
            Need(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), "Shape", "invalid " + key + " member");
            Need(values.Add(value.GetString()), "Shape", "duplicate " + key + " member " + value.GetString());
        }
        // C5/C6 choose one as the minimum nonempty list, not a production count.
        Need(!nonempty || values.Count > 0, "Shape", "empty " + key);
        return values;
    }

    private static Dictionary<string, JsonElement> Rows(JsonElement root, string key, HashSet<string> expected, string check)
    {
        var rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement row in Array(root, key).EnumerateArray())
        {
            string id = Text(row, "id");
            Need(!rows.ContainsKey(id), check, "duplicate " + key + " " + id);
            rows.Add(id, row);
        }
        Need(expected.SetEquals(rows.Keys), check, key + " coverage differs; expected " + string.Join(",", expected));
        return rows;
    }

    private static HashSet<string> Names(Type type)
    {
        return new HashSet<string>(Enum.GetNames(type), StringComparer.Ordinal);
    }

    private static void Flow(JsonElement row, HashSet<string> checks, bool noLocomotion)
    {
        string id = Text(row, "id");
        JsonElement flow = row.GetProperty("flow");
        foreach (string phase in Phases)
        {
            JsonElement node = flow.GetProperty(phase);
            Strings(node, "clips", true);
            Text(node, "transition");
            JsonElement locomotion = node.GetProperty("self_locomotion");
            Need(locomotion.ValueKind == JsonValueKind.True || locomotion.ValueKind == JsonValueKind.False, "C5", id + " " + phase + " needs self_locomotion bool");
            Need(!noLocomotion || locomotion.ValueKind == JsonValueKind.False, "C7", id + " " + phase + " dependent self locomotion");
        }
        Strings(row, "props", false);
        JsonElement point = row.GetProperty("interaction_point");
        if (Text(point, "slot_id") == "none") Text(point, "reason");
        else Text(point, "approach_pose");
        JsonElement cues = row.GetProperty("cues");
        Text(cues, "posture"); Text(cues, "gaze");
        // BP-PRES-02 requires a non-animation icon or text cue without audio/colour.
        if (cues.TryGetProperty("icon", out _)) Text(cues, "icon");
        else Text(cues, "caption");
        Need(Strings(row, "readability_check_ids", true).IsSubsetOf(checks), "C6", id + " unknown readability check");
    }

    private static void Dependencies()
    {
        // The SDoc is the text/configuration contract, not a source-string runtime proxy.
        sdoc = File.ReadAllText("migrations/blockparty/game/blockparty-game-system.sdoc");
        sdocCodes = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sdoc.Length; i++)
        {
            if (i + 4 > sdoc.Length || sdoc.Substring(i, 4) != "ACT-") continue;
            int end = i + 4;
            while (end < sdoc.Length && sdoc[end] >= 'A' && sdoc[end] <= 'Z') end++;
            if (end > i + 4 && end + 2 < sdoc.Length && sdoc[end] == '-'
                && char.IsDigit(sdoc[end + 1]) && char.IsDigit(sdoc[end + 2]))
                sdocCodes.Add(sdoc.Substring(i, end + 3 - i));
        }
        Need(sdocCodes.Count > 0, "C1", "SDoc ACT corpus empty");
        // Read the landed tables without maintaining a second copy of their stage bounds.
        var catalog = (ActivityRule[])typeof(CommandArbitrator).GetField("Catalog", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        foreach (ActivityRule rule in catalog)
        {
            Need(!Rules.ContainsKey(rule.Code), "C2", "duplicate ActivityRule " + rule.Code);
            Rules.Add(rule.Code, rule);
        }
        Need(sdocCodes.SetEquals(Rules.Keys), "C2", "SDoc/ActivityRule sets differ");
        var durations = (System.Array)typeof(ActivityExecutor).GetField("Durations", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var durationCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (object duration in durations)
        {
            string code = (string)duration.GetType().GetField("Code").GetValue(duration);
            Need(durationCodes.Add(code), "C2", "duplicate DurationRule " + code);
        }
        Need(sdocCodes.SetEquals(durationCodes), "C2", "SDoc/DurationRule sets differ");
        var available = new HashSet<string>(StringComparer.Ordinal);
        long commandId = 0;
        foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
        {
            var world = WorldState.CreateSeeded(7L, 42L, 0, null);
            var home = world.AllocateHome(StateWriter.SimCore, 8);
            var household = world.AllocateHousehold(StateWriter.SimCore, "Catalog", home.Id);
            var resident = world.AllocateResident(StateWriter.SimCore, "Catalog", stage, household.Id, home.Id);
            var arbitrator = new CommandArbitrator(world, new Clock(world));
            foreach (AvailableAction action in arbitrator.Snapshot(resident.Id).Available)
            {
                available.Add(action.Activity.Code);
                var result = arbitrator.Submit(new CommandEnvelope((CommandId)(++commandId), world.Epoch,
                    world.GameSeconds, CommandSource.PlayerHuman, resident.ResidentSequence + 1u,
                    CommandKind.DirectActivity, resident.Id, action.Activity, null,
                    new CommandParameters.Direct(QueueMode.Immediate)));
                Need(result.Accepted || result.Reason != RejectReason.UnknownActivity, "C2", action.Activity.Code + " UnknownActivity at " + stage);
            }
        }
        Need(sdocCodes.SetEquals(available), "C2", "eight-stage Snapshot union differs from SDoc");
        Console.WriteLine("PASS C2 SDoc/ActivityRule/DurationRule/Snapshot/DirectActivity agree (" + sdocCodes.Count + " codes)");
    }

    private static void Validate(JsonElement root)
    {
        JsonElement header = root.GetProperty("readability_catalog");
        Need(Text(header, "sha256") == CheckHash, "C6", "checks.yaml hash differs from recorded corpus");
        var checks = Strings(header, "ids", true);
        Need(checks.SetEquals(CheckIds.Split(' ')), "C6", "header check ids differ from recorded corpus");
        foreach (var pair in Rows(root, "activities", sdocCodes, "C1"))
        {
            ActivityRule rule = Rules[pair.Key];
            JsonElement row = pair.Value;
            Need(Text(row, "min_stage") == rule.MinStage.ToString() && Text(row, "max_stage") == rule.MaxStage.ToString(), "C7", pair.Key + " stage range differs from ActivityRule");
            Flow(row, checks, rule.MinStage <= LifeStage.Infant);
        }
        foreach (var pair in Rows(root, "commands", Names(typeof(CommandKind)), "C3"))
        {
            string movement = Text(pair.Value, "movement");
            Need(movement == "movement" || movement == "none", "C3", pair.Key + " invalid movement classification");
            Need((pair.Key != "TravelTo" && pair.Key != "DirectActivity") || movement == "movement", "C3", pair.Key + " must name movement flow");
            if (movement == "none") Text(pair.Value, "reason");
            else Flow(pair.Value, checks, false);
            if (pair.Key == "TravelTo")
                Need(pair.Value.GetProperty("flow").GetProperty("loop").GetProperty("self_locomotion").ValueKind == JsonValueKind.True, "C3", "TravelTo requires locomotion loop");
        }
        foreach (var pair in Rows(root, "life_stages", Names(typeof(LifeStage)), "C4"))
        {
            bool dependent = pair.Key == "Newborn" || pair.Key == "Infant";
            var presentations = Strings(pair.Value, "presentations", true);
            if (dependent) Need(presentations.SetEquals(new[] { "carried", "crib", "held" }), "C4", pair.Key + " needs carried/crib/held presentations");
            if (pair.Key == "Toddler") Need(presentations.Contains("walk"), "C4", "Toddler walk missing");
            Flow(pair.Value, checks, dependent);
        }
        var feelings = Names(typeof(FeelingKind)); feelings.Add("Neutral");
        foreach (var pair in Rows(root, "reactions", feelings, "C4")) Flow(pair.Value, checks, false);
    }

    private static void RejectCopy(JsonElement root, string mutation)
    {
        // Mutate a real producer catalog in memory; never edit the deliverable.
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name != "activities") { property.Value.WriteTo(writer); continue; }
                    writer.WriteStartArray(); int index = 0;
                    foreach (JsonElement row in property.Value.EnumerateArray())
                    {
                        if (index++ == 0)
                        {
                            if (mutation == "missing") continue;
                            if (mutation == "duplicate") row.WriteTo(writer);
                            if (mutation == "empty-phase")
                            {
                                writer.WriteStartObject();
                                foreach (JsonProperty field in row.EnumerateObject())
                                {
                                    writer.WritePropertyName(field.Name);
                                    if (field.Name != "flow") { field.Value.WriteTo(writer); continue; }
                                    writer.WriteStartObject();
                                    foreach (JsonProperty phase in field.Value.EnumerateObject())
                                    {
                                        writer.WritePropertyName(phase.Name);
                                        if (phase.Name == "loop") writer.WriteStringValue("");
                                        else phase.Value.WriteTo(writer);
                                    }
                                    writer.WriteEndObject();
                                }
                                writer.WriteEndObject(); continue;
                            }
                        }
                        row.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            bool rejected = false;
            using (var copy = JsonDocument.Parse(stream.ToArray()))
            {
                try { Validate(copy.RootElement); }
                catch (InvalidDataException) { rejected = true; }
                catch (InvalidOperationException) { rejected = true; }
                catch (KeyNotFoundException) { rejected = true; }
            }
            Need(rejected, "Negative." + mutation, "corrupted catalog accepted");
            Console.WriteLine("PASS Negative." + mutation + " rejected");
        }
    }

    private static string SdocActivity(string code)
    {
        int start = sdoc.IndexOf(code + " ", StringComparison.Ordinal);
        Need(start >= 0, "Source", "SDoc activity missing: " + code);
        int end = sdoc.IndexOf(". ACT-", start, StringComparison.Ordinal);
        Need(end > start, "Source", "SDoc activity boundary missing: " + code);
        return sdoc.Substring(start, end + 1 - start);
    }

    private static string Between(string text, string before, string after)
    {
        int start = text.IndexOf(before, StringComparison.Ordinal);
        Need(start >= 0, "Source", "SDoc clause missing: " + before);
        start += before.Length;
        int end = text.IndexOf(after, start, StringComparison.Ordinal);
        Need(end > start, "Source", "SDoc clause boundary missing: " + after);
        return text.Substring(start, end - start);
    }

    private static JsonElement ChildVariant(JsonElement row)
    {
        Need(row.TryGetProperty("variants", out _), "Catalog", Text(row, "id") + " missing Child variant/qualification");
        foreach (JsonElement variant in Array(row, "variants").EnumerateArray())
            if (Text(variant, "stage") == LifeStage.Child.ToString()) return variant;
        throw new InvalidDataException(Text(row, "id") + " has no Child variant/qualification");
    }

    private static void ReviewCheck(string id, Action check, ref int failures)
    {
        try { check(); Console.WriteLine("PASS " + id); }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine("FAIL " + id + " " + error.Message);
        }
    }

    private static int ReviewChecks(JsonElement root)
    {
        var activities = Rows(root, "activities", sdocCodes, "C1");
        var commands = Rows(root, "commands", Names(typeof(CommandKind)), "C3");
        var checks = Strings(root.GetProperty("readability_catalog"), "ids", true);
        int failures = 0;
        ReviewCheck("Review.FollowResidentPresentation", () =>
        {
            var world = WorldState.CreateSeeded(7L, 42L, 0, null);
            var home = world.AllocateHome(StateWriter.SimCore, 8);
            var household = world.AllocateHousehold(StateWriter.SimCore, "Follow", home.Id);
            var resident = world.AllocateResident(StateWriter.SimCore, "Follow", LifeStage.Adult, household.Id, home.Id);
            var clock = new Clock(world);
            var arbitrator = new CommandArbitrator(world, clock);
            var executor = new ActivityExecutor(world, arbitrator, clock);
            var navigation = new WorldNavigation(world, arbitrator, executor);
            var result = arbitrator.Submit(new CommandEnvelope((CommandId)1L, world.Epoch,
                world.GameSeconds, CommandSource.PlayerHuman, 1u, CommandKind.FollowResident,
                resident.Id, null, null, CommandParameters.None.Instance));
            Need(result.Accepted, "Source", "FollowResident without envelope target must be accepted");
            var targeted = arbitrator.Submit(new CommandEnvelope((CommandId)2L, world.Epoch,
                world.GameSeconds, CommandSource.PlayerHuman, 2u, CommandKind.FollowResident,
                resident.Id, null, (long)resident.Id, CommandParameters.None.Instance));
            Need(!targeted.Accepted && targeted.Reason == RejectReason.InvalidParameters,
                "Source", "FollowResident must reject an envelope target");
            int before = world.ComputeStateHash();
            Need(navigation.FollowResident(resident.Id).Accepted && navigation.FollowedResident == resident.Id
                && world.ComputeStateHash() == before && arbitrator.GetCurrent(resident.Id) == null
                && arbitrator.GetPending(resident.Id).Length == 0, "Source", "follow must change presentation only");
            JsonElement row = commands[CommandKind.FollowResident.ToString()];
            Need(Text(row, "movement") == "none", "Catalog", "runtime follows with the camera, accepts no target and queues no resident travel");
            Text(row, "reason");
            if (row.TryGetProperty("flow", out JsonElement flow))
                foreach (string phase in Phases)
                    Need(flow.GetProperty(phase).GetProperty("self_locomotion").ValueKind == JsonValueKind.False,
                        "Catalog", "camera follow cannot declare resident locomotion");
            Need(!row.TryGetProperty("interaction_point", out _), "Catalog", "camera follow has no resident interaction point/envelope target");
        }, ref failures);
        ReviewCheck("Review.BaristaEffectiveEligibility", () =>
        {
            JsonElement row = activities["ACT-WORK-01"];
            var mismatches = new List<string>();
            foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
            for (int level = 0; level <= StateSchema.CareerLevelMax; level++)
            {
                var world = WorldState.CreateSeeded(7L, 42L, 0, null);
                var home = world.AllocateHome(StateWriter.SimCore, 8);
                var household = world.AllocateHousehold(StateWriter.SimCore, "Eligibility", home.Id);
                var resident = world.AllocateResident(StateWriter.SimCore, "Eligibility", stage, household.Id, home.Id);
                world.SetCareer(StateWriter.SimCore, resident.Id, "Barista", level, 0);
                var arbitrator = new CommandArbitrator(world, new Clock(world));
                bool offered = false;
                foreach (AvailableAction action in arbitrator.Snapshot(resident.Id).Available)
                    if (action.Activity.Code == Text(row, "id")) offered = true;
                var result = arbitrator.Submit(new CommandEnvelope((CommandId)1L, world.Epoch,
                    world.GameSeconds, CommandSource.PlayerHuman, 1u, CommandKind.DirectActivity,
                    resident.Id, new ActivityId(Text(row, "id")), null, new CommandParameters.Direct(QueueMode.Immediate)));
                Need(result.Accepted == offered && (offered || result.Reason == RejectReason.StageIneligible),
                    "Source", "Barista Snapshot/Submit eligibility disagree");
                bool declared = stage >= Enum.Parse<LifeStage>(Text(row, "min_stage"))
                    && stage <= Enum.Parse<LifeStage>(Text(row, "max_stage"));
                if (row.TryGetProperty("eligibility_exceptions", out _))
                    foreach (JsonElement exception in Array(row, "eligibility_exceptions").EnumerateArray())
                        if (Text(exception, "stage") == stage.ToString()
                            && level <= exception.GetProperty("max_career_level").GetInt32()) declared = true;
                if (declared != offered) mismatches.Add(stage + " career=" + level + " runtime=" + offered + " catalog=" + declared);
            }
            Need(mismatches.Count == 0, "Catalog", string.Join("; ", mismatches));
        }, ref failures);
        ReviewCheck("Review.ChildPlayOutsideVariant", () =>
        {
            string source = SdocActivity("ACT-SPORT-01");
            string variantName = Between(source, "child as ", " variant");
            JsonElement row = activities["ACT-SPORT-01"];
            JsonElement child = ChildVariant(row);
            Need(Text(child, "id") == variantName, "Catalog", "SDoc child performance is " + variantName);
            Flow(child, checks, false);
            string ordinaryName = Between(source, "ACT-SPORT-01 ", ":");
            Need(Text(row.GetProperty("flow").GetProperty("loop"), "transition").Contains(ordinaryName)
                && Text(child.GetProperty("flow").GetProperty("loop"), "transition").Contains(variantName),
                "Catalog", "Child " + variantName + " must have its own flow alongside teen+ " + ordinaryName);
        }, ref failures);
        ReviewCheck("Review.StockSuppliesShoppingFlow", () =>
        {
            string source = SdocActivity("ACT-HOME-03");
            string shoppingCode = Between(source, "same as ", " but for ");
            string shopping = SdocActivity(shoppingCode);
            string destination = Between(shopping, "destination ", " counter slot");
            string returnTo = Between(shopping, "adds stock to ", " on return");
            JsonElement row = activities["ACT-HOME-03"];
            JsonElement approach = row.GetProperty("flow").GetProperty("approach");
            JsonElement finish = row.GetProperty("flow").GetProperty("finish");
            string slot = Text(row.GetProperty("interaction_point"), "slot_id");
            Need(slot != "none" && slot == Text(activities[shoppingCode].GetProperty("interaction_point"), "slot_id")
                && approach.TryGetProperty("destination", out JsonElement outward) && outward.GetString() == destination
                && finish.TryGetProperty("destination", out JsonElement inward) && inward.GetString() == returnTo
                && approach.GetProperty("self_locomotion").ValueKind == JsonValueKind.True
                && finish.GetProperty("self_locomotion").ValueKind == JsonValueKind.True,
                "Catalog", "SDoc same-as " + shoppingCode + " requires " + destination + " counter interaction and travel back to " + returnTo);
            Text(row.GetProperty("interaction_point"), "approach_pose");
        }, ref failures);
        ReviewCheck("Review.ChildCleanHomeAssistance", () =>
        {
            string source = SdocActivity("ACT-HOME-01");
            string participation = Between(source, "child ", " at ");
            Need(Between(source, " at ", " effect") == "half", "Source", "SDoc child cleaning effect qualification changed");
            JsonElement child = ChildVariant(activities["ACT-HOME-01"]);
            Need(Text(child, "participation") == participation && child.GetProperty("effect_multiplier").GetDouble() == 0.5,
                "Catalog", "SDoc child cleaning is " + participation + " at half effect, not ordinary participation");
        }, ref failures);
        return failures;
    }

    public static int Main(string[] args)
    {
        try
        {
            Dependencies();
            Need(File.Exists(CatalogPath), "C1.DeliverablePresent", "missing " + CatalogPath);
            int reviewFailures;
            using (var document = JsonDocument.Parse(File.ReadAllText(CatalogPath)))
            {
                Validate(document.RootElement);
                Console.WriteLine("PASS C1 C3 C4 C5 C6 C7 declarative coverage");
                foreach (string mutation in new[] { "missing", "duplicate", "empty-phase" }) RejectCopy(document.RootElement, mutation);
                reviewFailures = ReviewChecks(document.RootElement);
            }
            if (System.Array.IndexOf(args, "negative-fixture") >= 0)
            {
                Console.WriteLine("FAIL NegativeFixture corrupted catalog copies rejected");
                return 1;
            }
            if (reviewFailures != 0) return 1;
            Console.WriteLine("ACTION-CATALOG ALL PASS");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error.GetType().Name + " " + error.Message);
            return 1;
        }
    }
}
