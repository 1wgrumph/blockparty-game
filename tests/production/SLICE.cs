/* SLICE@1 TDD red step. Local integration checks, not whole SEIT acceptance.
 * New suite is required because PROOF-SCENE/GREYBOX cannot establish the M2
 * asset/decision/audio join. All landed API preparation runs before the red.
 * Scene/director/builder and slice.json contract: evidence/production/SLICE/red.log.
 * Standalone resource group isolated-csharp: no engine, display, network or sleeps;
 * unique temporary saves, removed in finally. Unity staging: copy this unchanged
 * into an Editor TestAssemblies assembly referencing the real runtime assembly.
 * Filter Blockparty.SliceUnityTests; domain reload OFF, scene reload ON, Unity
 * 6000.3.10f1. One warm editor/project with an external editor/display lock.
 * Run only on build-host, copy m2-copy-a; qualify 5 serial and 2 concurrent
 * isolated copies before green acceptance. A complete rendered run may take
 * 240 seconds (existing GreyboxDirector timeout), hence the readiness deadline.
 * #157 camera/click selection/action menu/HUD are M3 and deliberately excluded.
 */
using System;
using System.Collections.Generic;
using System.IO;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

#if UNITY_EDITOR
namespace Blockparty
{
    public sealed class SliceUnityTests
    {
        private static object Member(object target, string name)
        {
            var type = target.GetType();
            var property = type.GetProperty(name);
            if (property != null) return property.GetValue(target);
            var field = type.GetField(name);
            NUnit.Framework.Assert.IsNotNull(field, "SLICE director API missing: " + name);
            return field.GetValue(target);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator LiveSliceAcrossTwoPlayEntries()
        {
            NUnit.Framework.Assert.AreEqual("6000.3.10f1", UnityEngine.Application.unityVersion);
            NUnit.Framework.Assert.IsTrue(UnityEditor.EditorSettings.enterPlayModeOptionsEnabled);
            NUnit.Framework.Assert.AreEqual(UnityEditor.EnterPlayModeOptions.DisableDomainReload,
                UnityEditor.EditorSettings.enterPlayModeOptions);
            string temporary = Path.Combine(Path.GetTempPath(), "blockparty-slice-" + Guid.NewGuid().ToString("N"));
            string repositoryEntries = Path.GetFullPath("evidence/production/SLICE/entries");
            int repositoryEntryCount = Directory.Exists(repositoryEntries)
                ? Directory.GetFiles(repositoryEntries, "entry-*.json").Length : 0;
            int? initial = null, final = null;
            try
            {
                for (int entry = 0; entry < 2; entry++)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Blockparty/Scenes/VerticalSlice.unity");
                    yield return new UnityEngine.TestTools.EnterPlayMode(false);
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    var directors = new List<UnityEngine.MonoBehaviour>();
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var component in root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
                            if (component != null && component.GetType().Name == "SliceDirector") directors.Add(component);
                    NUnit.Framework.Assert.AreEqual(1, directors.Count, "one live SliceDirector required");
                    var director = directors[0];
                    var run = director.GetType().GetMethod("RunVerification", new[] { typeof(string) });
                    NUnit.Framework.Assert.IsNotNull(run, "RunVerification(string evidenceDirectory)");
                    string output = Path.Combine(temporary, "entry-" + entry);
                    run.Invoke(director, new object[] { output });
                    double deadline = UnityEditor.EditorApplication.timeSinceStartup + 240;
                    while (!(bool)Member(director, "done") && UnityEditor.EditorApplication.timeSinceStartup < deadline)
                        yield return null;
                    NUnit.Framework.Assert.IsTrue((bool)Member(director, "done"), "SLICE readiness deadline; " + Member(director, "failureReason"));
                    NUnit.Framework.Assert.IsFalse((bool)Member(director, "failed"), (string)Member(director, "failureReason"));
                    string sessionEntries = Path.Combine(output, "entries");
                    NUnit.Framework.Assert.IsTrue(Directory.Exists(sessionEntries), "session-owned entries root missing");
                    NUnit.Framework.Assert.AreEqual(1, Directory.GetFiles(sessionEntries, "entry-*.json").Length,
                        "one current-session entry required");
                    int afterRepositoryEntries = Directory.Exists(repositoryEntries)
                        ? Directory.GetFiles(repositoryEntries, "entry-*.json").Length : 0;
                    NUnit.Framework.Assert.AreEqual(repositoryEntryCount, afterRepositoryEntries,
                        "editor test escaped its session root");
                    var game = (GameStack)Member(director, "Game");
                    NUnit.Framework.Assert.AreEqual(8, game.World.ResidentsSnapshot().Length);
                    NUnit.Framework.Assert.Greater(game.World.EffectLedger.Count, 0, "physical effects must exist in the live world");
                    var roots = (UnityEngine.Transform[])Member(director, "residentRoots");
                    NUnit.Framework.Assert.AreEqual(8, new HashSet<UnityEngine.Transform>(roots).Count);
                    var residentRows = game.World.ResidentsSnapshot();
                    Array.Sort(residentRows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
                    var visuals = (Array)Member(director, "residentVisuals");
                    NUnit.Framework.Assert.AreEqual(roots.Length, visuals.Length, "one visual binding per resident");
                    var bodies = new HashSet<BaseBody>();
                    var meshPaths = new HashSet<string>();
                    for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                    {
                        var root = roots[rootIndex];
                        NUnit.Framework.Assert.IsNotNull(root);
                        NUnit.Framework.Assert.IsTrue(root.gameObject.activeInHierarchy);
                        var meshes = root.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>();
                        NUnit.Framework.Assert.Greater(meshes.Length, 0, "landed skinned resident required");
                        foreach (var mesh in meshes)
                        {
                            NUnit.Framework.Assert.IsTrue(mesh.enabled && mesh.sharedMesh != null);
                            meshPaths.Add(UnityEditor.AssetDatabase.GetAssetPath(mesh.sharedMesh));
                        }
                        object visual = visuals.GetValue(rootIndex);
                        var visualType = visual.GetType();
                        var bodyRenderer = (UnityEngine.SkinnedMeshRenderer)visualType.GetField("body_renderer").GetValue(visual);
                        var sourceMesh = (UnityEngine.Mesh)visualType.GetField("source_body_mesh").GetValue(visual);
                        string sourceAsset = (string)visualType.GetField("source_asset").GetValue(visual);
                        string renderedBody = (string)visualType.GetField("base_body").GetValue(visual);
                        NUnit.Framework.Assert.IsNotNull(bodyRenderer, "drawable resident body missing");
                        NUnit.Framework.Assert.IsTrue(bodyRenderer.enabled && bodyRenderer.sharedMesh != null);
                        NUnit.Framework.Assert.Greater(bodyRenderer.sharedMesh.vertexCount, 0, "drawable body geometry required");
                        bool drawableSkin = false;
                        foreach (var material in bodyRenderer.sharedMaterials)
                            drawableSkin |= material != null && material.name.StartsWith("Skin_", StringComparison.Ordinal);
                        NUnit.Framework.Assert.IsTrue(drawableSkin, "drawable body skin submesh required");
                        NUnit.Framework.Assert.IsNotNull(sourceMesh, "retained source body mesh missing");
                        NUnit.Framework.Assert.AreEqual(sourceAsset, UnityEditor.AssetDatabase.GetAssetPath(sourceMesh),
                            "rendered source body asset binding");
                        NUnit.Framework.Assert.IsTrue(game.Appearances.TryGet(residentRows[rootIndex].Id, out AppearanceRecord appearanceForRoot));
                        NUnit.Framework.Assert.AreEqual(appearanceForRoot.BaseBody.ToString(), renderedBody,
                            "rendered body differs from saved appearance");
                        meshPaths.Add(UnityEditor.AssetDatabase.GetAssetPath(sourceMesh));
                        NUnit.Framework.Assert.IsTrue(root.GetComponentInChildren<UnityEngine.Animation>() != null
                            || root.GetComponentInChildren<UnityEngine.Animator>() != null, "resident animation required");
                    }
                    foreach (var resident in game.World.ResidentsSnapshot())
                    {
                        NUnit.Framework.Assert.IsTrue(game.Appearances.TryGet(resident.Id, out AppearanceRecord appearance));
                        bodies.Add(appearance.BaseBody);
                    }
                    NUnit.Framework.Assert.IsTrue(bodies.SetEquals(new[] { BaseBody.Female, BaseBody.Male }));
                    foreach (string body in new[] { "female", "male" })
                        NUnit.Framework.Assert.IsTrue(meshPaths.Contains("Assets/Blockparty/Art/Characters/MakeHuman/" + body + ".gltf"), "preset must switch actual imported mesh: " + body);
                    NUnit.Framework.Assert.IsNotNull(director.GetComponent<AudioMixer>(), "landed AudioMixer bridge required");
                    NUnit.Framework.Assert.Greater(director.GetComponentsInChildren<UnityEngine.AudioSource>().Length, 0);
                    NUnit.Framework.Assert.IsTrue(File.Exists(Path.Combine(output, "slice.json")), "real run must export evidence");
                    int start = (int)Member(director, "InitialStateHash"), finish = game.World.ComputeStateHash();
                    if (entry == 0) { initial = start; final = finish; }
                    else
                    {
                        NUnit.Framework.Assert.AreEqual(initial.Value, start, "static state reset at play entry");
                        NUnit.Framework.Assert.AreEqual(final.Value, finish, "same seed/workflows deterministic across play entries");
                    }
                    yield return new UnityEngine.TestTools.ExitPlayMode();
                }
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }

        [UnityEngine.TestTools.UnityTearDown]
        public System.Collections.IEnumerator ExitOwnedPlayMode()
        {
            if (UnityEngine.Application.isPlaying) yield return new UnityEngine.TestTools.ExitPlayMode();
        }
    }
}
#else
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.AI;

public static class SliceChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private const string ScenePath = "Assets/Blockparty/Scenes/VerticalSlice.unity";
    private const string DirectorPath = "Assets/Blockparty/Presentation/SliceDirector.cs";
    private const string BuilderPath = "Assets/Blockparty/Editor/SliceSceneBuild.cs";
    private const string Evidence = "evidence/production/SLICE/slice.json";
    private const string Catalog = "Assets/Blockparty/Audio/Recorded/cues.json";
    private const string Materials = "Assets/Blockparty/Art/Characters/MakeHuman/materials/catalog.json";
    private static int failures;
    private static readonly string[] Gltfs = {
        "Characters/MakeHuman/female", "Characters/MakeHuman/male", "Characters/second",
        "Rigs/MakeHuman/humanoid", "Clothing/first", "Clothing/second",
        "Animations/MakeHuman/first", "Animations/second", "Environment/apartment-first",
        "Environment/second", "Props/seat-first", "Props/second" };
    private static readonly string[] Flows = { "title", "new", "inspect", "map", "follow", "takeover", "save", "load", "continue", "accessibility" };

