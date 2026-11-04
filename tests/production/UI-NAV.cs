/*
UI-NAV@1 test-first API contract. Product write: Assets/Blockparty/UI/WorldNavigation.cs.
All new types below belong to Blockparty.UI, are plain C# 9, and use no UnityEngine.

WorldNavigation(WorldState world, CommandArbitrator commands, ActivityExecutor executor)
  bool IsMapOpen { get; }
  ResidentId? SelectedResident { get; }, FollowedResident { get; }
  string Message { get; } // Plain no-match/refusal/death notice for the view.
  void OpenMap(), CloseMap(), StopFollowing(), Refresh()
  IReadOnlyList<NavigationResident> SearchByName(string name)
  IReadOnlyList<NavigationResident> SearchByActivity(string nameOrCode)
    // Cooking and ACT-FOOD-01 resolve the same currently performing activity.
  CommandResult SelectResident(ResidentId id), FollowResident(ResidentId id)
  CommandResult TravelTo(ResidentId traveller, long targetId, QueueMode mode)
NavigationResident (snapshot): ResidentId Id; string DisplayName; HomeId Home;
  ApproachPose Position; ActivityId? Activity.

Search reads WorldState + NAV runtime data, never a visible-entity list or SAM.
Selection/follow/map/Refresh are presentation-only; TravelTo preflights the target
and reachability, then uses COMMAND/NAV, never SetResidentPosition for movement.
Refusals return a typed reason without changing simulation or command queues.
Refresh observes identity/name<home> and ends follow on death with notice.
New controller instances start with no map/selection/follow state (domain reload off).

Coverage is LOCAL only. Case IDs in check names are traceability, not completion
of their Unity/packaged-build oracles. Object display-name/location metadata,
off-map return context, camera rendering, SAM fault injection, inspection and
notifications are not supplied by these consumed APIs; gaps are in red.log.
Resource group: none (Roslyn/netcorerun only; no Unity editor/display/server).
Fixture: real WorldState seed 42, real COMMAND/NAV; no test doubles or I/O.
*/
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

public static class UiNavChecks
{
    private const StateWriter Core = StateWriter.SimCore;
    private static int failures;

