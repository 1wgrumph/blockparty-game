/* REFINE@1 / #94 / M5-OD-10, Test-first red step. Method=test.
 * Inputs are SYNTHETIC; simulation, commands, executor and ledgers are real.
 * Existing BIRTH/STORM/ESTATE/TIERS/SELECT-ACTION test services separately.
 * This new suite tests the missing live composition, not whole REFINE/SEIT
 * acceptance or the HUMAN-PLAY/PERFORMANCE findings ledger.
 *
 * DEVELOPER DELIVERABLE CONTRACT (names/signatures fixed by this oracle):
 * Assets/Blockparty/Simulation/LiveSimulation.cs, Blockparty.Simulation:
 *   public static LiveSimulation Create(long seed, string tuningPath)
 *   public GameStack Game { get; } // actual shared live services, not a copy
 *   public void AdvanceRealSeconds(double seconds)
 *   public CommandResult Submit(string commandId, ResidentId actor, long target)
 * Create is the ordinary live bootstrap: exactly 8 living residents. It may
 * prepare mixed stages/progress, caregivers, consent, beds, vacant homes and
 * exposure, as AlphaDirector.RunLifeGates does. Tests do not arm life gates,
 * jump time, create births/deaths or call component Tick methods.
 * Submit ids:
 *   live.conceive: target=consenting other parent's ResidentId; real ACT-REL-06
 *     through FamilyCreation/CommandArbitrator/executor using the authored bed.
 *   live.move-household: target=HomeId; real Housing.MoveHousehold.
 *   ACT-HEALTH-02: target=0; ordinary RestRecover through Health/executor.
 * Seed 1234 uses BIRTH's real successful first conception draw, no forced RNG.
 * AlphaDirector.LiveTick MUST call this SAME AdvanceRealSeconds once per elapsed
 * slice, replacing its separate Clock/TickAll calls. Startup/reload must bind
 * the same GameStack/services; scene object registrations remain live.
 *
 * Consumed Assets/Blockparty/Content/tuning.json keys:
 * {live_play:{speed:"Normal",aging_preset:"Moderate"|"Accelerated",
 *   initial_world_age_days:<0..30>,transition_bound_days:7}}.
 * Seven days bounds this replay, not SDoc lifespan/storm/gestation thresholds.
 * Production chooses startup progress/vacancies to make the gates reachable.
 * Normal: .5 real seconds = 30 game seconds, pause advances nothing.
 *
 * evidence/production/REFINE/result.json:
 * {package:"REFINE",status:"PASS",synthetic:false,started_utc,ended_utc,
 *  active_hours,commit:<40 hex>,tree_hash:<40 hex>,
 *  live_session:{path:"evidence/production/REFINE/live-session.json",sha256}}.
 * Developer supplies real player live-session.json:
 * {synthetic:false,entry_point:"Blockparty.Simulation.LiveSimulation.AdvanceRealSeconds",
 *  commit,tree_hash,player_sha256:<64 hex>,log:{path,sha256},frames:[{path,sha256}]}.
 * Referenced log/frames prove AlphaDirector uses this entry after startup AND
 * reload, ordinary conception/housing/rest commands, all transitions, controls
 * (pause/selection/menu/travel), save/reload/restart and no duplicate effects.
 * All references are below evidence/production/REFINE/. Presentation is excluded
 * by run-standalone: independent live-session review must prove that wiring and
 * rendered behavior. R-13 checks binding, not the truth of rendered observations.
 *
 * Oracle ids: R-1 API/config/start; R-2 scheduler/clock; R-3 commands;
 * R-4 storm; R-5 birth; R-6 estate/care; R-7 scarcity; R-8 growth;
 * R-9 relocation; R-10 recovery; R-11 no duplicate effects; R-12 existing live
 * pause/menu Immediate+Append/travel/completion; R-13 evidence.
 * Local support for SOAK S-3/S-4, AC-07/19, LIFE-01, AUTO-01/02. Whole-case
 * acceptance, human playtest, performance and findings-ledger proof stay pending.
 * negative-fixture [name] validates labelled SYNTHETIC observation controls using
 * the same validator as real play; emits FAIL per rejection and exits 1.
 * Scheduler-negative also advances the real Clock without PopulationScheduler.
 * Default self-checks positive/negative controls without printing FAIL.
 * Resource isolated-csharp: no engine/display/network; engine 5+2 not applicable.
 * Seven days of fixed-tick execution may exceed 30s under load; runner has 180s.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Persistence;
using Blockparty.Simulation;
using Blockparty.UI;

public static class RefineChecks
{
    private const long Day = 86400;
    private const string TypeName = "Blockparty.Simulation.LiveSimulation";
    private const string Tuning = "Assets/Blockparty/Content/tuning.json";
    private static readonly string[] Transitions = { "storm", "birth", "estate", "care", "scarcity", "growth", "relocation", "recovery" };
    private static readonly string[] Ids = { "R-4.Storm", "R-5.Birth", "R-6.Estate", "R-6.Care", "R-7.Scarcity", "R-8.Growth", "R-9.Relocation", "R-10.Recovery" };
    private sealed class Rejection : Exception
    {
        public readonly string Id;
        public Rejection(string id, string detail) : base(detail) { Id = id; }
    }
    private static void Require(bool ok, string id, string detail)
    { if (!ok) throw new Rejection(id, detail); }
    private sealed class Observation
    {
        public readonly HashSet<string> Seen = new HashSet<string>();
        public bool Scheduler, Commands, DuplicateBirth, DuplicateEffects;
    }
    private static void Validate(Observation o)
    {
        Require(o.Scheduler, "R-2.Scheduler", "clock advanced but population needs never decayed");
        Require(o.Commands, "R-3.Commands", "conception/housing command absent or refused");
        for (int i = 0; i < Transitions.Length; i++)
            Require(o.Seen.Contains(Transitions[i]), Ids[i], "missing " + Transitions[i] + " within seven live game days");
        Require(!o.DuplicateBirth, "R-11.DuplicateBirth", "one conception produced duplicate birth effects");
        Require(!o.DuplicateEffects, "R-11.DuplicateEffects", "duplicate event, estate, care or completion effect");
    }
    private static JsonElement Read(string path)
    { using (var d = JsonDocument.Parse(File.ReadAllText(path))) return d.RootElement.Clone(); }
    private static string Text(JsonElement e, string key) { return e.GetProperty(key).GetString(); }
    private static string Hash(string path)
    { using (var sha = SHA256.Create()) using (var s = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    private sealed class Live
    {
        public readonly GameStack Game;
        private readonly object instance;
        private readonly MethodInfo advance, submit;
        public Live()
        {
            Type t = typeof(WorldState).Assembly.GetType(TypeName);
            Require(t != null, "R-1.EntryPoint", "missing " + TypeName + "; live population tick contract is absent");
            var create = t.GetMethod("Create", new[] { typeof(long), typeof(string) });
            advance = t.GetMethod("AdvanceRealSeconds", new[] { typeof(double) });
            submit = t.GetMethod("Submit", new[] { typeof(string), typeof(ResidentId), typeof(long) });
            var game = t.GetProperty("Game");
            Require(create != null && create.IsStatic && create.ReturnType == t && advance != null
                && advance.ReturnType == typeof(void) && submit != null && submit.ReturnType == typeof(CommandResult)
                && game != null && game.PropertyType == typeof(GameStack), "R-1.Contract", "deliverable API signature differs");
            Require(File.Exists(Tuning), "R-1.Tuning", "missing consumed tuning.json");
            var c = Read(Tuning).GetProperty("live_play");
            Require(Text(c, "speed") == "Normal" && (Text(c, "aging_preset") == "Moderate" || Text(c, "aging_preset") == "Accelerated")
                && c.GetProperty("transition_bound_days").GetInt32() == 7 && c.GetProperty("initial_world_age_days").GetDouble() >= 0
                && c.GetProperty("initial_world_age_days").GetDouble() <= 30, "R-1.Tuning", "live replay tuning contract");
            instance = create.Invoke(null, new object[] { 1234L, Path.GetFullPath(Tuning) });
            Game = (GameStack)game.GetValue(instance);
            Require(Game != null && Game.Family != null && Game.Housing != null, "R-1.Contract", "shared live services missing");
            Require(Game.Clock.Speed == SimulationSpeed.Normal && Game.Clock.Preset.ToString() == Text(c, "aging_preset")
                && Game.World.GameSeconds == c.GetProperty("initial_world_age_days").GetDouble() * Day,
                "R-1.TuningConsumer", "live factory did not consume configured clock/start age");
            int living = 0; foreach (var r in Game.World.ResidentsSnapshot()) if (r.IsAlive) living++;
            Require(living == 8, "R-1.Start", "ordinary live world must start with eight living residents; got " + living);
            Require(Game.World.Events.Count == 0 && Game.Family.Export().Births.Count == 0 && Game.World.EstateLedger.Count == 0,
                "R-1.Start", "transitions must occur after ticking, not be preloaded");
        }
        public void Advance(double seconds) { advance.Invoke(instance, new object[] { seconds }); }
        public CommandResult Submit(string id, ResidentId actor, long target)
        { return (CommandResult)submit.Invoke(instance, new object[] { id, actor, target }); }
    }
    private static ResidentRecord Person(GameStack g, ResidentId id)
    { ResidentRecord r; Require(g.World.TryGetResident(id, out r), "R-1.Resident", "resident disappeared"); return r; }
    private static Dictionary<ResidentId, float> Hunger(WorldState world)
    { var h = new Dictionary<ResidentId, float>(); foreach (var r in world.ResidentsSnapshot()) h.Add(r.Id, r.GetNeed(Need.Hunger)); return h; }
    private static bool SchedulerAdvanced(WorldState world, Dictionary<ResidentId, float> hunger)
    {
        int decayed = 0;
        foreach (var r in world.ResidentsSnapshot())
            if (r.IsAlive && hunger.ContainsKey(r.Id) && hunger[r.Id] > 0
                && r.GetNeed(Need.Hunger) < hunger[r.Id]) decayed++;
        return decayed == hunger.Count;
    }
    private static bool ExtraChildren(WorldState world, HashSet<ResidentId> initial)
    {
        int children = 0;
        foreach (var r in world.ResidentsSnapshot()) if (!initial.Contains(r.Id) && r.Stage <= LifeStage.Toddler) children++;
        return children > 1;
    }
    private static ActionInstanceId LatestAction(GameStack g, ResidentId resident, string code)
    {
        ActionInstanceId id = default(ActionInstanceId); var current = g.Arbitrator.GetCurrent(resident);
        if (current != null && current.Activity.Code == code) id = current.Action;
        foreach (var p in g.Arbitrator.GetPending(resident)) if (p.Activity.Code == code && (long)p.Action > (long)id) id = p.Action;
        return id;
    }
    private static void Play()
    {
        var live = new Live(); var g = live.Game; long start = g.World.GameSeconds;
        var hunger = Hunger(g.World); var stages = new Dictionary<ResidentId, LifeStage>();
        var initial = new HashSet<ResidentId>(); var originalGuardians = new Dictionary<ResidentId, HashSet<ResidentId>>();
        foreach (var r in g.World.ResidentsSnapshot())
        {
            stages.Add(r.Id, r.Stage); initial.Add(r.Id); originalGuardians[r.Id] = new HashSet<ResidentId>();
            foreach (var link in r.Family) if (link.Kind == "Parent") originalGuardians[r.Id].Add(link.Other);
        }
        foreach (var c in g.World.CareAssignments) originalGuardians[c.Dependent].Add(c.Guardian);
        live.Advance(.5);
        Require(g.World.GameSeconds == start + 30, "R-2.Clock", "Normal clock must advance exactly once");
        var o = new Observation { Scheduler = SchedulerAdvanced(g.World, hunger) };
        Require(o.Scheduler, "R-2.Scheduler", "clock advanced but population needs never decayed");
        ResidentId carrier = default(ResidentId), other = default(ResidentId), mover = default(ResidentId);
        foreach (var c in g.Family.Export().Capabilities)
        { if (c.CanCarry && (long)carrier == 0) carrier = (ResidentId)c.Resident;
          if (c.CanImpregnate && (long)other == 0) other = (ResidentId)c.Resident; }
        foreach (var r in g.World.ResidentsSnapshot())
            if (r.IsAlive && r.Id != carrier && r.Id != other && r.Stage == LifeStage.Adult) mover = r.Id;
        Require((long)carrier != 0 && (long)other != 0 && carrier != other && (long)mover != 0,
            "R-3.Commands", "startup needs eligible parents and a separate adult housing actor");
        HomeId destination = default(HomeId), oldHome = Person(g, mover).Home;
        HouseholdId movingHousehold = Person(g, mover).Household;
        var movingMembers = new HashSet<ResidentId>();
        foreach (var r in g.World.ResidentsSnapshot()) if (r.Household == movingHousehold) movingMembers.Add(r.Id);
        foreach (var h in g.Housing.Export().Homes)
        {
            bool occupied = false; HomeRecord home;
            foreach (var r in g.World.ResidentsSnapshot()) if ((long)r.Home == h.Home) occupied = true;
            if (!occupied && g.World.TryGetHome((HomeId)h.Home, out home) && home.BedCount >= movingMembers.Count)
            { destination = home.Id; break; }
        }
        Require((long)destination != 0, "R-3.Commands", "no authored relocation target");
        var conception = live.Submit("live.conceive", carrier, (long)other);
        var move = live.Submit("live.move-household", mover, (long)destination);
        o.Commands = conception.Accepted && move.Accepted;
        Require(o.Commands, "R-3.Commands", "live.conceive=" + conception.Accepted + "/" + conception.Reason
            + "; live.move-household=" + move.Accepted + "/" + move.Reason);
        bool relocated = oldHome != destination;
        foreach (var id in movingMembers) relocated &= Person(g, id).Home == destination && Person(g, id).Household == movingHousehold;
        HouseholdRecord movedHousehold;
        relocated &= g.World.TryGetHousehold(movingHousehold, out movedHousehold) && movedHousehold.Home == destination;
        if (relocated) o.Seen.Add("relocation");
        var sick = new HashSet<ResidentId>(); var restActions = new Dictionary<ResidentId, ActionInstanceId>();
        var eventIds = new HashSet<EventId>(); var birthdays = new HashSet<string>();
        var births = new HashSet<long>(); var conceptions = new HashSet<EventId>();
        var settledDeaths = new HashSet<ResidentId>(); int cursor = 0, stormCount = 0;
        while (g.World.GameSeconds < start + 7 * Day)
        {
            long before = g.World.GameSeconds; live.Advance(.5);
            Require(g.World.GameSeconds == before + 30, "R-2.Clock", "live clock skipped/doubled or stalled");
            foreach (var r in g.World.ResidentsSnapshot())
            {
                if (r.IsAlive && r.GetNeed(Need.Hunger) <= 10 && hunger.ContainsKey(r.Id) && hunger[r.Id] > 10) o.Seen.Add("scarcity");
                bool restDone = restActions.ContainsKey(r.Id) && g.Executor.GetExecution(restActions[r.Id]) != null
                    && g.Executor.GetExecution(restActions[r.Id]).Outcome == TerminalOutcome.Completed;
                if (r.Ill || r.Injured)
                {
                    sick.Add(r.Id);
                    if (r.IsAlive && (!restActions.ContainsKey(r.Id) || restDone))
                    { if (live.Submit("ACT-HEALTH-02", r.Id, 0).Accepted) restActions[r.Id] = LatestAction(g, r.Id, "ACT-HEALTH-02"); }
                }
                else if (r.IsAlive && sick.Contains(r.Id) && restDone && r.GetNeed(Need.Health) >= 40) o.Seen.Add("recovery");
            }
            while (cursor < g.World.Events.Count)
            {
                var e = g.World.Events[cursor++]; if (!eventIds.Add(e.Event)) o.DuplicateEffects = true;
                Require(e.GameSeconds > start && e.GameSeconds <= g.World.GameSeconds, "R-11.EventTime", "event outside live clock");
                if (e.Kind == "SevereStorm")
                {
                    if (++stormCount > 1) o.DuplicateEffects = true;
                    Require(g.World.Storm.ActiveEvent == (long)e.Event && g.World.Storm.ActiveEndGameSeconds > e.GameSeconds,
                        "R-4.Storm", "storm event without active weather state"); o.Seen.Add("storm");
                }
                if (e.Kind == "Birthday" && e.Actor.HasValue && stages.ContainsKey(e.Actor.Value))
                {
                    Require(Person(g, e.Actor.Value).Stage > stages[e.Actor.Value], "R-8.Growth", "birthday without stage growth");
                    if (!birthdays.Add((long)e.Actor.Value + "/" + e.Detail)) o.DuplicateEffects = true;
                    o.Seen.Add("growth");
                }
                if (e.Kind == "Birth")
                {
                    Require(e.Actor == carrier && e.Detail.StartsWith("child ", StringComparison.Ordinal), "R-5.Birth", "birth provenance");
                    long child = long.Parse(e.Detail.Substring(6), CultureInfo.InvariantCulture); var b = g.Family.GetBirth((ResidentId)child);
                    Require(b != null && b.BirthEvent == e.Event && b.Carrier == carrier && b.OtherParent == other
                        && b.Child == (ResidentId)child && Person(g, b.Child).IsAlive, "R-5.Birth", "real birth record/child absent");
                    if (!births.Add(child) || !conceptions.Add(b.ConceptionEvent)) o.DuplicateBirth = true;
                    WorldEvent conceived = null;
                    foreach (var c in g.World.Events) if (c.Event == b.ConceptionEvent && c.Kind == "Conception") conceived = c;
                    Require(conceived != null && conceived.Actor == carrier && e.GameSeconds - conceived.GameSeconds == 3 * Day,
                        "R-5.Birth", "delivery must follow real conception by exactly three days"); o.Seen.Add("birth");
                }
                if (e.Kind == "Death" && e.Actor.HasValue && !Person(g, e.Actor.Value).IsAlive && g.World.IsEstateSettled(e.Event))
                {
                    Require(Person(g, e.Actor.Value).Money == 0, "R-6.Estate", "settled estate did not distribute deceased money");
                    settledDeaths.Add(e.Actor.Value); o.Seen.Add("estate");
                }
                if (e.Kind == "CareTransfer" && e.Actor.HasValue)
                    foreach (var c in g.World.CareAssignments)
                        if (c.Dependent == e.Actor.Value && c.TransferEvent == e.Event && Person(g, c.Guardian).IsAlive
                            && (c.Placement.Household == Person(g, c.Dependent).Household || c.Placement.FacilityPlace > 0)
                            && originalGuardians.ContainsKey(c.Dependent))
                            foreach (var guardian in originalGuardians[c.Dependent]) if (settledDeaths.Contains(guardian)) o.Seen.Add("care");
            }
        }
        o.DuplicateBirth |= births.Count > 1 || g.Family.Export().Births.Count > 1 || ExtraChildren(g.World, initial);
        CheckUniqueEffects(g, o);
        int count = g.World.EventCount, effects = g.World.EffectLedger.Count, estates = g.World.EstateLedger.Count, state = g.World.ComputeStateHash();
        for (int i = 0; i < 3; i++) live.Advance(0);
        Require(g.World.EventCount == count && g.World.EffectLedger.Count == effects && g.World.EstateLedger.Count == estates
            && g.World.ComputeStateHash() == state, "R-11.ZeroTick", "zero elapsed time reapplied a live effect");
        Validate(o); foreach (string id in Ids) Console.WriteLine("PASS " + id);
    }
    private static void CheckUniqueEffects(GameStack g, Observation o)
    {
        var keys = new HashSet<string>();
        foreach (var e in g.World.EffectLedger)
            if (!keys.Add((long)e.Action + "/" + e.EffectKind + "/" + (long)e.Recipient.First + "/"
                + (e.Recipient.Second.HasValue ? ((long)e.Recipient.Second.Value).ToString(CultureInfo.InvariantCulture) : ""))) o.DuplicateEffects = true;
        keys.Clear(); foreach (var e in g.World.EstateLedger)
            if (!keys.Add((long)e.DeathEvent + "/" + e.AssetRef + "/" + e.Recipient)) o.DuplicateEffects = true;
        keys.Clear(); foreach (var c in g.World.CareAssignments)
            if (!keys.Add((long)c.TransferEvent + "/" + (long)c.Dependent)) o.DuplicateEffects = true;
    }
    private static void Compatibility()
    {
        var live = new Live(); var g = live.Game; var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false));
        var input = new InputMap(access, g.Clock);
        foreach (string screen in new[] { "Camera", "Follow", "Selection", "Map", "ActionMenu", "HudFocus", "MinigolfAim" })
        {
            input.EnterState(screen); Require(input.TryActivate(access.BindingFor("Pause")) && g.Clock.Paused, "R-12.Pause", screen);
            long time = g.World.GameSeconds; int hash = g.World.ComputeStateHash(); live.Advance(2);
            Require(g.World.GameSeconds == time && g.World.ComputeStateHash() == hash, "R-12.Pause", "paused world changed in " + screen);
            Require(input.TryActivate(access.BindingFor("Pause")) && !g.Clock.Paused, "R-12.Pause", "resume " + screen);
        }
        ResidentId actor = default(ResidentId);
        foreach (var r in g.World.ResidentsSnapshot()) if (r.IsAlive && r.Stage == LifeStage.Adult) { actor = r.Id; break; }
        Require((long)actor != 0, "R-12.Menu", "adult actor absent");
        // Separate compatibility fixture adds only a real reachable action object.
        ObjectId target = g.World.AllocateObject(StateWriter.SimCore); var pose = g.Executor.GetResidentPosition(actor);
        g.Executor.RegisterObject(target, new[] { new ApproachPose(pose.X + 2, pose.Y, pose.Z) });
        var activity = new ActivityId("ACT-HEALTH-02"); var menu = new ActionMenu(g.World, g.Arbitrator, access, input);
        menu.Open(actor, target, "synthetic rest seat", new[] { new AvailableAction(activity, (long)target) });
        Require(menu.Choose(activity, QueueMode.Immediate).Accepted, "R-12.Menu", "Immediate command refused");
        Require(menu.Choose(activity, QueueMode.Append).Accepted, "R-12.Menu", "Append command refused");
        var actions = new HashSet<ActionInstanceId>(); var current = g.Arbitrator.GetCurrent(actor);
        if (current != null && current.Activity.Code == activity.Code) actions.Add(current.Action);
        foreach (var p in g.Arbitrator.GetPending(actor)) if (p.Activity.Code == activity.Code) actions.Add(p.Action);
        Require(actions.Count == 2, "R-12.Queue", "Immediate/Append lost or duplicated a player action");
        bool moved = false, completed = false;
        for (int i = 0; i < 3600; i++)
        {
            live.Advance(.5); var now = g.Executor.GetResidentPosition(actor); moved |= now.X != pose.X || now.Z != pose.Z; int done = 0;
            foreach (var a in actions) { var e = g.Executor.GetExecution(a); if (e != null && e.Outcome == TerminalOutcome.Completed) done++; }
            if (done == 2) { completed = true; break; }
        }
        Require(moved && completed, "R-12.Progress", "menu commands did not travel and complete under the live tick");
        var o = new Observation(); CheckUniqueEffects(g, o); Require(!o.DuplicateEffects, "R-12.Effects", "duplicate completion effects");
    }
    private static string Reference(JsonElement r)
    {
        string p = Text(r, "path");
        Require(p.StartsWith("evidence/production/REFINE/", StringComparison.Ordinal) && !p.Contains("..")
            && File.Exists(p) && Hash(p) == Text(r, "sha256"), "R-13.Reference", "missing/mismatched REFINE evidence " + p); return p;
    }
    private static bool Hex(string s, int length)
    { if (s == null || s.Length != length) return false; foreach (char c in s) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
    private static void Evidence()
    {
        const string path = "evidence/production/REFINE/result.json";
        Require(File.Exists(path), "R-13.Evidence", "developer result/live-session evidence absent"); var r = Read(path);
        Require(Text(r, "package") == "REFINE" && Text(r, "status") == "PASS" && !r.GetProperty("synthetic").GetBoolean()
            && double.IsFinite(r.GetProperty("active_hours").GetDouble()) && r.GetProperty("active_hours").GetDouble() >= 0
            && DateTimeOffset.Parse(Text(r, "ended_utc"), CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(Text(r, "started_utc"), CultureInfo.InvariantCulture)
            && Hex(Text(r, "commit"), 40) && Hex(Text(r, "tree_hash"), 40), "R-13.Result", "real passing package result required");
        var session = Read(Reference(r.GetProperty("live_session")));
        Require(!session.GetProperty("synthetic").GetBoolean() && Text(session, "entry_point") == TypeName + ".AdvanceRealSeconds"
            && Text(session, "commit") == Text(r, "commit") && Text(session, "tree_hash") == Text(r, "tree_hash")
            && Hex(Text(session, "player_sha256"), 64), "R-13.Binding", "live-session source/entry-point binding differs");
        Reference(session.GetProperty("log")); Require(session.GetProperty("frames").GetArrayLength() > 0, "R-13.Frames", "frame evidence missing");
        foreach (var frame in session.GetProperty("frames").EnumerateArray()) Reference(frame);
    }
    private static Observation Fixture(JsonElement f)
    {
        Require(f.GetProperty("synthetic").GetBoolean(), "R-0.Fixture", "synthetic marker required");
        var o = new Observation { Scheduler = f.GetProperty("scheduler").GetBoolean(), Commands = f.GetProperty("commands").GetBoolean(),
            DuplicateBirth = f.GetProperty("duplicate_birth").GetBoolean(), DuplicateEffects = f.GetProperty("duplicate_effects").GetBoolean() };
        foreach (var e in f.GetProperty("transitions").EnumerateArray()) o.Seen.Add(e.GetString()); return o;
    }
    private static int Fixtures(bool negative, string selected)
    {
        var fixtures = Read("tests/production/fixtures/REFINE/observations.json"); int rejected = 0;
        foreach (var f in fixtures.EnumerateArray())
        {
            string name = Text(f, "name"), expected = Text(f, "reject");
            if ((selected != null && selected != name) || (negative && expected == "")) continue; var o = Fixture(f);
            if (name == "scheduler-never-ticked")
            {
                var world = WorldState.CreateSeeded(94, 1234, 8, null); var hunger = Hunger(world);
                new Clock(world).AdvanceRealSeconds(StateWriter.SimCore, 1);
                Require(world.GameSeconds == 60, "R-0.Fixture", "real Clock negative setup failed"); o.Scheduler = SchedulerAdvanced(world, hunger);
            }
            if (name == "duplicate-birth")
            {
                // Deliberate synthetic corruption of real resident state, as in BIRTH's negative control.
                var world = WorldState.CreateSeeded(94, 1234, 8, null); var initial = new HashSet<ResidentId>();
                foreach (var r in world.ResidentsSnapshot()) initial.Add(r.Id);
                var parent = world.ResidentsSnapshot()[0];
                world.AllocateResident(StateWriter.SimCore, "Synthetic child A", LifeStage.Newborn, parent.Household, parent.Home);
                world.AllocateResident(StateWriter.SimCore, "Synthetic child B", LifeStage.Newborn, parent.Household, parent.Home);
                o.DuplicateBirth = ExtraChildren(world, initial);
            }
            string actual = ""; try { Validate(o); } catch (Rejection e) { actual = e.Id; }
            Require(actual == expected, "R-0.Fixture", name + " expected " + expected + " got " + actual);
            if (actual != "") { rejected++; if (negative) Console.WriteLine("FAIL " + actual + " SYNTHETIC negative-fixture " + name + " (expected rejection)"); }
        }
        Require(rejected > 0, "R-0.Fixture", "no negative fixtures selected"); return negative ? 1 : 0;
    }
    private static int Run(string id, Action test)
    {
        try { test(); Console.WriteLine("PASS " + id); return 0; }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Console.WriteLine("FAIL " + (e is Rejection r ? r.Id : id) + ": " + e.Message); return 1;
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "negative-fixture")
        { try { return Fixtures(true, args.Length > 1 ? args[1] : null); } catch (Exception e) { Console.WriteLine("FAIL R-0.Fixture: " + e.Message); return 2; } }
        int failures = Run("R-0.Fixtures", () => Fixtures(false, null));
        int play = Run("R-1..11.LivePlay", Play); failures += play;
        if (play == 0) { failures += Run("R-12.Compatibility", Compatibility); failures += Run("R-13.Evidence", Evidence); }
        if (failures == 0) Console.WriteLine("REFINE ALL PASS"); return failures == 0 ? 0 : 1;
    }
}