    private static void Require(bool ok, string why) { if (!ok) throw new InvalidDataException(why); }
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static void Reject(string name, Action action)
    {
        Test(name, () => {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "oracle accepted in-memory mutation");
        });
    }
    private static JsonElement Read(string path)
    {
        using (var d = JsonDocument.Parse(File.ReadAllText(path))) return d.RootElement.Clone();
    }
    private static string Text(JsonElement node, string key) { return node.GetProperty(key).GetString(); }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static string Reference(JsonElement node)
    {
        string path = Text(node, "path");
        Require(!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path)
            && Path.GetFullPath(path).StartsWith(Path.GetFullPath(".") + Path.DirectorySeparatorChar, StringComparison.Ordinal), "repo-relative reference required");
        Require(File.Exists(path), "missing reference " + path);
        Require(Hash(path) == Text(node, "sha256"), "sha256 mismatch " + path);
        return path;
    }
    private static JsonElement Bound(string path)
    {
        return JsonSerializer.SerializeToElement(new { path = path, sha256 = Hash(path) });
    }
    private static bool HasBinding(JsonElement node, string path, string hash)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("sha256", out var digest)
                && ((node.TryGetProperty("path", out var p) && p.GetString() == path)
                    || (node.TryGetProperty("file", out p) && p.GetString() == path))
                && digest.GetString() == hash) return true;
            foreach (var property in node.EnumerateObject()) if (HasBinding(property.Value, path, hash)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var row in node.EnumerateArray()) if (HasBinding(row, path, hash)) return true;
        return false;
    }
    private static void Rights(string path)
    {
        foreach (string file in Directory.GetFiles("production/provenance", "*.json"))
            if (HasBinding(Read(file), path, Hash(path))) return;
        // The four earlier owned world exports record provenance in glTF extras.
        if (path.Contains("/Environment/") || path.Contains("/Props/"))
        {
            var extras = Read(path).GetProperty("asset").GetProperty("extras").GetProperty("blockparty");
            if (extras.TryGetProperty("provenance", out var owned)) extras = owned;
            Require(Text(extras, "rights") == "owned" && !string.IsNullOrWhiteSpace(Text(extras, "author")), "owned provenance missing " + path);
            return;
        }
        throw new InvalidDataException("rights hash join missing " + path);
    }
    private static void Gltf(JsonElement doc, string path)
    {
        Require(Text(doc.GetProperty("asset"), "version") == "2.0", "glTF 2.0 required " + path);
        foreach (var buffer in doc.GetProperty("buffers").EnumerateArray())
            Require(Text(buffer, "uri").StartsWith("data:"), "self-contained buffer required " + path);
        if (path.Contains("/Animations/"))
        {
            var clips = new HashSet<string>();
            foreach (var clip in doc.GetProperty("animations").EnumerateArray()) clips.Add(Text(clip, "name"));
            Require(clips.SetEquals(path.Contains("/MakeHuman/") ? new[] { "walk", "sit", "reach", "hold" } : new[] { "eat" }), "measured animation names differ " + path);
        }
        if (path.Contains("/Characters/") || path.Contains("/Clothing/") || path.Contains("/Rigs/"))
            Require(doc.GetProperty("skins").GetArrayLength() > 0, "landed skin missing " + path);
    }
    private static void LandedAssets()
    {
        foreach (string name in Gltfs)
        {
            string path = "Assets/Blockparty/Art/" + name + ".gltf";
            Gltf(Read(path), path);
            Rights(path);
            Require(File.ReadAllText(path + ".meta").Contains("guid: "), "asset GUID missing " + path);
        }
        Rights(Materials);
        var catalog = Read(Catalog);
        foreach (var cue in catalog.GetProperty("cues").EnumerateObject())
        {
            string wav = Text(cue.Value, "wav");
            Rights(wav);
            Require(!string.IsNullOrWhiteSpace(Text(cue.Value.GetProperty("visual"), "caption"))
                && !string.IsNullOrWhiteSpace(Text(cue.Value.GetProperty("visual"), "icon")), "caption/icon missing");
        }
        Rights(Text(catalog.GetProperty("ambience").GetProperty("day"), "wav"));
    }

    // The actual scene-content interface, not a stand-in for a game service.
    private sealed class Stations : ISceneContent
    {
        public void RegisterObjects(ActivityExecutor executor)
        {
            for (long id = 9; id <= 16; id++)
                executor.RegisterObject((ObjectId)id, new[] { new ApproachPose((id - 9) * 2f, 0f, 2f) });
        }
    }
    private sealed class NoNetwork : IDecisionTransport
    {
        public int Sends;
        public void Send(GatewayRequest request, DecisionPayload payload) { Sends++; }
        public bool TryReceive(out DecisionResponse response) { response = null; return false; }
    }
    private static int Preparation(string directory)
    {
        var flow = new TitleFlow(directory, new Stations());
        Require(flow.Screen == TitleScreen.Title && flow.Activate(TitleAction.NewGame).Ok, "title/new API");
        Require(flow.PrepareNewGame(27L, 42L, null).Ok, "factory API");
        var game = flow.Game;
        var residents = game.World.ResidentsSnapshot();
        Require(residents.Length == 8, "factory must create eight residents");
        Array.Sort(residents, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        Require(flow.SetBaseBody(residents[0].Id, BaseBody.Female).Ok
            && flow.SetBaseBody(residents[1].Id, BaseBody.Male).Ok && flow.BeginPlay().Ok, "BODY-PRESET API");
        var hero = residents[0];
        for (int n = 0; n < StateSchema.NeedCount; n++) game.World.SetNeed(Core, hero.Id, (Need)n, 80f);
        game.World.SetNeed(Core, hero.Id, Need.Hunger, 8f);
        game.World.SetTrait(Core, hero.Id, TraitAxis.Active, 50);
        var relations = new Relationships(game.World);
        var history = relations.Complete(Core, new ActivityId("ACT-REL-04"), hero.Id, residents[1].Id,
            loserDraw: 0, initiatorSentimentDraw: 0.99, recipientSentimentDraw: 0.99);
        Require(history.Applied, "real known event preparation");
        var unknown = game.World.AppendEvent(Core, "unobserved", residents[2].Id, "not resident knowledge").Event;
        var autonomy = new Autonomy(game.World, game.Arbitrator);
        var boundary = new ProposalBoundary(game.World, game.Clock, game.Arbitrator, autonomy);
        var payload = boundary.BuildDecisionPayload(hero.Id);
        Require(payload.Needs[(int)Need.Hunger] == 8f && payload.KnownHistoryEventIds.Length == 1
            && payload.KnownHistoryEventIds[0] == (long)history.Event
            && Array.IndexOf(payload.KnownHistoryEventIds, (long)unknown) < 0, "grounded need/history API");
        var transport = new NoNetwork();
        var adapter = new DecisionAdapter(boundary, transport);
        game.Food.SetHomeStock(Core, hero.Home, 4, 2);
        Require(adapter.RequestDecision(hero.Id, new ServiceEndpoint("local", "https://example.invalid", "d1-ultra", false), false, true) == null
            && transport.Sends == 0 && autonomy.Decisions.Count == 1, "unprovisioned D1U local fallback");
        var decision = autonomy.Decisions[0];
        var pending = game.Arbitrator.GetPending(hero.Id);
        Require(decision.HasChoice && decision.Chosen.Code == "ACT-FOOD-01" && pending.Length == 1
            && pending[0].Activity == decision.Chosen && pending[0].Owner == ActionOwner.Autonomous, "local choice enqueued via COMMAND: chosen=" + decision.Chosen.Code + " pending=" + pending.Length + " effect=" + decision.CommittedEffect);
        var action = pending[0].Action;
        // Seeded fallback chooses Cook (+45 Hunger, +2 meals); NAV travel is exercised below.
        game.Executor.SetResidentPosition(hero.Id, new ApproachPose(-2f, 0f, -2f));
        for (int tick = 0; tick < 500; tick++)
        {
            game.Clock.AdvanceRealSeconds(Core, 0.1);
            game.Food.Tick(StateWriter.ActivityExecutor);
        }
        var execution = game.Executor.GetExecution(action);
        Require(execution != null && execution.Outcome == TerminalOutcome.Completed
            && hero.GetNeed(Need.Hunger) == 53f && game.Food.PreparedMeals(hero.Home) == 4, "physical fallback cook and catalog +45 Hunger/+2 meals");
        int committed = 0;
        foreach (var effect in game.World.EffectLedger) if (effect.Action == action && effect.State == LedgerState.Committed) committed++;
        Require(committed == 1, "one physical effect commit");
        var panel = new ResidentPanel(game.World, game.Arbitrator);
        Require(panel.TryInspect(hero.Id, out ResidentPanelSnapshot snapshot, out string reason)
            && snapshot.Resident.Id == hero.Id && panel.TryExplain(hero.Id, history.Event, out MemoryEntry explanation, out reason), "resident inspect/explain API");
        var nav = new WorldNavigation(game.World, game.Arbitrator, game.Executor);
        nav.OpenMap();
        Require(nav.IsMapOpen && nav.SelectResident(hero.Id).Accepted && nav.FollowResident(hero.Id).Accepted
            && nav.FollowedResident == hero.Id && nav.TravelTo(hero.Id, 9L, QueueMode.Immediate, TravelTargetKind.Object).Accepted, "map/follow/manual takeover COMMAND/NAV API");
        for (int tick = 0; tick < 100; tick++)
        {
            game.Clock.AdvanceRealSeconds(Core, 0.1); game.Food.Tick(StateWriter.ActivityExecutor);
        }
        var arrived = game.Executor.GetResidentPosition(hero.Id);
        // NAV uses a 0.06 m arrival envelope in the existing PROOF-SCENE suite.
        Require(Math.Abs(arrived.X) <= 0.06f && Math.Abs(arrived.Z - 2f) <= 0.06f, "physical NAV arrival");
        var access = new Accessibility(flow.Settings);
        access.SetCaptions(true); access.SetNonColorCues(true);
        Require(access.TrySetTextScale(1.5f) && access.CaptionFor("Meal finished") == "Meal finished"
            && access.NonColorIndicator("Completed") == "[Completed]", "ACCESS API");
        Require(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.Save).Ok
            && flow.Activate(TitleAction.ReturnToTitle).Ok && flow.ContinueEnabled
            && flow.Activate(TitleAction.Continue).Ok, "real SaveV1 save/Continue API");
        Require(flow.Game.World.ResidentsSnapshot().Length == 8 && flow.Game.Appearances.Count == 8, "eight residents reloaded");
        for (int i = 0; i < residents.Length; i++)
        {
            Require(flow.Game.World.TryGetResident(residents[i].Id, out var loaded)
                && loaded.DisplayName == residents[i].DisplayName && loaded.Money == residents[i].Money
                && loaded.GetNeed(Need.Hunger) == residents[i].GetNeed(Need.Hunger), "resident continuity");
            Require(flow.Game.Appearances.TryGet(residents[i].Id, out var look)
                && game.Appearances.TryGet(residents[i].Id, out var originalLook)
                && look.BaseBody == originalLook.BaseBody, "body choice continuity");
        }
        Require(flow.Activate(TitleAction.Pause).Ok && flow.Activate(TitleAction.ReturnToTitle).Ok
            && flow.Activate(TitleAction.Load).Ok && flow.LoadSavedGame().Ok, "explicit Load API");
        var savedBefore = Read(Path.Combine(directory, SaveFormat.FileName)).GetProperty("payload");
        Require(SaveV1.SaveGame(flow.Game, directory).Ok, "resave actual loaded world");
        var savedAfter = Read(Path.Combine(directory, SaveFormat.FileName)).GetProperty("payload");
        var ids = new HashSet<long>();
        foreach (var resident in residents) ids.Add((long)resident.Id);
        SavedResidents(savedBefore, savedAfter, ids);
        // Reject a mutation of real SaveV1 output, not a synthetic receipt fixture.
        var bad = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(savedAfter.GetRawText());
        bad["residents"] = JsonSerializer.SerializeToElement(new object[0]);
        bool rejected = false;
        try { SavedResidents(savedBefore, JsonSerializer.SerializeToElement(bad), ids); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "saved-resident oracle accepted in-memory deletion");
        return flow.Game.World.ComputeStateHash();
    }

    private static HashSet<long> ResidentRows(JsonElement rows)
    {
        var ids = new HashSet<long>();
        var bodies = new HashSet<string>();
        foreach (var row in rows.EnumerateArray())
        {
            Require(ids.Add(row.GetProperty("id").GetInt64()), "duplicate resident");
            string body = Text(row, "base_body"), mesh = Reference(row.GetProperty("mesh"));
            Require(body == "Female" || body == "Male", "unknown base body");
            if (row.GetProperty("id").GetInt64() == 1)
                Require(body == "Female" && mesh == "Assets/Blockparty/Art/Characters/second.gltf",
                    "resident 1 source mesh mismatch");
            else
                Require(mesh == "Assets/Blockparty/Art/Characters/MakeHuman/" + body.ToLowerInvariant() + ".gltf",
                    "base-body mesh mismatch");
            bodies.Add(body);
        }
        Require(ids.Count == 8 && bodies.SetEquals(new[] { "Female", "Male" }), "eight residents and both bodies required");
        return ids;
    }
    private static void SavedResidents(JsonElement beforePayload, JsonElement afterPayload, HashSet<long> ids)
    {
        var savedIds = new HashSet<long>();
        var savedBodies = new HashSet<int>();
        var beforeResidents = beforePayload.GetProperty("residents");
        var afterResidents = afterPayload.GetProperty("residents");
        Require(beforeResidents.GetArrayLength() == 8 && afterResidents.GetArrayLength() == 8, "eight actual saved residents required");
        for (int i = 0; i < 8; i++)
        {
            savedIds.Add(beforeResidents[i].GetProperty("id").GetInt64());
            // SAVE rebases command sequences/generations on load; compare durable resident data.
            foreach (string field in new[] { "id", "name", "stage", "household", "home", "money", "needs", "traits", "skills", "bonds", "family", "feelings", "memories" })
                Require(beforeResidents[i].GetProperty(field).GetRawText() == afterResidents[i].GetProperty(field).GetRawText(), "saved resident differs: " + field);
        }
        Require(savedIds.SetEquals(ids), "actual save ids differ from rendered cast");
        foreach (var row in beforePayload.GetProperty("appearances").EnumerateArray()) savedBodies.Add(row.GetProperty("baseBody").GetInt32());
        Require(savedBodies.SetEquals(new[] { (int)BaseBody.Female, (int)BaseBody.Male })
            && beforePayload.GetProperty("appearances").GetRawText() == afterPayload.GetProperty("appearances").GetRawText(), "actual saved appearances differ");
    }
    private static void Receipt()
    {
        var receipt = Read(Evidence);
        Require(Text(receipt, "schema") == "blockparty-slice/1" && Text(receipt, "claim_scope") == "M2 representative corner; local integration only",
            "local prototype scope required; not full-game/alpha/SEIT acceptance");
        Require(Reference(receipt.GetProperty("scene")) == ScenePath, "wrong scene");
        Require(Reference(receipt.GetProperty("director")) == DirectorPath && Reference(receipt.GetProperty("builder")) == BuilderPath, "candidate code binding");
        var assets = new HashSet<string>();
        foreach (var asset in receipt.GetProperty("assets").EnumerateArray())
        {
            string path = Reference(asset); assets.Add(path);
            if (path != Catalog) Rights(path);
            if (asset.TryGetProperty("guid", out var guid))
                Require(File.ReadAllText(path + ".meta").Contains("guid: " + guid.GetString()), "GUID mismatch " + path);
        }
        foreach (var name in Gltfs) Require(assets.Contains("Assets/Blockparty/Art/" + name + ".gltf"), "slice omitted landed asset " + name);
        Require(assets.Contains(Materials) && assets.Contains(Catalog), "material/audio catalog consumption required");
        var ids = ResidentRows(receipt.GetProperty("residents"));
        var save = receipt.GetProperty("save_reload");
        string beforeFile = Reference(save.GetProperty("save")), afterFile = Reference(save.GetProperty("reloaded_save"));
        var beforePayload = Read(beforeFile).GetProperty("payload");
        var afterPayload = Read(afterFile).GetProperty("payload");
        SavedResidents(beforePayload, afterPayload, ids);
        Require(Text(save.GetProperty("save"), "path") != Text(save.GetProperty("reloaded_save"), "path"), "independent save/reload artifacts required");
        Require(ResidentRows(save.GetProperty("before")).SetEquals(ids) && ResidentRows(save.GetProperty("after")).SetEquals(ids), "save/reload resident ids differ");
        Require(save.GetProperty("before").GetRawText() == save.GetProperty("after").GetRawText(), "canonical saved resident projections differ");
        var interactions = new HashSet<string>();
        foreach (var row in receipt.GetProperty("interactions").EnumerateArray())
        {
            Require(ids.Contains(row.GetProperty("resident_id").GetInt64()), "interaction resident missing");
            string body = Text(row, "base_body"), clip = Text(row, "clip");
            Require(body == "Female" || body == "Male", "interaction body missing");
            bool bodyMatches = false;
            foreach (var resident in receipt.GetProperty("residents").EnumerateArray())
                if (resident.GetProperty("id").GetInt64() == row.GetProperty("resident_id").GetInt64()) bodyMatches = Text(resident, "base_body") == body;
            Require(bodyMatches, "interaction body differs from resident preset");
            Require(Text(row, "outcome") == "Completed" && !string.IsNullOrWhiteSpace(Text(row, "note")), "interaction completion/note missing");
            // BP-PRES-01/SEIT-PRES-01-P2 candidate tolerance, not a vendor guarantee.
            double error = row.GetProperty("approach_error_m").GetDouble();
            Require(error >= 0 && error <= 0.15, "approach exceeds 0.15 m candidate");
            Reference(row.GetProperty("frame")); interactions.Add(body + ":" + clip);
        }
        foreach (string body in new[] { "Female", "Male" }) foreach (string clip in new[] { "walk", "sit", "reach", "hold" })
            Require(interactions.Contains(body + ":" + clip), "interaction missing " + body + ":" + clip);
        var physical = receipt.GetProperty("physical_action");
        Require(ids.Contains(physical.GetProperty("resident_id").GetInt64()) && (Text(physical, "activity") == "ACT-FOOD-01" || Text(physical, "activity") == "ACT-FOOD-02")
            && Text(physical, "outcome") == "Completed" && physical.GetProperty("committed_effects").GetInt32() == 1
            && physical.GetProperty("hunger_after").GetDouble() - physical.GetProperty("hunger_before").GetDouble() == (Text(physical, "activity") == "ACT-FOOD-01" ? 45 : 40), "physical meal must complete through executor/FOOD once");
        Reference(physical.GetProperty("trace"));
        var decision = receipt.GetProperty("grounded_decision");
        Require(ids.Contains(decision.GetProperty("resident_id").GetInt64()) && Text(decision, "mode") == "local-fallback"
            && Text(decision, "outcome") == "Unprovisioned" && decision.GetProperty("transport_sends").GetInt32() == 0, "unprovisioned game must use local D1U fallback");
        Require(Text(decision, "chosen_activity") == Text(physical, "activity")
            && decision.GetProperty("resident_id").GetInt64() == physical.GetProperty("resident_id").GetInt64(), "decision must drive recorded physical action");
        var known = new HashSet<long>();
        foreach (var id in decision.GetProperty("known_history_ids").EnumerateArray()) known.Add(id.GetInt64());
        Require(known.Count > 0 && !known.Contains(decision.GetProperty("unknown_event_id").GetInt64()), "known history only");
        Require(decision.GetProperty("needs").GetArrayLength() == StateSchema.NeedCount
            && decision.GetProperty("needs")[(int)Need.Hunger].GetDouble() == physical.GetProperty("hunger_before").GetDouble(), "actual needs must ground decision");
        Reference(decision.GetProperty("trace"));
        var seenFlows = new HashSet<string>();
        foreach (var row in receipt.GetProperty("ui_flows").EnumerateArray())
        {
            Require(Text(row, "outcome") == "Completed", "UI flow not exercised");
            seenFlows.Add(Text(row, "id")); Reference(row.GetProperty("frame")); Reference(row.GetProperty("trace"));
        }
        Require(seenFlows.SetEquals(Flows), "landed UI flows missing");
        var cues = Read(Catalog).GetProperty("cues");
        var played = new HashSet<string>();
        foreach (var row in receipt.GetProperty("audio").EnumerateArray())
        {
            string id = Text(row, "id"); played.Add(id);
            var cue = cues.GetProperty(id);
            string wav = Reference(row.GetProperty("wav"));
            Require(wav == Text(cue, "wav") && assets.Contains(wav) && row.GetProperty("played").GetBoolean(), "recorded cue not played/resolved");
            Require(Text(row, "caption") == Text(cue.GetProperty("visual"), "caption")
                && Text(row, "visual_id") == Text(cue.GetProperty("visual"), "icon"), "caption/visual must match recorded cue");
            Reference(row.GetProperty("trace"));
        }
        Require(played.SetEquals(new[] { "food-completed", "sleep-completed" }), "both recorded completion cues required");
        var ambience = receipt.GetProperty("ambience");
        string day = Reference(ambience.GetProperty("wav"));
        Require(day == Text(Read(Catalog).GetProperty("ambience").GetProperty("day"), "wav") && assets.Contains(day)
            && ambience.GetProperty("played").GetBoolean() && ambience.GetProperty("loop").GetBoolean(), "recorded day ambience must loop in slice");
        Reference(ambience.GetProperty("trace"));
        var entries = receipt.GetProperty("play_entries");
        Require(entries.GetArrayLength() >= 2, "two play entries required");
        foreach (var entry in entries.EnumerateArray())
        {
            Require(Text(entry, "unity_version") == "6000.3.10f1" && !entry.GetProperty("domain_reload").GetBoolean()
                && entry.GetProperty("scene_reload").GetBoolean(), "Fast Enter Play Mode configuration");
            Require(entry.GetProperty("initial_state_hash").GetInt32() == entries[0].GetProperty("initial_state_hash").GetInt32()
                && entry.GetProperty("final_state_hash").GetInt32() == entries[0].GetProperty("final_state_hash").GetInt32(), "play entry state leaked");
            Reference(entry.GetProperty("trace"));
        }
    }

    private static string UnityRoot()
    {
        string configured = Environment.GetEnvironmentVariable("BLOCKPARTY_UNITY");
        if (!string.IsNullOrEmpty(configured)) return configured;
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
    }

    public static int Main(string[] args)
    {
        Test("SLICE.LandedAssets SEIT-IF-ASSET-P2 SEIT-PRES-01-P3", LandedAssets);
        string temp = Path.Combine(Path.GetTempPath(), "blockparty-slice-" + Guid.NewGuid().ToString("N"));
        try
        {
            Test("SLICE.LandedWorkflow SEIT-AC-18 SEIT-AC-20 SEIT-AI-05-P1/P2/N3 SEIT-IF-D1U-N5", () => {
                Directory.CreateDirectory(temp);
                int first = Preparation(Path.Combine(temp, "a")), second = Preparation(Path.Combine(temp, "b"));
                Require(first == second, "cross-run deterministic fixture");
            });
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        var reference = Bound("Assets/Blockparty/Art/Characters/MakeHuman/female.gltf");
        Reject("SLICE.Reference-negative SEIT-IF-ASSET-P2", () => Reference(JsonSerializer.SerializeToElement(new {
            path = Text(reference, "path"), sha256 = new string('0', 64) })));
        var gltf = Read("Assets/Blockparty/Art/Animations/MakeHuman/first.gltf");
        var mutated = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(gltf.GetRawText());
        mutated["animations"] = JsonSerializer.SerializeToElement(new object[0]);
        Reject("SLICE.Clips-negative SEIT-PRES-01-N1", () => Gltf(JsonSerializer.SerializeToElement(mutated), "Assets/Blockparty/Art/Animations/MakeHuman/first.gltf"));
        if (args.Length == 1 && args[0] == "--preconditions") return failures == 0 ? 0 : 1;
        Test("SLICE.SceneContract SEIT-AC-13 SEIT-SYS-01-N1/N2", () => {
            var missing = new List<string>();
            foreach (string path in new[] { ScenePath, DirectorPath, BuilderPath }) if (!File.Exists(path)) missing.Add(path);
            Require(missing.Count == 0, "MISSING_SLICE: " + string.Join(", ", missing));
        });
        if (File.Exists(ScenePath) && File.Exists(DirectorPath) && File.Exists(BuilderPath))
            Test("SLICE.PlayEvidence SEIT-AC-13/18/20 SEIT-PRES-01-P1/P2 SEIT-PRES-02-P1/P2/N1", Receipt);
        Test("SLICE.NormalStartPlayer residents/routines/audio/save", () => {
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "python3", "evidence/production/SLICE/check_player.py") { UseShellExecute = false }))
            {
                process.WaitForExit();
                Require(process.ExitCode == 0, "normal-start player regression");
            }
        });
        Test("SLICE.RepairRegressions R1-R4 and OCR a-d", () => {
            string arguments = "evidence/production/SLICE/check_repairs.py --item all --unity \""
                + UnityRoot() + "\"";
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "python3", arguments) { UseShellExecute = false }))
            {
                process.WaitForExit();
                Require(process.ExitCode == 0, "repair regression checks");
            }
        });
        if (failures == 0) Console.WriteLine("SLICE ALL PASS");
        else Console.WriteLine("SLICE FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
#endif
