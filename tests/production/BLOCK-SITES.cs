/*
BLOCK-SITES@1 test-first contract; isolated-csharp (no editor/display/server).
New package suite required by plan.json / m3/test-engineer.json BLOCK-SITES.
Real producers: committed Workplaces/{cafe,workshop}.gltf and
Civic/{office,school}.gltf asset.extras.blockparty anchors; real STATE/COMMAND/NAV.

Product API in Blockparty.Simulation (plain C# 9):
  BlockSites(WorldState, ActivityExecutor); IReadOnlyList<BlockSite> Sites
  BlockSite: string Id, AssetPath; ObjectId Object; ApproachPose Origin, Entry;
             IReadOnlyList<BlockSiteSlot> Slots
  BlockSiteSlot: string Id, ActivityCode, ObjectKind; ObjectId Object; ApproachPose Pose
Object is the allocated, registered entry destination; each slot Object is its
allocated interaction target. Origin translates the asset's metre-space anchors.
Office Computer slots offer ACT-WORK-03/02/04; workshop retains ACT-WORK-02.
WorldNavigation adds a four-argument overload (..., BlockSites) and MapPins;
each pin exposes Id, Object, Entry. Existing three-argument callers remain valid.
Reflection is limited to these NEW API seams so missing capability is a named
runtime FAIL, while the entire test body still compiles against the real baseline.

Coverage: RegistryAssetsAndPins => SEIT-WORLD-01-P1 (four-building subclaim),
SEIT-TB-07-N5 / SEIT-AC-08 building inventory only; TravelEverySite =>
SEIT-UI-03-P1; UseSlotsAndFollow => SEIT-WORLD-01-P1, SEIT-UI-05-P1/N1/N2
(M3 on-block work/study), SEIT-UI-06-P1 (activity/location subclaim).
Camera rendering, packaged normal controls, asset exclusion/privacy and the rest
of those system/acceptance cases remain at their later gates. No new durable
registry state is required: layout metadata is in code, not a save schema.

negative-fixture [fault]: run the SAME assertions with corrupted observations
(missing-slot-{cafe,workshop,office,school}, missing-office-{ACT-WORK-03,
ACT-WORK-02,ACT-WORK-04}, follow-retarget) or real NAV faults
(unreachable-{site}, off-map-{site}). With no fault argument all are exercised
on fresh worlds. Observation corruption checks the oracle, not a product defect.
All normal/negative runs must be repeated on the product candidate; missing
BLOCK-SITES on the baseline cannot qualify those branches as passing.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

public static class BlockSitesChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;
    private static string fault;
    private static readonly string[] Names = { "cafe", "workshop", "office", "school" };
    private static readonly string[] Paths = {
        "Assets/Blockparty/Art/Environment/Workplaces/cafe.gltf",
        "Assets/Blockparty/Art/Environment/Workplaces/workshop.gltf",
        "Assets/Blockparty/Art/Environment/Civic/office.gltf",
        "Assets/Blockparty/Art/Environment/Civic/school.gltf" };

    private sealed class Site
    {
        public string Id, AssetPath;
        public ObjectId Object;
        public ApproachPose Origin, Entry;
        public readonly List<Slot> Slots = new List<Slot>();
    }
    private sealed class Slot
    {
        public string Id, ActivityCode, ObjectKind;
        public ObjectId Object;
        public ApproachPose Pose;
    }
    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 42L, 0, null);
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly ActivityExecutor Executor;
        public readonly WorldNavigation Ui;
        public readonly List<Site> Sites;
        public readonly long FirstObject;
        public Fixture()
        {
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock);
            // Existing allocated objects ensure site IDs cannot be hardcoded from 1.
            for (int i = 0; i < 3; i++) World.AllocateObject(Core);
            FirstObject = World.NextObjectId;
            Type type = typeof(WorldState).Assembly.GetType("Blockparty.Simulation.BlockSites");
            if (type == null) throw new Exception("missing BLOCK-SITES capability: Blockparty.Simulation.BlockSites registry");
            object registry = Activator.CreateInstance(type, new object[] { World, Executor });
            Sites = ReadSites(Read<object>(registry, "Sites"));
            Ui = (WorldNavigation)Activator.CreateInstance(typeof(WorldNavigation),
                new object[] { World, Commands, Executor, registry });
        }
        public ResidentRecord Add(string name, LifeStage stage, ApproachPose position)
        {
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord household = World.AllocateHousehold(Core, name + " home", home.Id);
            ResidentRecord resident = World.AllocateResident(Core, name, stage, household.Id, home.Id);
            Executor.SetResidentPosition(resident.Id, position);
            return resident;
        }
        public void Step()
        {
            Clock.AdvanceRealSeconds(Core, 0.1);
            Executor.Tick(StateWriter.ActivityExecutor);
            Ui.Refresh();
        }
    }

    private static T Read<T>(object row, string name)
    {
        if (row == null) throw new Exception("missing BLOCK-SITES capability: null " + name);
        Type type = row.GetType();
        PropertyInfo property = type.GetProperty(name);
        FieldInfo field = type.GetField(name);
        if (property == null && field == null)
            throw new Exception("missing BLOCK-SITES capability: " + type.Name + "." + name);
        return (T)(property != null ? property.GetValue(row) : field.GetValue(row));
    }
    private static List<Site> ReadSites(object rows)
    {
        var sites = new List<Site>();
        foreach (object row in (IEnumerable)rows)
        {
            var site = new Site { Id = Read<string>(row, "Id"), AssetPath = Read<string>(row, "AssetPath"),
                Object = Read<ObjectId>(row, "Object"), Origin = Read<ApproachPose>(row, "Origin"),
                Entry = Read<ApproachPose>(row, "Entry") };
            foreach (object slot in (IEnumerable)Read<object>(row, "Slots"))
                site.Slots.Add(new Slot { Id = Read<string>(slot, "Id"),
                    ActivityCode = Read<string>(slot, "ActivityCode"), ObjectKind = Read<string>(slot, "ObjectKind"),
                    Object = Read<ObjectId>(slot, "Object"), Pose = Read<ApproachPose>(slot, "Pose") });
            sites.Add(site);
        }
        return sites;
    }
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static void Run(string name, Action action)
    {
        try { action(); }
        catch (Exception e) { Check(false, name + " " + e.GetBaseException().Message); }
    }
    private static double Distance(ApproachPose a, ApproachPose b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        return Math.Sqrt(x*x + y*y + z*z);
    }
    private static bool Finite(ApproachPose p)
    {
        return !float.IsNaN(p.X) && !float.IsInfinity(p.X)
            && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y)
            && !float.IsNaN(p.Z) && !float.IsInfinity(p.Z);
    }
    private static Site Find(Fixture f, string name)
    {
        foreach (Site site in f.Sites) if (site.Id == name) return site;
        throw new Exception("missing BLOCK-SITES site " + name);
    }
    private static ApproachPose Anchor(JsonElement nodes, int index, ApproachPose origin)
    {
        JsonElement translation;
        if (!nodes[index].TryGetProperty("translation", out translation)) return origin;
        return new ApproachPose(origin.X + translation[0].GetSingle(),
            origin.Y + translation[1].GetSingle(), origin.Z + translation[2].GetSingle());
    }
    private static string Asset(string relative)
    {
        DirectoryInfo dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new Exception("missing real ENV asset: " + relative);
    }
    private static bool Allocated(Fixture f, ObjectId id, HashSet<long> used)
    {
        long value = (long)id;
        return value >= f.FirstObject && value < f.World.NextObjectId && used.Add(value);
    }
    private static void RegistryAssetsAndPins()
    {
        var f = new Fixture();
        var again = new Fixture();
        var used = new HashSet<long>();
        var entries = new List<ApproachPose>();
        Check(f.Sites.Count == 6, "RegistryAssetsAndPins six-sites SEIT-WORLD-01-P1");
        for (int i = 0; i < Names.Length; i++)
        {
            Site site = Find(f, Names[i]), twin = Find(again, Names[i]);
            Check(Allocated(f, site.Object, used) && site.Object == twin.Object
                && site.AssetPath == Paths[i] && Finite(site.Origin) && Finite(site.Entry)
                && Distance(site.Entry, twin.Entry) < 0.0001,
                "RegistryAssetsAndPins allocated-stable-entry-" + site.Id);
            foreach (ApproachPose entry in entries)
                Check(Distance(site.Entry, entry) > ActivityExecutor.ArrivalToleranceMeters,
                    "RegistryAssetsAndPins distinct-sites-" + site.Id);
            entries.Add(site.Entry);
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Asset(Paths[i]))))
            {
                JsonElement root = doc.RootElement, nodes = root.GetProperty("nodes");
                JsonElement bp = root.GetProperty("asset").GetProperty("extras").GetProperty("blockparty");
                JsonElement anchors;
                bool workplaces = bp.TryGetProperty("anchors", out anchors);
                int entryNode = workplaces ? anchors.GetProperty("entry").GetInt32() : bp.GetProperty("entry_node").GetInt32();
                JsonElement expected = workplaces ? anchors.GetProperty("slots") : bp.GetProperty("interaction_slots");
                Check(Distance(site.Entry, Anchor(nodes, entryNode, site.Origin)) < 0.0001,
                    "RegistryAssetsAndPins real-asset-entry-" + site.Id);
                if (fault == "missing-slot-" + site.Id) site.Slots.Clear();
                if (site.Id == "office" && fault != null && fault.StartsWith("missing-office-", StringComparison.Ordinal))
                    site.Slots.RemoveAll(s => s.ActivityCode == fault.Substring("missing-office-".Length));
                Check(site.Slots.Count >= 1, "RegistryAssetsAndPins usable-slot-in-" + site.Id + " SEIT-WORLD-01-P1");
                foreach (JsonElement expectedSlot in expected.EnumerateArray())
                {
                    string id = expectedSlot.GetProperty("id").GetString();
                    Slot slot = site.Slots.Find(s => s.Id == id);
                    Check(slot != null && slot.ActivityCode == expectedSlot.GetProperty("activity").GetString()
                        && Distance(slot.Pose, Anchor(nodes, expectedSlot.GetProperty("node").GetInt32(), site.Origin)) < 0.0001
                        && Allocated(f, slot.Object, used)
                        && twin.Slots.Exists(s => s.Id == id && s.Object == slot.Object
                            && s.ActivityCode == slot.ActivityCode && Distance(s.Pose, slot.Pose) < 0.0001)
                        && (site.Id != "office" || slot.ObjectKind == "Computer"),
                        "RegistryAssetsAndPins asset-slot-" + site.Id + "/" + id + " M3-OD-OFFICE");
                }
            }
        }
        var pinIds = new HashSet<string>();
        foreach (object pin in (IEnumerable)Read<object>(f.Ui, "MapPins"))
        {
            string id = Read<string>(pin, "Id");
            Site site = Find(f, id);
            Check(pinIds.Add(id) && Read<ObjectId>(pin, "Object") == site.Object
                && Distance(Read<ApproachPose>(pin, "Entry"), site.Entry) < 0.0001,
                "RegistryAssetsAndPins map-pin-" + id);
        }
        Check(pinIds.SetEquals(new[] { "apartment", "common-room", "cafe", "workshop", "office", "school" }), "RegistryAssetsAndPins all-six-map-pins SEIT-WORLD-01-P1");
    }
    private static bool BoundedStep(Fixture f, ResidentId resident)
    {
        ApproachPose before = f.Executor.GetResidentPosition(resident);
        long time = f.World.GameSeconds;
        f.Step();
        return Distance(before, f.Executor.GetResidentPosition(resident))
            <= ActivityExecutor.TravelMetersPerGameMinute * (f.World.GameSeconds - time) / 60.0 + 0.00001;
    }
    private static void TravelEverySite()
    {
        foreach (string name in Names)
        {
            var f = new Fixture();
            Site site = Find(f, name);
            var start = new ApproachPose(site.Entry.X - 60f, site.Entry.Y, site.Entry.Z);
            ResidentRecord resident = f.Add("Walker", LifeStage.Adult, start);
            if (fault == "unreachable-" + name) f.Executor.SetReachable(site.Object, false);
            if (fault == "off-map-" + name) f.Executor.RemoveObject(site.Object);
            f.Ui.OpenMap();
            long time = f.World.GameSeconds;
            var result = f.Ui.TravelTo(resident.Id, (long)site.Object, QueueMode.Immediate, TravelTargetKind.Object);
            ActionEntry action = f.Commands.GetCurrent(resident.Id);
            Check(result.Accepted && action != null && action.TargetId == (long)site.Object
                && action.TargetKind == TravelTargetKind.Object
                && Distance(start, f.Executor.GetResidentPosition(resident.Id)) == 0,
                "TravelEverySite submit-without-teleport-" + name + " SEIT-UI-03-P1");
            if (action == null) continue;
            f.Executor.Tick(StateWriter.ActivityExecutor);
            ExecutionRecord execution = f.Executor.GetExecution(action.Action);
            bool bounded = true;
            for (int tick = 0; tick < 2000 && execution != null && !execution.IsTerminal; tick++)
                bounded &= BoundedStep(f, resident.Id);
            Check(bounded && execution != null && execution.Outcome == TerminalOutcome.Completed
                && f.World.GameSeconds > time
                && Distance(f.Executor.GetResidentPosition(resident.Id), site.Entry) <= ActivityExecutor.ArrivalToleranceMeters,
                "TravelEverySite bounded-arrival-" + name + " SEIT-UI-03-P1");
        }
    }
    private static void UseSlotsAndFollow()
    {
        foreach (string name in Names)
        {
            // A fresh real world for each slot prevents holds leaking between cases.
            var catalog = new Fixture();
            foreach (Slot advertised in Find(catalog, name).Slots)
            {
                var f = new Fixture();
                Site site = Find(f, name);
                Slot slot = site.Slots.Find(s => s.Id == advertised.Id);
                var start = new ApproachPose(slot.Pose.X - 2f, slot.Pose.Y, slot.Pose.Z);
                ResidentRecord resident = f.Add("Same Name", name == "school" ? LifeStage.Child : LifeStage.Adult, start);
                ResidentRecord lookalike = f.Add("Same Name", LifeStage.Adult,
                    new ApproachPose(start.X + 0.5f, start.Y, start.Z));
                Check(f.Ui.FollowResident(resident.Id).Accepted, "UseSlotsAndFollow follow-start-" + name);
                if (name == "office" && slot.Id == "work-ai-engineer")
                {
                    // WORK owns AIEngineerShift execution; BLOCK-SITES owns its advertised destination.
                    Site office = Find(catalog, "office");
                    Check(slot.ActivityCode == "ACT-WORK-04" && slot.ObjectKind == "Computer"
                        && site.Id == office.Id && site.Object == office.Object
                        && Distance(site.Entry, office.Entry) == 0
                        && slot.Object == advertised.Object && slot.ActivityCode == advertised.ActivityCode
                        && Distance(slot.Pose, advertised.Pose) == 0,
                        "UseSlotsAndFollow advertised-activity-office/work-ai-engineer ACT-WORK-04");
                    f.Ui.StopFollowing();
                    Check(!f.Ui.FollowedResident.HasValue, "UseSlotsAndFollow explicit-stop-" + name);
                    continue;
                }
                // Removing a real target emulates an off-map placeholder, not an in-place activity.
                if (fault == "off-map-" + name) f.Executor.RemoveObject(slot.Object);
                if (fault == "unreachable-" + name) f.Executor.SetReachable(slot.Object, false);
                var result = f.Commands.Submit(new CommandEnvelope(f.World.AllocateCommand(Core), f.World.Epoch,
                    f.World.GameSeconds, CommandSource.PlayerHuman, resident.ResidentSequence + 1u,
                    CommandKind.DirectActivity, resident.Id, new ActivityId(slot.ActivityCode), (long)slot.Object,
                    new CommandParameters.Direct(QueueMode.Immediate)));
                ActionEntry action = f.Commands.GetCurrent(resident.Id);
                Check(result.Accepted && action != null && action.TargetId == (long)slot.Object,
                    "UseSlotsAndFollow targeted-activity-" + name + "/" + slot.Id);
                if (action == null) continue;
                f.Executor.Tick(StateWriter.ActivityExecutor);
                ExecutionRecord execution = f.Executor.GetExecution(action.Action);
                bool bounded = true, identity = true;
                int ticks = 0;
                while (ticks++ < 2000 && execution != null && !execution.IsTerminal && execution.Stage != ExecutionStage.Perform)
                {
                    bounded &= BoundedStep(f, resident.Id);
                    identity &= f.Ui.FollowedResident == resident.Id;
                }
                bool performing = execution != null && !execution.IsTerminal && execution.Stage == ExecutionStage.Perform;
                Check(performing && bounded && execution.TargetId == (long)slot.Object
                    && execution.Holds.Length >= 1 && execution.Holds[0].Object == slot.Object
                    && Distance(f.Executor.GetResidentPosition(resident.Id), slot.Pose) <= ActivityExecutor.ArrivalToleranceMeters,
                    "UseSlotsAndFollow usable-registered-slot-" + name + "/" + slot.Id + " SEIT-WORLD-01-P1");
                bool located = performing;
                // Five game minutes at work/study, with the look-alike crossing each tick.
                for (int tick = 0; tick < 50 && performing; tick++)
                {
                    ApproachPose position = f.Executor.GetResidentPosition(resident.Id);
                    f.Executor.SetResidentPosition(lookalike.Id,
                        new ApproachPose(position.X + (tick % 2 == 0 ? 0.5f : -0.5f), position.Y, position.Z));
                    int hash = f.World.ComputeStateHash();
                    f.Ui.Refresh();
                    ResidentId? observed = f.Ui.FollowedResident;
                    if (fault == "follow-retarget") observed = lookalike.Id;
                    identity &= observed == resident.Id && hash == f.World.ComputeStateHash()
                        && Distance(position, f.Executor.GetResidentPosition(resident.Id)) == 0;
                    bool found = false;
                    foreach (NavigationResident row in f.Ui.SearchByActivity(slot.ActivityCode))
                        if (row.Id == resident.Id && row.Activity.HasValue && row.Activity.Value.Code == slot.ActivityCode
                            && Distance(row.Position, position) < 0.0001) found = true;
                    located &= found && Distance(position, slot.Pose) <= ActivityExecutor.ArrivalToleranceMeters;
                    bounded &= BoundedStep(f, resident.Id);
                }
                Check(performing && identity && bounded,
                    "UseSlotsAndFollow retains-identity-through-work-study-" + name + "/" + slot.Id + " SEIT-UI-05-P1/N1/N2");
                Check(located, "UseSlotsAndFollow activity-search-site-position-" + name + "/" + slot.Id + " SEIT-UI-06-P1");
                f.Ui.StopFollowing();
                Check(!f.Ui.FollowedResident.HasValue, "UseSlotsAndFollow explicit-stop-" + name);
            }
            Check(Find(catalog, name).Slots.Count > 0, "UseSlotsAndFollow no-decorative-only-" + name);
        }
    }
    // M4-D3: placement/registration only; obstacle-aware runtime following belongs to LOD-NAV.
    private static void BlockWorldLayout()
    {
        const string layout = "Assets/Blockparty/Content/block-layout.json";
        Check(File.Exists(layout), "BLOCK-WORLD layout-present SEIT-WORLD-01-P1");
        if (!File.Exists(layout)) return;
        var f = new Fixture();
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(layout)))
        {
            var seen = new HashSet<string>();
            foreach (JsonElement row in doc.RootElement.GetProperty("sites").EnumerateArray())
            {
                string id = row.GetProperty("id").GetString();
                Site site = Find(f, id);
                JsonElement origin = row.GetProperty("origin"), entry = row.GetProperty("entry");
                Check(seen.Add(id) && site.AssetPath == row.GetProperty("asset").GetString()
                    && Distance(site.Origin, new ApproachPose(origin[0].GetSingle(), origin[1].GetSingle(), origin[2].GetSingle())) < 0.0001
                    && Distance(site.Entry, new ApproachPose(entry[0].GetSingle(), entry[1].GetSingle(), entry[2].GetSingle())) < 0.0001
                    && f.Executor.IsReachable(site.Object),
                    "BLOCK-WORLD address-and-entry-" + id + " SEIT-WORLD-01-P1");
            }
            Check(seen.SetEquals(new[] { "apartment", "common-room", "cafe", "workshop", "office", "school" })
                && f.Sites.Count == seen.Count, "BLOCK-WORLD every-module-at-address SEIT-WORLD-01-P1");
        }
    }
    private static void Cases()
    {
        Run("BlockWorldLayout", BlockWorldLayout);
        Run("RegistryAssetsAndPins", RegistryAssetsAndPins);
        Run("TravelEverySite", TravelEverySite);
        Run("UseSlotsAndFollow", UseSlotsAndFollow);
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "negative-fixture")
        {
            var faults = new List<string>();
            foreach (string name in Names)
            {
                faults.Add("missing-slot-" + name);
                faults.Add("unreachable-" + name);
                faults.Add("off-map-" + name);
            }
            foreach (string code in new[] { "ACT-WORK-03", "ACT-WORK-02", "ACT-WORK-04" })
                faults.Add("missing-office-" + code);
            faults.Add("follow-retarget");
            if (args.Length > 1)
            {
                if (!faults.Contains(args[1])) { Check(false, "unknown negative fixture " + args[1]); return 1; }
                faults = new List<string> { args[1] };
            }
            foreach (string name in faults)
            {
                fault = name;
                Console.WriteLine("BLOCK-SITES negative-fixture " + name);
                int before = failures;
                Cases();
                if (failures == before)
                {
                    Console.WriteLine("NEGATIVE FIXTURE ESCAPED " + name);
                    return 2;
                }
                Console.WriteLine("NEGATIVE FIXTURE DETECTED " + name);
            }
        }
        else Cases();
        if (failures != 0) return 1;
        Console.WriteLine("BLOCK-SITES ALL PASS");
        return 0;
    }
}