    private sealed class Fixture
    {
        public readonly WorldState World = WorldState.CreateSeeded(7L, 42L, 0, null);
        public readonly Clock Clock;
        public readonly CommandArbitrator Commands;
        public readonly ActivityExecutor Executor;
        public readonly WorldNavigation Ui;
        public Fixture()
        {
            Clock = new Clock(World);
            Commands = new CommandArbitrator(World, Clock);
            Executor = new ActivityExecutor(World, Commands, Clock);
            Ui = new WorldNavigation(World, Commands, Executor);
        }
        public ResidentRecord Add(string name, float x)
        {
            HomeRecord home = World.AllocateHome(Core, 8);
            HouseholdRecord house = World.AllocateHousehold(Core, name + " home", home.Id);
            ResidentRecord resident = World.AllocateResident(Core, name, LifeStage.Adult, house.Id, home.Id);
            Executor.SetResidentPosition(resident.Id, new ApproachPose(x, 0.0f, 0.0f));
            return resident;
        }
        public ObjectId Object(float x, bool reachable)
        {
            // NAV's bare-long target namespace prefers objects: keep fixture IDs disjoint.
            ObjectId id;
            do { id = World.AllocateObject(Core); } while ((long)id <= World.ResidentCount);
            Executor.RegisterObject(id, new ApproachPose[] { new ApproachPose(x, 0.0f, 0.0f) });
            Executor.SetReachable(id, reachable);
            return id;
        }
        public void Step()
        {
            Clock.AdvanceRealSeconds(Core, 0.1);
            Executor.Tick(StateWriter.ActivityExecutor);
            Ui.Refresh();
        }
        public CommandResult Direct(ResidentId id, string code, long? target)
        {
            ResidentRecord current;
            World.TryGetResident(id, out current);
            return Commands.Submit(new CommandEnvelope(World.AllocateCommand(Core), World.Epoch,
                World.GameSeconds, CommandSource.PlayerHuman, current.ResidentSequence + 1u,
                CommandKind.DirectActivity, id, new ActivityId(code), target,
                new CommandParameters.Direct(QueueMode.Immediate)));
        }
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) failures++;
    }

    private static double Distance(ApproachPose a, ApproachPose b)
    {
        double x = (double)a.X - (double)b.X;
        double y = (double)a.Y - (double)b.Y;
        double z = (double)a.Z - (double)b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static string Simulation(Fixture f)
    {
        StringBuilder s = new StringBuilder();
        s.Append(f.World.ComputeStateHash()).Append('/').Append(f.World.RngState)
            .Append('/').Append(f.World.GameSeconds).Append('/').Append(f.Clock.Speed)
            .Append('/').Append(f.Clock.Paused).Append('/').Append(f.Clock.TotalTicks)
            .Append('/').Append(f.Clock.TickRemainder).Append('/').Append(f.World.NextCommandId)
            .Append('/').Append(f.World.NextActionId).Append('/').Append(f.Commands.WorldSequence);
        foreach (ResidentRecord resident in f.World.ResidentsSnapshot())
        {
            ApproachPose p = f.Executor.GetResidentPosition(resident.Id);
            s.Append('|').Append((long)resident.Id).Append(':').Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z);
            ActionEntry current = f.Commands.GetCurrent(resident.Id);
            if (current != null) AppendAction(s, current);
            s.Append(" pending:");
            foreach (ActionEntry pending in f.Commands.GetPending(resident.Id)) AppendAction(s, pending);
        }
        return s.ToString();
    }

    private static void AppendAction(StringBuilder s, ActionEntry entry)
    {
        s.Append('[').Append((long)entry.Action).Append(':').Append(entry.Kind)
            .Append(':').Append(entry.Activity.Code).Append(':').Append(entry.TargetId).Append(']');
    }

    private static bool Match(NavigationResident row, ResidentRecord resident, ApproachPose position)
    {
        return row.Id == resident.Id && row.DisplayName == resident.DisplayName
            && row.Home == resident.Home && Distance(row.Position, position) < 0.00001;
    }

    private static void MapIsPresentationOnly()
    {
        foreach (bool paused in new bool[] { false, true })
        {
            Fixture f = new Fixture();
            f.Add("A", 1.0f); f.Add("B", 9.0f);
            f.Clock.SetSpeed(SimulationSpeed.Fast);
            if (paused) f.Clock.Pause();
            string before = Simulation(f);
            f.Ui.OpenMap();
            bool opened = f.Ui.IsMapOpen;
            f.Ui.CloseMap();
            Check(opened && !f.Ui.IsMapOpen && before == Simulation(f),
                "SEIT-UI-03-N1 map-open-close-preserves-clock-and-all-positions-" + paused);
        }
    }

    private static string Travel(bool residentTarget)
    {
        Fixture f = new Fixture();
        ResidentRecord walker = f.Add("Walker", 0.0f);
        ResidentRecord destination = f.Add("Destination", 60.0f);
        long target = residentTarget ? (long)destination.Id : (long)f.Object(60.0f, true);
        if (residentTarget)
        {
            IReadOnlyList<NavigationResident> rows = f.Ui.SearchByName("Destination");
            Check(rows.Count == 1 && Match(rows[0], destination, f.Executor.GetResidentPosition(destination.Id)),
                "SEIT-IF-RESOLVE-P1 search-resolves-destination-id-before-travel");
            if (rows.Count != 1) return "missing search result";
            target = (long)rows[0].Id;
        }
        f.Ui.OpenMap();
        ApproachPose initial = f.Executor.GetResidentPosition(walker.Id);
        long start = f.World.GameSeconds;
        uint sequence = walker.ResidentSequence;
        CommandResult result = f.Ui.TravelTo(walker.Id, target, QueueMode.Immediate);
        ActionEntry action = f.Commands.GetCurrent(walker.Id);
        Check(result.Accepted && action != null && action.Kind == CommandKind.TravelTo
            && action.TargetId == target && walker.ResidentSequence == sequence + 1u
            && Distance(initial, f.Executor.GetResidentPosition(walker.Id)) == 0.0
            && f.World.GameSeconds == start,
            "SEIT-UI-03-P1 SEIT-IF-RESOLVE-N3 map-submits-one-command-without-relocation-" + residentTarget);
        if (action == null) return "missing travel command";
        f.Executor.Tick(StateWriter.ActivityExecutor);
        ExecutionRecord record = f.Executor.GetExecution(action.Action);
        bool bounded = true;
        StringBuilder trace = new StringBuilder();
        for (int i = 0; i < 700 && record != null && !record.IsTerminal; i++)
        {
            ApproachPose before = f.Executor.GetResidentPosition(walker.Id);
            long time = f.World.GameSeconds;
            f.Step();
            ApproachPose after = f.Executor.GetResidentPosition(walker.Id);
            double bound = ActivityExecutor.TravelMetersPerGameMinute
                * (double)(f.World.GameSeconds - time) / 60.0;
            bounded &= Distance(before, after) <= bound + 0.00001;
            trace.Append(after.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        }
        int completions = 0;
        if (record != null)
            foreach (StageRecord stage in record.Trace)
                if (stage.Stage == ExecutionStage.Complete && !stage.Skipped) completions++;
        Check(bounded && record != null && record.Outcome == TerminalOutcome.Completed
            && completions == 1 && f.World.GameSeconds > start
            && Distance(f.Executor.GetResidentPosition(walker.Id), new ApproachPose(60.0f, 0.0f, 0.0f))
                <= ActivityExecutor.ArrivalToleranceMeters
            && f.Commands.GetCurrent(walker.Id) == null && f.Commands.GetPending(walker.Id).Length == 0,
            "SEIT-UI-03-P1 SEIT-IF-RESOLVE-P1 SEIT-IF-RESOLVE-N3 bounded-travel-arrives-once-" + residentTarget);
        f.Step();
        Check(record != null && record.Trace.Count > 0 && f.Commands.GetCurrent(walker.Id) == null
            && walker.ResidentSequence == sequence + 1u,
            "SEIT-IF-RESOLVE-P1 completed-map-command-not-reissued-" + residentTarget);
        return trace.ToString();
    }

    private static void CollidingTargetKinds()
    {
        var f = new Fixture();
        var destination = f.Add("Destination", 12f);
        var walker = f.Add("Walker", 0f);
        var collision = f.World.AllocateObject(Core);
        f.Executor.RegisterObject(collision, new[] { new ApproachPose(50f, 0f, 0f) });
        string before = Simulation(f);
        var ambiguous = f.Ui.TravelTo(walker.Id, (long)destination.Id, QueueMode.Immediate);
        Check(!ambiguous.Accepted && ambiguous.Reason == RejectReason.InvalidParameters
            && before == Simulation(f), "R13-ambiguous-map-target-refused-unchanged");
        f.Executor.SetReachable(collision, false);
        f.Ui.SelectResident(destination.Id);
        Check(f.Ui.TravelTo(walker.Id, (long)destination.Id, QueueMode.Immediate).Accepted
            && f.Commands.GetCurrent(walker.Id).TargetKind == TravelTargetKind.Resident,
            "R13-selection-preserves-resident-kind-over-unreachable-object");
        before = Simulation(f);
        var missingObject = f.Ui.TravelTo(walker.Id, (long)walker.Id, QueueMode.Immediate, TravelTargetKind.Object);
        Check(!missingObject.Accepted && missingObject.Reason == RejectReason.TargetMissing
            && before == Simulation(f), "R13-explicit-missing-object-never-falls-back-to-resident");
    }

    private static void RefusedTravel()
    {
        foreach (string fault in new string[] { "enclosed", "missing-target", "missing-resident", "deceased" })
        {
            Fixture f = new Fixture();
            ResidentRecord walker = f.Add("Walker", 0.0f);
            f.Add("Bystander", 12.0f);
            ObjectId target = f.Object(60.0f, fault != "enclosed");
            ResidentId id = fault == "missing-resident" ? (ResidentId)999999L : walker.Id;
            long destination = fault == "missing-target" ? 999998L : (long)target;
            if (fault == "deceased") f.World.SetAlive(Core, walker.Id, false);
            // A refused Immediate must not cancel an existing action either.
            if (fault == "enclosed") f.Direct(walker.Id, "ACT-HOME-01", null);
            string before = Simulation(f);
            CommandResult result = f.Ui.TravelTo(id, destination, QueueMode.Immediate);
            RejectReason reason = fault == "enclosed" ? RejectReason.NoPath
                : fault == "deceased" ? RejectReason.ResidentDeceased
                : fault == "missing-target" ? RejectReason.TargetMissing : RejectReason.InvalidParameters;
            Check(!result.Accepted && result.Reason == reason && before == Simulation(f)
                && !string.IsNullOrWhiteSpace(f.Ui.Message),
                "SEIT-UI-03-N2 SEIT-IF-RESOLVE-N7 refused-travel-preserves-state-" + fault);
        }
    }

    private static void SearchAndSelection()
    {
        Fixture f = new Fixture();
        ResidentRecord first = f.Add("Sam", 3.0f);
        ResidentRecord second = f.Add("Sam", 19.0f);
        f.Add("Other", 31.0f);
        string before = Simulation(f);
        IReadOnlyList<NavigationResident> rows = f.Ui.SearchByName("Sam");
        bool firstFound = false, secondFound = false;
        foreach (NavigationResident row in rows)
        {
            firstFound |= Match(row, first, f.Executor.GetResidentPosition(first.Id));
            secondFound |= Match(row, second, f.Executor.GetResidentPosition(second.Id));
        }
        Check(rows.Count == 2 && firstFound && secondFound && before == Simulation(f),
            "SEIT-IF-RESOLVE-N1 SEIT-IF-RESOLVE-N4 SEIT-UI-06-N3 offscreen-duplicate-names-have-distinct-id-home-position");
        Check(f.Ui.SelectResident(first.Id).Accepted && f.Ui.SelectedResident == first.Id
            && f.Ui.FollowResident(first.Id).Accepted && f.Ui.FollowedResident == first.Id
            && f.Ui.SelectResident(second.Id).Accepted && f.Ui.SelectedResident == second.Id
            && f.Ui.FollowResident(second.Id).Accepted && f.Ui.FollowedResident == second.Id
            && before == Simulation(f),
            "SEIT-IF-RESOLVE-P2 SEIT-IF-RESOLVE-N4 selection-and-follow-bind-each-exact-id");
        IReadOnlyList<NavigationResident> none = f.Ui.SearchByName("Absent resident");
        Check(none.Count == 0 && !f.Ui.SelectedResident.HasValue && f.Ui.FollowedResident == second.Id
            && f.Ui.Message != null && f.Ui.Message.IndexOf("no match", StringComparison.OrdinalIgnoreCase) >= 0
            && before == Simulation(f),
            "SEIT-UI-06-N1 SEIT-IF-RESOLVE-N6 no-match-clears-selection-leaves-follow-and-simulation");
        ResidentId? follow = f.Ui.FollowedResident;
        CommandResult invalid = f.Ui.FollowResident((ResidentId)999999L);
        CommandResult invalidSelect = f.Ui.SelectResident((ResidentId)999999L);
        Check(!invalid.Accepted && invalid.Reason == RejectReason.InvalidParameters
            && !invalidSelect.Accepted && invalidSelect.Reason == RejectReason.InvalidParameters
            && f.Ui.FollowedResident == follow && !f.Ui.SelectedResident.HasValue && before == Simulation(f),
            "SEIT-IF-RESOLVE-N4 unknown-id-refused-without-cross-binding");
        f.World.RenameResident(Core, second.Id, "Renamed");
        f.Ui.Refresh();
        rows = f.Ui.SearchByName("Renamed");
        Check(rows.Count == 1 && Match(rows[0], second, f.Executor.GetResidentPosition(second.Id))
            && f.Ui.FollowedResident == second.Id && f.Ui.SearchByName("Sam").Count == 1,
            "SEIT-IF-RESOLVE-P2 SEIT-UI-06-P1 search-refreshes-authoritative-name-with-stable-id");
    }

    private static void ActivitySearch()
    {
        Fixture f = new Fixture();
        ResidentRecord first = f.Add("R7", 3.0f);
        ResidentRecord second = f.Add("R8", 19.0f);
        f.Add("Idle", 31.0f);
        ObjectId stoveA = f.Object(3.0f, true), stoveB = f.Object(19.0f, true);
        bool setup = f.Direct(first.Id, "ACT-FOOD-01", (long)stoveA).Accepted
            && f.Direct(second.Id, "ACT-FOOD-01", (long)stoveB).Accepted;
        for (int i = 0; i < 3; i++) f.Step();
        Check(setup && f.Executor.GetResidentStatus(first.Id).Execution.Stage == ExecutionStage.Perform
            && f.Executor.GetResidentStatus(second.Id).Execution.Stage == ExecutionStage.Perform,
            "SEIT-UI-06-P1 real-nav-cooking-fixture");
        string before = Simulation(f);
        foreach (string query in new string[] { "Cooking", "ACT-FOOD-01" })
        {
            IReadOnlyList<NavigationResident> rows = f.Ui.SearchByActivity(query);
            bool a = false, b = false;
            foreach (NavigationResident row in rows)
            {
                bool cooking = row.Activity.HasValue && row.Activity.Value.Code == "ACT-FOOD-01";
                a |= cooking && Match(row, first, f.Executor.GetResidentPosition(first.Id));
                b |= cooking && Match(row, second, f.Executor.GetResidentPosition(second.Id));
            }
            Check(rows.Count == 2 && a && b && before == Simulation(f),
                "SEIT-UI-06-P1 all-performing-residents-found-with-current-location-" + query);
        }
        IReadOnlyList<NavigationResident> named = f.Ui.SearchByName("R7");
        Check(named.Count == 1 && Match(named[0], first, f.Executor.GetResidentPosition(first.Id)),
            "SEIT-UI-06-P1 SEIT-IF-RESOLVE-N1 named-cook-found-without-any-view-registration");
        f.Direct(first.Id, "ACT-HOME-01", null);
        for (int i = 0; i < 3; i++) f.Step();
        IReadOnlyList<NavigationResident> remaining = f.Ui.SearchByActivity("Cooking");
        Check(remaining.Count == 1 && remaining[0].Id == second.Id,
            "SEIT-UI-06-P1 search-does-not-return-stale-activity");
    }

    private static void FollowIdentity()
    {
        Fixture f = new Fixture();
        ResidentRecord followed = f.Add("Sam", 0.0f);
        ResidentRecord lookalike = f.Add("Sam", -0.5f);
        ResidentRecord b = f.Add("B", -2.0f), c = f.Add("C", -3.0f);
        Check(f.Ui.FollowResident(followed.Id).Accepted, "SEIT-UI-05-P1 follow-accepted");
        bool stable = true;
        for (int i = 0; i < 50; i++)
        {
            // Real NAV-owned positions simulate crossings; UI never receives screen coordinates.
            float x = -0.5f + (float)i * 0.02f;
            f.Executor.SetResidentPosition(lookalike.Id, new ApproachPose(x, 0.0f, 0.0f));
            f.Executor.SetResidentPosition(b.Id, new ApproachPose(-x, 0.0f, 0.0f));
            f.Executor.SetResidentPosition(c.Id, new ApproachPose(x, 0.0f, 0.5f));
            f.Step();
            stable &= f.Ui.FollowedResident == followed.Id;
        }
        Check(stable && f.World.GameSeconds == 300L,
            "SEIT-UI-05-P1 SEIT-UI-05-N1 SEIT-IF-RESOLVE-N2 five-game-minutes-crossings-do-not-retarget");
        HomeRecord home = f.World.AllocateHome(Core, 8);
        HouseholdRecord house = f.World.AllocateHousehold(Core, "New household", home.Id);
        f.World.SetHousehold(Core, followed.Id, house.Id, home.Id);
        f.Ui.Refresh();
        Check(f.Ui.FollowedResident == followed.Id,
            "SEIT-IF-RESOLVE-N8 household-move-keeps-followed-id");
        f.Clock.Pause();
        string before = Simulation(f);
        f.Ui.StopFollowing();
        f.Ui.FollowResident(followed.Id);
        f.Ui.StopFollowing();
        Check(!f.Ui.FollowedResident.HasValue && before == Simulation(f),
            "SEIT-UI-05-N4 paused-follow-stop-preserves-simulation");
        f.Ui.FollowResident(followed.Id);
        f.World.SetAlive(Core, followed.Id, false);
        before = Simulation(f);
        f.Ui.Refresh();
        Check(!f.Ui.FollowedResident.HasValue && f.Ui.Message != null
            && f.Ui.Message.Contains("Sam") && before == Simulation(f),
            "SEIT-UI-05-N3 death-ends-follow-with-named-notice-without-retarget");
    }

    private static bool HasStaticMutable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!field.IsInitOnly && !field.IsLiteral) return true;
        return false;
    }

    private static void BlockWorldPins()
    {
        var f = new Fixture();
        ResidentRecord resident = f.Add("Map walker", 0f);
        var sites = new BlockSites(f.World, f.Executor);
        var ui = new WorldNavigation(f.World, f.Commands, f.Executor, sites);
        var expected = new HashSet<string> { "apartment", "common-room", "cafe", "workshop", "office", "school" };
        var seen = new HashSet<string>();
        string before = Simulation(f);
        ui.OpenMap();
        foreach (MapPin pin in ui.MapPins)
        {
            BlockSite site = null;
            foreach (BlockSite row in sites.Sites) if (row.Id == pin.Id) site = row;
            Check(seen.Add(pin.Id) && site != null && pin.Object == site.Object && Distance(pin.Entry, site.Entry) == 0,
                "BLOCK-WORLD map-address-" + pin.Id + " SEIT-WORLD-01-P1");
        }
        ui.CloseMap();
        Check(seen.SetEquals(expected) && before == Simulation(f),
            "BLOCK-WORLD six-pins-presentation-only SEIT-WORLD-01-P1 SEIT-UI-03-N1");
        Check(f.Ui.MapPins.Count == 0, "BLOCK-WORLD legacy-controller-without-registry");
        foreach (MapPin pin in ui.MapPins)
        {
            f.Executor.SetReachable(pin.Object, false);
            before = Simulation(f);
            CommandResult denied = ui.TravelTo(resident.Id, (long)pin.Object, QueueMode.Immediate, TravelTargetKind.Object);
            Check(!denied.Accepted && denied.Reason == RejectReason.NoPath && before == Simulation(f),
                "BLOCK-WORLD unreachable-pin-no-mutation-" + pin.Id + " BP-UI-03 (local refusal)");
            f.Executor.SetReachable(pin.Object, true);
            ApproachPose position = f.Executor.GetResidentPosition(resident.Id);
            CommandResult accepted = ui.TravelTo(resident.Id, (long)pin.Object, QueueMode.Immediate, TravelTargetKind.Object);
            ActionEntry action = f.Commands.GetCurrent(resident.Id);
            Check(accepted.Accepted && action != null && action.TargetId == (long)pin.Object
                && action.TargetKind == TravelTargetKind.Object && Distance(position, f.Executor.GetResidentPosition(resident.Id)) == 0,
                "BLOCK-WORLD pin-submits-object-travel-" + pin.Id + " SEIT-UI-03-P1");
        }
    }

    public static int Main()
    {
        BlockWorldPins();
        CollidingTargetKinds();
        MapIsPresentationOnly();
        string firstRun = Travel(false);
        string secondRun = Travel(false);
        Check(firstRun == secondRun && firstRun.Length > 100,
            "SEIT-UI-03-P1 repeated-seeded-run-identical-position-trace");
        Travel(true);
        RefusedTravel();
        SearchAndSelection();
        ActivitySearch();
        FollowIdentity();
        Fixture old = new Fixture();
        ResidentRecord oldResident = old.Add("Previous play", 0.0f);
        old.Ui.OpenMap(); old.Ui.SelectResident(oldResident.Id); old.Ui.FollowResident(oldResident.Id);
        Fixture fresh = new Fixture();
        fresh.Add("Next play", 0.0f);
        Check(!fresh.Ui.IsMapOpen && !fresh.Ui.SelectedResident.HasValue && !fresh.Ui.FollowedResident.HasValue
            && fresh.Ui.SearchByName("Previous play").Count == 0
            && old.Ui.IsMapOpen && old.Ui.FollowedResident == oldResident.Id,
            "SEIT-UI-05-N4 repeated-play-entry-presentation-is-instance-owned");
        Check(!HasStaticMutable(typeof(WorldNavigation)) && !HasStaticMutable(typeof(NavigationResident)),
            "SEIT-UI-05-N4 no-static-mutable-product-state");
        failures += M1Regressions.Run("UI-NAV");
        Console.WriteLine(failures == 0 ? "UI-NAV ALL PASS" : "UI-NAV FAILURES: " + failures.ToString());
        return failures == 0 ? 0 : 1;
    }
}
