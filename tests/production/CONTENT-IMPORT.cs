/* CONTENT-IMPORT local oracle; plan full-game-rev2-m4-1.
 * Separate suite: SLICE qualifies the earlier assets, not final 16 stage variants.
 * Resource group isolated-csharp: no editor, display, ports, network or sleeps.
 * TDD interface: Blockparty.Content.StageVariants.Resolve(BaseBody, LifeStage)
 * returns the repository-relative glTF path. Reflection keeps missing product red
 * at runtime rather than hiding it behind a compilation failure.
 * content.json follows SLICE/GATE-M1 REF={path,sha256} conventions:
 * sources:[REF], variants:[{body,stage,asset:REF,presentation}],
 * activities:[{id,gltf:REF,flow:{approach/start/loop/finish/interruption:[clip]}}],
 * appearance:[{body,dimension,index,asset:REF}],
 * Deferred audio block: William, 2026-11-01, "audio is not a blocker".
 * CI-Audio and CI-AudioVisual are not required by M4. Restore the catalog clip
 * coverage, WAV binding and matching caption/icon checks from a80db062 in a
 * later package; audio:[{id,wav:REF,caption,icon}] is deferred with them.
 * This supersedes the earlier music note's caption/icon requirement.
 * frames:[{id,png:REF,measurements:REF}], owner_ok:REF,
 * seat_slots:[string] enumerates used runtime slots, for CAP-01 seated frames.
 * Frame measurements bind png_sha256 and stdlib PNG reader tool:REF, with
 * residents:[{body_pixels:{head,face,neck,left_hand,right_hand,left_forearm,
 * right_forearm},face_render,blink,eyes_visible}], seats:[{facing_degrees,approach_error_m}],
 * garments:[{culled_body_faces,push_out}]. Nonresident frames may have no residents.
 * play_entries:[{domain_reload:false,scene_reload:true,initial_state_hash,
 * final_state_hash,trace:REF}].
 * This inspects bound gameplay evidence; it does not run Unity or certify hearing,
 * capture authenticity, full SEIT cases, visual acceptance or owner approval.
 * Synthetic matrix self-checks are parser fixtures only, never play evidence.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Activities;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;

public static class ContentImportChecks
{
    const string Scene = "Assets/Blockparty/Scenes/Main.unity";
    const string Resolver = "Assets/Blockparty/Content/StageVariants.cs";
    const string Director = "Assets/Blockparty/Presentation/MainDirector.cs";
    const string Builder = "Assets/Blockparty/Editor/MainSceneBuild.cs";
    const string Evidence = "evidence/production/CONTENT-IMPORT/";
    static readonly string[] Phases = { "approach", "start", "loop", "finish", "interruption" };
    /* Deferred #243 music block: William, 2026-10-30, "we can add music later";
     * no music is required by M4. Restore in a later package from 0297feeb:
     * MusicContexts / music_plays coverage for intro-first-launch, intro-onboarding,
     * gameplay, activity-{Club,Creative,Education,Food,Health,Minigolf,Party,Social,Work}
     * and event-{romance,funeral,birthday} (CI-MusicCoverage).
     * MainDirector/Main.unity trigger and Audio/Music path (CI-MusicTrigger/Path),
     * positive volume and advancing playing samples (CI-MusicPlays), gameplay loop
     * (CI-MusicLoop), matching intro (CI-MusicIntro), 9 activity + 3 event tracks
     * (CI-MusicTracks), bound nonsilent PCM16 WAV output (CI-MusicCapture).
     * Playback/Audible/Music helpers, playback/PCM positives and music negatives
     * are deferred with these checks; cue captions/icons remain required.
     */
    static int failures;
    static void Need(bool ok, string id, string detail)
    { if (!ok) throw new InvalidDataException(id + ": " + detail); }
    static void Run(string id, Action action)
    {
        try { action(); Console.WriteLine("PASS " + id); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + id + " " + e.GetBaseException().Message); }
    }
    static string Text(JsonElement row, string key) { return row.GetProperty(key).GetString(); }
    static JsonElement Read(string path)
    {
        Need(File.Exists(path), "CI-Missing", "missing deliverable " + path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(path))) return doc.RootElement.Clone();
    }
    static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
    static string Reference(JsonElement reference)
    {
        string path = Text(reference, "path");
        Need(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            && Path.GetFullPath(path).StartsWith(Path.GetFullPath(".") + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "CI-Reference", "expected repository-relative path");
        Need(File.Exists(path), "CI-Missing", "missing deliverable " + path);
        Need(Hash(path) == Text(reference, "sha256"), "CI-Hash", "changed content " + path);
        return path;
    }
    static HashSet<string> StageKeys()
    {
        var keys = new HashSet<string>();
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody)))
            foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage))) keys.Add(body + "/" + stage);
        return keys;
    }
    static HashSet<string> ActionKeys()
    {
        var keys = new HashSet<string>();
        foreach (var action in Read("Assets/Blockparty/Content/action-catalog.json").GetProperty("activities").EnumerateArray())
            Need(keys.Add(Text(action, "id")), "CI-Catalog", "duplicate ACT code");
        Need(keys.Count > 0, "CI-Catalog", "empty action catalog");
        return keys;
    }
    static void Coverage(JsonElement rows, HashSet<string> expected, string key, string id)
    {
        var actual = new HashSet<string>();
        foreach (var row in rows.EnumerateArray())
        {
            string value = key == "variant" ? Text(row, "body") + "/" + Text(row, "stage") : Text(row, key);
            Need(actual.Add(value), id, "duplicate " + value);
        }
        Need(actual.SetEquals(expected), id, "incomplete or unknown binding; expected " + expected.Count + ", found " + actual.Count);
    }
    static void Matrices(JsonElement receipt)
    {
        Coverage(receipt.GetProperty("variants"), StageKeys(), "variant", "CI-Stages");
        Coverage(receipt.GetProperty("activities"), ActionKeys(), "id", "CI-Actions");
    }
    static string Resolve(BaseBody body, LifeStage stage)
    {
        var type = typeof(BaseBody).Assembly.GetType("Blockparty.Content.StageVariants");
        Need(type != null, "CI-Resolver", "missing StageVariants runtime type");
        var method = type.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(BaseBody), typeof(LifeStage) }, null);
        Need(method != null && method.ReturnType == typeof(string), "CI-Resolver", "Resolve(BaseBody, LifeStage) -> string required");
        return (string)method.Invoke(null, new object[] { body, stage });
    }
    sealed class BirthScene : ISceneContent
    {
        public ObjectId Bed;
        public void RegisterObjects(ActivityExecutor executor)
        {
            if ((long)Bed != 0) executor.RegisterObject(Bed,
                new[] { new ApproachPose(0, 0, 0), new ApproachPose(0, 0, 0) });
        }
    }
    static AppearanceRecord Look(GameStack game, ResidentId id)
    {
        AppearanceRecord look;
        Need(game.Appearances.TryGet(id, out look), "CI-Lifecycle", "missing appearance"); return look;
    }
    static ResidentRecord Person(GameStack game, ResidentId id)
    {
        ResidentRecord person;
        Need(game.World.TryGetResident(id, out person), "CI-Lifecycle", "missing resident"); return person;
    }
    static string Identity(AppearanceRecord look)
    {
        return JsonSerializer.Serialize(new { look.BaseBody, look.SkinTone, look.Face, look.EyeColor,
            look.HairType, look.HairStyle, look.HairColor, look.Accessory });
    }
    static void Lifecycle(bool resolve)
    {
        const StateWriter core = StateWriter.SimCore;
        var world = WorldState.CreateSeeded(801L, 1234L, 2, null);
        var residents = world.ResidentsSnapshot(); var appearances = new AppearanceStore();
        foreach (var resident in residents)
        {
            world.SetStage(core, resident.Id, LifeStage.Adult, 0);
            appearances.Register(resident.Id, new WorldRng(1234L + (long)resident.Id));
        }
        var clock = new Clock(world); clock.SetPreset(AgingPreset.Off);
        var commands = new CommandArbitrator(world, clock); var executor = new ActivityExecutor(world, commands, clock);
        var accounts = new Accounts(world); var food = new Food(world, commands, executor, accounts);
        var expenses = new Expenses(world, accounts); var ownership = new Ownership(world, accounts);
        var housing = new Housing(world, accounts, ownership, executor, appearances);
        var game = new GameStack(world, clock, commands, executor, appearances, accounts, food, expenses, ownership, housing);
        var family = game.Family; ResidentId parent = residents[0].Id, other = residents[1].Id;
        housing.RegisterHome(core, residents[0].Home, 0L);
        housing.SetCaregiver(core, parent, true); housing.SetCaregiver(core, other, true);
        world.SetBond(core, parent, other, 50, 50); world.SetBond(core, other, parent, 50, 50);
        family.SetReproductiveCapabilities(core, parent, true, false);
        family.SetReproductiveCapabilities(core, other, false, true); family.SetPartner(core, parent, other, true);
        var scene = new BirthScene(); do { scene.Bed = world.AllocateObject(core); } while ((long)scene.Bed <= (long)other);
        scene.RegisterObjects(executor); family.RegisterBed(core, scene.Bed, residents[0].Home);
        executor.SetResidentPosition(parent, new ApproachPose(0, 0, 0)); executor.SetResidentPosition(other, new ApproachPose(0, 0, 0));
        var accepted = family.Submit(new CommandEnvelope(world.AllocateCommand(core), world.Epoch, world.GameSeconds,
            CommandSource.PlayerHuman, residents[0].ResidentSequence + 1u, CommandKind.DirectActivity, parent,
            new ActivityId("ACT-REL-06"), (long)scene.Bed, new CommandParameters.Direct(QueueMode.Immediate)));
        Need(accepted.Accepted, "CI-Birth", "real TryForBaby refused " + accepted.Reason);
        clock.AdvanceRealSeconds(core, 1812.0 / 60.0, () => family.Tick(StateWriter.ActivityExecutor));
        PregnancyRecord pregnancy;
        Need(family.TryGetPregnancy(parent, out pregnancy), "CI-Birth", "no real conception");
        clock.AdvanceRealSeconds(core, (pregnancy.DueGameTime - world.GameSeconds) / 60.0,
            () => family.Tick(StateWriter.ActivityExecutor));
        Need(world.ResidentCount == 3, "CI-Birth", "expected one newborn");
        ResidentId child = default(ResidentId);
        foreach (var resident in world.ResidentsSnapshot()) if (resident.Id != parent && resident.Id != other) child = resident.Id;
        Need(Person(game, child).Stage == LifeStage.Newborn, "CI-Birth", "wrong newborn stage");
        var birth = family.GetBirth(child); var look = Look(game, child);
        Need(birth != null && look.BaseBody == birth.BaseBody
            && (int)look.BaseBody == new WorldRng(birth.BaseBodySeed).NextInt(0, 2), "CI-Birth", "body differs from stored seeded draw");
        string identity = Identity(look), newborn = resolve ? Resolve(look.BaseBody, LifeStage.Newborn) : null;
        string temporary = Path.Combine(Path.GetTempPath(), "blockparty-content-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = SaveV1.SaveGame(game, temporary); Need(saved.Ok, "CI-Save", saved.Detail);
            var loaded = SaveV1.LoadGame(temporary, scene); Need(loaded.Ok, "CI-Save", loaded.Notice);
            var g = loaded.Game;
            game = new GameStack(g.World, g.Clock, g.Arbitrator, g.Executor, g.Appearances,
                g.Accounts, g.Food, g.Expenses, g.Ownership, g.Housing, g.Family);
            Need(Identity(Look(game, child)) == identity, "CI-Save", "newborn identity redrawn");
            if (resolve) Need(Resolve(Look(game, child).BaseBody, Person(game, child).Stage) == newborn,
                "CI-Save", "newborn variant changed after reload");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        // Independent BP-SEC-05 Moderate durations, as in AGING's oracle.
        double[] days = { 2.25, 11.25, 15.75, 31.5, 47.25, 63, 94.5 };
        game.Clock.SetPreset(AgingPreset.Moderate); var aging = new Aging(game.World, game.Clock);
        for (int stage = 0; stage < days.Length; stage++)
        {
            string before = resolve ? Resolve(look.BaseBody, (LifeStage)stage) : null;
            game.Clock.AdvanceRealSeconds(core, (days[stage] * 86400 - 6) / 60); aging.Tick(core);
            Need(Person(game, child).Stage == (LifeStage)stage, "CI-Aging", "early stage transition");
            if (resolve) Need(Resolve(Look(game, child).BaseBody, Person(game, child).Stage) == before,
                "CI-Aging", "variant changed before stage boundary");
            game.Clock.AdvanceRealSeconds(core, 6.0 / 60); aging.Tick(core);
            Need(Person(game, child).Stage == (LifeStage)(stage + 1) && Identity(Look(game, child)) == identity,
                "CI-Aging", "skipped stage or changed identity");
            if (resolve) Need(Resolve(Look(game, child).BaseBody, Person(game, child).Stage) != before,
                "CI-Aging", "variant did not change at boundary");
        }
    }
    static void Artifacts()
    {
        var missing = new List<string>();
        foreach (string path in new[] { Scene, Resolver, Director, Builder, Evidence + "result.json",
            Evidence + "build-manifest.json", Evidence + "unity-build.log", Evidence + "player-play.mp4" })
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(path);
        Need(missing.Count == 0, "CI-Missing", "missing deliverables " + string.Join(", ", missing));
        // Serialized scene references, rather than matching implementation words.
        string guid = null;
        foreach (string line in File.ReadLines(Director + ".meta"))
            if (line.StartsWith("guid: ", StringComparison.Ordinal)) guid = line.Substring(6).Trim();
        Need(!string.IsNullOrWhiteSpace(guid) && File.ReadAllText(Scene).Contains("guid: " + guid),
            "CI-SceneBinding", "Main scene does not bind MainDirector GUID");
        Need(Text(Read(Evidence + "result.json"), "status") == "PASS", "CI-Result", "result status is not PASS");
        Read(Evidence + "build-manifest.json");
    }
    static void Variants(JsonElement receipt)
    {
        foreach (var row in receipt.GetProperty("variants").EnumerateArray())
        {
            var body = (BaseBody)Enum.Parse(typeof(BaseBody), Text(row, "body"));
            var stage = (LifeStage)Enum.Parse(typeof(LifeStage), Text(row, "stage"));
            string path = Reference(row.GetProperty("asset"));
            Need(path == Resolve(body, stage) && (path.EndsWith(".gltf", StringComparison.Ordinal)
                || path.EndsWith(".glb", StringComparison.Ordinal)), "CI-Resolver", body + "/" + stage + " wrong asset");
            string package = "STAGE-" + stage.ToString().ToUpperInvariant();
            var stageReceipt = Read("evidence/production/" + package + "/result.json");
            var files = stageReceipt.GetProperty("files");
            Need(files.GetProperty(path).GetString() == Hash(path), "CI-StageReceipt", package + " does not bind resolved asset");
            Need(Text(stageReceipt.GetProperty("measured").GetProperty("bodies").GetProperty(body.ToString().ToLowerInvariant()), "asset") == path,
                "CI-StageReceipt", "resolved body differs from stage producer binding");
            if (stage <= LifeStage.Infant)
                Need(Text(row, "presentation") == "carried/crib", "CI-Dependent", "newborn/infant must use carried/crib presentation");
        }
    }
    static void Activities(JsonElement receipt)
    {
        var source = new Dictionary<string, JsonElement>();
        foreach (var action in Read("Assets/Blockparty/Content/action-catalog.json").GetProperty("activities").EnumerateArray())
            source.Add(Text(action, "id"), action);
        var clipIds = Read("Assets/Blockparty/Art/Animations/catalog.json").GetProperty("clip_ids");
        foreach (var row in receipt.GetProperty("activities").EnumerateArray())
        {
            string path = Reference(row.GetProperty("gltf"));
            var names = new HashSet<string>();
            foreach (var clip in Read(path).GetProperty("animations").EnumerateArray()) names.Add(Text(clip, "name"));
            foreach (string phase in Phases)
            {
                var clips = row.GetProperty("flow").GetProperty(phase);
                Need(clips.GetArrayLength() > 0, "CI-Flow", Text(row, "id") + " empty " + phase);
                var expected = new HashSet<string>(); var actual = new HashSet<string>();
                foreach (var clip in source[Text(row, "id")].GetProperty("flow").GetProperty(phase).GetProperty("clips").EnumerateArray())
                    expected.Add(clipIds.GetProperty(clip.GetString()).GetString());
                foreach (var clip in clips.EnumerateArray())
                {
                    Need(names.Contains(clip.GetString()), "CI-Flow", Text(row, "id") + " missing imported clip " + clip.GetString());
                    actual.Add(clip.GetString());
                }
                Need(actual.SetEquals(expected), "CI-Flow", Text(row, "id") + " differs from action/animation producer mapping for " + phase);
            }
        }
    }
    static void AppearanceAndAudio(JsonElement receipt)
    {
        var expected = new HashSet<string>(); var actual = new HashSet<string>();
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody)))
            foreach (AppearanceDimension dimension in Enum.GetValues(typeof(AppearanceDimension)))
                for (int index = 0; index < ResidentFactory.OptionCount(dimension); index++)
                    expected.Add(body + "/" + dimension + "/" + index);
        foreach (var row in receipt.GetProperty("appearance").EnumerateArray())
        {
            string key = Text(row, "body") + "/" + Text(row, "dimension") + "/" + row.GetProperty("index").GetInt32();
            Need(actual.Add(key), "CI-Appearance", "duplicate appearance binding " + key);
            Reference(row.GetProperty("asset"));
        }
        Need(actual.SetEquals(expected), "CI-Appearance", "creator index range not completely imported");
    }
    static void Receipt()
    {
        var receipt = Read(Evidence + "content.json"); Matrices(receipt);
        var sources = new HashSet<string>();
        foreach (var source in receipt.GetProperty("sources").EnumerateArray()) sources.Add(Reference(source));
        foreach (string path in new[] { Scene, Resolver, Director, Builder, Evidence + "build-manifest.json",
            Evidence + "unity-build.log", Evidence + "player-play.mp4" })
            Need(sources.Contains(path), "CI-Binding", "content receipt does not bind " + path);
        Variants(receipt); Activities(receipt); AppearanceAndAudio(receipt);
        var entries = receipt.GetProperty("play_entries");
        Need(entries.GetArrayLength() >= 2, "CI-PlayEntries", "two play entries required");
        foreach (var entry in entries.EnumerateArray())
        {
            Need(!entry.GetProperty("domain_reload").GetBoolean() && entry.GetProperty("scene_reload").GetBoolean()
                && entry.GetProperty("initial_state_hash").GetInt32() == entries[0].GetProperty("initial_state_hash").GetInt32()
                && entry.GetProperty("final_state_hash").GetInt32() == entries[0].GetProperty("final_state_hash").GetInt32(),
                "CI-PlayEntries", "state leaked between Fast Enter Play Mode entries");
            Reference(entry.GetProperty("trace"));
        }
        var frames = new HashSet<string>();
        foreach (var frame in receipt.GetProperty("frames").EnumerateArray())
        {
            Need(frames.Add(Text(frame, "id")), "CI-Frames", "duplicate frame id");
            string png = Reference(frame.GetProperty("png"));
            using (var file = File.OpenRead(png)) using (var reader = new BinaryReader(file))
                Need(BitConverter.ToString(reader.ReadBytes(8)) == "89-50-4E-47-0D-0A-1A-0A", "CI-Frames", "not a PNG");
            var measurements = Read(Reference(frame.GetProperty("measurements")));
            Need(Text(measurements, "png_sha256") == Text(frame.GetProperty("png"), "sha256"), "CI-Measurements", "measurements bind a different frame");
            Reference(measurements.GetProperty("tool"));
            var people = measurements.GetProperty("residents");
            if (StageKeys().Contains(Text(frame, "id")))
                Need(people.GetArrayLength() > 0, "CI-BodyPresence", "stage frame contains no resident measurements");
            foreach (var person in people.EnumerateArray())
            {
                foreach (string part in new[] { "head", "face", "neck", "left_hand", "right_hand", "left_forearm", "right_forearm" })
                    Need(person.GetProperty("body_pixels").GetProperty(part).GetInt32() > 0, "CI-BodyPresence", "missing rendered " + part);
                if (person.GetProperty("face_render").GetBoolean() && !person.GetProperty("blink").GetBoolean())
                    Need(person.GetProperty("eyes_visible").GetInt32() == 2, "CI-Eyes", "both eyes required in nonblink face renders");
            }
            foreach (var seat in measurements.GetProperty("seats").EnumerateArray())
            {
                double facing = seat.GetProperty("facing_degrees").GetDouble();
                Need(facing >= 0 && facing <= 45, "CI-Seats", "seat facing exceeds plan blocker 45 degrees");
                Need(seat.GetProperty("approach_error_m").GetDouble() >= 0, "CI-Seats", "missing measured approach error; 0.15 m is a candidate, not a hard gate");
            }
            foreach (var garment in measurements.GetProperty("garments").EnumerateArray())
                Need(garment.GetProperty("culled_body_faces").GetInt32() > 0 && garment.GetProperty("push_out").GetDouble() == 0,
                    "CI-Masking", "garment must cull a real body group without push-out");
        }
        foreach (string key in StageKeys()) Need(frames.Contains(key), "CI-Frames", "missing stage frame " + key);
        foreach (string key in ActionKeys()) Need(frames.Contains(key), "CI-Frames", "missing ACT frame " + key);
        foreach (string key in CaptureIds(receipt)) Need(frames.Contains(key), "CI-CAP01", "missing capture " + key);
        var owner = Read(Reference(receipt.GetProperty("owner_ok")));
        Need(Text(owner, "owner") == "William Rumph" && Text(owner, "verdict") == "accept",
            "CI-Owner", "bound owner acceptance required; agents cannot provide it");
        foreach (var frame in receipt.GetProperty("frames").EnumerateArray())
            Need(owner.GetProperty("captures").GetProperty(Text(frame.GetProperty("png"), "path")).GetString()
                == Text(frame.GetProperty("png"), "sha256"), "CI-Owner", "owner acceptance does not bind capture");
    }
    static HashSet<string> CaptureIds(JsonElement receipt)
    {
        var expected = new HashSet<string>();
        foreach (var task in Read("docs/plans/full-game-rev2/plan.json").GetProperty("tasks").EnumerateArray())
        {
            if (Text(task, "id") != "CONTENT-IMPORT") continue;
            foreach (var value in task.GetProperty("verification").GetProperty("visual").GetProperty("captures").EnumerateArray())
            {
                string name = value.GetString();
                if (!name.Contains("<")) expected.Add(name);
                else if (name.StartsWith("act-", StringComparison.Ordinal))
                    foreach (string code in ActionKeys()) expected.Add("act-" + code + "-loop.png");
                else if (name.StartsWith("interior-", StringComparison.Ordinal))
                    foreach (var site in Read("Assets/Blockparty/Content/block-layout.json").GetProperty("sites").EnumerateArray())
                    {
                        string id = Text(site, "id");
                        if (id == "apartment" || id == "common-room") expected.Add("interior-" + id + "-noon.png");
                    }
                else if (name.StartsWith("seated-", StringComparison.Ordinal))
                {
                    var slots = receipt.GetProperty("seat_slots");
                    Need(slots.GetArrayLength() > 0, "CI-CAP01", "used seat slot inventory required");
                    foreach (var slot in slots.EnumerateArray()) expected.Add("seated-" + slot.GetString() + ".png");
                }
                else Need(false, "CI-CAP01", "unresolved capture template " + name);
            }
        }
        Need(expected.Count > 0, "CI-CAP01", "missing CONTENT-IMPORT capture allocation");
        return expected;
    }
    static object Obj(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    static JsonElement Json(object value)
    { using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(value))) return doc.RootElement.Clone(); }
    static JsonElement MatrixFixture(string fault)
    {
        var variants = new List<object>(); var actions = new List<object>();
        foreach (BaseBody body in Enum.GetValues(typeof(BaseBody))) foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
            variants.Add(Obj("body", body.ToString(), "stage", stage.ToString()));
        foreach (string code in ActionKeys()) actions.Add(Obj("id", code));
        if (fault == "missing-stage") variants.RemoveAt(0);
        if (fault == "missing-action") actions.RemoveAt(0);
        return Json(Obj("variants", variants, "activities", actions));
    }
    static readonly Dictionary<string, string> Faults = new Dictionary<string, string> {
        { "missing-stage", "CI-Stages:" }, { "missing-action", "CI-Actions:" },
        { "changed-hash", "CI-Hash:" }
    };
    static void Fault(string name)
    {
        if (name.StartsWith("missing-", StringComparison.Ordinal)) Matrices(MatrixFixture(name));
        else if (name == "changed-hash") Reference(Json(Obj("path", "Assets/Blockparty/Content/action-catalog.json", "sha256", new string('0', 64))));
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "negative-fixture")
        {
            string name = args.Length == 2 ? args[1] : "missing-stage";
            if (!Faults.ContainsKey(name)) { Console.WriteLine("FAIL CI-Harness unknown fixture"); return 2; }
            Run("CI-Negative " + name, () => Fault(name));
            if (failures == 0) { Console.WriteLine("FAIL CI-Harness invalid fixture accepted"); return 2; }
            return 1;
        }
        Run("CI-PositiveMatrices", () => Matrices(MatrixFixture(null)));
        Run("CI-LandedBirthAgingSave", () => Lifecycle(false));
        foreach (var fault in Faults)
            Run("CI-Detector " + fault.Key, () => {
                try { Fault(fault.Key); }
                catch (InvalidDataException e) { Need(e.Message.StartsWith(fault.Value, StringComparison.Ordinal), "CI-Harness", "wrong rejection " + e.Message); return; }
                Need(false, "CI-Harness", "invalid fixture accepted " + fault.Key);
            });
        if (args.Length == 1 && args[0] == "self-check-only") return failures == 0 ? 0 : 1;
        Run("CI-Artifacts SEIT-AC-13 SEIT-SYS-01-P1", Artifacts);
        Run("CI-StageRuntime SEIT-AC-07 SEIT-PRES-03-P1", () => {
            foreach (BaseBody body in Enum.GetValues(typeof(BaseBody))) foreach (LifeStage stage in Enum.GetValues(typeof(LifeStage)))
                Need(!string.IsNullOrWhiteSpace(Resolve(body, stage)), "CI-Resolver", "empty stage path");
        });
        Run("CI-Lifecycle SEIT-AC-07 SEIT-PRES-03-P2", () => Lifecycle(true));
        Run("CI-Content SEIT-IF-ASSET-P2 SEIT-PRES-01-P1 SEIT-PRES-02-P1 SEIT-PRES-04-P1", Receipt);
        if (failures == 0) Console.WriteLine("CONTENT-IMPORT ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
